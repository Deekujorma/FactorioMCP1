# Screenshots: local and LAN setup

The MCP server stays **stdio**, next to the MCP client/AI agent. Gameplay and screenshot requests still use **RCON TCP**. Only remote screenshot bytes use a separate, small .NET 9 HTTP helper on the graphical Factorio machine. No SSH, VPN, or HTTP MCP server is needed.

```text
AI/MCP client --stdio--> FactorioMCP --RCON--> graphical Factorio
                              |                   |
local:                        +-- script-output <-+
remote:                       +-- HTTP + token --> helper -- script-output
```

## Install and build

Use the same PR branch on both machines. The commands below assume Windows checkout `C:\FactorioMCP1`, Linux checkout `~/FactorioMCP1`, Windows IP `192.168.1.10`, and Linux IP `192.168.1.20`. Substitute your actual paths/IPs. Keep tokens and passwords out of Git.

### Windows (PowerShell)

Install the .NET 9 SDK if necessary, then open a new terminal:

```powershell
winget install --id Microsoft.DotNet.SDK.9 --exact
```

```powershell
Set-Location C:\FactorioMCP1
git switch fix/factorio-2.1-inventory-compatibility
git pull --ff-only
dotnet build FactorioMCP.sln -c Release
```

### Omarchy / Arch Linux (Bash)

Install a user-local .NET 9 SDK so these instructions do not depend on the current Arch package's .NET major version:

```bash
sudo pacman -S --needed git curl icu openssl zlib
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/factorio-dotnet-install.sh
bash /tmp/factorio-dotnet-install.sh --channel 9.0 --install-dir "$HOME/.dotnet"
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
# Repeat the two exports in future terminals, or add them to ~/.bashrc.
git clone --branch fix/factorio-2.1-inventory-compatibility https://github.com/Deekujorma/FactorioMCP1.git "$HOME/FactorioMCP1"
cd "$HOME/FactorioMCP1"
dotnet build FactorioMCP.sln -c Release
```

For an existing checkout, use `cd ~/FactorioMCP1`, `git switch fix/factorio-2.1-inventory-compatibility`, and `git pull --ff-only` instead of cloning. Do not switch branches over unrelated uncommitted changes.

## Graphical Factorio and RCON

Start/join the graphical game before starting MCP. Steam installations require Steam running. For example, **in a separate game terminal**, host an existing save with RCON enabled:

Windows PowerShell (adjust the executable/save paths):

```powershell
$Factorio = 'D:\Steam\steamapps\common\Factorio\bin\x64\factorio.exe'
$Save = "$env:APPDATA\Factorio\saves\your-save.zip"
$RconPassword = Read-Host 'RCON password'
& $Factorio --host $Save --rcon-bind 127.0.0.1:27015 --rcon-password $RconPassword
```

Omarchy Bash (default Steam installation):

```bash
read -rsp 'RCON password: ' RCON_PASSWORD; echo
"$HOME/.local/share/Steam/steamapps/common/Factorio/bin/x64/factorio" \
  --host "$HOME/.factorio/saves/your-save.zip" \
  --rcon-bind 127.0.0.1:27015 --rcon-password "$RCON_PASSWORD"
```

For LAN use, change **the game's** `--rcon-bind` to its actual LAN IP: `192.168.1.20:27015` on the example Linux game, or `192.168.1.10:27015` on Windows. This flag was checked against Factorio 2.1.24's `--help`. Restrict RCON in the firewall just like the helper. RCON passwords can be visible to local process-inspection tools because Factorio accepts them as command-line arguments.

A headless-only instance **cannot produce screenshots**. The helper cannot add rendering to it. The selected connected player must have a graphical instance; point the helper at that instance's output directory. A headless server plus graphical multiplayer client is not verified by this change; do not assume files will appear on the server.

## A. Windows game + Windows MCP, same host

No helper or screenshot token is needed. In the MCP terminal:

```powershell
Set-Location C:\FactorioMCP1
$env:FACTORIO_SCREENSHOT_TRANSPORT = 'local'
$env:FACTORIO_RCON_HOST = '127.0.0.1'
$env:FACTORIO_RCON_PORT = '27015'
$env:FACTORIO_RCON_PASSWORD = Read-Host 'RCON password'
# Optional, only for custom user-data locations/mounted folders:
# $env:FACTORIO_SCRIPT_OUTPUT_DIR = 'C:\custom-factorio\script-output'
dotnet .\FactorioMCP\bin\Release\net9.0\FactorioMCP.dll
```

