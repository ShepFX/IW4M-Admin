#:package RaidMax.IW4MAdmin.SharedLibraryCore@2026.1.6.1

using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharedLibraryCore;
using SharedLibraryCore.Commands;
using SharedLibraryCore.Configuration;
using SharedLibraryCore.Database.Models;
using SharedLibraryCore.Interfaces;

/// <summary>
/// Discord Link - !discord (alias !dc) gives a player a one-time code that links their Discord
/// account to their game account: they type /link CODE in the CUKServers Discord and the server bot
/// redeems it. A linked player gets a WaW, BO1 or BO2 Record Holder role for every game they hold a
/// #1 high round in.
///
/// The code comes from the stats site (highscores-flask), which stores the links. Typing the code in
/// Discord is what proves the Discord account belongs to whoever is in game on this account, so nobody
/// can claim someone else's records.
/// </summary>
public class DiscordLinkPlugin : IPluginV2
{
    public string Name => "Discord Link";
    public string Author => "CUKServers";
    public string Version => "1.0";

    public DiscordLinkPlugin(ILogger<DiscordLinkPlugin> logger)
    {
        logger.LogInformation("Discord Link {Version} loaded", Version);
    }
}

public class DiscordLinkCommand : Command
{
    private const string CodeUrl = "http://highscores-flask:5000/api/discord/code";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly ILogger<DiscordLinkCommand> _logger;

    public DiscordLinkCommand(CommandConfiguration config, ITranslationLookup translationLookup,
        ILogger<DiscordLinkCommand> logger) : base(config, translationLookup)
    {
        Name = "discord";
        Description = "get a code to link your Discord account for record holder roles";
        Alias = "dc";
        Permission = EFClient.Permission.User;
        RequiresTarget = false;
        _logger = logger;
    }

    public override async Task ExecuteAsync(GameEvent gameEvent)
    {
        var origin = gameEvent.Origin;
        // Client 1 is the IW4MAdmin console, which has no game account to link.
        if (origin is null || origin.ClientId <= 1)
        {
            return;
        }

        var token = gameEvent.Owner?.Manager?.CancellationToken ?? CancellationToken.None;

        try
        {
            using var response = await Http.PostAsJsonAsync(CodeUrl, new { client_id = origin.ClientId }, token);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<CodeResponse>(cancellationToken: token);
            if (string.IsNullOrWhiteSpace(result?.Code))
            {
                throw new InvalidOperationException("the stats site returned no code");
            }

            var minutes = Math.Max(1, result.ExpiresIn / 60);
            origin.Tell($"^7Your Discord link code: ^5{result.Code} ^7(valid {minutes} minutes)");
            origin.Tell($"^7In the CUKServers Discord, type ^5/link {result.Code}");
            if (result.AlreadyLinked)
            {
                origin.Tell("^7Linking again replaces the Discord account you linked before.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discord Link: no code for client {ClientId}", origin.ClientId);
            origin.Tell("^1Couldn't get a Discord code right now. ^7Try again in a minute.");
        }
    }

    private sealed class CodeResponse
    {
        [JsonPropertyName("code")]
        public string? Code { get; init; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }

        [JsonPropertyName("already_linked")]
        public bool AlreadyLinked { get; init; }
    }
}
