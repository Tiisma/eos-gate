using ProtoBuf;

namespace Th3Essentials.Sombreterre;

/// <summary>
/// What the client mod sends right after joining. Must stay field-for-field identical to the copy in
/// SombreterreGate.Client: the network channel matches messages by registration order and protobuf
/// field numbers, not by assembly.
/// </summary>
[ProtoContract]
public class GateTicketPacket
{
    public const string Channel = "sombreterregate";

    [ProtoMember(1)]
    public string Ticket = "";
}