Default image directory: `%APPDATA%\Factorio\script-output`.

## B. Linux game + Linux MCP, same host

No helper or screenshot token is needed:

```bash
cd "$HOME/FactorioMCP1"
export FACTORIO_SCREENSHOT_TRANSPORT=local
export FACTORIO_RCON_HOST=127.0.0.1 FACTORIO_RCON_PORT=27015
read -rsp 'RCON password: ' FACTORIO_RCON_PASSWORD; echo
export FACTORIO_RCON_PASSWORD
# Optional: export FACTORIO_SCRIPT_OUTPUT_DIR=/path/to/custom/script-output
dotnet FactorioMCP/bin/Release/net9.0/FactorioMCP.dll
```

Default image directory: `~/.factorio/script-output`, **not** `~/.config/Factorio/script-output`. Portable/custom/Flatpak installations may need an override.

## C. Linux game + Windows MCP over LAN

On the **Omarchy game laptop**, run the helper in a separate terminal:

```bash
cd "$HOME/FactorioMCP1"
export FACTORIO_SCRIPT_OUTPUT_DIR="$HOME/.factorio/script-output"
export FACTORIO_SCREENSHOT_TRANSPORT=local
export FACTORIO_SCREENSHOT_LISTEN=http://192.168.1.20:8088
export FACTORIO_SCREENSHOT_ALLOW_LAN=true
export FACTORIO_SCREENSHOT_TOKEN="$(openssl rand -hex 32)"
# Copy this token securely to the MCP machine. Do not commit it or share logs containing it.
printf '%s\n' "$FACTORIO_SCREENSHOT_TOKEN"
dotnet FactorioMCP.ScreenshotHelper/bin/Release/net9.0/FactorioMCP.ScreenshotHelper.dll
```

On the **Windows MCP desktop**:

```powershell
Set-Location C:\FactorioMCP1
$env:FACTORIO_RCON_HOST = '192.168.1.20'
$env:FACTORIO_RCON_PORT = '27015'
$env:FACTORIO_RCON_PASSWORD = Read-Host 'RCON password'
$env:FACTORIO_SCREENSHOT_TRANSPORT = 'http'
$env:FACTORIO_SCREENSHOT_ENDPOINT = 'http://192.168.1.20:8088'
$env:FACTORIO_SCREENSHOT_TOKEN = Read-Host 'Paste helper token'
dotnet .\FactorioMCP\bin\Release\net9.0\FactorioMCP.dll
```

Do not set the MCP machine's local directory to a Linux path. The helper, not Windows, resolves the game's directory.

## D. Windows game + Linux MCP over LAN

On the **Windows game machine**, in a separate helper terminal:

```powershell
Set-Location C:\FactorioMCP1
$env:FACTORIO_SCRIPT_OUTPUT_DIR = "$env:APPDATA\Factorio\script-output"
$env:FACTORIO_SCREENSHOT_TRANSPORT = 'local'
$env:FACTORIO_SCREENSHOT_LISTEN = 'http://192.168.1.10:8088'
$env:FACTORIO_SCREENSHOT_ALLOW_LAN = 'true'
$env:FACTORIO_SCREENSHOT_TOKEN = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
# Copy securely to the MCP machine; this prints a secret.
Write-Output $env:FACTORIO_SCREENSHOT_TOKEN
dotnet .\FactorioMCP.ScreenshotHelper\bin\Release\net9.0\FactorioMCP.ScreenshotHelper.dll
```

On the **Linux MCP machine**:

```bash
cd "$HOME/FactorioMCP1"
export FACTORIO_RCON_HOST=192.168.1.10 FACTORIO_RCON_PORT=27015
read -rsp 'RCON password: ' FACTORIO_RCON_PASSWORD; echo
export FACTORIO_RCON_PASSWORD
export FACTORIO_SCREENSHOT_TRANSPORT=http
export FACTORIO_SCREENSHOT_ENDPOINT=http://192.168.1.10:8088
read -rsp 'Paste helper token: ' FACTORIO_SCREENSHOT_TOKEN; echo
export FACTORIO_SCREENSHOT_TOKEN
dotnet FactorioMCP/bin/Release/net9.0/FactorioMCP.dll
```

