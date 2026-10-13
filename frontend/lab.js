// AEGIS Lab UI: direct test picker, optional roulette, live chart, explicit consent and Stop.
// Workloads live in AegisLabTests; neither the picker nor the wheel starts them.
(() => {
  "use strict";
  const $ = selector => document.querySelector(selector);
  const cores = Math.max(1, navigator.hardwareConcurrency || 4);
  const kinds = [
    { id: "cpu", name: "Процессор (CPU)", description: "Настоящие вычисления в выбранном числе потоков. Общая загрузка CPU видна на графике ниже." },
    { id: "ram", name: "Оперативная память (RAM)", description: "Выделение, удержание и проверка реальной памяти с освобождением после теста." },
    { id: "gpu", name: "Видеокарта (GPU)", description: "Рендер фрактального шейдера WebGL2 с подсчётом FPS. Показывается, какой рендерер реально используется." },
    { id: "disk", name: "Накопитель (DISK)", description: "Запись, сброс на диск и чтение с проверкой содержимого временного файла в TEMP." },
    { id: "network", name: "Сеть (NET, loopback)", description: "Непрерывный обмен с локальным агентом через 127.0.0.1. Это проверка сетевого стека, а не скорость интернета." }
  ];
  const iconFor = { cpu: "◉", ram: "▦", gpu: "◈", disk: "▤", network: "⌁" };
  const notes = {
    cpu: "Каждый поток — отдельный Web Worker. Максимум " + cores + " потоков и 60 секунд.",
    ram: "Память выделяется в браузере блоками по 64 МиБ и освобождается при завершении или «Стоп».",
    gpu: "Если в VM нет 3D-ускорения, браузер рисует программно (SwiftShader) — это нагрузка на CPU, а не на видеокарту. AEGIS это явно пишет.",
    disk: "Файл создаётся только в %TEMP%\\AEGIS-lab, удаляется после проверки. Нужно свободное место: размер + 2 ГиБ.",
    network: "Трафик не выходит за пределы компьютера; счётчики сетевых адаптеров его не видят."
  };
  let selected = null;
  let wheelBusy = false;
  let spinTimer = null;
  let wheelAngle = 0;
  let active = null;
  let countdown = null;
  let watchdog = null;
  let band = null;
  let history = [];
  try {
    const value = JSON.parse(localStorage.getItem("aegis_lab_results") || "[]");
    if (Array.isArray(value)) history = value.slice(0, 30);
  } catch {}

  $("#labCpuThreads").max = String(cores);
  $("#labCpuThreads").value = String(Math.max(1, Math.ceil(cores / 2)));
  $("#labCpuThreadsAmount").textContent = $("#labCpuThreads").value + " из " + cores;

  function feedback(message) { $("#labOutcome").textContent = message; }

  function updateUi() {
    const running = active !== null;
    $("#labStatus").textContent = running ? "▶ ТЕСТ ИДЁТ" : wheelBusy ? "РУЛЕТКА" : "ГОТОВО";
    $("#labStatus").classList.toggle("is-running", running);
    $("#labWheelButton").disabled = wheelBusy || running;
    document.querySelectorAll(".lab-pick").forEach(button => {
      button.disabled = wheelBusy || running;
      button.classList.toggle("is-selected", selected?.id === button.dataset.kind);
      button.setAttribute("aria-pressed", String(selected?.id === button.dataset.kind));
    });
    $("#labStart").disabled = wheelBusy || running || !selected || !$("#labConsent").checked;
    $("#labStop").disabled = !running;
    $("#labTuning").hidden = !selected;
    $("#labDurationRow").hidden = !selected || selected.id === "disk";
    $("#labCpuRow").hidden = selected?.id !== "cpu";
    $("#labRamRow").hidden = selected?.id !== "ram";
    $("#labGpuRow").hidden = selected?.id !== "gpu";
    $("#labDiskRow").hidden = selected?.id !== "disk";
    $("#labSafetyNotice").textContent = selected ? notes[selected.id] : "";
    for (const input of document.querySelectorAll("#labTuning input, #labTuning select")) input.disabled = running;
  }

  function choose(entry) {
    if (active) return;
    selected = entry;
    $("#labSelectedTitle").textContent = entry.name;
    $("#labSelectedDescription").textContent = entry.description;
    $("#labSelectedIcon").textContent = iconFor[entry.id];
    $("#labActions").hidden = false;
    $("#labGpuCanvas").hidden = true;
    $("#labResultSummary").hidden = true;
    $("#labTimer").textContent = "00:00";
    feedback($("#labConsent").checked ? "Нажми «Запустить тест»." : "Отметь подтверждение и нажми «Запустить тест».");
    updateUi();
  }

  document.querySelectorAll(".lab-pick").forEach(button =>
    button.addEventListener("click", () => choose(kinds.find(k => k.id === button.dataset.kind))));

  $("#labWheelButton").addEventListener("click", () => {
    if (wheelBusy || active) return;
    wheelBusy = true;
    updateUi();
    feedback("Выбираем тест…");
    const random = new Uint32Array(1);
    crypto.getRandomValues(random);
    const index = random[0] % kinds.length;
    const target = (360 - index * 72) % 360;
    const current = ((wheelAngle % 360) + 360) % 360;
    wheelAngle += 360 * 5 + (target - current + 360) % 360;
    $("#labWheel").style.transform = "rotate(" + wheelAngle + "deg)";
    spinTimer = setTimeout(() => {
      spinTimer = null;
      wheelBusy = false;
      choose(kinds[index]);
    }, 3550);
  });

  $("#labConsent").addEventListener("change", () => {
    updateUi();
    if (selected && !active) feedback($("#labConsent").checked ? "Нажми «Запустить тест»." : "Отметь подтверждение и нажми «Запустить тест».");
  });
  $("#labMemory").addEventListener("input", e => { $("#labMemoryAmount").textContent = e.target.value + " МиБ"; });
  $("#labCpuIntensity").addEventListener("input", e => { $("#labCpuAmount").textContent = e.target.value + "%"; });
  $("#labCpuThreads").addEventListener("input", e => { $("#labCpuThreadsAmount").textContent = e.target.value + " из " + cores; });
  $("#labGpuIterations").addEventListener("input", e => { $("#labGpuAmount").textContent = e.target.value; });

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
      status.textContent = (item.status === "Успешно" ? "✓ " : "✕ ") + item.status;
      status.className = item.status === "Успешно" ? "lab-good" : "lab-warning";
      const desc = document.createElement("small");
      desc.textContent = item.summary || "";
      row.append(time, kind, status, desc);
      list.append(row);
    }
  }

  function summaryFor(kind, m) {
    switch (kind) {
      case "cpu": return m.threads + " потоков × " + m.dutyPercent + "%: " + Math.round(m.iterationsPerSecond).toLocaleString("ru-RU") + " вычислений/с";
      case "ram": return m.allocatedMiB + " МиБ выделено и удержано; содержимое " + (m.verified ? "проверено" : "ПОВРЕЖДЕНО");
      case "gpu": return m.fps.toFixed(1) + " FPS, " + m.frames + " кадров " + m.resolution + "; рендерер: " + m.renderer + (m.softwareRenderer ? " (программный — нагрузка шла на CPU)" : "");
      case "disk": return m.sizeMiB + " МиБ: запись " + m.writeMiBps.toFixed(0) + " МиБ/с, чтение " + m.readMiBps.toFixed(0) + " МиБ/с; содержимое " + (m.verified ? "проверено" : "не проверено");
      case "network": return m.totalMiB.toFixed(0) + " МиБ за " + m.seconds.toFixed(1) + " с = " + m.mibps.toFixed(0) + " МиБ/с по loopback";
      default: return "Результат получен.";
    }
  }

  function release() {
    if (!active) return null;
    const task = active;
    active = null;
    task.controller.abort();
    for (const worker of task.workers) worker.terminate();
    task.workers.length = 0;
    if (task.cleanup) task.cleanup();
    if (countdown) clearInterval(countdown);
    if (watchdog) clearTimeout(watchdog);
    countdown = null;
    watchdog = null;
    if (band) band.to = Date.now();
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
    history = history.slice(0, 30);
    saveHistory();
    const summary = $("#labResultSummary");
    summary.replaceChildren();
    const title = document.createElement("strong");
    title.textContent = (status === "Успешно" ? "✓ " : "✕ ") + result.kind.toUpperCase() + " · " + result.status + " · " + result.durationSeconds + " с";
    const desc = document.createElement("p");
    desc.textContent = result.summary;
    summary.append(title, desc);
    summary.hidden = false;
    feedback(status === "Успешно" ? "Тест завершён, ресурсы освобождены." : status + ": " + note);
    if (typeof appendAudit === "function")
      appendAudit("lab.test." + (status === "Успешно" ? "completed" : "stopped"), task.kind, result.summary, "AEGIS Lab");
  }

  $("#labStop").addEventListener("click", () => {
    if (active) complete("Остановлен", {}, "Тест остановлен вручную, ресурсы освобождены.");
  });

  $("#labStart").addEventListener("click", async () => {
    if (!selected || wheelBusy || active || !$("#labConsent").checked) return;
    const durationMs = Number($("#labDuration").value) * 1000;
    const task = {
      kind: selected.id, began: performance.now(), durationMs,
      controller: new AbortController(), workers: [], cleanup: null
    };
    active = task;
    band = { from: Date.now(), to: null, label: selected.id.toUpperCase() + " тест" };
    updateUi();
    $("#labResultSummary").hidden = true;
    const timeLimit = task.kind === "disk" ? 120000 : durationMs;
    feedback("Идёт тест «" + selected.name + "»…");
    const deadline = performance.now() + timeLimit;
    countdown = setInterval(() => {
      const elapsed = Math.floor((performance.now() - task.began) / 1000);
      const left = Math.max(0, Math.ceil((deadline - performance.now()) / 1000));
      $("#labTimer").textContent = task.kind === "disk"
        ? String(Math.floor(elapsed / 60)).padStart(2, "0") + ":" + String(elapsed % 60).padStart(2, "0")
        : String(Math.floor(left / 60)).padStart(2, "0") + ":" + String(left % 60).padStart(2, "0");
    }, 250);
    watchdog = setTimeout(() => {
      if (active === task) complete("Прерван", {}, "Тест не завершился за отведённое время.");
    }, timeLimit + 10000);
    try {
      const settings = {
        durationMs,
        cpuThreads: Number($("#labCpuThreads").value),
        cpuDutyPercent: Number($("#labCpuIntensity").value),
        memoryMiB: Number($("#labMemory").value),
        gpuIterations: Number($("#labGpuIterations").value),
        sizeMiB: Number($("#labDiskSize").value)
      };
      const metrics = await window.AegisLabTests[task.kind](task, settings, text => { if (active === task) feedback(text); });
      if (active === task) complete("Успешно", metrics);
    } catch (error) {
      if (active === task) complete("Ошибка", {}, error?.message || String(error));
    }
  });

  $("#labExport").addEventListener("click", () => {
    const machine = typeof lastTelemetryPayload === "undefined" ? null : lastTelemetryPayload?.system?.machineName;
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
    try { localStorage.removeItem("aegis_lab_results"); } catch {}
    saveHistory();
    $("#labResultSummary").hidden = true;
    feedback("История тестов очищена.");
  });

  $("#labBsodButton").addEventListener("click", () => {
    if (active) complete("Прерван", {}, "Запущена демонстрация синего экрана.");
    if (typeof startFakeCrash === "function") startFakeCrash();
  });

  let labChart = null;
  function readings() {
    const system = typeof lastTelemetryPayload === "undefined" ? null : lastTelemetryPayload?.system;
    const fresh = system && Date.now() - new Date(system.timestamp).getTime() < 10000;
    const disks = (system?.disks || []).map(d => Number(d.usedPercent)).filter(Number.isFinite);
    const fields = [
      ["cpu", system?.cpuLoadPercent, "%", 65, 85],
      ["ram", system?.memory?.loadPercent, "%", 75, 90],
      ["gpu", system?.gpus?.[0]?.loadPercent, "%", 65, 90],
      ["disk", disks.length ? Math.max(...disks) : null, "% занято", 85, 95],
      ["network", system?.network?.totalReceiveBytesPerSecond, "", 10000000, 50000000]
    ];
    for (const [id, raw, suffix, warning, critical] of fields) {
      const number = raw == null || !fresh ? null : Number(raw);
      const available = number != null && Number.isFinite(number);
      $("#labActual-" + id).textContent = !system ? "нет связи с агентом"
        : !fresh ? "данные устарели"
        : !available ? (id === "gpu" ? "датчик недоступен" : "нет данных")
        : id === "network" ? AegisCharts.formatBytes(number) : number.toFixed(1) + suffix;
      $("#labIndicator-" + id).classList.toggle("warn", available && number >= warning);
      $("#labIndicator-" + id).classList.toggle("high", available && number >= critical);
    }
    if (!$("#lab").classList.contains("active") || typeof liveSamples === "undefined") return;
    labChart ??= AegisCharts.create($("#labLiveChart"), {
      series: percentSeries, height: 220, ariaLabel: "Нагрузка CPU, RAM, GPU во время теста",
      emptyText: "Нет данных от AEGIS Agent — график появится после подключения."
    });
    const now = Date.now();
    labChart.render(liveSamples, { from: now - 90000, to: now, band: band && (band.to ?? now) > now - 90000 ? band : null });
  }

  addEventListener("pagehide", () => {
    if (active) complete("Прерван", {}, "Окно AEGIS закрыто.");
    if (spinTimer) clearTimeout(spinTimer);
  });
  new MutationObserver(() => {
    if (!$("#lab").classList.contains("active") && active)
      complete("Прерван", {}, "Переход в другой раздел — тест остановлен.");
  }).observe($("#lab"), { attributes: true, attributeFilter: ["class"] });

  saveHistory();
  updateUi();
  readings();
  setInterval(readings, 1000);
})();
