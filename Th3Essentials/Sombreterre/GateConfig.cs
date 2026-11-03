using System.Collections.Generic;

namespace Th3Essentials.Sombreterre;

/// <summary>
/// Settings of the Sombreterre gate, stored in ModConfig/SombreterreGate.json.
/// The Discord bot token itself stays in Th3Config.json (DiscordConfig.Token).
/// </summary>
public class GateConfig
{
    /// <summary>Master switch. Off: nobody is gated and the HTTP API does not start.</summary>
    public bool Enabled = false;

    /// <summary>Discord role that whitelists a linked player. Losing it kicks them.</summary>
    public ulong WhitelistRoleId = 0;

    /// <summary>Discord OAuth2 application id (same application as the bot).</summary>
    public string? OAuthClientId = null;

    /// <summary>Discord OAuth2 client secret. Never leaves the server.</summary>
    public string? OAuthClientSecret = null;

    /// <summary>
    /// Address the HTTP API listens on. Loopback by default; anything else needs a reverse proxy
    /// (and, on Windows, a URL ACL for HttpListener).
    /// </summary>
    public string HttpPrefix = "http://127.0.0.1:42421/";

    /// <summary>
    /// Public base address players reach the API at, used to build the OAuth2 redirect URI.
    /// Empty means <see cref="HttpPrefix"/>. Must end with "/".
    /// </summary>
    public string PublicBaseUrl = "";

    /// <summary>How long a player may stay connected without presenting a valid ticket.</summary>
    public int TicketGraceSeconds = 20;

    /// <summary>How long a launch ticket stays valid after the launcher asked for it.</summary>
    public int TicketLifetimeSeconds = 180;

    /// <summary>Player UIDs never gated (staff accounts, test accounts).</summary>
    public List<string> BypassPlayerUids = new();

    /// <summary>Mod ids the launcher takes from the ModDB instead of from this server.</summary>
    public List<string> ModDbModIds = new();

    /// <summary>
    /// Extra folders whose .zip mods are added to the pack, for client-only mods the server does not
    /// load itself (the gate's own client mod, shaders...). Relative paths are resolved from the
    /// server data folder.
    /// </summary>
    public List<string> AdditionalPackFolders = new() { "SombreterrePack" };

    /// <summary>Pack name shown in the manifest.</summary>
    public string PackName = "Sombreterre";

    public string PublicBase() => string.IsNullOrWhiteSpace(PublicBaseUrl) ? HttpPrefix : PublicBaseUrl;
}