For normal operation, configure your MCP client to launch that same `dotnet .../FactorioMCP.dll` command as a **stdio** server, with the same environment. Running it manually will wait for JSON-RPC on stdin; it is not a web UI. Stop the helper with **Ctrl+C** (SIGTERM also works on Linux).

## Configuration reference

| Environment variable | Where | Default / meaning |
|---|---|---|
| `FACTORIO_SCREENSHOT_TRANSPORT` | MCP | `local` or `http`; local needs no helper. Keep `local` in the helper's environment. |
| `FACTORIO_SCRIPT_OUTPUT_DIR` | Local MCP / helper | Actual `script-output` directory; Windows `%APPDATA%/Factorio/script-output`, Linux `~/.factorio/script-output`. Supports a locally mounted remote folder in local mode. Not used to find files on the HTTP MCP host. |
| `FACTORIO_SCREENSHOT_ENDPOINT` | HTTP MCP | Required HTTP(S) origin, e.g. `http://192.168.1.20:8088`; no path prefix, query, credentials, or fragment. |
| `FACTORIO_SCREENSHOT_TOKEN` | HTTP MCP + helper | Required matching bearer token, at least 32 non-whitespace characters. Required even for a loopback helper. Independent of the RCON password. |
| `FACTORIO_SCREENSHOT_LISTEN` | Helper | `http://127.0.0.1:8088`; explicit IP address and port, not a hostname. Native helper listens over HTTP. |
| `FACTORIO_SCREENSHOT_ALLOW_LAN` | Helper | Unset/false. Must be exactly `true` to bind a non-loopback IP (including `0.0.0.0`). Prefer one LAN interface. |
| `FACTORIO_SCREENSHOT_TIMEOUT_SECONDS` | MCP + helper | `15`, range `1..120`. MCP retrieval deadline, including polling/network reads; helper request deadline. Does not replace RCON's existing timeouts. |
| `FACTORIO_SCREENSHOT_MAX_BYTES` | MCP + helper | `33554432` (32 MiB), range `1024..67108864`. Each side enforces its own limit; keep them aligned. |
| `FACTORIO_SCREENSHOT_PLAYER` | MCP | Unset: first connected player. Otherwise a positive Factorio player index, used for focus and `by_player`. Select the player on the helper's graphical machine. |
| `FACTORIO_RCON_HOST` | MCP | `127.0.0.1`; set to the game host's LAN IP for remote deployments. |
| `FACTORIO_RCON_PORT` | MCP | `27015`. |
| `FACTORIO_RCON_PASSWORD` | MCP | Existing default `mypassword`; explicitly use a strong matching password instead. |

Screenshot dimensions are limited to **4096 × 4096** before decoding. Returned MCP images retain the existing **JPEG quality 80 / maximum 512-pixel side** optimization. The helper also limits connections/concurrent decodes, request body size, header time, and keep-alive time. Metadata is skipped and decoder allocation is bounded; these are not substitutes for patching image-library vulnerabilities.

## LAN firewall and security

Allow inbound **TCP 8088** (helper) and **TCP 27015** (RCON) on the **game host only**, restricted to the MCP host's IP. Do not open either port on your router or expose them to the Internet. No inbound helper rule is needed on the MCP-only host. Game multiplayer traffic, if needed, is separate.

Windows game host, elevated PowerShell (example Linux MCP at `.20`):

```powershell
New-NetFirewallRule -DisplayName 'Factorio screenshot helper from MCP' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 8088 -RemoteAddress 192.168.1.20 -Profile Private
New-NetFirewallRule -DisplayName 'Factorio RCON from MCP' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 27015 -RemoteAddress 192.168.1.20 -Profile Private
```

Omarchy game host, **if UFW is your active firewall** (example Windows MCP at `.10`):

```bash
sudo ufw allow from 192.168.1.10 to any port 8088 proto tcp
sudo ufw allow from 192.168.1.10 to any port 27015 proto tcp
sudo ufw status verbose
```

If UFW is not installed/active, apply equivalent source-IP rules to your existing nftables/firewalld configuration. Do not install/enable a second firewall blindly, or leave an older broad allow rule in place. Confirm the Windows network profile is Private before using the example rules.

