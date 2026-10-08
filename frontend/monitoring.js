// Live telemetry, processes, network investigation and Agent diagnostics.
const AGENT_HTTP="http://127.0.0.1:8765";
const AGENT_WS="ws://127.0.0.1:8765/ws/monitor";
let agentSocket=null;
let reconnectTimer=null;

function setAgentState(online,text){
  const el=q("#agentState");
  setService("serviceAgentRow","serviceAgentStatus",online?"online":"offline",online?"ok":"down");
  if(!el)return;
  el.classList.toggle("offline",!online);
  el.classList.toggle("online",online);
  el.innerHTML="<i></i> "+(text||(online?"AGENT ONLINE":"AGENT OFFLINE"));
  const side=q("#sidebarAgentHealth");if(side){side.innerHTML="<i></i> "+(online?"Агент подключён · данные LIVE":"Агент не подключён") ;side.classList.toggle("is-offline",!online)}
}

function formatDuration(seconds){
  const total=Math.max(0,Math.floor(Number(seconds)||0));
  const days=Math.floor(total/86400);
  const hours=Math.floor((total%86400)/3600);
  const minutes=Math.floor((total%3600)/60);
  const secs=total%60;
  if(days)return days+"d "+hours+"h "+minutes+"m";
  if(hours)return hours+"h "+minutes+"m";
  if(minutes)return minutes+"m "+secs+"s";
  return secs+"s";
}
function fmtPercent(v){
  return numeric(v)!==null?Number(v).toFixed(1)+"%":"—";
}
function fmtTemp(v){
  return numeric(v)!==null?Number(v).toFixed(1)+" °C":"—";
}
function formatFileSize(v){
  const n=Number(v);
  if(!Number.isFinite(n))return "—";
  if(n>=1024*1024*1024)return (n/1024/1024/1024).toFixed(2)+" GB";
  if(n>=1024*1024)return (n/1024/1024).toFixed(1)+" MB";
  if(n>=1024)return (n/1024).toFixed(1)+" KB";
  return Math.round(n)+" B";
}
function fmtBytes(v){
  const n=Number(v);
  if(!Number.isFinite(n))return "—";
  if(n>=1024*1024)return (n/1024/1024).toFixed(1)+" MB/s";
  if(n>=1024)return (n/1024).toFixed(1)+" KB/s";
  return Math.round(n)+" B/s";
}
function clampPercent(v){
  const n=Number(v);
  return Number.isFinite(n)?Math.max(0,Math.min(100,n)):0;
}
function setBar(id,value){
  const el=q(id);
  if(el)el.style.width=clampPercent(value)+"%";
}
function sensorRows(items,valueFormatter,limit=8){
  if(!items||!items.length)return '<p class="muted">Датчик не предоставил данные.</p>';
  return items.slice(0,limit).map(item=>'<div class="sensor-row"><div><b>'+escapeHtml(item.name||item.device||"Sensor")+'</b><span>'+escapeHtml(item.device||item.hardwareType||"")+'</span></div><strong>'+valueFormatter(item)+'</strong></div>').join("");
}
function escapeHtml(value){
  return String(value??"").replace(/[&<>"']/g,ch=>({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#039;"}[ch]));
}

