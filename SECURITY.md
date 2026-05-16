# Security

This document describes the threat model GameServerControl is designed for,
what protections are built in, and what an operator should do to deploy it
safely. If you're shipping this to public users (e.g. installing it for friends,
or hosting it on a server reachable from the internet), please read all of it.

## Threat model

GameServerControl is **administrative software** for game-server hosts. Anyone
with a valid API token can start, stop, reconfigure, or destroy your servers —
including the host's underlying VMs. Treat the API token like an SSH key.

The default deployment model assumed by this project:

| Surface              | Assumption                                                       |
|----------------------|------------------------------------------------------------------|
| Agent network access | Reachable only over a private overlay (Tailscale) or LAN.       |
| Agent host           | Trusted; you own and patch it.                                  |
| Client network       | Untrusted (laptop on hotel Wi-Fi is fine — Tailscale handles it).|
| Game-server clients  | Untrusted (game players); they only talk to the GAME, not the agent. |

**The agent should not be exposed to the public internet without HTTPS,
mTLS, or a reverse proxy with WAF rules.** It is not battle-hardened web
infrastructure.

## Built-in protections

| What                                  | Where                                              |
|---------------------------------------|----------------------------------------------------|
| Bearer-token auth on all `/api/*`     | `Auth/TokenAuthHandler.cs`                         |
| **First-run token auto-generation**   | `Auth/FirstRunTokenGenerator.cs` — placeholder/empty tokens are replaced on boot with 32-byte cryptographic random, persisted to `appsettings.json`, printed to console once. **No GitHub clone ships with a default credential.** |
| Optional HTTPS with self-signed cert  | `Auth/CertHelper.cs` — enable by setting `Agent:BindHttps` and `Agent:CertPassword`. |
| Tailscale-only binding by default     | `appsettings.json` `Agent:Bind` defaults to a Tailscale IP. |
| Audit log of authenticated mutations  | `Auth/AuditLogger.cs` — every POST/PUT/DELETE/PATCH on `/api/*` is appended JSON-lines to `Logs/audit/audit-YYYY-MM-DD.jsonl` (timestamp, IP, method, path, status, user). |
| Atomic writes for config & state      | `*.tmp` + `File.Replace` so a crash never corrupts `servers.json`. |
| ID validation for new servers         | `Servers/ServerStore.cs` — lowercase alphanumerics + hyphens only; prevents path-traversal via server ID. |
| AGS API token cache invalidation on 401 | `Admin/SatisfactoryAdminClient.cs` — handles password rotation gracefully. |

## What you should do before deploying

### 1. Replace the placeholder token (or let the agent do it)

`appsettings.json` ships with `ApiToken` set to a placeholder. On first boot
the agent replaces it with a random 32-byte token and prints it to console:

```
====================================================================
  GENERATED NEW AGENT API TOKEN — copy this into your client now:

     T7c2k8m_xyz...

  Saved to: C:\GameServerControl\Agent\appsettings.json
====================================================================
```

Copy that into the client's Settings dialog. If you miss it, the token is
in `appsettings.json` — readable by anyone with file-system access to the
agent's directory (so protect that with NTFS ACLs).

### 2. Don't expose the agent to the public internet

Bind the agent to a Tailscale IP (`100.x.y.z`) or your LAN, never to `0.0.0.0`
on a public-facing interface without HTTPS and a reverse proxy. The provided
default is loopback.

### 3. Add a Windows Firewall rule limiting inbound

The auto-installed rule allows `100.64.0.0/10` (Tailscale CGNAT range). If
your agent isn't on Tailscale, narrow this further. **Do not allow `Any`.**

### 4. Protect `servers.json` and `appsettings.json`

Game-server passwords (RCON / admin / client passwords) are stored in plain
text in `servers.json`. Restrict NTFS permissions on the agent directory to
`SYSTEM` + the account running the service.

### 5. Keep `audit-*.jsonl` reviewable

The audit log shows everyone who modified anything. Read it occasionally;
if someone starts/stops servers you didn't tell them to, your token leaked.

## Known limitations (planned hardening)

These are NOT yet implemented. PRs welcome.

| Gap | Mitigation today | Future fix |
|-----|------------------|------------|
| Passwords stored plaintext in `servers.json` | NTFS ACLs | DPAPI encryption (Windows-only, per-machine key) |
| Single shared API token | Lock down network access | Per-user tokens with roles (read-only vs. admin) |
| No rate limiting on auth failures | Tailscale gating | Throttle middleware: N failures / 5 min → block IP 15 min |
| No HSTS / CSP / X-Content-Type-Options headers | Use reverse proxy if exposed | Add `app.UseSecurityHeaders()` |
| Hardcoded default cert password (`gscagent`) | Set `Agent:CertPassword` explicitly | Refuse to start if password is the default |
| Service runs as LocalSystem | Trade-off accepted (needed for Hyper-V + Task Scheduler) | Document minimum-privilege custom service account |
| No CSRF token on SignalR | Same-origin in practice | Add CSRF token to status hub |
| Web UI is anonymous (loads HTML), API calls inside it still auth-gated | Keep WebUi short or remove if not needed | Optionally require token for `/` too |

## Reporting security issues

Don't open a public GitHub issue for an exploitable vuln. Instead:
- Open a [private security advisory](https://github.com/...) on the repo, or
- Email the maintainer.

If you don't get a response in 7 days, you may publicly disclose.

## Sensible deployment checklist

Before exposing this to a friend or putting it on the internet:

- [ ] Default placeholder token has been replaced (verified by checking `appsettings.json`)
- [ ] Agent binds to private interface only (Tailscale / LAN, not `0.0.0.0` public)
- [ ] Windows Firewall rule limits inbound to expected source CIDRs
- [ ] `appsettings.json` and `servers.json` have restricted NTFS ACLs
- [ ] Audit log directory is writable by the service account
- [ ] HTTPS is enabled if the agent is reachable from outside Tailscale/LAN
- [ ] Cert password is non-default
- [ ] You've read the audit log at least once and recognize all entries
