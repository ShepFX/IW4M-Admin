#:package RaidMax.IW4MAdmin.SharedLibraryCore@2026.3.22.1-preview

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Data.Abstractions;
using Data.Models;
using Data.Models.Client.Stats;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharedLibraryCore;
using SharedLibraryCore.Commands;
using SharedLibraryCore.Configuration;
using SharedLibraryCore.Events.Game;
using SharedLibraryCore.Events.Management;
using SharedLibraryCore.Events.Server;
using SharedLibraryCore.Interfaces;
using SharedLibraryCore.Interfaces.Events;
using EFClient = SharedLibraryCore.Database.Models.EFClient;
using Game = SharedLibraryCore.Server.Game;

/// <summary>
/// Game Interface Plugin - Provides bidirectional communication between IW4MAdmin and
/// game server GSC scripts via dvars or file-based bus.
/// </summary>
public sealed class GameInterfacePlugin : IPluginV2
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static void RegisterDependencies(IServiceCollection serviceCollection)
    {
        serviceCollection.AddConfiguration<GameInterfaceConfig>(
            "GameInterfaceSettings",
            new GameInterfaceConfig());
        serviceCollection.AddSingleton<GameInterfaceState>();
    }

    public string Name => "Game Interface";
    public string Author => "RaidMax";
    public string Version => "2.3";

    private readonly ILogger<GameInterfacePlugin> _logger;
    private readonly GameInterfaceConfig _config;
    private readonly GameInterfaceState _state;
    private readonly ITranslationLookup _translationLookup;
    private readonly IManager _manager;
    private readonly IMetaServiceV2 _metaService;
    private readonly IDatabaseContextFactory _contextFactory;
    private readonly IScriptCommandFactory _scriptCommandFactory;

    private const string InDvar = "sv_iw4madmin_in";
    private const string OutDvar = "sv_iw4madmin_out";
    private const string IntegrationEnabledDvar = "sv_iw4madmin_integration_enabled";
    private const char GroupSeparator = '\x1d';
    private const char RecordSeparator = '\x1e';
    private const char UnitSeparator = '\x1f';

    public GameInterfacePlugin(
        ILogger<GameInterfacePlugin> logger,
        GameInterfaceConfig config,
        GameInterfaceState state,
        ITranslationLookup translationLookup,
        IManager manager,
        IMetaServiceV2 metaService,
        IDatabaseContextFactory contextFactory,
        IScriptCommandFactory scriptCommandFactory)
    {
        _logger = logger;
        _config = config;
        _state = state;
        _translationLookup = translationLookup;
        _manager = manager;
        _metaService = metaService;
        _contextFactory = contextFactory;
        _scriptCommandFactory = scriptCommandFactory;

        IManagementEventSubscriptions.ClientStateInitialized += OnClientEnteredMatch;
        IGameServerEventSubscriptions.MonitoringStarted += OnServerMonitoringStart;
        IGameServerEventSubscriptions.ServerRemoved += OnServerRemoved;
        IGameEventSubscriptions.MatchStarted += OnMatchStart;
        IManagementEventSubscriptions.ClientPenaltyAdministered += OnPenalty;

        // Start loops for any servers already running (handles hot-reload scenario
        // where MonitoringStarted won't re-fire for existing servers)
        foreach (var server in _manager.GetServers().OfType<Server>())
        {
            InitializeServer(server);
        }

        _logger.LogInformation("[GameInterface] {Name} {Version} by {Author} loaded. PollingRate={PollingRate}ms",
            Name, Version, Author, _config.PollingRate);
    }

    #region Event Handlers

    private Task OnClientEnteredMatch(ClientStateInitializeEvent clientEvent, CancellationToken token)
    {
        var server = clientEvent.Client.CurrentServer;
        var serverState = _state.GetServerState(server.Id);

        if (serverState is null)
        {
            InitializeServer(server);
        }
        else if (serverState.LoopTask is null or { IsCompleted: true })
        {
            _logger.LogDebug("[GameInterface] restarting loop for {ServerId}", server.Id);
            StartLoop(server, serverState);
        }

        return Task.CompletedTask;
    }

    private Task OnPenalty(ClientPenaltyEvent penaltyEvent, CancellationToken token)
    {
        if (penaltyEvent.Penalty.Type != EFPenalty.PenaltyType.Warning || !penaltyEvent.Client.IsIngame)
            return Task.CompletedTask;

        SendScriptCommand(penaltyEvent.Client.CurrentServer, "Alert",
            penaltyEvent.Penalty.Punisher as EFClient, penaltyEvent.Client,
            new Dictionary<string, string>
            {
                ["alertType"] = (_translationLookup["GLOBAL_WARNING"] ?? "Warning") + "!",
                ["message"] = penaltyEvent.Penalty.Offense
            });

        return Task.CompletedTask;
    }

    private Task OnServerMonitoringStart(MonitorStartEvent monitorStartEvent, CancellationToken token)
    {
        if (monitorStartEvent.Server is Server server)
            InitializeServer(server);
        return Task.CompletedTask;
    }

    private Task OnServerRemoved(ServerRemoveEvent serverRemovedEvent, CancellationToken token)
    {
        var serverId = serverRemovedEvent.Server.Id;
        var serverState = _state.GetServerState(serverId);
        if (serverState is not null)
        {
            _logger.LogInformation("[GameInterface] cleaning up server state for removed server {ServerId}", serverId);
            serverState.Stop();
            _state.RemoveServerState(serverId);
        }

        return Task.CompletedTask;
    }

    private Task OnMatchStart(MatchStartEvent matchStartEvent, CancellationToken token)
    {
        _state.BusMode = "rcon";
        QueueEventMessage(matchStartEvent.Server, true, "GetBusModeRequested", null, null, null,
            new Dictionary<string, string>());
        return Task.CompletedTask;
    }

    #endregion

    #region Server Loop

    private void InitializeServer(Server server)
    {
        var state = new ServerState(server);
        _state.SetServerState(server.Id, state);
        _logger.LogDebug("[GameInterface] initializing game interface for {ServerId}", server.Id);
        StartLoop(server, state);
    }

    private void StartLoop(Server server, ServerState state)
    {
        state.Stop();
        state.LoopCts = CancellationTokenSource.CreateLinkedTokenSource(_manager.CancellationToken);
        state.LoopTask = Task.Run(() => RunServerLoopAsync(server, state, state.LoopCts.Token));
    }

    private async Task RunServerLoopAsync(Server server, ServerState state, CancellationToken token)
    {
        try
        {
            // Phase 1: Check if GSC integration is enabled on this server
            var enabledDvar = await server.GetDvarAsync(IntegrationEnabledDvar, "", token);

            if (enabledDvar?.Value != "1")
            {
                _logger.LogInformation("[GameInterface] gsc integration is disabled for {Server}", server.Id);
                return;
            }

            _logger.LogInformation("[GameInterface] gsc integration is enabled for {Server}", server.Id);

            state.Enabled = true;

            // todo: this might not work for all games
            server.RconParser.Configuration.FloodProtectInterval = 150;

            // Clear any stale dvar state from a previous session
            await SetDvarValueAsync(server, InDvar, "", token);

            // Request bus mode and available commands from the game
            QueueEventMessage(server, true, "GetBusModeRequested", null, null, null,
                new Dictionary<string, string>());
            QueueEventMessage(server, true, "GetCommandsRequested", null, null, null,
                new Dictionary<string, string>());

            // Phase 2: Main polling loop
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(CalculateDelay(server), token);

                if (server.ConnectedClients.Count == 0 && !Utilities.IsDevelopment)
                {
                    _logger.LogDebug("[GameInterface] no clients connected, pausing loop for {ServerId}", server.Id);
                    return; // loop exits; OnClientEnteredMatch will restart it
                }

                await PollServerAsync(server, state, token);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("[GameInterface] loop cancelled for {ServerId}", server.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError("[GameInterface] loop terminated unexpectedly for {ServerId}: {Exception}",
                server.Id, ex.ToString());
        }
        finally
        {
            _logger.LogDebug("[GameInterface] loop exited for {ServerId}", server.Id);
        }
    }

    private async Task PollServerAsync(Server server, ServerState state, CancellationToken token)
    {
        try
        {
            var input = await GetDvarValueAsync(server, InDvar, token);

            if (string.IsNullOrEmpty(input) || input == "null")
            {
                // No data from game — send next outgoing message if queued
                if (!state.CommandQueue.TryDequeue(out var outgoing)) return;
                _logger.LogDebug("[GameInterface] sending queued command to {ServerId}", server.Id);
                await SetDvarValueAsync(server, OutDvar, outgoing, token);

                return;
            }

            _logger.LogDebug("[GameInterface] received data from {ServerId}: {Input}", server.Id, input);

            // Acknowledge receipt by clearing the inbound dvar
            await SetDvarValueAsync(server, InDvar, "", token);

            // Process the message from the game
            await ProcessEventMessageAsync(input, server, token);
        }
        catch (OperationCanceledException)
        {
            throw;
        } // let the outer loop handle cancellation
        catch (Exception ex)
        {
            _logger.LogWarning("[GameInterface] poll iteration failed for {ServerId}: {Exception}",
                server.Id, ex.ToString());
        }
    }

    /// <summary>
    /// Reads a dvar value, using file bus or direct RCon depending on current bus mode.
    /// </summary>
    private async Task<string> GetDvarValueAsync(Server server, string dvarName, CancellationToken token)
    {
        if (_state.BusMode == "file")
        {
            var path = Path.Combine(_state.BusDir, FileForDvar(dvarName));
            return await File.ReadAllTextAsync(path, token);
        }

        var dvar = await server.GetDvarAsync(dvarName, "", token);
        return dvar?.Value ?? "";
    }

    /// <summary>
    /// Writes a dvar value, using file bus or direct RCon depending on current bus mode.
    /// </summary>
    private async Task SetDvarValueAsync(Server server, string dvarName, string value, CancellationToken token)
    {
        if (_state.BusMode == "file")
        {
            var path = Path.Combine(_state.BusDir, FileForDvar(dvarName));
            _logger.LogDebug("[GameInterface] writing {Value} to {File}", value, path);
            await File.WriteAllTextAsync(path, value, token);
            return;
        }

        await server.SetDvarAsync(dvarName, value, token);
    }

    #endregion

    #region Event Message Processing

    private async Task ProcessEventMessageAsync(string input, IGameServer server, CancellationToken token)
    {
        var eventData = ParseEvent(input);

        _logger.LogDebug("[GameInterface] processing {EventType} {SubType} {ClientNumber}",
            eventData.EventType, eventData.SubType, eventData.ClientNumber);

        switch (eventData.EventType)
        {
            case "ClientDataRequested":
                await HandleClientDataRequestedAsync(eventData, server, token);
                break;
            case "SetClientDataRequested":
                await HandleSetClientDataRequestedAsync(eventData, server, token);
                break;
            case "UrlRequested":
                await HandleUrlRequestedAsync(eventData, server, token);
                break;
            case "RegisterCommandRequested":
                HandleRegisterCommandRequested(eventData);
                break;
            case "GetBusModeRequested":
                HandleGetBusModeRequested(eventData);
                break;
        }
    }

    private async Task HandleClientDataRequestedAsync(GameInterfaceEvent eventData, IGameServer server,
        CancellationToken token)
    {
        var clientNumber = int.TryParse(eventData.ClientNumber, out var cn) ? cn : -1;
        var client = server.ConnectedClients.FirstOrDefault(c => c.ClientNumber == clientNumber);

        if (client is null)
        {
            _logger.LogWarning("[GameInterface] could not find client slot {ClientNumber} when processing {EventType}",
                eventData.ClientNumber, eventData.EventType);
            QueueEventMessage(server, false, "ClientDataReceived", "Fail", null, null,
                new Dictionary<string, string> { ["ClientNumber"] = eventData.ClientNumber ?? "" });
            return;
        }

        _logger.LogDebug("[GameInterface] Found client {Name}", client.Name);

        Dictionary<string, string> data;

        if (eventData.SubType == "Meta")
        {
            var metaKey = eventData.Data?.ToString() ?? "";
            var meta = await _metaService.GetPersistentMeta(metaKey, client.ClientId, token);
            data = new Dictionary<string, string> { [metaKey] = meta?.Value ?? "" };
        }
        else
        {
            var clientStats = await GetClientStatsAsync(client.ClientId, server.LegacyDatabaseId, token);
            var tagMeta = await _metaService.GetPersistentMetaByLookup(
                "ClientTagV2", "ClientTagNameV2", client.ClientId, token);

            data = new Dictionary<string, string>
            {
                ["level"] = client.Level.ToString(),
                ["clientId"] = client.ClientId.ToString(CultureInfo.InvariantCulture),
                ["lastConnection"] = client.TimeSinceLastConnectionString,
                ["tag"] = tagMeta?.Value ?? "",
                ["performance"] = (clientStats?.Performance ?? 200.0).ToString("F1", CultureInfo.InvariantCulture),
                ["ipAddress"] = client.IPAddressString
            };
        }

        QueueEventMessage(server, false, "ClientDataReceived", eventData.SubType, client, null, data);
    }

    private async Task HandleSetClientDataRequestedAsync(GameInterfaceEvent eventData, IGameServer server,
        CancellationToken token)
    {
        var clientNumber = int.TryParse(eventData.ClientNumber, out var cn) ? cn : -1;
        var client = server.ConnectedClients.FirstOrDefault(c => c.ClientNumber == clientNumber);
        var dataDict = eventData.Data as Dictionary<string, string?>;

        var clientId = client?.ClientId
                       ?? (dataDict is not null
                           && dataDict.TryGetValue("clientId", out var idStr)
                           && int.TryParse(idStr, out var parsed)
                           ? parsed
                           : -1);

        _logger.LogDebug("[GameInterface] clientId={ClientId}", clientId);

        if (clientId < 0)
        {
            _logger.LogWarning(
                "[GameInterface] could not find client slot {ClientNumber} when processing {EventType}: {EventData}",
                eventData.ClientNumber, eventData.EventType, eventData.Data);
            QueueEventMessage(server, false, "SetClientDataCompleted", "Meta", null, null,
                new Dictionary<string, string>
                {
                    ["ClientNumber"] = eventData.ClientNumber ?? "",
                    ["status"] = "Fail"
                });
            return;
        }

        if (eventData.SubType != "Meta" || dataDict is null
                                        || !dataDict.TryGetValue("value", out var value)
                                        || !dataDict.TryGetValue("key", out var key))
        {
            return;
        }

        var status = "Complete";

        try
        {
            _logger.LogDebug("[GameInterface] key={Key}, value={Value}, direction={Direction}",
                key, value, dataDict.GetValueOrDefault("direction"));

            if (dataDict.TryGetValue("direction", out var direction) && direction is not null)
            {
                if (int.TryParse(value, out var parsedValue))
                {
                    if (direction == "increment")
                        await _metaService.IncrementPersistentMeta(key, parsedValue, clientId, token);
                    else
                        await _metaService.DecrementPersistentMeta(key, parsedValue, clientId, token);
                }
            }
            else
            {
                await _metaService.SetPersistentMeta(key, value, clientId, token);
            }

            if (key == "PersistentClientGuid" && client is not null)
            {
                _manager.QueueEvent(new ClientPersistentIdReceiveEvent(client, value));
            }
        }
        catch (Exception error)
        {
            status = "Fail";
            _logger.LogError("[GameInterface] could not persist client meta {Key}={Value} {Error} for {Client}",
                key, value, error.ToString(), clientId);
        }

        QueueEventMessage(server, false, "SetClientDataCompleted", "Meta", null, null,
            new Dictionary<string, string>
            {
                ["ClientNumber"] = eventData.ClientNumber ?? "",
                ["status"] = status
            });
    }

    private async Task HandleUrlRequestedAsync(GameInterfaceEvent eventData, IGameServer server,
        CancellationToken token)
    {
        var dataDict = eventData.Data as Dictionary<string, string>;
        if (dataDict is null || !dataDict.TryGetValue("url", out var url) || string.IsNullOrEmpty(url))
        {
            _logger.LogWarning("[GameInterface] no url provided for gamescript web request");
            return;
        }

        var method = dataDict.GetValueOrDefault("method") ?? "GET";
        var contentType = dataDict.GetValueOrDefault("contentType") ?? "text/plain";
        var body = dataDict.GetValueOrDefault("body");
        var headerString = dataDict.GetValueOrDefault("headers");

        _logger.LogDebug("[GameInterface] making gamescript web request {Url} {Method}", url, method);

        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), url);

            if (headerString is not null)
            {
                foreach (var header in headerString.Split(','))
                {
                    var kv = header.Split(':');
                    if (kv.Length == 2)
                        request.Headers.TryAddWithoutValidation(kv[0].Trim(), kv[1].Trim());
                }
            }

            if (body is not null)
                request.Content = new StringContent(body, Encoding.UTF8, contentType);

            using var response = await HttpClient.SendAsync(request, token);
            var responseString = await response.Content.ReadAsStringAsync(token);

            _logger.LogDebug("[GameInterface] web response length={Length}", responseString.Length);

            var quoteReplace = server.GameCode == Reference.Game.T6 ? "\\\\\\\"" : "\\\"";

            var cleaned = responseString
                .Replace("\"", quoteReplace, StringComparison.Ordinal)
                .Replace("\n", "", StringComparison.Ordinal)
                .Replace("\t", "", StringComparison.Ordinal);

            const int chunkSize = 800;
            const int maxChunks = 10;
            var chunks = ChunkString(cleaned, chunkSize);

            if (chunks.Count > maxChunks)
            {
                _logger.LogWarning("[GameInterface] response chunks exceed max ({Max}), truncating", maxChunks);
                chunks = chunks.Take(maxChunks).ToList();
            }

            var entity = dataDict.GetValueOrDefault("entity") ?? "";
            for (var i = 0; i < chunks.Count; i++)
            {
                QueueEventMessage(server, false, "UrlRequestCompleted", null, null, null,
                    new Dictionary<string, string>
                    {
                        ["entity"] = entity,
                        ["remaining"] = (chunks.Count - (i + 1)).ToString(CultureInfo.InvariantCulture),
                        ["response"] = chunks[i]
                    });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("[GameInterface] web request failed: {Exception}", ex.ToString());
        }
    }

    private void HandleRegisterCommandRequested(GameInterfaceEvent eventData)
    {
        if (eventData.Data is not Dictionary<string, string> dataDict) return;

        var commandName = dataDict.GetValueOrDefault("name") ?? "DEFAULT";
        var description = dataDict.GetValueOrDefault("description") ?? "DEFAULT";
        var alias = dataDict.GetValueOrDefault("alias") ?? "DEFAULT";
        var permission = dataDict.GetValueOrDefault("minPermission") ?? "User";
        var targetRequired = (dataDict.GetValueOrDefault("targetRequired") ?? "0") == "1";
        var supportedGamesStr = dataDict.GetValueOrDefault("supportedGames") ?? "";
        var eventKey = dataDict.GetValueOrDefault("eventKey") ?? commandName;

        var supportedGames = supportedGamesStr.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(g => Enum.TryParse<Reference.Game>(g.Trim(), out var game) ? game : (Reference.Game?)null)
            .Where(g => g.HasValue)
            .Select(g => g!.Value);

        var plugin = this;

        var scriptCommand = _scriptCommandFactory.CreateScriptCommand(
            commandName, alias, description, permission, targetRequired,
            [], ExecuteAction, supportedGames);

        _manager.RemoveCommandByName(scriptCommand.Name);
        _manager.AddAdditionalCommand(scriptCommand);
        _logger.LogDebug("[GameInterface] registered dynamic command {Name}", commandName);
        return;

        Task ExecuteAction(GameEvent gameEvent)
        {
            if (!plugin.ValidateEnabled(gameEvent.Owner, gameEvent.Origin)) return Task.CompletedTask;

            if (gameEvent.Data == "--reload" && gameEvent.Origin.Level == Data.Models.Client.EFClient.Permission.Owner)
            {
                plugin.QueueEventMessage(gameEvent.Owner, true, "GetCommandsRequested", null, null, null,
                    new Dictionary<string, string> { ["name"] = gameEvent.Extra?.ToString() ?? "" });
            }
            else
            {
                plugin.SendScriptCommand(gameEvent.Owner, $"{eventKey}Execute", gameEvent.Origin, gameEvent.Target,
                    new Dictionary<string, string> { ["args"] = gameEvent.Data ?? "" });
            }

            return Task.CompletedTask;
        }
    }

    private void HandleGetBusModeRequested(GameInterfaceEvent eventData)
    {
        if (eventData.Data is not Dictionary<string, string> dataDict) return;
        if (!dataDict.TryGetValue("directory", out var directory) || !dataDict.TryGetValue("mode", out var mode))
            return;

        _state.BusMode = mode;
        _state.BusDir = directory.Replace("'", "", StringComparison.Ordinal)
            .Replace("\"", "", StringComparison.Ordinal);

        if (_state.BusMode == "file"
            && dataDict.TryGetValue("inLocation", out var inLoc)
            && dataDict.TryGetValue("outLocation", out var outLoc))
        {
            _state.BusFileIn = inLoc;
            _state.BusFileOut = outLoc;
        }

        _logger.LogDebug("[GameInterface] setting bus mode to {Mode} dir={Dir}", _state.BusMode, _state.BusDir);
    }

    #endregion

    #region Helpers

    private void QueueEventMessage(IGameServer server, bool responseExpected, string eventType,
        string? subType, EFClient? origin, EFClient? target, Dictionary<string, string> data)
    {
        var serverState = _state.GetServerState(server.Id);
        if (serverState is null) return;

        var output = FormatEventMessage(responseExpected, eventType, subType,
            origin?.ClientNumber ?? -1, target?.ClientNumber ?? -1, data);

        serverState.CommandQueue.Enqueue(output);
    }

    private void SendScriptCommand(IGameServer server, string command, EFClient? origin, EFClient? target,
        Dictionary<string, string>? data)
    {
        var serverState = _state.GetServerState(server.Id);
        if (serverState is not { Enabled: true }) return;

        QueueEventMessage(server, false, "ExecuteCommandRequested", command, origin, target,
            data ?? new Dictionary<string, string>());
    }

    private int CalculateDelay(IGameServer server)
    {
        var delayMs = _config.PollingRate;

        if (server.MatchEndTime is null) return delayMs;

        const int extraDelay = 15000;
        var diff = (DateTime.Now - server.MatchEndTime.Value).TotalMilliseconds;
        if (diff is < 0 or >= extraDelay) return delayMs;

        delayMs = (int)(extraDelay - diff) + _config.PollingRate;
        _logger.LogDebug("[GameInterface] increasing delay to {Delay}ms due to recent map change", delayMs);

        return delayMs;
    }

    private async Task<EFClientStatistics?> GetClientStatsAsync(int clientId, long serverId, CancellationToken token)
    {
        try
        {
            await using var context = _contextFactory.CreateContext(false);
            return await context.ClientStatistics
                .FirstOrDefaultAsync(s => s.ClientId == clientId && s.ServerId == serverId, token);
        }
        catch (Exception ex)
        {
            _logger.LogError("[GameInterface] failed to get client stats: {Exception}", ex.ToString());
            return null;
        }
    }

    private bool ValidateEnabled(IGameServer server, EFClient origin)
    {
        var enabled = _state.GetServerState(server.Id) is { Enabled: true };
        if (!enabled)
            origin.Tell("Game interface is not enabled on this server");
        return enabled;
    }

    private string FileForDvar(string dvar) => dvar == InDvar ? _state.BusFileIn : _state.BusFileOut;

    #endregion

    #region Dispose

    public void Dispose()
    {
        IManagementEventSubscriptions.ClientStateInitialized -= OnClientEnteredMatch;
        IGameServerEventSubscriptions.MonitoringStarted -= OnServerMonitoringStart;
        IGameServerEventSubscriptions.ServerRemoved -= OnServerRemoved;
        IGameEventSubscriptions.MatchStarted -= OnMatchStart;
        IManagementEventSubscriptions.ClientPenaltyAdministered -= OnPenalty;

        _state.StopAll();

        _logger.LogInformation("[GameInterface] Game Interface unloaded");
    }

    #endregion

    #region Message Formatting / Parsing

    internal static string FormatEventMessage(bool responseExpected, string eventType, string? subType,
        int originClientNumber, int targetClientNumber, Dictionary<string, string>? data)
    {
        // subType must never be empty — CoD GSC strtok collapses consecutive delimiters,
        // which shifts all subsequent field indices. The JS version produced the literal
        // string "null" for null values via JavaScript template interpolation.
        return $"{(responseExpected ? '1' : '0')}{GroupSeparator}{eventType}{GroupSeparator}" +
               $"{subType ?? "null"}{GroupSeparator}{originClientNumber}{GroupSeparator}" +
               $"{targetClientNumber}{GroupSeparator}{BuildDataString(data)}";
    }

    private static string BuildDataString(Dictionary<string, string>? data)
    {
        if (data is null || data.Count == 0) return "";

        var sb = new StringBuilder();
        var first = true;
        foreach (var (key, value) in data)
        {
            if (!first) sb.Append(RecordSeparator);
            sb.Append(key);
            sb.Append(UnitSeparator);
            sb.Append(value);
            first = false;
        }

        return sb.ToString();
    }

    private static GameInterfaceEvent ParseEvent(string input)
    {
        if (string.IsNullOrEmpty(input)) return new GameInterfaceEvent();

        var parts = input.Split(GroupSeparator);
        return new GameInterfaceEvent
        {
            EventType = parts.Length > 1 ? parts[1] : null,
            SubType = parts.Length > 2 ? parts[2] : null,
            ClientNumber = parts.Length > 3 ? parts[3] : null,
            Data = parts.Length > 4 ? ParseDataString(parts[4]) : null
        };
    }

    private static object? ParseDataString(string data)
    {
        if (string.IsNullOrEmpty(data)) return data;

        var dict = new Dictionary<string, string>();
        foreach (var segment in data.Split(RecordSeparator))
        {
            var kv = segment.Split(UnitSeparator);
            if (kv.Length == 2)
                dict[kv[0]] = kv[1];
        }

        return dict.Count == 0 ? data : dict;
    }

    private static List<string> ChunkString(string str, int chunkSize)
    {
        var result = new List<string>((str.Length / chunkSize) + 1);
        for (var i = 0; i < str.Length; i += chunkSize)
            result.Add(str.Substring(i, Math.Min(chunkSize, str.Length - i)));
        return result;
    }

    #endregion
}