function renderTelemetry(payload){
  const system=payload?.system;
  if(!system)return;
  lastTelemetryPayload=payload;
  // Visual LEDs mirror actual Windows telemetry; no simulated resource usage.
  for(const [ledId,valueId,current,suffix,warn,critical] of [
    ["signalCpu","signalCpuValue",numeric(system.cpuLoadPercent),"%",65,85],
    ["signalRam","signalRamValue",numeric(system.memory?.loadPercent),"%",75,90],
    ["signalTemp","signalTempValue",numeric(system.cpuTemperatureC),"°C",75,88]
  ]){
    const led=q("#"+ledId),value=q("#"+valueId);
    if(!led||!value)continue;
    led.classList.toggle("warm",current!==null&&current>=warn);
    led.classList.toggle("critical",current!==null&&current>=critical);
    value.textContent=current===null?"Нет датчика":current.toFixed(1)+suffix;
  }
  pushHistory(system);
  updateOverview(payload);

  q("#machineName").textContent=system.machineName||"Windows PC";
  q("#machineMeta").textContent=(system.os||"Windows")+" • "+(system.logicalProcessors||"?")+" logical CPU";

  q("#cpuLoad").textContent=fmtPercent(system.cpuLoadPercent);
  q("#cpuTemp").textContent="Температура: "+fmtTemp(system.cpuTemperatureC);
  setBar("#cpuBar",system.cpuLoadPercent);

  q("#ramLoad").textContent=fmtPercent(system.memory?.loadPercent);
  q("#ramMeta").textContent=Number.isFinite(Number(system.memory?.usedGb))
    ?system.memory.usedGb.toFixed(1)+" / "+system.memory.totalGb.toFixed(1)+" GB"
    :"—";
  setBar("#ramBar",system.memory?.loadPercent);

  const gpu=system.gpus?.[0];
  q("#gpuLoad").textContent=gpu?fmtPercent(gpu.loadPercent):"—";
  q("#gpuMeta").textContent=gpu
    ?(gpu.name+" • "+fmtTemp(gpu.temperatureC))
    :"GPU sensor unavailable";
  setBar("#gpuBar",gpu?.loadPercent);

  const down=system.network?.totalReceiveBytesPerSecond;
  const up=system.network?.totalSendBytesPerSecond;
  q("#networkRate").textContent=fmtBytes((Number(down)||0)+(Number(up)||0));
  q("#networkDown").textContent="↓ "+fmtBytes(down);
  q("#networkUp").textContent="↑ "+fmtBytes(up);

  q("#temperatureList").innerHTML=sensorRows(system.temperatures,x=>fmtTemp(x.celsius),10);
  q("#fanList").innerHTML=sensorRows(system.fans,x=>Number.isFinite(Number(x.rpm))?Math.round(x.rpm)+" RPM":"—",8);
  q("#diskList").innerHTML=system.disks?.length
    ?system.disks.map(d=>'<div class="sensor-row"><div><b>'+escapeHtml(d.name)+'</b><span>'+escapeHtml(d.fileSystem||"Disk")+'</span></div><strong>'+d.usedPercent.toFixed(1)+'%</strong></div>').join("")
    :'<p class="muted">Диски не найдены.</p>';

  renderProcesses(payload.processes||[]);
}

function renderProcesses(processes){
  lastProcesses=Array.isArray(processes)?processes:lastProcesses;
  const search=(q("#processSearch")?.value||"").trim().toLowerCase();
  const visible=search?lastProcesses.filter(p=>
    String(p.pid).includes(search)||
    String(p.name||"").toLowerCase().includes(search)||
    String(p.path||"").toLowerCase().includes(search)
  ):lastProcesses;

  q("#processCount").textContent=visible.length+" / "+lastProcesses.length+" процессов";
  q("#processRows").innerHTML=visible.length?visible.map(p=>
    '<button class="process-row process-click" data-pid="'+p.pid+'"><span>'+p.pid+'</span><span class="process-name" title="'+escapeHtml(p.path||"")+'">'+escapeHtml(p.name)+'</span><span>'+fmtPercent(p.cpuPercent)+'</span><span>'+Number(p.memoryMb).toFixed(1)+' MB</span><span>'+(p.threads??"—")+'</span></button>'
  ).join(""):'<div class="empty-process">Процессы не найдены.</div>';
  qa(".process-click").forEach(row=>row.addEventListener("click",()=>openProcessDetails(Number(row.dataset.pid))));
}
q("#processSearch")?.addEventListener("input",()=>renderProcesses(lastProcesses));
function renderNetworkConnections(){
  const box=q("#networkConnectionRows");if(!box)return;
  const search=(q("#connectionSearch")?.value||"").trim().toLowerCase();
  const visible=search?lastConnections.filter(item=>
    [item.localAddress,item.localPort,item.remoteAddress,item.remotePort,item.state,item.processId,item.processName]
      .some(value=>String(value??"").toLowerCase().includes(search))
  ):lastConnections;

  box.innerHTML=visible.length?visible.map(item=>
    '<div class="connection-row"><span>'+escapeHtml(item.localAddress+":"+item.localPort)+'</span><span>'+escapeHtml(item.remoteAddress+":"+item.remotePort)+'</span><b>'+escapeHtml(item.state)+'</b><span class="connection-process">'+escapeHtml(item.processName?item.processName+" • PID "+item.processId:(item.processId?"PID "+item.processId:"—"))+'</span></div>'
  ).join(""):'<div class="empty-process">Соединения не найдены.</div>';
}
async function loadNetworkConnections(){
  const box=q("#networkConnectionRows");if(!box)return;
  try{
    lastConnections=await apiJson("/api/v1/network/connections?limit=100");
    renderNetworkConnections();
  }catch(error){
    box.innerHTML='<div class="empty-process">Connections API: '+escapeHtml(error.message)+'</div>';
  }
}
q("#refreshConnections")?.addEventListener("click",loadNetworkConnections);
q("#connectionSearch")?.addEventListener("input",renderNetworkConnections);

