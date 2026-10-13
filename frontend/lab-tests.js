// AEGIS lab engines. Explicitly bounded tests, single active test at a time.
// These checks measure this computer through the browser and the local agent;
// they are not certified stress tests of hardware at maximum rated load.
(() => {
  "use strict";

  function abortError() { return new DOMException("Aborted", "AbortError"); }

  function startWorker(task, settings, onMessage) {
    const worker = new Worker("frontend/lab-worker.js");
    task.workers.push(worker);
    worker.onmessage = event => { if (!task.controller.signal.aborted) onMessage(event.data || {}); };
    worker.postMessage({ type: "start", ...settings });
    return worker;
  }

  function cpuTest(task, settings, report) {
    const threads = Math.max(1, Math.min(navigator.hardwareConcurrency || 4, Number(settings.cpuThreads) || 1));
    return new Promise((resolve, reject) => {
      task.controller.signal.addEventListener("abort", () => reject(abortError()), { once: true });
      const iterations = new Array(threads).fill(0);
      const results = [];
      for (let i = 0; i < threads; i++) {
        const worker = startWorker(task, { ...settings, kind: "cpu" }, data => {
          if (data.type === "progress") {
            iterations[i] = data.value || 0;
            const total = iterations.reduce((a, b) => a + b, 0);
            report("Потоков: " + threads + " · вычислений: " + total.toLocaleString("ru-RU"));
          } else if (data.type === "complete") {
            results.push(data.metrics || {});
            if (results.length === threads) {
              const total = results.reduce((a, m) => a + (m.iterations || 0), 0);
              const seconds = Math.max(...results.map(m => m.durationSeconds || 0.001));
              resolve({ threads, dutyPercent: results[0].dutyPercent, iterations: total, iterationsPerSecond: total / seconds, durationSeconds: seconds });
            }
          } else if (data.type === "error") reject(new Error(data.message || "Ошибка вычислительного потока"));
        });
        worker.onerror = () => reject(new Error("Вычислительный поток завершился с ошибкой."));
      }
    });
  }

  function ramTest(task, settings, report) {
    return new Promise((resolve, reject) => {
      task.controller.signal.addEventListener("abort", () => reject(abortError()), { once: true });
      const worker = startWorker(task, { ...settings, kind: "ram" }, data => {
        if (data.type === "progress") report(data.detail || "Выделение памяти…");
        else if (data.type === "complete") resolve(data.metrics || {});
        else if (data.type === "error") reject(new Error(data.message || "Ошибка выделения памяти"));
      });
      worker.onerror = () => reject(new Error("Поток проверки памяти завершился с ошибкой."));
    });
  }

  function compileShader(gl, kind, source) {
    const shader = gl.createShader(kind);
    if (!shader) throw new Error("Невозможно создать шейдер");
    gl.shaderSource(shader, source);
    gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
      const error = gl.getShaderInfoLog(shader);
      gl.deleteShader(shader);
      throw new Error("WebGL2: " + String(error).slice(0, 200));
    }
    return shader;
  }

  function rendererInfo(gl) {
    const ext = gl.getExtension("WEBGL_debug_renderer_info");
    const renderer = String(ext ? gl.getParameter(ext.UNMASKED_RENDERER_WEBGL) : gl.getParameter(gl.RENDERER));
    const software = /swiftshader|llvmpipe|software|basic render|microsoft basic/i.test(renderer);
    return { renderer, software };
  }

  async function gpuTest(task, settings, report) {
    const canvas = document.querySelector("#labGpuCanvas");
    canvas.hidden = false;
    const gl = canvas.getContext("webgl2", { antialias: false, depth: false, stencil: false, preserveDrawingBuffer: false });
    if (!gl) {
      canvas.hidden = true;
      throw new Error("WebGL2 недоступен. Включи аппаратное ускорение браузера или 3D-ускорение VM.");
    }
    const info = rendererInfo(gl);
    const loops = Math.max(8, Math.min(256, Number(settings.gpuIterations) || 64));
    const vertexText = "#version 300 es\nvoid main(){vec2 p[3]=vec2[3](vec2(-1.,-1.),vec2(3.,-1.),vec2(-1.,3.));gl_Position=vec4(p[gl_VertexID],0.,1.);}";
    const fragmentText = "#version 300 es\nprecision highp float;uniform float uTime;uniform vec2 uSize;out vec4 color;\n" +
      "void main(){vec2 p=(gl_FragCoord.xy*2.-uSize)/uSize.y;float a=0.;vec2 z=p;\n" +
      "for(int i=0;i<" + loops + ";i++){float n=float(i);z=vec2(z.x*z.x-z.y*z.y,2.*z.x*z.y)*.5+p+vec2(sin(uTime*.3+n*.01),cos(uTime*.2))*.3;a+=exp(-dot(z,z));}\n" +
      "float g=clamp(a/" + loops + ".*3.,0.,1.);color=vec4(vec3(g),1.);}";
    let vertex = null, fragment = null, program = null, vao = null, frame = 0;
    const cleanup = () => {
      if (frame) cancelAnimationFrame(frame);
      if (vao) gl.deleteVertexArray(vao);
      if (program) gl.deleteProgram(program);
      if (vertex) gl.deleteShader(vertex);
      if (fragment) gl.deleteShader(fragment);
      canvas.hidden = true;
    };
    try {
      vertex = compileShader(gl, gl.VERTEX_SHADER, vertexText);
      fragment = compileShader(gl, gl.FRAGMENT_SHADER, fragmentText);
      program = gl.createProgram();
      gl.attachShader(program, vertex);
      gl.attachShader(program, fragment);
      gl.linkProgram(program);
      if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error("WebGL2: " + gl.getProgramInfoLog(program));
      vao = gl.createVertexArray();
      gl.bindVertexArray(vao);
      gl.useProgram(program);
      const uTime = gl.getUniformLocation(program, "uTime");
      const uSize = gl.getUniformLocation(program, "uSize");
      const started = performance.now();
      task.cleanup = cleanup;
      report("Рендер: " + info.renderer + (info.software ? " — ПРОГРАММНЫЙ (CPU), не видеокарта" : ""));
      return await new Promise((resolve, reject) => {
        task.controller.signal.addEventListener("abort", () => reject(abortError()), { once: true });
        let frames = 0, lastReport = started;
        function tick(now) {
          if (task.controller.signal.aborted) return;
          const seconds = (now - started) / 1000;
          if (seconds * 1000 >= settings.durationMs) {
            gl.finish();
            resolve({ frames, fps: frames / Math.max(0.001, seconds), durationSeconds: seconds,
              resolution: canvas.width + "x" + canvas.height, iterations: loops,
              renderer: info.renderer, softwareRenderer: info.software });
            return;
          }
          gl.viewport(0, 0, canvas.width, canvas.height);
          gl.uniform1f(uTime, seconds);
          gl.uniform2f(uSize, canvas.width, canvas.height);
          for (let pass = 0; pass < 4; pass++) gl.drawArrays(gl.TRIANGLES, 0, 3);
          frames++;
          if (now - lastReport > 1000) {
            lastReport = now;
            report((frames / seconds).toFixed(1) + " FPS · " + info.renderer + (info.software ? " (программный)" : ""));
          }
          frame = requestAnimationFrame(tick);
        }
        frame = requestAnimationFrame(tick);
      });
    } catch (error) {
      if (!task.cleanup) cleanup();
      throw error;
    }
  }

  async function diskTest(task, settings, report) {
    report("Запись и проверка " + settings.sizeMiB + " МиБ во временном файле…");
    const response = await fetch("/api/v1/lab/disk", {
      method: "POST", cache: "no-store", signal: task.controller.signal,
      headers: { "Content-Type": "application/json", "X-AEGIS-Lab-Intent": "bounded-resource-check" },
      body: JSON.stringify({ sizeMiB: settings.sizeMiB })
    });
    const body = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(body.detail || "Disk test HTTP " + response.status);
    return body;
  }

  async function networkTest(task, settings, report) {
    const started = performance.now();
    const deadline = started + settings.durationMs;
    let bytes = 0, requests = 0;
    while (performance.now() < deadline) {
      task.controller.signal.throwIfAborted();
      const response = await fetch("/api/v1/lab/loopback-sample?sizeKiB=1024", { cache: "no-store", signal: task.controller.signal });
      if (!response.ok) throw new Error("Локальный агент вернул HTTP " + response.status);
      const buffer = await response.arrayBuffer();
      if (buffer.byteLength !== 1048576) throw new Error("Неожиданный размер тестового пакета");
      bytes += buffer.byteLength;
      requests++;
      if (requests % 8 === 0) {
        const s = (performance.now() - started) / 1000;
        report("Loopback: " + (bytes / 1048576).toFixed(0) + " МиБ · " + (bytes / 1048576 / s).toFixed(0) + " МиБ/с");
      }
    }
    const seconds = Math.max(0.001, (performance.now() - started) / 1000);
    return { totalMiB: bytes / 1048576, mibps: bytes / 1048576 / seconds, requests, seconds, endpoint: "127.0.0.1 (loopback)" };
  }

  window.AegisLabTests = { cpu: cpuTest, ram: ramTest, gpu: gpuTest, disk: diskTest, network: networkTest };
})();
