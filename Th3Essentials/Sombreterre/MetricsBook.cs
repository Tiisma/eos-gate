using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Th3Essentials.Sombreterre;

/// <summary>
/// Anonymous technical metrics: what players who said yes in the launcher send (launcher and game
/// performance, hardware, loading times, crash reports), and the server's own figures.
///
/// Nothing that names a person is ever kept. A record only holds the fields listed in
/// <see cref="Schemas"/>, each checked and bounded; anything else in a report is dropped, and crash
/// traces are cleaned once more of paths, addresses and identifiers. Records carry no account, no
/// address and no session id, and their time is rounded down to the hour. They are written one JSON
/// line each, in one file per UTC day, and files older than the retention are deleted.
/// </summary>
public class MetricsBook
{
    public const string Launcher = "launcher";
    public const string Client = "client";
    public const string Crash = "crash";
    public const string Server = "server";

    private enum FieldType
    {
        Number,
        Version,
        Label,
        Trace
    }

    private record Field(FieldType Type, double Min = 0, double Max = 0, int Length = 0);

    private static Field Number(double max) => new(FieldType.Number, 0, max);
    private static readonly Field VersionField = new(FieldType.Version, Length: 32);
    private static Field Label(int length) => new(FieldType.Label, Length: length);

    /// <summary>Every field a record of each kind may hold. Nothing else is ever stored.</summary>
    private static readonly Dictionary<string, Dictionary<string, Field>> Schemas = new()
    {
        [Launcher] = new()
        {
            ["launcherVersion"] = VersionField,
            ["gameVersion"] = VersionField,
            ["optimumVersion"] = VersionField,
            ["os"] = Label(48),
            ["cpu"] = Label(96),
            ["gpu"] = Label(96),
            ["ramGb"] = Number(4096),
            ["cpuThreads"] = Number(1024),
            ["installSeconds"] = Number(86400),
            ["syncSeconds"] = Number(86400),
            ["launchError"] = Label(64)
        },
        [Client] = new()
        {
            ["modVersion"] = VersionField,
            ["gameVersion"] = VersionField,
            ["fpsAvg"] = Number(2000),
            ["fpsMin"] = Number(2000),
            ["joinSeconds"] = Number(3600),
            ["memoryMb"] = Number(1048576)
        },
        [Crash] = new()
        {
            ["source"] = Label(16),
            ["version"] = VersionField,
            ["gameVersion"] = VersionField,
            ["errorType"] = Label(128),
            ["mod"] = Label(64),
            ["trace"] = new Field(FieldType.Trace, Length: 4000)
        },
        [Server] = new()
        {
            ["tickMsAvg"] = Number(600000),
            ["tickMsMax"] = Number(600000),
            ["memoryMb"] = Number(1048576),
            ["players"] = Number(10000),
            ["uptimeHours"] = Number(1000000)
        }
    };

    private static readonly Regex VersionPattern = new(@"^[0-9A-Za-z.+-]{1,32}$", RegexOptions.CultureInvariant);

    // Labels are free text from a player's machine (a GPU name, an error type): kept to plain characters.
    private static readonly Regex LabelUnsafe = new(@"[^\p{L}\p{N} ._()+/:#@,`'\[\]-]", RegexOptions.CultureInvariant);

    private static readonly Regex[] TraceScrubbers =
    {
        // Windows and Unix paths: they hold the user's account name.
        new(@"[A-Za-z]:[\\/][^\s:""'<>|]*", RegexOptions.CultureInvariant),
        new(@"(?<![\w.])/(?:home|Users|root|mnt|media|var|tmp|opt|usr|run)/[^\s:""'<>|]*", RegexOptions.CultureInvariant),
        new(@"\\\\[^\s:""'<>|]+", RegexOptions.CultureInvariant),
        // E-mail addresses, IPv4 and IPv6 addresses.
        new(@"[\w.+-]+@[\w-]+(\.[\w-]+)+", RegexOptions.CultureInvariant),
        new(@"\b\d{1,3}(\.\d{1,3}){3}(:\d+)?\b", RegexOptions.CultureInvariant),
        new(@"\b(?:[0-9A-Fa-f]{1,4}:){3,7}[0-9A-Fa-f]{1,4}\b", RegexOptions.CultureInvariant),
        // Long tokens mixing letters and digits: player UIDs, keys, session ids, hashes. Plain code
        // names (NullReferenceException) have no digits and stay.
        new(@"(?<![A-Za-z0-9+/=_-])(?=[A-Za-z0-9+/=_-]*\d)(?=[A-Za-z0-9+/=_-]*[A-Za-z])[A-Za-z0-9+/=_-]{20,}", RegexOptions.CultureInvariant)
    };

