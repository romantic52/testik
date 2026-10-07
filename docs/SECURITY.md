# AEGIS Security Model

## Scope

AEGIS — local-first Windows monitoring application.

Главная граница доверия:
- monitoring Agent имеет доступ к локальным system/process/hardware данным;
- browser UI получает их только через loopback API;
- AETHER plaintext/crypto state остаётся в browser client.

## Network exposure

Default listen URL:

~~~text
http://127.0.0.1:8765
~~~

Agent не должен быть опубликован в LAN без отдельной threat-model ревизии и authentication layer.

Host header middleware разрешает только:
- 127.0.0.1;
- localhost.

## Browser security headers

Agent выставляет:
- X-Content-Type-Options: nosniff;
- X-Frame-Options: DENY;
- Referrer-Policy: no-referrer;
- restrictive Permissions-Policy;
- Content-Security-Policy;
- no-store для API.

CSP разрешает local scripts/styles и wasm-unsafe-eval только потому, что canonical AETHER Double Ratchet runtime использует WebAssembly.

Обычный unsafe-eval не разрешён.

## AETHER

AEGIS не переносит E2E crypto в backend.

Browser:
- принимает пароль пользователя только для login/key backup decryption;
- создаёт отдельный soc-* device;
- хранит encrypted local Ratchet state;
- проверяет AETHER device/master signatures;
- шифрует до relay;
- расшифровывает после relay.

Backend bridge видит protocol envelopes, но не должен получать chat plaintext от UI.

### Relay SSRF boundary

AetherRelayProxyService:
- не принимает произвольный destination path;
- использует allow-list AETHER endpoints;
- запрещает URL credentials;
- запрещает remote plain HTTP;
- отключает redirects;
- имеет configured timeout.

Это снижает риск превращения local Agent в generic SSRF proxy.

## Persistence

incidents.json и alert-settings.json находятся в user LocalAppData.

audit.jsonl — operational audit, но не cryptographically immutable log. Термин immutable-like означает append-oriented semantics, а не tamper-proof storage.

Для production-grade tamper evidence понадобились бы:
- hash chaining;
- signing;
- protected remote log sink.

## Hardware permissions

AEGIS не пытается обходить Windows security boundaries.

Если protected process path, temperature или fan RPM недоступны:
- значение остаётся unavailable/null;
- UI не подставляет fake data.

Administrator execution может открыть больше hardware sensors, но не является обязательным режимом.

## Automatic rules

Rules создают incidents и не выполняют destructive remediation.

Нет:
- kill process;
- remote execution;
- Windows shutdown;
- firewall mutation;
- credential changes.

Кнопка CRASH SYSTEM — только визуальная demo simulation.

## UI truthfulness

Overview не содержит фиктивных host counts.

Access Control и Cameras не показываются как live, пока real connector не настроен.

High process CPU обозначается как anomaly requiring review, не как malware detection.

## Release verification

Release считается валидным только после:
- xUnit tests;
- publish;
- launching packaged executable;
- /api/health;
- incident endpoint;
- alert settings endpoint;
- history endpoint.

Это проверяется scripts/smoke-agent.ps1.


## Windows Event Log

Event Log integration is read-only.

AEGIS allow-lists only:
- System;
- Application.

The Security log and arbitrary log paths are not exposed by the API.

If Windows denies access or event descriptions cannot be resolved, AEGIS returns unavailable/empty data rather than attempting privilege escalation.


## Windows Services

Service integration is inventory-only.

AEGIS reads service name/display name/status/type and capability flags. It exposes no Start, Stop, Pause, Restart or configuration endpoint.

An operator may create an incident from a suspicious or unexpected service state, but remediation is intentionally outside the current Agent.