#region Supporting Types

public class GameInterfaceState
{
    private readonly ConcurrentDictionary<string, ServerState> _servers = new();

    public string BusMode { get; set; } = "rcon";
    public string BusDir { get; set; } = "";
    public string BusFileIn { get; set; } = "";
    public string BusFileOut { get; set; } = "";

    public ServerState? GetServerState(string serverId) =>
        _servers.TryGetValue(serverId, out var state) ? state : null;

    public void SetServerState(string serverId, ServerState state) => _servers[serverId] = state;

    public void RemoveServerState(string serverId) => _servers.TryRemove(serverId, out _);

    public void StopAll()
    {
        foreach (var state in _servers.Values)
            state.Stop();
    }
}

public class ServerState(Server server)
{
    public Server Server { get; } = server;
    public bool Enabled { get; set; }
    public ConcurrentQueue<string> CommandQueue { get; } = new();
    public CancellationTokenSource? LoopCts { get; set; }
    public Task? LoopTask { get; set; }

    public void Stop()
    {
        LoopCts?.Cancel();
        LoopCts?.Dispose();
        LoopCts = null;
    }
}

public class GameInterfaceEvent
{
    public string? EventType { get; init; }
    public string? SubType { get; init; }
    public string? ClientNumber { get; init; }
    public object? Data { get; init; }
}

