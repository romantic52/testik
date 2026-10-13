// Isolated and bounded CPU/RAM diagnostic workload for the AEGIS lab.
// One worker = one CPU thread. Worker termination immediately releases the
// workload (and all memory) on Stop, navigation or tab hide.
const MAX_DURATION_MS = 60000;
const MAX_MEMORY_MIB = 2048;
const CHUNK_MIB = 64;
const CYCLE_MS = 100;

self.onmessage = event => {
  const data = event.data || {};
  if (data.type !== "start" || !["cpu", "ram"].includes(data.kind)) return;
  const durationMs = Math.min(MAX_DURATION_MS, Math.max(5000, Number(data.durationMs) || 10000));
  const deadline = performance.now() + durationMs;
  const begun = performance.now();

  if (data.kind === "ram") {
    const mib = Math.min(MAX_MEMORY_MIB, Math.max(16, Number(data.memoryMiB) || 256));
    const chunks = [];
    let allocated = 0;
    try {
      while (allocated < mib) {
        const size = Math.min(CHUNK_MIB, mib - allocated);
        const chunk = new Uint8Array(size * 1048576);
        // Touch every page so Windows really commits it.
        for (let offset = 0; offset < chunk.length; offset += 4096) chunk[offset] = 0xa5;
        chunks.push(chunk);
        allocated += size;
        self.postMessage({ type: "progress", detail: "Выделено " + allocated + " из " + mib + " МиБ", value: allocated });
      }
    } catch (error) {
      self.postMessage({ type: "error", message: "Браузер отказал в памяти после " + allocated + " МиБ: " + String(error?.message || error) });
      return;
    }
    const hold = setInterval(() => {
      // Re-touch pages so they stay resident rather than being trimmed.
      for (const chunk of chunks) for (let offset = 0; offset < chunk.length; offset += 65536) chunk[offset] ^= 0;
      self.postMessage({ type: "progress", detail: "Удерживается " + allocated + " МиБ", value: allocated });
    }, 1000);
    setTimeout(() => {
      clearInterval(hold);
      let verified = true;
      for (const chunk of chunks)
        for (let offset = 0; offset < chunk.length; offset += 4096)
          if (chunk[offset] !== 0xa5) { verified = false; break; }
      self.postMessage({
        type: "complete",
        metrics: { allocatedMiB: allocated, verified, durationSeconds: (performance.now() - begun) / 1000 }
      });
    }, Math.max(0, deadline - performance.now()));
    return;
  }

  const dutyPercent = Math.min(100, Math.max(10, Number(data.cpuDutyPercent) || 75));
  const workMs = CYCLE_MS * dutyPercent / 100;
  let cycles = 0, iterations = 0, sink = 0.31;

  function runCycle() {
    if (performance.now() >= deadline) {
      self.postMessage({
        type: "complete",
        metrics: {
          iterations, cycles, dutyPercent,
          durationSeconds: (performance.now() - begun) / 1000,
          iterationsPerSecond: iterations / Math.max(0.001, (performance.now() - begun) / 1000)
        }
      });
      return;
    }
    const start = performance.now();
    while (performance.now() - start < workMs && performance.now() < deadline) {
      for (let i = 0; i < 64; i++) sink = Math.sqrt(sink + 0.01079) * 1.0000001;
      iterations += 64;
    }
    cycles++;
    if (cycles % 10 === 0) self.postMessage({ type: "progress", value: iterations });
    if (!Number.isFinite(sink)) sink = 0.31;
    setTimeout(runCycle, Math.max(0, CYCLE_MS - (performance.now() - start)));
  }
  runCycle();
};
