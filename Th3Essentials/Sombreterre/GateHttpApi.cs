using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Th3Essentials.Sombreterre;

/// <summary>
/// The launcher's side of the gate, over HTTP:
///
///   GET  /api/status                 players online, public
///   POST /api/link/session           {verifierHash, loopbackPort} -> {session, url}: starts a Discord link
///   GET  /api/link/start?session=    redirects the browser to Discord
///   GET  /api/link/callback          Discord comes back here; the browser is sent on to the launcher
///   POST /api/link/poll              {session, verifier, handoff} -> {status, token?, name?}
///   GET  /api/me                     (token) Discord account, membership, whitelist
///   POST /api/ticket                 (token, whitelisted) -> {ticket, expiresIn}
///   POST /api/logout                 (token) revokes it
///   GET  /api/pack.json              (token, whitelisted) the pack manifest
///   GET  /api/{file}.zip             (token, whitelisted) one private mod of that manifest
///
/// The launch flow never handles a Discord password or the OAuth client secret.
///
/// Who gets the token: the launcher that started the session proves it twice. It holds a verifier
/// only it knows (PKCE style), and once Discord has sent the player back, this API redirects the
/// browser to a one-time listener on the launcher's own loopback address with a handoff code
/// (RFC 8252). Both are required to collect the token. A link sent to someone else therefore cannot
/// hand their Discord account to the sender: the victim's browser goes back to the victim's own
/// computer, where the sender's launcher is not listening. The token is only minted at that point,
/// so an abandoned or failed session never leaves a token behind.
/// </summary>
public class GateHttpApi : IDisposable
{
    private static readonly Regex PlayerUidPattern = new("^[A-Za-z0-9+/=_-]{8,64}$", RegexOptions.CultureInvariant);
    private static readonly Regex PlayerNamePattern = new(@"^[\p{L}\p{N}_.-]{1,32}$", RegexOptions.CultureInvariant);

    public interface IBackend
    {
        MembershipStatus Membership(ulong discordId);
        (int Online, int Max) Status();
        PackBuilder.Result Pack();
        void Log(string message);

        /// <summary>Records a security-relevant event: server log and, when configured, the Discord admin channel.</summary>
        void Audit(string message);

        /// <summary>Tells a Discord account that a launcher was just linked to it, so a link they did not make is noticed.</summary>
        void NotifyLinked(ulong discordId);

        /// <summary>Brings the server's whitelist in line with this Discord account's role. <paramref name="droppedUid"/> is an account it no longer plays with.</summary>
        void SyncWhitelist(ulong discordId, string? droppedUid);

        /// <summary>True while staff has locked the gate: no new links, tickets or downloads.</summary>
        bool LockedDown { get; }
    }

    private enum SessionState
    {
        Pending,
        InProgress,
        Authorized,
        Failed
    }

    private class LinkSession
    {
        public string VerifierHash = "";
        public int LoopbackPort;
        public DateTime CreatedUtc;
        public SessionState State = SessionState.Pending;
        public ulong DiscordId;
        public string Name = "";
        public string Handoff = "";
        public string Address = "";
    }

    public const string LauncherVersionHeader = "X-EoS-Launcher-Version";

    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AddressWindow = TimeSpan.FromMinutes(10);
    private const int MaxBodyBytes = 4096;
    private const int MaxSessions = 500;

    private readonly GateConfig _config;
    private readonly GateStore _store;
    private readonly TicketBook _tickets;
    private readonly IBackend _backend;
    private readonly DiscordOAuth? _oauth;
    private readonly Version? _minLauncherVersion;
    private readonly Dictionary<string, LinkSession> _sessions = new();
    private readonly Dictionary<string, List<DateTime>> _sessionsByAddress = new();
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Tests only: stands in for the Discord code exchange.</summary>
    internal Func<string, Task<(ulong Id, string Name)?>>? ExchangeOverride { get; set; }

    public GateHttpApi(GateConfig config, GateStore store, TicketBook tickets, IBackend backend)
    {
        _config = config;
        _store = store;
        _tickets = tickets;
        _backend = backend;
        if (!string.IsNullOrEmpty(config.OAuthClientId) && !string.IsNullOrEmpty(config.OAuthClientSecret))
            _oauth = new DiscordOAuth(config.OAuthClientId, config.OAuthClientSecret, config.PublicBase() + "api/link/callback");
        _minLauncherVersion = ParseVersion(config.MinLauncherVersion);
        _listener.Prefixes.Add(config.HttpPrefix);
    }

