using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Web;
using Newtonsoft.Json.Linq;
using Th3Essentials.Sombreterre;

namespace Tests;

public class SombreterreGateHttpTests : IDisposable
{
    private class FakeBackend : GateHttpApi.IBackend
    {
        public MembershipStatus Status = MembershipStatus.Whitelisted;
        public PackBuilder.Result PackResult = new();
        public readonly List<string> Audits = new();
        public readonly List<ulong> Notified = new();
        public bool LockedDown { get; set; }
        public MembershipStatus Membership(ulong discordId) => Status;
        (int Online, int Max) GateHttpApi.IBackend.Status() => (3, 20);
        public PackBuilder.Result Pack() => PackResult;
        public void Log(string message) { }
        public void Audit(string message) { lock (Audits) Audits.Add(message); }
        public void NotifyLinked(ulong discordId) { lock (Notified) Notified.Add(discordId); }
    }

    private const string Version = "0.1.2";

    private readonly string _folder = Directory.CreateTempSubdirectory("gate-http-").FullName;
    private readonly GateStore _store = new(null);
    private readonly TicketBook _tickets = new();
    private readonly FakeBackend _backend = new();
    private GateHttpApi _api = null!;
    private HttpClient _http = null!;

    public SombreterreGateHttpTests()
    {
        using (var archive = ZipFile.Open(Path.Combine(_folder, "core.zip"), ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("modinfo.json").Open());
            writer.Write("{\"modid\":\"core\",\"version\":\"1.0.0\"}");
        }
        _backend.PackResult = PackBuilder.Build("Sombreterre", "1.22.7", new[] { _folder }, Array.Empty<string>());
        StartApi(new GateConfig());
    }

