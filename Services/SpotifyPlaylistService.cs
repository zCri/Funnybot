using Funnybot.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpotifyAPI.Web;

namespace Funnybot.Services;

public sealed class SpotifyPlaylistService(IOptions<BotConfig> config, ILogger<SpotifyPlaylistService> log)
{
    private SpotifyClient? _client;
    private DateTime _tokenExpiresAtUtc = DateTime.MinValue;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public string PlaylistId => config.Value.Spotify.PlaylistId;
    public string PlaylistUrl => $"https://open.spotify.com/playlist/{PlaylistId}";
    public string CallbackUrl => config.Value.Spotify.CallbackUrl;

    public static readonly string[] RequiredScopes =
    [
        Scopes.PlaylistModifyPublic,
        Scopes.PlaylistModifyPrivate,
        Scopes.PlaylistReadPrivate,
        Scopes.PlaylistReadCollaborative,
        Scopes.UserReadPrivate,
    ];

    public async Task<SpotifyClient> GetClientAsync(CancellationToken ct = default)
    {
        if (_client is not null && DateTime.UtcNow < _tokenExpiresAtUtc - TimeSpan.FromMinutes(5))
            return _client;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_client is not null && DateTime.UtcNow < _tokenExpiresAtUtc - TimeSpan.FromMinutes(5))
                return _client;

            var s = config.Value.Spotify;
            if (string.IsNullOrWhiteSpace(s.ClientId) || string.IsNullOrWhiteSpace(s.ClientSecret))
                throw new InvalidOperationException("Missing Spotify ClientId/ClientSecret.");
            if (string.IsNullOrWhiteSpace(s.RefreshToken))
                throw new InvalidOperationException("Missing Spotify RefreshToken.");
            if (string.IsNullOrWhiteSpace(s.PlaylistId))
                throw new InvalidOperationException("Missing Spotify PlaylistId.");

            var oauth = new OAuthClient();
            var refresh = await oauth.RequestToken(new AuthorizationCodeRefreshRequest(
                s.ClientId, s.ClientSecret, s.RefreshToken), ct);

            _client = new SpotifyClient(refresh.AccessToken);
            _tokenExpiresAtUtc = DateTime.UtcNow + TimeSpan.FromSeconds(refresh.ExpiresIn);
            return _client;
        }
        finally { _initLock.Release(); }
    }

    public async Task<IReadOnlyList<FullTrack>> SearchTracksAsync(string query, int limit = 5, CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct);
        var result = await client.Search.Item(new SearchRequest(SearchRequest.Types.Track, query)
        {
            Limit = Math.Clamp(limit, 1, 5),
            Market = "from_token",
        }, ct);
        return result.Tracks.Items ?? [];
    }

    public async Task<FullTrack> GetTrackAsync(string trackId, CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct);
        return await client.Tracks.Get(trackId, new TrackRequest { Market = "from_token" }, ct);
    }

    public async Task<bool> IsInPlaylistAsync(string trackId, CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct);
        var items = await client.Playlists.GetPlaylistItems(PlaylistId, new PlaylistGetItemsRequest
        {
            Limit = 100,
        }, ct);
        return items.Items?.Any(i => i.Track is FullTrack t && t.Id == trackId) == true;
    }

    public async Task AddToPlaylistAsync(string trackId, CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct);
        await client.Playlists.AddPlaylistItems(PlaylistId, new PlaylistAddItemsRequest(new[] { $"spotify:track:{trackId}" }), ct);
    }

    public async Task RemoveFromPlaylistAsync(string trackId, CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct);
        await client.Playlists.RemovePlaylistItems(PlaylistId, new PlaylistRemoveItemsRequestV2
        {
            Items = new List<PlaylistRemoveItemsRequestV2.Item> { new() { Uri = $"spotify:track:{trackId}" } },
        }, ct);
    }

    public async Task EnsureAccessAsync(CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct);
        try
        {
            await client.Playlists.Get(PlaylistId, ct);
        }
        catch (APIException ex) when (IsMissingScope(ex))
        {
            log.LogError(ex, "Spotify token missing scopes. {Detail}", Describe(ex));
            throw new InvalidOperationException("Spotify token missing scopes, re-auth needed.");
        }
        catch (APIException ex)
        {
            log.LogWarning(ex, "Spotify playlist check failed. {Detail}", Describe(ex));
        }
    }

    public static string BuildAuthUrl(string clientId, string callbackUrl)
    {
        var req = new LoginRequest(new Uri(callbackUrl), clientId, LoginRequest.ResponseType.Code)
        {
            Scope = RequiredScopes,
        };
        return req.ToUri().ToString();
    }

    public static bool IsMissingScope(APIException ex) =>
        ex.Response?.StatusCode is System.Net.HttpStatusCode.Forbidden
        && (ex.Message.Contains("scope", StringComparison.OrdinalIgnoreCase)
            || (ex.Response?.Body?.ToString() ?? "").Contains("scope", StringComparison.OrdinalIgnoreCase));

    public static string Describe(APIException ex)
    {
        var status = ex.Response?.StatusCode.ToString() ?? "no-status";
        string? body = null;
        try { body = ex.Response?.Body?.ToString(); } catch { }
        return $"Spotify API error [{status}]: {ex.Message}"
            + (string.IsNullOrWhiteSpace(body) ? "" : $" | Body: {body}");
    }

    public static async Task<AuthorizationCodeTokenResponse> ExchangeCodeAsync(
        string clientId, string clientSecret, string code, string callbackUrl, CancellationToken ct = default)
    {
        var oauth = new OAuthClient();
        return await oauth.RequestToken(new AuthorizationCodeTokenRequest(
            clientId, clientSecret, code, new Uri(callbackUrl)), ct);
    }
}
