using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Discord;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace Th3Essentials.Sombreterre;

/// <summary>
/// Anonymous metrics (see <see cref="MetricsBook"/>): reports from the client mod over its own network
/// channel, reports from the launcher over the HTTP API, the server's own figures every few minutes, the
/// daily clean-up and the periodic summary on Discord.
/// </summary>
public partial class SombreterreGateSystem
{
    private const string MetricsFolderName = "SombreterreMetrics";
    private const string LastSummaryFile = "last-summary.txt";

    /// <summary>Reports one connected player may send per connection; the count is dropped when they leave.</summary>
    private const int MetricsPerConnection = 20;

    private const int ServerSampleMinutes = 5;
    private const int HousekeepingMinutes = 60;

    private MetricsBook? _metrics;
    private readonly ConcurrentDictionary<string, int> _metricsPerPlayer = new();

    private double _tickMsSum;
    private double _tickMsMax;
    private int _tickCount;

    private string MetricsFolder => Path.Combine(GamePaths.DataPath, "ModData", MetricsFolderName);

    private void StartMetrics(ICoreServerAPI sapi)
    {
        sapi.Network.RegisterChannel(GateMetricsPacket.Channel)
            .RegisterMessageType<GateMetricsPacket>()
            .SetMessageHandler<GateMetricsPacket>(OnMetricsPacket);

        if (!_config.MetricsEnabled)
        {
            sapi.Logger.Notification("[SombreterreGate] Metrics are off (MetricsEnabled).");
            return;
        }

        _metrics = new MetricsBook(MetricsFolder);
        // Every server tick: the time since the previous one.
        sapi.Event.RegisterGameTickListener(dt =>
        {
            var ms = dt * 1000.0;
            _tickMsSum += ms;
            _tickCount++;
            if (ms > _tickMsMax) _tickMsMax = ms;
        }, 0);
        sapi.Event.RegisterGameTickListener(_ => SampleServer(), ServerSampleMinutes * 60 * 1000);
        sapi.Event.RegisterGameTickListener(_ => MetricsHousekeeping(), HousekeepingMinutes * 60 * 1000);
        MetricsHousekeeping();
    }

    public bool RecordMetrics(string kind, JObject report)
    {
        if (_metrics == null) return false;
        try
        {
            return _metrics.Add(kind, report, DateTime.UtcNow);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log($"Could not store a metrics report: {e.GetType().Name}");
            return false;
        }
    }

    private void OnMetricsPacket(IServerPlayer player, GateMetricsPacket packet)
    {
        // Who sent it is only used to cap the count, and forgotten when they leave.
        if (_metricsPerPlayer.AddOrUpdate(player.PlayerUID, 1, (_, count) => count + 1) > MetricsPerConnection) return;
        if (packet.Kind is not (MetricsBook.Client or MetricsBook.Crash) || packet.Json.Length > 16384) return;
        JObject? report;
        try
        {
            report = JToken.Parse(packet.Json) as JObject;
        }
        catch (JsonException)
        {
            return;
        }
        if (report != null) RecordMetrics(packet.Kind, report);
    }

    private void SampleServer()
    {
        if (_tickCount == 0) return;
        var process = Process.GetCurrentProcess();
        RecordMetrics(MetricsBook.Server, new JObject
        {
            ["tickMsAvg"] = _tickMsSum / _tickCount,
            ["tickMsMax"] = _tickMsMax,
            ["memoryMb"] = process.WorkingSet64 / (1024.0 * 1024.0),
            ["players"] = _sapi.World.AllOnlinePlayers.Length,
            ["uptimeHours"] = (DateTime.Now - process.StartTime).TotalHours
        });
        _tickMsSum = 0;
        _tickMsMax = 0;
        _tickCount = 0;
    }

    /// <summary>Deletes what is past the retention, and posts the summary once it is due.</summary>
    private void MetricsHousekeeping()
    {
        if (_metrics == null) return;
        var now = DateTime.UtcNow;
        try
        {
            var removed = _metrics.Prune(now, _config.MetricsRetentionDays);
            if (removed > 0) _sapi.Logger.Notification($"[SombreterreGate] Deleted {removed} day(s) of metrics past {_config.MetricsRetentionDays} days.");

            if (_config.MetricsSummaryDays <= 0) return;
            var stamp = Path.Combine(MetricsFolder, LastSummaryFile);
            if (!File.Exists(stamp))
            {
                // The first summary comes one full period after metrics start.
                File.WriteAllText(stamp, now.ToString("o", CultureInfo.InvariantCulture));
                return;
            }
            if (!DateTime.TryParse(File.ReadAllText(stamp).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var last)) last = DateTime.MinValue;
            if (now - last < TimeSpan.FromDays(_config.MetricsSummaryDays)) return;
            if (!PostMetricsSummary(_config.MetricsSummaryDays)) return;
            File.WriteAllText(stamp, now.ToString("o", CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log($"Metrics housekeeping failed: {e.GetType().Name}");
        }
    }

    /// <summary>Sends the summary of the last days to the metrics channel. False when Discord is not there to take it.</summary>
    private bool PostMetricsSummary(int days)
    {
        if (_metrics == null) return false;
        var channel = MetricsChannel();
        if (channel == null) return false;
        var summary = MetricsBook.Summary(_metrics.Read(DateTime.UtcNow, days), days);
        _ = Task.Run(async () =>
        {
            try
            {
                await channel.SendMessageAsync(summary);
            }
            catch (Exception e)
            {
                _sapi.Logger.Warning($"[SombreterreGate] Could not post the metrics summary: {e.GetType().Name}");
            }
        });
        return true;
    }

    private IMessageChannel? MetricsChannel()
    {
        if (_discord == null) return null;
        if (_config.MetricsChannelId == 0) return _discord.AdminLogChannel;
        return _discord.Client.GetChannel(_config.MetricsChannelId) as IMessageChannel;
    }

    private TextCommandResult MetricsCommand(int days)
    {
        if (_metrics == null) return TextCommandResult.Error("Metrics are off (MetricsEnabled).");
        if (days <= 0) days = Math.Max(1, _config.MetricsSummaryDays);
        days = Math.Min(days, Math.Max(1, _config.MetricsRetentionDays));
        var summary = MetricsBook.Summary(_metrics.Read(DateTime.UtcNow, days), days);
        var posted = PostMetricsSummary(days);
        return TextCommandResult.Success(posted ? summary : summary + "\n(Not posted: no Discord channel for metrics.)");
    }
}