async function openProcessDetails(pid){
  openModal("processModal");q("#processDetailBody").innerHTML='<p class="muted">Загрузка…</p>';
  try{
    const p=await apiJson("/api/v1/processes/"+pid);
    q("#processModalTitle").textContent=(p.name||"Process")+" • PID "+p.pid;
    const rows=[
      ["Path",p.path||"Недоступен"],
      ["Started",p.startedAt?new Date(p.startedAt).toLocaleString("ru-RU"):"Недоступно"],
      ["Working set",Number(p.memoryMb||0).toFixed(1)+" MB"],
      ["Private memory",Number(p.privateMemoryMb||0).toFixed(1)+" MB"],
      ["Threads",p.threads??"—"],
      ["Handles",p.handles??"—"],
      ["Priority",p.priority||"—"],
      ["Responding",p.responding==null?"—":(p.responding?"Да":"Нет")],
      ["File size",p.fileSizeBytes==null?"—":formatFileSize(p.fileSizeBytes)],
      ["File version",p.fileVersion||"—"],
      ["Product",p.productName||"—"],
      ["Company",p.companyName||"—"],
      ["SHA-256",p.sha256||"Недоступен"],
      ["Embedded signature",p.embeddedSignaturePresent==null?"Не удалось проверить":(p.embeddedSignaturePresent?"Есть":"Нет")],
      ["Signer",p.signerSubject||"—"],
      ["Signer thumbprint",p.signerThumbprint||"—"],
      ["Signer validity",p.signerNotBefore&&p.signerNotAfter?(new Date(p.signerNotBefore).toLocaleDateString("ru-RU")+" — "+new Date(p.signerNotAfter).toLocaleDateString("ru-RU")):"—"]
    ];
    q("#processDetailBody").innerHTML='<div class="process-detail-grid">'+rows.map(x=>'<div><span>'+escapeHtml(x[0])+'</span><b>'+escapeHtml(x[1])+'</b></div>').join("")+'</div><button class="secondary process-create-incident" id="processIncidentButton">Создать инцидент по процессу</button>';
    q("#processIncidentButton").onclick=()=>{
      q("#incidentTitleInput").value="Проверка процесса "+(p.name||pid);
      q("#incidentSourceInput").value=(p.name||"process")+" (PID "+p.pid+")";
      q("#incidentDescriptionInput").value="Path: "+(p.path||"недоступен")+"\nRAM: "+Number(p.memoryMb||0).toFixed(1)+" MB\nSHA-256: "+(p.sha256||"недоступен")+"\nSigner: "+(p.signerSubject||"нет/неизвестен");
      closeModal("processModal");openModal("incidentModal");
    };
  }catch(error){q("#processDetailBody").innerHTML='<p class="muted">Не удалось получить процесс: '+escapeHtml(error.message)+'</p>'}
}

