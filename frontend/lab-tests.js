// AEGIS lab engines. Explicitly bounded tests, single active test at a time.
// These checks measure this VM/browser, not all hardware at maximum rated load.
(() => {
  "use strict";

  function workerTest(task, settings, report) {
    return new Promise((resolve, reject) => {
      const worker = new Worker("frontend/lab-worker.js");
      task.worker = worker;
      task.controller.signal.addEventListener("abort", () => {
        reject(new DOMException("Aborted", "AbortError"));
      }, { once: true });
      worker.onmessage = event => {
        if (task.controller.signal.aborted) return;
        const data = event.data || {};
        if (data.type === "progress") report(data.detail || "Выполняется…");
        else if (data.type === "complete") resolve(data.metrics || {});
        else if (data.type === "error") reject(new Error(data.message || "Ошибка Worker"));
      };
      worker.onerror = () => reject(new Error("Вычислительный поток завершился с ошибкой."));
      worker.postMessage({ type: "start", ...settings });
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

  async function gpuTest(task, settings) {
    const canvas = document.querySelector("#labGpuCanvas");
    canvas.hidden = false;
    const gl = canvas.getContext("webgl2", {
      antialias: false, depth: false, stencil: false, preserveDrawingBuffer: false
    });
    if (!gl) {
      canvas.hidden = true;
      throw new Error("WebGL2 недоступен. В виртуальной машине включи 3D-ускорение.");
    }

    const vertexText = '#version 300 es\nvoid main(){\n' +
      'vec2 pos[3] = vec2[3](vec2(-1.,-1.),vec2(3.,-1.),vec2(-1.,3.));\n' +
      'gl_Position=vec4(pos[gl_VertexID],0.,1.);\n}';
    const fragmentText = '#version 300 es\nprecision highp float;\n' +
      'uniform float uTime;\nout vec4 color;\nvoid main(){\n' +
      'vec2 p=gl_FragCoord.xy/vec2(512.,320.);\nfloat x=0.;\n' +
      'for(int i=0;i<12;i++){\nfloat n=float(i);\n' +
      'x+=sin(p.x*11.+uTime*.7+n*.36)*cos(p.y*13.-uTime*.5-n*.27);\n}\n' +
      'color=vec4(.28+.22*sin(x),.35+.25*cos(x),.55+.25*sin(x*.7),1.);\n}';
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
      if (!program) throw new Error("WebGL2: программа не создана");
      gl.attachShader(program, vertex);
      gl.attachShader(program, fragment);
      gl.linkProgram(program);
      if (!gl.getProgramParameter(program, gl.LINK_STATUS))
        throw new Error("WebGL2: " + gl.getProgramInfoLog(program));
      vao = gl.createVertexArray();
      gl.bindVertexArray(vao);
      gl.useProgram(program);
      const uTime = gl.getUniformLocation(program, "uTime");
      const started = performance.now();
      task.cleanup = cleanup;

      return await new Promise((resolve, reject) => {
        task.controller.signal.addEventListener("abort", () =>
          reject(new DOMException("Aborted", "AbortError")), { once: true });
        let frames = 0;
        function tick(now) {
          if (task.controller.signal.aborted) return;
          const seconds = (now - started) / 1000;
          if (seconds * 1000 >= settings.durationMs) {
            resolve({ frames, fps: frames / Math.max(0.001, seconds),
              durationSeconds: seconds, resolution: "512x320", renderer: "WebGL2" });
            return;
          }
          gl.viewport(0, 0, canvas.width, canvas.height);
          gl.uniform1f(uTime, seconds);
          gl.drawArrays(gl.TRIANGLES, 0, 3);
          frames++;
          frame = requestAnimationFrame(tick);
        }
        frame = requestAnimationFrame(tick);
      });
    } catch (error) {
      if (!task.cleanup) cleanup();
      throw error;
    }
  }

  async function diskTest(task, settings) {
    const response = await fetch("/api/v1/lab/disk", {
      method: "POST",
      cache: "no-store",
      signal: task.controller.signal,
      headers: {
        "Content-Type": "application/json",
        "X-AEGIS-Lab-Intent": "bounded-resource-check"
      },
      body: JSON.stringify({ sizeMiB: settings.sizeMiB })
    });
    const body = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(body.detail || "Disk test HTTP " + response.status);
    return body;
  }

  async function networkTest(task, settings, report) {
    const started = performance.now();
    let bytes = 0;
    for (let i = 0; i < 48; i++) {
      task.controller.signal.throwIfAborted();
      const response = await fetch("/api/v1/lab/loopback-sample", {
        cache: "no-store", signal: task.controller.signal
      });
      if (!response.ok) throw new Error("Локальный агент вернул HTTP " + response.status);
      const buffer = await response.arrayBuffer();
      if (buffer.byteLength !== 65536) throw new Error("Неожиданный размер тестового пакета");
      bytes += buffer.byteLength;
      if (i % 12 === 0) report("Loopback: " + (bytes / 1048576).toFixed(1) + " МиБ получено");
    }
    const seconds = Math.max(0.001, (performance.now() - started) / 1000);
    return {
      totalMiB: bytes / 1048576,
      mibps: bytes / 1048576 / seconds,
      requests: 48,
      seconds,
      endpoint: "127.0.0.1 (loopback)"
    };
  }

  window.AegisLabTests = {
    cpu: (task, settings, report) => workerTest(task, { ...settings, kind: "cpu" }, report),
    ram: (task, settings, report) => workerTest(task, { ...settings, kind: "ram" }, report),
    gpu: gpuTest,
    disk: diskTest,
    network: networkTest
  };
})();
