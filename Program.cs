using Discord.Interactions;
using Discord.WebSocket;
using Funnybot.Config;
using Funnybot.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables();

builder.Services.Configure<BotConfig>(builder.Configuration.GetSection("Bot"));
builder.Services.PostConfigure<BotConfig>(cfg =>
{
    var c = builder.Configuration;
    cfg.DiscordToken = First(c["Bot:DiscordToken"], c["DISCORD_TOKEN"], cfg.DiscordToken);
    if (ulong.TryParse(First(c["Bot:GuildId"], c["GUILD_ID"], cfg.GuildId?.ToString()), out var g) && g != 0)
        cfg.GuildId = g;
    cfg.DataFile = First(c["Bot:DataFile"], c["DATA_FILE"], cfg.DataFile);
    cfg.Spotify.ClientId = First(c["Bot:Spotify:ClientId"], c["SPOTIFY_CLIENT_ID"], cfg.Spotify.ClientId);
    cfg.Spotify.ClientSecret = First(c["Bot:Spotify:ClientSecret"], c["SPOTIFY_CLIENT_SECRET"], cfg.Spotify.ClientSecret);
    cfg.Spotify.RefreshToken = First(c["Bot:Spotify:RefreshToken"], c["SPOTIFY_REFRESH_TOKEN"], cfg.Spotify.RefreshToken);
    cfg.Spotify.PlaylistId = First(c["Bot:Spotify:PlaylistId"], c["SPOTIFY_PLAYLIST_ID"], cfg.Spotify.PlaylistId);
    cfg.Spotify.CallbackUrl = First(c["Bot:Spotify:CallbackUrl"], c["SPOTIFY_CALLBACK_URL"], cfg.Spotify.CallbackUrl);
});

static string First(params string?[] vals)
{
    foreach (var v in vals)
        if (!string.IsNullOrWhiteSpace(v)) return v!;
    return "";
}

builder.Services.AddSingleton(_ => new DiscordSocketConfig
{
    GatewayIntents = Discord.GatewayIntents.Guilds,
    LogLevel = Discord.LogSeverity.Info,
});
builder.Services.AddSingleton<DiscordSocketClient>();
builder.Services.AddSingleton(s => new InteractionService(
    s.GetRequiredService<DiscordSocketClient>().Rest,
    new InteractionServiceConfig { LogLevel = Discord.LogSeverity.Info }));

builder.Services.AddSingleton(s =>
{
    var cfg = s.GetRequiredService<Microsoft.Extensions.Options.IOptions<BotConfig>>().Value;
    return new DailyLimitService(cfg.DataFile);
});
builder.Services.AddSingleton<SpotifyPlaylistService>();
builder.Services.AddHostedService<DiscordBotService>();

var host = builder.Build();

{
    using var scope = host.Services.CreateScope();
    var cfg = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<BotConfig>>().Value;
    var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("SpotifySetup");
    var s = cfg.Spotify;

    if (string.IsNullOrWhiteSpace(cfg.DiscordToken))
    {
        log.LogError("Discord token missing. Set Bot:DiscordToken in appsettings.json or DISCORD_TOKEN env var.");
        return 1;
    }

    if (string.IsNullOrWhiteSpace(s.RefreshToken)
        && !string.IsNullOrWhiteSpace(s.ClientId)
        && !string.IsNullOrWhiteSpace(s.ClientSecret))
    {
        var token = await SpotifyAuthHelper.AcquireRefreshTokenAsync(s, log, TimeSpan.FromMinutes(5));
        if (!string.IsNullOrEmpty(token))
        {
            log.LogWarning("Restart the bot with SPOTIFY_REFRESH_TOKEN / Bot:Spotify:RefreshToken set.");
            return 0;
        }
        log.LogError("Continuing without Spotify auth, /add will fail.");
    }
    else if (!string.IsNullOrWhiteSpace(s.RefreshToken)
        && !string.IsNullOrWhiteSpace(s.ClientId)
        && !string.IsNullOrWhiteSpace(s.ClientSecret)
        && !string.IsNullOrWhiteSpace(s.PlaylistId))
    {
        try
        {
            var spotify = scope.ServiceProvider.GetRequiredService<SpotifyPlaylistService>();
            await spotify.EnsureAccessAsync();
            log.LogInformation("Spotify access OK.");
        }
        catch (InvalidOperationException ex)
        {
            log.LogWarning("Spotify re-auth needed: {Reason}", ex.Message);
            var token = await SpotifyAuthHelper.AcquireRefreshTokenAsync(s, log, TimeSpan.FromMinutes(5));
            if (!string.IsNullOrEmpty(token))
            {
                log.LogWarning("Restart the bot with the new SPOTIFY_REFRESH_TOKEN / Bot:Spotify:RefreshToken.");
                return 0;
            }
            log.LogError("Continuing without working Spotify auth, /add will fail.");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Spotify preflight failed, continuing anyway.");
        }
    }
}

await host.RunAsync();
return 0;
