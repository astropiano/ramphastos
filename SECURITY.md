# Security Policy

## Supported versions

Ramphastos is in alpha (`0.x`). Only the latest release receives security
fixes.

## Reporting a vulnerability

Please **do not** open a public issue for security problems.

Report vulnerabilities privately via GitHub's private vulnerability
reporting ("Report a vulnerability" on the Security tab), or by emailing the
maintainer listed on the repository profile.

Include the protocol version, a description of the issue, and a
reproduction if possible. We aim to acknowledge reports within a few days.

## Security expectations

- **Sessions are not anonymous-safe by default.** `MapRamphastos()` exposes
  a SignalR endpoint; use `requireAuthorization` and/or
  `RamphastosOptions.AuthorizeHello` to gate `hello` — unauthenticated
  sessions can attach as viewers and (as controllers) execute `[Command]`
  methods on your view-models.
- **Roles are a hard boundary only if you treat them as one.** `viewer`
  receives the replicated graph; `controller` may invoke commands and open
  upstream streams. Any property you do not want on the wire must be marked
  `[ServerOnly]` or `[NoSync]` — everything else is sent to every attached
  client.
- **Stream direction is enforced, content is not.** Declared directions
  (`up`/`down`/`duplex`) and the controller role gate *who* may open a
  stream, not *what* flows through it. Validate payload bytes yourself.
- **Session persistence uses TotalSerializer**, which reconstructs object
  graphs including delegates — it is not a safe format for untrusted input.
  Session stores (file/Redis) must not be writable by untrusted parties;
  treat session records as trusted internal state.
- The alpha protocol carries no integrity guarantees beyond the transport —
  use TLS (wss/https) in production.
