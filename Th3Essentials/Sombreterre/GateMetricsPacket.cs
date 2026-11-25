using ProtoBuf;

namespace Th3Essentials.Sombreterre;

/// <summary>
/// One anonymous metrics report from the client mod, sent only when the player agreed in the launcher.
/// Must stay field-for-field identical to the copy in SombreterreGate.Client.
/// </summary>
[ProtoContract]
public class GateMetricsPacket
{
    public const string Channel = "sombreterremetrics";

    /// <summary>"client" or "crash".</summary>
    [ProtoMember(1)]
    public string Kind = "";

    /// <summary>The report, as a JSON object; see MetricsBook for the fields kept.</summary>
    [ProtoMember(2)]
    public string Json = "";
}
