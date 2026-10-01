#:package RaidMax.IW4MAdmin.SharedLibraryCore@2026.1.6.1

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharedLibraryCore.Dtos.Meta.Responses;
using SharedLibraryCore.Events.Management;
using SharedLibraryCore.Interfaces;
using SharedLibraryCore.Interfaces.Events;
using SharedLibraryCore.QueryHelper;

/// <summary>
/// Adds a player's zombies career to their IW4MAdmin profile: one Zombie
/// Statistics panel carrying what they have earned (best round, records, first
/// places, speedrun times) and how they play (kills, headshots, revives, downs),
/// followed by live badges: the top 5 of each high-round board (Gold, Silver,
/// Bronze, 4th, 5th, as on the stats site), and Gold, Silver and Bronze for
/// fastest-to-round speedruns and easter egg times.
///
/// The statistics and the rankings both come from the stats site's APIs and are
/// never persisted by this plugin, so the profile and the stats site can never
/// disagree about a player's totals.
///
/// One profile tile per map and category, with a row per placing, so a player
/// with a dozen records on one map gets one tile rather than a dozen. The tile
/// value is "Map | medal headline detail | ..." - the webfront titleizes tile
/// labels (punctuation dropped, "3P" becomes "3 P"), so everything that must
/// render exactly goes in the value and the label is plain words. Without
/// cuk-webfront.js the value reads as a plain line; with it, the medals become
/// gold/silver/bronze trophies and the rows are laid out properly.
/// </summary>
public sealed class ZombieRecordBadgesPlugin : IPluginV2
{
    private const string LeaderboardUrl = "http://highscores-flask:5000/api/leaderboard";
    private const string SpeedrunsUrl = "http://highscores-flask:5000/api/speedruns";
    private const string LinkedUrl = "http://highscores-flask:5000/api/linked/";
    private const string GameStatsUrl = "http://highscores-flask:5000/api/gamestats/";
    private const string PlayerUrl = "http://highscores-flask:5000/api/player/";
    private const string ZombieStatsCategory = "Zombie Statistics";
    private const string UserCssPath = "/app/wwwroot/css/user/user.css";
    private const string CssMarker = "CUK Zombie Record Badges";
    private const string RecordBadgeCss = @"

/* CUK Zombie Record Badges */
.ph.ph-tag {
    background: none !important;
    color: var(--color-primary, currentColor) !important;
}

.ph.ph-tag::before {
    content: ""\e67e"" !important;
}
";
    private const string RecordsCategory = "Zombie Records";

    // A high-round board is a real top 5 of every finished game, so all five
    // places earn a badge. Speedrun and easter egg badges stay at the podium.
    private const int RecordPlaces = 5;
    private const string SpeedrunsCategory = "Zombie Speedruns";
    private const string EasterEggsCategory = "Easter Eggs";

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LinkCacheDuration = TimeSpan.FromMinutes(10);
    private static readonly Regex GamePrefix = new(@"^T[456]\s+", RegexOptions.Compiled);
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<ZombieRecordBadgesPlugin> _logger;
    private readonly IMetaServiceV2 _metaService;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private bool _registered;

    private IReadOnlyDictionary<int, IReadOnlyList<Badge>> _badgesByClient =
        new Dictionary<int, IReadOnlyList<Badge>>();
    private DateTime _cacheExpiresAtUtc = DateTime.MinValue;

    // client id -> every client id on the same player (see GetLinkedIdsAsync)
    private readonly ConcurrentDictionary<int, (DateTime ExpiresAtUtc, int[] Ids)> _linkedIds = new();

    // client id -> kills / revives / downs ... from the GameStats scripts
    private readonly ConcurrentDictionary<int, (DateTime ExpiresAtUtc, PlayerGameStats? Stats)> _gameStats = new();

    // client id -> best round, records and speedrun counts from the stats site
    private readonly ConcurrentDictionary<int, (DateTime ExpiresAtUtc, PlayerProfile? Profile)> _profiles = new();

    public ZombieRecordBadgesPlugin(
        ILogger<ZombieRecordBadgesPlugin> logger,
        IMetaServiceV2 metaService)
    {
        _logger = logger;
        _metaService = metaService;
        IManagementEventSubscriptions.Load += OnLoad;

        _logger.LogInformation("{Name} {Version} loaded", Name, Version);
    }

