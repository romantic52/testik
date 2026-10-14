// Interface scale inside AEGIS (70–150 %), remembered in this browser. Ctrl+Alt+= / Ctrl+Alt+- / Ctrl+Alt+0.
(() => {
  "use strict";
  const KEY = "aegis_ui_scale", MIN = 70, MAX = 150, STEP = 10;
  let scale = 100;
  try { scale = Math.min(MAX, Math.max(MIN, Number(localStorage.getItem(KEY)) || 100)); } catch {}

  // CSS zoom does not change the viewport width that @media rules see, so a 150 % interface in a
  // 1400 px window would keep the wide layout. Scale the width breakpoints by the same factor.
  const original = new WeakMap();
  function scaleMediaRules(factor) {
    const visit = rules => {
      for (const rule of rules) {
        if (rule instanceof CSSMediaRule) {
          if (!original.has(rule)) original.set(rule, rule.media.mediaText);
          const text = original.get(rule);
          if (/width/.test(text)) {
            const scaled = factor === 1 ? text : text.replace(/(\d+(?:\.\d+)?)px/g, (_, n) => Math.round(Number(n) * factor) + "px");
            if (rule.media.mediaText !== scaled) rule.media.mediaText = scaled;
          }
          visit(rule.cssRules);
        }
      }
    };
    for (const sheet of document.styleSheets) {
      try { visit(sheet.cssRules); } catch { /* not same-origin */ }
    }
  }

  function apply(next) {
    scale = Math.min(MAX, Math.max(MIN, Math.round(next / STEP) * STEP));
    document.documentElement.style.zoom = scale === 100 ? "" : String(scale / 100);
    // Under CSS zoom, viewport units are zoomed too: 100dvh would cover only 70 % of the window at 70 %.
    document.documentElement.style.setProperty("--ui-zoom", String(scale / 100));
    scaleMediaRules(scale / 100);
    try { localStorage.setItem(KEY, String(scale)); } catch {}
    const label = document.getElementById("uiScaleValue");
    if (label) label.textContent = scale + "%";
    const down = document.getElementById("uiScaleDown"), up = document.getElementById("uiScaleUp");
    if (down) down.disabled = scale <= MIN;
    if (up) up.disabled = scale >= MAX;
    window.dispatchEvent(new Event("resize"));   // charts re-measure their width
  }

  apply(scale);
  document.addEventListener("DOMContentLoaded", () => apply(scale));
  window.addEventListener("load", () => apply(scale));
  document.getElementById("uiScaleDown")?.addEventListener("click", () => apply(scale - STEP));
  document.getElementById("uiScaleUp")?.addEventListener("click", () => apply(scale + STEP));
  document.getElementById("uiScaleValue")?.addEventListener("click", () => apply(100));
  document.addEventListener("keydown", event => {
    if (!event.ctrlKey || !event.altKey) return;
    if (event.key === "=" || event.key === "+") { event.preventDefault(); apply(scale + STEP); }
    else if (event.key === "-") { event.preventDefault(); apply(scale - STEP); }
    else if (event.key === "0") { event.preventDefault(); apply(100); }
  });
  window.AegisScale = { get: () => scale, set: apply };
})();
