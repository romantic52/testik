// AEGIS "Защита": Microsoft Defender, disk health, open ports, removable devices.
// Everything is read from the local agent; AEGIS itself blocks and deletes nothing here.
(() => {
  "use strict";
  const esc = v => escapeHtml(v);
  const time = v => v ? new Date(v).toLocaleString("ru-RU") : "—";
  const healthNames = { healthy: "✓ исправен", warning: "⚠ предупреждение", unhealthy: "✕ неисправен", unknown: "? неизвестно" };
  const smartNames = { ok: "✓ отказ не прогнозируется", failing: "✕ ПРОГНОЗ ОТКАЗА", unknown: "нет данных" };
  const trustNames = { signed: "✓ подписан", "windows-component": "✓ компонент Windows", unsigned: "нет встроенной подписи", "unsigned-user-writable": "⚠ без подписи, папка пользователя", missing: "файл не найден", "no-path": "—", "no-access": "путь скрыт (системный процесс)" };
  const deviceKinds = { removable: "флешка / карта", fixed: "диск", optical: "оптический привод" };
  let active = false, timer = null, ports = [], scanning = false;

  async function loadDefender() {
    try {
      const [s, detections] = await Promise.all([apiJson("/api/v1/defender/status"), apiJson("/api/v1/defender/detections?limit=100")]);
      const ok = s.available && s.antivirusEnabled && s.realTimeProtection;
      q("#protDefStatus").textContent = !s.available ? "DEFENDER: НЕДОСТУПЕН" : ok ? "DEFENDER: ✓ ЗАЩИТА ВКЛЮЧЕНА" : "DEFENDER: ⚠ ЗАЩИТА ОСЛАБЛЕНА";
      q("#protDefStatus").classList.toggle("is-warning", !ok);
      q("#protAv").textContent = !s.available ? "недоступен" : s.antivirusEnabled ? "✓ вкл" : "✕ выкл";
      q("#protRt").textContent = !s.available ? "—" : s.realTimeProtection ? "✓ вкл" : "✕ выкл";
      q("#protDefText").textContent = !s.available ? (s.error || "Defender не отвечает.")
        : "Сигнатуры " + (s.signatureVersion || "—") + " от " + time(s.signatureUpdated) +
          " · защита от изменений: " + (s.tamperProtection ? "вкл" : "выкл") +
          " · последняя быстрая проверка: " + time(s.lastQuickScan) + " · полная: " + time(s.lastFullScan) + ".";
      q("#protDetCount").textContent = detections.length;
      q("#protDetections").innerHTML = detections.length ? detections.map(d =>
        '<div class="access-row' + (d.active ? " is-failure" : "") + '"><time>' + esc(time(d.detectedAt)) + '</time>' +
        '<b>' + (d.active ? "✕ активна" : "✓ " + esc(d.action)) + '</b><span>' + esc(d.name) + '</span>' +
        '<p>' + esc(d.resource || "") + '</p><em>Defender</em></div>').join("")
        : '<p class="muted">Обнаружений в журнале Defender нет (без прав администратора Windows может скрывать историю).</p>';
    } catch (error) { q("#protDefText").textContent = "Нет связи с агентом: " + error.message; }
  }

  async function scan(path) {
    if (scanning) return;
    const what = path ? "проверку «" + path + "»" : "быструю проверку компьютера (несколько минут)";
    if (!confirm("Запустить " + what + " Защитником Windows?")) return;
    scanning = true;
    q("#protQuickScan").disabled = q("#protPathScan").disabled = true;
    q("#protScanResult").textContent = "⏳ Защитник Windows проверяет… Окно можно не закрывать, проверка идёт в фоне.";
    try {
      const r = await apiJson("/api/v1/defender/scan", {
        method: "POST", headers: { "X-AEGIS-Scan-Intent": "defender-scan" }, body: JSON.stringify({ path: path || null })
      });
      q("#protScanResult").textContent = r.threatsFound ? "✕ Найдены угрозы (" + r.target + "): Defender их обработал, создан инцидент."
        : r.clean ? "✓ Проверка завершена: угроз не найдено (" + r.target + ")."
        : "⚠ Defender завершился с кодом " + r.exitCode + ": " + (r.output || "без пояснений").slice(-300);
      loadDefender();
    } catch (error) { q("#protScanResult").textContent = "✕ " + error.message; }
    finally { scanning = false; q("#protQuickScan").disabled = q("#protPathScan").disabled = false; }
  }
  q("#protQuickScan").addEventListener("click", () => scan(null));
  q("#protPathScan").addEventListener("click", () => {
    const path = q("#protScanPath").value.trim();
    if (!path) { q("#protScanResult").textContent = "Укажи папку или файл для проверки."; return; }
    scan(path);
  });

  async function loadDisks() {
    try {
      const r = await apiJson("/api/v1/disks");
      const bad = r.disks.filter(d => d.predicted === "failing" || d.health === "unhealthy").length;
      const warn = r.disks.filter(d => d.health === "warning").length;
      q("#protDisks").textContent = bad ? "✕ " + bad + " неисправно" : warn ? "⚠ " + warn + " предупр." : "✓ " + r.disks.length + " в норме";
      q("#protectionBadge").hidden = !bad;
      q("#protSmartNote").textContent = r.smartAccess ? "SMART читается" : "SMART-прогноз: нужны права администратора (служба AEGIS)";
      q("#protDiskRows").innerHTML = r.disks.length ? r.disks.map(d =>
        '<div class="protect-disk' + (d.predicted === "failing" || d.health !== "healthy" && d.health !== "unknown" ? " is-failure" : "") + '">' +
        '<b>' + esc(d.model) + '</b><span>' + esc(d.mediaType + " · " + d.bus + (d.sizeGb ? " · " + d.sizeGb + " ГБ" : "")) + '</span>' +
        '<span>Windows: ' + esc(healthNames[d.health] || d.health) + '</span><span>SMART: ' + esc(smartNames[d.predicted] || d.predicted) + '</span></div>').join("")
        : '<p class="muted">Физические диски не найдены.</p>';
      q("#protVolumeRows").innerHTML = r.volumes.map(v => {
        const used = Math.max(0, Math.min(100, v.usedPercent));
        const full = used >= 90;
        return '<div class="protect-volume' + (full ? " is-failure" : "") + '"><b>' + esc(v.drive) + '</b><span>' + esc((v.label || "без метки") + " · " + v.fileSystem + (v.removable ? " · съёмный" : "")) + '</span>' +
          '<progress class="protect-bar" max="100" value="' + used + '" aria-label="Занято ' + used + '%"></progress>' +
          '<span>' + (full ? "⚠ " : "") + used + '% · свободно ' + v.freeGb + ' из ' + v.totalGb + ' ГБ</span></div>';
      }).join("");
    } catch (error) { q("#protDiskRows").innerHTML = '<p class="muted">' + esc(error.message) + '</p>'; }
  }

  async function loadPorts() {
    try { ports = await apiJson("/api/v1/ports"); renderPorts(); }
    catch (error) { q("#protPortRows").innerHTML = '<div class="empty-process">' + esc(error.message) + '</div>'; }
  }
  function renderPorts() {
    const scope = q("#protPortScope").value, proto = q("#protPortProto").value;
    q("#protExposed").textContent = ports.filter(p => p.protocol === "TCP" && p.exposure === "network").length + " TCP";
    const rows = ports.filter(p => (!scope || p.exposure === scope) && (!proto || p.protocol === proto));
    q("#protPortCount").textContent = rows.length + " из " + ports.length;
    q("#protPortRows").innerHTML = rows.length ? rows.slice(0, 500).map(p => {
      const t = p.trust?.status || (p.processId <= 4 ? "no-path" : "no-access");
      return '<div class="port-row' + (t === "unsigned-user-writable" && p.exposure === "network" ? " is-failure" : "") + '">' +
        '<span><b>' + esc(p.protocol + " " + p.port) + '</b></span><span>' + esc(p.address) + '</span>' +
        '<span>' + (p.exposure === "network" ? "<b>◆ из сети</b>" : "○ только этот ПК") + '</span>' +
        '<span title="' + esc(p.processPath || "") + '"><b>' + esc(p.processName || "—") + '</b><small>PID ' + p.processId + '</small></span>' +
        '<span title="' + esc(p.trust?.publisher || "") + '">' + esc(trustNames[t] || t) + (p.trust?.publisher ? "<small>" + esc(p.trust.publisher) + "</small>" : "") + '</span>' +
        '<span>' + esc(p.service || "") + '</span></div>';
    }).join("") : '<div class="empty-process">Портов по фильтру нет.</div>';
  }
  q("#protPortScope").addEventListener("change", renderPorts);
  q("#protPortProto").addEventListener("change", renderPorts);

  async function loadDevices() {
    try {
      const rows = await apiJson("/api/v1/devices/events?limit=200");
      q("#protDeviceRows").innerHTML = rows.length ? rows.map(e =>
        '<div class="access-row"><time>' + esc(time(e.timestamp)) + '</time><b>' + (e.action === "connected" ? "↘ подключено" : "↗ отключено") + '</b>' +
        '<span>' + esc(e.label) + '</span><p>' + esc(e.detail || "") + '</p><em>' + esc(deviceKinds[e.kind] || e.kind) + '</em></div>').join("")
        : '<p class="muted">Подключений пока не было. AEGIS проверяет диски каждые 15 секунд, пока работает.</p>';
    } catch (error) { q("#protDeviceRows").innerHTML = '<p class="muted">' + esc(error.message) + '</p>'; }
  }
  q("#protDevRefresh").addEventListener("click", loadDevices);

  window.AegisProtection = {
    onView(id) {
      const next = id === "protection";
      if (next === active) return;
      active = next;
      clearInterval(timer); timer = null;
      if (!active) return;
      loadDefender(); loadDisks(); loadPorts(); loadDevices();
      timer = setInterval(() => { loadPorts(); loadDevices(); }, 15000);
    }
  };
})();
