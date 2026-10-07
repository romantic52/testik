# AEGIS SOC

AEGIS 0.3.0 — local-first Windows monitoring/SOC project with a .NET 8 Agent, real hardware/process/network telemetry, persistent incidents and audit, backend alert rules, reporting, and E2E integration with AETHER.chat.

## Status

This repository is no longer a static dashboard mockup.

Current pipeline verifies:
- frontend JavaScript syntax;
- .NET Release build;
- xUnit tests;
- real Agent startup on Windows;
- local API smoke checks;
- self-contained Windows publish;
- startup and API smoke checks of the published Aegis.Agent.exe.

## Core capabilities

### Monitoring

- CPU load and temperature when the sensor is available;
- RAM total / used / load;
- GPU load and temperature;
- hardware temperatures;
- fan RPM when exposed by the controller;
- disks and used/free space;
- network adapters and RX/TX throughput;
- process PID / CPU / RAM / threads / path;
- process detail: start time, private memory, handles, priority and responding state;
- executable file metadata and on-demand SHA-256;
- active TCP connections and UDP listeners;
- read-only Windows Event Log investigation for System/Application warnings, errors and critical events;
- read-only Windows services inventory with status/search;
- WebSocket live telemetry;
- bounded telemetry history for real charts.

Telemetry is collected by one background TelemetrySamplerService. REST, reports and WebSocket read the same sample instead of independently changing process/network delta baselines.

### SOC

- manual incidents;
- automatic incidents from a backend rule engine;
- persistent incidents;
- close/delete workflows;
- append-oriented JSONL audit;
- persistent alert settings;
- JSON operational report;
- incident CSV export;
- live incident revision over WebSocket.

The backend rule engine continues to work even when the browser is closed.

### Automatic rules

Current rules cover:
- stale telemetry;
- sustained CPU load;
- CPU temperature;
- RAM load;
- GPU temperature;
- disk usage;
- high temperature + very low RPM when a real fan sensor exists;
- sustained process CPU anomaly.

Thresholds, sustain time and cooldown are configured from the UI but stored and enforced by Aegis.Agent.

A high-CPU process is treated as a resource anomaly requiring review, not automatically classified as malware.

### AETHER.chat

The right-side SOC channel uses the current Double Ratchet implementation from romantic52/AETHER.chat.

Implemented:
- normal AETHER login;
- TOTP/2FA;
- separate soc-* crypto device;
- signed prekeys and fallback key;
- device/master key verification;
- per-device encrypted fanout;
- inbox + ACK;
- AETHER WebSocket realtime notification;
- polling fallback;
- queued automatic incident delivery.

E2E crypto remains client-side. Aegis.Agent does not own the user's chat Ratchet state.

## Honest connector state

AEGIS does not fake external enterprise systems.

- local Windows inventory: real;
- hardware/process/network monitoring: real;
- AETHER integration: real;
- Access Control / СКУД: NOT CONFIGURED until a real controller/API is connected;
- Cameras/VMS: NOT CONFIGURED until a real RTSP/VMS integration is connected.

The CRASH SYSTEM button is intentionally only a visual BSOD/offline simulation. It never shuts down Windows or performs destructive remediation.

## Architecture

~~~text
Windows hardware / processes / network
                 |
                 v
        TelemetrySamplerService
                 |
        +--------+--------+
        |                 |
        v                 v
 REST / WebSocket    AlertEngineService
        |                 |
        |                 v
        |          IncidentStoreService
        |                 |
        +--------+--------+
                 |
                 v
             Browser UI
                 |
                 v
        AETHER Double Ratchet
                 |
                 v
            AETHER relay
~~~

More detail:
- docs/ARCHITECTURE.md
- docs/API.md
- docs/SECURITY.md
- docs/OPERATIONS.md

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

docs/
scripts/
vendor/

index.html
styles.css
app.js
aether.js

run-agent.bat
build-release.bat
~~~

## Data directory

Default:

~~~text
%LocalAppData%\AEGIS\
  incidents.json
  audit.jsonl
  alert-settings.json
~~~

The directory can be changed through Agent:DataDirectory.

## Quick start from source

Requirements:
- Windows;
- .NET 8 SDK.

Install SDK if required:

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

The Agent is loopback-only by default.

## Windows Service mode

After building the verified bundle, run an elevated terminal:

~~~powershell
.\scripts\install-service.ps1
~~~

or use install-service.bat.

The service is configured for automatic startup and restart-on-failure. Service-mode persistence uses %ProgramData%\AEGIS.

Remove it with:

~~~powershell
.\scripts\uninstall-service.ps1
~~~

Data is preserved by default. See docs/OPERATIONS.md.

## Build a verified Windows bundle

~~~text
build-release.bat
~~~

The script does not just publish files. It runs:

1. restore;
2. xUnit tests;
3. self-contained win-x64 publish;
4. smoke test of the packaged Aegis.Agent.exe.

Output:

~~~text
dist\AEGIS\Aegis.Agent.exe
~~~

run-agent.bat uses the packaged executable when present, otherwise it falls back to dotnet run.

## Main API

~~~text
GET    /api/health
GET    /api/diagnostics
GET    /api/system
GET    /api/hardware
GET    /api/network
GET    /api/network/connections?limit=200
GET    /api/network/udp?limit=200
GET    /api/windows/events?log=System&limit=100
GET    /api/windows/services?limit=500
GET    /api/processes?limit=50
GET    /api/processes/{pid}
GET    /api/history?seconds=900
GET    /api/connectors

GET    /api/incidents
PUT    /api/incidents/{id}
DELETE /api/incidents/{id}

GET    /api/settings/alerts
PUT    /api/settings/alerts

GET    /api/audit?limit=200
POST   /api/audit

GET    /api/reports/current.json
GET    /api/reports/incidents.csv

POST   /api/aether/relay

WS     /ws/monitor
~~~

See docs/API.md for behavior and contracts.

## Security

AEGIS is local-first and currently intentionally binds to:

~~~text
http://127.0.0.1:8765
~~~

The web layer includes:
- loopback Host header validation;
- CSP;
- frame blocking;
- no-referrer;
- restrictive Permissions-Policy;
- no-store API responses.

The AETHER bridge is allow-listed and is not a generic HTTP proxy. Remote AETHER relay URLs require HTTPS and redirects are disabled.

WebAssembly execution is permitted only for the Ratchet WASM runtime; general unsafe-eval is not enabled.

See docs/SECURITY.md.

## Hardware limitations

LibreHardwareMonitor is used for available hardware sensors.

Not every motherboard, laptop EC, GPU driver or fan controller exposes every sensor. AEGIS returns unavailable/null instead of inventing values.

Running the Agent elevated may expose more hardware sensors on some systems, but AEGIS does not bypass Windows process or device security boundaries.

## CI

Build Windows Agent workflow verifies every main/PR change with:
- app.js syntax;
- aether.js syntax;
- restore;
- Release build;
- xUnit tests;
- source runtime smoke;
- on main push: self-contained win-x64 publish + packaged EXE smoke.

Package Windows Release provides an artifact-oriented workflow for tags/manual release builds.

Canonical AETHER Ratchet WASM is synchronized by the dedicated vendor workflow.
