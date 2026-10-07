# AEGIS SOC

Локальная Windows-система мониторинга для курсового проекта: web-интерфейс + реальный .NET agent + демо-слой интеграции AETHER.chat.

## Что теперь реально работает

- CPU load и температура, если датчик доступен
- RAM: total / used / load
- GPU load / temperature, если драйвер и датчик доступны
- температуры железа
- RPM вентиляторов, если материнская плата/контроллер их отдаёт
- диски и заполнение
- сеть: RX/TX в реальном времени
- список процессов Windows: PID, CPU, RAM, threads, путь где доступен
- WebSocket telemetry каждую секунду
- REST API для ручного просмотра данных
- фронтенд получает live-данные от локального backend
- AETHER.chat остаётся отдельным adapter-слоем
- CRASH SYSTEM остаётся безопасной визуальной симуляцией и не выключает компьютер

## Архитектура

```text
Windows sensors / processes
        │
        ▼
backend/Aegis.Agent (.NET 8)
        │
        ├── GET /api/health
        ├── GET /api/system
        ├── GET /api/hardware
        ├── GET /api/processes
        ├── GET /api/network
        └── WS  /ws/monitor
                │
                ▼
        HTML / CSS / JS dashboard
                │
                └── AETHER.chat adapter
```

Агент слушает только `127.0.0.1:8765`, то есть по умолчанию не публикуется в локальную сеть.

## Запуск на Windows

Самый простой вариант:

1. Клонировать репозиторий.
2. Установить .NET 8 SDK, если его нет.
3. Запустить `run-agent.bat`.
4. Скрипт запускает agent и открывает `http://127.0.0.1:8765`.
5. В интерфейсе открыть раздел **Мониторинг**.

Установка SDK через winget:

```powershell
winget install Microsoft.DotNet.SDK.8
```

Либо вручную:

```powershell
cd backend\Aegis.Agent
dotnet restore
dotnet run
```

## API

```text
GET http://127.0.0.1:8765/api/health
GET http://127.0.0.1:8765/api/system
GET http://127.0.0.1:8765/api/hardware
GET http://127.0.0.1:8765/api/processes?limit=50
GET http://127.0.0.1:8765/api/network
WS  ws://127.0.0.1:8765/ws/monitor
```

## Важный момент про датчики

Для hardware sensors используется LibreHardwareMonitor. Не каждый ноутбук/материнская плата предоставляет все датчики через стандартно доступные интерфейсы. Поэтому отсутствие, например, Fan RPM означает, что конкретный контроллер не отдал этот sensor; приложение не подменяет такие значения фейковыми.

На части устройств больше датчиков становятся доступны при запуске терминала/агента от администратора.

## AETHER.chat

`aether.js` сейчас является adapter boundary. Следующий этап реальной интеграции: подключить relay/API из `romantic52/AETHER.chat`, затем перенести E2E crypto в клиентскую часть до отправки ciphertext relay.
