// AEGIS power controls: separate demo BSOD and explicit, cancellable Windows shutdown.
// The demo NEVER calls the power API.
const crashConfirm = q("#crashConfirm");
const bsod = q("#bsod");
const fakeOff = q("#fakeOff");
const shutdownForm = q("#powerConfirmForm");
let powerMode = "demo";
let scheduledShutdown = false;
let crashTimer = null;

function resetPowerTimer() {
  if (crashTimer !== null) clearInterval(crashTimer);
  crashTimer = null;
}
function closePowerDialog() {
  crashConfirm.classList.remove("show");
  crashConfirm.setAttribute("aria-hidden", "true");
  shutdownForm.hidden = true;
  q("#shutdownPhrase").value = "";
  q("#shutdownAcknowledge").checked = false;
  q("#powerFeedback").textContent = "";
}
function openPowerDialog() {
  if (scheduledShutdown) {
    toast("Выключение запланировано. Нажми «Отменить выключение».");
    return;
  }
  crashConfirm.classList.add("show");
  crashConfirm.setAttribute("aria-hidden", "false");
}
async function enterBsod() {
  try {
    if (!document.fullscreenElement) await document.documentElement.requestFullscreen();
  } catch {
    // The overlay remains screen-sized within the browser if fullscreen is denied.
  }
  bsod.classList.add("show");
  bsod.setAttribute("aria-hidden", "false");
  fakeOff.classList.remove("show");
  fakeOff.setAttribute("aria-hidden", "true");
  q("#crashProgress").textContent = "0";
}
async function exitPowerOverlay() {
  bsod.classList.remove("show");
  bsod.setAttribute("aria-hidden", "true");
  fakeOff.classList.remove("show");
  fakeOff.setAttribute("aria-hidden", "true");
  if (document.fullscreenElement) {
    try { await document.exitFullscreen(); } catch {}
  }
}
function setShutdownControls(visible) {
  q("#cancelScheduledShutdown").hidden = !visible;
  q("#cancelShutdownOffline").hidden = !visible;
  q("#restoreSystem").hidden = visible;
}
async function startFakeCrash() {
  resetPowerTimer();
  powerMode = "demo";
  scheduledShutdown = false;
  closePowerDialog();
  setShutdownControls(false);
  q("#bsodModeLabel").textContent = "ДЕМОНСТРАЦИЯ — Windows не выключается";
  await enterBsod();
  let progress = 0;
  crashTimer = setInterval(() => {
    progress = Math.min(100, progress + Math.floor(Math.random() * 12) + 4);
    q("#crashProgress").textContent = String(progress);
    if (progress >= 100) {
      resetPowerTimer();
      setTimeout(() => {
        if (powerMode !== "demo") return;
        bsod.classList.remove("show");
        bsod.setAttribute("aria-hidden", "true");
        q("#offlineStateText").textContent = "Демо: компьютер выключен только на экране";
        fakeOff.classList.add("show");
        fakeOff.setAttribute("aria-hidden", "false");
      }, 650);
    }
  }, 260);
}
function showRealPowerForm() {
  shutdownForm.hidden = false;
  q("#shutdownPhrase").focus();
}
function showShutdownCountdown(delay) {
  powerMode = "shutdown";
  scheduledShutdown = true;
  setShutdownControls(true);
  q("#bsodModeLabel").textContent = "WINDOWS SHUTDOWN • 45 секунд на отмену";
  closePowerDialog();
  enterBsod();
  const started = Date.now();
  resetPowerTimer();
  crashTimer = setInterval(() => {
    const remaining = Math.max(0, delay - Math.floor((Date.now() - started) / 1000));
    q("#crashProgress").textContent = String(Math.min(99, Math.floor((delay - remaining) / delay * 100)));
    q("#bsodModeLabel").textContent =
      "Реальное выключение Windows через " + remaining + " сек. Нажми «Отменить выключение», чтобы остановить.";
    if (remaining <= 0) {
      resetPowerTimer();
      q("#bsodModeLabel").textContent = "Команда выключения передана Windows.";
    }
  }, 250);
}
async function requestRealShutdown(event) {
  event.preventDefault();
  const phrase = q("#shutdownPhrase").value.trim();
  const acknowledged = q("#shutdownAcknowledge").checked;
  if (phrase !== "ВЫКЛЮЧИТЬ" || !acknowledged) {
    q("#powerFeedback").textContent = "Введи ВЫКЛЮЧИТЬ и подтверди предупреждение.";
    return;
  }
  // Fullscreen requires the original user gesture. Request it before network I/O.
  const fullscreenRequested = (!document.fullscreenElement && document.documentElement.requestFullscreen)
    ? document.documentElement.requestFullscreen().catch(() => {})
    : Promise.resolve();
  const submit = q("#scheduleShutdownButton");
  submit.disabled = true;
  q("#powerFeedback").textContent = "Проверяю подтверждение Windows…";
  try {
    const result = await fetch("/api/v1/system/shutdown", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "X-AEGIS-Operator-Intent": "local-shutdown-confirmed"
      },
      body: JSON.stringify({ confirmation: phrase })
    });
    const payload = await result.json().catch(() => ({}));
    if (!result.ok || !payload.success) throw new Error(payload.detail || payload.message || "Команда отклонена.");
    showShutdownCountdown(Number(payload.delaySeconds) || 45);
  } catch (error) {
    q("#powerFeedback").textContent = error.message || "Не удалось запросить выключение.";
    await fullscreenRequested;
    if (document.fullscreenElement && !scheduledShutdown) {
      try { await document.exitFullscreen(); } catch {}
    }
  } finally {
    submit.disabled = false;
  }
}
async function cancelRealShutdown() {
  if (!scheduledShutdown) return;
  const buttons = [q("#cancelScheduledShutdown"), q("#cancelShutdownOffline")];
  buttons.forEach(button => { button.disabled = true; button.textContent = "Отмена выключения…"; });
  try {
    const result = await fetch("/api/v1/system/shutdown/cancel", {
      method: "POST",
      headers: { "X-AEGIS-Operator-Intent": "local-shutdown-confirmed" }
    });
    const payload = await result.json().catch(() => ({}));
    if (!result.ok || !payload.success) throw new Error(payload.detail || payload.message || "Windows не подтвердила отмену");
    resetPowerTimer();
    scheduledShutdown = false;
    powerMode = "demo";
    await exitPowerOverlay();
    toast("Выключение Windows отменено");
  } catch (error) {
    q("#bsodModeLabel").textContent = "Ошибка отмены: " + (error.message || "проверь Windows");
    toast("Не удалось отменить выключение. Проверь состояние Windows.");
  } finally {
    buttons.forEach(button => { button.disabled = false; button.textContent = "Отменить выключение Windows"; });
  }
}
q("#crashButton").addEventListener("click", openPowerDialog);
q("#cancelCrash").addEventListener("click", closePowerDialog);
q("#confirmCrash").addEventListener("click", startFakeCrash);
q("#realShutdownButton").addEventListener("click", showRealPowerForm);
shutdownForm.addEventListener("submit", requestRealShutdown);
q("#restoreSystem").addEventListener("click", async () => {
  if (scheduledShutdown) return;
  resetPowerTimer();
  await exitPowerOverlay();
  toast("AEGIS возвращён из демонстрационного режима");
});
q("#cancelScheduledShutdown").addEventListener("click", cancelRealShutdown);
q("#cancelShutdownOffline").addEventListener("click", cancelRealShutdown);
document.addEventListener("keydown", event => {
  if (event.key !== "Escape") return;
  if (scheduledShutdown) { event.preventDefault(); cancelRealShutdown(); }
  else if (crashConfirm.classList.contains("show")) closePowerDialog();
});
