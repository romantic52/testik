// AEGIS Lab UI: roulette, measured test results, explicit consent and emergency Stop.
// Workloads are isolated in AegisLabTests, never triggered by the wheel itself.
(() => {
  "use strict";
  const $ = selector => document.querySelector(selector);
  const kinds = [
    { id: "cpu", name: "Процессор", description: "Вычислительный тест одного CPU-потока с интенсивностью 20–80%." },
    { id: "ram", name: "Оперативная память", description: "Выделение и проверка 16–128 МиБ памяти с автоматическим освобождением." },
    { id: "gpu", name: "Видеокарта", description: "Реальная отрисовка WebGL2 с подсчётом FPS (требуется 3D-ускорение)." },
    { id: "disk", name: "Накопители", description: "Запись и проверка 8–32 МиБ во временном файле AEGIS." },
    { id: "network", name: "Сеть", description: "Замер скорости обмена 3 МиБ через 127.0.0.1, без внешнего трафика." }
  ];
  const iconFor = { cpu: "◉", ram: "▦", gpu: "◈", disk: "▤", network: "⌁" };
  let selected = null;
  let wheelBusy = false;
  let spinTimer = null;
  let wheelAngle = 0;
  let active = null;
  let countdown = null;
  let watchdog = null;
  let history = [];
  try {
    const value = JSON.parse(localStorage.getItem("aegis_lab_results") || "[]");
    if (Array.isArray(value)) history = value.slice(0, 20);
  } catch {}

  function feedback(message) {
    $("#labOutcome").textContent = message;
  }

  function updateUi() {
    const running = active !== null;
    $("#labStatus").textContent = running ? "ТЕСТ ИДЁТ" : wheelBusy ? "РУЛЕТКА" : "ГОТОВО";
    $("#labStatus").classList.toggle("is-running", running);
    $("#labWheelButton").disabled = wheelBusy || running;
    $("#labStart").disabled = wheelBusy || running || !selected || !$("#labConsent").checked;
    $("#labStop").disabled = !running;
    $("#labTuning").hidden = !selected;
    $("#labDurationRow").hidden = !selected || selected.id === "disk" || selected.id === "network";
    $("#labCpuRow").hidden = selected?.id !== "cpu";
    $("#labRamRow").hidden = selected?.id !== "ram";
    $("#labDiskRow").hidden = selected?.id !== "disk";
    const notes = {
      disk: "Файл создаётся только в каталоге TEMP и всегда удаляется после проверки.",
      network: "Тест проходит только через локальный агент; это не скорость интернета.",
      gpu: "Браузер может использовать программный рендеринг, если в VM нет GPU-ускорения.",
      cpu: "Загрузка ограничена одним потоком. Общая загрузка зависит от числа ядер.",
      ram: "128 МиБ — верхний предел: тест не пытается заполнить всю RAM."
    };
    $("#labSafetyNotice").textContent = selected ? notes[selected.id] : "";
  }

  function choose(entry) {
    selected = entry;
    $("#labSelectedTitle").textContent = entry.name;
    $("#labSelectedDescription").textContent = entry.description;
    $("#labSelectedIcon").textContent = iconFor[entry.id];
    $("#labConsent").checked = false;
    $("#labActions").hidden = false;
    $("#labGpuCanvas").hidden = true;
    $("#labResultSummary").hidden = true;
    $("#labTimer").textContent = "00:00";
    feedback("Настрой параметры, подтверди запуск и нажми «Проверить».");
    updateUi();
  }

  $("#labWheelButton").addEventListener("click", () => {
    if (wheelBusy || active) return;
    wheelBusy = true;
    selected = null;
    $("#labActions").hidden = true;
    $("#labResultSummary").hidden = true;
    updateUi();
    feedback("Выбираем следующий тест...");
    const random = new Uint32Array(1);
    crypto.getRandomValues(random);
    const index = random[0] % kinds.length;
    const target = (360 - index * 72) % 360;
    const current = ((wheelAngle % 360) + 360) % 360;
    wheelAngle += 360 * 6 + (target - current + 360) % 360;
    $("#labWheel").style.transform = "rotate(" + wheelAngle + "deg)";
    spinTimer = setTimeout(() => {
      spinTimer = null;
      wheelBusy = false;
      choose(kinds[index]);
    }, 3550);
  });

  $("#labConsent").addEventListener("change", updateUi);
  $("#labMemory").addEventListener("input", event => {
    $("#labMemoryAmount").textContent = event.target.value + " МиБ";
  });
  $("#labCpuIntensity").addEventListener("input", event => {
    $("#labCpuAmount").textContent = event.target.value + "%";
  });

  function saveHistory() {
    try { localStorage.setItem("aegis_lab_results", JSON.stringify(history)); } catch {}
    const list = $("#labRunHistory");
    list.replaceChildren();
    if (!history.length) {
      const empty = document.createElement("p");
      empty.className = "muted";
      empty.textContent = "Проверок пока не было.";
      list.append(empty);
      return;
    }
    for (const item of history) {
      const row = document.createElement("div");
      row.className = "lab-run-row";
      const time = document.createElement("time");
      time.textContent = new Date(item.timestamp).toLocaleString("ru-RU");
      const kind = document.createElement("strong");
      kind.textContent = item.kind.toUpperCase();
      const status = document.createElement("span");
      status.textContent = item.status;
      status.className = item.status === "Успешно" ? "lab-good" : "lab-warning";
      const desc = document.createElement("small");
      desc.textContent = item.summary || "";
      row.append(time, kind, status, desc);
      list.append(row);
    }
  }

  function summaryFor(kind, metrics) {
    switch (kind) {
      case "cpu": return Math.round(metrics.iterationsPerSecond).toLocaleString("ru-RU")
        + " вычислений/с при интенсивности " + metrics.dutyPercent + "% одного потока";
      case "ram": return metrics.allocatedMiB + " МиБ выделено; страницы "
        + (metrics.verified ? "проверены" : "повреждены");
      case "gpu": return metrics.fps.toFixed(1) + " FPS; кадры " + metrics.frames + "; " + metrics.resolution;
      case "disk": return "Запись " + metrics.writeMiBps.toFixed(1) + " МиБ/с, чтение "
        + metrics.readMiBps.toFixed(1) + " МиБ/с; содержимое " + (metrics.verified ? "проверено" : "не проверено");
      case "network": return metrics.mibps.toFixed(1) + " МиБ/с; "
        + metrics.totalMiB.toFixed(1) + " МиБ по loopback";
      default: return "Результат получен.";
    }
  }

  function release() {
    if (!active) return null;
    const task = active;
    active = null;
    task.controller.abort();
    if (task.worker) task.worker.terminate();
    if (task.cleanup) task.cleanup();
    if (countdown) clearInterval(countdown);
    if (watchdog) clearTimeout(watchdog);
    countdown = null;
    watchdog = null;
    $("#labTimer").textContent = "00:00";
    updateUi();
    return task;
  }

  function complete(status, metrics = {}, note = "") {
    const task = release();
    if (!task) return;
    const result = {
      timestamp: new Date().toISOString(),
      kind: task.kind,
      status,
      durationSeconds: Number(((performance.now() - task.began) / 1000).toFixed(1)),
      metrics,
      summary: status === "Успешно" ? summaryFor(task.kind, metrics) : note
    };
    history.unshift(result);
    history = history.slice(0, 20);
    saveHistory();

    const summary = $("#labResultSummary");
    summary.replaceChildren();
    const title = document.createElement("strong");
    title.textContent = result.kind.toUpperCase() + " · " + result.status;
    const desc = document.createElement("p");
    desc.textContent = result.summary;
    summary.append(title, desc);
    summary.hidden = false;
    feedback(status === "Успешно"
      ? "Проверка завершена. Результат сохранён."
      : status + ": " + note);
  }

  $("#labStop").addEventListener("click", () => {
    if (active) complete("Остановлен", {}, "Проверка остановлена вручную.");
  });

  $("#labStart").addEventListener("click", async () => {
    if (!selected || wheelBusy || active || !$("#labConsent").checked) return;
    const task = {
      kind: selected.id,
      began: performance.now(),
      durationMs: Number($("#labDuration").value) * 1000,
      controller: new AbortController(),
      worker: null, cleanup: null
    };
    active = task;
    updateUi();
    $("#labResultSummary").hidden = true;
    const timeLimit = ["disk", "network"].includes(task.kind) ? 30000 : task.durationMs;
    feedback("Проводится настоящий тест «" + selected.name + "»...");
    const deadline = performance.now() + timeLimit;
    countdown = setInterval(() => {
      const seconds = Math.max(0, Math.ceil((deadline - performance.now()) / 1000));
      $("#labTimer").textContent = "00:" + String(seconds).padStart(2, "0");
    }, 250);
    watchdog = setTimeout(() => {
      if (active === task) complete("Прерван", {}, "Сработал тайм-аут теста.");
    }, timeLimit + 2500);

    try {
      const settings = {
        durationMs: task.durationMs,
        cpuDutyPercent: Number($("#labCpuIntensity").value),
        memoryMiB: Number($("#labMemory").value),
        sizeMiB: Number($("#labDiskSize").value)
      };
      const metrics = await window.AegisLabTests[task.kind](
        task, settings, text => { if (active === task) feedback(text); }
      );
      if (active === task) complete("Успешно", metrics);
    } catch (error) {
      if (active === task) complete("Ошибка", {}, error?.message || String(error));
    }
  });

  $("#labExport").addEventListener("click", () => {
    const machine = typeof lastTelemetryPayload === "undefined"
      ? null : lastTelemetryPayload?.system?.machineName;
    const data = { product: "AEGIS Lab", timestamp: new Date().toISOString(), machine, results: history };
    const url = URL.createObjectURL(new Blob([JSON.stringify(data, null, 2)], { type: "application/json" }));
    const link = document.createElement("a");
    link.href = url;
    link.download = "aegis-lab-results.json";
    document.body.append(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1500);
  });

  $("#labClear").addEventListener("click", () => {
    history = [];
    localStorage.removeItem("aegis_lab_results");
    saveHistory();
    $("#labResultSummary").hidden = true;
    feedback("История тестов очищена.");
  });

  function readings() {
    const system = typeof lastTelemetryPayload === "undefined" ? null : lastTelemetryPayload?.system;
    const disks = (system?.disks || []).map(d => Number(d.usedPercent)).filter(Number.isFinite);
    const fields = [
      ["cpu", system?.cpuLoadPercent, "%", 65, 85],
      ["ram", system?.memory?.loadPercent, "%", 75, 90],
      ["gpu", system?.gpus?.[0]?.loadPercent, "%", 65, 90],
      ["disk", disks.length ? Math.max(...disks) : null, "%", 85, 95],
      ["network", system?.network?.totalReceiveBytesPerSecond, " Б/с RX", 10000000, 50000000]
    ];
    for (const [id, raw, suffix, warning, critical] of fields) {
      const number = raw == null ? null : Number(raw);
      const available = number != null && Number.isFinite(number);
      $("#labActual-" + id).textContent = available
        ? number.toFixed(id === "network" ? 0 : 1) + suffix : "Нет данных";
      $("#labIndicator-" + id).classList.toggle("warn", available && number >= warning);
      $("#labIndicator-" + id).classList.toggle("high", available && number >= critical);
    }
  }

  addEventListener("pagehide", () => {
    if (active) complete("Прерван", {}, "Приложение закрыто.");
    if (spinTimer) clearTimeout(spinTimer);
  });
  document.addEventListener("visibilitychange", () => {
    if (document.hidden && active) complete("Прерван", {}, "Окно неактивно.");
  });
  new MutationObserver(() => {
    if (!$("#lab").classList.contains("active") && active)
      complete("Прерван", {}, "Переход в другой раздел.");
  }).observe($("#lab"), { attributes: true, attributeFilter: ["class"] });

  saveHistory();
  updateUi();
  readings();
  setInterval(readings, 1000);
})();
