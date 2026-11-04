using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Th3Essentials.Discord;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Server;

namespace Th3Essentials.Sombreterre;

/// <summary>
/// The Sombreterre gate: only players who launched through the Sombreterre launcher, with a Discord
/// account that is on the server's guild and holds the whitelist role, may stay connected.
///
/// Each join must present a fresh launch ticket (see <see cref="TicketBook"/>), sent by the
/// SombreterreGate.Client mod. The first valid ticket links the game account to the Discord account
/// that asked for it, so players never type a command. Until the ticket is accepted the player is
/// held on the spot in spectator mode with their chat swallowed, so the wait cannot be used to play.
/// Losing the role, or leaving the guild, kicks a linked player who is online.
///
/// The server's own whitelist follows the same role (AutoWhitelist): the launcher names its game
/// account when it asks for a ticket, so a player with the role is on the list before the game starts.
///
/// Every security-relevant event (links, first joins, refusals, revocations) goes to the server log
/// and, when Th3Essentials has an admin log channel, to Discord.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class SombreterreGateSystem : ModSystem, GateHttpApi.IBackend
{
    internal const string ConfigFile = "SombreterreGate.json";
    private const string StoreFile = "SombreterreGate-data.json";

    /// <summary>Where a frozen player's own game mode waits for their admission, in their server data.</summary>
    private const string SavedModeKey = "sombreterregate-mode";

    /// <summary>Where a frozen player's position waits for their admission, as "x;y;z".</summary>
    private const string SavedPositionKey = "sombreterregate-pos";

    /// <summary>Where a frozen player's own move speed waits for their admission.</summary>
    private const string SavedSpeedKey = "sombreterregate-speed";

    private const int HoldCheckIntervalMs = 250;

    /// <summary>How far a frozen player may be from their spot before being put back, in blocks.</summary>
    private const double MaxHeldDistance = 0.5;

    private ICoreServerAPI _sapi = null!;
    private GateConfig _config = null!;
    private GateStore _store = null!;
    private readonly TicketBook _tickets = new();
    private GateHttpApi? _api;
    private Th3Discord? _discord;
    private ulong _guildId;
    private volatile bool _lockedDown;

    private PackBuilder.Result _pack = new();
    private readonly object _packLock = new();

    /// <summary>Players admitted this session, by UID. Cleared on disconnect, so every join is checked again.</summary>
    private readonly ConcurrentDictionary<string, bool> _admitted = new();

    /// <summary>Frozen players online, by UID, and the spot they are held on.</summary>
    private readonly ConcurrentDictionary<string, Vec3d> _heldAt = new();

    /// <summary>Game accounts refused recently, by UID, until when they are turned away on join.</summary>
    private readonly ConcurrentDictionary<string, DateTime> _refusedUntil = new();

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    /// <summary>Before Th3Essentials, so the bot is created with the member intents the gate needs.</summary>
    public override double ExecuteOrder() => -0.1;

    public bool LockedDown => _lockedDown;

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

        if (!string.IsNullOrWhiteSpace(_config.MinLauncherVersion) && GateHttpApi.ParseVersion(_config.MinLauncherVersion) == null)
            sapi.Logger.Error($"[SombreterreGate] MinLauncherVersion '{_config.MinLauncherVersion}' is not a version like 0.1.2; no launcher version is enforced.");
        if (_config.BypassPlayerUids.Count > 0)
            sapi.Logger.Warning($"[SombreterreGate] {_config.BypassPlayerUids.Count} player UID(s) bypass the gate (BypassPlayerUids). Keep this list short.");

        Th3Discord.GateRequiresMembers = true;
        _store = new GateStore(Path.Combine(GamePaths.DataPath, "ModData", StoreFile));
        Directory.CreateDirectory(Path.Combine(GamePaths.DataPath, "ModData"));
        RebuildPack();

        sapi.Network.RegisterChannel(GateTicketPacket.Channel)
            .RegisterMessageType<GateTicketPacket>()
            .SetMessageHandler<GateTicketPacket>(OnTicket);

        sapi.Event.PlayerNowPlaying += OnPlayerNowPlaying;
        sapi.Event.PlayerChat += OnPlayerChat;
        sapi.Event.PlayerDisconnect += player =>
        {
            _admitted.TryRemove(player.PlayerUID, out _);
            _heldAt.TryRemove(player.PlayerUID, out _);
        };
        sapi.Event.RegisterGameTickListener(_ => HoldPlayersInPlace(), HoldCheckIntervalMs);

        var th3 = sapi.ModLoader.GetModSystem<Th3Essentials>();
        th3.OnTh3DiscordReady += OnDiscordReady;

        RegisterCommands(sapi);

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

    private void RegisterCommands(ICoreServerAPI sapi)
    {
        sapi.ChatCommands.Create("gate")
            .WithDescription("Sombreterre gate administration")
            .RequiresPrivilege(Privilege.controlserver)
            .BeginSubCommand("reloadpack")
                .WithDescription("Rebuild the launcher pack from the mod folders")
                .HandleWith(_ =>
                {
                    RebuildPack();
                    Audit($"Pack rebuilt: {_pack.PrivateFiles.Count} private mod(s), {_pack.Skipped.Count} skipped.");
                    return TextCommandResult.Success($"Pack rebuilt: {_pack.PrivateFiles.Count} private mod(s), {_pack.Skipped.Count} skipped (see server log).");
                })
            .EndSubCommand()
            .BeginSubCommand("unlink")
                .WithDescription("Remove the Discord link of a player UID and revoke that Discord account's launcher tokens")
                .WithArgs(sapi.ChatCommands.Parsers.Word("playeruid"))
                .HandleWith(args =>
                {
                    var uid = (string)args[0];
                    var discordId = _store.Unlink(uid);
                    if (discordId == null) return TextCommandResult.Error("No link for that UID.");
                    var revoked = _store.RevokeAllFor(discordId.Value);
                    Audit($"Staff unlinked player {uid} from Discord {discordId} and revoked {revoked} launcher token(s).");
                    if (_config.AutoWhitelist) RemoveFromWhitelist(((ServerMain)_sapi.World).PlayerDataManager, uid, "staff unlinked it");
                    return TextCommandResult.Success($"Unlinked; {revoked} launcher token(s) revoked.");
                })
            .EndSubCommand()
            .BeginSubCommand("revoke")
                .WithDescription("Revoke every launcher token of a Discord user ID (the player must link again)")
                .WithArgs(sapi.ChatCommands.Parsers.Word("discordid"))
                .HandleWith(args =>
                {
                    if (!ulong.TryParse((string)args[0], out var discordId)) return TextCommandResult.Error("Not a Discord user ID.");
                    var revoked = _store.RevokeAllFor(discordId);
                    Audit($"Staff revoked {revoked} launcher token(s) of Discord {discordId}.");
                    return TextCommandResult.Success($"{revoked} launcher token(s) revoked.");
                })
            .EndSubCommand()
            .BeginSubCommand("lockdown")
                .WithDescription("Stop or resume new links, tickets and pack downloads (players already in stay)")
                .WithArgs(sapi.ChatCommands.Parsers.Bool("on"))
                .HandleWith(args =>
                {
                    _lockedDown = (bool)args[0];
                    Audit(_lockedDown ? "Gate locked down by staff: no new links, tickets or downloads." : "Gate lockdown lifted by staff.");
                    return TextCommandResult.Success(_lockedDown ? "Gate locked down." : "Gate reopened.");
                })
            .EndSubCommand();
    }

    private void OnDiscordReady(Th3Discord discord)
    {
        _discord = discord;
        _guildId = Th3Essentials.Config.DiscordConfig?.GuildId ?? 0;
        discord.Client.GuildMemberUpdated += (_, after) => OnMemberChanged(after.Id);
        discord.Client.UserLeft += (_, user) => OnMemberChanged(user.Id);
        discord.Client.UserBanned += (user, _) => OnMemberChanged(user.Id);
        discord.Client.GuildMembersDownloaded += guild =>
        {
            if (guild.Id == _guildId) ReconcileWhitelist();
            return Task.CompletedTask;
        };
        // The members may already be in by the time this runs.
        if (discord.Client.GetGuild(_guildId)?.HasAllMembers == true) ReconcileWhitelist();
    }

    /// <summary>Catches up on the role changes made while the server was off.</summary>
    private void ReconcileWhitelist()
    {
        foreach (var discordId in _store.AccountHolders()) SyncWhitelist(discordId, null);
    }

    public void SyncWhitelist(ulong discordId, string? droppedUid)
    {
        if (!_config.AutoWhitelist) return;
        var membership = Membership(discordId);
        var account = _store.AccountOf(discordId);
        // The whitelist belongs to the main thread; Discord events and the launcher API do not run on it.
        _sapi.Event.EnqueueMainThreadTask(() => ApplyWhitelist(discordId, membership, account, droppedUid), "sombreterregate-whitelist");
    }

    /// <summary>
    /// An unknown membership (Discord down, members still loading) changes nothing. Players come off
    /// only by their game account, and an entry under the same name but another account is reported
    /// rather than touched, since the game would otherwise rewrite it to the account claimed here.
    /// </summary>
    public void ApplyWhitelist(ulong discordId, MembershipStatus membership, GateStore.PlayerClaim? account, string? droppedUid)
    {
        var players = ((ServerMain)_sapi.World).PlayerDataManager;
        if (droppedUid != null) RemoveFromWhitelist(players, droppedUid, $"Discord {discordId} now plays with another game account");
        if (account == null) return;

        if (membership is MembershipStatus.NotMember or MembershipStatus.MemberWithoutRole)
        {
            RemoveFromWhitelist(players, account.PlayerUid, $"Discord {discordId} no longer holds the whitelist role");
            return;
        }
        if (membership != MembershipStatus.Whitelisted) return;
        if (players.WhitelistedPlayers.Any(entry => entry.PlayerUID == account.PlayerUid)) return;

        var name = account.PlayerName.Length > 0 ? account.PlayerName : _sapi.PlayerData.GetPlayerDataByUid(account.PlayerUid)?.LastKnownPlayername;
        if (string.IsNullOrEmpty(name)) return;
        if (players.WhitelistedPlayers.Any(entry => string.Equals(entry.PlayerName, name, StringComparison.OrdinalIgnoreCase)))
        {
            Audit($"Not whitelisting {account.PlayerUid} for Discord {discordId}: the name {name} is already on the whitelist under another game account.");
            return;
        }

        players.WhitelistPlayer(name, account.PlayerUid, "Discord role", $"Discord {discordId}", DateTime.Now.AddYears(50));
        Audit($"{name} ({account.PlayerUid}) added to the whitelist: Discord {discordId} holds the role.");
    }

    private void RemoveFromWhitelist(PlayerDataManager players, string playerUid, string reason)
    {
        if (_config.BypassPlayerUids.Contains(playerUid)) return;
        var entry = players.WhitelistedPlayers.FirstOrDefault(candidate => candidate.PlayerUID == playerUid);
        if (entry == null) return;
        players.UnWhitelistPlayer(entry.PlayerName, playerUid);
        Audit($"{entry.PlayerName} ({playerUid}) removed from the whitelist: {reason}.");
    }

    private Task OnMemberChanged(ulong discordId)
    {
        SyncWhitelist(discordId, null);
        if (Membership(discordId) == MembershipStatus.Whitelisted) return Task.CompletedTask;
        var uid = _store.PlayerOf(discordId);
        if (uid == null) return Task.CompletedTask;

        _sapi.Event.EnqueueMainThreadTask(() =>
        {
            var player = _sapi.World.PlayerByUid(uid) as IServerPlayer;
            if (player?.ConnectionState is EnumClientState.Connected or EnumClientState.Playing)
            {
                Audit($"{player.PlayerName} lost the whitelist on Discord and was disconnected.");
                player.Disconnect(Lang.Get("th3essentials:gate-kick-role-lost"));
            }
        }, "sombreterregate-kick");
        return Task.CompletedTask;
    }

    private bool Bypassed(IServerPlayer player) => _config.BypassPlayerUids.Contains(player.PlayerUID);

    private bool Pending(IServerPlayer player) => !Bypassed(player) && !_admitted.ContainsKey(player.PlayerUID);

    private void OnTicket(IServerPlayer player, GateTicketPacket packet)
    {
        if (!Pending(player)) return;

        if (_lockedDown)
        {
            Refuse(player, "lockdown", Lang.Get("th3essentials:gate-kick-lockdown"));
            return;
        }

        var wasLinked = _store.LinkOf(player.PlayerUID) != null;
        var outcome = GateDecision.Evaluate(player.PlayerUID, packet.Ticket ?? "", _store, _tickets, Membership, DateTime.UtcNow, TimeSpan.FromHours(Math.Max(1, _config.TicketSessionHours)));
        if (outcome == GateOutcome.Admitted)
        {
            _admitted[player.PlayerUID] = true;
            _refusedUntil.TryRemove(player.PlayerUID, out _);
            Unfreeze(player);
            _sapi.Logger.Notification($"[SombreterreGate] Ticket accepted for {player.PlayerName}.");
            if (!wasLinked) Audit($"{player.PlayerName} ({player.PlayerUID}) joined for the first time and was linked to Discord {_store.LinkOf(player.PlayerUID)}.");
            return;
        }

        Refuse(player, outcome.ToString(), RefusalMessage(outcome));
    }

    private void OnPlayerNowPlaying(IServerPlayer player)
    {
        if (Bypassed(player))
        {
            _sapi.Logger.Notification($"[SombreterreGate] {player.PlayerName} joined through BypassPlayerUids.");
            Unfreeze(player);
            return;
        }
        if (_admitted.ContainsKey(player.PlayerUID)) return;

        if (_refusedUntil.TryGetValue(player.PlayerUID, out var until) && until > DateTime.UtcNow)
        {
            player.Disconnect(Lang.Get("th3essentials:gate-kick-cooldown", Math.Ceiling((until - DateTime.UtcNow).TotalSeconds)));
            return;
        }

        Freeze(player);

        // The ticket can still be on its way: the client sends it once its connection is ready.
        _sapi.Logger.Notification($"[SombreterreGate] Waiting up to {_config.TicketGraceSeconds}s for {player.PlayerName}'s launch ticket.");
        _sapi.Event.RegisterCallback(_ =>
        {
            if (_admitted.ContainsKey(player.PlayerUID)) return;
            if (player.ConnectionState is EnumClientState.Connected or EnumClientState.Playing)
                Refuse(player, $"no ticket within {_config.TicketGraceSeconds}s (their client-main.log says why, lines starting with [SombreterreGate])", Lang.Get("th3essentials:gate-kick-no-ticket"));
        }, _config.TicketGraceSeconds * 1000);
    }

    /// <summary>Nothing a player who has not been admitted says reaches the server, Discord or other players.</summary>
    private void OnPlayerChat(IServerPlayer player, int channelId, ref string message, ref string data, BoolRef consumed)
    {
        if (!Pending(player)) return;
        consumed.value = true;
        player.SendMessage(GlobalConstants.GeneralChatGroup, Lang.Get("th3essentials:gate-waiting"), EnumChatType.Notification);
    }

    private void Refuse(IServerPlayer player, string reason, string message)
    {
        _refusedUntil[player.PlayerUID] = DateTime.UtcNow.AddSeconds(Math.Max(0, _config.RefusalCooldownSeconds));
        Audit($"Refused {player.PlayerName} ({player.PlayerUID}): {reason}.");
        player.Disconnect(message);
    }

    /// <summary>
    /// Until admission: spectator mode, so no building, breaking, interacting or fighting, and held on
    /// the spot. Spectator alone would let the player fly through walls and be admitted wherever they
    /// ended up. A move speed of zero stops an ordinary client, and <see cref="HoldPlayersInPlace"/>
    /// puts back any other. The player's own mode and position are kept in their server data, so a
    /// crash or disconnect in between never loses them. Uses the game's own /player gamemode command,
    /// which notifies every system that tracks modes.
    /// </summary>
    private void Freeze(IServerPlayer player)
    {
        if (!_config.FreezeUntilAdmitted) return;
        player.SendMessage(GlobalConstants.GeneralChatGroup, Lang.Get("th3essentials:gate-waiting"), EnumChatType.Notification);

        var saved = player.ServerData.CustomPlayerData;
        var mode = player.WorldData.CurrentGameMode;
        if (!saved.ContainsKey(SavedModeKey)) saved[SavedModeKey] = mode.ToString();
        if (!saved.ContainsKey(SavedSpeedKey)) saved[SavedSpeedKey] = player.WorldData.MoveSpeedMultiplier.ToString(CultureInfo.InvariantCulture);
        if (!saved.TryGetValue(SavedPositionKey, out var position) || ParsePosition(position) == null)
        {
            var current = player.Entity.ServerPos;
            saved[SavedPositionKey] = string.Create(CultureInfo.InvariantCulture, $"{current.X};{current.Y};{current.Z}");
        }
        if (mode != EnumGameMode.Spectator) SetGameMode(player, EnumGameMode.Spectator);

        player.WorldData.MoveSpeedMultiplier = 0;
        player.BroadcastPlayerData();
        _heldAt[player.PlayerUID] = ParsePosition(saved[SavedPositionKey])!;
    }

    private void HoldPlayersInPlace()
    {
        foreach (var (uid, position) in _heldAt)
        {
            if (_sapi.World.PlayerByUid(uid) is IServerPlayer { Entity: not null } player) MoveBack(player, position);
        }
    }

    private static void MoveBack(IServerPlayer player, Vec3d position)
    {
        if (player.Entity.ServerPos.SquareDistanceTo(position.X, position.Y, position.Z) > MaxHeldDistance * MaxHeldDistance)
            player.Entity.TeleportToDouble(position.X, position.Y, position.Z);
    }

    private static Vec3d? ParsePosition(string text)
    {
        var parts = text.Split(';');
        if (parts.Length != 3) return null;
        var numbers = new double[3];
        for (var i = 0; i < 3; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i])) return null;
        }
        return new Vec3d(numbers[0], numbers[1], numbers[2]);
    }

    private void Unfreeze(IServerPlayer player)
    {
        _heldAt.TryRemove(player.PlayerUID, out _);
        var data = player.ServerData.CustomPlayerData;

        if (data.Remove(SavedPositionKey, out var position) && ParsePosition(position) is { } origin) MoveBack(player, origin);
        if (data.Remove(SavedModeKey, out var saved) && Enum.TryParse<EnumGameMode>(saved, out var mode) && mode != EnumGameMode.Spectator) SetGameMode(player, mode);

        if (data.Remove(SavedSpeedKey, out var speedText))
        {
            player.WorldData.MoveSpeedMultiplier = float.TryParse(speedText, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) && speed > 0 ? speed : 1;
            player.BroadcastPlayerData();
        }
    }

    private void SetGameMode(IServerPlayer player, EnumGameMode mode)
    {
        var args = new TextCommandCallingArgs
        {
            Caller = new Caller
            {
                Type = EnumCallerType.Console,
                CallerRole = "admin",
                CallerPrivileges = new[] { "*" },
                FromChatGroupId = GlobalConstants.ConsoleGroup
            }
        };
        _sapi.ChatCommands.ExecuteUnparsed($"/player {player.PlayerName} gamemode {mode.ToString().ToLowerInvariant()}", args, result =>
        {
            if (result.Status != EnumCommandStatus.Success) _sapi.Logger.Warning($"[SombreterreGate] Could not set {player.PlayerName}'s game mode to {mode}: {result.StatusMessage}");
        });
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
        if (user == null) return guild.HasAllMembers ? MembershipStatus.NotMember : MembershipStatus.Unknown;
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

    public void Audit(string message)
    {
        _sapi.Logger.Audit($"[SombreterreGate] {message}");
        if (_discord?.AdminLogChannel != null) _discord.SendAdminLog($"[Gate] {message}");
    }

    public void NotifyLinked(ulong discordId)
    {
        var client = _discord?.Client;
        if (client == null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var user = await client.GetUserAsync(discordId);
                if (user != null) await user.SendMessageAsync(Lang.Get("th3essentials:gate-dm-linked"));
            }
            catch (Exception e)
            {
                // Closed DMs are common and fine: the admin log still has the event.
                _sapi.Logger.Debug($"[SombreterreGate] Could not DM a newly linked account: {e.GetType().Name}");
            }
        });
    }

    public override void Dispose()
    {
        _api?.Dispose();
        base.Dispose();
    }
}
