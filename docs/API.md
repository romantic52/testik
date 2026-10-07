# AEGIS API

Base URL:

~~~text
http://127.0.0.1:8765
~~~

Primary API prefix:

~~~text
/api/v1
~~~

Legacy `/api` aliases currently map to the same handlers for compatibility.

## Version / health

`GET /api/v1/version`
: API/service version.

`GET /api/v1/health`
: overall agent status and telemetry metadata.

`GET /api/v1/health/live`
: liveness only.

`GET /api/v1/health/ready`
: readiness. HTTP 200 requires a fresh telemetry frame and a healthy SQLite persistence probe; otherwise HTTP 503.

## Diagnostics

`GET /api/v1/diagnostics`

Shape:

~~~json
{
  "agent": {},
  "persistence": {
    "provider": "sqlite",
    "database": "aegis.db",
    "revision": 42
  }
}
~~~

Agent diagnostics include version, PID, service/console mode, uptime, memory/GC, thread/handle counts, telemetry age/history and active data directory.

## Telemetry / investigation

`GET /api/v1/system`
: latest system snapshot.

`GET /api/v1/hardware`
: hardware sensors from the same sample.

`GET /api/v1/history?seconds=900`
: bounded telemetry history.

`GET /api/v1/processes?limit=50`
: sampled process list; backend caps the limit.

`GET /api/v1/processes/{pid}`
: on-demand process details, including available path/file metadata, bounded SHA-256 and embedded signer certificate metadata. Signer metadata is not a full trust-chain verification result.

`GET /api/v1/network`
: sampled adapter counters.

`GET /api/v1/network/connections?limit=200`
: active TCP connections. IPv4 rows include owner PID/process when Windows IP Helper returns it. IPv6 endpoints are preserved and may have null owner fields.

`GET /api/v1/network/udp?limit=200`
: active UDP listeners.

`GET /api/v1/windows/events?log=System&limit=100`
: read-only Warning/Error/Critical events. Only System/Application are allow-listed.

`GET /api/v1/windows/services?limit=500`
: read-only service inventory.

`GET /api/v1/connectors`
: truthfulness/capability surface for real vs not-configured integrations.

Snapshot endpoints can return 503 while telemetry warms up.

## Incidents

`GET /api/v1/incidents`

`PUT /api/v1/incidents/{id}`

`DELETE /api/v1/incidents/{id}`

Incident fields:
- id
- title
- source
- severity
- status
- description
- events[]
- auto
- aetherSent
- ruleKey
- createdAt

Incident mutations are persisted in SQLite. Create/update/delete operations generate backend audit entries transactionally.

## Alert settings

`GET /api/v1/settings/alerts`

`PUT /api/v1/settings/alerts`

Fields:
- enabled
- autoAether
- cpuLoad
- cpuTemp
- gpuTemp
- ram
- processCpu
- disk
- sustainSeconds
- cooldownMinutes

Values are normalized to backend safe ranges and stored in SQLite.

## Audit

`GET /api/v1/audit?limit=200`

`POST /api/v1/audit`

The audit store is operational, not cryptographically tamper-proof.

Automatic backend incident lifecycle events also write audit entries.

## Reports

`GET /api/v1/reports/current.json`
: current operational report.

`GET /api/v1/reports/incidents.csv`
: incident export.

`GET /api/v1/reports/bundle.zip`
: support ZIP containing report/CSV/manifest. AETHER password and browser Ratchet state are excluded.

## AETHER bridge

`POST /api/v1/aether/relay`

This is an allow-listed protocol bridge, not a generic HTTP proxy.

Backend restrictions include method/path allow-listing, remote HTTPS, no URL credentials, redirects disabled and bounded timeout.

## WebSocket

~~~text
WS /ws/monitor
~~~

Example:

~~~json
{
  "type": "telemetry",
  "system": {},
  "processes": [],
  "incidentRevision": 42
}
~~~

The WebSocket publishes the latest `TelemetrySamplerService` frame; it does not independently sample the machine.
