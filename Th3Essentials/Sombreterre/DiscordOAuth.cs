using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Th3Essentials.Sombreterre;

/// <summary>
/// The authorization-code flow against Discord, server side only: the client secret is used here
/// and nowhere else. Only the "identify" scope is asked for; guild membership and roles come from
/// the bot, which already sees them.
/// </summary>
public class DiscordOAuth
{
    private static readonly HttpClient Http = CreateClient();

    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _redirectUri;

    public DiscordOAuth(string clientId, string clientSecret, string redirectUri)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _redirectUri = redirectUri;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // Discord's API refuses requests without a User-Agent; this is the form its docs ask for.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DiscordBot (https://gitlab.com/th3dilli_vintagestory/th3essentials, 1.0)");
        return client;
    }

    public string AuthorizeUrl(string state)
    {
        return "https://discord.com/oauth2/authorize?response_type=code&scope=identify&prompt=consent"
               + "&client_id=" + Uri.EscapeDataString(_clientId)
               + "&redirect_uri=" + Uri.EscapeDataString(_redirectUri)
               + "&state=" + Uri.EscapeDataString(state);
    }

    /// <summary>
    /// The Discord user behind an authorization code, or why there is none. The reason names the
    /// step, the HTTP status and Discord's own error code, never a secret or a token.
    /// </summary>
    public async Task<(ulong Id, string Name)?> ExchangeAsync(string code, Action<string> fail)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _redirectUri,
            ["client_id"] = _clientId,
            ["client_secret"] = _clientSecret
        });

        using var tokenResponse = await Http.PostAsync("https://discord.com/api/oauth2/token", form);
        var tokenBody = await tokenResponse.Content.ReadAsStringAsync();
        if (!tokenResponse.IsSuccessStatusCode)
        {
            fail($"Discord token exchange refused: HTTP {(int)tokenResponse.StatusCode} {ErrorCode(tokenBody)}");
            return null;
        }
        var accessToken = Parse(tokenBody)?["access_token"]?.ToString();
        if (string.IsNullOrEmpty(accessToken))
        {
            fail("Discord token exchange answered without an access token");
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/users/@me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var userResponse = await Http.SendAsync(request);
        var userBody = await userResponse.Content.ReadAsStringAsync();
        if (!userResponse.IsSuccessStatusCode)
        {
            fail($"Discord user lookup refused: HTTP {(int)userResponse.StatusCode} {ErrorCode(userBody)}");
            return null;
        }

        var user = Parse(userBody);
        if (user == null || !ulong.TryParse(user["id"]?.ToString(), out var id))
        {
            fail("Discord user lookup answered without a user id");
            return null;
        }
        var name = user["global_name"]?.Type == JTokenType.String ? user["global_name"]!.ToString() : user["username"]?.ToString() ?? "";
        return (id, name);
    }

    private static JObject? Parse(string body)
    {
        try
        {
            return JObject.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Discord's short error code ("invalid_client", "invalid_grant"...), or the first characters of a non-JSON answer.</summary>
    private static string ErrorCode(string body)
    {
        var json = Parse(body);
        var error = json?["error"]?.ToString();
        if (!string.IsNullOrEmpty(error)) return error;
        var message = json?["message"]?.ToString();
        if (!string.IsNullOrEmpty(message)) return message;
        var text = body.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return text.Length > 120 ? text[..120] : text;
    }
}
