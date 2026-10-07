(() => {
  "use strict";

  const BRIDGE = "/api/v1/aether/relay";
  const encoder = new TextEncoder();
  const decoder = new TextDecoder();
  const MAX_SESSIONS_PER_DEVICE = 5;
  const FALLBACK_ROTATE_MS = 7 * 24 * 60 * 60 * 1000;

  function b64urlEncode(bytes) {
    let binary = "";
    for (const byte of bytes) binary += String.fromCharCode(byte);
    return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
  }

  function b64urlDecode(value) {
    let text = String(value || "").replace(/-/g, "+").replace(/_/g, "/");
    while (text.length % 4) text += "=";
    const binary = atob(text);
    const result = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) result[i] = binary.charCodeAt(i);
    return result;
  }

  function randomId(prefix = "") {
    const bytes = crypto.getRandomValues(new Uint8Array(8));
    return prefix + Array.from(bytes, b => b.toString(16).padStart(2, "0")).join("");
  }

  function normalizeServer(raw) {
    const url = new URL(String(raw || "").trim());
    const local = ["localhost", "127.0.0.1", "::1"].includes(url.hostname);
    if (url.protocol !== "https:" && !(url.protocol === "http:" && local)) {
      throw new Error("Удалённый AETHER relay должен работать через HTTPS");
    }
    if (url.username || url.password) throw new Error("URL relay не должен содержать логин/пароль");
    return url.origin;
  }

  async function deriveBackupKey(password, salt) {
    const keyMaterial = await crypto.subtle.importKey(
      "raw", encoder.encode(password), { name: "PBKDF2" }, false, ["deriveKey"]
    );
    return crypto.subtle.deriveKey(
      { name: "PBKDF2", salt, iterations: 100000, hash: "SHA-256" },
      keyMaterial,
      { name: "AES-GCM", length: 256 },
      false,
      ["encrypt", "decrypt"]
    );
  }

  async function decryptPrivateKeyBackup(blob, password) {
    const parts = String(blob || "").split(":");
    if (parts.length !== 3) throw new Error("AETHER: неверный формат backup приватного ключа");
    const salt = b64urlDecode(parts[0]);
    const iv = b64urlDecode(parts[1]);
    const ciphertext = b64urlDecode(parts[2]);
    const key = await deriveBackupKey(password, salt);
    const clear = await crypto.subtle.decrypt({ name: "AES-GCM", iv }, key, ciphertext);
    return decoder.decode(clear);
  }

  async function deriveLocalKey(password, salt) {
    const material = await crypto.subtle.importKey(
      "raw", encoder.encode(password), "PBKDF2", false, ["deriveKey"]
    );
    return crypto.subtle.deriveKey(
      { name: "PBKDF2", salt, iterations: 180000, hash: "SHA-256" },
      material,
      { name: "AES-GCM", length: 256 },
      false,
      ["encrypt", "decrypt"]
    );
  }

  async function encryptLocalState(value, password) {
    const salt = crypto.getRandomValues(new Uint8Array(16));
    const iv = crypto.getRandomValues(new Uint8Array(12));
    const key = await deriveLocalKey(password, salt);
    const ciphertext = await crypto.subtle.encrypt(
      { name: "AES-GCM", iv },
      key,
      encoder.encode(JSON.stringify(value))
    );
    return JSON.stringify({
      v: 1,
      salt: b64urlEncode(salt),
      iv: b64urlEncode(iv),
      ct: b64urlEncode(new Uint8Array(ciphertext))
    });
  }

  async function decryptLocalState(blob, password) {
    try {
      const value = JSON.parse(blob);
      if (value.v !== 1) return null;
      const key = await deriveLocalKey(password, b64urlDecode(value.salt));
      const clear = await crypto.subtle.decrypt(
        { name: "AES-GCM", iv: b64urlDecode(value.iv) },
        key,
        b64urlDecode(value.ct)
      );
      return JSON.parse(decoder.decode(clear));
    } catch {
      return null;
    }
  }

  class AetherAdapter {
    constructor() {
      this.connected = false;
      this.serverUrl = "";
      this.userId = "";
      this.password = "";
      this.token = "";
      this.peerId = "";
      this.deviceId = "";
      this.boxSecretB64 = "";
      this.ratchet = null;
      this.accountPickle = "";
      this.identityB64 = "";
      this.sessions = Object.create(null);
      this.identityPins = Object.create(null);
      this.edPins = Object.create(null);
      this.masterPins = Object.create(null);
      this.peerDevices = Object.create(null);
      this.ws = null;
      this.pollTimer = null;
      this.pollBusy = false;
      this.cryptoQueue = Promise.resolve();
    }

    emit(name, detail = {}) {
      window.dispatchEvent(new CustomEvent(name, { detail }));
    }

    stateNamespace() {
      return encodeURIComponent(this.serverUrl) + "::" + this.userId;
    }

    stateKey() {
      return "aegis_aether_ratchet::" + this.stateNamespace();
    }

    deviceKeyStorage() {
      return "aegis_aether_device::" + this.stateNamespace();
    }

    fallbackKeyStorage() {
      return "aegis_aether_fallback::" + this.stateNamespace();
    }

    pinKey(peerId, deviceId) {
      return deviceId === "primary" ? peerId : peerId + "::" + deviceId;
    }

    authHeaders() {
      return this.token ? { Authorization: "Bearer " + this.token } : {};
    }

    async relay(method, path, body = null, useToken = true) {
      const response = await fetch(BRIDGE, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          serverUrl: this.serverUrl,
          method,
          path,
          token: useToken ? this.token : null,
          body
        })
      });

      let data = null;
      const text = await response.text();
      if (text) {
        try { data = JSON.parse(text); }
        catch { data = { detail: text }; }
      }

      if (!response.ok) {
        const error = new Error(data?.detail || ("AETHER HTTP " + response.status));
        error.status = response.status;
        error.data = data;
        throw error;
      }
      return data || {};
    }

    async loadRatchet() {
      if (this.ratchet) return this.ratchet;
      try {
        const module = await import("./vendor/ratchet/aether_ratchet_wasm.js");
        await module.default();
        this.ratchet = module;
        return module;
      } catch (error) {
        throw new Error("Не загружен AETHER Ratchet WASM: " + (error?.message || error));
      }
    }

    async connect({ serverUrl, userId, password, totpCode = "", peerId = "" }) {
      await this.disconnect(false);

      this.serverUrl = normalizeServer(serverUrl);
      this.userId = String(userId || "").trim().toLowerCase().replace(/^@/, "");
      this.password = String(password || "");
      this.peerId = String(peerId || "").trim().toLowerCase().replace(/^@/, "");

      if (!/^[a-z0-9_]{2,64}$/i.test(this.userId)) throw new Error("Некорректный AETHER user_id");
      if (!this.password) throw new Error("Введите пароль AETHER");
      if (this.peerId && !/^[a-z0-9_]{2,64}$/i.test(this.peerId)) throw new Error("Некорректный peer user_id");

      const loginBody = { user_id: this.userId, password: this.password };
      if (String(totpCode || "").trim()) loginBody.totp_code = String(totpCode).trim();

      let login;
      try {
        login = await this.relay("POST", "/users/login", loginBody, false);
      } catch (error) {
        if (error.status === 401 && error.data?.detail === "totp_required") {
          error.code = "TOTP_REQUIRED";
        } else if (error.status === 401 && error.data?.detail === "totp_invalid") {
          error.code = "TOTP_INVALID";
        }
        throw error;
      }

      this.token = login.token || "";
      if (!this.token) throw new Error("AETHER relay не вернул session token");
      if (!login.encrypted_private_key_b64) throw new Error("У аккаунта нет encrypted_private_key_b64");

      this.boxSecretB64 = await decryptPrivateKeyBackup(login.encrypted_private_key_b64, this.password);

      if (!window.nacl?.box?.keyPair?.fromSecretKey) {
        throw new Error("NaCl dependency не загружена");
      }
      const pair = window.nacl.box.keyPair.fromSecretKey(b64urlDecode(this.boxSecretB64));
      const derivedPublic = b64urlEncode(pair.publicKey);
      if (login.public_key_b64 && derivedPublic !== login.public_key_b64) {
        throw new Error("AETHER backup приватного ключа не совпадает с public key аккаунта");
      }

      await this.prepareRatchet();
      await this.bindDevice();

      this.connected = true;
      this.emit("aether-state", {
        connected: true,
        userId: this.userId,
        peerId: this.peerId,
        deviceId: this.deviceId,
        serverUrl: this.serverUrl
      });

      this.connectRealtime();
      await this.pollInbox();
      this.pollTimer = setInterval(() => this.pollInbox().catch(() => {}), 2500);
      this.heartbeat().catch(() => {});
      return { userId: this.userId, deviceId: this.deviceId };
    }

    async disconnect(callServer = true) {
      if (this.pollTimer) clearInterval(this.pollTimer);
      this.pollTimer = null;

      if (this.ws) {
        try { this.ws.onclose = null; this.ws.close(); } catch {}
      }
      this.ws = null;

      if (callServer && this.token && this.serverUrl) {
        try { await this.relay("POST", "/logout", {}); } catch {}
      }

      const wasConnected = this.connected;
      this.connected = false;
      this.token = "";
      this.password = "";
      this.boxSecretB64 = "";

      if (wasConnected) this.emit("aether-state", { connected: false });
    }

    async prepareRatchet() {
      const api = await this.loadRatchet();
      const storedDevice = localStorage.getItem(this.deviceKeyStorage());
      this.deviceId = storedDevice || randomId("soc-");
      localStorage.setItem(this.deviceKeyStorage(), this.deviceId);

      const encrypted = localStorage.getItem(this.stateKey());
      const local = encrypted ? await decryptLocalState(encrypted, this.password) : null;

      this.accountPickle = local?.account_pickle || api.account_new();
      this.identityB64 = api.account_identity(this.accountPickle);

      if (local?.account_identity === this.identityB64) {
        this.sessions = local.sessions || Object.create(null);
        this.identityPins = local.identity_pins || Object.create(null);
        this.edPins = local.identity_ed_pins || Object.create(null);
        this.masterPins = local.master_pins || Object.create(null);
      }

      await this.ensureOwnKeys();
      await this.saveState();
    }

    async saveState() {
      if (!this.accountPickle || !this.password) return;
      const encrypted = await encryptLocalState({
        account_pickle: this.accountPickle,
        account_identity: this.identityB64,
        sessions: this.sessions,
        identity_pins: this.identityPins,
        identity_ed_pins: this.edPins,
        master_pins: this.masterPins
      }, this.password);
      localStorage.setItem(this.stateKey(), encrypted);
    }

    sessionEntries(key) {
      const map = this.sessions[key] && typeof this.sessions[key] === "object"
        ? this.sessions[key] : (this.sessions[key] = Object.create(null));
      return Object.keys(map)
        .filter(id => map[id]?.s)
        .sort((a, b) => (map[b].t || 0) - (map[a].t || 0))
        .map(id => ({ id, pickle: map[id].s }));
    }

    putSession(key, id, pickle) {
      const map = this.sessions[key] && typeof this.sessions[key] === "object"
        ? this.sessions[key] : (this.sessions[key] = Object.create(null));
      map[id] = { s: pickle, t: Date.now() };
      const ids = Object.keys(map).sort((a, b) => (map[b].t || 0) - (map[a].t || 0));
      for (const stale of ids.slice(MAX_SESSIONS_PER_DEVICE)) delete map[stale];
    }

    async ensureOwnKeys() {
      const api = await this.loadRatchet();
      let info = await this.relay("GET", "/keys/count?device_id=" + encodeURIComponent(this.deviceId));
      const serverIdentity = typeof info.identity_key_b64 === "string" ? info.identity_key_b64 : "";

      if (serverIdentity && serverIdentity !== this.identityB64) {
        this.deviceId = randomId("soc-");
        localStorage.setItem(this.deviceKeyStorage(), this.deviceId);
        info = { count: 0, identity_key_b64: "" };
      }

      const count = Number(info.count) || 0;
      const fallbackStamp = Number(localStorage.getItem(this.fallbackKeyStorage())) || 0;
      const fallbackStale = Date.now() - fallbackStamp > FALLBACK_ROTATE_MS;
      if (count >= 20 && serverIdentity === this.identityB64 && !fallbackStale) return;

      const publish = JSON.parse(api.account_generate_otks_signed(
        this.accountPickle,
        Math.max(50 - count, 20),
        this.userId,
        this.deviceId
      ));
      this.accountPickle = publish.account_pickle;
      this.identityB64 = publish.identity_key_b64;

      let fallback = null;
      if (fallbackStale) {
        fallback = JSON.parse(api.account_generate_fallback_signed(
          this.accountPickle, this.userId, this.deviceId
        ));
        this.accountPickle = fallback.account_pickle;
      }

      const masterKeyB64 = api.master_public(this.boxSecretB64);
      const deviceSigB64 = api.sign_device(
        this.boxSecretB64,
        this.userId,
        this.deviceId,
        this.identityB64,
        publish.ed25519_key_b64
      );

      const body = {
        identity_key_b64: this.identityB64,
        ed25519_key_b64: publish.ed25519_key_b64,
        identity_sig_b64: publish.identity_sig_b64,
        one_time_keys: JSON.parse(publish.one_time_keys_json),
        otk_signatures: JSON.parse(publish.otk_signatures_json),
        device_id: this.deviceId,
        master_key_b64: masterKeyB64,
        device_sig_b64: deviceSigB64
      };
      if (fallback) {
        body.fallback_key = {
          key_id: fallback.key_id,
          key_b64: fallback.key_b64,
          sig_b64: fallback.sig_b64
        };
      }

      await this.relay("PUT", "/keys/upload", body);
      if (fallback) localStorage.setItem(this.fallbackKeyStorage(), String(Date.now()));
      this.peerDevices = Object.create(null);
      await this.saveState();
    }

    async bindDevice() {
      await this.relay("PUT", "/sessions/me/device", {
        device_id: this.deviceId,
        identity_key_b64: this.identityB64
      });
    }

    async heartbeat() {
      if (!this.connected) return;
      await this.relay("POST", "/users/me/heartbeat", {});
      setTimeout(() => this.heartbeat().catch(() => {}), 30000);
    }

    async getPeerDevices(peerId, force = false) {
      const peer = peerId.toLowerCase();
      const cached = this.peerDevices[peer];
      if (!force && cached && Date.now() - cached.ts < 60000) return cached.devices;

      const data = await this.relay("GET", "/users/" + encodeURIComponent(peer) + "/devices");
      const devices = Array.isArray(data.devices) ? data.devices : [];
      this.peerDevices[peer] = { devices, ts: Date.now() };
      return devices;
    }

    verifyDevice(api, peerId, device) {
      const peer = peerId.toLowerCase();
      const key = this.pinKey(peer, device.device_id);
      const identity = String(device.identity_key_b64 || "");
      const ed = String(device.ed25519_key_b64 || "");
      const master = String(device.master_key_b64 || "");
      const sig = String(device.device_sig_b64 || "");

      if (!identity) throw new Error("AETHER device без identity key");
      if (!(ed && master && sig)) {
        if (this.identityPins[key] === identity) return;
        throw new Error("Неподписанное устройство AETHER отклонено: " + device.device_id);
      }

      api.verify_device(master, peer, device.device_id, identity, ed, sig);

      if (this.masterPins[peer] && this.masterPins[peer] !== master) {
        throw new Error("Изменился master key аккаунта @" + peer + ". Нужна ручная сверка.");
      }
      if (this.identityPins[key] && this.identityPins[key] !== identity) {
        throw new Error("Изменился identity key устройства " + device.device_id);
      }
      if (this.edPins[key] && this.edPins[key] !== ed) {
        throw new Error("Изменился signing key устройства " + device.device_id);
      }

      this.masterPins[peer] = master;
      this.identityPins[key] = identity;
      this.edPins[key] = ed;
    }

    verifyBundle(api, peerId, deviceId, bundle) {
      const otk = bundle.one_time_key || {};
      const ed = String(bundle.ed25519_key_b64 || "");
      const idSig = String(bundle.identity_sig_b64 || "");
      const otkSig = String(otk.sig_b64 || "");
      if (!(bundle.identity_key_b64 && otk.key_id && otk.key_b64 && ed && idSig && otkSig)) {
        throw new Error("AETHER prekey bundle неполный или не подписан");
      }

      api.verify_prekey_bundle(
        peerId,
        deviceId,
        bundle.identity_key_b64,
        ed,
        idSig,
        otk.key_id,
        otk.key_b64,
        otkSig
      );

      this.verifyDevice(api, peerId, {
        device_id: deviceId,
        identity_key_b64: bundle.identity_key_b64,
        ed25519_key_b64: ed,
        master_key_b64: bundle.master_key_b64,
        device_sig_b64: bundle.device_sig_b64
      });
    }

    serializeCrypto(task) {
      const next = this.cryptoQueue.then(task, task);
      this.cryptoQueue = next.catch(() => {});
      return next;
    }

    async encryptForDevice(peerId, deviceId, plaintext) {
      return this.serializeCrypto(async () => {
        const api = await this.loadRatchet();
        const key = this.pinKey(peerId, deviceId);
        const known = this.sessionEntries(key);
        let sessionId = known[0]?.id || "";
        let session = known[0]?.pickle || "";

        if (!session) {
          const bundle = await this.relay(
            "POST",
            "/keys/claim/" + encodeURIComponent(peerId) + "?device_id=" + encodeURIComponent(deviceId),
            {}
          );
          this.verifyBundle(api, peerId, deviceId, bundle);
          session = api.create_outbound(
            this.accountPickle,
            bundle.identity_key_b64,
            bundle.one_time_key.key_b64
          );
          sessionId = api.session_id(session);
        }

        const encrypted = JSON.parse(api.encrypt(session, plaintext));
        this.putSession(key, sessionId, encrypted.session_pickle);
        await this.saveState();

        return {
          ratchet: "1",
          olm_identity: this.identityB64,
          sender_device: this.deviceId,
          type: encrypted.message_type,
          body_b64: encrypted.body_b64
        };
      });
    }

    async sendText(peerId, text) {
      if (!this.connected) throw new Error("Сначала подключи AETHER.chat");
      const peer = String(peerId || this.peerId || "").trim().toLowerCase().replace(/^@/, "");
      if (!/^[a-z0-9_]{2,64}$/i.test(peer)) throw new Error("Укажи AETHER peer");

      const devices = await this.getPeerDevices(peer, true);
      if (!devices.length) throw new Error("У @" + peer + " нет зарегистрированных crypto-devices");

      const plaintext = JSON.stringify({ type: "text", text: String(text) });
      let firstMessageId = null;

      for (const device of devices) {
        const envelope = await this.encryptForDevice(peer, device.device_id, plaintext);
        const clientId = crypto.randomUUID();
        const response = await this.relay("POST", "/messages", {
          sender_id: this.userId,
          recipient_id: peer,
          envelope,
          client_id: clientId,
          target_device_id: device.device_id
        });
        if (!firstMessageId) firstMessageId = response.message_id;
      }

      this.peerId = peer;
      this.emit("aether-state", {
        connected: true,
        userId: this.userId,
        peerId: this.peerId,
        deviceId: this.deviceId,
        serverUrl: this.serverUrl
      });
      return { messageId: firstMessageId, peerId: peer };
    }

    async sendMessage(payload) {
      return this.sendText(this.peerId, payload?.text ?? "");
    }

    async shareIncident(incident) {
      const text =
        "🚨 " + incident.id + " • " + incident.title + "\n" +
        "Источник: " + incident.source + "\n" +
        "Приоритет: " + incident.severity + "\n" +
        "Статус: " + incident.status;
      return this.sendText(this.peerId, text);
    }

    async openEnvelope(peerId, envelope) {
      return this.serializeCrypto(async () => {
        const api = await this.loadRatchet();
        const identity = String(envelope.olm_identity || "");
        if (!identity) throw new Error("В Ratchet-конверте отсутствует identity");

        let senderDevice = String(envelope.sender_device || "");
        let devices = await this.getPeerDevices(peerId);
        if (!senderDevice) {
          const match = devices.find(d => d.identity_key_b64 === identity);
          senderDevice = match?.device_id || "primary";
        }

        const key = this.pinKey(peerId, senderDevice);
        const knownIdentity = this.identityPins[key] === identity;
        if (!knownIdentity || !this.masterPins[peerId]) {
          if (!devices.find(d => d.device_id === senderDevice)) {
            devices = await this.getPeerDevices(peerId, true);
          }
          const device = devices.find(d => d.device_id === senderDevice);
          if (!device || device.identity_key_b64 !== identity) {
            throw new Error("Устройство отправителя не подтверждено AETHER directory");
          }
          this.verifyDevice(api, peerId, device);
        }

        if (this.identityPins[key] && this.identityPins[key] !== identity) {
          throw new Error("Identity key отправителя изменился");
        }
        this.identityPins[key] = identity;

        const sessions = this.sessionEntries(key);
        let prekeySessionId = "";
        if (Number(envelope.type) === 0) {
          try { prekeySessionId = api.prekey_session_id(envelope.body_b64); } catch {}
        }

        const ordered = sessions.slice();
        const hit = prekeySessionId ? ordered.findIndex(s => s.id === prekeySessionId) : -1;
        if (hit > 0) ordered.unshift(ordered.splice(hit, 1)[0]);

        let lastError = null;
        for (const entry of ordered) {
          try {
            const result = JSON.parse(api.decrypt(entry.pickle, Number(envelope.type), envelope.body_b64));
            this.putSession(key, entry.id, result.session_pickle);
            await this.saveState();
            return result.plaintext;
          } catch (error) {
            lastError = error;
          }
        }

        if (Number(envelope.type) !== 0) {
          throw lastError || new Error("Нет Ratchet-сессии для сообщения");
        }
        if (prekeySessionId && sessions.some(s => s.id === prekeySessionId)) {
          throw lastError || new Error("Prekey известной сессии не расшифровался");
        }

        const inbound = JSON.parse(api.create_inbound(
          this.accountPickle,
          identity,
          envelope.body_b64
        ));
        this.accountPickle = inbound.account_pickle;
        this.putSession(key, api.session_id(inbound.session_pickle), inbound.session_pickle);
        await this.saveState();
        this.ensureOwnKeys().catch(() => {});
        return inbound.plaintext;
      });
    }

    async pollInbox() {
      if (!this.connected || this.pollBusy) return;
      this.pollBusy = true;
      try {
        const data = await this.relay(
          "GET",
          "/messages/inbox/" + encodeURIComponent(this.userId) +
            "?device_id=" + encodeURIComponent(this.deviceId)
        );

        const ack = [];
        for (const item of data.messages || []) {
          const envelope = item.envelope || {};
          if (envelope.ratchet !== "1" && envelope.ratchet !== 1) {
            ack.push(item.id);
            continue;
          }

          try {
            const peer = String(item.sender_id || "").toLowerCase();
            const plaintext = await this.openEnvelope(peer, envelope);
            let wire;
            try { wire = JSON.parse(plaintext); }
            catch { wire = { type: "text", text: plaintext }; }

            if (wire.type === "text") {
              this.emit("aether-message", {
                id: item.id,
                senderId: peer,
                recipientId: item.recipient_id,
                text: String(wire.text ?? ""),
                createdAt: item.created_at
              });
            }
            ack.push(item.id);
          } catch (error) {
            console.warn("AETHER decrypt failed; keeping message unacked", item.id, error);
            this.emit("aether-error", { message: error?.message || String(error) });
          }
        }

        if (ack.length) {
          await this.relay("POST", "/messages/ack", {
            message_ids: ack,
            device_id: this.deviceId
          });
        }
      } finally {
        this.pollBusy = false;
      }
    }

    connectRealtime() {
      if (!this.token || !this.serverUrl) return;
      if (this.ws) {
        try { this.ws.onclose = null; this.ws.close(); } catch {}
      }

      try {
        const url = new URL(this.serverUrl);
        url.protocol = url.protocol === "https:" ? "wss:" : "ws:";
        url.pathname = "/ws";
        url.search = "?token=" + encodeURIComponent(this.token);

        const ws = new WebSocket(url);
        this.ws = ws;

        ws.onopen = () => {
          const ping = setInterval(() => {
            if (ws.readyState === WebSocket.OPEN) ws.send("ping");
            else clearInterval(ping);
          }, 25000);
        };

        ws.onmessage = event => {
          if (event.data === "pong") return;
          try {
            const message = JSON.parse(event.data);
            if (message.type === "new_message") this.pollInbox().catch(() => {});
          } catch {}
        };

        ws.onclose = () => {
          if (!this.connected || this.ws !== ws) return;
          setTimeout(() => this.connectRealtime(), 3000);
        };
        ws.onerror = () => { try { ws.close(); } catch {} };
      } catch {
        // Polling remains the fallback transport.
      }
    }
  }

  window.aether = new AetherAdapter();
})();