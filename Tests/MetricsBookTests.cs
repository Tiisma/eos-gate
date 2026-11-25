using Newtonsoft.Json.Linq;
using Th3Essentials.Sombreterre;

namespace Tests;

public class MetricsBookTests
{
    private static readonly DateTime Now = new(2026, 11, 25, 18, 42, 7, DateTimeKind.Utc);

    [Fact]
    public void OnlyListedFieldsSurviveBoundedAndTheHourIsRounded()
    {
        var record = MetricsBook.Clean(MetricsBook.Client, new JObject
        {
            ["fpsAvg"] = 58.437,
            ["fpsMin"] = -3,
            ["joinSeconds"] = 1e9,
            ["gameVersion"] = "1.22.7",
            ["modVersion"] = "1.0; DROP TABLE",
            ["playerName"] = "alice",
            ["playerUid"] = "Zx9kLm2Qp7Rt4Vw8Yb1Nc3"
        }, Now)!;

        Assert.Equal("client", record["kind"]!.ToString());
        Assert.Equal("2026-11-25T18:00Z", record["at"]!.ToString());
        Assert.Equal(58.4, record["fpsAvg"]!.Value<double>());
        Assert.Equal("1.22.7", record["gameVersion"]!.ToString());
        Assert.Null(record["fpsMin"]);
        Assert.Null(record["joinSeconds"]);
        Assert.Null(record["modVersion"]);
        Assert.Null(record["playerName"]);
        Assert.Null(record["playerUid"]);
    }

    [Fact]
    public void UnknownKindsAndEmptyReportsAreRefused()
    {
        Assert.Null(MetricsBook.Clean("chat", new JObject { ["text"] = "hi" }, Now));
        Assert.Null(MetricsBook.Clean(MetricsBook.Client, new JObject { ["playerName"] = "alice" }, Now));
        Assert.Null(MetricsBook.Clean(MetricsBook.Client, null, Now));
    }

    [Fact]
    public void CrashTracesLoseUserPathsAddressesAndIdentifiers()
    {
        var trace = "System.NullReferenceException: Object reference not set\n"
                    + "   at Vintagestory.Client.TextureAtlasManager.RegenMipMaps() in C:\\Users\\Alice\\AppData\\Roaming\\VintagestoryData\\Mods\\x.cs:line 12\n"
                    + "   at Foo.Bar() in /home/alice/.config/VintagestoryData/Mods/y.cs\n"
                    + "Connected to 57.129.112.230:42420 as alice@example.com, uid Zx9kLm2Qp7Rt4Vw8Yb1Nc3==";
        var record = MetricsBook.Clean(MetricsBook.Crash, new JObject { ["source"] = "client", ["errorType"] = "System.NullReferenceException", ["trace"] = trace }, Now)!;
        var kept = record["trace"]!.ToString();

        Assert.Contains("System.NullReferenceException", kept);
        Assert.Contains("TextureAtlasManager.RegenMipMaps", kept);
        Assert.DoesNotContain("Alice", kept);
        Assert.DoesNotContain("alice", kept);
        Assert.DoesNotContain("57.129.112.230", kept);
        Assert.DoesNotContain("Zx9kLm2Qp7Rt4Vw8Yb1Nc3", kept);
        Assert.Equal("System.NullReferenceException", record["errorType"]!.ToString());
    }

    [Fact]
    public void DailyFilesAreReadBackAndDeletedAfterTheRetention()
    {
        var folder = Directory.CreateTempSubdirectory("metrics-").FullName;
        var book = new MetricsBook(folder);
        Assert.True(book.Add(MetricsBook.Client, new JObject { ["fpsAvg"] = 60 }, Now.AddDays(-31)));
        Assert.True(book.Add(MetricsBook.Client, new JObject { ["fpsAvg"] = 40 }, Now.AddDays(-2)));
        Assert.True(book.Add(MetricsBook.Server, new JObject { ["players"] = 4 }, Now));

        Assert.Equal(2, book.Read(Now, 7).Count);
        Assert.Equal(1, book.Prune(Now, 30));
        Assert.Equal(2, Directory.GetFiles(folder, "*.jsonl").Length);

        var summary = MetricsBook.Summary(book.Read(Now, 7), 7);
        Assert.Contains("FPS: median 40", summary);
        Assert.Contains("Players: up to 4", summary);
        Assert.True(summary.Length < 2000);
        Directory.Delete(folder, true);
    }
}
