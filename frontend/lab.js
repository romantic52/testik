// AEGIS lab roulette. Only short, capped CPU/RAM checks are executable.
// All other wheel outcomes open read-only diagnostics; no BSOD is triggered here.
(() => {
  "use strict";
  const $ = selector => document.querySelector(selector);
  const options = [
    { id: "cpu", title: "Процессор", detail: "Реальный короткий вычислительный тест в отдельном Web Worker.", runnable: true },
    { id: "ram", title: "Оперативная память", detail: "Проверка реальным выделением до 64 МиБ в изолированном Web Worker.", runnable: true },
    { id: "gpu", title: "Видеокарта", detail: "Проверка температуры и загрузки GPU по датчикам AEGIS. Без искусственной нагрузки.", runnable: false },
    { id: "disk", title: "Накопители", detail: "Проверка свободного пространства. Без записи тестовых файлов.", runnable: false },
    { id: "network", title: "Сеть", detail: "Проверка текущих скоростей сети. Без генерации трафика.", runnable: false }
  ];

  let selected = null;
  let wheelAngle = 0;
  let wheelBusy = false;
  let worker = null;
  let completionTimer = null;
  let spinnerTimer = null;
  let running = false;
  let timeEnds = 0;

  const msg = text => { $("#labOutcome").textContent = text; };
  const setRunning = value => {
    running = value;
    $("#labStart").disabled = !selected || (selected.runnable && (!$("#labConsent").checked || value));
    $("#labStop").disabled = !value;
    $("#labWheelButton").disabled = wheelBusy || value;
    $("#labStatus").textContent = value ? "ТЕСТ ИДЁТ" : "ГОТОВО";
    $("#labStatus").classList.toggle("is-running", value);
    $("#labTuning").hidden = selected?.id !== "ram";
  };

  const stop = (reason = "Тест остановлен.") => {
    if (worker) { worker.terminate(); worker = null; }
    if (completionTimer) clearTimeout(completionTimer);
    if (spinnerTimer) clearInterval(spinnerTimer);
    completionTimer = null;
    spinnerTimer = null;
    running = false;
    $("#labTimer").textContent = "00:00";
    setRunning(false);
    msg(reason);
  };

  function updateMeasurement() {
    const s = typeof lastTelemetryPayload === "undefined" ? null : lastTelemetryPayload?.system;
    const fields = [
      ["cpu", s?.cpuLoadPercent, "%", 65, 85],
      ["ram", s?.memory?.loadPercent, "%", 75, 90],
      ["gpu", s?.gpus?.[0]?.loadPercent, "%", 65, 90],
      ["disk", s?.disks?.length ? Math.max(...s.disks.map(d => Number(d.usedPercent) || 0)) : null, "%", 85, 95],
      ["network", s?.network?.totalReceiveBytesPerSecond, " Б/с RX", 10000000, 50000000]
    ];
    for (const [id, raw, unit, warn, high] of fields) {
      const value = raw == null ? null : Number(raw);
      const el = $("#labActual-" + id);
      const led = $("#labIndicator-" + id);
      if (!el || !led) continue;
      const valid = value != null && Number.isFinite(value);
      el.textContent = valid ? value.toFixed(id === "network" ? 0 : 1) + unit : "Нет данных";
      led.classList.toggle("warn", valid && value >= warn);
      led.classList.toggle("high", valid && value >= high);
    }
  }

  function onSelected(option) {
    selected = option;
    $("#labSelectedTitle").textContent = option.title;
    $("#labSelectedDescription").textContent = option.detail;
    $("#labSelectedIcon").textContent =
      ({ cpu: "◉", ram: "▦", gpu: "◈", disk: "▤", network: "⌁" })[option.id];
    $("#labTuning").hidden = option.id !== "ram";
    $("#labConsent").checked = false;
    $("#labStart").textContent = option.runnable ? "▶ Запустить тест (10 сек)" : "Открыть мониторинг";
    $("#labConsentLabel").hidden = !option.runnable;
    $("#labStart").disabled = option.runnable && !$("#labConsent").checked;
    $("#labActions").hidden = false;
    $("#labOutcome").textContent = option.runnable
      ? "Подтверди безопасную короткую проверку и нажми запуск."
      : "Режим диагностики: измерения уже поступают от Windows Agent.";
  }

  $("#labWheelButton").addEventListener("click", () => {
    if (wheelBusy || running) return;
    wheelBusy = true;
    selected = null;
    $("#labActions").hidden = true;
    $("#labWheelButton").disabled = true;
    $("#labOutcome").textContent = "Рулетка выбирает проверку...";
    const bytes = new Uint32Array(1);
    crypto.getRandomValues(bytes);
    const index = bytes[0] % options.length;
    // CSS conic-gradient centers the first segment at the top (0°).
    const target = (360 - index * 72) % 360;
    const current = ((wheelAngle % 360) + 360) % 360;
    const correction = (target - current + 360) % 360;
    wheelAngle += 360 * 6 + correction;
    $("#labWheel").style.transform = "rotate(" + wheelAngle + "deg)";
    setTimeout(() => {
      wheelBusy = false;
      $("#labWheelButton").disabled = false;
      onSelected(options[index]);
    }, 3550);
  });

  $("#labConsent").addEventListener("change", () => setRunning(false));
  $("#labMemory").addEventListener("input", event => {
    $("#labMemoryAmount").textContent = event.target.value + " МиБ";
  });

  $("#labStart").addEventListener("click", () => {
    if (!selected || wheelBusy || running) return;
    if (!selected.runnable) {
      if (typeof view === "function") view("monitoring");
      return;
    }
    if (!$("#labConsent").checked) {
      msg("Сначала подтверди запуск ограниченного теста.");
      return;
    }
    if (typeof Worker === "undefined") {
      msg("Web Worker недоступен в этом браузере.");
      return;
    }
    stop("");
    const thisWorker = new Worker("frontend/lab-worker.js");
    worker = thisWorker;
    setRunning(true);
    timeEnds = Date.now() + 10000;
    msg("Выполняется настоящая ограниченная проверка " + selected.title.toLowerCase() + "...");
    const settings = {
      type: "start", kind: selected.id, durationMs: 10000,
      memoryMiB: Number($("#labMemory").value)
    };
    thisWorker.onmessage = event => {
      if (worker !== thisWorker) return;
      if (event.data?.type === "error") stop("Ошибка проверки: " + event.data.message);
      if (event.data?.type === "allocated") msg("Выделено " + event.data.allocatedMiB + " МиБ RAM. Через 10 сек память будет освобождена.");
      if (event.data?.type === "complete") stop("Проверка завершена. Ресурсы освобождены.");
    };
    thisWorker.onerror = () => stop("Ошибка вычислительного потока; проверка остановлена.");
    thisWorker.postMessage(settings);
    completionTimer = setTimeout(() => stop("Лимит времени достигнут. Тест остановлен."), 12000);
    spinnerTimer = setInterval(() => {
      const seconds = Math.max(0, Math.ceil((timeEnds - Date.now()) / 1000));
      $("#labTimer").textContent = "00:" + String(seconds).padStart(2, "0");
    }, 250);
  });

  $("#labStop").addEventListener("click", () => stop("Остановлено пользователем."));
  addEventListener("pagehide", () => stop(""));
  document.addEventListener("visibilitychange", () => {
    if (document.hidden && running) stop("Тест остановлен при переключении окна.");
  });

  setInterval(updateMeasurement, 1000);
  updateMeasurement();
  setRunning(false);
})();
