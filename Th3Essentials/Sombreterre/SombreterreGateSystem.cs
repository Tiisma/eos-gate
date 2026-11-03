using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Th3Essentials.Discord;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace Th3Essentials.Sombreterre;

/// <summary>
/// The Sombreterre gate: only players who launched through the Sombreterre launcher, with a Discord
/// account that is on the server's guild and holds the whitelist role, may stay connected.
///
/// Each join must present a fresh launch ticket (see <see cref="TicketBook"/>), sent by the
/// SombreterreGate.Client mod. The first valid ticket links the game account to the Discord account
/// that asked for it, so players never type a command. Losing the role, or leaving the guild, kicks
/// a linked player who is online.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class SombreterreGateSystem : ModSystem, GateHttpApi.IBackend
{
    internal const string ConfigFile = "SombreterreGate.json";
    private const string StoreFile = "SombreterreGate-data.json";

    private ICoreServerAPI _sapi = null!;
    private GateConfig _config = null!;
    private GateStore _store = null!;
    private readonly TicketBook _tickets = new();
    private GateHttpApi? _api;
    private Th3Discord? _discord;
    private ulong _guildId;

    private PackBuilder.Result _pack = new();
    private readonly object _packLock = new();

    /// <summary>Players admitted this session, by UID. Cleared on disconnect, so every join is checked again.</summary>
    private readonly ConcurrentDictionary<string, bool> _admitted = new();

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    /// <summary>Before Th3Essentials, so the bot is created with the member intents the gate needs.</summary>
    public override double ExecuteOrder() => -0.1;

    public override void StartServerSide(ICoreServerAPI sapi)
    {
        _sapi = sapi;
        try
        {
            _config = sapi.LoadModConfig<GateConfig>(ConfigFile) ?? new GateConfig();
            // Newtonsoft appends a loaded list to the field's default instead of replacing it, so
            // defaults would pile up on every start. Duplicates carry no meaning in these lists.
            _config.AdditionalPackFolders = _config.AdditionalPackFolders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _config.BypassPlayerUids = _config.BypassPlayerUids.Distinct().ToList();
            _config.ModDbModIds = _config.ModDbModIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            sapi.StoreModConfig(_config, ConfigFile);
        }
        catch (Exception e)
        {
            sapi.Logger.Error($"[SombreterreGate] Could not read {ConfigFile}, gate disabled: {e.Message}");
            return;
        }

        if (!_config.Enabled)
        {
            sapi.Logger.Notification($"[SombreterreGate] Disabled. Set Enabled in ModConfig/{ConfigFile} to turn it on.");
            return;
        }

        Th3Discord.GateRequiresMembers = true;
        _store = new GateStore(Path.Combine(GamePaths.DataPath, "ModData", StoreFile));
        Directory.CreateDirectory(Path.Combine(GamePaths.DataPath, "ModData"));
        RebuildPack();

        sapi.Network.RegisterChannel(GateTicketPacket.Channel)
            .RegisterMessageType<GateTicketPacket>()
            .SetMessageHandler<GateTicketPacket>(OnTicket);

        sapi.Event.PlayerNowPlaying += OnPlayerNowPlaying;
        sapi.Event.PlayerDisconnect += player => _admitted.TryRemove(player.PlayerUID, out _);

        var th3 = sapi.ModLoader.GetModSystem<Th3Essentials>();
        th3.OnTh3DiscordReady += OnDiscordReady;

        sapi.ChatCommands.Create("gate")
            .WithDescription("Sombreterre gate administration")
            .RequiresPrivilege(Privilege.controlserver)
            .BeginSubCommand("reloadpack")
                .WithDescription("Rebuild the launcher pack from the mod folders")
                .HandleWith(_ =>
                {
                    RebuildPack();
                    return TextCommandResult.Success($"Pack rebuilt: {_pack.PrivateFiles.Count} private mod(s), {_pack.Skipped.Count} skipped (see server log).");
                })
            .EndSubCommand()
            .BeginSubCommand("unlink")
                .WithDescription("Remove the Discord link of a player UID")
                .WithArgs(sapi.ChatCommands.Parsers.Word("playeruid"))
                .HandleWith(args => _store.Unlink((string)args[0]) ? TextCommandResult.Success("Unlinked.") : TextCommandResult.Error("No link for that UID."))
            .EndSubCommand();

        try
        {
            _api = new GateHttpApi(_config, _store, _tickets, this);
            _api.Start();
            sapi.Logger.Notification($"[SombreterreGate] Launcher API listening on {_config.HttpPrefix}");
        }
        catch (Exception e)
        {
            sapi.Logger.Error($"[SombreterreGate] Launcher API could not start on {_config.HttpPrefix}: {e.Message}");
        }
    }

    private void OnDiscordReady(Th3Discord discord)
    {
        _discord = discord;
        _guildId = Th3Essentials.Config.DiscordConfig?.GuildId ?? 0;
        discord.Client.GuildMemberUpdated += (_, after) => OnMemberChanged(after.Id);
        discord.Client.UserLeft += (_, user) => OnMemberChanged(user.Id);
        discord.Client.UserBanned += (user, _) => OnMemberChanged(user.Id);
    }

    private Task OnMemberChanged(ulong discordId)
    {
        if (Membership(discordId) == MembershipStatus.Whitelisted) return Task.CompletedTask;
        var uid = _store.PlayerOf(discordId);
        if (uid == null) return Task.CompletedTask;

        _sapi.Event.EnqueueMainThreadTask(() =>
        {
            var player = _sapi.World.PlayerByUid(uid) as IServerPlayer;
            if (player?.ConnectionState is EnumClientState.Connected or EnumClientState.Playing)
                player.Disconnect(Lang.Get("th3essentials:gate-kick-role-lost"));
        }, "sombreterregate-kick");
        return Task.CompletedTask;
    }

    private bool Bypassed(IServerPlayer player) => _config.BypassPlayerUids.Contains(player.PlayerUID);

    private void OnTicket(IServerPlayer player, GateTicketPacket packet)
    {
        if (Bypassed(player) || _admitted.ContainsKey(player.PlayerUID)) return;

        var outcome = GateDecision.Evaluate(player.PlayerUID, packet.Ticket ?? "", _store, _tickets, Membership, DateTime.UtcNow);
        if (outcome == GateOutcome.Admitted)
        {
            _admitted[player.PlayerUID] = true;
            _sapi.Logger.Notification($"[SombreterreGate] Ticket accepted for {player.PlayerName}.");
            return;
        }

        _sapi.Logger.Notification($"[SombreterreGate] Refused {player.PlayerName}: {outcome}.");
        player.Disconnect(RefusalMessage(outcome));
    }

    private void OnPlayerNowPlaying(IServerPlayer player)
    {
        if (Bypassed(player) || _admitted.ContainsKey(player.PlayerUID)) return;

        // The ticket can still be on its way: the client sends it once its connection is ready.
        _sapi.Logger.Notification($"[SombreterreGate] Waiting up to {_config.TicketGraceSeconds}s for {player.PlayerName}'s launch ticket.");
        _sapi.Event.RegisterCallback(_ =>
        {
            if (_admitted.ContainsKey(player.PlayerUID)) return;
            if (player.ConnectionState is EnumClientState.Connected or EnumClientState.Playing)
            {
                _sapi.Logger.Notification($"[SombreterreGate] Refused {player.PlayerName}: no ticket received within {_config.TicketGraceSeconds}s. Their client-main.log says why (lines starting with [SombreterreGate]).");
                player.Disconnect(Lang.Get("th3essentials:gate-kick-no-ticket"));
            }
        }, _config.TicketGraceSeconds * 1000);
    }

    private static string RefusalMessage(GateOutcome outcome) => outcome switch
    {
        GateOutcome.InvalidTicket => Lang.Get("th3essentials:gate-kick-no-ticket"),
        GateOutcome.PlayerLinkedElsewhere => Lang.Get("th3essentials:gate-kick-player-linked"),
        GateOutcome.DiscordLinkedElsewhere => Lang.Get("th3essentials:gate-kick-discord-linked"),
        GateOutcome.NotMember => Lang.Get("th3essentials:gate-kick-not-member"),
        GateOutcome.NotWhitelisted => Lang.Get("th3essentials:gate-kick-not-whitelisted"),
        _ => Lang.Get("th3essentials:gate-kick-discord-unavailable")
    };

    private void RebuildPack()
    {
        var folders = new[] { Path.Combine(GamePaths.DataPath, "Mods") }
            .Concat(_config.AdditionalPackFolders.Select(f => Path.IsPathRooted(f) ? f : Path.Combine(GamePaths.DataPath, f)))
            .ToList();
        var pack = PackBuilder.Build(_config.PackName, GameVersion.ShortGameVersion, folders, _config.ModDbModIds);
        foreach (var skipped in pack.Skipped) _sapi.Logger.Notification($"[SombreterreGate] Pack: skipped {skipped}");
        lock (_packLock) _pack = pack;
    }

    // --- GateHttpApi.IBackend -------------------------------------------------------------

    public MembershipStatus Membership(ulong discordId)
    {
        var client = _discord?.Client;
        if (client == null || client.ConnectionState != ConnectionState.Connected) return MembershipStatus.Unknown;
        var guild = client.GetGuild(_guildId);
        if (guild == null) return MembershipStatus.Unknown;
        var user = guild.GetUser(discordId);
        if (user == null) return MembershipStatus.NotMember;
        return user.Roles.Any(role => role.Id == _config.WhitelistRoleId) ? MembershipStatus.Whitelisted : MembershipStatus.MemberWithoutRole;
    }

    public (int Online, int Max) Status()
    {
        var online = _sapi.Server.Players.Count(player => player.ConnectionState == EnumClientState.Playing);
        return (online, _sapi.Server.Config.MaxClients);
    }

    public PackBuilder.Result Pack()
    {
        lock (_packLock) return _pack;
    }

    public void Log(string message) => _sapi.Logger.Warning($"[SombreterreGate] {message}");

    public override void Dispose()
    {
        _api?.Dispose();
        base.Dispose();
    }
}