    private readonly string? _folder;
    private readonly object _lock = new();

    /// <param name="folder">Where the daily files go; null keeps nothing (tests).</param>
    public MetricsBook(string? folder)
    {
        _folder = folder;
        if (_folder != null) Directory.CreateDirectory(_folder);
    }

    /// <summary>
    /// The record that would be stored for this report, or null when it is not a known kind. Only
    /// listed fields survive, each checked, bounded and cleaned; empty fields are left out.
    /// </summary>
    public static JObject? Clean(string kind, JObject? report, DateTime nowUtc)
    {
        if (report == null || !Schemas.TryGetValue(kind, out var schema)) return null;
        var record = new JObject
        {
            ["kind"] = kind,
            ["at"] = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, 0, 0, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:00Z", CultureInfo.InvariantCulture)
        };
        foreach (var (name, field) in schema)
        {
            var value = report[name];
            if (value == null || value.Type == JTokenType.Null) continue;
            switch (field.Type)
            {
                case FieldType.Number when value.Type is JTokenType.Integer or JTokenType.Float:
                    var number = value.Value<double>();
                    if (double.IsFinite(number) && number >= field.Min && number <= field.Max) record[name] = Math.Round(number, 1);
                    break;
                case FieldType.Version when value.Type == JTokenType.String:
                    var version = value.ToString().Trim();
                    if (VersionPattern.IsMatch(version)) record[name] = version;
                    break;
                case FieldType.Label when value.Type == JTokenType.String:
                    var label = LabelUnsafe.Replace(Scrub(value.ToString()), "").Trim();
                    if (label.Length > field.Length) label = label[..field.Length];
                    if (label.Length > 0) record[name] = label;
                    break;
                case FieldType.Trace when value.Type == JTokenType.String:
                    var trace = Scrub(value.ToString()).Replace("\r", "");
                    if (trace.Length > field.Length) trace = trace[..field.Length];
                    if (trace.Trim().Length > 0) record[name] = trace;
                    break;
            }
        }
        return record.Count > 2 ? record : null;
    }

    /// <summary>Removes paths, e-mail and IP addresses, and long identifiers from free text.</summary>
    public static string Scrub(string text)
    {
        foreach (var scrubber in TraceScrubbers) text = scrubber.Replace(text, "<removed>");
        return text;
    }

    /// <summary>Cleans a report and stores it. False when nothing of it was worth keeping.</summary>
    public bool Add(string kind, JObject? report, DateTime nowUtc)
    {
        var record = Clean(kind, report, nowUtc);
        if (record == null) return false;
        if (_folder == null) return true;
        var line = record.ToString(Formatting.None) + "\n";
        lock (_lock)
        {
            File.AppendAllText(DayFile(nowUtc), line, Encoding.UTF8);
        }
        return true;
    }

