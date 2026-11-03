# Sombreterre gate

This repository is a local fork of [Th3Essentials](https://gitlab.com/th3dilli_vintagestory/th3essentials) (MIT) for the Sombreterre roleplay server. The upstream remote is named `upstream`; there is no `origin` yet. Everything Th3Essentials does is kept. The fork adds a **gate** that works with the [Sombreterre launcher](../sombreterre-launcher/docs/sombreterre.md).

## What the gate does

- **Discord role = whitelist.** A player may stay connected only if their Discord account is on the server's guild and holds the whitelist role. Removing the role, kicking or banning them on Discord, disconnects them if they are online.
- **No command to type.** The player clicks "Link my Discord" once in the launcher. On each Play, the launcher fetches a single-use launch ticket and leaves it in the game folder. The `sombreterregate` client mod sends it to the server on join. The first valid ticket links the game account to that Discord account.
- **Players online** on the bot's status (inherited from Th3Essentials) and on the launcher's home screen.
- **The pack comes from the server.** The launcher's manifest is generated from the server's `Mods` folder plus `SombreterrePack/`. Server-side mods are never listed or served. Mods named in `ModDbModIds` are taken from the ModDB; every other client or universal `.zip` is served by the gate, to whitelisted launchers only.

Why it is safe:

- The game identity is the one the server authenticates itself; the Discord identity comes from Discord's OAuth2 flow, whose client secret never leaves the server.
- A ticket is single use and expires after `TicketLifetimeSeconds`, so one read off the (unencrypted) game connection is worthless.
- Links are one to one and never replaced automatically (`/gate unlink <playeruid>` is the staff's way to move one).
- Launcher tokens are stored server side as SHA-256 digests only, and client side encrypted with the OS keyring.
- Starting the game without the launcher gets the player disconnected after `TicketGraceSeconds`.

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
| `SombreterreGate.Client/` | Client mod that sends the ticket |
| `Tests/SombreterreGate*.cs` | Unit tests and HTTP API tests |

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
     "TicketGraceSeconds": 20,
     "TicketLifetimeSeconds": 180,
     "BypassPlayerUids": [],
     "ModDbModIds": [],
     "AdditionalPackFolders": ["SombreterrePack"],
     "PackName": "Sombreterre"
   }
   ```

   Keep this file private: it holds the OAuth client secret.
6. Start the server. The log says `Launcher API listening on ...`. After changing mods, run `/gate reloadpack`.

The server's own whitelist (`WhitelistMode`) should stay off: the gate replaces it, and a native whitelist would turn new players away before they can link.

## Going online

`HttpListener` serves plain HTTP. For players outside this machine, put a reverse proxy with HTTPS in front of `HttpPrefix`, set `PublicBaseUrl` to its public address (ending with `/`), and update the launcher profile's `gateBaseUrl` and `packManifestUrl`. Keep `HttpPrefix` on loopback: the proxy is the only thing that should reach it.

The Early Access server uses [Caddy](https://caddyserver.com) on Windows, which obtains and renews a Let's Encrypt certificate by itself, with the free name `eos-gate.duckdns.org` pointing at the server:

1. Download Caddy for Windows amd64 from https://caddyserver.com/download and put `caddy.exe` in `C:\caddy\`.
2. Create `C:\caddy\Caddyfile`:

   ```
   eos-gate.duckdns.org {
       reverse_proxy 127.0.0.1:42421 {
           # HttpListener only answers requests addressed to its own prefix.
           header_up Host 127.0.0.1:42421
       }
   }
   ```

3. Open the firewall (PowerShell as administrator), and the same ports in the hosting provider's network firewall if it has one:

   ```powershell
   New-NetFirewallRule -DisplayName "Caddy HTTP/HTTPS" -Direction Inbound -Protocol TCP -LocalPort 80,443 -Action Allow
   New-NetFirewallRule -DisplayName "Vintage Story TCP" -Direction Inbound -Protocol TCP -LocalPort 42420 -Action Allow
   New-NetFirewallRule -DisplayName "Vintage Story UDP" -Direction Inbound -Protocol UDP -LocalPort 42420 -Action Allow
   ```

4. Try it in the foreground first: `C:\caddy\caddy.exe run --config C:\caddy\Caddyfile`. Once `https://eos-gate.duckdns.org/api/status` answers in a browser, stop it and install it as a service:

   ```powershell
   sc.exe create caddy start= auto binPath= "C:\caddy\caddy.exe run --config C:\caddy\Caddyfile"
   sc.exe start caddy
   ```

5. In `SombreterreGate.json`, set `"PublicBaseUrl": "https://eos-gate.duckdns.org/"`, and add `https://eos-gate.duckdns.org/api/link/callback` to the Discord application's OAuth2 redirects.

## Building and testing

```bash
export VINTAGE_STORY_PRE="C:/Users/march/Documents/VintageStory/VSLauncher/VSLGameVersions/1.22.7"
./build.sh                                   # Releases/th3essentials_<version>.zip
dotnet build SombreterreGate.Client -c Release
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Sombreterre"
```

`Releases/` holds the packages and is git-ignored. Zip `SombreterreGate.Client/bin/Release/Mods/sombreterregate/` (its `.dll` and `modinfo.json`) for the client mod.

## Not verified yet

- Checked on a local Stratum `v1.22.7-stratum.2` dedicated server: the mod builds against its DLLs and loads, the API listens, `/api/status` answers, a token is required for tickets and the pack, and without the bot connected tickets and the pack are refused (`discord-unavailable`). No game client has joined it yet.
- The join timing (ticket sent on the client's `LevelFinalize`, checked against `PlayerNowPlaying` plus the grace delay) is reasoned from the API, not observed.
- The Discord OAuth exchange and the role events have only been tested with a fake backend, not against Discord.
- Th3Essentials' own date tests (`Tests/UnitTest1.cs`) fail on a machine with a non-US locale; that predates the fork.