public class GameInterfaceConfig
{
    public int PollingRate { get; set; } = 300;
}

#endregion

#region Commands

/// <summary>
/// Base class for all game interface commands. Handles validation and
/// script command dispatch through the shared <see cref="GameInterfaceState"/>.
/// </summary>
public abstract class GameInterfaceCommand(
    CommandConfiguration config,
    ITranslationLookup translationLookup,
    GameInterfaceState state)
    : Command(config, translationLookup)
{
    protected bool ValidateEnabled(GameEvent gameEvent)
    {
        ArgumentNullException.ThrowIfNull(gameEvent);
        var enabled = state.GetServerState(gameEvent.Owner.Id) is { Enabled: true };
        if (!enabled)
            gameEvent.Origin.Tell("Game interface is not enabled on this server");
        return enabled;
    }

    protected void SendScriptCommand(IGameServer server, string command,
        EFClient? origin, EFClient? target, Dictionary<string, string>? data)
    {
        ArgumentNullException.ThrowIfNull(server);
        var serverState = state.GetServerState(server.Id);
        if (serverState is not { Enabled: true }) return;

        var output = GameInterfacePlugin.FormatEventMessage(false, "ExecuteCommandRequested", command,
            origin?.ClientNumber ?? -1, target?.ClientNumber ?? -1, data);
        serverState.CommandQueue.Enqueue(output);
    }

    protected static readonly Game[] AllScriptGames = [Game.IW4, Game.IW5, Game.T4, Game.T5, Game.T6];
}

