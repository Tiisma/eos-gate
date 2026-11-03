using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json.Linq;
using Th3Essentials.Sombreterre;

namespace Tests;

public class SombreterreGateHttpTests : IDisposable
{
    private class FakeBackend : GateHttpApi.IBackend
    {
        public MembershipStatus Status = MembershipStatus.Whitelisted;
        public PackBuilder.Result PackResult = new();
        public MembershipStatus Membership(ulong discordId) => Status;
        (int Online, int Max) GateHttpApi.IBackend.Status() => (3, 20);
        public PackBuilder.Result Pack() => PackResult;
        public void Log(string message) { }
    }

    private readonly string _folder = Directory.CreateTempSubdirectory("gate-http-").FullName;
    private readonly GateStore _store = new(null);
    private readonly TicketBook _tickets = new();
    private readonly FakeBackend _backend = new();
    private readonly GateHttpApi _api;
    private readonly HttpClient _http;

    public SombreterreGateHttpTests()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var prefix = $"http://127.0.0.1:{port}/";
        _api = new GateHttpApi(new GateConfig { HttpPrefix = prefix, OAuthClientId = "id", OAuthClientSecret = "secret" }, _store, _tickets, _backend);
        _api.Start();
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(prefix) };

        using (var archive = ZipFile.Open(Path.Combine(_folder, "core.zip"), ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("modinfo.json").Open());
            writer.Write("{\"modid\":\"core\",\"version\":\"1.0.0\"}");
        }
        _backend.PackResult = PackBuilder.Build("Sombreterre", "1.22.7", new[] { _folder }, Array.Empty<string>());
    }

    private HttpRequestMessage Authed(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
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
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.PostAsync("api/ticket", null)).StatusCode);
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
    public async Task LinkSessionAnswersOnlyTheLauncherHoldingTheVerifier()
    {
        const string verifier = "launcher-secret-verifier";
        var created = await _http.PostAsync("api/link/session", new StringContent(new JObject { ["verifierHash"] = GateStore.Hash(verifier) }.ToString(), Encoding.UTF8, "application/json"));
        var session = JObject.Parse(await created.Content.ReadAsStringAsync())["session"]!.ToString();

        var start = await _http.GetAsync($"api/link/start?session={Uri.EscapeDataString(session)}");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        Assert.StartsWith("https://discord.com/oauth2/authorize", start.Headers.Location!.ToString());

        async Task<JObject> Poll(string v) => JObject.Parse(await (await _http.PostAsync("api/link/poll", new StringContent(new JObject { ["session"] = session, ["verifier"] = v }.ToString(), Encoding.UTF8, "application/json"))).Content.ReadAsStringAsync());
        Assert.Equal("expired", (await Poll("someone-else"))["status"]!.ToString());
        Assert.Equal("pending", (await Poll(verifier))["status"]!.ToString());
    }

    public void Dispose()
    {
        _api.Dispose();
        _http.Dispose();
        Directory.Delete(_folder, true);
    }
}
