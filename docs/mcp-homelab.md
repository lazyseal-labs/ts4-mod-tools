# Host the MCP tools on a Linux homelab

The MCP host can run on a Linux server without an installed copy of The Sims 4. It needs the project files, the S4MT CLI, and an optional game index. The index contains resource keys and provider metadata; it does not contain game resource payloads or `.package` files. That provides the game metadata used by the current `validate` and `build` tools. Before the first upload, `project_init` works normally, while checks that need game references report that the catalog is unavailable.

Generate the index on the Windows game PC when needed, then upload it to the homelab. Once activated, the server reads its local copy and does not need the Windows PC or the game installation to remain available. Regenerate and upload the index after a game update. Its precedence metadata is currently unverified, so it remains a search hint rather than a verified game load order.

## Prepare the Linux server

Install the .NET 10 ASP.NET Core runtime, publish or copy each project's complete framework-dependent output to `/opt/s4mt/cli` and `/opt/s4mt/mcp`, and create directories for projects, build output, and the private cache. Run the service under a dedicated unprivileged account. The cache must be separate from the configured tool read and write roots.

Create separate bearer tokens for MCP clients and cache administration. The MCP token cannot replace the game cache; the admin token cannot call MCP tools.

```sh
sudo install -d -m 0750 -o root -g s4mt /etc/s4mt
sudo install -m 0640 -o root -g s4mt /dev/null /etc/s4mt/mcp.token
sudo install -m 0640 -o root -g s4mt /dev/null /etc/s4mt/cache-admin.token
openssl rand -hex 32 | sudo tee /etc/s4mt/mcp.token >/dev/null
openssl rand -hex 32 | sudo tee /etc/s4mt/cache-admin.token >/dev/null
sudo install -d -m 0750 -o s4mt -g s4mt \
  /srv/s4mt/projects /srv/s4mt/builds /var/lib/s4mt/cache
```

Configure the HTTP host in `/etc/s4mt/mcp.json`:

```json
{
  "cliPath": "/opt/s4mt/cli/s4mt.dll",
  "dotnetPath": "/usr/bin/dotnet",
  "readRoots": ["/srv/s4mt/projects"],
  "writeRoots": ["/srv/s4mt/projects", "/srv/s4mt/builds"],
  "gameRoots": [],
  "transport": "http",
  "httpListenUrl": "http://127.0.0.1:8080",
  "httpBearerTokenFile": "/etc/s4mt/mcp.token",
  "cacheAdminBearerTokenFile": "/etc/s4mt/cache-admin.token",
  "gameIndexCacheRoot": "/var/lib/s4mt/cache",
  "maximumGameIndexBytes": 536870912,
  "maximumGameIndexUncompressedBytes": 8589934592,
  "maximumRetainedGameIndexBytes": 4294967296,
  "maximumGameIndexImportMilliseconds": 1800000,
  "timeoutMilliseconds": 600000,
  "maximumStdoutBytes": 1048576,
  "maximumStderrBytes": 65536
}
```

The host defaults to loopback HTTP. For access beyond loopback, use a reverse proxy that terminates TLS and preserves the `Authorization` header, configure Kestrel for HTTPS with a certificate, or explicitly opt in to private-LAN HTTP as described below. `/mcp` requires the MCP bearer token; `/api/cache/*` requires the separate cache-admin token. Keep authorization headers out of proxy access logs.

For direct access from a trusted private LAN, you can opt in to plain HTTP on one specific private IP address. Set `allowPrivateNetworkHttp` to `true` and bind `httpListenUrl` to an RFC1918 IPv4 address or IPv6 ULA address assigned to the server. The host rejects hostnames, wildcard addresses, and public IP addresses for this mode. For example, if the server currently has `192.168.0.129`:

```json
{
  "allowPrivateNetworkHttp": true,
  "httpListenUrl": "http://192.168.0.129:18124"
}
```

MCP clients can then connect to `http://krux.local:18124/mcp`. The MCP and cache-admin bearer tokens remain required, but plain HTTP does not encrypt them in transit. Use this only on a trusted LAN; use HTTPS when clients cross an untrusted network. The server's current address is a dynamic DHCP lease, so reserve it in the router if you need the endpoint to survive address changes.

Example systemd unit:

