// Native AEGIS mini-messenger UI. Uses the AETHER relay and Rust Double Ratchet adapter.
// All decrypted content is rendered as text. No plaintext message history is stored on disk.
const aetherConfig = q("#aetherConfig");
const aetherTotpField = q("#aetherTotpField");
const aetherPanel = q("#aetherPanel");
const aetherMini = {
  conversations: new Map(),
  unread: new Map(),
  peer: "",
  session: "",
  connected: false,
  sending: false,
  restoring: false,
  pending: [],
  saveQueue: Promise.resolve()
};

let rememberedAether = {};
try { rememberedAether = JSON.parse(localStorage.getItem("aegis_aether_ui") || "{}"); }
catch { rememberedAether = {}; }
q("#aetherServer").value = rememberedAether.server || "";
q("#aetherUser").value = rememberedAether.user || "";
q("#aetherPeer").value = rememberedAether.peer || "";

function normalizeAetherPeer(value) {
  const peer = String(value || "").trim().replace(/^@/, "").toLowerCase();
  if (!/^[a-z0-9_]{2,64}$/.test(peer)) {
    throw new Error("Укажи AETHER username: от 2 до 64 символов");
  }
  if (window.aether.connected && peer === window.aether.userId) {
    throw new Error("Нельзя создать переписку с самим собой");
  }
  return peer;
}

function aetherTime(date) {
  const timestamp = new Date(date || Date.now());
  return Number.isNaN(timestamp.getTime())
    ? "" : timestamp.toLocaleTimeString("ru-RU", { hour: "2-digit", minute: "2-digit" });
}

function setAetherUiStatus(value) {
  q("#aetherComposeState").textContent = value;
}
function rememberMiniPeer(peer) {
  const preferences = {
    server: q("#aetherServer").value.trim(),
    user: q("#aetherUser").value.trim(),
    peer
  };
  localStorage.setItem("aegis_aether_ui", JSON.stringify(preferences));
}

function renderAetherContacts() {
  const box = q("#aetherContacts");
  box.replaceChildren();

  const peers = [...aetherMini.conversations.keys()].sort((left, right) => {
    const a = aetherMini.conversations.get(left)?.at(-1)?.createdAt || "";
    const b = aetherMini.conversations.get(right)?.at(-1)?.createdAt || "";
    return b.localeCompare(a);
  });

  if (!peers.length) {
    const hint = document.createElement("div");
    hint.className = "mini-contacts-empty";
    hint.textContent = "Диалогов пока нет. Нажми +";
    box.append(hint);
  }

  for (const peer of peers) {
    const messages = aetherMini.conversations.get(peer) || [];
    const last = messages.at(-1);
    const unread = aetherMini.unread.get(peer) || 0;
    const button = document.createElement("button");
    button.type = "button";
    button.className = "mini-contact" + (aetherMini.peer === peer ? " selected" : "");
    button.setAttribute("aria-pressed", String(aetherMini.peer === peer));
    button.title = "Открыть переписку с @" + peer;
    const avatar = document.createElement("span");
    avatar.className = "mini-contact-avatar";
    avatar.textContent = peer.slice(0, 1).toUpperCase();
    const details = document.createElement("span");
    details.className = "mini-contact-details";
    const username = document.createElement("strong");
    username.textContent = "@" + peer;
    const preview = document.createElement("small");
    preview.textContent = last?.text ? last.text.replace(/\s+/g, " ").slice(0, 32) : "Новый диалог";
    details.append(username, preview);
    button.append(avatar, details);
    if (unread) {
      const badge = document.createElement("span");
      badge.className = "mini-unread-count";
      badge.textContent = unread > 9 ? "9+" : String(unread);
      button.append(badge);
    }
    button.addEventListener("click", () => selectAetherConversation(peer));
    box.append(button);
  }
}

