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
/// Reads the launch ticket the launcher left in ModConfig and sends it to the server once the world
/// is loaded, then deletes the file: a ticket is single use, so a stale one is worth nothing anyway.
/// On a server without the gate (single player, another server) the channel is not connected and
/// nothing is sent.
/// </summary>
// ReSharper disable once UnusedType.Global
public class GateClientSystem : ModSystem
{
    internal const string TicketFileName = "sombreterre-ticket.json";

    private ICoreClientAPI _capi = null!;
    private IClientNetworkChannel _channel = null!;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI capi)
    {
        _capi = capi;
        _channel = capi.Network.RegisterChannel(GateTicketPacket.Channel).RegisterMessageType<GateTicketPacket>();
        capi.Event.LevelFinalize += SendTicket;
    }

    private void SendTicket()
    {
        string? ticket = null;
        try
        {
            ticket = _capi.LoadModConfig<TicketFile>(TicketFileName)?.Ticket;
        }
        catch (Exception e)
        {
            _capi.Logger.Warning($"[SombreterreGate] Unreadable launch ticket: {e.GetType().Name}");
        }
        finally
        {
            var path = Path.Combine(GamePaths.ModConfig, TicketFileName);
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
                // left for the next launch to overwrite
            }
        }

        if (string.IsNullOrEmpty(ticket) || !_channel.Connected) return;
        _channel.SendPacket(new GateTicketPacket { Ticket = ticket });
    }
}
