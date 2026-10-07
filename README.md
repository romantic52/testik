# AEGIS SOC 0.5.0

AEGIS — local-first Windows monitoring/SOC application for a coursework project that has grown into a production-like local agent: .NET 8 backend, real telemetry, SQLite state, backend correlation rules, Windows Event Log correlation, reports, Windows Service mode and E2E AETHER.chat integration.

## What is real

- CPU / RAM / GPU / disks / temperatures / fan RPM when hardware exposes the sensor.
- Process sampling: PID, CPU, RAM, threads and path where Windows permits access.
- Process investigation: file metadata, SHA-256 and embedded signer certificate metadata.
- Network throughput, TCP endpoints, IPv4 owner PID/process mapping and IPv6 endpoint visibility.
- Windows System/Application Event Log investigation.
- Read-only Windows Services inventory.
- Live WebSocket telemetry and bounded history.
- Manual and backend-generated incidents.
- SQLite persistence, audit history and alert settings.
- Automatic Critical/Error Windows Event Log → incident correlation.
- JSON, CSV and support ZIP reports.
- AETHER.chat Double Ratchet messaging with a dedicated `soc-*` device.

Access Control/СКУД and Cameras/VMS are explicit connector placeholders until real vendor APIs are supplied. They do not display fake live data.

`CRASH SYSTEM` is intentionally a visual-only BSOD/offline simulation. It never shuts down Windows or performs destructive remediation.

## Architecture

~~~text
Windows
  ├─ hardware sensors
  ├─ processes
  ├─ network
  ├─ Event Log
  └─ Services
       |
       v
Aegis.Agent (.NET 8)
  ├─ TelemetrySamplerService
  ├─ AlertEngineService
  ├─ WindowsEventAlertService
  ├─ SQLite state (WAL)
  ├─ reports / diagnostics
  ├─ /api/v1
  └─ /ws/monitor
       |
       v
Operator UI
  ├─ overview / investigations
  ├─ incidents / audit / reports
  ├─ alert settings
  └─ AETHER Double Ratchet client
       |
       v
AETHER relay (ciphertext)
~~~

See:
- `docs/ARCHITECTURE.md`
- `docs/API.md`
- `docs/SECURITY.md`
- `docs/OPERATIONS.md`

## Repository layout

~~~text
backend/
  Aegis.Agent/
    Configuration/
    Endpoints/
    Infrastructure/
    Models/
    Services/
  Aegis.Agent.Tests/

frontend/
  core.js
  incidents.js
  aether-ui.js
  automation.js
  monitoring.js
  crash-demo.js

docs/
scripts/
vendor/

index.html
styles.css
aether.js
app.js
~~~

`app.js` is now only the bootstrap layer; functional browser code lives under `frontend/`.

## Persistence

Console/source default:

~~~text
%LocalAppData%\AEGIS\aegis.db
~~~

Windows Service default:

~~~text
%ProgramData%\AEGIS\aegis.db
~~~

SQLite runs in WAL mode and stores:
- incidents;
- audit;
- alert settings;
- schema/revision metadata.

Old `incidents.json`, `audit*.jsonl` and `alert-settings.json` are migrated once when found. Migrated files are preserved with a `.migrated` suffix.

Audit retention is bounded by age and maximum row count. Incidents are not deleted by maintenance.

## Automatic detection

Backend rules continue to run with the browser closed:
- stale telemetry;
- sustained CPU load;
- CPU temperature;
- RAM pressure;
- GPU temperature;
- disk usage;
- high temperature + very low RPM when a real fan sensor exists;
- sustained high process CPU.

A second background correlator watches new Critical/Error records from Windows System/Application logs. Startup uses a watermark so existing historical Event Log entries are not bulk-imported as incidents.

High CPU alone is treated as a resource anomaly requiring review, not proof of malware.

## AETHER.chat

E2E remains browser-owned by design:
- normal login + optional TOTP;
- dedicated `soc-*` device;
- signed prekeys/fallback key;
- device/master key verification;
- per-device Double Ratchet fanout;
- inbox/ACK;
- relay WebSocket + polling fallback;
- queued incident delivery.

The privileged Agent does not store the AETHER password or browser Ratchet state.

## Primary API

Use the versioned surface:

~~~text
/api/v1/...
~~~

The old `/api/...` routes remain temporarily available as a compatibility surface.

Important endpoints:

~~~text
GET /api/v1/version
GET /api/v1/health
GET /api/v1/health/live
GET /api/v1/health/ready
GET /api/v1/diagnostics

GET /api/v1/system
GET /api/v1/hardware
GET /api/v1/processes
GET /api/v1/processes/{pid}
GET /api/v1/network/connections
GET /api/v1/windows/events
GET /api/v1/windows/services

GET/PUT/DELETE /api/v1/incidents...
GET/POST       /api/v1/audit...
GET/PUT        /api/v1/settings/alerts...

GET /api/v1/reports/current.json
GET /api/v1/reports/incidents.csv
GET /api/v1/reports/bundle.zip

POST /api/v1/aether/relay
WS   /ws/monitor
~~~

## Run

Requirements for source mode:
- Windows
- .NET 8 SDK

~~~powershell
winget install Microsoft.DotNet.SDK.8
~~~

Then:

~~~text
run-agent.bat
~~~

Dashboard:

~~~text
http://127.0.0.1:8765
~~~

## Build release

~~~text
build-release.bat
~~~

Output:

~~~text
dist\AEGIS\Aegis.Agent.exe
~~~

The verified pipeline checks:
- all frontend runtime syntax;
- DOM/frontend structure;
- PowerShell syntax;
- .NET build;
- xUnit tests;
- source runtime smoke;
- self-contained win-x64 publish;
- published executable smoke;
- Windows Service smoke.

## Windows Service

From an elevated terminal after building:

~~~powershell
.\scripts\install-service.ps1
~~~

Uninstall:

~~~powershell
.\scripts\uninstall-service.ps1
~~~

Operational data is preserved by default.

## Security boundaries

AEGIS binds loopback-only by default and validates the configured listen URL.

There are no endpoints for:
- killing arbitrary processes;
- stopping/restarting Windows Services;
- Windows shutdown;
- firewall mutation;
- arbitrary command execution.

Process signer fields describe an embedded certificate if present; they are not a full Authenticode trust-chain verdict.

See `docs/SECURITY.md`.