    public void Start()
    {
        _listener.Start();
        Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (_stop.IsCancellationRequested || !_listener.IsListening)
            {
                return;
            }
            _ = Task.Run(() => Handle(context));
        }
    }

    private async Task Handle(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            await Route(context.Request, response);
        }
        catch (Exception e)
        {
            _backend.Log($"HTTP {context.Request.HttpMethod} {context.Request.Url?.AbsolutePath} failed: {e.GetType().Name}");
            try
            {
                Json(response, 500, new { error = "internal" });
            }
            catch (Exception)
            {
                // the response was already being written
            }
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception)
            {
                // client went away
            }
        }
    }

    private async Task Route(HttpListenerRequest request, HttpListenerResponse response)
    {
        var path = request.Url?.AbsolutePath ?? "/";
        var method = request.HttpMethod;

        if (method == "GET" && path == "/api/status")
        {
            var (online, max) = _backend.Status();
            Json(response, 200, new { online, max });
            return;
        }

        if (method == "GET" && path == "/api/link/callback")
        {
            // The browser coming back from Discord carries no launcher header; it is checked at poll.
            await Callback(request, response);
            return;
        }

        if (method == "GET" && path == "/api/link/start")
        {
            StartSession(request, response);
            return;
        }

        if (!LauncherIsRecentEnough(request))
        {
            Json(response, 426, new { error = "launcher-outdated", minVersion = _config.MinLauncherVersion });
            return;
        }

        switch (method, path)
        {
            case ("POST", "/api/link/session"):
                await CreateSession(request, response);
                return;
            case ("POST", "/api/link/poll"):
                await Poll(request, response);
                return;
        }

        // Everything below needs a launcher token.
        var token = BearerToken(request);
        var record = token == null ? null : _store.FindToken(token, DateTime.UtcNow, TimeSpan.FromDays(Math.Max(1, _config.TokenIdleDays)));
        if (token == null || record == null)
        {
            Json(response, 401, new { error = "unauthorized" });
            return;
        }

        var membership = _backend.Membership(record.DiscordId);
        switch (method, path)
        {
            case ("GET", "/api/me"):
                Json(response, 200, new { discordName = record.DiscordName, membership = MembershipName(membership), linkedPlayer = _store.PlayerOf(record.DiscordId) != null });
                return;
            case ("POST", "/api/logout"):
                _store.RevokeToken(token);
                _backend.Audit($"Launcher token revoked by its owner (Discord {record.DiscordId}).");
                Json(response, 200, new { ok = true });
                return;
        }

        if (_backend.LockedDown)
        {
            Json(response, 423, new { error = "locked-down" });
            return;
        }

        if (membership != MembershipStatus.Whitelisted)
        {
            Json(response, 403, new { error = MembershipName(membership) });
            return;
        }

        if (method == "POST" && path == "/api/ticket")
        {
            var body = await ReadBody(request);
            var playerUid = body?["playerUid"]?.ToString();
            var playerName = body?["playerName"]?.ToString();
            string? droppedUid = null;
            if (playerUid != null && playerName != null && PlayerUidPattern.IsMatch(playerUid) && PlayerNamePattern.IsMatch(playerName))
            {
                var claim = _store.Claim(record.DiscordId, playerUid, playerName, out droppedUid);
                if (claim != GateStore.ClaimOutcome.Claimed)
                {
                    // The server would refuse this account at the door: say why before the game starts.
                    Json(response, 409, new { error = claim == GateStore.ClaimOutcome.PlayerLinkedElsewhere ? "player-linked" : "discord-linked" });
                    return;
                }
            }
            _backend.SyncWhitelist(record.DiscordId, droppedUid);

            var ticket = _tickets.Issue(record.DiscordId, DateTime.UtcNow, TimeSpan.FromSeconds(_config.TicketLifetimeSeconds));
            Json(response, 200, new { ticket, expiresIn = _config.TicketLifetimeSeconds });
            return;
        }

        if (method == "GET" && path == "/api/pack.json")
        {
            Write(response, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(_backend.Pack().ManifestJson));
            return;
        }

        if (method == "GET" && path.StartsWith("/api/", StringComparison.Ordinal) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            // Only names the current manifest lists are served: the map is the allowlist, so no path
            // a request spells ever reaches the disk.
            var name = Uri.UnescapeDataString(path["/api/".Length..]);
            if (_backend.Pack().PrivateFiles.TryGetValue(name, out var file))
            {
                await using var stream = File.OpenRead(file.FullPath);
                response.StatusCode = 200;
                response.ContentType = "application/zip";
                response.ContentLength64 = stream.Length;
                await stream.CopyToAsync(response.OutputStream);
                return;
            }
        }

        Json(response, 404, new { error = "not-found" });
    }

    private async Task CreateSession(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (_oauth == null)
        {
            Json(response, 503, new { error = "oauth-not-configured" });
            return;
        }
        if (_backend.LockedDown)
        {
            Json(response, 423, new { error = "locked-down" });
            return;
        }

        var body = await ReadBody(request);
        var verifierHash = body?["verifierHash"]?.ToString();
        var port = body?["loopbackPort"]?.Type == JTokenType.Integer ? (int)body["loopbackPort"]! : 0;
        if (verifierHash == null || verifierHash.Length != 64 || !verifierHash.All(Uri.IsHexDigit) || port is < 1024 or > 65535)
        {
            Json(response, 400, new { error = "bad-request" });
            return;
        }

        var address = ClientAddress(request);
        var now = DateTime.UtcNow;
        var id = GateStore.RandomToken();
        lock (_sessions)
        {
            Prune(now);
            var recent = _sessionsByAddress.TryGetValue(address, out var times) ? times : _sessionsByAddress[address] = new List<DateTime>();
            if (_sessions.Count >= MaxSessions || recent.Count >= Math.Max(1, _config.LinkSessionsPerAddress))
            {
                Json(response, 429, new { error = "busy" });
                return;
            }
            recent.Add(now);
            _sessions[id] = new LinkSession { VerifierHash = verifierHash.ToLowerInvariant(), LoopbackPort = port, CreatedUtc = now, Address = address };
        }
        Json(response, 200, new { session = id, url = _config.PublicBase() + "api/link/start?session=" + Uri.EscapeDataString(id) });
    }

    private void StartSession(HttpListenerRequest request, HttpListenerResponse response)
    {
        var id = request.QueryString["session"];
        bool known;
        lock (_sessions)
        {
            known = id != null && _sessions.TryGetValue(id, out var session) && session.State == SessionState.Pending;
        }
        if (!known || _oauth == null)
        {
            Html(response, 404, "Lien expiré", "Ce lien n'est plus valable. Relance la liaison depuis le launcher.");
            return;
        }
        response.StatusCode = 302;
        response.RedirectLocation = _oauth.AuthorizeUrl(id!);
    }

    private async Task Callback(HttpListenerRequest request, HttpListenerResponse response)
    {
        var id = request.QueryString["state"];
        var code = request.QueryString["code"];
        LinkSession? session = null;
        lock (_sessions)
        {
            // Claimed under the lock, so a second callback for the same session finds it busy.
            if (id != null && _sessions.TryGetValue(id, out var found) && found.State == SessionState.Pending)
            {
                found.State = SessionState.InProgress;
                session = found;
            }
        }
        if (session == null || _oauth == null)
        {
            Html(response, 404, "Lien expiré", "Ce lien n'est plus valable. Relance la liaison depuis le launcher.");
            return;
        }

        if (code == null) _backend.Log($"Discord link cancelled: {request.QueryString["error"] ?? "no code"}");
        var user = code == null ? null : ExchangeOverride != null ? await ExchangeOverride(code) : await _oauth.ExchangeAsync(code, _backend.Log);
        if (user == null)
        {
            lock (_sessions) session.State = SessionState.Failed;
            Html(response, 400, "Liaison annulée", "Discord n'a pas confirmé la connexion. Tu peux fermer cet onglet et réessayer depuis le launcher.");
            return;
        }

        string handoff;
        lock (_sessions)
        {
            session.DiscordId = user.Value.Id;
            session.Name = user.Value.Name;
            session.Handoff = handoff = GateStore.RandomToken();
            session.State = SessionState.Authorized;
        }

        // Only the launcher listening on this computer's loopback learns the handoff code.
        response.StatusCode = 302;
        response.RedirectLocation = $"http://127.0.0.1:{session.LoopbackPort}/eos-link?handoff={Uri.EscapeDataString(handoff)}";
    }

    private async Task Poll(HttpListenerRequest request, HttpListenerResponse response)
    {
        var body = await ReadBody(request);
        var id = body?["session"]?.ToString();
        var verifier = body?["verifier"]?.ToString();
        var handoff = body?["handoff"]?.ToString();
        if (id == null || verifier == null)
        {
            Json(response, 400, new { error = "bad-request" });
            return;
        }

        LinkSession? session;
        bool collected = false;
        lock (_sessions)
        {
            Prune(DateTime.UtcNow);
            session = _sessions.TryGetValue(id, out var found) && FixedEquals(found.VerifierHash, GateStore.Hash(verifier)) ? found : null;
            if (session?.State == SessionState.Failed) _sessions.Remove(id);
            if (session?.State == SessionState.Authorized && handoff != null && FixedEquals(session.Handoff, handoff))
            {
                _sessions.Remove(id);
                collected = true;
            }
        }

        if (session == null)
        {
            Json(response, 404, new { status = "expired" });
            return;
        }
        if (session.State == SessionState.Failed)
        {
            Json(response, 200, new { status = "failed" });
            return;
        }
        if (!collected)
        {
            Json(response, 200, new { status = "pending" });
            return;
        }

        var token = _store.IssueToken(session.DiscordId, session.Name, DateTime.UtcNow);
        _backend.Audit($"Launcher linked to Discord {session.DiscordId} ({session.Name}) from {session.Address}.");
        _backend.NotifyLinked(session.DiscordId);
        Json(response, 200, new { status = "linked", token, discordName = session.Name });
    }

    private void Prune(DateTime now)
    {
        foreach (var key in _sessions.Where(s => now - s.Value.CreatedUtc > SessionLifetime).Select(s => s.Key).ToList()) _sessions.Remove(key);
        foreach (var (address, times) in _sessionsByAddress.ToList())
        {
            times.RemoveAll(t => now - t > AddressWindow);
            if (times.Count == 0) _sessionsByAddress.Remove(address);
        }
    }

    private bool LauncherIsRecentEnough(HttpListenerRequest request)
    {
        if (_minLauncherVersion == null) return true;
        var sent = ParseVersion(request.Headers[LauncherVersionHeader]) ?? new Version(0, 0, 0);
        return sent >= _minLauncherVersion;
    }

    /// <summary>"1.2.3" or "1.2.3-beta.1" as 1.2.3; anything else as null.</summary>
    public static Version? ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var core = value.Trim().Split('-', '+')[0];
        return Version.TryParse(core, out var version) && core.Count(c => c == '.') == 2 ? version : null;
    }

    /// <summary>
    /// The address a request came from. Behind the reverse proxy on this machine, that is the last
    /// entry the proxy put in X-Forwarded-For; a request that did not come through loopback is taken
    /// at its socket address, so a forged header from outside counts for nothing.
    /// </summary>
    private static string ClientAddress(HttpListenerRequest request)
    {
        var remote = request.RemoteEndPoint?.Address;
        if (remote == null) return "unknown";
        if (!IPAddress.IsLoopback(remote)) return remote.ToString();
        var forwarded = request.Headers["X-Forwarded-For"];
        var last = forwarded?.Split(',').Select(part => part.Trim()).LastOrDefault(part => part.Length > 0);
        return last != null && IPAddress.TryParse(last, out var parsed) ? parsed.ToString() : remote.ToString();
    }

    private static bool FixedEquals(string a, string b)
    {
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    }

    public static string MembershipName(MembershipStatus status) => status switch
    {
        MembershipStatus.Whitelisted => "whitelisted",
        MembershipStatus.MemberWithoutRole => "not-whitelisted",
        MembershipStatus.NotMember => "not-member",
        _ => "discord-unavailable"
    };

    private static string? BearerToken(HttpListenerRequest request)
    {
        var header = request.Headers["Authorization"];
        if (header == null || !header.StartsWith("Bearer ", StringComparison.Ordinal)) return null;
        var token = header["Bearer ".Length..].Trim();
        return token.Length is > 0 and < 256 ? token : null;
    }

    private static async Task<JObject?> ReadBody(HttpListenerRequest request)
    {
        if (request.ContentLength64 > MaxBodyBytes) return null;
        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
        var buffer = new char[MaxBodyBytes + 1];
        var read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
        if (read > MaxBodyBytes) return null;
        try
        {
            return JObject.Parse(new string(buffer, 0, read));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Json(HttpListenerResponse response, int status, object body)
    {
        Write(response, status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(body)));
    }

    private static void Html(HttpListenerResponse response, int status, string title, string message)
    {
        var page = $"<!doctype html><html lang=\"fr\"><meta charset=\"utf-8\"><title>{WebUtility.HtmlEncode(title)}</title>"
                   + "<body style=\"font-family:sans-serif;background:#18181b;color:#e4e4e7;display:grid;place-items:center;height:100vh;margin:0\">"
                   + $"<div style=\"text-align:center\"><h1>{WebUtility.HtmlEncode(title)}</h1><p>{WebUtility.HtmlEncode(message)}</p></div></body></html>";
        Write(response, status, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(page));
    }

    private static void Write(HttpListenerResponse response, int status, string contentType, byte[] bytes)
    {
        response.StatusCode = status;
        response.ContentType = contentType;
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes, 0, bytes.Length);
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
            // already closed
        }
    }
}
