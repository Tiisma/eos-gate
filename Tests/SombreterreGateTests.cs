using System.IO.Compression;
using Newtonsoft.Json.Linq;
using Th3Essentials.Sombreterre;

namespace Tests;

public class SombreterreGateTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
    private const ulong Alice = 1001;
    private const ulong Bob = 1002;

    private static Func<ulong, MembershipStatus> Everyone(MembershipStatus status) => _ => status;

    [Fact]
    public void TicketIsSingleUseAndExpires()
    {
        var book = new TicketBook();
        var ticket = book.Issue(Alice, Now, TimeSpan.FromMinutes(3));
        Assert.Equal(Alice, book.Consume(ticket, Now));
        Assert.Null(book.Consume(ticket, Now));

        var late = book.Issue(Alice, Now, TimeSpan.FromMinutes(3));
        Assert.Null(book.Consume(late, Now.AddMinutes(4)));
        Assert.Null(book.Consume("forged", Now));
    }

    [Fact]
    public void FirstValidTicketLinksWithoutAnyCommand()
    {
        var store = new GateStore(null);
        var book = new TicketBook();

        var outcome = GateDecision.Evaluate("uid-alice", book.Issue(Alice, Now, TimeSpan.FromMinutes(3)), store, book, Everyone(MembershipStatus.Whitelisted), Now);

        Assert.Equal(GateOutcome.Admitted, outcome);
        Assert.Equal(Alice, store.LinkOf("uid-alice"));
        Assert.Equal(GateOutcome.Admitted, GateDecision.Evaluate("uid-alice", book.Issue(Alice, Now, TimeSpan.FromMinutes(3)), store, book, Everyone(MembershipStatus.Whitelisted), Now));
    }

    [Fact]
    public void NobodyCanTakeOverAnotherAccount()
    {
        var store = new GateStore(null);
        var book = new TicketBook();
        store.Link("uid-alice", Alice);

        // Bob's ticket used from Alice's game account, and Alice's Discord from a second game account.
        Assert.Equal(GateOutcome.PlayerLinkedElsewhere, GateDecision.Evaluate("uid-alice", book.Issue(Bob, Now, TimeSpan.FromMinutes(3)), store, book, Everyone(MembershipStatus.Whitelisted), Now));
        Assert.Equal(GateOutcome.DiscordLinkedElsewhere, GateDecision.Evaluate("uid-other", book.Issue(Alice, Now, TimeSpan.FromMinutes(3)), store, book, Everyone(MembershipStatus.Whitelisted), Now));
        Assert.Equal(Alice, store.LinkOf("uid-alice"));
        Assert.Null(store.LinkOf("uid-other"));
    }

    [Theory]
    [InlineData(MembershipStatus.NotMember, GateOutcome.NotMember)]
    [InlineData(MembershipStatus.MemberWithoutRole, GateOutcome.NotWhitelisted)]
    [InlineData(MembershipStatus.Unknown, GateOutcome.DiscordUnavailable)]
    public void RefusesWithoutTheRoleAndLinksNothing(MembershipStatus status, GateOutcome expected)
    {
        var store = new GateStore(null);
        var book = new TicketBook();
        Assert.Equal(expected, GateDecision.Evaluate("uid-alice", book.Issue(Alice, Now, TimeSpan.FromMinutes(3)), store, book, Everyone(status), Now));
        Assert.Null(store.LinkOf("uid-alice"));
    }

    [Fact]
    public void StoreKeepsOnlyTokenDigestsAndSurvivesRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gate-store-{Guid.NewGuid():N}.json");
        try
        {
            var store = new GateStore(path);
            var token = store.IssueToken(Alice, "alice", Now);
            store.Link("uid-alice", Alice);

            Assert.DoesNotContain(token, File.ReadAllText(path));
            var reloaded = new GateStore(path);
            Assert.Equal(Alice, reloaded.FindToken(token, Now, TimeSpan.FromDays(60))?.DiscordId);
            Assert.Equal("uid-alice", reloaded.PlayerOf(Alice));
            Assert.True(reloaded.RevokeToken(token));
            Assert.Null(new GateStore(path).FindToken(token, Now, TimeSpan.FromDays(60)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IdleTokensExpireAndRevokeAllClearsAnAccount()
    {
        var store = new GateStore(null);
        var idle = TimeSpan.FromDays(60);
        var old = store.IssueToken(Alice, "alice", Now);
        var other = store.IssueToken(Alice, "alice", Now);
        var bob = store.IssueToken(Bob, "bob", Now);

        Assert.NotNull(store.FindToken(old, Now.AddDays(59), idle));
        Assert.NotNull(store.FindToken(old, Now.AddDays(100), idle)); // used on day 59, so still fresh
        Assert.Null(store.FindToken(bob, Now.AddDays(61), idle));
        Assert.Null(store.FindToken(bob, Now, idle)); // dropped for good

        Assert.Equal(2, store.RevokeAllFor(Alice));
        Assert.Null(store.FindToken(other, Now, idle));
    }

    [Fact]
    public void OneDiscordAccountHoldsOneTicketAtATime()
    {
        var book = new TicketBook();
        var first = book.Issue(Alice, Now, TimeSpan.FromMinutes(10));
        var second = book.Issue(Alice, Now, TimeSpan.FromMinutes(10));
        var bob = book.Issue(Bob, Now, TimeSpan.FromMinutes(10));

        Assert.Null(book.Consume(first, Now));
        Assert.Equal(Alice, book.Consume(second, Now));
        Assert.Equal(Bob, book.Consume(bob, Now));
    }

    [Theory]
    [InlineData("0.1.2", "0.1.2")]
    [InlineData("1.0.0-beta.3", "1.0.0")]
    [InlineData("0.1", null)]
    [InlineData("nope", null)]
    [InlineData("", null)]
    public void LauncherVersionsParseAsThreePartVersions(string value, string? expected)
    {
        Assert.Equal(expected, GateHttpApi.ParseVersion(value)?.ToString());
    }

    [Fact]
    public void UnlinkReportsTheDiscordAccountItFreed()
    {
        var store = new GateStore(null);
        store.Link("uid-alice", Alice);
        Assert.Equal(Alice, store.Unlink("uid-alice"));
        Assert.Null(store.Unlink("uid-alice"));
    }

    [Fact]
    public void PackListsClientModsAndKeepsServerModsPrivateToTheServer()
    {
        var folder = Directory.CreateTempSubdirectory("gate-pack-").FullName;
        try
        {
            Zip(folder, "core.zip", "{\"modid\":\"sombreterrecore\",\"version\":\"0.1.0\",\"side\":\"universal\"}");
            Zip(folder, "secret.zip", "{\"ModID\":\"sombreterreserver\",\"Version\":\"1.0.0\",\"Side\":\"Server\"}");
            Zip(folder, "carryon.zip", "{\"modid\":\"carryon\",\"version\":\"1.10.4\"}");
            Zip(folder, "noinfo.zip", null);
            File.WriteAllText(Path.Combine(folder, "loose.dll"), "x");

            var pack = PackBuilder.Build("Sombreterre", "1.22.7", new[] { folder }, new[] { "carryon" });
            var mods = (JArray)JObject.Parse(pack.ManifestJson)["mods"]!;

            Assert.Equal(2, mods.Count);
            var carryon = mods.Single(m => (string?)m["modid"] == "carryon");
            Assert.Equal("moddb", (string?)carryon["source"]);
            Assert.Equal(64, ((string?)carryon["sha256"])?.Length);
            var core = mods.Single(m => (string?)m["modid"] == "sombreterrecore");
            Assert.Equal("private", (string?)core["source"]);
            Assert.Equal(64, ((string?)core["sha256"])?.Length);
            Assert.Equal(new[] { "core.zip" }, pack.PrivateFiles.Keys);
            Assert.Equal(2, pack.Skipped.Count);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static void Zip(string folder, string name, string? modinfo)
    {
        using var archive = ZipFile.Open(Path.Combine(folder, name), ZipArchiveMode.Create);
        var entry = archive.CreateEntry(modinfo == null ? "readme.txt" : "modinfo.json");
        using var writer = new StreamWriter(entry.Open());
        writer.Write(modinfo ?? "nothing");
    }
}