HTTP bearer tokens and RCON are **not encrypted**. Use this only on a trusted LAN; for untrusted networks, terminate TLS at a configured reverse proxy (MCP accepts an HTTPS origin) and secure RCON separately. Run the helper as an unprivileged user. Only trusted Factorio should write its configured directory; this is not a sandbox against hostile local writers racing file operations.

The helper has no directory listing, upload, delete, or arbitrary-path endpoint. It accepts only `GET /screenshots/<32 lowercase hex characters>`, mapping to `ai-player/vision-<id>.png` beneath the configured directory. It rejects symlinks/junctions in the served subtree, checks PNG validity and size, and sends `Cache-Control: no-store`. HTTP redirects are not followed by MCP. Tokens are checked before file access.

## Correctness and troubleshooting

- Each capture uses a fresh GUID filename; no fallback to `vision.png` or any older image. Missing/incomplete files are polled until the retrieval deadline. Authentication, network, permission and size failures produce explicit diagnostics.
- When retrieval fails, the tool returns **`Image unavailable: ...` plus the original structured Map Legend**, not a stale image. Caller cancellation remains cancellation. An RCON failure before a legend exists cannot preserve a legend that was never received.
- A successful call returns a real MCP image content block plus text legend. The command queue covers capture, retrieval, and optimization so requests cannot interleave within the MCP process.
- Factorio [2.1.24 `take_screenshot`](https://lua-api.factorio.com/2.1.24/classes/LuaGameScript.html#take_screenshot) saves in the **game instance's user-data/script-output**, not the MCP directory. `player` controls focus; `by_player` selects the player whose instance should capture. `force_render=true` requests rendering at the end of the update (not guaranteed for catching-up multiplayer clients).
- [`set_wait_for_screenshots_to_finish`](https://lua-api.factorio.com/2.1.24/classes/LuaGameScript.html#set_wait_for_screenshots_to_finish) flushes queued writes; it does **not** prove a headless/other peer rendered the requested file. File retrieval/validation is the success check. These API behaviors were checked against the official 2.1.24 runtime JSON, not a successful live cross-host run.
- Overlays survive asynchronous rendering, expire after 600 game ticks, and are cleared before the next capture. Do not run multiple independent MCP servers controlling the same screenshot player concurrently.
- `401`: token mismatch/missing. `403`: directory access denied or prohibited symlink/junction. `404`: the specific capture is not present yet. `413`: byte/pixel limit. `422`: invalid/incomplete image. Timeout: check selected player, headless status, actual user-data directory, firewall, and endpoint.
- Files are **not automatically deleted**. Periodically remove old `ai-player/vision-*.png` files on the game machine when captures are idle; do not delete in-progress captures.
- ImageSharp **3.1.12** is retained from the existing project and has NuGet security advisories. Limits/authentication reduce exposure but do not fix that dependency. A dependency upgrade needs a separate licensing review: 3.2.0 introduces a Release-build license requirement.

## Validation

From the repository root, specify the solution because this checkout has both `.sln` and `.slnx` (bare build/test is ambiguous with the installed SDK):

```sh
dotnet build FactorioMCP.sln
dotnet test FactorioMCP.sln --filter 'Category!=Integration'
dotnet test FactorioMCP.sln
```

The automated screenshot tests use temporary directories, image fixtures, and a **real loopback Kestrel server**, never production saves. They cover both OS path defaults, local/HTTP retrieval, delayed/incomplete writes, missing files, authentication, traversal attempts, size/pixel limits, read failures, cancellation, timeouts/network failure, repeated unique captures, and MCP image/legend output.

For an **opt-in disposable graphical world**, build Debug, set the RCON/screenshot variables above, then run:

```sh
python scripts/run-screenshot-smoke.py
```

It checks two consecutive stdio MCP image+legend responses. It does not load saves or inject items; capture adds only temporary annotation overlays and PNG output. The repository's other `Category=Integration` tests require their pre-existing localhost RCON/password setup and include gameplay operations: do not point them at production saves.

**Verification limits:** Windows fixture/HTTP tests are not Linux or cross-host evidence. Actual Linux local, both LAN directions, and successful live graphical capture remain unverified here. The isolated Windows graphical launch attempt failed because Steam was not running. See the PR update for the latest exact build/test counts.
