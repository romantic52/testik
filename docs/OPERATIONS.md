# AEGIS Operations

## Runtime modes

### Source / development

~~~text
run-agent.bat
~~~

If no published bundle exists, the launcher uses `dotnet run`.

Default state:

~~~text
%LocalAppData%\AEGIS\aegis.db
~~~

### Published console bundle

~~~text
build-release.bat
run-agent.bat
~~~

Preferred executable:

~~~text
dist\AEGIS\Aegis.Agent.exe
~~~

### Windows Service

Elevated terminal:

~~~powershell
.\scripts\install-service.ps1
~~~

or:

~~~text
install-service.bat
~~~

Service name: `AEGISAgent`

Display name: `AEGIS Agent`

Service state directory:

~~~text
%ProgramData%\AEGIS\aegis.db
~~~

Uninstall:

~~~powershell
.\scripts\uninstall-service.ps1
~~~

Data is preserved by default. `-PurgeData` is explicit/destructive.

## Configuration

Base file:

~~~text
backend\Aegis.Agent\appsettings.json
~~~

Important keys:

~~~text
Agent:ListenUrl
Agent:SampleIntervalMs
Agent:ProcessLimit
Agent:HistoryMinutes
Agent:DataDirectory
Agent:AetherTimeoutSeconds
Agent:AuditRetentionDays
Agent:AuditMaxRows
Agent:StateMaintenanceMinutes
~~~

Environment override example:

~~~powershell
$env:Agent__SampleIntervalMs = "2000"
$env:Agent__AuditRetentionDays = "180"
~~~

Command line:

~~~powershell
Aegis.Agent.exe --Agent:DataDirectory="D:\AEGIS-Data"
~~~

## SQLite persistence

Primary state file:

~~~text
aegis.db
~~~

SQLite uses WAL mode. Incidents, audit and alert settings share one transactional store.

On first start after upgrading from the JSON-based version, AEGIS imports:
- `incidents.json`
- `audit*.jsonl`
- `alert-settings.json`

Successfully imported files are renamed with `.migrated`. Malformed legacy incident JSON is preserved with a timestamped `.corrupt-...` suffix.

## Audit retention

Defaults:

~~~text
Agent:AuditRetentionDays = 90
Agent:AuditMaxRows = 100000
Agent:StateMaintenanceMinutes = 360
~~~

`StateMaintenanceService` removes only audit rows older than the retention window and then enforces the row cap. Incidents are never removed by maintenance.

A passive WAL checkpoint runs after maintenance.

## Backup

For a simple consistent manual backup, stop the console process or Windows Service first, then copy:

~~~text
aegis.db
~~~

When the Agent is live, WAL files may contain uncheckpointed state, so copying only the main DB file is not recommended.

## Health

Primary endpoints:

~~~text
GET /api/v1/health/live
GET /api/v1/health/ready
GET /api/v1/diagnostics
~~~

Readiness requires both fresh telemetry and a successful SQLite persistence probe.

Diagnostics exposes nested `agent` and `persistence` objects.

## Automatic background services

Without any browser open, the Agent runs:
- telemetry sampling;
- telemetry alert engine;
- Windows Event Log Critical/Error correlation;
- agent lifecycle audit;
- SQLite state maintenance.

AETHER E2E delivery remains browser-owned. Pending incidents keep `aetherSent=false` until an operator connects an AETHER-capable browser session.

## Windows Event correlation

System/Application logs are polled for newly created Critical/Error entries.

Startup seeds a watermark from current records; historical errors are not bulk-created as incidents.

## Troubleshooting

### Agent not reachable

~~~powershell
Get-Service AEGISAgent
Invoke-RestMethod http://127.0.0.1:8765/api/v1/health
~~~

Port check:

~~~powershell
Get-NetTCPConnection -LocalPort 8765
~~~

### Readiness is 503

Inspect:

~~~powershell
Invoke-RestMethod http://127.0.0.1:8765/api/v1/health/ready
Invoke-RestMethod http://127.0.0.1:8765/api/v1/diagnostics
~~~

Possible causes:
- telemetry still warming up;
- stale sampler;
- SQLite/data-directory access failure.

### Sensors missing

Not all laptop ECs, motherboards, GPUs or fan controllers expose every sensor through LibreHardwareMonitor.

Missing data remains null/unavailable.

### Process fields missing

Windows can deny access to protected processes. AEGIS does not bypass access controls.

### TCP PID missing

IPv4 owner mapping uses Windows IP Helper. IPv6 is preserved but may not have owner PID in the current implementation.

## CI/release verification

Main CI validates:
1. split frontend JavaScript syntax;
2. frontend DOM/runtime structure;
3. PowerShell syntax;
4. restore;
5. Release build;
6. xUnit tests;
7. source runtime smoke;
8. self-contained win-x64 publish;
9. packaged executable smoke;
10. Windows Service smoke.

The release workflow repeats verification before artifact upload.