    private void StartApi(GateConfig config)
    {
        _api?.Dispose();
        _http?.Dispose();

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        config.HttpPrefix = $"http://127.0.0.1:{port}/";
        config.OAuthClientId = "id";
        config.OAuthClientSecret = "secret";
        _api = new GateHttpApi(config, _store, _tickets, _backend) { ExchangeOverride = code => Task.FromResult<(ulong, string)?>(code == "alice-code" ? (42UL, "alice") : (77UL, "victim")) };
        _api.Start();
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(config.HttpPrefix) };
        _http.DefaultRequestHeaders.Add(GateHttpApi.LauncherVersionHeader, Version);
    }

    private HttpRequestMessage Authed(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (method == HttpMethod.Post) request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        return request;
    }

    private static StringContent Body(JObject json) => new(json.ToString(), Encoding.UTF8, "application/json");

    private async Task<string> CreateSession(string verifier, int port = 51234)
    {
        var created = await _http.PostAsync("api/link/session", Body(new JObject { ["verifierHash"] = GateStore.Hash(verifier), ["loopbackPort"] = port }));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        return JObject.Parse(await created.Content.ReadAsStringAsync())["session"]!.ToString();
    }

    private async Task<JObject> Poll(string session, string verifier, string? handoff)
    {
        var body = new JObject { ["session"] = session, ["verifier"] = verifier };
        if (handoff != null) body["handoff"] = handoff;
        return JObject.Parse(await (await _http.PostAsync("api/link/poll", Body(body))).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task StatusIsPublic()
    {
        var body = JObject.Parse(await _http.GetStringAsync("api/status"));
        Assert.Equal(3, (int)body["online"]!);
        Assert.Equal(20, (int)body["max"]!);
    }

    [Fact]
    public async Task EverythingElseNeedsAToken()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.PostAsync("api/ticket", new StringContent("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("api/pack.json")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(Authed(HttpMethod.Get, "api/core.zip", "forged"))).StatusCode);
    }

    [Fact]
    public async Task WhitelistedTokenGetsTicketPackAndFiles()
    {
        var token = _store.IssueToken(42, "alice", DateTime.UtcNow);

        var ticket = JObject.Parse(await (await _http.SendAsync(Authed(HttpMethod.Post, "api/ticket", token))).Content.ReadAsStringAsync())["ticket"]!.ToString();
        Assert.Equal(42UL, _tickets.Consume(ticket, DateTime.UtcNow));

        var manifest = JObject.Parse(await (await _http.SendAsync(Authed(HttpMethod.Get, "api/pack.json", token))).Content.ReadAsStringAsync());
        Assert.Equal("core.zip", manifest["mods"]![0]!["file"]!.ToString());

        var file = await _http.SendAsync(Authed(HttpMethod.Get, "api/core.zip", token));
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal(new FileInfo(Path.Combine(_folder, "core.zip")).Length, (await file.Content.ReadAsByteArrayAsync()).Length);

        // Only names the manifest lists are ever served.
        Assert.NotEqual(HttpStatusCode.OK, (await _http.SendAsync(Authed(HttpMethod.Get, "api/..%2F..%2Fsecret.zip", token))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.SendAsync(Authed(HttpMethod.Get, "api/other.zip", token))).StatusCode);
    }

    [Fact]
    public async Task NoTicketWithoutTheRole()
    {
        var token = _store.IssueToken(42, "alice", DateTime.UtcNow);
        _backend.Status = MembershipStatus.MemberWithoutRole;

        var refused = await _http.SendAsync(Authed(HttpMethod.Post, "api/ticket", token));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("not-whitelisted", JObject.Parse(await refused.Content.ReadAsStringAsync())["error"]!.ToString());

        var me = JObject.Parse(await (await _http.SendAsync(Authed(HttpMethod.Get, "api/me", token))).Content.ReadAsStringAsync());
        Assert.Equal("not-whitelisted", me["membership"]!.ToString());
    }

    [Fact]
    public async Task LinkHandsTheTokenOnlyToTheLauncherThatReceivedTheLoopbackRedirect()
    {
        const string verifier = "launcher-secret-verifier";
        var session = await CreateSession(verifier, 51234);

        var start = await _http.GetAsync($"api/link/start?session={Uri.EscapeDataString(session)}");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        Assert.StartsWith("https://discord.com/oauth2/authorize", start.Headers.Location!.ToString());

        // Before Discord answers, and with someone else's verifier, nothing comes out.
        Assert.Equal("pending", (await Poll(session, verifier, null))["status"]!.ToString());
        Assert.Equal("expired", (await Poll(session, "someone-else", null))["status"]!.ToString());

        var callback = await _http.GetAsync($"api/link/callback?state={Uri.EscapeDataString(session)}&code=alice-code");
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        var location = callback.Headers.Location!;
        Assert.Equal("127.0.0.1", location.Host);
        Assert.Equal(51234, location.Port);
        var handoff = HttpUtility.ParseQueryString(location.Query)["handoff"]!;

        // The verifier alone is not enough any more: the handoff only reached the loopback listener.
        Assert.Equal("pending", (await Poll(session, verifier, null))["status"]!.ToString());
        Assert.Equal("pending", (await Poll(session, verifier, "guessed"))["status"]!.ToString());
        Assert.Empty(_backend.Notified);

        var linked = await Poll(session, verifier, handoff);
        Assert.Equal("linked", linked["status"]!.ToString());
        Assert.Equal(42UL, _store.FindToken(linked["token"]!.ToString(), DateTime.UtcNow, TimeSpan.FromDays(60))?.DiscordId);
        Assert.Equal(new[] { 42UL }, _backend.Notified);
        Assert.Contains(_backend.Audits, a => a.Contains("Discord 42"));

        // Handed over once.
        Assert.Equal("expired", (await Poll(session, verifier, handoff))["status"]!.ToString());
    }

    [Fact]
    public async Task ASharedLinkGivesTheSenderNothing()
    {
        // The attacker starts a session on their own computer and sends the start link to a victim.
        const string attackerVerifier = "attacker-verifier";
        var session = await CreateSession(attackerVerifier, 50000);

        // The victim approves on Discord; their browser is sent to *their* loopback, not the attacker's.
        var callback = await _http.GetAsync($"api/link/callback?state={Uri.EscapeDataString(session)}&code=victim-code");
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);

        // The attacker never sees the handoff, so polling never yields a token.
        for (var i = 0; i < 3; i++) Assert.Equal("pending", (await Poll(session, attackerVerifier, null))["status"]!.ToString());

        // A second callback for the same session is refused (no double token).
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync($"api/link/callback?state={Uri.EscapeDataString(session)}&code=alice-code")).StatusCode);
    }

    [Fact]
    public async Task LinkSessionsNeedALoopbackPortAndAreRateLimited()
    {
        var missingPort = await _http.PostAsync("api/link/session", Body(new JObject { ["verifierHash"] = GateStore.Hash("v") }));
        Assert.Equal(HttpStatusCode.BadRequest, missingPort.StatusCode);

        for (var i = 0; i < 5; i++) await CreateSession($"v{i}");
        var sixth = await _http.PostAsync("api/link/session", Body(new JObject { ["verifierHash"] = GateStore.Hash("v6"), ["loopbackPort"] = 51234 }));
        Assert.Equal((HttpStatusCode)429, sixth.StatusCode);
    }

    [Fact]
    public async Task OutdatedLaunchersAreRefused()
    {
        StartApi(new GateConfig { MinLauncherVersion = "0.1.2" });
        var token = _store.IssueToken(42, "alice", DateTime.UtcNow);

        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(Authed(HttpMethod.Post, "api/ticket", token))).StatusCode);

        _http.DefaultRequestHeaders.Remove(GateHttpApi.LauncherVersionHeader);
        var old = await _http.SendAsync(Authed(HttpMethod.Post, "api/ticket", token));
        Assert.Equal((HttpStatusCode)426, old.StatusCode);
        Assert.Equal("launcher-outdated", JObject.Parse(await old.Content.ReadAsStringAsync())["error"]!.ToString());

        _http.DefaultRequestHeaders.Add(GateHttpApi.LauncherVersionHeader, "0.1.1");
        Assert.Equal((HttpStatusCode)426, (await _http.SendAsync(Authed(HttpMethod.Get, "api/pack.json", token))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("api/status")).StatusCode);
    }

    [Fact]
    public async Task LockdownStopsTicketsAndLinksButNotStatus()
    {
        var token = _store.IssueToken(42, "alice", DateTime.UtcNow);
        _backend.LockedDown = true;

        Assert.Equal((HttpStatusCode)423, (await _http.SendAsync(Authed(HttpMethod.Post, "api/ticket", token))).StatusCode);
        Assert.Equal((HttpStatusCode)423, (await _http.PostAsync("api/link/session", Body(new JObject { ["verifierHash"] = GateStore.Hash("v"), ["loopbackPort"] = 51234 }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("api/status")).StatusCode);
    }

    public void Dispose()
    {
        _api.Dispose();
        _http.Dispose();
        Directory.Delete(_folder, true);
    }
}