```ini
[Unit]
Description=S4MT MCP host
After=network.target

[Service]
Type=exec
User=s4mt
Group=s4mt
WorkingDirectory=/opt/s4mt/mcp
ExecStart=/usr/bin/dotnet /opt/s4mt/mcp/S4MT.Mcp.dll --config /etc/s4mt/mcp.json
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
Restart=on-failure
KillMode=control-group
TimeoutStopSec=15
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadOnlyPaths=/opt/s4mt /etc/s4mt
ReadWritePaths=/srv/s4mt/projects /srv/s4mt/builds /var/lib/s4mt/cache

[Install]
WantedBy=multi-user.target
```

`KillMode=control-group` lets systemd stop CLI descendants that remain in the service cgroup when the host is stopped. The MCP host also puts each Linux CLI call in its own process group and sends `SIGTERM` followed by `SIGKILL` on timeout or cancellation. These process controls support normal trusted CLI execution; configured path roots are not an OS sandbox.

## Transfer a game index from Windows

Copy `scripts/publish-game-index.ps1` to the Windows game PC. It runs the existing `game index` command, prompts for the bearer token without echoing it, streams the compressed index to the homelab, and deletes its temporary export when done.

```powershell
.\scripts\publish-game-index.ps1 `
  -CliPath 'C:\tools\s4mt\s4mt.dll' `
  -DotnetPath 'C:\Program Files\dotnet\dotnet.exe' `
  -GameRoot 'C:\Program Files\EA Games\The Sims 4' `
  -ServiceUri 'https://s4mt.home'
```

The service streams the upload into a private staging directory, enforces compressed and decompressed byte limits, a 1 MiB JSONL record limit, and an overall import deadline, computes SHA-256, and asks the S4MT CLI to read and validate the complete index. Imports run one at a time; extra concurrent import attempts receive HTTP 409. Only after successful validation does it move the index to immutable content-addressed storage and atomically activate a new manifest. A failed upload leaves the current cache active. Previous versions are retained, so in-flight builds can finish while later requests use the newly activated index. Total retained blobs are capped by `maximumRetainedGameIndexBytes`; once full, stop the service and remove only old blobs that are not referenced by `active.json`.

Check cache status:

```sh
curl --fail --silent \
  --config ~/.config/s4mt/cache-admin.curl \
  https://s4mt.home/api/cache/status
```

Create `~/.config/s4mt/cache-admin.curl` with mode `0600` containing `header = "Authorization: Bearer <cache-admin-token>"`; keep it private and do not use that curl config for other hosts.

The response reports the SHA-256 transfer digest, game version, index fingerprint, package and resource counts, and activation time. The fingerprint describes the source game packages; use SHA-256 to identify the exact transferred bytes.

## Connect an MCP client

Configure the client's Streamable HTTP transport URL as `https://s4mt.home/mcp` when using TLS, or `http://krux.local:18124/mcp` for the private-LAN opt-in above. Send the MCP token as `Authorization: Bearer <token>` on every request. The host keeps the existing three tools: `s4mt_project_init`, `s4mt_project_validate`, and `s4mt_project_build`. Their absolute path arguments name files on the Linux server. When `gameIndex` is omitted, validation and build use the active local cache; their `_meta` includes the active cache SHA-256, game version, and source fingerprint. An explicit index override must still be inside a configured read or game root.

For Codex and Antigravity, add these project files to the repository. Neither file contains the token.

`.codex/config.toml`:

```toml
[mcp_servers.s4mt-krux]
url = "http://krux.local:18124/mcp"
bearer_token_env_var = "S4MT_MCP_TOKEN"
```

`.agents/mcp_config.json`:

```json
{
  "mcpServers": {
    "s4mt-krux": {
      "serverUrl": "http://krux.local:18124/mcp",
      "headers": {
        "Authorization": "Bearer ${S4MT_MCP_TOKEN}"
      }
    }
  }
}
```

Set `S4MT_MCP_TOKEN` in the Windows user environment from your local MCP token file, then restart Codex and Antigravity so they inherit it:

```powershell
$tokenPath = Join-Path $env:LOCALAPPDATA 'S4MT\mcp.token'
$token = (Get-Content -Raw -LiteralPath $tokenPath).Trim()
[Environment]::SetEnvironmentVariable('S4MT_MCP_TOKEN', $token, 'User')
Remove-Variable token
```

Codex loads `.codex/config.toml` after you trust the project. Restart Antigravity to load `.agents/mcp_config.json`. MCP tool paths still refer to Linux paths on Krux, under its configured project and build roots.

Local stdio configuration remains the default. HTTP mode and its cache settings are opt-in through `transport: "http"`.