public class GiveWeaponCommand : GameInterfaceCommand
{
    public GiveWeaponCommand(CommandConfiguration config, ITranslationLookup translationLookup,
        GameInterfaceState state) : base(config, translationLookup, state)
    {
        Name = "giveweapon";
        Description = "gives specified weapon";
        Alias = "gw";
        Permission = Data.Models.Client.EFClient.Permission.SeniorAdmin;
        RequiresTarget = true;
        Arguments = [new CommandArgument { Name = "player", Required = true }, new() { Name = "weapon name", Required = true }];
        SupportedGames = AllScriptGames;
    }

    public override Task ExecuteAsync(GameEvent gameEvent)
    {
        if (!ValidateEnabled(gameEvent)) return Task.CompletedTask;
        SendScriptCommand(gameEvent.Owner, "GiveWeapon", gameEvent.Origin, gameEvent.Target,
            new Dictionary<string, string> { ["weaponName"] = gameEvent.Data });
        return Task.CompletedTask;
    }
}

public class TakeWeaponsCommand : GameInterfaceCommand
{
    public TakeWeaponsCommand(CommandConfiguration config, ITranslationLookup translationLookup,
        GameInterfaceState state) : base(config, translationLookup, state)
    {
        Name = "takeweapons";
        Description = "take all weapons from specified player";
        Alias = "tw";
        Permission = Data.Models.Client.EFClient.Permission.SeniorAdmin;
        RequiresTarget = true;
        Arguments = [new CommandArgument { Name = "player", Required = true }];
        SupportedGames = AllScriptGames;
    }

    public override Task ExecuteAsync(GameEvent gameEvent)
    {
        if (!ValidateEnabled(gameEvent)) return Task.CompletedTask;
        SendScriptCommand(gameEvent.Owner, "TakeWeapons", gameEvent.Origin, gameEvent.Target, null);
        return Task.CompletedTask;
    }
}

public class SwitchTeamCommand : GameInterfaceCommand
{
    public SwitchTeamCommand(CommandConfiguration config, ITranslationLookup translationLookup,
        GameInterfaceState state) : base(config, translationLookup, state)
    {
        Name = "switchteam";
        Description = "switches specified player to the opposite team";
        Alias = "st";
        Permission = Data.Models.Client.EFClient.Permission.Administrator;
        RequiresTarget = true;
        Arguments = [new CommandArgument { Name = "player", Required = true }];
        SupportedGames = AllScriptGames;
    }

    public override Task ExecuteAsync(GameEvent gameEvent)
    {
        if (!ValidateEnabled(gameEvent)) return Task.CompletedTask;
        SendScriptCommand(gameEvent.Owner, "SwitchTeams", gameEvent.Origin, gameEvent.Target, null);
        return Task.CompletedTask;
    }
}

