// Time-limited, bounded browser-side resource checks for the AEGIS lab.
// Hard limits are enforced here as well as in the user interface.
let stopped = false;
let memory = null;
const MAX_DURATION_MS = 12000;
const MAX_MEMORY_BYTES = 64 * 1024 * 1024;

self.onmessage = event => {
  if (event.data?.type !== "start" || !["cpu", "ram"].includes(event.data.kind)) return;
  const kind = event.data.kind;
  const durationMs = Math.min(Math.max(Number(event.data.durationMs) || 8000, 1000), MAX_DURATION_MS);
  const end = performance.now() + durationMs;
  stopped = false;

  if (kind === "ram") {
    const requested = Math.min(Math.max(Number(event.data.memoryMiB) || 16, 8), 64);
    const byteCount = Math.min(requested * 1024 * 1024, MAX_MEMORY_BYTES);
    try {
      memory = new Uint8Array(byteCount);
      for (let i = 0; i < byteCount; i += 4096) memory[i] = 1;
      self.postMessage({ type: "allocated", allocatedMiB: byteCount / 1048576 });
    } catch (error) {
      memory = null;
      self.postMessage({ type: "error", message: "Not enough memory for the bounded check." });
      return;
    }
    setTimeout(() => { memory = null; self.postMessage({ type: "complete" }); }, durationMs);
    return;
  }

  const maxWorkMsPerCycle = 15;
  const cycle = () => {
    if (stopped || performance.now() >= end) {
      self.postMessage({ type: "complete" });
      return;
    }
    const begin = performance.now();
    let result = 1.2357;
    while (performance.now() - begin < maxWorkMsPerCycle && performance.now() < end) {
      result = Math.sqrt(result + 0.1876);
    }
    self.postMessage({ type: "heartbeat", checksum: result });
    setTimeout(cycle, 100);
  };
  cycle();
};
