# Sombreterre gate

This repository is a fork of [Th3Essentials](https://gitlab.com/th3dilli_vintagestory/th3essentials) (MIT) for The Exiles of Shadowland roleplay server. Everything Th3Essentials does is kept. The fork adds a **gate** that works with [The Exiles of Shadowland launcher](https://github.com/Tiisma/eos-launcher).

## What the gate does

- **Discord role = whitelist.** A player may stay connected only if their Discord account is on the server's guild and holds the whitelist role. Removing the role, kicking or banning them on Discord, disconnects them if they are online.
- **No command to type.** The player clicks "Link my Discord" once in the launcher. On each Play, the launcher fetches a single-use launch ticket and leaves it in the game folder. The `sombreterregate` client mod sends it to the server on join. The first valid ticket links the game account to that Discord account.
- **Nothing to do before admission.** Until their ticket is accepted, a player is a spectator and their chat reaches nobody; their own game mode comes back on admission. A refused account is turned away on join for `RefusalCooldownSeconds`.
- **A main menu for this server.** Once a player has joined, the game's main menu only offers **Play** (back to the server, no launcher needed), **Settings** and **Quit**, over the server's own backgrounds, one picked at random each time. Single player, multiplayer, the server dashboard, mods and credits are gone.
- **Players online** on the bot's status (inherited from Th3Essentials) and on the launcher's home screen.
- **The pack comes from the server.** The launcher's manifest is generated from the server's `Mods` folder plus `SombreterrePack/`. Server-side mods are never listed or served. Mods named in `ModDbModIds` are taken from the ModDB; every other client or universal `.zip` is served by the gate, to whitelisted launchers only.

How it is protected:

- The game identity is the one the server authenticates itself; the Discord identity comes from Discord's OAuth2 flow, whose client secret never leaves the server.
- **A shared link gives nothing away.** After Discord, the browser is sent back to a one-time listener on the launcher's own loopback address with a handoff code (RFC 8252). The token is only minted for the launcher holding both that code and its verifier, so sending someone the link cannot steal their account. The bot also DMs the Discord account each time a launcher is linked to it.
- A player waiting for their ticket is in spectator mode at move speed zero and put back on their spot if they move anyway, then admitted exactly where they joined, in their own mode.
- A ticket expires after `TicketLifetimeSeconds` if unused, and one Discord account holds one at a time, so tickets cannot be stockpiled for others. Its first use binds it to the game account that presented it, which may then come back with it for `TicketSessionHours` (the menu's Play button); a ticket read off the (unencrypted) game connection is worthless to anyone else, since the server authenticates the game account itself.
- Links are one to one and never replaced automatically.
- Launcher tokens are stored server side as SHA-256 digests only, client side encrypted with the OS keyring, and expire after `TokenIdleDays` without use. `/gate revoke` and `/gate unlink` revoke them.
- Link attempts are limited per address; `/gate lockdown true` stops new links, tickets and downloads at once.
- Launchers older than `MinLauncherVersion` are refused.
- Every link, first join, refusal and revocation is written to the server log and, when Th3Essentials has an `AdminLogChannelId`, to that Discord channel.
- Mods from the ModDB are pinned to the SHA-256 of the archive the server runs.

What it does not protect against: whoever controls the server machine controls the mods every player installs, and the pack's SHA-256 values are computed by that same machine. Treat its security (below) as the security of every player's computer.

## Code

| Path | Role |
| --- | --- |
| `Th3Essentials/Sombreterre/SombreterreGateSystem.cs` | Mod system: join check, Discord role events, `/gate` command, pack rebuild |
| `Th3Essentials/Sombreterre/GateDecision.cs` | Pure decision for one player and one ticket |
| `Th3Essentials/Sombreterre/GateStore.cs` | Persistent links and token digests (`ModData/SombreterreGate-data.json`) |
| `Th3Essentials/Sombreterre/TicketBook.cs` | In-memory launch tickets |
| `Th3Essentials/Sombreterre/GateHttpApi.cs` | HTTP API for the launcher |
| `Th3Essentials/Sombreterre/DiscordOAuth.cs` | Discord authorization-code exchange |
| `Th3Essentials/Sombreterre/PackBuilder.cs` | Pack manifest from the mod folders |
| `SombreterreGate.Client/` | Client mod: sends the ticket, trims the main menu, shows the server's backgrounds |
| `SombreterreGate.Startup/` | Startup hook shipped in the client mod: the server's main menu from the game's first screen |
| `Tests/SombreterreGate*.cs` | Unit tests and HTTP API tests |
| `SombreterreGate.Tests/` | Scenarios on an embedded Stratum server (Atlas): holding on the spot, refusal, cooldown, mode and position restore, whitelist |

## Setting it up

1. **Discord application** (https://discord.com/developers/applications):
   - Bot: copy the token. Under *Privileged Gateway Intents*, turn on **Server Members Intent** (the gate reads members and roles) and Message Content (Th3Essentials' chat relay).
   - OAuth2: copy the client id and client secret, and add the redirect `http://127.0.0.1:42421/api/link/callback` (or `<PublicBaseUrl>api/link/callback` once online).
   - Invite the bot to the guild.
2. **Server mods**: put `th3essentials_<version>.zip` in the server's `Mods` folder, and `sombreterregate_<version>.zip` in `<server data>/SombreterrePack/` (it is a client mod: the server does not load it, it only hands it to players).
3. Start the server once to create the configs, then stop it.
4. `ModConfig/Th3Config.json`: set `DiscordConfig.Token`, `DiscordConfig.GuildId` and `DiscordConfig.ChannelId` (see Th3Essentials' README).
5. `ModConfig/SombreterreGate.json`:

   ```json
   {
     "Enabled": true,
     "WhitelistRoleId": 123456789012345678,
     "OAuthClientId": "<client id>",
     "OAuthClientSecret": "<client secret>",
     "HttpPrefix": "http://127.0.0.1:42421/",
     "PublicBaseUrl": "",
     "TicketGraceSeconds": 60,
     "TicketLifetimeSeconds": 21600,
     "TicketSessionHours": 12,
     "FreezeUntilAdmitted": true,
     "AutoWhitelist": true,
     "RefusalCooldownSeconds": 60,
     "TokenIdleDays": 60,
     "LinkSessionsPerAddress": 5,
     "MinLauncherVersion": "0.1.2",
     "BypassPlayerUids": [],
     "ModDbModIds": [],
     "AdditionalPackFolders": ["SombreterrePack"],
     "PackName": "Sombreterre"
   }
   ```

   Keep this file private: it holds the OAuth client secret.
6. In `Th3Config.json`, set `DiscordConfig.AdminLogChannelId` to a staff-only channel: the gate's audit trail goes there.
7. Start the server. The log says `Launcher API listening on ...`.

### Staff commands (`controlserver` privilege)

| Command | Effect |
| --- | --- |
| `/gate reloadpack` | Rebuild the pack after changing mods |
| `/gate unlink <playeruid>` | Remove a game account's Discord link and revoke that Discord account's launcher tokens |
| `/gate revoke <discord user id>` | Revoke every launcher token of a Discord account; the player links again |
| `/gate lockdown true` / `false` | Stop or resume new links, tickets and pack downloads; players already in stay |

On Stratum, `ClientModPolicy` in `Data/stratum.json` decides which client-side mods a player may load. When it is enabled, it silently disables `sombreterregate` on the player's side, the ticket is never sent, and the player is refused after `TicketGraceSeconds` (their `client-main.log` then says `Server sends me channel name sombreterregate, but no client side mod registered it`). Keep the policy if you want it, and allow the gate's client mod, along with any other client-only mod of the pack:

```json
"ClientModPolicy": {
  "Enabled": true,
  "StrictWhitelist": true,
  "IncludeServerUniversalMods": true,
  "AllowModIds": ["sombreterregate"],
  "LogPolicyOnStartup": true
}
```

The server's own whitelist (`WhitelistMode`) should stay on: it stops anyone without the role at the door, including while the gate is down. With `AutoWhitelist` (on by default) it follows the Discord role by itself, so staff only give or take the role:

- The launcher names its game account with each ticket request. If the Discord account holds the role, that account goes on the whitelist before the game starts.
- Losing the role, leaving the guild, or `/gate unlink` takes it off and disconnects the player. Role changes made while the server was off are caught up once Discord's member list has loaded. While Discord is unavailable the list does not change.
- Entries for accounts no Discord account is tied to are never touched, nor are `BypassPlayerUids`, so staff can keep their own. An entry with the same name under another game account is left as it is and reported in the admin log.
- The account a launcher names is not proven until it joins with a ticket. A false one only puts that account on the whitelist; without a ticket the gate still turns it away. Set `MinLauncherVersion` to 0.1.6 so that every launcher names its account.

## The main menu and its backgrounds

The game runs no mod code at its main menu. The client mod therefore also ships a .NET startup hook, `SombreterreGate.Startup.dll`, in its zip under `native/` (a folder the game's mod loader skips). Launcher 0.1.5 and later copy it to `.eos-startup/` in the installation after each pack sync and start the game with `DOTNET_STARTUP_HOOKS` pointing at it, without connecting to the server: the game opens on the server's menu, and the player joins with **Play** when they are ready, after changing settings (and restarting for a new language) if they want to. The hook logs to `.eos-startup/startup.log`.

Without the hook (an older launcher), the client mod changes the menu once the player is on the server, and the changed menu shows when they leave it. Either way, if the game's menu ever changes in a way the mod does not recognise, it keeps the game's own menu and says so in its log.

The **Play** button reads the server address from the ticket file the launcher writes before each launch (launcher 0.1.3 and later); the client mod leaves that file in place. Without it, Play is not shown. Since the player may sit in the settings before choosing Play, `TicketLifetimeSeconds` defaults to 6 hours; an unused ticket only sits in that player's game folder.

To add backgrounds (screenshots of the server, for instance):

1. Save them as PNG, 1920×1080 or larger, in `SombreterreGate.Client/assets/sombreterregate/textures/gui/backgrounds/`.
2. Optionally list one in `backgroundPaths` in `SombreterreGate.Client/modinfo.json`: that one shows on the very first menu, before the mod has run.
3. Bump the version in that `modinfo.json`, rebuild and package the client mod with its `assets` folder (CI does it), replace the zip in `SombreterrePack/`, and run `/gate reloadpack`. Players get it on their next Play.

## Going online

`HttpListener` serves plain HTTP. For players outside this machine, put a reverse proxy with HTTPS in front of `HttpPrefix`, set `PublicBaseUrl` to its public address (ending with `/`), and update the launcher profile's `gateBaseUrl` and `packManifestUrl`. Keep `HttpPrefix` on loopback: the proxy is the only thing that should reach it.

The Early Access server uses [Caddy](https://caddyserver.com) on Windows, with the free name `eos-gate.duckdns.org` pointing at the server. Ports 80 and 443 belong to the host's Apache, which must keep running, so Caddy listens on **8443** only and proves the domain to Let's Encrypt through a DuckDNS TXT record (DNS challenge), which needs no port at all:

1. Download Caddy **with the DuckDNS module**: on https://caddyserver.com/download pick Windows amd64, search for and add the package `github.com/caddy-dns/duckdns`, then download. Save it as `C:\caddy\caddy.exe`. The plain Caddy build cannot do the DNS challenge.
2. Create `C:\caddy\Caddyfile` (no extension), with the DuckDNS token from the top of the DuckDNS page. The file stays on the server: the token controls the domain.

   ```
   {
   	auto_https disable_redirects
   }

   eos-gate.duckdns.org:8443 {
   	tls {
   		dns duckdns YOUR_DUCKDNS_TOKEN
   	}
   	reverse_proxy 127.0.0.1:42421 {
   		# HttpListener only answers requests addressed to its own prefix.
   		header_up Host 127.0.0.1:42421
   	}
   	log {
   		output file C:\caddy\logs\eos-gate.log {
   			roll_size 10MiB
   			roll_keep 10
   		}
   		format json
   	}
   }
   ```

   `disable_redirects` stops Caddy from opening port 80 for HTTP-to-HTTPS redirects, which would collide with Apache.
3. Open the firewall (PowerShell as administrator), and the same ports in the hosting provider's network firewall if it has one:

   ```powershell
   New-NetFirewallRule -DisplayName "EoS gate HTTPS" -Direction Inbound -Protocol TCP -LocalPort 8443 -Action Allow
   New-NetFirewallRule -DisplayName "Vintage Story TCP" -Direction Inbound -Protocol TCP -LocalPort 42420 -Action Allow
   New-NetFirewallRule -DisplayName "Vintage Story UDP" -Direction Inbound -Protocol UDP -LocalPort 42420 -Action Allow
   ```

4. Check that `C:\caddy\caddy.exe list-modules` lists `dns.providers.duckdns`, then try it in the foreground: `C:\caddy\caddy.exe run --config C:\caddy\Caddyfile`. The log should end with `certificate obtained successfully`. Once `https://eos-gate.duckdns.org:8443/api/status` answers in a browser, stop it and install it as a service:

   ```powershell
   sc.exe create caddy start= auto binPath= "C:\caddy\caddy.exe run --config C:\caddy\Caddyfile"
   sc.exe start caddy
   ```

5. In `SombreterreGate.json`, set `"PublicBaseUrl": "https://eos-gate.duckdns.org:8443/"`, and add `https://eos-gate.duckdns.org:8443/api/link/callback` to the Discord application's OAuth2 redirects.

## Securing the server machine

The gate is only as safe as the machine it runs on. On the Early Access host:

- **Secrets**: `ModConfig/Th3Config.json` (bot token), `ModConfig/SombreterreGate.json` (OAuth secret), `ModData/SombreterreGate-data.json` (links) and `C:\caddy\Caddyfile` (DuckDNS token) should be readable only by the account that runs the server and Caddy, and by administrators:

  ```powershell
  icacls "<file>" /inheritance:r /grant:r "Administrators:F" "SYSTEM:F" "<server account>:M"
  ```

  The server account must keep write access (`M`): the gate rewrites its config and its links, and a config it cannot write to stops the gate from starting.

  If a secret leaks: reset it (Discord bot token, OAuth client secret, DuckDNS token), restart, then `/gate revoke` the accounts involved.
- **Remote access**: RDP not exposed to the internet (VPN, or the host's allow-list of addresses), Windows and Apache patched. The firewall only opens 42420 (game) and 8443 (gate) for this project.
- **Accounts**: 2FA on the Discord application owner, every Discord administrator and moderator, GitHub and DuckDNS. Know who can give the whitelist role (*Manage Roles*, other bots) and who is in Th3Essentials' `ModerationRoles`: those slash commands can shut the server down or change whitelists. Keep a trusted second owner so the project does not rest on one person.
- **Backups** off the machine, with a restore actually tested.

## Building and testing

```bash
export VINTAGE_STORY_PRE="C:/Users/march/Documents/VintageStory/EoS-Server-Test/server"   # a Stratum server folder
export VINTAGE_STORY="$VINTAGE_STORY_PRE"
./build.sh                                   # Releases/th3essentials_<version>.zip
dotnet build SombreterreGate.Client -c Release
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Sombreterre"
dotnet test SombreterreGate.Tests            # embedded server scenarios
```

`Releases/` holds the packages and is git-ignored. For the client mod, zip the `.dll`, `modinfo.json`, the `native` folder (the startup hook, built along with the mod) and the `assets` folder of `SombreterreGate.Client/bin/Release/Mods/sombreterregate/`, with forward slashes in the paths (CI does it with `zip -r`; PowerShell's `Compress-Archive` writes backslashes).

## What has been verified

- With real players on the Early Access Stratum server: Discord linking, the pack, tickets, automatic linking of the game account and admission.
- On an embedded Stratum server (Atlas): spectator mode at speed zero, put back on the spot when moved, chat swallowed, refusal after the grace period, cooldown on retry, own mode, position and speed restored, whitelist entries following the role.
- Unit and HTTP tests: the loopback handoff (a shared link yields nothing), revocation, token expiry, one ticket per account, rate limits, lockdown, outdated launchers.
- Not yet observed with a real client: being unable to move while waiting, the release on admission, the whitelist reacting to live Discord role changes, the DM on linking, the admin channel audit, the role-loss kick.
- Th3Essentials' own date tests (`Tests/UnitTest1.cs`) fail on a machine with a non-US locale; that predates the fork.