public class LockControlsCommand : GameInterfaceCommand
{
    public LockControlsCommand(CommandConfiguration config, ITranslationLookup translationLookup,
        GameInterfaceState state) : base(config, translationLookup, state)
    {
        Name = "lockcontrols";
        Description = "locks target player's controls";
        Alias = "lc";
        Permission = Data.Models.Client.EFClient.Permission.Administrator;
        RequiresTarget = true;
        Arguments = [new CommandArgument { Name = "player", Required = true }];
        SupportedGames = AllScriptGames;
    }

    public override Task ExecuteAsync(GameEvent gameEvent)
    {
        if (!ValidateEnabled(gameEvent)) return Task.CompletedTask;
        SendScriptCommand(gameEvent.Owner, "LockControls", gameEvent.Origin, gameEvent.Target, null);
        return Task.CompletedTask;
    }
}

public class NoClipCommand : GameInterfaceCommand
{
    public NoClipCommand(CommandConfiguration config, ITranslationLookup translationLookup,
        GameInterfaceState state) : base(config, translationLookup, state)
    {
        Name = "noclip";
        Description = "enable noclip on yourself ingame";
        Alias = "nc";
        Permission = Data.Models.Client.EFClient.Permission.SeniorAdmin;
        RequiresTarget = false;
        SupportedGames = [Game.IW4, Game.IW5];
    }

    public override Task ExecuteAsync(GameEvent gameEvent)
    {
        if (!ValidateEnabled(gameEvent)) return Task.CompletedTask;
        SendScriptCommand(gameEvent.Owner, "NoClip", gameEvent.Origin, gameEvent.Origin, null);
        return Task.CompletedTask;
    }
}

public class HideCommand : GameInterfaceCommand
{
    public HideCommand(CommandConfiguration config, ITranslationLookup translationLookup,
        GameInterfaceState state) : base(config, translationLookup, state)
    {
        Name = "hide";
        Description = "hide yourself ingame";
        Alias = "hi";
        Permission = Data.Models.Client.EFClient.Permission.SeniorAdmin;
        RequiresTarget = false;
        SupportedGames = AllScriptGames;
    }

    public override Task ExecuteAsync(GameEvent gameEvent)
    {
        if (!ValidateEnabled(gameEvent)) return Task.CompletedTask;
        SendScriptCommand(gameEvent.Owner, "Hide", gameEvent.Origin, gameEvent.Origin, null);
        return Task.CompletedTask;
    }
}

public class AlertCommand : GameInterfaceCommand
{
    public AlertCommand(CommandConfiguration config, ITranslationLookup translationLookup,
        GameInterfaceState state) : base(config, translationLookup, state)
    {
        Name = "alert";
        Description = "alert a player";
        Alias = "alr";
        Permission = Data.Models.Client.EFClient.Permission.SeniorAdmin;
        RequiresTarget = true;
        Arguments = [new CommandArgument { Name = "player", Required = true }, new CommandArgument { Name = "message", Required = true }];
        SupportedGames = AllScriptGames;
    }

    public override Task ExecuteAsync(GameEvent gameEvent)
    {
        if (!ValidateEnabled(gameEvent)) return Task.CompletedTask;
        SendScriptCommand(gameEvent.Owner, "Alert", gameEvent.Origin, gameEvent.Target,
            new Dictionary<string, string> { ["alertType"] = "Alert", ["message"] = gameEvent.Data });
        return Task.CompletedTask;
    }
}

public class GotoPlayerCommand : GameInterfaceCommand
{
    public GotoPlayerCommand(CommandConfiguration config, ITranslationLookup translationLookup,
        GameInterfaceState state) : base(config, translationLookup, state)
    {
        Name = "gotoplayer";
        Description = "teleport to a player";
        Alias = "g2p";
        Permission = Data.Models.Client.EFClient.Permission.SeniorAdmin;
        RequiresTarget = true;
        Arguments = [new CommandArgument { Name = "player", Required = true }];
        SupportedGames = AllScriptGames;
    }

    public override Task ExecuteAsync(GameEvent gameEvent)
    {
        if (!ValidateEnabled(gameEvent)) return Task.CompletedTask;
        SendScriptCommand(gameEvent.Owner, "Goto", gameEvent.Origin, gameEvent.Target, null);
        return Task.CompletedTask;
    }
}

public class PlayerToMeCommand : GameInterfaceCommand
{
    public PlayerToMeCommand(CommandConfiguration config, ITranslationLookup translationLookup,
        GameInterfaceState state) : base(config, translationLookup, state)
    {
        Name = "playertome";
        Description = "teleport a player to you";
        Alias = "p2m";
        Permission = Data.Models.Client.EFClient.Permission.SeniorAdmin;
        RequiresTarget = true;
        Arguments = [new CommandArgument { Name = "player", Required = true }];
        SupportedGames = AllScriptGames;
    }

    public override Task ExecuteAsync(GameEvent gameEvent)
    {
        if (!ValidateEnabled(gameEvent)) return Task.CompletedTask;
        SendScriptCommand(gameEvent.Owner, "PlayerToMe", gameEvent.Origin, gameEvent.Target, null);
        return Task.CompletedTask;
    }
}

public class GotoCommand : GameInterfaceCommand
{
    public GotoCommand(CommandConfiguration config, ITranslationLookup translationLookup,
        GameInterfaceState state) : base(config, translationLookup, state)
    {
        Name = "goto";
        Description = "teleport to a position";
        Alias = "g2";
        Permission = Data.Models.Client.EFClient.Permission.SeniorAdmin;
        RequiresTarget = false;
        Arguments =
        [
            new CommandArgument { Name = "x", Required = true },
            new CommandArgument { Name = "y", Required = true },
            new CommandArgument { Name = "z", Required = true }
        ];
        SupportedGames = AllScriptGames;
    }

    public override Task ExecuteAsync(GameEvent gameEvent)
    {
        if (!ValidateEnabled(gameEvent)) return Task.CompletedTask;

        var args = (gameEvent.Data ?? "").Split(' ');
        SendScriptCommand(gameEvent.Owner, "Goto", gameEvent.Origin, gameEvent.Target,
            new Dictionary<string, string>
            {
                ["x"] = args.ElementAtOrDefault(0) ?? "0",
                ["y"] = args.ElementAtOrDefault(1) ?? "0",
                ["z"] = args.ElementAtOrDefault(2) ?? "0"
            });
        return Task.CompletedTask;
    }
}

public class KillPlayerCommand : GameInterfaceCommand
{
    public KillPlayerCommand(CommandConfiguration config, ITranslationLookup translationLookup,
        GameInterfaceState state) : base(config, translationLookup, state)
    {
        Name = "kill";
        Description = "kill a player";
        Alias = "kpl";
        Permission = Data.Models.Client.EFClient.Permission.SeniorAdmin;
        RequiresTarget = true;
        Arguments = [new CommandArgument { Name = "player", Required = true }];
        SupportedGames = AllScriptGames;
    }

    public override Task ExecuteAsync(GameEvent gameEvent)
    {
        if (!ValidateEnabled(gameEvent)) return Task.CompletedTask;
        SendScriptCommand(gameEvent.Owner, "Kill", gameEvent.Origin, gameEvent.Target, null);
        return Task.CompletedTask;
    }
}

public class SetSpectatorCommand : GameInterfaceCommand
{
    public SetSpectatorCommand(CommandConfiguration config, ITranslationLookup translationLookup,
        GameInterfaceState state) : base(config, translationLookup, state)
    {
        Name = "setspectator";
        Description = "sets a player as spectator";
        Alias = "spec";
        Permission = Data.Models.Client.EFClient.Permission.Administrator;
        RequiresTarget = true;
        Arguments = [new CommandArgument { Name = "player", Required = true }];
        SupportedGames = AllScriptGames;
    }

    public override Task ExecuteAsync(GameEvent gameEvent)
    {
        if (!ValidateEnabled(gameEvent)) return Task.CompletedTask;
        SendScriptCommand(gameEvent.Owner, "SetSpectator", gameEvent.Origin, gameEvent.Target, null);
        return Task.CompletedTask;
    }
}

#endregion

#region Team balance