    public string Name => "Zombie Record Badges";
    public string Author => "CUK Servers";
    public string Version => "2.8.1";

    private Task OnLoad(IManager manager, CancellationToken token)
    {
        if (_registered)
        {
            return Task.CompletedTask;
        }

        _metaService.AddRuntimeMeta<ClientPaginationRequest, InformationResponse>(
            MetaType.Information,
            GetProfileBadgesAsync);
        _registered = true;

        EnsureRecordBadgeStyles();

        _logger.LogInformation("Registered zombie record, speedrun and easter egg profile badges");
        return Task.CompletedTask;
    }

    private async Task<IEnumerable<InformationResponse>> GetProfileBadgesAsync(
        ClientPaginationRequest request,
        CancellationToken token)
    {
        var badgesByClient = await GetBadgeCacheAsync(token);

        // A record is credited to whichever account set it, so gather the
        // badges of every account linked to this player, not just this one.
        var badges = (await GetLinkedIdsAsync(request.ClientId, token))
            .SelectMany(clientId => badgesByClient.TryGetValue(clientId, out var own) ? own : Array.Empty<Badge>())
            .Distinct()
            .ToList();

        var tiles = new List<InformationResponse>();
        var stats = await GetGameStatsAsync(request.ClientId, token);
        var profile = await GetPlayerProfileAsync(request.ClientId, token);
        tiles.AddRange(ZombieStatTiles(stats, profile));

        // Group rows into one tile per map. Tiles sort by their best placing,
        // then map name; rows within a tile by placing, then their own key.
        tiles.AddRange(badges
            .GroupBy(badge => (badge.Category, badge.CategoryOrder, badge.MapName))
            .OrderBy(group => group.Key.CategoryOrder)
            .ThenBy(group => group.Min(badge => badge.Place))
            .ThenBy(group => group.Key.MapName, StringComparer.OrdinalIgnoreCase)
            .Select((group, index) =>
            {
                var rows = group
                    .OrderBy(badge => badge.Place)
                    .ThenBy(badge => badge.Sort, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return new InformationResponse
                {
                    Key = rows[0].Label,
                    Value = group.Key.MapName + string.Concat(
                        rows.Select(row => $" | {Medal(row.Place)} {row.Headline} {row.Detail}")),
                    ToolTipText = string.Join(" | ", rows.Select(row => row.Tooltip)),
                    Category = group.Key.Category,
                    Type = MetaType.Information,
                    Order = group.Key.CategoryOrder + index
                };
            }));
        return tiles;
    }

    // ── zombie statistics ───────────────────────────────────────────

    // One plain tile per number, above the badges. What the player has earned
    // comes first - the best round and the boards they are on - then how they
    // got there. Unlike the Game Statistics panel, which is multiplayer only,
    // this shows for anyone who has either a record or a game logged.
    private static IEnumerable<InformationResponse> ZombieStatTiles(
        PlayerGameStats? stats,
        PlayerProfile? profile)
    {
        var rows = new List<(string Label, string Value, string Tip)>();
        var totals = profile?.Totals;
        var records = profile?.Records ?? new List<PlayerRecord>();

        // Only the figures the player actually has: a row of dashes and zeros
        // tells them nothing and makes the panel look broken.
        if (totals is not null && totals.Records > 0)
        {
            var best = records.OrderByDescending(record => record.Round).FirstOrDefault();
            var maps = records
                .Select(record => ShortMapName(record.Map))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            rows.Add(("Best Round", Count(totals.BestRound), "Highest round on any board"));
            if (best is not null)
            {
                rows.Add(("Best Round Map", ShortMapName(best.Map),
                    $"Round {best.Round.ToString(CultureInfo.InvariantCulture)} on {ShortMapName(best.Map)} ({best.Slot}P)"));
            }

            rows.Add(("Map Records", Count(totals.Records), "Times on a map leaderboard"));
            rows.Add(("First Places", Count(totals.FirstPlaces), "Leaderboards topped"));
            rows.Add(("Maps on the Boards", Count(maps), "Different maps placed on"));
        }

        // On its own this tile just counts the panel of speedruns sitting right
        // below it, so it only earns its place beside other figures.
        if (totals is { Speedruns: > 0 } && (totals.Records > 0 || stats is { Games: > 0 }))
        {
            rows.Add(("Speedrun Times", Count(totals.Speedruns), "Times on a speedrun board"));
        }

        if (stats is { Games: > 0 })
        {
            var headshotShare = stats.Kills > 0
                ? $" ({(stats.Headshots * 100 / stats.Kills).ToString(CultureInfo.InvariantCulture)}%)"
                : string.Empty;

            rows.Add(("Kills", Count(stats.Kills), "Zombies killed"));
            rows.Add(("Headshots", Count(stats.Headshots) + headshotShare,
                "Headshot kills, and their share of all kills"));
            rows.Add(("Revives", Count(stats.Revives), "Teammates picked back up"));
            rows.Add(("Downs", Count(stats.Downs), "Times gone down"));
            rows.Add(("Games", Count(stats.Games), "Games played"));
            rows.Add(("Time Played", Duration(stats.Ms), "Time in games"));

            if (stats.BestGame is { Kills: > 0 } bestGame)
            {
                rows.Add(("Best Game",
                    $"{Count(bestGame.Kills)} kills",
                    $"Most kills in one game - {ShortMapName(bestGame.Map)} round {bestGame.Round.ToString(CultureInfo.InvariantCulture)}, {bestGame.Date}"));
            }
        }

        return rows.Select((row, index) => new InformationResponse
        {
            Key = row.Label,
            Value = row.Value,
            ToolTipText = $"{row.Tip} on CUK Zombies servers, linked accounts included",
            Category = ZombieStatsCategory,
            Type = MetaType.Information,
            Order = 1800 + index
        });
    }

    // Best round, records and speedrun counts, already totalled across every
    // linked account by the stats site - the same figures its own player page
    // shows, so the two never disagree.
    private async Task<PlayerProfile?> GetPlayerProfileAsync(int clientId, CancellationToken token)
    {
        if (_profiles.TryGetValue(clientId, out var hit) && hit.ExpiresAtUtc > DateTime.UtcNow)
        {
            return hit.Profile;
        }

        var expires = DateTime.UtcNow.Add(CacheDuration);
        PlayerProfile? profile;
        try
        {
            profile = await FetchAsync<PlayerProfile>(
                PlayerUrl + clientId.ToString(CultureInfo.InvariantCulture), token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keep showing what was there last time; try again in a minute.
            profile = hit.Profile;
            expires = DateTime.UtcNow.AddMinutes(1);
            _logger.LogDebug(ex, "Could not look up zombie profile for client {ClientId}", clientId);
        }

        if (_profiles.Count > 5000)
        {
            _profiles.Clear();
        }

        _profiles[clientId] = (expires, profile);
        return profile;
    }

    private static string Count(long value) => value.ToString("N0", CultureInfo.GetCultureInfo("en-GB"));

    private static string Duration(long ms)
    {
        var hours = ms / 3_600_000.0;
        if (hours >= 1)
        {
            var text = hours.ToString(hours >= 10 ? "0" : "0.0", CultureInfo.InvariantCulture);
            return text == "1.0" ? "1 hour" : $"{text} hours";
        }

        var minutes = Math.Max(1, ms / 60_000);
        return minutes == 1
            ? "1 minute"
            : $"{minutes.ToString(CultureInfo.InvariantCulture)} minutes";
    }

    private async Task<PlayerGameStats?> GetGameStatsAsync(int clientId, CancellationToken token)
    {
        if (_gameStats.TryGetValue(clientId, out var hit) && hit.ExpiresAtUtc > DateTime.UtcNow)
        {
            return hit.Stats;
        }

        var expires = DateTime.UtcNow.Add(CacheDuration);
        PlayerGameStats? stats;
        try
        {
            stats = (await FetchAsync<GameStatsFeed>(
                GameStatsUrl + clientId.ToString(CultureInfo.InvariantCulture), token))?.GameStats;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keep showing what was there last time; try again in a minute.
            stats = hit.Stats;
            expires = DateTime.UtcNow.AddMinutes(1);
            _logger.LogDebug(ex, "Could not look up zombie stats for client {ClientId}", clientId);
        }

        if (_gameStats.Count > 5000)
        {
            _gameStats.Clear();
        }

        _gameStats[clientId] = (expires, stats);
        return stats;
    }

    private void EnsureRecordBadgeStyles()
    {
        try
        {
            if (!File.Exists(UserCssPath))
            {
                return;
            }

            var css = File.ReadAllText(UserCssPath);
            if (!css.Contains(CssMarker, StringComparison.Ordinal))
            {
                css += RecordBadgeCss;
                File.WriteAllText(UserCssPath, css, new UTF8Encoding(false));
            }

            WriteCompressedCss(UserCssPath + ".gz", css, brotli: false);
            WriteCompressedCss(UserCssPath + ".br", css, brotli: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not install zombie record badge web styles");
        }
    }

    private static void WriteCompressedCss(string path, string css, bool brotli)
    {
        using var output = File.Create(path);
        using Stream compressor = brotli
            ? new BrotliStream(output, CompressionLevel.SmallestSize)
            : new GZipStream(output, CompressionLevel.SmallestSize);
        using var writer = new StreamWriter(compressor, new UTF8Encoding(false));
        writer.Write(css);
    }

    private async Task<IReadOnlyDictionary<int, IReadOnlyList<Badge>>> GetBadgeCacheAsync(
        CancellationToken token)
    {
        if (DateTime.UtcNow < _cacheExpiresAtUtc)
        {
            return _badgesByClient;
        }

        await _cacheLock.WaitAsync(token);
        try
        {
            if (DateTime.UtcNow < _cacheExpiresAtUtc)
            {
                return _badgesByClient;
            }

            try
            {
                var maps = await FetchAsync<List<LeaderboardMap>>(LeaderboardUrl, token)
                           ?? new List<LeaderboardMap>();

                // The speedrun feed is newer than the high-round one; a failure
                // there should not take the record badges down with it.
                SpeedrunFeed speedruns;
                try
                {
                    speedruns = await FetchAsync<SpeedrunFeed>(SpeedrunsUrl, token) ?? new SpeedrunFeed();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    speedruns = new SpeedrunFeed();
                    _logger.LogWarning(ex, "Could not refresh speedrun badges from {Url}", SpeedrunsUrl);
                }

                var index = new Dictionary<int, List<Badge>>();
                AddRecordBadges(index, maps);
                AddSpeedrunBadges(index, speedruns);

                _badgesByClient = index.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<Badge>)pair.Value
                        .Distinct()
                        .OrderBy(badge => badge.CategoryOrder)
                        .ThenBy(badge => badge.Place)
                        .ThenBy(badge => badge.Sort, StringComparer.OrdinalIgnoreCase)
                        .ToArray());
                _cacheExpiresAtUtc = DateTime.UtcNow.Add(CacheDuration);

                _logger.LogDebug("Refreshed zombie badges for {ClientCount} profiles", _badgesByClient.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Keep serving the last successful snapshot during a temporary API outage.
                _cacheExpiresAtUtc = DateTime.UtcNow.AddSeconds(30);
                _logger.LogWarning(ex, "Could not refresh zombie record badges from {Url}", LeaderboardUrl);
            }

            return _badgesByClient;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    // Plutonium gives a player a different account per game, and the stats site
    // already works out which accounts belong together for its player profiles.
    // On failure the profile just shows its own badges, as before.
    private async Task<int[]> GetLinkedIdsAsync(int clientId, CancellationToken token)
    {
        if (_linkedIds.TryGetValue(clientId, out var hit) && hit.ExpiresAtUtc > DateTime.UtcNow)
        {
            return hit.Ids;
        }

        int[] ids;
        var expires = DateTime.UtcNow.Add(LinkCacheDuration);
        try
        {
            var linked = await FetchAsync<LinkedAccounts>(LinkedUrl + clientId.ToString(CultureInfo.InvariantCulture), token);
            ids = (linked?.Linked ?? new List<int>())
                .Where(id => id > 0)
                .Append(clientId)
                .Distinct()
                .ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ids = new[] { clientId };
            expires = DateTime.UtcNow.AddMinutes(1);
            _logger.LogDebug(ex, "Could not look up linked accounts for client {ClientId}", clientId);
        }

        if (_linkedIds.Count > 5000)
        {
            _linkedIds.Clear();
        }

        _linkedIds[clientId] = (expires, ids);
        return ids;
    }

    private static async Task<T?> FetchAsync<T>(string url, CancellationToken token)
    {
        using var response = await HttpClient.GetAsync(url, token);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(token);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, token);
    }

    // ── high rounds ──────────────────────────────────────────────────────────

    private static void AddRecordBadges(Dictionary<int, List<Badge>> index, IEnumerable<LeaderboardMap> maps)
    {
        foreach (var map in maps)
        {
            foreach (var slot in map.Slots)
            {
                if (!int.TryParse(slot.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var teamSize))
                {
                    continue;
                }

                // The source contains progression rounds from the same run. Collapse each
                // unique team to its highest round before assigning leaderboard places.
                var rankedTeams = slot.Value
                    .Where(entry => entry.Round > 0 && entry.PlayerMeta.Count > 0)
                    .Select(entry => new RankedEntry(entry, TeamKey(entry.PlayerMeta)))
                    .Where(entry => !string.IsNullOrEmpty(entry.TeamKey))
                    .GroupBy(entry => entry.TeamKey, StringComparer.Ordinal)
                    .Select(group => group
                        .OrderByDescending(item => item.Entry.Round)
                        .ThenByDescending(item => ParseDate(item.Entry.Date))
                        .First().Entry)
                    // A stable sort keeps the site's order among equal rounds, so
                    // the five rows badged are the five rows the board shows.
                    .OrderByDescending(entry => entry.Round)
                    .ToList();

                var previousRound = -1;
                var place = 0;

                for (var position = 0; position < Math.Min(rankedTeams.Count, RecordPlaces); position++)
                {
                    var entry = rankedTeams[position];
                    if (entry.Round != previousRound)
                    {
                        // Competition ranking: equal rounds share a place (1, 1, 3).
                        place = position + 1;
                        previousRound = entry.Round;
                    }

                    var mapName = ShortMapName(map.Name);
                    var badge = new Badge(
                        RecordsCategory,
                        2000,
                        place,
                        mapName,
                        GameName(map.Game),
                        entry.Round.ToString(CultureInfo.InvariantCulture),
                        $"{teamSize}P",
                        Tooltip(
                            $"{teamSize}P round {entry.Round}",
                            entry.Players,
                            entry.Date,
                            string.IsNullOrWhiteSpace(entry.VodUrl) ? null : "VOD on the High Rounds page"),
                        $"{teamSize}");

                    Credit(index, entry.PlayerMeta, badge);
                }
            }
        }
    }

    // ── speedruns and easter eggs ────────────────────────────────────────────

    private static void AddSpeedrunBadges(Dictionary<int, List<Badge>> index, SpeedrunFeed feed)
    {
        foreach (var map in feed.Maps)
        {
            var mapName = ShortMapName(map.Name);

            foreach (var board in map.Boards)
            {
                var separator = board.Key.IndexOf(':');
                if (separator <= 0)
                {
                    continue;
                }

                var kind = board.Key[..separator];
                var target = board.Key[(separator + 1)..];
                var isEgg = kind == "ee";
                if (!isEgg && kind != "round")
                {
                    continue;
                }

                var eggName = isEgg && feed.EasterEggLabels.TryGetValue(target, out var label)
                    ? label
                    : target.Replace('_', ' ');

                foreach (var slot in board.Value)
                {
                    if (!int.TryParse(slot.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var teamSize))
                    {
                        continue;
                    }

                    // The site already keeps one entry per squad, fastest first.
                    var ranked = slot.Value
                        .Where(entry => entry.Ms > 0 && entry.PlayerMeta.Count > 0)
                        .OrderBy(entry => entry.Ms)
                        .ThenBy(entry => ParseDate(entry.Date))
                        .ToList();

                    long previousMs = -1;
                    var place = 0;

                    for (var position = 0; position < ranked.Count; position++)
                    {
                        var entry = ranked[position];
                        if (entry.Ms != previousMs)
                        {
                            place = position + 1;
                            previousMs = entry.Ms;
                        }

                        if (place > 3)
                        {
                            break;
                        }

                        var time = string.IsNullOrWhiteSpace(entry.Time) ? FormatTime(entry.Ms) : entry.Time;
                        var badge = isEgg
                            ? new Badge(
                                EasterEggsCategory,
                                2200,
                                place,
                                mapName,
                                GameName(map.Game),
                                time,
                                $"{eggName} · {teamSize}P",
                                Tooltip(
                                    $"{teamSize}P {eggName} in {time}",
                                    entry.Players,
                                    entry.Date,
                                    null),
                                $"{eggName}|{teamSize}")
                            : new Badge(
                                SpeedrunsCategory,
                                2100,
                                place,
                                mapName,
                                GameName(map.Game),
                                time,
                                $"R{target} · {teamSize}P",
                                Tooltip(
                                    $"{teamSize}P to round {target} in {time}",
                                    entry.Players,
                                    entry.Date,
                                    null),
                                $"{target.PadLeft(3, '0')}|{teamSize}");

                        Credit(index, entry.PlayerMeta, badge);
                    }
                }
            }
        }
    }

    // ── shared ───────────────────────────────────────────────────────────────

    private static void Credit(Dictionary<int, List<Badge>> index, IEnumerable<PlayerMeta> players, Badge badge)
    {
        foreach (var clientId in players
                     .Select(player => player.ProfileId)
                     .Where(clientId => clientId.HasValue && clientId.Value > 0)
                     .Select(clientId => clientId!.Value)
                     .Distinct())
        {
            if (!index.TryGetValue(clientId, out var clientBadges))
            {
                clientBadges = new List<Badge>();
                index[clientId] = clientBadges;
            }

            clientBadges.Add(badge);
        }
    }

    private static string TeamKey(IEnumerable<PlayerMeta> players) => string.Join(
        ":",
        players
            .Select(player => player.ProfileId)
            .Where(clientId => clientId.HasValue && clientId.Value > 0)
            .Select(clientId => clientId!.Value)
            .Distinct()
            .OrderBy(clientId => clientId));

    // Labels are titleized by the webfront, which splits "T5" into "T 5", so the
    // games are named in words.
    private static string GameName(string game) => game switch
    {
        "T4" => "World at War",
        "T5" => "Black Ops",
        "T6" => "Black Ops II",
        _ => game
    };

    // "T5 Der Riese" -> "Der Riese". The game is shown on the small line instead.
    private static string ShortMapName(string name) => GamePrefix.Replace(name ?? string.Empty, string.Empty).Trim();

    private static DateTime ParseDate(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : DateTime.MinValue;

    private static string FormatTime(long ms)
    {
        var total = ms / 1000;
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var seconds = total % 60;
        return hours > 0 ? $"{hours}:{minutes:00}:{seconds:00}" : $"{minutes}:{seconds:00}";
    }

    private static string Tooltip(string what, string players, string date, string? extra)
    {
        var parts = new List<string> { what, $"Players: {players}" };

        if (!string.IsNullOrWhiteSpace(date))
        {
            parts.Add($"Set: {date}");
        }

        if (!string.IsNullOrWhiteSpace(extra))
        {
            parts.Add(extra);
        }

        return string.Join(" | ", parts);
    }

    private static string Medal(int place) => place switch
    {
        1 => "🥇",
        2 => "🥈",
        3 => "🥉",
        // cuk-webfront.js reads the first character as the place, so 4th and
        // 5th need glyphs of their own.
        4 => "\U0001F396",
        _ => "\U0001F3C5"
    };

    public void Dispose()
    {
        IManagementEventSubscriptions.Load -= OnLoad;
        _cacheLock.Dispose();
    }

    private sealed record RankedEntry(LeaderboardEntry Entry, string TeamKey);

    // One placing. Tiles are built by grouping these per map.
    private sealed record Badge(
        string Category,
        int CategoryOrder,
        int Place,
        string MapName,
        string Label,
        string Headline,
        string Detail,
        string Tooltip,
        string Sort);

    private sealed class LeaderboardMap
    {
        [JsonPropertyName("game")]
        public string Game { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("slots")]
        public Dictionary<string, List<LeaderboardEntry>> Slots { get; init; } = new();
    }

    private sealed class LeaderboardEntry
    {
        [JsonPropertyName("date")]
        public string Date { get; init; } = string.Empty;

        [JsonPropertyName("players")]
        public string Players { get; init; } = string.Empty;

        [JsonPropertyName("round")]
        public int Round { get; init; }

        [JsonPropertyName("vod_url")]
        public string? VodUrl { get; init; }

        [JsonPropertyName("player_meta")]
        public List<PlayerMeta> PlayerMeta { get; init; } = new();
    }

    private sealed class SpeedrunFeed
    {
        [JsonPropertyName("ee_labels")]
        public Dictionary<string, string> EasterEggLabels { get; init; } = new();

        [JsonPropertyName("maps")]
        public List<SpeedrunMap> Maps { get; init; } = new();
    }

    private sealed class SpeedrunMap
    {
        [JsonPropertyName("game")]
        public string Game { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        // board key ("round:30" / "ee:<token>") -> squad size -> entries, fastest first
        [JsonPropertyName("boards")]
        public Dictionary<string, Dictionary<string, List<SpeedrunEntry>>> Boards { get; init; } = new();
    }

    private sealed class SpeedrunEntry
    {
        [JsonPropertyName("date")]
        public string Date { get; init; } = string.Empty;

        [JsonPropertyName("players")]
        public string Players { get; init; } = string.Empty;

        [JsonPropertyName("ms")]
        public long Ms { get; init; }

        [JsonPropertyName("time")]
        public string Time { get; init; } = string.Empty;

        [JsonPropertyName("player_meta")]
        public List<PlayerMeta> PlayerMeta { get; init; } = new();
    }

    private sealed class GameStatsFeed
    {
        [JsonPropertyName("gamestats")]
        public PlayerGameStats? GameStats { get; init; }
    }

    private sealed class PlayerProfile
    {
        [JsonPropertyName("totals")]
        public PlayerTotals? Totals { get; init; }

        [JsonPropertyName("records")]
        public List<PlayerRecord>? Records { get; init; }
    }

    private sealed class PlayerTotals
    {
        [JsonPropertyName("best_round")]
        public long BestRound { get; init; }

        [JsonPropertyName("records")]
        public long Records { get; init; }

        [JsonPropertyName("first_places")]
        public long FirstPlaces { get; init; }

        [JsonPropertyName("speedruns")]
        public long Speedruns { get; init; }
    }

    private sealed class PlayerRecord
    {
        [JsonPropertyName("map")]
        public string Map { get; init; } = string.Empty;

        [JsonPropertyName("round")]
        public int Round { get; init; }

        [JsonPropertyName("slot")]
        public string Slot { get; init; } = string.Empty;
    }

    private sealed class PlayerGameStats
    {
        [JsonPropertyName("kills")]
        public long Kills { get; init; }

        [JsonPropertyName("headshots")]
        public long Headshots { get; init; }

        [JsonPropertyName("revives")]
        public long Revives { get; init; }

        [JsonPropertyName("downs")]
        public long Downs { get; init; }

        [JsonPropertyName("games")]
        public long Games { get; init; }

        [JsonPropertyName("ms")]
        public long Ms { get; init; }

        [JsonPropertyName("best_game")]
        public BestGame? BestGame { get; init; }
    }

    private sealed class BestGame
    {
        [JsonPropertyName("kills")]
        public long Kills { get; init; }

        [JsonPropertyName("map")]
        public string Map { get; init; } = string.Empty;

        [JsonPropertyName("round")]
        public int Round { get; init; }

        [JsonPropertyName("date")]
        public string Date { get; init; } = string.Empty;
    }

    private sealed class LinkedAccounts
    {
        [JsonPropertyName("linked")]
        public List<int> Linked { get; init; } = new();
    }

    private sealed class PlayerMeta
    {
        [JsonPropertyName("profile_id")]
        public int? ProfileId { get; init; }
    }
}
