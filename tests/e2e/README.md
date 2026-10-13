# Браузерная проверка AEGIS

Запускать только в изолированной Windows (Sandbox/VM) с установленным Playwright:

```powershell
$env:Agent__ListenUrl='http://127.0.0.1:8775'; $env:Agent__DataDirectory="$env:TEMP\aegis-e2e"
Start-Process .\dist\AEGIS\Aegis.Agent.exe
node tests/e2e/ui-lab-bsod.mjs http://127.0.0.1:8775 .\e2e-out
```

Проверяет: реальные показания в обзоре и мониторинге, оси и подсказку графиков, 5 тестов лаборатории + Стоп, демонстрацию синего экрана и выход Esc. Результат — e2e-out/qa-report.json и скриншоты.
