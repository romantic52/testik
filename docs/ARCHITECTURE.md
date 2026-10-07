# AEGIS Architecture

## Цель

AEGIS — локальный Windows monitoring/SOC agent. Он разделён на две части:

1. Aegis.Agent — источник данных, rule engine, persistence и API.
2. Browser UI — операторская консоль и клиент E2E AETHER.chat.

Критически важная логика мониторинга не зависит от открытой вкладки браузера.

## Поток данных

~~~text
Windows sensors / processes / network
                |
                v
        TelemetrySamplerService
          1 snapshot / interval
                |
      +---------+----------+
      |                    |
      v                    v
REST + WebSocket      AlertEngineService
      |                    |
      |                    v
      |             IncidentStoreService
      |                    |
      +---------+----------+
                |
                v
           Operator UI
                |
                v
       AETHER Double Ratchet
                |
                v
            AETHER relay
~~~

## Single-source telemetry

TelemetrySamplerService — единственный компонент, который периодически снимает delta-зависимые метрики.

Это важно для:
- CPU процесса;
- RX/TX сети;
- единообразия REST и WebSocket;
- истории нагрузки.

API не запускает повторный сбор. Он читает последний immutable-like snapshot.

## Backend layers

### Configuration

Configuration/AgentOptions.cs

Отвечает за:
- listen URL;
- sampling interval;
- process limit;
- history window;
- data directory;
- AETHER HTTP timeout.

Настройки загружаются через стандартный .NET configuration pipeline из appsettings.json и могут быть переопределены environment variables.

### Models

Models/TelemetryModels.cs

Содержит контракты:
- TelemetryFrame;
- TelemetryHistoryPoint;
- AgentStatus.

### Services

HardwareMonitorService
: LibreHardwareMonitor sensors, memory и disks.

NetworkMonitorService
: network counters и throughput delta.

ProcessMonitorService
: process CPU/RAM sample.

TelemetrySamplerService
: background single-source sampler + bounded history.

AlertSettingsService
: persistent thresholds.

AlertEngineService
: background rules; работает без открытого UI.

IncidentStoreService
: persistent incidents + JSONL audit.

ReportService
: report из уже sampled telemetry, incidents, history и settings.

AetherRelayProxyService
: allow-listed bridge к AETHER relay.

AgentLifecycleService
: agent.started / agent.stopped audit.

### Endpoints

Endpoints/AegisEndpoints.cs содержит HTTP/WebSocket surface. Program.cs остаётся composition root.

### Infrastructure

Infrastructure/AegisWebExtensions.cs отвечает за:
- Host header guard;
- security headers;
- Content-Security-Policy;
- static web hosting;
- no-store для API.

## Persistence

По умолчанию:

~~~text
%LocalAppData%\AEGIS\
  incidents.json
  audit.jsonl
  alert-settings.json
~~~

IncidentStoreService использует temp-file + replace для incidents.json.

Audit хранится как настоящий JSON Lines: одна JSON-запись на строку.

## Incident lifecycle

~~~text
Telemetry rule / operator
          |
          v
      IncidentRecord
          |
          v
 IncidentStoreService
          |
    +-----+------+
    |            |
    v            v
 audit.jsonl   WebSocket revision
                 |
                 v
               UI refresh
                 |
                 v
        optional AETHER send
~~~

AETHER encryption остаётся client-side намеренно: crypto state и пароль пользователя не переносятся в monitoring backend.

## Rule engine

AlertEngineService выполняется в Agent и не зависит от браузера.

Текущие правила:
- telemetry stale;
- CPU load;
- CPU temperature;
- RAM load;
- GPU temperature;
- disk usage;
- high temperature + very low fan RPM, когда fan sensor реально доступен;
- sustained process CPU anomaly.

У каждого правила есть sustain и cooldown.

High CPU процесса трактуется как ресурсная аномалия, а не доказательство вредоносности.

## Frontend

Frontend остаётся framework-free специально для курсового MVP:
- index.html — layout;
- styles.css — visual layer;
- app.js — UI/API orchestration;
- aether.js — crypto/messenger boundary.

Source of truth для incidents и alert settings — backend, не localStorage.

Overview строится из реальной telemetry/history. СКУД и Cameras честно отображаются как not configured до подключения реальных connectors.

## Packaging

Aegis.Agent.csproj включает frontend/vendor в published wwwroot.

build-release.bat:
1. restore;
2. tests;
3. self-contained win-x64 publish;
4. smoke test packaged executable.

run-agent.bat использует packaged EXE, если он существует, иначе source dotnet run.

## CI quality gates

Build workflow:
1. frontend syntax check;
2. restore;
3. Release build;
4. xUnit tests;
5. runtime smoke test.

Package workflow:
1. tests;
2. self-contained publish;
3. smoke test published Aegis.Agent.exe;
4. artifact upload.


## Windows Service hosting

The same Aegis.Agent executable supports both console and Windows Service execution.

Microsoft.Extensions.Hosting.WindowsServices is context-aware: UseWindowsService activates WindowsServiceLifetime only when SCM hosts the process.

The install script points SCM at the verified published executable and gives service-mode persistence an explicit %ProgramData%\AEGIS directory.

AgentDiagnosticsService reports whether the current process is actually hosted by Windows Service Control Manager.

Automatic monitoring and AlertEngineService are backend hosted services, so they continue with no browser open. AETHER E2E delivery remains browser-owned and pending incidents are sent when an operator client connects.
