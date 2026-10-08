# AEGIS Security Model

## Trust boundaries

AEGIS is local-first.

- The .NET Agent can read local machine/process/hardware information.
- The browser operator console talks to the Agent over loopback.
- AETHER password and Double Ratchet state remain browser-side.
- External enterprise connectors are not considered trusted/active until explicitly implemented.

## Network exposure

Default:

~~~text
http://127.0.0.1:8765
~~~

`Agent:ListenUrl` is validated as loopback-only. Publishing AEGIS to LAN/Internet would require a separate authentication and threat-model change.

Host validation allows loopback names/addresses only.

## HTTP/browser controls

The web layer sets restrictive headers including CSP, frame blocking, no-referrer, Permissions-Policy and API no-store behavior.

WebAssembly support is enabled for the canonical AETHER Ratchet runtime; arbitrary unsafe eval is not enabled.

## Persistence

Operational SOC state is stored in SQLite `aegis.db` under the configured data directory.

SQLite stores:
- incident content;
- audit records;
- alert settings;
- internal schema/revision metadata.

It is not encrypted at rest and the audit table is not cryptographically tamper-evident. Local filesystem permissions remain part of the trust boundary.

For stronger production guarantees, use OS-protected storage and/or a signed remote audit sink.

Legacy JSON state is migrated but preserved with a `.migrated` suffix rather than silently discarded.

Audit maintenance deletes only rows outside configured retention; it does not delete incidents.

## AETHER E2E

The privileged Agent does not own user E2E secrets.

Browser responsibilities:
- login/key-backup decryption;
- dedicated `soc-*` device;
- prekeys/fallback key;
- device/master signature verification;
- Double Ratchet encryption/decryption.

The bridge is allow-listed and has SSRF controls:
- allowed protocol endpoints only;
- remote relay requires HTTPS;
- no embedded credentials;
- redirects disabled;
- bounded timeout.

## Process investigation

AEGIS respects Windows access boundaries. Protected process fields may be unavailable.

SHA-256 is computed only when a readable path exists and the executable is below the configured safety bound in code.

Embedded signer fields indicate whether Windows/.NET can extract a certificate from the signed file and expose certificate metadata. This is not a full Authenticode chain/revocation/policy trust verdict.

AEGIS does not automatically classify unsigned or high-CPU processes as malware.

## Network investigation

IPv4 TCP owner PID/process mapping uses Windows IP Helper APIs.

IPv6 endpoints remain visible even when owner PID mapping is unavailable. Missing PID is reported as unavailable rather than guessed.

## Windows Event Log

Only System/Application are exposed.

Background correlation converts newly observed Critical/Error events into incidents. Startup establishes a watermark, preventing historical-log floods.

The Security log is not exposed, and AEGIS does not escalate privileges to access unavailable logs.

## Windows Services

Service inventory is read-only. There is no HTTP endpoint to start, stop, restart or reconfigure arbitrary Windows Services.

## Local system power action

AEGIS exposes a narrow, **operator-confirmed Windows shutdown**, not a remote-management shell:
- Visual-only BSOD demo is the default and does not request OS power operations.
- A separate real-shutdown action requires a second choice, the exact typed phrase `ВЫКЛЮЧИТЬ`, an acknowledgement and a custom intent header.
- AEGIS waits 45 seconds, then requests graceful Windows shutdown with zero OS timeout. Open apps may block shutdown.
- The operator may cancel the scheduled shutdown in the interface before the timeout.
- The local loopback origin/security headers are still enforced; this is not authorization for arbitrary hosts or users.

This is a **high-impact local action**. Do not expose the agent to untrusted local applications/users, and keep unsaved documents closed before using it. A passing CI test confirms input validation, not that a real desktop machine has actually been switched off.

There is no API for process termination, arbitrary commands, firewall changes, service mutation or credentials.

## Windows Service privilege boundary

Installing AEGIS as a Windows Service is explicit and requires elevation.

Service-mode state defaults to `%ProgramData%\AEGIS`. The browser still owns AETHER credentials.

Uninstall preserves SOC data unless the operator explicitly requests purge.

**Power safety note:** Positive Windows `shutdown.exe /t` values implicitly force-close apps. AEGIS therefore keeps the 45-second delay in its own agent, then calls the Windows shutdown utility with `/t 0` (no implicit force-close). If the agent exits during the countdown, the shutdown is not requested. Open apps can still block shutdown, and the operator may need to close them manually. Real power actions are not available when the agent runs as a non-interactive Windows Service.
