// AEGIS network journal, access control, cameras and agent logs.
// All data comes from the local agent; nothing here is simulated.
(() => {
  "use strict";
  const $ = s => document.querySelector(s);
  const esc = v => escapeHtml(v);
  const scopeNames = { internet: "Интернет", lan: "Локальная сеть", provider: "Сеть провайдера", loopback: "Этот ПК", multicast: "Multicast", none: "—" };
  const dirNames = { outbound: "→ исходящее", inbound: "← входящее", listen: "ожидание" };
  const kindNames = { logon: "Вход", logoff: "Выход", lockout: "Блокировка", lock: "Экран заблокирован", unlock: "Экран разблокирован", rdp_reconnect: "RDP подключён", rdp_disconnect: "RDP отключён", door: "Проход" };
  const bytes = v => v == null ? "—" : AegisCharts.formatBytes(v).replace("/с", "");
  const time = v => v ? new Date(v).toLocaleString("ru-RU") : "—";
  let current = null;
  const timers = {};

  function stopTimers() { for (const k of Object.keys(timers)) { clearInterval(timers[k]); delete timers[k]; } }

  // ---------- Network ----------
  let live = [], byteCounters = false;
  async function loadLive() {
    try {
      const data = await apiJson("/api/v1/network/live");
      live = data.connections || [];
      byteCounters = !!data.byteCounters;
      renderLive();
    } catch (error) { $("#netLiveRows").innerHTML = '<div class="empty-process">Нет связи с агентом: ' + esc(error.message) + '</div>'; }
  }
  function renderLive() {
    const scope = $("#netScope").value, dir = $("#netDirection").value, q = $("#netSearch").value.trim().toLowerCase();
    const external = live.filter(c => c.scope !== "loopback");
    $("#netActive").textContent = external.length;
    $("#netInternet").textContent = external.filter(c => c.scope === "internet").length;
    $("#netLan").textContent = external.filter(c => c.scope === "lan").length;
    $("#netInbound").textContent = external.filter(c => c.direction === "inbound").length;
    $("#netCounters").textContent = byteCounters ? "считается" : "нужна служба";
    const rows = live.filter(c => (!scope ? c.scope !== "loopback" : scope === "all" || c.scope === scope)
      && (!dir || c.direction === dir)
      && (!q || [c.processName, c.remoteAddress, c.remoteHost, c.service, c.remotePort, c.localPort].some(v => String(v ?? "").toLowerCase().includes(q))));
    $("#netLiveCount").textContent = rows.length + " из " + live.length;
    $("#netLiveRows").innerHTML = rows.length ? rows.slice(0, 400).map(c =>
      '<div class="net-row"><span class="net-proc" title="' + esc(c.processPath || "") + '"><b>' + esc(c.processName || "PID " + c.processId) + '</b><small>PID ' + c.processId + '</small></span>' +
      '<span><b>' + esc(c.remoteHost || c.remoteAddress) + '</b><small>' + esc(c.remoteAddress + ":" + c.remotePort) + '</small></span>' +
      '<span>' + esc(c.service || "порт " + (c.direction === "inbound" ? c.localPort : c.remotePort)) + '</span>' +
      '<span class="net-scope scope-' + esc(c.scope) + '">' + esc(scopeNames[c.scope] || c.scope) + '</span>' +
      '<span>' + esc(dirNames[c.direction] || c.direction) + '</span>' +
      '<span>' + esc(c.state) + '</span>' +
      '<span class="net-bytes">' + (c.bytesIn == null ? "—" : "↓ " + bytes(c.bytesIn) + "<br>↑ " + bytes(c.bytesOut)) + '</span></div>').join("")
      : '<div class="empty-process">Соединений по фильтру нет.</div>';
  }
  async function loadProcesses() {
    try {
      const data = await apiJson("/api/v1/network/processes");
      $("#netCountersNote").textContent = data.byteCounters
        ? "Байты считаются по каждому TCP-соединению (Windows TCP Extended Statistics) с момента, когда AEGIS его увидел. Трафик внутри этого ПК не учитывается."
        : "Подсчёт байт по процессам доступен, когда AEGIS работает службой Windows или запущен от администратора. Сейчас показано только число соединений.";
      $("#netProcessRows").innerHTML = (data.processes || []).length ? data.processes.slice(0, 40).map(p =>
        '<div class="net-proc-row"><b>' + esc(p.process) + '</b><span>' + p.connections + ' соед. · ' + p.internetConnections + ' в интернет</span>' +
        '<span>' + (data.byteCounters ? "↓ " + AegisCharts.formatBytes(p.receiveBytesPerSecond) + " · ↑ " + AegisCharts.formatBytes(p.sendBytesPerSecond) : "—") + '</span>' +
        '<span>' + (data.byteCounters ? "всего ↓ " + bytes(p.totalReceived) + " · ↑ " + bytes(p.totalSent) : "") + '</span></div>').join("")
        : '<p class="muted">Внешних соединений нет.</p>';
    } catch (error) { $("#netProcessRows").innerHTML = '<p class="muted">' + esc(error.message) + '</p>'; }
  }
  function journalQuery() {
    const p = new URLSearchParams({ hours: $("#netJournalHours").value });
    const q = $("#netJournalSearch").value.trim(); if (q) p.set("q", q);
    const s = $("#netJournalScope").value; if (s) p.set("scope", s);
    return p;
  }
  async function loadJournal() {
    const p = journalQuery();
    $("#netJournalCsv").href = "/api/v1/network/journal.csv?" + p;
    p.set("limit", "500");
    $("#netJournalRows").innerHTML = '<p class="muted">Загрузка…</p>';
    try {
      const rows = await apiJson("/api/v1/network/journal?" + p);
      $("#netJournalCount").textContent = rows.length >= 500 ? "последние 500" : rows.length + " записей";
      $("#netJournalRows").innerHTML = rows.length ? rows.map(r =>
        '<div class="net-journal-row"><time>' + esc(time(r.openedAt)) + '</time><span>' + (r.closedAt ? "до " + esc(new Date(r.closedAt).toLocaleTimeString("ru-RU")) : "<b>открыто</b>") + '</span>' +
        '<b>' + esc(r.processName || "PID " + r.processId) + '</b><span>' + esc(dirNames[r.direction] || r.direction) + '</span>' +
        '<span>' + esc((r.remoteHost ? r.remoteHost + " · " : "") + r.remoteAddress + ":" + r.remotePort) + '</span>' +
        '<span>' + esc(r.service || "") + '</span><span class="net-scope scope-' + esc(r.scope) + '">' + esc(scopeNames[r.scope] || r.scope) + '</span>' +
        '<span>' + (r.bytesIn == null ? "" : "↓ " + bytes(r.bytesIn) + " ↑ " + bytes(r.bytesOut)) + '</span></div>').join("")
        : '<p class="muted">Записей нет. Журнал ведётся для всех соединений, кроме внутренних (127.0.0.1), хранится 7 дней.</p>';
    } catch (error) { $("#netJournalRows").innerHTML = '<p class="muted">' + esc(error.message) + '</p>'; }
  }
  ["#netScope", "#netDirection"].forEach(s => $(s).addEventListener("change", renderLive));
  $("#netSearch").addEventListener("input", renderLive);
  $("#netJournalRefresh").addEventListener("click", loadJournal);
  $("#netJournalHours").addEventListener("change", loadJournal);
  $("#netJournalScope").addEventListener("change", loadJournal);
  $("#netJournalSearch").addEventListener("keydown", e => { if (e.key === "Enter") loadJournal(); });

  // ---------- Access control ----------
  async function loadAccess() {
    try {
      const [s, events] = await Promise.all([
        apiJson("/api/v1/access/summary"),
        apiJson("/api/v1/access/events?limit=300" + ($("#accessSource").value ? "&source=" + $("#accessSource").value : ""))
      ]);
      const online = s.windowsLog === "online";
      $("#accessStatus").textContent = online ? "ЖУРНАЛ WINDOWS: ONLINE" : "ЖУРНАЛ WINDOWS: НЕТ ДОСТУПА";
      $("#accessStatus").classList.toggle("is-warning", !online);
      $("#accessStatusText").textContent = online ? "События входа читаются из журнала безопасности Windows этого компьютера." : (s.windowsLogError || "Журнал безопасности недоступен.");
      $("#accessOk").textContent = s.successfulLogons24h; $("#accessFail").textContent = s.failedLogons24h;
      $("#accessLock").textContent = s.lockouts24h; $("#accessCtl").textContent = s.controllerEvents24h;
      $("#accessCtlState").textContent = s.lastControllerEvent ? "последнее событие: " + time(s.lastControllerEvent) : "контроллер ещё ничего не присылал";
      $("#accessRows").innerHTML = events.length ? events.map(e =>
        '<div class="access-row' + (e.outcome === "failure" ? " is-failure" : "") + '"><time>' + esc(time(e.timestamp)) + '</time>' +
        '<b>' + (e.outcome === "failure" ? "✕ " : "✓ ") + esc(kindNames[e.kind] || e.kind) + '</b><span>' + esc(e.subject || "—") + '</span>' +
        '<p>' + esc(e.detail || "") + '</p><em>' + esc((e.source === "controller" ? "СКУД · " : "Windows · ") + (e.origin || "этот ПК")) + '</em></div>').join("")
        : '<p class="muted">' + (online ? "За последние 24 часа входов не было." : "Событий нет: журнал безопасности недоступен.") + '</p>';
    } catch (error) { $("#accessRows").innerHTML = '<p class="muted">' + esc(error.message) + '</p>'; }
  }
  async function loadIntake() {
    try {
      const i = await apiJson("/api/v1/access/intake");
      $("#accessIntake").textContent =
        'curl -X POST http://127.0.0.1:8765' + i.endpoint + ' -H "Content-Type: application/json" -H "' + i.header + ': ' + i.token + '" ' +
        '-d "{\\"eventId\\":\\"door1-0001\\",\\"door\\":\\"Вход 1\\",\\"person\\":\\"Иванов И.\\",\\"granted\\":false,\\"reason\\":\\"карта не найдена\\"}"';
    } catch (error) { $("#accessIntake").textContent = error.message; }
  }
  $("#accessSource").addEventListener("change", loadAccess);
  $("#accessRefresh").addEventListener("click", loadAccess);

  // ---------- Cameras ----------
  let cameras = [];
  const motion = {};
  let localStream = null;
  async function loadCameras() {
    try { cameras = await apiJson("/api/v1/cameras"); renderCameras(); }
    catch (error) { $("#cameraGrid").innerHTML = '<p class="muted">' + esc(error.message) + '</p>'; }
  }
  function renderCameras() {
    const grid = $("#cameraGrid");
    grid.replaceChildren();
    if (!cameras.length && !localStream) {
      grid.innerHTML = '<p class="muted">Камер нет. Добавь IP-камеру по адресу снимка или MJPEG либо включи камеру этого компьютера.</p>';
      return;
    }
    if (localStream) grid.append(cameraTile({ id: "local", name: "Камера этого компьютера", url: "USB / встроенная", motion: true, sensitivity: 30 }, true));
    for (const c of cameras) grid.append(cameraTile(c, false));
  }
  function cameraTile(c, isLocal) {
    const tile = document.createElement("article");
    tile.className = "camera-tile";
    tile.dataset.id = c.id;
    const media = isLocal ? document.createElement("video") : document.createElement("img");
    media.className = "camera-media";
    if (isLocal) { media.autoplay = true; media.muted = true; media.playsInline = true; media.srcObject = localStream; }
    else media.alt = c.name;
    const head = document.createElement("div");
    head.className = "camera-head";
    head.innerHTML = '<b>' + esc(c.name) + '</b><span class="camera-state" data-state="unknown">…</span>';
    const meta = document.createElement("p");
    meta.className = "camera-meta";
    meta.textContent = c.url + (c.motion ? " · детектор движения: вкл" : "");
    const actions = document.createElement("div");
    actions.className = "camera-actions";
    if (!isLocal) {
      const remove = document.createElement("button");
      remove.type = "button"; remove.className = "secondary"; remove.textContent = "Удалить";
      remove.onclick = async () => {
        if (!confirm("Удалить камеру «" + c.name + "»?")) return;
        cameras = await apiJson("/api/v1/cameras", { method: "PUT", body: JSON.stringify(cameras.filter(x => x.id !== c.id)) });
        renderCameras();
      };
      actions.append(remove);
    } else {
      const off = document.createElement("button");
      off.type = "button"; off.className = "secondary"; off.textContent = "Выключить";
      off.onclick = () => { localStream?.getTracks().forEach(t => t.stop()); localStream = null; renderCameras(); };
      actions.append(off);
    }
    tile.append(head, media, meta, actions);
    return tile;
  }
  function setCameraState(tile, state, text) {
    const el = tile.querySelector(".camera-state");
    el.dataset.state = state; el.textContent = text;
  }
  // Motion: grayscale 64x48 difference between consecutive frames.
  function detectMotion(id, source, sensitivity, tile) {
    const canvas = motion[id]?.canvas || document.createElement("canvas");
    canvas.width = 64; canvas.height = 48;
    const ctx = canvas.getContext("2d", { willReadFrequently: true });
    try { ctx.drawImage(source, 0, 0, 64, 48); } catch { return; }
    const pixels = ctx.getImageData(0, 0, 64, 48).data;
    const gray = new Uint8Array(64 * 48);
    for (let i = 0; i < gray.length; i++) gray[i] = (pixels[i * 4] * 3 + pixels[i * 4 + 1] * 6 + pixels[i * 4 + 2]) / 10;
    const previous = motion[id]?.gray;
    motion[id] = { canvas, gray, hits: motion[id]?.hits || 0 };
    if (!previous) return;
    const threshold = 40 - sensitivity * 0.3;
    let changed = 0;
    for (let i = 0; i < gray.length; i++) if (Math.abs(gray[i] - previous[i]) > threshold) changed++;
    const percent = changed / gray.length * 100;
    const moving = percent > Math.max(1.5, 12 - sensitivity / 10);
    motion[id].hits = moving ? motion[id].hits + 1 : 0;
    tile.classList.toggle("is-motion", moving);
    if (motion[id].hits === 2) {
      apiJson("/api/v1/cameras/" + encodeURIComponent(id) + "/motion", { method: "POST", body: JSON.stringify({ changedPercent: percent }) })
        .then(r => { if (r?.incident) toast("Движение: " + tile.querySelector(".camera-head b").textContent + " — создан инцидент"); })
        .catch(() => {});
    }
  }
  function pollCameras() {
    for (const tile of document.querySelectorAll(".camera-tile")) {
      const id = tile.dataset.id;
      const config = id === "local" ? { motion: true, sensitivity: 30 } : cameras.find(c => c.id === id);
      if (!config) continue;
      if (id === "local") {
        const video = tile.querySelector("video");
        setCameraState(tile, "online", "● LIVE");
        if (video.readyState >= 2) detectMotion(id, video, config.sensitivity, tile);
        continue;
      }
      if (tile.dataset.loading === "1") continue;
      tile.dataset.loading = "1";
      const img = tile.querySelector("img");
      const next = new Image();
      next.onload = () => {
        tile.dataset.loading = "0";
        img.src = next.src;
        setCameraState(tile, "online", "● " + new Date().toLocaleTimeString("ru-RU"));
        if (config.motion) detectMotion(id, next, config.sensitivity, tile);
      };
      next.onerror = async () => {
        tile.dataset.loading = "0";
        let reason = "нет кадра";
        try { const r = await fetch("/api/v1/cameras/" + encodeURIComponent(id) + "/frame", { cache: "no-store" }); reason = (await r.json()).detail || reason; } catch {}
        setCameraState(tile, "offline", "✕ " + reason);
      };
      next.src = "/api/v1/cameras/" + encodeURIComponent(id) + "/frame?t=" + Date.now();
    }
  }
  $("#cameraForm").addEventListener("submit", async event => {
    event.preventDefault();
    const entry = { id: null, name: $("#cameraName").value, url: $("#cameraUrl").value, motion: $("#cameraMotion").checked, sensitivity: Number($("#cameraSensitivity").value) };
    try {
      cameras = await apiJson("/api/v1/cameras", { method: "PUT", body: JSON.stringify([...cameras, entry]) });
      $("#cameraForm").reset();
      $("#cameraFeedback").textContent = "Камера добавлена.";
      renderCameras();
    } catch (error) { $("#cameraFeedback").textContent = error.message; }
  });
  $("#cameraLocal").addEventListener("click", async () => {
    if (!navigator.mediaDevices?.getUserMedia) { $("#cameraFeedback").textContent = "Браузер не поддерживает доступ к камере."; return; }
    try {
      localStream = await navigator.mediaDevices.getUserMedia({ video: { width: 640, height: 480 }, audio: false });
      renderCameras();
      $("#cameraFeedback").textContent = "Камера этого компьютера включена. Изображение не записывается и никуда не отправляется.";
    } catch (error) {
      $("#cameraFeedback").textContent = error.name === "NotFoundError" ? "Камера на этом компьютере не найдена." : error.name === "NotAllowedError" ? "Доступ к камере запрещён в браузере." : "Камера недоступна: " + error.message;
    }
  });

  // ---------- Agent logs ----------
  async function loadLogs() {
    try {
      const data = await apiJson("/api/v1/logs/agent?lines=400");
      $("#agentLogFile").textContent = data.file;
      $("#agentLogBox").textContent = data.lines.length ? data.lines.join("\n") : "Лог пуст.";
      $("#agentLogBox").scrollTop = $("#agentLogBox").scrollHeight;
    } catch (error) { $("#agentLogBox").textContent = error.message; }
  }
  $("#refreshAgentLog").addEventListener("click", loadLogs);

  window.AegisSecurity = {
    onView(id) {
      if (id === current) return;
      current = id;
      stopTimers();
      if (id !== "cameras" && localStream) { localStream.getTracks().forEach(t => t.stop()); localStream = null; }
      if (id === "network") { loadLive(); loadProcesses(); loadJournal(); timers.live = setInterval(() => { loadLive(); loadProcesses(); }, 3000); }
      if (id === "access") { loadAccess(); loadIntake(); timers.access = setInterval(loadAccess, 15000); }
      if (id === "cameras") { loadCameras().then(pollCameras); timers.cam = setInterval(pollCameras, 1000); }
      if (id === "audit") loadLogs();
    }
  };
})();
