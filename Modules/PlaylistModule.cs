using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Funnybot.Services;
using Microsoft.Extensions.Logging;

namespace Funnybot.Modules;

public sealed partial class PlaylistModule(SpotifyPlaylistService spotify, DailyLimitService limits, ILogger<PlaylistModule> log) : InteractionModuleBase<SocketInteractionContext>
{
    private const string ReauthMessage = "Spotify token needs re-auth. Clear the refresh token and restart the bot.";

    private static string SpotifyError(SpotifyAPI.Web.APIException ex, string fallback) =>
        SpotifyPlaylistService.IsMissingScope(ex) ? ReauthMessage : fallback;

    [SlashCommand("add", "Add one song per day to the shared playlist")]
    public async Task AddAsync(
        [Summary("query", "Song name, artist, or Spotify link")]
        string query)
    {
        await DeferAsync(ephemeral: true);

        if (limits.HasAddedToday(Context.User.Id, out var existing))
        {
            await FollowupAsync(
                $"You already added **{existing!.TrackName}** today. Come back tomorrow (UTC).",
                ephemeral: true);
            return;
        }

        string? directId = ExtractTrackId(query);
        if (directId is not null)
        {
            await PresentSingleTrackAsync(directId);
            return;
        }

        IReadOnlyList<SpotifyAPI.Web.FullTrack> results;
        try
        {
            results = await spotify.SearchTracksAsync(query, limit: 5);
        }
        catch (InvalidOperationException ex)
        {
            await FollowupAsync($"Spotify is not set up: {ex.Message}", ephemeral: true);
            return;
        }
        catch (SpotifyAPI.Web.APIException ex)
        {
            log.LogError(ex, "Search failed. {Detail}", SpotifyPlaylistService.Describe(ex));
            await FollowupAsync(SpotifyError(ex, "Spotify search failed, try again later."), ephemeral: true);
            return;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Search failed");
            await FollowupAsync("Spotify search failed, try again later.", ephemeral: true);
            return;
        }

        if (results.Count == 0)
        {
            await FollowupAsync($"No results for **{query}**.", ephemeral: true);
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle($"Results for \"{Truncate(query, 60)}\"")
            .WithDescription(string.Join("\n", results.Select((t, i) =>
                $"**{i + 1}.** [{t.Name}]({t.ExternalUrls["spotify"]}) - {Artists(t)}\n" +
                $"`{t.Album.Name}` - {Duration(t)}"))
                + "\n\nPick one below. Only you can use this menu.")
            .WithColor(Color.Green)
            .Build();

        var menu = new SelectMenuBuilder()
            .WithCustomId($"pick-track:{Context.User.Id}")
            .WithPlaceholder("Choose a song to add")
            .WithMinValues(1).WithMaxValues(1);

        foreach (var t in results)
            menu.AddOption(
                label: Truncate($"{t.Name} - {Artists(t)}", 100),
                value: t.Id,
                description: Truncate($"{t.Album.Name} - {Duration(t)}", 100));

        var components = new ComponentBuilder().WithSelectMenu(menu).Build();
        await FollowupAsync(embed: embed, components: components, ephemeral: true);
    }

    [ComponentInteraction("pick-track:*")]
    public async Task PickTrackAsync(string ownerIdRaw, string[] selected)
    {
        if (!ulong.TryParse(ownerIdRaw, out var ownerId) || ownerId != Context.User.Id)
        {
            await RespondAsync("That menu belongs to someone else, use `/add` to get your own.", ephemeral: true);
            return;
        }
        if (selected.Length == 0)
        {
            await RespondAsync("No song selected.", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        string trackId = selected[0];

        if (limits.HasAddedToday(Context.User.Id, out var existing))
        {
            await ModifyOriginalResponseAsync(m =>
            {
                m.Content = $"You already added **{existing!.TrackName}** today.";
                m.Embeds = new[] { BuildPlaylistEmbed() };
                m.Components = new ComponentBuilder().Build();
            });
            return;
        }

        SpotifyAPI.Web.FullTrack track;
        try
        {
            track = await spotify.GetTrackAsync(trackId);
        }
        catch (SpotifyAPI.Web.APIException ex)
        {
            log.LogError(ex, "GetTrack failed. {Detail}", SpotifyPlaylistService.Describe(ex));
            await FollowupAsync(SpotifyError(ex, "Couldn't load that track, try again."), ephemeral: true);
            return;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "GetTrack failed");
            await FollowupAsync("Couldn't load that track, try again.", ephemeral: true);
            return;
        }

        try
        {
            if (await spotify.IsInPlaylistAsync(trackId))
            {
                await FollowupAsync(
                    $"**{track.Name}** is already in the playlist, pick another one.",
                    ephemeral: true);
                return;
            }
            await spotify.AddToPlaylistAsync(trackId);
        }
        catch (SpotifyAPI.Web.APIException ex)
        {
            log.LogError(ex, "Add failed. {Detail}", SpotifyPlaylistService.Describe(ex));
            await FollowupAsync(SpotifyError(ex, "Couldn't add to the playlist, try again."), ephemeral: true);
            return;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Add failed");
            await FollowupAsync("Couldn't add to the playlist, try again.", ephemeral: true);
            return;
        }

        var record = new AddedTrack(
            Context.User.Id,
            Context.User.Username,
            track.Id,
            track.Name,
            Artists(track),
            track.ExternalUrls.TryGetValue("spotify", out var url) ? url : spotify.PlaylistUrl,
            DateTime.UtcNow);

        var (ok, lostRace) = await limits.TryAddTodayAsync(record);
        if (!ok)
        {
            try { await spotify.RemoveFromPlaylistAsync(trackId); } catch { }
            await ModifyOriginalResponseAsync(m =>
            {
                m.Content = $"You already added **{lostRace!.TrackName}** today.";
                m.Components = new ComponentBuilder().Build();
            });
            return;
        }

        await ModifyOriginalResponseAsync(m =>
        {
            m.Content = $"Added **{track.Name}**";
            m.Embeds = new[] { BuildPlaylistEmbed() };
            m.Components = new ComponentBuilder().Build();
        });

        var announce = new EmbedBuilder()
            .WithAuthor(Context.User)
            .WithTitle($"{track.Name} - {Artists(track)}")
            .WithUrl(record.SpotifyUrl)
            .WithThumbnailUrl(track.Album.Images.FirstOrDefault()?.Url)
            .AddField("Album", Truncate(track.Album.Name, 100), true)
            .AddField("Length", Duration(track), true)
            .WithColor(Color.Green)
            .WithFooter($"{DateOnly.FromDateTime(DateTime.UtcNow):yyyy-MM-dd} UTC")
            .Build();

        await Context.Channel.SendMessageAsync(
            text: $"{Context.User.Mention} added today's song to the playlist (run /playlist to check it out)",
            embed: announce);
        await FollowupAsync($"Added: {spotify.PlaylistUrl}", ephemeral: true);
    }

    [SlashCommand("today", "Show today's picks")]
    public async Task TodayAsync()
    {
        var picks = limits.GetToday();
        if (picks.Count == 0)
        {
            await RespondAsync("Nothing added yet today. Use `/add` to be first.", ephemeral: true);
            return;
        }
        var embed = new EmbedBuilder()
            .WithTitle($"Today's picks ({picks.Count}) - {DateOnly.FromDateTime(DateTime.UtcNow):yyyy-MM-dd} UTC")
            .WithDescription(string.Join("\n", picks.Select(p =>
                $"- **[{p.TrackName}]({p.SpotifyUrl})** - {p.Artists} (<@{p.UserId}>)")))
            .WithUrl(spotify.PlaylistUrl)
            .WithColor(Color.Blue)
            .Build();
        await RespondAsync(embed: embed, ephemeral: true);
    }

    [SlashCommand("playlist", "Get the shared playlist link")]
    public async Task PlaylistAsync()
    {
        await RespondAsync(spotify.PlaylistUrl, ephemeral: false);
    }

    [SlashCommand("remove", "Remove the song you added today")]
    public async Task RemoveAsync()
    {
        await DeferAsync(ephemeral: true);
        if (!limits.HasAddedToday(Context.User.Id, out var existing) || existing is null)
        {
            await FollowupAsync("You haven't added a song today.", ephemeral: true);
            return;
        }
        try { await spotify.RemoveFromPlaylistAsync(existing.TrackId); }
        catch (SpotifyAPI.Web.APIException ex)
        {
            log.LogError(ex, "Remove failed. {Detail}", SpotifyPlaylistService.Describe(ex));
            await FollowupAsync(SpotifyError(ex, "Couldn't remove from Spotify, try again."), ephemeral: true);
            return;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Remove failed");
            await FollowupAsync("Couldn't remove from Spotify, try again.", ephemeral: true);
            return;
        }
        await limits.RemoveTodayAsync(Context.User.Id);
        await FollowupAsync($"Removed **{existing.TrackName}**, you can `/add` again today.", ephemeral: true);
    }

    private async Task PresentSingleTrackAsync(string trackId)
    {
        SpotifyAPI.Web.FullTrack track;
        try { track = await spotify.GetTrackAsync(trackId); }
        catch (Exception ex)
        {
            log.LogError(ex, "GetTrack failed");
            await FollowupAsync("Couldn't load that Spotify link.", ephemeral: true);
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle($"{track.Name} - {Artists(track)}")
            .WithUrl(track.ExternalUrls["spotify"])
            .WithThumbnailUrl(track.Album.Images.FirstOrDefault()?.Url)
            .WithDescription($"`{track.Album.Name}` - {Duration(track)}\n\nConfirm below to add it as today's song.")
            .WithColor(Color.Green)
            .Build();

        var buttons = new ComponentBuilder()
            .WithButton("Add this song", $"confirm-track:{Context.User.Id}:{track.Id}", ButtonStyle.Success)
            .WithButton("Cancel", $"cancel-pick:{Context.User.Id}", ButtonStyle.Secondary)
            .Build();

        await FollowupAsync(embed: embed, components: buttons, ephemeral: true);
    }

    [ComponentInteraction("confirm-track:*:*")]
    public async Task ConfirmDirectAsync(string ownerIdRaw, string trackId)
    {
        if (!ulong.TryParse(ownerIdRaw, out var ownerId) || ownerId != Context.User.Id)
        {
            await RespondAsync("That button belongs to someone else.", ephemeral: true);
            return;
        }
        await PickTrackFromButtonAsync(trackId);
    }

    [ComponentInteraction("cancel-pick:*")]
    public async Task CancelPickAsync(string ownerIdRaw)
    {
        if (!ulong.TryParse(ownerIdRaw, out var ownerId) || ownerId != Context.User.Id)
        {
            await RespondAsync("That button belongs to someone else.", ephemeral: true);
            return;
        }
        await DeferAsync(ephemeral: true);
        await ModifyOriginalResponseAsync(m =>
        {
            m.Content = "Cancelled.";
            m.Components = new ComponentBuilder().Build();
        });
    }

    private async Task PickTrackFromButtonAsync(string trackId)
    {
        await DeferAsync(ephemeral: true);

        if (limits.HasAddedToday(Context.User.Id, out var existing))
        {
            await ModifyOriginalResponseAsync(m =>
            {
                m.Content = $"You already added **{existing!.TrackName}** today.";
                m.Components = new ComponentBuilder().Build();
            });
            return;
        }

        SpotifyAPI.Web.FullTrack track;
        try { track = await spotify.GetTrackAsync(trackId); }
        catch (SpotifyAPI.Web.APIException ex)
        {
            log.LogError(ex, "GetTrack failed. {Detail}", SpotifyPlaylistService.Describe(ex));
            await FollowupAsync(SpotifyError(ex, "Couldn't load that track, try again."), ephemeral: true);
            return;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "GetTrack failed");
            await FollowupAsync("Couldn't load that track, try again.", ephemeral: true);
            return;
        }

        try
        {
            if (!await spotify.IsInPlaylistAsync(trackId))
                await spotify.AddToPlaylistAsync(trackId);
            else
            {
                await FollowupAsync($"**{track.Name}** is already in the playlist.", ephemeral: true);
                return;
            }
        }
        catch (SpotifyAPI.Web.APIException ex)
        {
            log.LogError(ex, "Add failed. {Detail}", SpotifyPlaylistService.Describe(ex));
            await FollowupAsync(SpotifyError(ex, "Couldn't add to the playlist, try again."), ephemeral: true);
            return;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Add failed");
            await FollowupAsync("Couldn't add to the playlist, try again.", ephemeral: true);
            return;
        }

        var record = new AddedTrack(Context.User.Id, Context.User.Username, track.Id,
            track.Name, Artists(track),
            track.ExternalUrls.TryGetValue("spotify", out var url) ? url : spotify.PlaylistUrl,
            DateTime.UtcNow);

        var (ok, lostRace) = await limits.TryAddTodayAsync(record);
        if (!ok)
        {
            try { await spotify.RemoveFromPlaylistAsync(trackId); } catch { }
            await ModifyOriginalResponseAsync(m =>
            {
                m.Content = $"You already added **{lostRace!.TrackName}** today.";
                m.Components = new ComponentBuilder().Build();
            });
            return;
        }

        await ModifyOriginalResponseAsync(m =>
        {
            m.Content = $"Added **{track.Name}**";
            m.Components = new ComponentBuilder().Build();
        });
        await Context.Channel.SendMessageAsync(
            $"{Context.User.Mention} added today's song **[{track.Name}]({record.SpotifyUrl})** - {record.Artists} to the playlist (run /playlist to check it out)");
    }

    private static string Artists(SpotifyAPI.Web.FullTrack t) =>
        t.Artists.Count == 0 ? "Unknown artist" : string.Join(", ", t.Artists.Select(a => a.Name));

    private static string Duration(SpotifyAPI.Web.FullTrack t)
    {
        var ts = TimeSpan.FromMilliseconds(t.DurationMs);
        return $"{(int)ts.TotalMinutes}:{ts.Seconds:00}";
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";

    [System.Text.RegularExpressions.GeneratedRegex(@"open\.spotify\.com/track/([A-Za-z0-9]{10,})")]
    private static partial System.Text.RegularExpressions.Regex SpotifyTrackRegex();

    private static string? ExtractTrackId(string input)
    {
        input = input.Trim().Trim('<', '>');
        if (input.StartsWith("spotify:track:", StringComparison.OrdinalIgnoreCase))
            return input["spotify:track:".Length..].Split('?')[0];
        var m = SpotifyTrackRegex().Match(input);
        return m.Success ? m.Groups[1].Value : null;
    }

    private Embed BuildPlaylistEmbed() =>
        new EmbedBuilder()
            .WithDescription($"[Open the playlist]({spotify.PlaylistUrl})")
            .WithColor(Color.DarkPurple)
            .Build();
}
