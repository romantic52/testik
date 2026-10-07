# AEGIS Architecture

## Design goal

AEGIS is a local-first Windows monitoring/SOC agent. Monitoring, persistence and detection belong to the backend; the browser is an operator console and the owner of AETHER E2E state.

The browser may be closed without stopping telemetry collection or incident generation.

## Runtime data flow

~~~text
Hardware / Processes / Network
             |
             v
   TelemetrySamplerService
      immutable-like frame
             |
      +------+------+
      |             |
      v             v
REST/WebSocket  AlertEngineService
                    |
Windows Event Log   |
      |             |
      v             v
WindowsEventAlertService
             |
             v
      IncidentStoreService
             |
             v
       SQLite / WAL
             |
      +------+------+
      |             |
      v             v
Operator UI      Reports
      |
      v
AETHER browser crypto
~~~

## Composition root

`Program.cs` wires configuration and services. HTTP contracts live in `Endpoints/AegisEndpoints.cs`; web/security hosting concerns live in `Infrastructure/AegisWebExtensions.cs`.

## Configuration

`Configuration/AgentOptions.cs` owns runtime limits:
- loopback listen URL;
- telemetry sample interval;
- process sample limit;
- history window;
- data directory;
- AETHER bridge timeout;
- SQLite audit retention days;
- SQLite audit max rows;
- state-maintenance interval.

Standard .NET configuration precedence applies. Environment variables use `__`, for example:

~~~powershell
$env:Agent__SampleIntervalMs = "2000"
~~~

## Telemetry

`TelemetrySamplerService` is the single periodic collector.

That is important because CPU process percentages and network throughput are delta-based. REST/report/WebSocket readers consume the same sampled frame instead of each changing the baseline independently.

The in-memory telemetry history is bounded by configured duration and is intentionally not written to SQLite every second.

## Persistence

`Infrastructure/StateDatabase.cs` owns `aegis.db`.

SQLite configuration:
- WAL journal;
- synchronous NORMAL;
- busy timeout;
- pooled shared connections.

Schema v1:
- `incidents`
- `audit`
- `settings`
- `metadata`

Incident create/update/delete operations and their generated audit entry are transactional.

`metadata.revision` is exposed to the telemetry WebSocket so the UI can notice backend incident changes.

Legacy JSON/JSONL state is imported once by the services and renamed with `.migrated`.

`StateMaintenanceService` removes old audit rows by retention age and row cap, then performs a passive WAL checkpoint. It never removes incidents.

## Detection

### AlertEngineService

Runs without a browser and evaluates:
- telemetry freshness;
- CPU load/temperature;
- RAM;
- GPU temperature;
- disk use;
- available fan RPM vs high temperature;
- sustained process CPU.

Sustain/cooldown values are stored in SQLite settings.

### WindowsEventAlertService

Polls allow-listed System/Application logs. On startup it seeds a record watermark, so historical events are not converted to incidents.

Only newly observed Critical/Error events become automatic incidents.

## Investigation services

`ProcessDetailsReader`
: on-demand process metadata, SHA-256 (bounded file size) and embedded signer certificate metadata.

`NetworkMonitorService`
: adapter throughput plus active TCP/UDP investigation. IPv4 TCP ownership uses Windows IP Helper owner-PID tables. IPv6 endpoints remain visible even when PID ownership is unavailable.

`WindowsEventLogService`
: read-only System/Application Warning/Error/Critical access.

`WindowsServiceMonitorService`
: read-only service inventory.

## API

Primary contract is `/api/v1`.

`/api` is mapped to the same handlers for backward compatibility and can be removed in a future breaking release.

Liveness and readiness are separate:
- liveness: HTTP process responds;
- readiness: telemetry is fresh and SQLite is writable/available.

WebSocket remains `/ws/monitor`.

## Frontend

Framework-free by design, but no longer monolithic:

- `frontend/core.js` — shared state, overview, API helpers, read-only investigation views;
- `frontend/incidents.js` — incident UI/lifecycle;
- `frontend/aether-ui.js` — operator AETHER bindings;
- `frontend/automation.js` — alert settings and pending delivery UI;
- `frontend/monitoring.js` — telemetry/process/network diagnostics;
- `frontend/crash-demo.js` — visual-only failure demo;
- `app.js` — bootstrap.

The backend is the source of truth for incidents/settings. Browser local storage is reserved for client-side AETHER/operator convenience state, not SOC persistence.

## AETHER boundary

Double Ratchet stays in the browser. The backend exposes an allow-listed relay bridge but does not receive the operator password or own Ratchet state.

Automatic incidents created while no operator is connected remain pending until an AETHER-capable browser session encrypts and delivers them.

## Packaging

The project publishes self-contained `win-x64`. Static frontend, vendor Ratchet WASM and scripts are packaged with the Agent.

The same executable supports console and Windows Service hosting.

## Verification

Main CI performs syntax/structure checks, .NET build, xUnit, source smoke, self-contained publish, packaged executable smoke and Windows Service smoke.
