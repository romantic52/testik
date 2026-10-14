// AEGIS control mode: new programs/services/autostart/ports go to an operator queue.
// Blocking happens only on an explicit button press with confirmation.
(() => {
  "use strict";
  const esc = v => escapeHtml(v);
  const time = v => v ? new Date(v).toLocaleString("ru-RU") : "—";
  const kinds = { process: "Программа", service: "Служба", autostart: "Автозагрузка", port: "Порт в сеть" };
  const risks = { high: "⚠ высокий", medium: "средний", low: "низкий" };
  const statuses = { pending: "ждёт решения", allowed: "✓ разрешено", threat: "✕ угроза" };
  const confirmed = { "Content-Type": "application/json", "X-AEGIS-Block-Intent": "operator-confirmed" };
  let active = false, timer = null, state = null;

  async function load() {
    try {
      state = await apiJson("/api/v1/control?status=" + encodeURIComponent(q("#ctlFilter").value));
      render();
    } catch (error) { q("#ctlRows").innerHTML = '<p class="muted">Нет связи с агентом: ' + esc(error.message) + '</p>'; }
  }

  async function badge() {
    try {
      const s = (await apiJson("/api/v1/control?status=threat")).summary;
      q("#controlBadge").hidden = !s.enabled || !s.pending;
      q("#controlBadge").textContent = s.pending;
    } catch {}
  }

  function render() {
    const s = state.summary;
    q("#ctlStatus").textContent = s.enabled ? "КОНТРОЛЬ: ● ВКЛЮЧЁН" : "КОНТРОЛЬ: ○ ВЫКЛЮЧЕН";
    q("#ctlStatus").classList.toggle("is-warning", !s.enabled);
    q("#ctlText").textContent = s.enabled
      ? "Базовое состояние с " + time(s.baselineSince) + ". Последняя проверка: " + time(s.lastScan) + " (каждые 30 секунд)." +
        (s.elevated ? "" : " AEGIS работает без прав администратора: системные процессы и правила брандмауэра недоступны — установи службу.")
      : "Режим выключен. При включении всё, что сейчас есть на компьютере, считается нормой, а всё новое попадает в очередь ниже.";
    q("#ctlToggle").textContent = s.enabled ? "Выключить режим контроля" : "Включить режим контроля";
    q("#ctlBaseline").textContent = s.baseline; q("#ctlPending").textContent = s.pending;
    q("#ctlHigh").textContent = s.pendingHigh; q("#ctlThreats").textContent = s.threats;
    q("#controlBadge").hidden = !s.enabled || !s.pending; q("#controlBadge").textContent = s.pending;

    const rows = q("#ctlRows");
    rows.replaceChildren();
    if (!state.items.length) {
      rows.innerHTML = '<p class="muted">' + (s.enabled ? "Пусто — ничего нового." : "Включи режим контроля, чтобы следить за новым.") + '</p>';
    }
    for (const item of state.items) rows.append(itemRow(item, s));

    q("#ctlRules").innerHTML = state.rules.length ? state.rules.map(r =>
      '<div class="access-row' + (r.active ? " is-failure" : "") + '"><time>' + esc(time(r.createdAt)) + '</time><b>' + (r.active ? "✕ заблокировано" : "○ снято") + '</b>' +
      '<span>' + esc(r.ruleName) + '</span><p>' + esc(r.target) + '</p><em>' + (r.active ? '<button class="secondary" type="button" data-unblock="' + r.id + '">Снять правило</button>' : "") + '</em></div>').join("")
      : '<p class="muted">Правил нет.</p>';
    q("#ctlRules").querySelectorAll("[data-unblock]").forEach(b => b.onclick = () => unblock(Number(b.dataset.unblock)));
  }

  function programPath(item) {
    if (item.kind === "process") return item.detail || item.key;
    if (item.kind === "port" && /^[a-z]:\\/i.test(item.detail || "")) return item.detail;
    return null;
  }

  function itemRow(item, summary) {
    const row = document.createElement("div");
    row.className = "ctl-row risk-" + item.risk;
    row.innerHTML = '<span><span class="ctl-risk">' + esc(risks[item.risk] || item.risk) + '</span><small>' + esc(kinds[item.kind] || item.kind) + '</small></span>' +
      '<span><b>' + esc(item.label) + '</b><small>впервые: ' + esc(time(item.firstSeen)) + ' · ' + esc(statuses[item.status] || item.status) + '</small></span>' +
      '<span>' + esc(item.reason || "") + '<small>' + esc(item.detail || item.key) + '</small></span>';
    const buttons = document.createElement("div");
    buttons.className = "ctl-buttons";
    const add = (text, cls, handler) => { const b = document.createElement("button"); b.type = "button"; b.className = cls; b.textContent = text; b.onclick = handler; buttons.append(b); };
    if (item.status === "pending") {
      add("✓ Разрешить", "secondary", () => decide(item, "allow"));
      add("✕ Угроза", "primary", () => decide(item, "threat"));
    }
    const path = programPath(item);
    if (path && item.status !== "allowed") {
      add("Остановить программу", "secondary", () => stopProgram(path));
      add("Закрыть сеть", "secondary", () => firewall(path, item));
    }
    row.append(buttons);
    return row;
  }

  async function post(url, body, okText) {
    try {
      const r = await apiJson(url, { method: "POST", headers: confirmed, body: JSON.stringify(body || {}) });
      if (okText) toast(okText(r));
      await load();
    } catch (error) { alert(error.message); }
  }

  function decide(item, decision) {
    const text = decision === "allow"
      ? "Разрешить «" + item.label + "»? Он станет частью нормы и больше не будет показан."
      : "Отметить «" + item.label + "» как угрозу? Будет создан инцидент. Сама программа не блокируется.";
    if (confirm(text)) post("/api/v1/control/items/" + item.id + "/" + decision, null, () => decision === "allow" ? "Разрешено" : "Отмечено как угроза, создан инцидент");
  }

  function stopProgram(path) {
    if (!confirm("Остановить все процессы программы?\n\n" + path + "\n\nНесохранённые данные этой программы будут потеряны.")) return;
    post("/api/v1/control/stop-program", { path }, r => "Остановлено процессов: " + r.stopped);
  }

  function firewall(path, item) {
    if (!confirm("Создать правило брандмауэра Windows, которое запрещает программе сеть (входящие и исходящие)?\n\n" + path + "\n\nПравило можно снять здесь же.")) return;
    post("/api/v1/control/firewall", { path, note: (kinds[item.kind] || item.kind) + ": " + item.label }, () => "Правило брандмауэра создано");
  }

  async function unblock(id) {
    if (!confirm("Снять правило брандмауэра? Программа снова получит доступ к сети.")) return;
    try { await apiJson("/api/v1/control/firewall/" + id, { method: "DELETE", headers: confirmed }); toast("Правило снято"); load(); }
    catch (error) { alert(error.message); }
  }

  q("#ctlToggle").addEventListener("click", () => {
    const enable = !state?.summary.enabled;
    const text = enable
      ? "Включить режим контроля? Всё, что сейчас запущено и установлено, будет принято как норма."
      : "Выключить режим контроля? Новые программы перестанут попадать в очередь.";
    if (confirm(text)) post("/api/v1/control/mode", { enabled: enable }, s => s.enabled ? "Режим контроля включён" : "Режим контроля выключен");
  });
  q("#ctlRebase").addEventListener("click", () => {
    if (confirm("Принять всё, что сейчас есть на компьютере, как норму? Очередь «ждут решения» будет очищена.")) post("/api/v1/control/baseline", null, () => "Базовое состояние обновлено");
  });
  q("#ctlScan").addEventListener("click", async () => {
    try { const r = await apiJson("/api/v1/control/scan", { method: "POST" }); toast("Новых элементов: " + r.added); load(); }
    catch (error) { alert(error.message); }
  });
  q("#ctlFilter").addEventListener("change", load);

  badge();
  setInterval(() => { if (!active) badge(); }, 60000);

  window.AegisControl = {
    onView(id) {
      const next = id === "control";
      if (next === active) return;
      active = next;
      clearInterval(timer); timer = null;
      if (!active) return;
      load();
      timer = setInterval(load, 15000);
    }
  };
})();
