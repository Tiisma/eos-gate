using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace SombreterreGate.Client;

/// <summary>
/// Must stay field-for-field identical to Th3Essentials.Sombreterre.GateMetricsPacket on the server.
/// </summary>
[ProtoContract]
public class GateMetricsPacket
{
    public const string Channel = "sombreterremetrics";

    [ProtoMember(1)]
    public string Kind = "";

    [ProtoMember(2)]
    public string Json = "";
}

/// <summary>The player's answer to the launcher's metrics question, which the launcher writes into ModConfig.</summary>
public class MetricsConsentFile
{
    public bool Consent;
}

/// <summary>
/// Anonymous game performance for the server's staff, only when the player said yes in the launcher
/// (ModConfig/sombreterre-metrics.json). Once per session, ten minutes into play, it sends the frame rate
/// (average and lowest, by the second, after the first minute), how long joining took and the memory in
/// use. Nothing names the player: no name, account or path is in the report, and the server keeps no
/// address. Single player (the creative world) sends nothing.
/// </summary>
// ReSharper disable once UnusedType.Global
public class MetricsClientSystem : ModSystem
{
    internal const string ConsentFileName = "sombreterre-metrics.json";
    private const string LogPrefix = "[SombreterreGate]";
    private const int SettleSeconds = 60;
    private const int ReportAfterSeconds = 600;

    private ICoreClientAPI _capi = null!;
    private IClientNetworkChannel _channel = null!;
    private readonly Stopwatch _sinceStart = Stopwatch.StartNew();
    private FrameCounter? _frames;
    private double _joinSeconds = -1;
    private long _listenerId;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI capi)
    {
        _capi = capi;
        _channel = capi.Network.RegisterChannel(GateMetricsPacket.Channel).RegisterMessageType<GateMetricsPacket>();
        if (capi.IsSinglePlayer || !Consented()) return;

        capi.Event.LevelFinalize += () =>
        {
            _joinSeconds = _sinceStart.Elapsed.TotalSeconds;
            _frames = new FrameCounter(SettleSeconds);
            capi.Event.RegisterRenderer(_frames, EnumRenderStage.Done, "sombreterregate-metrics");
            _listenerId = capi.Event.RegisterCallback(_ => Report(), ReportAfterSeconds * 1000);
        };
    }

    private bool Consented()
    {
        try
        {
            if (!File.Exists(Path.Combine(GamePaths.ModConfig, ConsentFileName))) return false;
            return _capi.LoadModConfig<MetricsConsentFile>(ConsentFileName)?.Consent == true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Report()
    {
        _listenerId = 0;
        if (!_channel.Connected || _frames == null) return;
        var report = new JsonObject
        {
            ["modVersion"] = Mod.Info.Version,
            ["gameVersion"] = GameVersion.ShortGameVersion,
            ["joinSeconds"] = Math.Round(_joinSeconds, 1),
            ["memoryMb"] = Math.Round(Process.GetCurrentProcess().WorkingSet64 / (1024.0 * 1024.0))
        };
        if (_frames.Seconds > 0)
        {
            report["fpsAvg"] = Math.Round(_frames.AverageFps, 1);
            report["fpsMin"] = Math.Round(_frames.LowestFps, 1);
        }
        _channel.SendPacket(new GateMetricsPacket { Kind = "client", Json = report.ToJsonString() });
        _capi.Logger.Notification($"{LogPrefix} Anonymous performance report sent (the player agreed in the launcher).");
        _capi.Event.UnregisterRenderer(_frames, EnumRenderStage.Done);
    }

    public override void Dispose()
    {
        if (_listenerId != 0) _capi?.Event.UnregisterCallback(_listenerId);
        base.Dispose();
    }

    /// <summary>Counts frames by the second, once the first seconds of loading are past.</summary>
    private class FrameCounter : IRenderer
    {
        private readonly double _settleSeconds;
        private double _elapsed;
        private double _secondTime;
        private int _secondFrames;
        private long _totalFrames;
        private double _countedTime;

        public FrameCounter(double settleSeconds) => _settleSeconds = settleSeconds;

        public int Seconds { get; private set; }
        public double LowestFps { get; private set; } = double.MaxValue;
        public double AverageFps => _countedTime > 0 ? _totalFrames / _countedTime : 0;

        public double RenderOrder => 1;
        public int RenderRange => 0;

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            _elapsed += deltaTime;
            if (_elapsed < _settleSeconds) return;
            _secondFrames++;
            _secondTime += deltaTime;
            if (_secondTime < 1) return;
            var fps = _secondFrames / _secondTime;
            if (fps < LowestFps) LowestFps = fps;
            _totalFrames += _secondFrames;
            _countedTime += _secondTime;
            Seconds++;
            _secondFrames = 0;
            _secondTime = 0;
        }

        public void Dispose()
        {
        }
    }
}
