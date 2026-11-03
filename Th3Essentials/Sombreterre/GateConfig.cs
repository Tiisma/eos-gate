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

    /// <summary>
    /// How long a player may stay connected without presenting a valid ticket, counted from the moment
    /// the server sees them playing. A heavily modded world can take the client a while to finish
    /// loading, and the client mod only sends once its connection is ready.
    /// </summary>
    public int TicketGraceSeconds = 60;

    /// <summary>
    /// How long a launch ticket stays valid after the launcher asked for it, until its first use. Long
    /// enough for a player to go through the game's settings before choosing Play in the main menu: an
    /// unused ticket only sits in the player's own game folder, and one Discord account holds one at a time.
    /// </summary>
    public int TicketLifetimeSeconds = 21600;

    /// <summary>
    /// After its first use, how long a ticket keeps letting the same game account back in (the game's
    /// Play button after leaving the server). Bound to that account: useless to anyone else.
    /// </summary>
    public int TicketSessionHours = 12;

    /// <summary>
    /// Put players in spectator mode until their ticket is accepted, and swallow their chat, so the
    /// grace period above cannot be used to play or grief. Their own game mode comes back on admission.
    /// </summary>
    public bool FreezeUntilAdmitted = true;

    /// <summary>After a refusal, how long the same game account is turned away straight on join.</summary>
    public int RefusalCooldownSeconds = 60;

    /// <summary>A launcher token unused for this many days stops working; the player links again.</summary>
    public int TokenIdleDays = 60;

    /// <summary>Discord link attempts one address may start per ten minutes.</summary>
    public int LinkSessionsPerAddress = 5;

    /// <summary>
    /// Oldest launcher version allowed to link, fetch the pack or get a ticket ("0.1.2"). Empty
    /// means no check. Launchers too old to say their version count as 0.0.0.
    /// </summary>
    public string MinLauncherVersion = "";

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