function renderAetherMessages() {
  const box = q("#messages");
  box.replaceChildren();
  const peer = aetherMini.peer;
  const messages = aetherMini.conversations.get(peer) || [];
  if (!peer || messages.length === 0) {
    const placeholder = document.createElement("div");
    placeholder.className = "mini-empty";
    const title = document.createElement("strong");
    title.textContent = peer ? "Защищённый диалог с @" + peer : "Выбери диалог";
    const caption = document.createElement("span");
    caption.textContent = peer
      ? "Сообщения будут доставляться через AETHER relay с E2E-шифрованием."
      : "Выбери собеседника или добавь новый диалог кнопкой +.";
    placeholder.append(title, caption);
    box.append(placeholder);
  } else {
    let previousDay = "";
    for (const message of messages) {
      const date = new Date(message.createdAt);
      const day = !Number.isNaN(date.getTime()) ? date.toLocaleDateString("ru-RU") : "";
      if (day && day !== previousDay) {
        const separator = document.createElement("div");
        separator.className = "mini-date-separator";
        separator.textContent = day;
        box.append(separator);
        previousDay = day;
      }
      const el = document.createElement("article");
      el.className = "mini-bubble " + (message.direction === "incoming" ? "incoming" : "outgoing");
      const meta = document.createElement("div");
      meta.className = "mini-bubble-meta";
      const sender = document.createElement("strong");
      sender.textContent = message.direction === "incoming" ? "@" + peer : "Вы";
      const clock = document.createElement("time");
      clock.textContent = aetherTime(message.createdAt);
      const body = document.createElement("p");
      body.textContent = message.text;
      meta.append(sender, clock);
      el.append(meta, body);
      box.append(el);
    }
  }
  box.scrollTop = box.scrollHeight;
}

function updateAetherComposer() {
  const active = aetherMini.connected && !!aetherMini.peer && !aetherMini.sending;
  q("#chatInput").disabled = !active;
  q("#chatSendButton").disabled = !active;
  q("#aetherQuickActions").classList.toggle("disabled", !active);
  q("#aetherPeerName").textContent = aetherMini.peer ? "@" + aetherMini.peer : "Выбери диалог";
  q("#aetherPeerAvatar").textContent = aetherMini.peer ? aetherMini.peer[0].toUpperCase() : "#";
  q("#aetherPeerLabel").textContent = aetherMini.peer
    ? "Личная переписка · Double Ratchet" : "Приватные сообщения · E2E";
  if (!aetherMini.connected) setAetherUiStatus("Войди в AETHER, чтобы написать");
  else if (!aetherMini.peer) setAetherUiStatus("Выбери собеседника");
  else if (aetherMini.sending) setAetherUiStatus("Шифрование и отправка…");
  else setAetherUiStatus("Защищённый канал · @" + aetherMini.peer);
}

function selectAetherConversation(value, focus = false) {
  const peer = normalizeAetherPeer(value);
  if (!aetherMini.conversations.has(peer)) aetherMini.conversations.set(peer, []);
  aetherMini.peer = peer;
  window.aether.peerId = peer;
  aetherMini.unread.set(peer, 0);
  rememberMiniPeer(peer);
  renderAetherContacts();
  renderAetherMessages();
  updateAetherComposer();
  if (focus) q("#chatInput").focus();
  return peer;
}

function persistAetherMiniHistory() {
  if (!aetherMini.connected || aetherMini.restoring) return;
  const session = aetherMini.session;
  const snapshot = Object.fromEntries(
    [...aetherMini.conversations.entries()].map(([peer, messages]) => [peer, messages.slice(-150)])
  );
  aetherMini.saveQueue = aetherMini.saveQueue
    .catch(() => {})
    .then(async () => {
      if (session === aetherMini.session && window.aether.connected) {
        await window.aether.saveChatHistory(snapshot);
      }
    })
    .catch(error => console.warn("Encrypted chat history could not be saved:", error));
}

function recordAetherMessage(data) {
  const peer = normalizeAetherPeer(data.peerId);
  const entry = {
    id: String(data.id || crypto.randomUUID()),
    direction: data.direction === "incoming" ? "incoming" : "outgoing",
    text: String(data.text || "").slice(0, 8000),
    createdAt: data.createdAt || new Date().toISOString()
  };
  if (!aetherMini.conversations.has(peer)) aetherMini.conversations.set(peer, []);
  const messages = aetherMini.conversations.get(peer);
  if (messages.some(m => m.id === entry.id && m.direction === entry.direction)) return;
  messages.push(entry);
  if (messages.length > 150) messages.splice(0, messages.length - 150);
  if (entry.direction === "incoming" && peer !== aetherMini.peer) {
    aetherMini.unread.set(peer, (aetherMini.unread.get(peer) || 0) + 1);
  }
  renderAetherContacts();
  if (peer === aetherMini.peer) renderAetherMessages();
  persistAetherMiniHistory();
}

