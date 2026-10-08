// Isolated and bounded CPU/RAM diagnostic workload for the AEGIS lab.
// Worker termination immediately releases the workload on Stop, navigation or tab hide.
const LIMIT_DURATION_MS = 30000;
const MAX_MEMORY_MIB = 128;
const CYCLE_MS = 100;
const MAX_BUSY_MS = 80;

self.onmessage = event => {
  const data = event.data || {};
  if (data.type !== "start" || !["cpu", "ram"].includes(data.kind)) return;
  const durationMs = Math.min(30000, Math.max(5000, Number(data.durationMs) || 10000));
  const deadline = performance.now() + durationMs;
  const begun = performance.now();

  if (data.kind === "ram") {
    const mib = Math.min(MAX_MEMORY_MIB, Math.max(16, Number(data.memoryMiB) || 32));
    try {
      const memory = new Uint8Array(mib * 1048576);
      // Touch every page to commit it; verify the data before releasing memory.
      for (let offset = 0; offset < memory.length; offset += 4096) {
        memory[offset] = 0xa5;
      }
      self.postMessage({ type: "progress", detail: "Выделено " + mib + " МиБ реальной памяти" });
      const finish = () => {
        let verified = true;
        for (let offset = 0; offset < memory.length; offset += 4096) {
          if (memory[offset] !== 0xa5) verified = false;
        }
        self.postMessage({
          type: "complete",
          metrics: { allocatedMiB: mib, verified, durationSeconds: (performance.now() - begun) / 1000 }
        });
      };
      setTimeout(finish, Math.max(0, deadline - performance.now()));
    } catch (error) {
      self.postMessage({ type: "error", message: "Память недоступна: " + String(error?.message || error) });
    }
    return;
  }

  const dutyPercent = Math.min(80, Math.max(20, Number(data.cpuDutyPercent) || 50));
  const workMs = CYCLE_MS * dutyPercent / 100;
  let cycles = 0, iterations = 0, sink = 0.31;

  function runCycle() {
    if (performance.now() >= deadline) {
      self.postMessage({
        type: "complete",
        metrics: {
          iterations,
          cycles,
          dutyPercent,
          durationSeconds: (performance.now() - begun) / 1000,
          iterationsPerSecond: iterations / Math.max(0.001, (performance.now() - begun) / 1000)
        }
      });
      return;
    }

    const start = performance.now();
    while (performance.now() - start < workMs && performance.now() < deadline) {
      sink = Math.sqrt(sink + 0.01079) * 1.0000001;
      iterations++;
    }
    cycles++;
    if (cycles % 8 === 0)
      self.postMessage({ type: "progress", detail: "Вычислений: " + iterations.toLocaleString("ru-RU") });
    if (!Number.isFinite(sink)) sink = 0.31;
    setTimeout(runCycle, Math.max(1, CYCLE_MS - (performance.now() - start)));
  }
  runCycle();
};
