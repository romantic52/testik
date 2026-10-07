# AEGIS Operations

## Runtime modes

AEGIS supports three execution modes.

### Source / development

~~~text
run-agent.bat
~~~

If no published bundle exists, the launcher uses dotnet run.

Default data directory:

~~~text
%LocalAppData%\AEGIS
~~~

### Published console bundle

~~~text
build-release.bat
run-agent.bat
~~~

The launcher prefers:

~~~text
dist\AEGIS\Aegis.Agent.exe
~~~

The build script verifies restore, tests, publish and runtime smoke before accepting the bundle.

### Windows Service

Build the release first, then open an elevated terminal:

~~~powershell
.\scripts\install-service.ps1
~~~

or:

~~~text
install-service.bat
~~~

Service name:

~~~text
AEGISAgent
~~~

Display name:

~~~text
AEGIS Agent
~~~

The installer:
- requires administrator rights;
- refuses to silently replace an existing service;
- points SCM at the published executable;
- configures automatic startup;
- configures restart-on-failure recovery;
- starts the service unless -NoStart is supplied;
- configures service-mode persistence at %ProgramData%\AEGIS.

Microsoft's Windows Service host integration is context-aware: the same executable still runs normally as a console app when it is not launched by SCM.

## Service data

Console/source default:

~~~text
%LocalAppData%\AEGIS
~~~

Installed service default:

~~~text
%ProgramData%\AEGIS
~~~

Service installation passes Agent:DataDirectory explicitly so LocalSystem profile paths do not silently change the persistence location.

## Uninstall

Elevated terminal:

~~~powershell
.\scripts\uninstall-service.ps1
~~~

or:

~~~text
uninstall-service.bat
~~~

By default data is preserved.

Explicit destructive cleanup:

~~~powershell
.\scripts\uninstall-service.ps1 -PurgeData
~~~

-PurgeData is intentionally opt-in.

## Configuration

Base configuration lives in:

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
Agent:AuditMaxMegabytes
Agent:AuditRetentionFiles
~~~

.NET configuration precedence allows command-line and environment overrides.

Environment variables use double underscore:

~~~powershell
$env:Agent__SampleIntervalMs = "2000"
$env:Agent__HistoryMinutes = "30"
~~~

Command-line example:

~~~powershell
Aegis.Agent.exe --Agent:DataDirectory="D:\AEGIS-Data"
~~~

## Diagnostics

~~~text
GET /api/diagnostics
~~~

Returns:
- Agent version;
- process id;
- console vs Windows Service mode;
- start time and uptime;
- working/private memory;
- managed memory / GC heap;
- thread and handle counts;
- GC collection counts;
- last telemetry sample and age;
- history size;
- incident revision;
- active persistence directory;
- runtime sampling configuration.

This is also visible in the Monitoring UI and included in JSON reports.

## Audit retention

Defaults:

~~~text
Agent:AuditMaxMegabytes = 25
Agent:AuditRetentionFiles = 5
~~~

When audit.jsonl reaches the configured size, AEGIS rotates it to audit.1.jsonl and retains bounded historical files. The audit API reads current + rotated files newest-first.

## Backups

For a consistent manual backup, stop the Windows Service or console Agent first.

Back up:

~~~text
incidents.json
audit.jsonl
alert-settings.json
~~~

The incident store uses temp-file replacement for normal updates.

If incidents.json is malformed, AEGIS preserves it as:

~~~text
incidents.corrupt-YYYYMMDDHHMMSSfff.json
~~~

instead of silently overwriting it.

## AETHER while running as a service

Monitoring and automatic incident creation run fully in the service.

AETHER E2E encryption intentionally remains in the browser operator client. Therefore:
- incidents continue to be created with the UI closed;
- incidents remain pending with aetherSent=false;
- when an operator opens the UI and connects AETHER, pending automatic incidents are encrypted and delivered.

This avoids storing the user's AETHER password and Ratchet state inside the privileged Windows Service.

## Recovery

The service installer configures SCM failure actions:
- restart after 5 seconds;
- restart after 15 seconds;
- restart after 60 seconds;
- reset failure counter after 24 hours.

Graceful service stop writes agent.stopped to the AEGIS audit when Windows gives the process time to shut down.

## Troubleshooting

### Agent API is not reachable

Check:

~~~powershell
Get-Service AEGISAgent
Invoke-RestMethod http://127.0.0.1:8765/api/health
~~~

If the port is already used:

~~~powershell
Get-NetTCPConnection -LocalPort 8765
~~~

### Temperatures or fans are missing

Not all ECs/controllers expose sensors through LibreHardwareMonitor.

AEGIS reports unavailable data instead of inventing values.

Running elevated can expose additional sensors on some hardware.

### Windows Event Log is empty

The integration is read-only and only exposes System/Application warning/error/critical records.

Unavailable descriptions or denied access return empty/unavailable values; AEGIS does not escalate privileges.

### Service inventory is empty

The ServiceController API can fail under restricted Windows environments. The endpoint returns an empty list instead of mutating system permissions.

## Release verification

The main CI gate checks:
1. JavaScript syntax;
2. frontend structure;
3. PowerShell script syntax;
4. .NET restore;
5. Release build;
6. xUnit;
7. source runtime smoke;
8. self-contained win-x64 publish;
9. smoke of the published executable.

The release workflow repeats test/publish/smoke before uploading its artifact.