async function restoreAetherMiniHistory(session) {
  aetherMini.restoring = true;
  try {
    const saved = await window.aether.loadChatHistory();
    if (session !== aetherMini.session) return;
    aetherMini.conversations = new Map(
      Object.entries(saved.conversations || {}).filter(([key, messages]) =>
        /^[a-z0-9_]{2,64}$/.test(key) && Array.isArray(messages)
      ).map(([key, messages]) => [key, messages.slice(-150)])
    );
  } catch (error) {
    console.warn("Encrypted chat history is unavailable:", error);
  } finally {
    if (session === aetherMini.session) {
      aetherMini.restoring = false;
      const pending = aetherMini.pending.splice(0);
      for (const event of pending) recordAetherMessage(event);
      const suggested = rememberedAether.peer || q("#aetherPeer").value.trim();
      const firstPeer = aetherMini.peer || suggested || aetherMini.conversations.keys().next().value;
      if (firstPeer) {
        try { selectAetherConversation(firstPeer); }
        catch { renderAetherContacts(); }
      } else {
        renderAetherContacts();
        renderAetherMessages();
        updateAetherComposer();
      }
    }
  }
}

function clearAetherMiniSession() {
  aetherMini.session = "";
  aetherMini.connected = false;
  aetherMini.restoring = false;
  aetherMini.pending = [];
  aetherMini.peer = "";
  aetherMini.conversations.clear();
  aetherMini.unread.clear();
  aetherPanel.classList.remove("is-connected");
  renderAetherContacts();
  renderAetherMessages();
  updateAetherComposer();
}

q("#aetherSetupToggle").addEventListener("click", () => {
  aetherConfig.classList.toggle("open");
  if (aetherConfig.classList.contains("open")) q("#aetherServer").focus();
});

q("#aetherNewChatToggle").addEventListener("click", () => {
  const form = q("#aetherPeerForm");
  form.hidden = !form.hidden;
  if (!form.hidden) q("#aetherNewPeer").focus();
});
q("#aetherPeerForm").addEventListener("submit", event => {
  event.preventDefault();
  try {
    selectAetherConversation(q("#aetherNewPeer").value, true);
    q("#aetherNewPeer").value = "";
    q("#aetherPeerForm").hidden = true;
    persistAetherMiniHistory();
  } catch (error) { toast(error.message); }
});

q("#aetherDisconnect").addEventListener("click", async () => {
  await window.aether.disconnect();
  q("#aetherPassword").value = "";
  q("#aetherTotp").value = "";
  clearAetherMiniSession();
  aetherConfig.classList.add("open");
  toast("AETHER отключён");
});

aetherConfig.addEventListener("submit", async event => {
  event.preventDefault();
  const server = q("#aetherServer").value.trim();
  const user = q("#aetherUser").value.trim();
  const peer = q("#aetherPeer").value.trim();
  const password = q("#aetherPassword").value;
  const totpCode = q("#aetherTotp").value.trim();
  const submit = q("#aetherConnect");
  submit.disabled = true;
  q("#aetherLoginStatus").textContent = "Входим и восстанавливаем защищённую сессию…";
  try {
    const result = await window.aether.connect({ serverUrl:server, userId:user, password, totpCode, peerId:peer });
    rememberedAether = { server, user, peer };
    localStorage.setItem("aegis_aether_ui", JSON.stringify(rememberedAether));
    q("#aetherLoginStatus").textContent = "Устройство " + result.deviceId + " подключено";
    q("#aetherPassword").value = "";
    q("#aetherTotp").value = "";
    aetherTotpField.classList.remove("show");
    aetherConfig.classList.remove("open");
  } catch (error) {
    if (error.code === "TOTP_REQUIRED" || error.code === "TOTP_INVALID") {
      aetherTotpField.classList.add("show");
      q("#aetherTotp").focus();
    }
    q("#aetherLoginStatus").textContent = error.message || "Ошибка AETHER";
    toast(error.message || "Ошибка подключения");
  } finally { submit.disabled = false; }
});