/// <summary>
///     One slot of the live line-up, as the game log records it. IW4MAdmin runs
///     with IgnoreBots, so bots never become clients; the log is the one place
///     that still knows every slot, which team it is on and how it has played
///     this match.
/// </summary>
internal sealed class BalanceSlot
{
    public int Slot { get; init; }
    public string Guid { get; init; } = "";
    public string Name { get; init; } = "";
    public string Team { get; set; } = "";
    public int Kills { get; set; }
    public int Deaths { get; set; }

    // Bots are told apart by GUID, never by name: T6 and T4 bots log a GUID of
    // 0 and IW5 bots log "botNNN", while a human is free to call themselves
    // "[bot]anything" - and one on Nuketown does.
    public bool IsBot => Guid == "0" || Guid.StartsWith("bot", StringComparison.OrdinalIgnoreCase);
}

internal sealed class BalanceLogWindow
{
    public List<string> Lines { get; init; } = [];
    public bool MatchOver { get; init; }
}

internal static class BalanceLog
{
    // Far more than a full match, so the start of the current one is always in range.
    private const long WindowBytes = 6 * 1024 * 1024;

    /// <summary>Reads back from the end of the log to the start of the current match.</summary>
    public static BalanceLogWindow ReadCurrentMatch(string path)
    {
        using var stream = OpenShared(path);
        var start = Math.Max(0, stream.Length - WindowBytes);
        stream.Seek(start, SeekOrigin.Begin);

        using var reader = new StreamReader(stream, Encoding.Latin1);
        var lines = reader.ReadToEnd().Split('\n').ToList();
        if (start > 0 && lines.Count > 0)
        {
            lines.RemoveAt(0); // began mid-line
        }

        var init = lines.FindLastIndex(line => Payload(line).StartsWith("InitGame", StringComparison.Ordinal));
        if (init >= 0)
        {
            lines = lines.GetRange(init + 1, lines.Count - init - 1);
        }

        var over = lines.Any(line =>
        {
            var payload = Payload(line);
            return payload.StartsWith("ShutdownGame", StringComparison.Ordinal)
                   || payload.StartsWith("ExitLevel", StringComparison.Ordinal);
        });

        return new BalanceLogWindow { Lines = lines, MatchOver = over };
    }

    /// <summary>
    ///     Complete lines written after <paramref name="offset" />, and the offset to
    ///     resume from. Latin-1 keeps characters and bytes one-to-one, so the
    ///     offset stays exact.
    /// </summary>
    public static (List<string> Lines, long Offset) ReadFrom(string path, long offset)
    {
        using var stream = OpenShared(path);
        if (stream.Length < offset)
        {
            return ([], stream.Length); // truncated or rotated underneath us
        }

        stream.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.Latin1);
        var text = reader.ReadToEnd();

        var lastNewline = text.LastIndexOf('\n');
        if (lastNewline < 0)
        {
            return ([], offset);
        }

        return (text[..lastNewline].Split('\n').ToList(), offset + lastNewline + 1);
    }

    public static long Length(string path)
    {
        using var stream = OpenShared(path);
        return stream.Length;
    }

    /// <summary>A log line without its leading timestamp, whatever the engine's format.</summary>
    public static string Payload(string line)
    {
        var trimmed = line.TrimStart().TrimEnd('\r');
        var space = trimmed.IndexOf(' ');
        return space < 0 ? trimmed : trimmed[(space + 1)..];
    }

    /// <summary>
    ///     Replays the current match into a line-up. Join and quit lines open and
    ///     close a slot; every kill or damage line refreshes both players' teams,
    ///     and kills and deaths are tallied from kill lines.
    /// </summary>
    public static Dictionary<int, BalanceSlot> BuildRoster(IEnumerable<string> lines)
    {
        var roster = new Dictionary<int, BalanceSlot>();

        foreach (var line in lines)
        {
            var parts = Payload(line).Split(';');
            if (parts.Length < 4)
            {
                continue;
            }

            switch (parts[0])
            {
                case "J" when TrySlot(parts[2], out var joined):
                    roster[joined] = new BalanceSlot { Slot = joined, Guid = parts[1], Name = Clean(parts[3]) };
                    break;

                case "Q" when TrySlot(parts[2], out var left):
                    roster.Remove(left);
                    break;

                case "JT" when parts.Length >= 5 && TrySlot(parts[2], out var switched):
                    Observe(roster, switched, parts[1], parts[4], parts[3]);
                    break;

                case "K" or "D" when parts.Length >= 9:
                    var victim = TrySlot(parts[2], out var victimSlot)
                        ? Observe(roster, victimSlot, parts[1], parts[4], parts[3])
                        : null;
                    var attacker = TrySlot(parts[6], out var attackerSlot)
                        ? Observe(roster, attackerSlot, parts[5], parts[8], parts[7])
                        : null;

                    if (parts[0] == "K")
                    {
                        if (victim is not null)
                        {
                            victim.Deaths++;
                        }

                        // A suicide names the same slot twice and earns no kill.
                        if (attacker is not null && attacker != victim)
                        {
                            attacker.Kills++;
                        }
                    }

                    break;
            }
        }

        return roster;
    }

    private static BalanceSlot? Observe(Dictionary<int, BalanceSlot> roster, int slot, string guid, string name,
        string team)
    {
        if (!roster.TryGetValue(slot, out var entry) || entry.Guid != guid)
        {
            // Seen before its join line (joined before the window began) or the
            // slot changed hands without one: start fresh for this occupant.
            entry = new BalanceSlot { Slot = slot, Guid = guid, Name = Clean(name) };
            roster[slot] = entry;
        }

        var normalised = team.Trim().ToLowerInvariant();
        if (normalised is "allies" or "axis")
        {
            entry.Team = normalised;
        }

        return entry;
    }

    private static bool TrySlot(string text, out int slot) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out slot) && slot >= 0;

    private static string Clean(string name)
    {
        var builder = new StringBuilder(name.Length);
        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] == '^' && i + 1 < name.Length && char.IsDigit(name[i + 1]))
            {
                i++; // colour code
                continue;
            }

            builder.Append(name[i]);
        }

        return builder.ToString().Trim();
    }

    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}

internal sealed class BalanceRated
{
    public int Slot { get; init; }
    public string Name { get; init; } = "";
    public bool IsBot { get; init; }
    public string Team { get; init; } = "";
    public double Skill { get; init; }
}

internal sealed record BalanceMove(int Slot, string Name, bool IsBot, string From, string To);

internal sealed class BalancePlan
{
    public List<BalanceMove> Moves { get; } = [];
    public double AlliesBefore { get; set; }
    public double AxisBefore { get; set; }
    public double AlliesAfter { get; set; }
    public double AxisAfter { get; set; }
    public int AlliesCountBefore { get; set; }
    public int AxisCountBefore { get; set; }
    public int AlliesCountAfter { get; set; }
    public int AxisCountAfter { get; set; }
}

/// <summary>
///     Chooses moves that even out team strength, keeping the head count within
///     one. Strength is a smoothed K/D, which puts bots and humans on the same
///     scale.
///     <para>
///         Bots are used first and humans last, as two separate passes rather
///         than a weighting: a weighting still lets one strong player look like
///         the "best single move", when a couple of bot swaps would even things
///         out and leave every human where they are. So the bots-only pass runs
///         to completion, and humans are only considered for whatever gap the
///         bots could not close.
///     </para>
/// </summary>
internal static class TeamBalancer
{
    private const double BotMoveCost = 0.05;
    private const double HumanMoveCost = 0.40;

    // An improvement smaller than about a fifth of an average player is not
    // worth disturbing anyone for.
    private const double MinImprovement = 0.20;

    private const int MaxBotSteps = 6;
    private const int MaxHumanSteps = 2;

    // How many deaths' worth of evidence the prior is worth. Early in a match a
    // player is rated almost entirely on history; as they play, this match
    // counts for more.
    public const int PriorWeight = 10;

    public static double Smoothed(int kills, int deaths, double prior) =>
        Math.Clamp((kills + prior * PriorWeight) / (deaths + (double)PriorWeight), 0.1, 10);

