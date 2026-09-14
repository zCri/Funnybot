namespace Funnybot.Config;

public sealed class BotConfig
{
    public string DiscordToken { get; set; } = "";
    public ulong? GuildId { get; set; }
    public SpotifyOptions Spotify { get; set; } = new();
    public string DataFile { get; set; } = "data/history.json";
}

public sealed class SpotifyOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string PlaylistId { get; set; } = "";
    public string CallbackUrl { get; set; } = "http://127.0.0.1:5000/callback";
}
