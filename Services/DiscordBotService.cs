using System.Reflection;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Funnybot.Config;
using Funnybot.Modules;
using Funnybot.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Funnybot.Services;

public sealed class DiscordBotService : BackgroundService
{
    private readonly DiscordSocketClient _client;
    private readonly InteractionService _interactions;
    private readonly IServiceProvider _services;
    private readonly BotConfig _config;
    private readonly ILogger<DiscordBotService> _log;
    private readonly ILoggerFactory _logFactory;

    public DiscordBotService(
        DiscordSocketClient client,
        InteractionService interactions,
        IServiceProvider services,
        IOptions<BotConfig> config,
        ILogger<DiscordBotService> log,
        ILoggerFactory logFactory)
    {
        _client = client;
        _interactions = interactions;
        _services = services;
        _config = config.Value;
        _log = log;
        _logFactory = logFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _client.Log += msg => LogDiscord(msg);
        _interactions.Log += msg => LogDiscord(msg);
        _client.Ready += OnReadyAsync;
        _client.InteractionCreated += OnInteractionAsync;

        if (string.IsNullOrWhiteSpace(_config.DiscordToken))
            throw new InvalidOperationException("Discord token missing. Set Bot:DiscordToken or DISCORD_TOKEN.");

        await _client.LoginAsync(TokenType.Bot, _config.DiscordToken);
        await _client.StartAsync();

        await Task.Delay(Timeout.Infinite, stoppingToken);

        await _client.StopAsync();
    }

    private async Task OnReadyAsync()
    {
        try
        {
            await _interactions.AddModulesAsync(Assembly.GetAssembly(typeof(PlaylistModule)), _services);

            if (_config.GuildId is { } guildId and not 0)
            {
                await _interactions.RegisterCommandsToGuildAsync(guildId, deleteMissing: true);
                _log.LogInformation("Registered slash commands to guild {GuildId}", guildId);
            }
            else
            {
                await _interactions.RegisterCommandsGloballyAsync(deleteMissing: true);
                _log.LogInformation("Registered global slash commands (can take up to 1h to appear)");
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to register slash commands");
        }
    }

    private async Task OnInteractionAsync(SocketInteraction interaction)
    {
        try
        {
            var ctx = new SocketInteractionContext(_client, interaction);
            var result = await _interactions.ExecuteCommandAsync(ctx, _services);
            if (!result.IsSuccess)
            {
                _log.LogWarning("Interaction failed: {Reason}", result.ErrorReason);
                if (!interaction.HasResponded)
                    await interaction.RespondAsync("Something went wrong, try again.", ephemeral: true);
                else
                    await interaction.FollowupAsync("Something went wrong, try again.", ephemeral: true);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error handling interaction");
            if (!interaction.HasResponded)
            {
                try { await interaction.RespondAsync("Something went wrong, try again.", ephemeral: true); }
                catch { }
            }
        }
    }

    private Task LogDiscord(LogMessage msg)
    {
        var logger = _logFactory.CreateLogger($"Discord.{msg.Source}");
        var level = msg.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            _ => LogLevel.Debug,
        };
        logger.Log(level, msg.Exception, "{Message}", msg.Message);
        return Task.CompletedTask;
    }
}
