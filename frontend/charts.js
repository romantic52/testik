// AEGIS time-series charts: real axes, gridlines, value labels, gaps for missing
// samples and a hover readout. Monochrome: series differ by dash pattern and width.
(() => {
  "use strict";
  const NS = "http://www.w3.org/2000/svg";
  const PAD = { left: 46, right: 14, top: 12, bottom: 26 };

  function el(name, attrs = {}, parent = null) {
    const node = document.createElementNS(NS, name);
    for (const [key, value] of Object.entries(attrs)) node.setAttribute(key, String(value));
    if (parent) parent.append(node);
    return node;
  }
  function num(value) {
    if (value === null || value === undefined || value === "") return null;
    const n = Number(value);
    return Number.isFinite(n) ? n : null;
  }
  function formatBytes(value) {
    const n = Math.abs(value);
    if (n >= 1073741824) return (value / 1073741824).toFixed(1) + " ГБ/с";
    if (n >= 1048576) return (value / 1048576).toFixed(1) + " МБ/с";
    if (n >= 1024) return (value / 1024).toFixed(0) + " КБ/с";
    return Math.round(value) + " Б/с";
  }
  function formatValue(mode, value) {
    if (value === null) return "нет данных";
    return mode === "bytes" ? formatBytes(value) : value.toFixed(1) + "%";
  }
  function niceMax(value) {
    if (!(value > 0)) return 1024;
    const exp = Math.pow(10, Math.floor(Math.log10(value)));
    for (const step of [1, 2, 2.5, 5, 10]) if (step * exp >= value) return step * exp;
    return 10 * exp;
  }
  function timeLabel(ms, spanMs) {
    const d = new Date(ms);
    const opts = spanMs <= 15 * 60 * 1000
      ? { hour: "2-digit", minute: "2-digit", second: "2-digit" }
      : { hour: "2-digit", minute: "2-digit" };
    return d.toLocaleTimeString("ru-RU", opts);
  }

  function create(host, options) {
    const opts = { mode: "percent", height: 220, gapMs: 15000, ...options };
    host.classList.add("aegis-chart");
    host.replaceChildren();
    const legend = document.createElement("div");
    legend.className = "aegis-chart-legend";
    const legendValues = new Map();
    for (const series of opts.series) {
      const item = document.createElement("span");
      item.className = "aegis-chart-key";
      const swatch = el("svg", { width: 26, height: 10, "aria-hidden": "true" });
      el("line", { x1: 1, y1: 5, x2: 25, y2: 5, class: "aegis-series " + series.cls }, swatch);
      const label = document.createElement("b");
      label.textContent = series.label;
      const value = document.createElement("em");
      value.textContent = "—";
      item.append(swatch, label, value);
      legend.append(item);
      legendValues.set(series.key, value);
    }
    const svg = el("svg", { class: "aegis-chart-svg", role: "img", "aria-label": opts.ariaLabel || "График" });
    const empty = document.createElement("p");
    empty.className = "aegis-chart-empty";
    empty.textContent = opts.emptyText || "Ожидание данных агента…";
    const tooltip = document.createElement("div");
    tooltip.className = "aegis-chart-tooltip";
    tooltip.hidden = true;
    host.append(legend, svg, empty, tooltip);

    let last = { points: [], from: 0, to: 0 };
    let geometry = null;

    function draw() {
      const width = Math.max(240, Math.floor(host.clientWidth || 600));
      const height = opts.height;
      svg.setAttribute("viewBox", "0 0 " + width + " " + height);
      svg.setAttribute("width", width);
      svg.setAttribute("height", height);
      svg.replaceChildren();
      const { points, from, to } = last;
      const gapMs = last.gapMs ?? opts.gapMs;
      const plotW = width - PAD.left - PAD.right;
      const plotH = height - PAD.top - PAD.bottom;
      const span = Math.max(1, to - from);

      let max = 100;
      if (opts.mode === "bytes") {
        let peak = 0;
        for (const p of points) for (const s of opts.series) {
          const v = num(p[s.key]);
          if (v !== null && v > peak) peak = v;
        }
        max = niceMax(peak * 1.1);
      }
      const x = t => PAD.left + (t - from) / span * plotW;
      const y = v => PAD.top + plotH - Math.max(0, Math.min(max, v)) / max * plotH;

      const grid = el("g", { class: "aegis-chart-grid" }, svg);
      for (let i = 0; i <= 4; i++) {
        const value = max * i / 4;
        const yy = y(value);
        el("line", { x1: PAD.left, y1: yy, x2: width - PAD.right, y2: yy, class: i === 0 ? "base" : "" }, grid);
        const text = el("text", { x: PAD.left - 6, y: yy + 4, "text-anchor": "end" }, grid);
        text.textContent = opts.mode === "bytes" ? formatBytes(value) : Math.round(value) + "%";
      }
      for (let i = 0; i <= 4; i++) {
        const t = from + span * i / 4;
        const xx = x(t);
        el("line", { x1: xx, y1: PAD.top, x2: xx, y2: PAD.top + plotH, class: "v" }, grid);
        const text = el("text", {
          x: xx, y: height - 8,
          "text-anchor": i === 0 ? "start" : i === 4 ? "end" : "middle"
        }, grid);
        text.textContent = i === 4 ? "сейчас" : timeLabel(t, span);
      }

      if (last.band) {
        const bx1 = x(Math.max(from, last.band.from)), bx2 = x(Math.min(to, last.band.to ?? to));
        if (bx2 > bx1) {
          el("rect", { x: bx1, y: PAD.top, width: bx2 - bx1, height: plotH, class: "aegis-band" }, svg);
          const label = el("text", { x: bx1 + 6, y: PAD.top + 14, class: "aegis-band-label" }, svg);
          label.textContent = last.band.label || "тест";
        }
      }
      const visible = points.filter(p => p.t >= from && p.t <= to);
      let anyValue = false;
      opts.series.forEach((series, index) => {
        const segments = [];
        let current = [];
        let previousT = null;
        for (const p of visible) {
          const v = num(p[series.key]);
          if (v === null || (previousT !== null && p.t - previousT > gapMs)) {
            if (current.length) segments.push(current);
            current = [];
          }
          if (v !== null) { current.push([x(p.t), y(v)]); anyValue = true; }
          previousT = p.t;
        }
        if (current.length) segments.push(current);
        const d = segments.map(seg => seg.map((pt, i) => (i ? "L" : "M") + pt[0].toFixed(1) + " " + pt[1].toFixed(1)).join(" ")).join(" ");
        if (index === 0 && opts.area !== false) {
          const areaD = segments.filter(s => s.length > 1).map(seg =>
            seg.map((pt, i) => (i ? "L" : "M") + pt[0].toFixed(1) + " " + pt[1].toFixed(1)).join(" ")
            + " L" + seg.at(-1)[0].toFixed(1) + " " + y(0).toFixed(1)
            + " L" + seg[0][0].toFixed(1) + " " + y(0).toFixed(1) + " Z").join(" ");
          if (areaD) el("path", { d: areaD, class: "aegis-area" }, svg);
        }
        if (d) el("path", { d, class: "aegis-series " + series.cls }, svg);
        const tail = segments.at(-1)?.at(-1);
        if (tail) el("circle", { cx: tail[0], cy: tail[1], r: 3.2, class: "aegis-dot " + series.cls }, svg);
      });

      const latest = visible.at(-1);
      for (const series of opts.series) {
        let value = null;
        for (let i = visible.length - 1; i >= 0 && value === null; i--) {
          value = num(visible[i][series.key]);
          if (latest && latest.t - visible[i].t > gapMs) break;
        }
        legendValues.get(series.key).textContent = formatValue(opts.mode, value);
      }
      empty.hidden = anyValue;
      geometry = { width, plotW, from, span, visible, x };
      el("line", { class: "aegis-cursor", x1: PAD.left, x2: PAD.left, y1: PAD.top, y2: PAD.top + plotH, visibility: "hidden" }, svg);
    }

    function hover(event) {
      if (!geometry || !geometry.visible.length) return;
      const rect = svg.getBoundingClientRect();
      const px = (event.clientX - rect.left) * (geometry.width / rect.width);
      const t = geometry.from + (px - PAD.left) / geometry.plotW * geometry.span;
      let best = null;
      for (const p of geometry.visible) if (!best || Math.abs(p.t - t) < Math.abs(best.t - t)) best = p;
      if (!best) return;
      const cursor = svg.querySelector(".aegis-cursor");
      const cx = geometry.x(best.t);
      cursor.setAttribute("x1", cx);
      cursor.setAttribute("x2", cx);
      cursor.setAttribute("visibility", "visible");
      tooltip.replaceChildren();
      const time = document.createElement("time");
      time.textContent = new Date(best.t).toLocaleTimeString("ru-RU");
      tooltip.append(time);
      for (const series of opts.series) {
        const row = document.createElement("div");
        const label = document.createElement("span");
        label.textContent = series.label;
        const value = document.createElement("b");
        value.textContent = formatValue(opts.mode, num(best[series.key]));
        row.append(label, value);
        tooltip.append(row);
      }
      tooltip.hidden = false;
      // CSS px of the host (clientWidth), not screen px (rect): with the in-app scale (CSS zoom) they differ.
      const width = svg.clientWidth || rect.width;
      const left = cx / geometry.width * width;
      tooltip.classList.toggle("flip", left > width * 0.6);
      tooltip.style.left = Math.round(left) + "px";
    }
    svg.addEventListener("mousemove", hover);
    svg.addEventListener("mouseleave", () => {
      tooltip.hidden = true;
      const cursor = svg.querySelector(".aegis-cursor");
      if (cursor) cursor.setAttribute("visibility", "hidden");
    });
    if (typeof ResizeObserver === "function") new ResizeObserver(() => draw()).observe(host);

    return {
      render(points, range) {
        last = { points, from: range.from, to: range.to, gapMs: range.gapMs, band: range.band };
        draw();
      }
    };
  }

  window.AegisCharts = { create, formatBytes };
})();
