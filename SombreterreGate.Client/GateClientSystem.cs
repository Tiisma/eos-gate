using System;
using System.IO;
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

/// <summary>What the Sombreterre launcher writes into ModConfig right before it starts the game.</summary>
public class TicketFile
{
    public string Ticket = "";
}

/// <summary>
/// Hands the launch ticket the launcher left in ModConfig to the server.
///
/// The ticket is read as soon as the mod starts and the file deleted, so a stale one never lingers.
/// It is then sent as soon as the gate's channel is connected and the player exists, checked every
/// second rather than tied to one loading event: a heavily modded world can take long to finish
/// loading, and the server only waits so long. Every step is written to client-main.log so a
/// refused join can be explained from the player's side.
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
    private string? _ticket;
    private long _listenerId;
    private int _attempts;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI capi)
    {
        _capi = capi;
        _channel = capi.Network.RegisterChannel(GateTicketPacket.Channel).RegisterMessageType<GateTicketPacket>();

        _ticket = ReadAndDeleteTicket();
        if (_ticket == null) return;

        capi.Event.LevelFinalize += TrySend;
        _listenerId = capi.Event.RegisterGameTickListener(_ => TrySend(), RetryIntervalMs);
    }

    private string? ReadAndDeleteTicket()
    {
        var path = Path.Combine(GamePaths.ModConfig, TicketFileName);
        if (!File.Exists(path))
        {
            _capi.Logger.Notification($"{LogPrefix} No launch ticket in {GamePaths.ModConfig}. Start the game from the launcher to join a gated server.");
            return null;
        }

        string? ticket = null;
        try
        {
            ticket = _capi.LoadModConfig<TicketFile>(TicketFileName)?.Ticket;
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

        if (string.IsNullOrEmpty(ticket)) return null;
        _capi.Logger.Notification($"{LogPrefix} Launch ticket read; it will be sent once the server's gate channel is connected.");
        return ticket;
    }

    private void TrySend()
    {
        if (_ticket == null) return;

        if (_channel.Connected && _capi.World?.Player != null)
        {
            _channel.SendPacket(new GateTicketPacket { Ticket = _ticket });
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
        _ticket = null;
        _capi.Event.LevelFinalize -= TrySend;
        if (_listenerId != 0) _capi.Event.UnregisterGameTickListener(_listenerId);
        _listenerId = 0;
    }
}