window.addEventListener("aether-state", event => {
  const data = event.detail || {};
  const online = !!data.connected;
  aetherOnline = online;
  updateOverview();
  q("#aetherRelayState").classList.toggle("relay-offline", !online);
  q("#aetherRelayState").classList.toggle("relay-online", online);
  q("#aetherRelayState").innerHTML = "<i></i> " + (online ? "Сессия активна" : "Нет сессии");
  q("#aetherSecureTitle").textContent = online ? "Защищённая связь" : "Подключи AETHER";
  q("#aetherSecureText").textContent = online
    ? "E2E Double Ratchet · SOC " + (data.deviceId || "")
    : "Чат внутри AEGIS. Сообщения шифруются на устройстве.";
  q("#aetherUserLabel").textContent = online ? "@" + data.userId : "Не в сети";
  q("#aetherE2eBadge").textContent = online ? "E2E ✓" : "E2E";
  q("#aetherConnectionDot").classList.toggle("online", online);
  q("#aetherConnectionDot").title = online ? "Сессия AETHER активна" : "Нет сессии";
  if (online) {
    aetherPanel.classList.add("is-connected");
    const nextSession = data.serverUrl + "::" + data.userId;
    if (nextSession !== aetherMini.session) {
      aetherMini.session = nextSession;
      aetherMini.peer = "";
      aetherMini.connected = true;
      restoreAetherMiniHistory(nextSession);
    } else {
      aetherMini.connected = true;
      updateAetherComposer();
    }
    flushAutoShares();
  } else {
    clearAetherMiniSession();
    aetherConfig.classList.add("open");
  }
});

window.addEventListener("aether-message", event => {
  const data = event.detail || {};
  const entry = {
    id: data.id,
    peerId: data.senderId,
    text: data.text,
    direction: "incoming",
    createdAt: data.createdAt
  };
  if (aetherMini.restoring) aetherMini.pending.push(entry);
  else recordAetherMessage(entry);
});

window.addEventListener("aether-sent", event => {
  const data = event.detail || {};
  const entry = {
    id: data.id,
    peerId: data.peerId,
    text: data.text,
    direction: "outgoing",
    createdAt: data.createdAt
  };
  if (aetherMini.restoring) aetherMini.pending.push(entry);
  else recordAetherMessage(entry);
});
window.addEventListener("aether-error", event => {
  const error = event.detail?.message;
  if (error) toast("AETHER: " + error);
});

q("#chatForm").addEventListener("submit", async event => {
  event.preventDefault();
  const input = q("#chatInput");
  const text = input.value.trim();
  const peer = aetherMini.peer;
  if (!text || !peer || !aetherMini.connected || aetherMini.sending) return;
  aetherMini.sending = true;
  updateAetherComposer();
  try {
    await window.aether.sendText(peer, text);
    if (input.value.trim() === text) input.value = "";
  } catch (error) {
    toast(error.message || "Не удалось отправить сообщение");
  } finally {
    aetherMini.sending = false;
    updateAetherComposer();
    input.focus();
  }
});
q("#chatInput").addEventListener("keydown", event => {
  if (event.key === "Enter" && !event.shiftKey && !event.isComposing) {
    event.preventDefault();
    q("#chatForm").requestSubmit();
  }
});
qa("[data-text]").forEach(button => button.addEventListener("click", () => {
  if (!aetherMini.connected || !aetherMini.peer) return;
  q("#chatInput").value = button.dataset.text;
  q("#chatInput").focus();
}));

// Keep operator incident workflow in AEGIS; it calls the same AETHER adapter.
q("#newIncident").onclick = () => openModal("incidentModal");
q("#incidentForm")?.addEventListener("submit", async event => {
  event.preventDefault();
  const incident = {
    id: "INC-" + Date.now().toString(36).toUpperCase(),
    title: q("#incidentTitleInput").value.trim(),
    source: q("#incidentSourceInput").value.trim(),
    severity: q("#incidentSeverityInput").value,
    status: "Новый",
    description: q("#incidentDescriptionInput").value.trim(),
    events: [alertClock() + " — создан вручную оператором"],
    auto: false,
    aetherSent: false,
    ruleKey: null,
    createdAt: new Date().toISOString(),
    time: alertClock()
  };
  state.incidents.unshift(incident);
  state.current = incident.id;
  await saveIncidentApi(incident);
  await appendAudit("incident.manual", incident.id, incident.title);
  event.target.reset();
  closeModal("incidentModal");
  renderIncidents();
  toast("Инцидент создан");
});
q("#notify").onclick = () => toast(state.incidents.filter(i => i.status !== "Закрыт").length + " активных инцидентов");
clearAetherMiniSession();
