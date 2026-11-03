using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Th3Essentials.Sombreterre;

/// <summary>
/// The launcher's side of the gate, over HTTP:
///
///   GET  /api/status                 players online, public
///   POST /api/link/session           {verifierHash} -> {session, url}: starts a Discord link
///   GET  /api/link/start?session=    redirects the browser to Discord
///   GET  /api/link/callback          Discord comes back here; issues the launcher token
///   POST /api/link/poll              {session, verifier} -> {status, token?, name?}
///   GET  /api/me                     (token) Discord account, membership, whitelist
///   POST /api/ticket                 (token, whitelisted) -> {ticket, expiresIn}
///   POST /api/logout                 (token) revokes it
///   GET  /api/pack.json              (token, whitelisted) the pack manifest
///   GET  /api/{file}.zip             (token, whitelisted) one private mod of that manifest
///
/// The launch flow never handles a Discord password or the OAuth client secret. The link session is
/// bound to the launcher that started it by a verifier only that launcher knows (PKCE style), so the
/// token goes nowhere else even if the browser link is shared.
/// </summary>
public class GateHttpApi : IDisposable
{
    public interface IBackend
    {
        MembershipStatus Membership(ulong discordId);
        (int Online, int Max) Status();
        PackBuilder.Result Pack();
        void Log(string message);
    }

    private class LinkSession
    {
        public string VerifierHash = "";
        public DateTime CreatedUtc;
        public string? Token;
        public string Name = "";
        public bool Failed;
    }

    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(10);
    private const int MaxBodyBytes = 4096;

    private readonly GateConfig _config;
    private readonly GateStore _store;
    private readonly TicketBook _tickets;
    private readonly IBackend _backend;
    private readonly DiscordOAuth? _oauth;
    private readonly Dictionary<string, LinkSession> _sessions = new();
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();

    public GateHttpApi(GateConfig config, GateStore store, TicketBook tickets, IBackend backend)
    {
        _config = config;
        _store = store;
        _tickets = tickets;
        _backend = backend;
        if (!string.IsNullOrEmpty(config.OAuthClientId) && !string.IsNullOrEmpty(config.OAuthClientSecret))
            _oauth = new DiscordOAuth(config.OAuthClientId, config.OAuthClientSecret, config.PublicBase() + "api/link/callback");
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

        switch (method, path)
        {
            case ("GET", "/api/status"):
            {
                var (online, max) = _backend.Status();
                Json(response, 200, new { online, max });
                return;
            }
            case ("POST", "/api/link/session"):
                await CreateSession(request, response);
                return;
            case ("GET", "/api/link/start"):
                StartSession(request, response);
                return;
            case ("GET", "/api/link/callback"):
                await Callback(request, response);
                return;
            case ("POST", "/api/link/poll"):
                await Poll(request, response);
                return;
        }

        // Everything below needs a launcher token.
        var token = BearerToken(request);
        var record = token == null ? null : _store.FindToken(token);
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
                Json(response, 200, new { ok = true });
                return;
        }

        if (membership != MembershipStatus.Whitelisted)
        {
            Json(response, 403, new { error = MembershipName(membership) });
            return;
        }

        if (method == "POST" && path == "/api/ticket")
        {
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
        var body = await ReadBody(request);
        var verifierHash = body?["verifierHash"]?.ToString();
        if (verifierHash == null || verifierHash.Length != 64 || !verifierHash.All(Uri.IsHexDigit))
        {
            Json(response, 400, new { error = "bad-request" });
            return;
        }

        var id = GateStore.RandomToken();
        lock (_sessions)
        {
            Prune();
            if (_sessions.Count > 500)
            {
                Json(response, 429, new { error = "busy" });
                return;
            }
            _sessions[id] = new LinkSession { VerifierHash = verifierHash.ToLowerInvariant(), CreatedUtc = DateTime.UtcNow };
        }
        Json(response, 200, new { session = id, url = _config.PublicBase() + "api/link/start?session=" + Uri.EscapeDataString(id) });
    }

    private void StartSession(HttpListenerRequest request, HttpListenerResponse response)
    {
        var id = request.QueryString["session"];
        bool known;
        lock (_sessions)
        {
            known = id != null && _sessions.ContainsKey(id);
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
        LinkSession? session;
        lock (_sessions)
        {
            session = id != null && _sessions.TryGetValue(id, out var found) && found.Token == null && !found.Failed ? found : null;
        }
        if (session == null || _oauth == null)
        {
            Html(response, 404, "Lien expiré", "Ce lien n'est plus valable. Relance la liaison depuis le launcher.");
            return;
        }

        // Discord sends the player back with ?error=access_denied when they press Cancel.
        if (code == null) _backend.Log($"Discord link cancelled: {request.QueryString["error"] ?? "no code"}");
        var user = code == null ? null : await _oauth.ExchangeAsync(code, _backend.Log);
        if (user == null)
        {
            lock (_sessions) session.Failed = true;
            Html(response, 400, "Liaison annulée", "Discord n'a pas confirmé la connexion. Tu peux fermer cet onglet et réessayer depuis le launcher.");
            return;
        }

        var token = _store.IssueToken(user.Value.Id, user.Value.Name, DateTime.UtcNow);
        lock (_sessions)
        {
            session.Token = token;
            session.Name = user.Value.Name;
        }
        Html(response, 200, "Compte Discord lié", "C'est fait ! Tu peux fermer cet onglet et retourner dans le launcher.");
    }

    private async Task Poll(HttpListenerRequest request, HttpListenerResponse response)
    {
        var body = await ReadBody(request);
        var id = body?["session"]?.ToString();
        var verifier = body?["verifier"]?.ToString();
        if (id == null || verifier == null)
        {
            Json(response, 400, new { error = "bad-request" });
            return;
        }

        LinkSession? session;
        lock (_sessions)
        {
            Prune();
            session = _sessions.TryGetValue(id, out var found) && found.VerifierHash == GateStore.Hash(verifier) ? found : null;
            // Handed over once: the session goes as soon as its token does.
            if (session?.Token != null || session?.Failed == true) _sessions.Remove(id);
        }

        if (session == null) Json(response, 404, new { status = "expired" });
        else if (session.Failed) Json(response, 200, new { status = "failed" });
        else if (session.Token == null) Json(response, 200, new { status = "pending" });
        else Json(response, 200, new { status = "linked", token = session.Token, discordName = session.Name });
    }

    private void Prune()
    {
        var now = DateTime.UtcNow;
        foreach (var key in _sessions.Where(s => now - s.Value.CreatedUtc > SessionLifetime).Select(s => s.Key).ToList()) _sessions.Remove(key);
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
