using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace SombreterreGate.Client;

/// <summary>
/// Must stay field-for-field identical to Th3Essentials.Sombreterre.GateTicketPacket on the server.
/// </summary>
[ProtoContract]
public class GateTicketPacket
{
    public const string Channel = "sombreterregate";

    [ProtoMember(1)]
    public string Ticket = "";
}

/// <summary>What the launcher writes into ModConfig right before it starts the game.</summary>
public class TicketFile
{
    public string Ticket = "";

    /// <summary>"host:port" of the server the game was launched for (launcher 0.1.3 and later).</summary>
    public string Server = "";
}

/// <summary>
/// What this game process knows about its launch: the ticket and the server. Static on purpose, so it
/// outlives the world it was read in. The server binds the ticket to this player's game account on
/// first use, so resending it after a disconnect (the main menu's Play button) lets this player, and
/// only this player, back in for the rest of the session.
/// </summary>
internal static class GateSession
{
    public static string? Ticket;
    public static string? Server;

    /// <summary>Main menu backgrounds this mod ships, read from its assets while a world was loaded.</summary>
    public static readonly List<byte[]> Backgrounds = new();
}

/// <summary>
/// The client side of The Exiles of Shadowland gate.
///
/// Reads the launch ticket the launcher left in ModConfig as soon as the mod starts and deletes the
/// file, then sends the ticket once the gate's channel is connected, checking every second rather than
/// waiting on one loading event: a heavily modded world can take long to load, and the server only
/// waits so long. Every step is written to client-main.log so a refused join can be explained.
///
/// It also installs the main menu changes (see <see cref="MainMenuPatches"/>). The game starts no mod
/// code at the main menu itself; the launcher always starts the game straight onto the server, so the
/// patches are in place by the time the player first sees the menu, when they leave the server.
/// On a server without the gate (single player, another server) the channel never connects and
/// nothing is sent.
/// </summary>
// ReSharper disable once UnusedType.Global
public class GateClientSystem : ModSystem
{
    internal const string TicketFileName = "sombreterre-ticket.json";
    private const string LogPrefix = "[SombreterreGate]";
    private const int RetryIntervalMs = 1000;
    private const int MaxAttempts = 300;

    private ICoreClientAPI _capi = null!;
    private IClientNetworkChannel _channel = null!;
    private long _listenerId;
    private int _attempts;
    private bool _sent;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI capi)
    {
        _capi = capi;
        _channel = capi.Network.RegisterChannel(GateTicketPacket.Channel).RegisterMessageType<GateTicketPacket>();

        LoadBackgrounds();
        MainMenuPatches.Apply(capi.Logger);

        ReadAndDeleteTicket();
        if (GateSession.Ticket == null) return;

        capi.Event.LevelFinalize += TrySend;
        _listenerId = capi.Event.RegisterGameTickListener(_ => TrySend(), RetryIntervalMs);
    }

    private void LoadBackgrounds()
    {
        try
        {
            var found = _capi.Assets.GetMany("textures/gui/backgrounds/", Mod.Info.ModID)
                .Where(asset => asset.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                .Select(asset => asset.Data)
                .Where(data => data is { Length: > 0 })
                .ToList();
            if (found.Count == 0) return;
            GateSession.Backgrounds.Clear();
            GateSession.Backgrounds.AddRange(found);
        }
        catch (Exception e)
        {
            _capi.Logger.Warning($"{LogPrefix} Could not read the main menu backgrounds: {e.GetType().Name}");
        }
    }

    private void ReadAndDeleteTicket()
    {
        var path = Path.Combine(GamePaths.ModConfig, TicketFileName);
        if (!File.Exists(path))
        {
            if (GateSession.Ticket != null) _capi.Logger.Notification($"{LogPrefix} No new launch ticket; reusing this session's.");
            else _capi.Logger.Notification($"{LogPrefix} No launch ticket in {GamePaths.ModConfig}. Start the game from the launcher to join a gated server.");
            return;
        }

        TicketFile? file = null;
        try
        {
            file = _capi.LoadModConfig<TicketFile>(TicketFileName);
        }
        catch (Exception e)
        {
            _capi.Logger.Warning($"{LogPrefix} Unreadable launch ticket: {e.GetType().Name}");
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException e)
        {
            _capi.Logger.Warning($"{LogPrefix} Could not delete the launch ticket: {e.GetType().Name}");
        }

        if (string.IsNullOrEmpty(file?.Ticket)) return;
        GateSession.Ticket = file.Ticket;
        if (!string.IsNullOrWhiteSpace(file.Server)) GateSession.Server = file.Server.Trim();
        _capi.Logger.Notification($"{LogPrefix} Launch ticket read; it will be sent once the server's gate channel is connected.");
    }

    private void TrySend()
    {
        if (_sent || GateSession.Ticket == null) return;

        if (_channel.Connected && _capi.World?.Player != null)
        {
            _channel.SendPacket(new GateTicketPacket { Ticket = GateSession.Ticket });
            _capi.Logger.Notification($"{LogPrefix} Launch ticket sent after {_attempts} check(s).");
            Stop();
            return;
        }

        if (++_attempts < MaxAttempts) return;
        _capi.Logger.Warning($"{LogPrefix} The server's gate channel never connected; no ticket sent. Is the gate installed on this server?");
        Stop();
    }

    private void Stop()
    {
        _sent = true;
        _capi.Event.LevelFinalize -= TrySend;
        if (_listenerId != 0) _capi.Event.UnregisterGameTickListener(_listenerId);
        _listenerId = 0;
    }
}