async function loadAgentDiagnostics(){
  if(!q("#agentDiagnosticsGrid"))return;
  try{
    const d=await apiJson("/api/v1/diagnostics");
    const agent=d.agent||d;
    const persistence=d.persistence||{};
    q("#diagMode").textContent=agent.runningAsWindowsService?"WINDOWS SERVICE":"CONSOLE";
    q("#diagVersion").textContent=agent.version||"—";
    q("#diagUptime").textContent=formatDuration(agent.uptimeSeconds);
    q("#diagPid").textContent=String(agent.processId??"—");
    q("#diagWorkingSet").textContent=Number.isFinite(Number(agent.workingSetMb))?Number(agent.workingSetMb).toFixed(1)+" MB":"—";
    q("#diagGcMemory").textContent=(Number.isFinite(Number(agent.managedMemoryMb))?Number(agent.managedMemoryMb).toFixed(1):"—")+" / "+(Number.isFinite(Number(agent.gcHeapSizeMb))?Number(agent.gcHeapSizeMb).toFixed(1):"—")+" MB";
    q("#diagThreads").textContent=(agent.threadCount??"—")+" / "+(agent.handleCount??"—");
    q("#diagSampleAge").textContent=agent.lastSampleAgeSeconds==null?"—":Number(agent.lastSampleAgeSeconds).toFixed(1)+"s";
    q("#diagHistory").textContent=String(agent.historyPoints??"—");
    q("#diagDataDir").textContent=(agent.dataDirectory||"—")+(persistence.provider?" • "+persistence.provider+" r"+(persistence.revision??0):"");
    const meta=q("#machineMeta");
    if(meta&&lastTelemetryPayload?.system){
      meta.textContent=(lastTelemetryPayload.system.os||"Windows")+" • "+(lastTelemetryPayload.system.logicalProcessors||"?")+" logical CPU • Agent "+(agent.version||"dev");
    }
  }catch(error){
    console.warn("Diagnostics unavailable",error);
  }
}

async function probeAgent(){
  try{
    const response=await fetch(AGENT_HTTP+"/api/v1/health",{cache:"no-store"});
    if(!response.ok)throw new Error("HTTP "+response.status);
    const health=await response.json();
    setAgentState(true,"AGENT ONLINE");markAgentOnlineForAlerts();
    loadTelemetryHistory();
    loadAgentDiagnostics();
    return health;
  }catch{
    setAgentState(false,"AGENT OFFLINE");
    return null;
  }
}

function connectAgent(){
  clearTimeout(reconnectTimer);
  if(agentSocket){
    try{agentSocket.close()}catch{}
    agentSocket=null;
  }

  probeAgent();
  try{
    const ws=new WebSocket(AGENT_WS);
    agentSocket=ws;

    ws.onopen=()=>{setAgentState(true,"AGENT ONLINE");markAgentOnlineForAlerts();loadNetworkConnections();loadAgentDiagnostics()};
    ws.onmessage=event=>{
      try{handleTelemetryMessage(JSON.parse(event.data))}catch(error){console.error("AEGIS telemetry parse error",error)}
    };
    ws.onerror=()=>{setAgentState(false,"AGENT ERROR");scheduleAgentOfflineAlert()};
    ws.onclose=()=>{
      if(agentSocket===ws)agentSocket=null;
      setAgentState(false,"AGENT OFFLINE");scheduleAgentOfflineAlert();
      reconnectTimer=setTimeout(connectAgent,3000);
    };
  }catch{
    setAgentState(false,"AGENT OFFLINE");
    reconnectTimer=setTimeout(connectAgent,3000);
  }
}

q("#reconnectAgent")?.addEventListener("click",()=>{toast("Переподключение к AEGIS Agent…");connectAgent()});