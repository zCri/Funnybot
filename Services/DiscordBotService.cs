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

public sealed class DiscordBotService(
    DiscordSocketClient client,
    InteractionService interactions,
    IServiceProvider services,
    IOptions<BotConfig> config,
    ILogger<DiscordBotService> log,
    ILoggerFactory logFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        client.Log += msg => LogDiscord(msg);
        interactions.Log += msg => LogDiscord(msg);
        client.Ready += OnReadyAsync;
        client.InteractionCreated += OnInteractionAsync;

        if (string.IsNullOrWhiteSpace(config.Value.DiscordToken))
            throw new InvalidOperationException("Discord token missing. Set Bot:DiscordToken or DISCORD_TOKEN.");

        await client.LoginAsync(TokenType.Bot, config.Value.DiscordToken);
        await client.StartAsync();

        await Task.Delay(Timeout.Infinite, stoppingToken);

        await client.StopAsync();
    }

    private async Task OnReadyAsync()
    {
        try
        {
            await interactions.AddModulesAsync(Assembly.GetAssembly(typeof(PlaylistModule)), services);

            if (config.Value.GuildId is { } guildId and not 0)
            {
                await interactions.RegisterCommandsToGuildAsync(guildId, deleteMissing: true);
                log.LogInformation("Registered slash commands to guild {GuildId}", guildId);
            }
            else
            {
                await interactions.RegisterCommandsGloballyAsync(deleteMissing: true);
                log.LogInformation("Registered global slash commands");
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to register slash commands");
        }
    }

    private async Task OnInteractionAsync(SocketInteraction interaction)
    {
        try
        {
            var ctx = new SocketInteractionContext(client, interaction);
            var result = await interactions.ExecuteCommandAsync(ctx, services);
            if (!result.IsSuccess)
            {
                log.LogWarning("Interaction failed: {Reason}", result.ErrorReason);
                if (!interaction.HasResponded)
                    await interaction.RespondAsync("Something went wrong, try again.", ephemeral: true);
                else
                    await interaction.FollowupAsync("Something went wrong, try again.", ephemeral: true);
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Error handling interaction");
            if (!interaction.HasResponded)
            {
                try { await interaction.RespondAsync("Something went wrong, try again.", ephemeral: true); }
                catch { }
            }
        }
    }

    private Task LogDiscord(LogMessage msg)
    {
        var logger = logFactory.CreateLogger($"Discord.{msg.Source}");
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
