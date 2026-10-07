# AEGIS SOC

AEGIS — локальный Windows SOC/monitoring MVP для курсового проекта с реальным .NET-агентом, web-интерфейсом и E2E-интеграцией с [AETHER.chat](https://github.com/romantic52/AETHER.chat).

## Что реально работает

### Windows monitoring
- CPU load и температура, если датчик доступен.
- RAM: total / used / load.
- GPU load и температура.
- hardware temperatures.
- Fan RPM, если контроллер/плата их предоставляет.
- диски и заполнение.
- сеть RX/TX.
- процессы Windows: PID, CPU, RAM, threads и путь.
- detail-карточка процесса: start time, private memory, handles, priority, responding.
- live telemetry через WebSocket раз в секунду.

### SOC
- ручные инциденты;
- автоматические инциденты из live telemetry;
- sustain-порог и cooldown против спама;
- закрытие и удаление инцидентов;
- persistent incident store;
- audit log;
- JSON report со снимком системы, процессов, incidents и audit;
- CSV export инцидентов.

Данные SOC сохраняются в:

```text
%LocalAppData%\AEGIS
```

### Автоматические правила

Из UI настраиваются пороги:

- CPU load;
- CPU temperature;
- GPU temperature;
- RAM load;
- process CPU;
- disk used;
- sustain time;
- cooldown.

Также есть detection потери AEGIS Agent и эвристика: высокая температура + доступный fan sensor с почти нулевым RPM.

AEGIS не объявляет процесс вредоносным только из-за высокой нагрузки — создаётся именно ресурсный/аномальный incident для проверки.

### AETHER.chat

Правая панель использует настоящий AETHER relay и текущий Double Ratchet из проекта `romantic52/AETHER.chat`.

- штатный `POST /users/login`;
- TOTP/2FA;
- отдельное устройство `soc-*`;
- signed prekeys;
- fallback key;
- master/device key verification;
- encrypted envelope отдельно на каждое устройство peer;
- inbox + ACK;
- realtime через AETHER WebSocket;
- polling fallback;
- автоматическая отправка созданных AEGIS incidents.

Relay получает ciphertext, а Ratchet работает на клиенте.

Canonical crypto vendor:

```text
vendor/nacl.min.js
vendor/ratchet/aether_ratchet_wasm.js
vendor/ratchet/aether_ratchet_wasm_bg.wasm
```

WASM синхронизируется workflow `.github/workflows/sync-aether-vendor.yml`.

## Архитектура

```text
Windows
  │
  ├─ LibreHardwareMonitor
  ├─ Process API
  ├─ NetworkInterface
  │
  ▼
Aegis.Agent (.NET 8)
  │
  ├─ REST API
  ├─ WebSocket telemetry
  ├─ incident store
  ├─ audit log
  ├─ report generation
  └─ allow-listed AETHER relay bridge
        │
        ▼
HTML / CSS / JS
  │
  ├─ monitoring dashboard
  ├─ automatic rules
  ├─ incidents / audit / reports
  └─ AETHER Double Ratchet client
```

Agent слушает только:

```text
http://127.0.0.1:8765
```

и по умолчанию не публикует monitoring API в локальную сеть.

## Быстрый запуск из исходников

Нужен .NET 8 SDK.

```powershell
winget install Microsoft.DotNet.SDK.8
```

После этого:

```text
run-agent.bat
```

Откроется:

```text
http://127.0.0.1:8765
```

## Windows release bundle

Собрать self-contained Windows x64 пакет:

```text
build-release.bat
```

Результат:

```text
dist\AEGIS\Aegis.Agent.exe
```

После сборки `run-agent.bat` автоматически использует published executable вместо `dotnet run`.

Также есть workflow:

```text
.github/workflows/package.yml
```

который собирает artifact `AEGIS-windows-x64` вручную или при push тега `v*`.

## API

```text
GET    /api/health
GET    /api/system
GET    /api/hardware
GET    /api/processes?limit=50
GET    /api/processes/{pid}
GET    /api/network

GET    /api/incidents
PUT    /api/incidents/{id}
DELETE /api/incidents/{id}

GET    /api/audit?limit=200
POST   /api/audit

GET    /api/reports/current.json
GET    /api/reports/incidents.csv

POST   /api/aether/relay

WS     /ws/monitor
```

## Безопасность AETHER bridge

`/api/aether/relay` — не универсальный HTTP proxy. Backend разрешает только конкретные AETHER endpoints, необходимые клиенту.

Для удалённого relay требуется HTTPS. HTTP разрешён только для loopback/localhost.

Пароль AETHER не сохраняется приложением. Он используется в памяти клиента для входа и расшифровки encrypted key backup.

## Hardware sensors

Для датчиков используется `LibreHardwareMonitorLib`.

Не каждое железо отдаёт все sensors. Если motherboard, laptop EC или fan controller не предоставляет RPM/temperature через доступный интерфейс, AEGIS показывает отсутствие данных, а не подставляет фиктивное значение.

На части компьютеров запуск от администратора открывает больше hardware sensors.

## Что в курсовом остаётся демонстрационным

СКУД, камеры и корпоративная inventory-секция сейчас являются UI-модулями презентационного уровня: к реальным физическим контроллерам/камерам они не подключены.

Кнопка `CRASH SYSTEM` — только безопасная визуальная демонстрация BSOD/offline screen. Она не завершает работу Windows и не выполняет destructive system actions.

## CI

`.github/workflows/build.yml` проверяет:

- syntax `app.js`;
- syntax `aether.js`;
- restore .NET dependencies;
- Release build Windows agent.

Главная ветка должна оставаться buildable после каждого изменения.