    public static BalancePlan Plan(IReadOnlyList<BalanceRated> players)
    {
        var allies = players.Where(player => player.Team == "allies").ToList();
        var axis = players.Where(player => player.Team == "axis").ToList();
        var moved = new HashSet<int>();

        var plan = new BalancePlan
        {
            AlliesBefore = allies.Sum(player => player.Skill),
            AxisBefore = axis.Sum(player => player.Skill),
            AlliesCountBefore = allies.Count,
            AxisCountBefore = axis.Count
        };

        double Gap() => allies.Sum(player => player.Skill) - axis.Sum(player => player.Skill);
        double Cost(BalanceRated player) => player.IsBot ? BotMoveCost : HumanMoveCost;
        bool Free(BalanceRated player) => !moved.Contains(player.Slot);

        void Move(BalanceRated player, List<BalanceRated> from, List<BalanceRated> to, string toTeam)
        {
            from.Remove(player);
            to.Add(player);
            moved.Add(player.Slot);
            plan.Moves.Add(new BalanceMove(player.Slot, player.Name, player.IsBot, player.Team, toTeam));
        }

        // Gives one player from a side that is two or more up. A bot goes if the
        // side has one; a human only when it is all humans.
        BalanceRated? PickGiveaway(List<BalanceRated> side, double sign)
        {
            var gap = Gap();
            var pool = side.Where(player => Free(player) && player.IsBot).ToList();
            if (pool.Count == 0)
            {
                pool = side.Where(Free).ToList();
            }

            return pool.MinBy(player => Math.Abs(gap - sign * 2 * player.Skill));
        }

        // 1. Head count first.
        while (allies.Count - axis.Count >= 2 && PickGiveaway(allies, 1) is { } fromAllies)
        {
            Move(fromAllies, allies, axis, "axis");
        }

        while (axis.Count - allies.Count >= 2 && PickGiveaway(axis, -1) is { } fromAxis)
        {
            Move(fromAxis, axis, allies, "allies");
        }

        // 2. Even out strength with bots alone, then with humans only if the
        //    bots could not get close enough.
        Improve(allowHumans: false, MaxBotSteps);
        Improve(allowHumans: true, MaxHumanSteps);

        plan.AlliesAfter = allies.Sum(player => player.Skill);
        plan.AxisAfter = axis.Sum(player => player.Skill);
        plan.AlliesCountAfter = allies.Count;
        plan.AxisCountAfter = axis.Count;
        return plan;

        // One improvement per step - a single move where the head count allows
        // it, or a one-for-one swap - kept only if it is worth the disruption.
        void Improve(bool allowHumans, int maxSteps)
        {
            bool Eligible(BalanceRated player) => Free(player) && (allowHumans || player.IsBot);

            for (var step = 0; step < maxSteps; step++)
            {
                var gap = Gap();
                var baseline = Math.Abs(gap);
                var bestScore = double.MaxValue;
                Action? apply = null;

                if (Math.Abs(allies.Count - 1 - (axis.Count + 1)) <= 1)
                {
                    foreach (var player in allies.Where(Eligible))
                    {
                        var score = Math.Abs(gap - 2 * player.Skill) + Cost(player);
                        if (score < bestScore)
                        {
                            bestScore = score;
                            var chosen = player;
                            apply = () => Move(chosen, allies, axis, "axis");
                        }
                    }
                }

                if (Math.Abs(axis.Count - 1 - (allies.Count + 1)) <= 1)
                {
                    foreach (var player in axis.Where(Eligible))
                    {
                        var score = Math.Abs(gap + 2 * player.Skill) + Cost(player);
                        if (score < bestScore)
                        {
                            bestScore = score;
                            var chosen = player;
                            apply = () => Move(chosen, axis, allies, "allies");
                        }
                    }
                }

                foreach (var fromAllies in allies.Where(Eligible))
                {
                    foreach (var fromAxis in axis.Where(Eligible))
                    {
                        var score = Math.Abs(gap - 2 * (fromAllies.Skill - fromAxis.Skill))
                                    + Cost(fromAllies) + Cost(fromAxis);
                        if (score < bestScore)
                        {
                            bestScore = score;
                            var a = fromAllies;
                            var b = fromAxis;
                            apply = () =>
                            {
                                Move(a, allies, axis, "axis");
                                Move(b, axis, allies, "allies");
                            };
                        }
                    }
                }

                if (apply is null || baseline - bestScore < MinImprovement)
                {
                    return;
                }

                apply();
            }
        }
    }
}

#endregion

#region Team balance command

