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
    /// Folder of main menu backgrounds (.png or .jpg) the launcher keeps in step on every player's machine,
    /// so pictures are added or removed with /gate reloadpack alone. Relative paths are resolved from the
    /// server data folder.
    /// </summary>
    public string BackgroundsFolder = "SombreterreBackgrounds";

    /// <summary>
    /// Folder of main menu texts: tips.txt (one tip per line, shown in turn at the bottom of the menu) and
    /// panel.txt (the panel on the right, in the game's rich text), each optionally per language
    /// (tips.fr.txt, panel.en.txt). Kept in step by the launcher; relative to the server data folder.
    /// </summary>
    public string MenuFolder = "SombreterreMenu";

    /// <summary>
    /// The title logo (.png, transparency kept) drawn in place of the Vintage Story one on players' main
    /// menu, kept in step by the launcher like the backgrounds. Relative to the server data folder; when the
    /// file is missing, players see the game's own logo.
    /// </summary>
    public string LogoFile = "SombreterreLogo.png";

    /// <summary>
    /// The Optimum release players run, through the launcher: a version such as "0.4.2" (up or down from
    /// what they have), "latest" for each new release as it comes out, or empty for the version built into
    /// the launcher. Changed with /gate reloadpack; players get it on their next Play.
    /// </summary>
    public string OptimumVersion = "";

    /// <summary>
    /// How long after entering the world a player may be disconnected. Disconnecting a client within
    /// about two seconds of that crashes it (TextureAtlasManager.RegenMipMaps), whoever does it. A
    /// refused player waits out the rest of this, held on the spot.
    /// </summary>
    public int RefusalDelaySeconds = 10;

    /// <summary>
    /// Keep the server's own whitelist in line with WhitelistRoleId: a player goes on it as soon as the
    /// launcher knows their game account and they hold the role, and comes off when they lose it.
    /// Entries for accounts no Discord account is tied to are left alone.
    /// </summary>
    public bool AutoWhitelist = true;

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

    /// <summary>
    /// Discord link attempts one address may start within ten minutes before it has to wait, so a player
    /// whose browser crashed can simply try again.
    /// </summary>
    public int LinkAttemptsBeforeWait = 3;

    /// <summary>The first wait after too many link attempts, in minutes. Each further wait doubles.</summary>
    public int LinkWaitMinutes = 5;

    /// <summary>The longest wait between link attempts, in minutes. An hour without attempts, or a link that goes through, starts over.</summary>
    public int LinkMaxWaitMinutes = 60;

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

    /// <summary>
    /// Folders whose .zip mods players may choose to add from the launcher (shaders...). The launcher
    /// keeps nothing else in a player's Mods folder. Stratum's ClientModPolicy is what the server itself
    /// enforces: with it on, list these mods in its AllowModIds too. Relative paths are resolved from
    /// the server data folder.
    /// </summary>
    public List<string> OptionalPackFolders = new() { "SombreterrePackOptional" };

    /// <summary>
    /// Accept the anonymous metrics players agreed to send from the launcher and the client mod, and record
    /// the server's own figures. Off: reports are refused and nothing is recorded.
    /// </summary>
    public bool MetricsEnabled = true;

    /// <summary>Days metrics are kept on the server; older daily files are deleted.</summary>
    public int MetricsRetentionDays = 30;

    /// <summary>Days between two metrics summaries posted on Discord. 0: no summary.</summary>
    public int MetricsSummaryDays = 7;

    /// <summary>Discord channel for the metrics summary. 0: Th3Essentials' admin log channel.</summary>
    public ulong MetricsChannelId = 0;

    /// <summary>Pack name shown in the manifest.</summary>
    public string PackName = "Sombreterre";

    public string PublicBase() => string.IsNullOrWhiteSpace(PublicBaseUrl) ? HttpPrefix : PublicBaseUrl;
}
