using System.Net;
using System.Text;
using Funnybot.Config;
using Microsoft.Extensions.Logging;

namespace Funnybot.Services;

public static class SpotifyAuthHelper
{
    public static async Task<string?> AcquireRefreshTokenAsync(
        SpotifyOptions spotify, ILogger log, TimeSpan timeout, CancellationToken ct = default)
    {
        var callback = spotify.CallbackUrl;
        var authUrl = SpotifyPlaylistService.BuildAuthUrl(spotify.ClientId, callback);

        log.LogWarning("No Spotify refresh token configured.");
        log.LogWarning("Open this URL in your browser and approve: {Url}", authUrl);
        log.LogWarning("Your Spotify app Redirect URI must include exactly: {Callback}", callback);

        if (!HttpListener.IsSupported)
        {
            log.LogError("HttpListener not supported on this platform.");
            return null;
        }

        string prefix = ToPrefix(callback);
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        try { listener.Start(); }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not listen on {Prefix}.", prefix);
            return null;
        }

        log.LogWarning("Listening for Spotify callback on {Prefix}...", prefix);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var ctxTask = listener.GetContextAsync();
                var completed = await Task.WhenAny(ctxTask, Task.Delay(Timeout.Infinite, cts.Token));
                if (completed != ctxTask) break;
                var ctx = await ctxTask;
                var code = ctx.Request.QueryString["code"];
                var error = ctx.Request.QueryString["error"];

                const string html = "<html><body style='font-family:sans-serif'><h2>Funnybot Spotify setup</h2><p>You can close this tab and return to the bot.</p></body></html>";
                var bytes = Encoding.UTF8.GetBytes(html);
                ctx.Response.ContentType = "text/html";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes, cts.Token);
                ctx.Response.Close();

                if (!string.IsNullOrEmpty(error))
                {
                    log.LogError("Spotify auth error: {Error}", error);
                    return null;
                }
                if (string.IsNullOrEmpty(code))
                    continue;

                var token = await SpotifyPlaylistService.ExchangeCodeAsync(
                    spotify.ClientId, spotify.ClientSecret, code, callback, cts.Token);
                log.LogWarning("Got Spotify refresh token. Add it to config (Bot:Spotify:RefreshToken / SPOTIFY_REFRESH_TOKEN):");
                log.LogWarning("{RefreshToken}", token.RefreshToken);
                return token.RefreshToken;
            }
        }
        catch (OperationCanceledException) { log.LogError("Timed out waiting for Spotify callback."); }
        catch (Exception ex) { log.LogError(ex, "Error during Spotify callback."); }
        finally { listener.Stop(); }

        return null;
    }

    private static string ToPrefix(string callbackUrl)
    {
        var uri = new Uri(callbackUrl);
        var path = uri.AbsolutePath.EndsWith('/') ? uri.AbsolutePath : uri.AbsolutePath + "/";
        var host = uri.Host;
        if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true"
            && (host == "localhost" || host == "127.0.0.1" || host == "::1"))
            host = "*";
        return $"{uri.Scheme}://{host}:{uri.Port}{path}";
    }
}