/// <summary>
///     !balance - evens out the teams by skill, moving bots before players.
///     "!balance preview" shows the plan without moving anyone.
///     <para>
///         The line-up comes from the game log rather than IW4MAdmin's client list,
///         because IgnoreBots keeps bots out of that list. Moves go through the
///         same in-game SwitchTeams handler as !switchteam, addressed by slot, which
///         is what lets a bot be moved at all.
///     </para>
/// </summary>
public class BalanceTeamsCommand : GameInterfaceCommand
{
    // Modes where there are no teams to balance.
    private static readonly HashSet<string> TeamlessGametypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "dm", "ffa", "gun", "oic", "shrp", "sas", "infect",
        "zclassic", "zstandard", "zcleansed", "zgrief", "zom", "cmp"
    };

    // The in-game handler waits two seconds before switching; this leaves room.
    private static readonly TimeSpan SwitchDelay = TimeSpan.FromSeconds(3.5);
    private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(40);
    private const int MaxResends = 2;

    // Below this many deaths, a lifetime K/D says more about luck than skill.
    private const int MinLifetimeDeaths = 20;

    private readonly GameInterfaceState _state;
    private readonly IDatabaseContextFactory _contextFactory;
    private readonly ILogger<BalanceTeamsCommand> _logger;

    public BalanceTeamsCommand(CommandConfiguration config, ITranslationLookup translationLookup,
        GameInterfaceState state, IDatabaseContextFactory contextFactory, ILogger<BalanceTeamsCommand> logger)
        : base(config, translationLookup, state)
    {
        _state = state;
        _contextFactory = contextFactory;
        _logger = logger;

        Name = "balance";
        Description = "evens out the teams by skill, moving bots before players (add 'preview' to see the plan)";
        Alias = "bal";
        Permission = Data.Models.Client.EFClient.Permission.Administrator;
        RequiresTarget = false;
        Arguments = [new CommandArgument { Name = "preview", Required = false }];
        // IW4x's integration script has no team-switch handler, so it is left out.
        SupportedGames = [Game.IW5, Game.T4, Game.T6];
    }

    public override async Task ExecuteAsync(GameEvent gameEvent)
    {
        if (!ValidateEnabled(gameEvent) || gameEvent.Owner is not Server server)
        {
            return;
        }

        var origin = gameEvent.Origin;

        if (TeamlessGametypes.Contains(server.Gametype ?? ""))
        {
            origin.Tell($"{server.Gametype} has no teams to balance");
            return;
        }

        if (string.IsNullOrEmpty(server.LogPath))
        {
            origin.Tell("This server has no game log configured, so its teams can't be read");
            return;
        }

        var preview = string.Equals(gameEvent.Data?.Trim(), "preview", StringComparison.OrdinalIgnoreCase);

        BalanceLogWindow window;
        try
        {
            window = BalanceLog.ReadCurrentMatch(server.LogPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Balance] could not read game log {Path}", server.LogPath);
            origin.Tell("Couldn't read this server's game log");
            return;
        }

        if (window.MatchOver)
        {
            origin.Tell("The match is over - try again once the next one starts");
            return;
        }

        var roster = BalanceLog.BuildRoster(window.Lines);
        var placed = roster.Values.Where(slot => slot.Team is "allies" or "axis").ToList();
        var unplaced = roster.Count - placed.Count;

        if (placed.Count < 3 || placed.All(slot => slot.Team == "allies") || placed.All(slot => slot.Team == "axis"))
        {
            origin.Tell("Not enough players on both teams to balance yet");
            return;
        }

        // A team is only known once a player has been in a fight, so straight
        // after a map starts there is too little to go on.
        if (unplaced > placed.Count / 2)
        {
            origin.Tell($"{unplaced} players haven't been in a fight yet this match - try again in a minute");
            return;
        }

        var rated = await RateAsync(server, placed);
        var plan = TeamBalancer.Plan(rated);

        if (plan.Moves.Count == 0)
        {
            origin.Tell("Teams are already as even as they can get: " +
                        Split(plan.AlliesBefore, plan.AxisBefore, plan.AlliesCountBefore, plan.AxisCountBefore));
            return;
        }

        var report = new[]
        {
            "Now:   " + Split(plan.AlliesBefore, plan.AxisBefore, plan.AlliesCountBefore, plan.AxisCountBefore),
            (preview ? "Would move: " : "Moving: ") + Describe(plan.Moves),
            "After: " + Split(plan.AlliesAfter, plan.AxisAfter, plan.AlliesCountAfter, plan.AxisCountAfter)
        };

        if (preview)
        {
            origin.Tell(report);
            return;
        }

        foreach (var move in plan.Moves)
        {
            QueueSwitch(server, move.Slot);
        }

        server.Broadcast("^5Balance^7: teams evened out - moved " + Describe(plan.Moves));
        origin.Tell(report);
        _logger.LogInformation("[Balance] {Admin} balanced {Server}: {Moves}",
            origin.Name, server.Id, string.Join(", ", plan.Moves.Select(move => $"{move.Name}->{move.To}")));

        // Confirmation reads the log for up to 40 seconds, so it runs on its own
        // rather than holding up this server's event processing.
        _ = Task.Run(() => ConfirmAsync(server, origin, plan.Moves));
    }

    private async Task<List<BalanceRated>> RateAsync(Server server, List<BalanceSlot> placed)
    {
        var clientsBySlot = server.GetClientsAsList()
            .Where(client => client is not null && client.ClientNumber >= 0)
            .GroupBy(client => client.ClientNumber)
            .ToDictionary(group => group.Key, group => group.First());

        var humanIds = placed
            .Where(slot => !slot.IsBot && clientsBySlot.ContainsKey(slot.Slot))
            .Select(slot => clientsBySlot[slot.Slot].ClientId)
            .Distinct()
            .ToList();

        var lifetime = new Dictionary<int, double>();
        if (humanIds.Count > 0)
        {
            try
            {
                await using var context = _contextFactory.CreateContext(false);
                var serverId = server.LegacyDatabaseId;
                var rows = await context.ClientStatistics
                    .Where(stat => stat.ServerId == serverId && humanIds.Contains(stat.ClientId))
                    .Select(stat => new { stat.ClientId, stat.Kills, stat.Deaths })
                    .ToListAsync();

                foreach (var row in rows.Where(row => row.Deaths >= MinLifetimeDeaths))
                {
                    lifetime[row.ClientId] = Math.Clamp(row.Kills / (double)row.Deaths, 0.1, 10);
                }
            }
            catch (Exception ex)
            {
                // Without history everyone starts even and this match decides.
                _logger.LogWarning(ex, "[Balance] could not load lifetime stats");
            }
        }

        // A human with no history is assumed typical of the humans who have one.
        var humanDefault = Median(lifetime.Values) ?? 1.0;

        // Bots on a server share a difficulty, so they are rated from the bots'
        // pooled record this match rather than each on their own few kills.
        var bots = placed.Where(slot => slot.IsBot).ToList();
        var botPrior = TeamBalancer.Smoothed(bots.Sum(bot => bot.Kills), bots.Sum(bot => bot.Deaths), 1.0);

        return placed.Select(slot =>
        {
            var prior = slot.IsBot
                ? botPrior
                : clientsBySlot.TryGetValue(slot.Slot, out var client) &&
                  lifetime.TryGetValue(client.ClientId, out var kd)
                    ? kd
                    : humanDefault;

            return new BalanceRated
            {
                Slot = slot.Slot,
                Name = slot.Name,
                IsBot = slot.IsBot,
                Team = slot.Team,
                Skill = TeamBalancer.Smoothed(slot.Kills, slot.Deaths, prior)
            };
        }).ToList();
    }

    private void QueueSwitch(Server server, int slot)
    {
        if (_state.GetServerState(server.Id) is not { Enabled: true } serverState)
        {
            return;
        }

        // No in-game origin, so the game doesn't echo every switch back to the
        // admin one bold line at a time; the command reports the balance instead.
        serverState.CommandQueue.Enqueue(GameInterfacePlugin.FormatEventMessage(
            false, "ExecuteCommandRequested", "SwitchTeams", -1, slot, null));
    }

    /// <summary>
    ///     Watches the log to confirm each move landed. The in-game switch refuses a
    ///     player who is dead at that moment, and switching toggles rather than
    ///     sets a team, so a blind retry could send someone straight back. A move is
    ///     only re-sent once the player shows up alive on their old team.
    /// </summary>
    private async Task ConfirmAsync(Server server, EFClient origin, IReadOnlyList<BalanceMove> moves)
    {
        try
        {
            var pending = moves.ToDictionary(move => move.Slot, move => new PendingSwitch(move));
            var confirmed = 0;
            var left = 0;

            await Task.Delay(SwitchDelay);
            var offset = BalanceLog.Length(server.LogPath);
            var lastRead = DateTime.UtcNow;
            var deadline = DateTime.UtcNow + ConfirmWindow;

            while (pending.Count > 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2.5));

                var batchStarted = lastRead;
                (var lines, offset) = BalanceLog.ReadFrom(server.LogPath, offset);
                lastRead = DateTime.UtcNow;

                foreach (var line in lines)
                {
                    var parts = BalanceLog.Payload(line).Split(';');
                    if (parts.Length < 3)
                    {
                        continue;
                    }

                    // The slot changed hands, or its player left.
                    if (parts[0] is "J" or "Q")
                    {
                        if (int.TryParse(parts[2], out var gone) && pending.Remove(gone))
                        {
                            left++;
                        }

                        continue;
                    }

                    if (parts[0] is not ("K" or "D") || parts.Length < 9)
                    {
                        continue;
                    }

                    foreach (var (slotText, teamText) in new[] { (parts[2], parts[3]), (parts[6], parts[7]) })
                    {
                        if (!int.TryParse(slotText, out var slot) || !pending.TryGetValue(slot, out var item))
                        {
                            continue;
                        }

                        var team = teamText.Trim().ToLowerInvariant();
                        if (team == item.Move.To)
                        {
                            pending.Remove(slot);
                            confirmed++;
                        }
                        else if (team == item.Move.From && batchStarted >= item.RetryAfter &&
                                 item.Resends < MaxResends)
                        {
                            item.Resends++;
                            item.RetryAfter = DateTime.UtcNow + SwitchDelay;
                            QueueSwitch(server, slot);
                        }
                    }
                }
            }

            var attempted = moves.Count - left;
            var summary = pending.Count == 0
                ? $"Balance confirmed: all {attempted} moves landed"
                : $"Balance: {confirmed} of {attempted} moves confirmed, {pending.Count} not seen in a fight yet";
            if (left > 0)
            {
                summary += $" ({left} left the server)";
            }

            origin.Tell(summary);
            _logger.LogInformation("[Balance] {Summary} on {Server}", summary, server.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Balance] confirming moves failed on {Server}", server.Id);
        }
    }

    private static string Describe(IReadOnlyList<BalanceMove> moves)
    {
        var parts = moves.Where(move => !move.IsBot)
            .Select(move => $"{Shorten(move.Name)} to {Label(move.To)}")
            .ToList();

        var bots = moves.Count(move => move.IsBot);
        if (bots > 0)
        {
            parts.Add($"{bots} bot{(bots == 1 ? "" : "s")}");
        }

        return string.Join(", ", parts);
    }

    private static string Split(double allies, double axis, int alliesCount, int axisCount)
    {
        var total = allies + axis;
        var alliesShare = total <= 0 ? 50 : (int)Math.Round(100 * allies / total);
        return $"Allies {alliesShare}% ({alliesCount}) v Axis {100 - alliesShare}% ({axisCount})";
    }

    private static string Label(string team) => team == "allies" ? "Allies" : "Axis";

    private static string Shorten(string name) => name.Length <= 16 ? name : name[..16];

    private static double? Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(value => value).ToList();
        if (sorted.Count == 0)
        {
            return null;
        }

        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private sealed class PendingSwitch(BalanceMove move)
    {
        public BalanceMove Move { get; } = move;
        public int Resends { get; set; }
        public DateTime RetryAfter { get; set; } = DateTime.MinValue;
    }
}

#endregion