    /// <summary>Deletes the daily files older than <paramref name="retentionDays"/>. Returns how many went.</summary>
    public int Prune(DateTime nowUtc, int retentionDays)
    {
        if (_folder == null) return 0;
        var oldest = nowUtc.Date.AddDays(-Math.Max(1, retentionDays) + 1);
        var removed = 0;
        lock (_lock)
        {
            foreach (var file in Directory.GetFiles(_folder, "*.jsonl"))
            {
                if (!DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var day)) continue;
                if (day.Date >= oldest) continue;
                File.Delete(file);
                removed++;
            }
        }
        return removed;
    }

    /// <summary>The records of the last <paramref name="days"/> days, today included.</summary>
    public List<JObject> Read(DateTime nowUtc, int days)
    {
        var records = new List<JObject>();
        if (_folder == null) return records;
        lock (_lock)
        {
            for (var i = Math.Max(1, days) - 1; i >= 0; i--)
            {
                var file = DayFile(nowUtc.AddDays(-i));
                if (!File.Exists(file)) continue;
                foreach (var line in File.ReadLines(file))
                {
                    try
                    {
                        if (JToken.Parse(line) is JObject record) records.Add(record);
                    }
                    catch (JsonException)
                    {
                        // a line cut short by a crash: skip it
                    }
                }
            }
        }
        return records;
    }

    /// <summary>A short text for the staff channel about these records, under Discord's 2000 characters.</summary>
    public static string Summary(IReadOnlyCollection<JObject> records, int days)
    {
        var text = new StringBuilder();
        text.AppendLine($"**Metrics, last {days} day(s)**");
        if (records.Count == 0)
        {
            text.AppendLine("No data.");
            return text.ToString();
        }

        List<JObject> Of(string kind) => records.Where(r => r["kind"]?.ToString() == kind).ToList();
        var client = Of(Client);
        var launcher = Of(Launcher);
        var crashes = Of(Crash);
        var server = Of(Server);

        if (client.Count > 0)
        {
            text.AppendLine($"__Game__ ({client.Count} report(s))");
            text.AppendLine($"FPS: median {Median(client, "fpsAvg")}, lowest 10% of minimums {Percentile(client, "fpsMin", 0.1)}");
            text.AppendLine($"Joining: median {Median(client, "joinSeconds")} s, slowest 10% {Percentile(client, "joinSeconds", 0.9)} s");
            text.AppendLine($"Memory: median {Median(client, "memoryMb")} MB");
        }

        if (launcher.Count > 0)
        {
            text.AppendLine($"__Launcher__ ({launcher.Count} report(s))");
            text.AppendLine($"Installing: median {Median(launcher, "installSeconds")} s. Syncing the pack: median {Median(launcher, "syncSeconds")} s");
            text.AppendLine($"RAM: median {Median(launcher, "ramGb")} GB. OS: {Top(launcher, "os", 3)}");
            text.AppendLine($"GPUs: {Top(launcher, "gpu", 3)}");
            var errors = Top(launcher, "launchError", 3);
            if (errors != "-") text.AppendLine($"Launch errors: {errors}");
        }

        text.AppendLine($"__Crashes__ ({crashes.Count})");
        foreach (var group in crashes
                     .GroupBy(c => $"{c["source"] ?? "?"}: {c["errorType"] ?? "unknown"}{(c["mod"] != null ? $" ({c["mod"]})" : "")}")
                     .OrderByDescending(g => g.Count())
                     .Take(5))
            text.AppendLine($"{group.Count()} x {group.Key}");

        if (server.Count > 0)
        {
            text.AppendLine($"__Server__ ({server.Count} sample(s))");
            text.AppendLine($"Tick: median {Median(server, "tickMsAvg")} ms, worst {Max(server, "tickMsMax")} ms. Memory: median {Median(server, "memoryMb")} MB. Players: up to {Max(server, "players")}");
        }

        var summary = text.ToString();
        return summary.Length <= 1990 ? summary : summary[..1990];
    }

    private static List<double> Values(IEnumerable<JObject> records, string field) =>
        records.Select(r => r[field]).Where(v => v != null && v.Type is JTokenType.Integer or JTokenType.Float).Select(v => v!.Value<double>()).OrderBy(v => v).ToList();

    private static string Median(IEnumerable<JObject> records, string field) => Percentile(records, field, 0.5);

    private static string Percentile(IEnumerable<JObject> records, string field, double rank)
    {
        var values = Values(records, field);
        if (values.Count == 0) return "-";
        var index = (int)Math.Round(rank * (values.Count - 1));
        return values[index].ToString("0.#", CultureInfo.InvariantCulture);
    }

    private static string Max(IEnumerable<JObject> records, string field)
    {
        var values = Values(records, field);
        return values.Count == 0 ? "-" : values[^1].ToString("0.#", CultureInfo.InvariantCulture);
    }

    private static string Top(IEnumerable<JObject> records, string field, int count)
    {
        var groups = records.Select(r => r[field]?.ToString()).Where(v => !string.IsNullOrEmpty(v))
            .GroupBy(v => v).OrderByDescending(g => g.Count()).Take(count).Select(g => $"{g.Key} ({g.Count()})").ToList();
        return groups.Count == 0 ? "-" : string.Join(", ", groups);
    }

    private string DayFile(DateTime dayUtc) => Path.Combine(_folder!, dayUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
}
