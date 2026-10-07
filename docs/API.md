# AEGIS API

Base URL:

~~~text
http://127.0.0.1:8765
~~~

API предназначен для локальной operator console. Agent по умолчанию не слушает LAN-интерфейс.

## Health

GET /api/health

Возвращает:
- status: ok или warming_up;
- service/version;
- machine;
- current timestamp;
- last telemetry sample;
- sample interval;
- number of history points;
- persistence directory.

## Telemetry

GET /api/system
: последний SystemSnapshot.

GET /api/hardware
: sensors из того же telemetry sample.

GET /api/network
: network snapshot из того же sample.

GET /api/network/connections?limit=200
: on-demand active TCP endpoints и TCP state.

GET /api/network/udp?limit=200
: on-demand active UDP listeners.

GET /api/windows/events?log=System&limit=100
: read-only Critical / Error / Warning events from allow-listed System or Application logs.

GET /api/windows/services?limit=500
: read-only Windows service inventory: name, display name, status, service type and capability flags.

GET /api/processes?limit=50
: sampled processes. limit ограничен 1..200.

GET /api/processes/{pid}
: on-demand detail процесса, если Windows разрешает чтение. Включает file size, version/product/company metadata и SHA-256 исполняемого файла, когда путь доступен и файл не превышает safety limit.

GET /api/history?seconds=900
: bounded telemetry history. Window ограничен configured history retention.

GET /api/connectors
: capability/status surface: monitoring, hardware, processes, network, AETHER, access control, cameras.

Если initial telemetry ещё не готова, snapshot endpoints возвращают HTTP 503.

## Incidents

GET /api/incidents
: полный persistent incident list, newest first.

PUT /api/incidents/{id}
: create/update incident. Path id должен совпадать с body id.

DELETE /api/incidents/{id}
: delete incident.

Incident fields:
- id;
- title;
- source;
- severity;
- status;
- description;
- events[];
- auto;
- aetherSent;
- ruleKey;
- createdAt.

## Alert settings

GET /api/settings/alerts

PUT /api/settings/alerts

Поля:
- enabled;
- autoAether;
- cpuLoad;
- cpuTemp;
- gpuTemp;
- ram;
- processCpu;
- disk;
- sustainSeconds;
- cooldownMinutes.

Backend нормализует значения в безопасные диапазоны.

## Audit

GET /api/audit?limit=200
: newest audit records. limit ограничивается backend.

POST /api/audit
: operator/client audit event.

Автоматические create/update/delete incidents также пишутся backend самостоятельно.

## Reports

GET /api/reports/current.json

Содержит:
- agent metadata;
- current system snapshot;
- process snapshot;
- 15-minute history;
- current alert settings;
- incidents;
- audit.

GET /api/reports/incidents.csv
: CSV export incidents.

## AETHER bridge

POST /api/aether/relay

Это не generic proxy.

Body содержит:
- serverUrl;
- method;
- path;
- token;
- optional JSON body.

Backend проверяет:
- allowed HTTP method;
- allow-listed AETHER path;
- remote HTTPS requirement;
- no embedded URL credentials;
- timeout.

HTTP для AETHER server разрешён только loopback.

## WebSocket

WS /ws/monitor

Payload type telemetry:

~~~json
{
  "type": "telemetry",
  "system": {},
  "processes": [],
  "incidentRevision": 12
}
~~~

incidentRevision позволяет UI понять, что backend rule engine или другой operator изменил incident store, и перечитать /api/incidents.

WebSocket сам не собирает telemetry — он публикует latest sample TelemetrySamplerService.
