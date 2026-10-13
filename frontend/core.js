// AEGIS frontend core: state, overview, API client, reports and read-only Windows views.
const state={current:"",incidents:[]};
const titles={overview:"Обзор инфраструктуры",incidents:"Управление инцидентами",assets:"Активы предприятия",access:"Контроль доступа",cameras:"Видеонаблюдение",reports:"Отчёты и аналитика",monitoring:"Мониторинг компьютера",lab:"Лаборатория испытаний",audit:"Журнал аудита",events:"События Windows",services:"Службы Windows"};
const q=s=>document.querySelector(s),qa=s=>[...document.querySelectorAll(s)];
let lastProcesses=[];
let lastConnections=[];
let lastAuditItems=[];
let lastWindowsEvents=[];
let lastWindowsServices=[];

let lastTelemetryPayload=null;
let telemetryHistory=[];
let incidentStoreOnline=false;
let aetherOnline=false;
let historyLoaded=false;
let historyWindowSeconds=900;
let historyRequestId=0;
let lastHistoryRefreshAt=0;

function numeric(value){
  if(value===null||value===undefined||value==="")return null;
  const n=Number(value);
  return Number.isFinite(n)?n:null;
}
function openIncidents(){
  return state.incidents.filter(i=>i.status!=="Закрыт");
}
function riskScore(system){
  let score=0;
  for(const incident of openIncidents()){
    const severity=String(incident.severity||"").toLowerCase();
    score+=severity.includes("крит")?18:severity.includes("выс")?10:severity.includes("сред")?5:2;
  }
  score=Math.min(score,45);

  const cpu=numeric(system?.cpuLoadPercent);
  const ram=numeric(system?.memory?.loadPercent);
  const cpuTemp=numeric(system?.cpuTemperatureC);
  const gpuTemp=numeric(system?.gpus?.[0]?.temperatureC);
  const maxDisk=Math.max(0,...(system?.disks||[]).map(d=>numeric(d.usedPercent)||0));

  if(cpu!==null&&cpu>70)score+=Math.min(15,(cpu-70)*0.5);
  if(ram!==null&&ram>80)score+=Math.min(10,(ram-80)*0.5);
  if(cpuTemp!==null&&cpuTemp>75)score+=Math.min(20,cpuTemp-75);
  if(gpuTemp!==null&&gpuTemp>75)score+=Math.min(15,(gpuTemp-75)*0.75);
  if(maxDisk>90)score+=Math.min(10,maxDisk-90);
  return Math.round(Math.max(0,Math.min(100,score)));
}
function riskLabel(score){
  if(score>=80)return ["критический","critical"];
  if(score>=55)return ["высокий","high"];
  if(score>=30)return ["средний","medium"];
  return ["низкий","low"];
}
function setService(rowId,statusId,text,level="ok"){
  const row=q("#"+rowId),status=q("#"+statusId);
  if(status)status.textContent=text;
  if(row){
    row.classList.toggle("warn",level==="warn");
    row.classList.toggle("down",level==="down");
  }
}
function renderOverviewIncidents(){
  const box=q("#overviewIncidentRows");if(!box)return;
  const incidents=state.incidents.slice(0,4);
  box.innerHTML=incidents.length?incidents.map(i=>{
    const sev=String(i.severity||"").toLowerCase();
    const sevClass=sev.includes("крит")?"critical":sev.includes("выс")||sev.includes("сред")?"medium":"low";
    const tagClass=sev.includes("крит")?"danger":i.status==="Закрыт"?"":"work";
    return '<button class="row overview-incident" data-id="'+escapeHtml(i.id)+'"><span>'+escapeHtml(i.id)+'</span><span><i class="sev '+sevClass+'"></i>'+escapeHtml(i.title)+'</span><span>'+escapeHtml(i.source)+'</span><span>'+escapeHtml(i.time||"")+'</span><span class="pill '+tagClass+'">'+escapeHtml(i.status)+'</span></button>';
  }).join(""):'<div class="empty-overview">Инцидентов пока нет.</div>';
  qa(".overview-incident").forEach(row=>row.addEventListener("click",()=>{state.current=row.dataset.id;view("incidents")}));
}
function updateOverview(payload=lastTelemetryPayload){
  const system=payload?.system;
  const open=openIncidents();
  const critical=open.filter(i=>String(i.severity||"").toLowerCase().includes("крит")).length;

  const openMetric=q("#openIncidentMetric");if(openMetric)openMetric.textContent=String(open.length);
  const criticalMetric=q("#criticalIncidentMetric");if(criticalMetric)criticalMetric.textContent=critical+" крит.";
  const incidentMeta=q("#incidentMetricMeta");if(incidentMeta)incidentMeta.textContent=incidentStoreOnline?"persistent store online":"локальный fallback";
  renderOverviewIncidents();

  if(!system)return;
  const score=riskScore(system),[label]=riskLabel(score);
  if(q("#riskValue"))q("#riskValue").textContent=String(score);
  if(q("#riskLabel"))q("#riskLabel").textContent=label;
  if(q("#riskBar"))q("#riskBar").style.width=score+"%";
  if(q("#systemOverallStatus"))q("#systemOverallStatus").textContent=score>=80?"Критично":score>=55?"Внимание":"Стабильно";

  const sources=(system.temperatures?.length||0)+(system.fans?.length||0)+(system.gpus?.length||0)+(system.disks?.length||0)+(system.network?.adapters?.length||0);
  if(q("#telemetrySourceMetric"))q("#telemetrySourceMetric").textContent=String(sources);
  if(q("#telemetryHealthMetric"))q("#telemetryHealthMetric").textContent=sources?"online":"limited";
  const processCount=payload?.processes?.length||0;
  if(q("#processMetric"))q("#processMetric").textContent=String(processCount);
  if(q("#processHealthMetric"))q("#processHealthMetric").textContent=processCount?"live":"—";

  setService("serviceHardwareRow","serviceHardwareStatus",sources?String(sources)+" sources":"unavailable",sources?"ok":"warn");
  setService("serviceProcessRow","serviceProcessStatus",processCount?String(processCount)+" sampled":"empty",processCount?"ok":"warn");
  const adapters=system.network?.adapters?.length||0;
  setService("serviceNetworkRow","serviceNetworkStatus",adapters?String(adapters)+" adapters":"unavailable",adapters?"ok":"warn");
  setService("serviceStoreRow","serviceStoreStatus",incidentStoreOnline?"online":"fallback",incidentStoreOnline?"ok":"warn");
  setService("serviceAetherRow","serviceAetherStatus",aetherOnline?"online":"offline",aetherOnline?"ok":"down");

  renderInventory(system);
}
function renderInventory(system){
  if(!system)return;
  if(q("#assetInventoryStatus"))q("#assetInventoryStatus").textContent="LIVE";
  if(q("#assetMachine"))q("#assetMachine").textContent=system.machineName||"—";
  if(q("#assetCpu"))q("#assetCpu").textContent=String(system.logicalProcessors??"—");
  if(q("#assetGpu"))q("#assetGpu").textContent=String(system.gpus?.length||0);
  if(q("#assetDisk"))q("#assetDisk").textContent=String(system.disks?.length||0);
  if(q("#assetNetwork"))q("#assetNetwork").textContent=String(system.network?.adapters?.length||0);
  if(q("#assetSensors"))q("#assetSensors").textContent=String((system.temperatures?.length||0)+(system.fans?.length||0));
  const details=q("#inventoryDetails");
  if(details){
    const gpu=(system.gpus||[]).map(x=>'<div><b>'+escapeHtml(x.name)+'</b><span>GPU • '+fmtTemp(x.temperatureC)+'</span></div>');
    const disks=(system.disks||[]).map(x=>'<div><b>'+escapeHtml(x.name)+'</b><span>'+escapeHtml(x.fileSystem)+' • '+x.usedPercent.toFixed(1)+'% used</span></div>');
    const net=(system.network?.adapters||[]).map(x=>'<div><b>'+escapeHtml(x.name)+'</b><span>'+escapeHtml(x.type)+' • '+fmtBytes(x.receiveBytesPerSecond+x.sendBytesPerSecond)+'</span></div>');
    details.innerHTML=[...gpu,...disks,...net].join("")||'<p class="muted">Дополнительных устройств нет.</p>';
  }
}
function historyPointFromSystem(system){
  return {
    timestamp:system.timestamp||new Date().toISOString(),
    cpuLoadPercent:numeric(system.cpuLoadPercent),
    memoryLoadPercent:numeric(system.memory?.loadPercent),
    gpuLoadPercent:numeric(system.gpus?.[0]?.loadPercent),
    receiveBytesPerSecond:numeric(system.network?.totalReceiveBytesPerSecond),
    sendBytesPerSecond:numeric(system.network?.totalSendBytesPerSecond)
  };
}
// Full-resolution buffer of the last 10 minutes for live charts (monitoring, lab).
let liveSamples=[];
const LIVE_BUFFER_MS=600000;
function chartPoint(p){
  return {
    t:new Date(p.timestamp).getTime(),
    cpu:numeric(p.cpuLoadPercent),
    ram:numeric(p.memoryLoadPercent),
    gpu:numeric(p.gpuLoadPercent),
    rx:numeric(p.receiveBytesPerSecond),
    tx:numeric(p.sendBytesPerSecond)
  };
}
const percentSeries=[
  {key:"cpu",label:"CPU",cls:"s-cpu"},
  {key:"ram",label:"RAM",cls:"s-ram"},
  {key:"gpu",label:"GPU",cls:"s-gpu"}
];
const networkSeries=[
  {key:"rx",label:"Приём ↓",cls:"s-rx"},
  {key:"tx",label:"Передача ↑",cls:"s-tx"}
];
const charts={};
function chartFor(id,options){
  if(charts[id])return charts[id];
  const host=q("#"+id);
  if(!host||!window.AegisCharts)return null;
  charts[id]=AegisCharts.create(host,options);
  return charts[id];
}
function pushHistory(system){
  if(!system)return;
  const next=historyPointFromSystem(system);
  const nextTime=new Date(next.timestamp).getTime();
  if(!Number.isFinite(nextTime))return;

  const point=chartPoint(next);
  if(!liveSamples.length||point.t>liveSamples.at(-1).t)liveSamples.push(point);
  const liveCutoff=Date.now()-LIVE_BUFFER_MS;
  while(liveSamples.length&&liveSamples[0].t<liveCutoff)liveSamples.shift();

  const previous=telemetryHistory.at(-1);
  const previousTime=previous?new Date(previous.timestamp).getTime():0;
  if(nextTime>previousTime){
    if(historyWindowSeconds>900&&nextTime-previousTime<5000&&telemetryHistory.length){
      telemetryHistory[telemetryHistory.length-1]=next;
    }else telemetryHistory.push(next);
  }
  const cutoff=Date.now()-historyWindowSeconds*1000;
  telemetryHistory=telemetryHistory.filter(p=>new Date(p.timestamp).getTime()>=cutoff).slice(-2500);
  renderHistoryChart();
  renderLiveCharts();
  if(historyWindowSeconds>900&&Date.now()-lastHistoryRefreshAt>=300000){
    lastHistoryRefreshAt=Date.now();
    loadTelemetryHistory(true);
  }
}
function renderHistoryChart(){
  if(!q("#overview")?.classList.contains("active"))return;
  const now=Date.now(),from=now-historyWindowSeconds*1000;
  const points=telemetryHistory.map(chartPoint).filter(p=>Number.isFinite(p.t));
  // Downsampled history is sparser: allow gaps proportional to the window.
  const gapMs=Math.max(15000,historyWindowSeconds*1000/600*3);
  chartFor("historyChart",{series:percentSeries,height:230,ariaLabel:"История нагрузки CPU, RAM, GPU",emptyText:"История ещё не накоплена или агент не подключён."})?.render(points,{from,to:now,gapMs});
  chartFor("historyNetChart",{series:networkSeries,mode:"bytes",height:150,area:false,ariaLabel:"История сетевого трафика",emptyText:"Нет данных о сети."})?.render(points,{from,to:now,gapMs});
}
let liveWindowSeconds=120;
function renderLiveCharts(){
  if(!q("#monitoring")?.classList.contains("active"))return;
  const now=Date.now(),from=now-liveWindowSeconds*1000;
  chartFor("liveLoadChart",{series:percentSeries,height:230,ariaLabel:"Живая нагрузка CPU, RAM, GPU"})?.render(liveSamples,{from,to:now});
  chartFor("liveNetChart",{series:networkSeries,mode:"bytes",height:160,area:false,ariaLabel:"Живой сетевой трафик"})?.render(liveSamples,{from,to:now});
}
async function loadTelemetryHistory(force=false){
  if(historyLoaded&&!force)return;
  const requestId=++historyRequestId;
  const requestedSeconds=historyWindowSeconds;
  try{
    const points=await apiJson("/api/v1/history?seconds="+requestedSeconds+"&maxPoints=600");
    if(requestId!==historyRequestId||requestedSeconds!==historyWindowSeconds)return;
    const current=telemetryHistory.at(-1);
    telemetryHistory=Array.isArray(points)?points:[];
    if(current){
      const currentTime=new Date(current.timestamp).getTime();
      if(currentTime>new Date(telemetryHistory.at(-1)?.timestamp||0).getTime()){
        telemetryHistory.push(current);
      }
    }
    if(liveSamples.length<10&&requestedSeconds<=900){
      const cutoff=Date.now()-LIVE_BUFFER_MS;
      const seeded=telemetryHistory.map(chartPoint).filter(p=>Number.isFinite(p.t)&&p.t>=cutoff);
      const firstLive=liveSamples[0]?.t??Infinity;
      liveSamples=[...seeded.filter(p=>p.t<firstLive),...liveSamples];
    }
    historyLoaded=true;
    lastHistoryRefreshAt=Date.now();
    renderHistoryChart();
    renderLiveCharts();
  }catch(error){
    if(requestId===historyRequestId)console.warn("History unavailable",error);
  }
}
q("#historyRange")?.addEventListener("change",event=>{
  const requested=Number(event.target.value);
  if(![900,3600,21600,86400].includes(requested))return;
  historyWindowSeconds=requested;
  historyLoaded=false;
  historyRequestId++;
  telemetryHistory=[];
  renderHistoryChart();
  loadTelemetryHistory(true);
});
q("#liveRange")?.addEventListener("change",event=>{
  const requested=Number(event.target.value);
  if(![60,120,300,600].includes(requested))return;
  liveWindowSeconds=requested;
  renderLiveCharts();
});

async function apiJson(path,options={}){
  const response=await fetch(path,{cache:"no-store",...options,headers:{"Content-Type":"application/json",...(options.headers||{})}});
  if(!response.ok){
    let detail="HTTP "+response.status;
    try{const body=await response.json();detail=body.detail||detail}catch{}
    throw new Error(detail);
  }
  if(response.status===204)return null;
  return response.json();
}
function normalizeIncidentForApi(i){
  return {
    id:i.id,
    title:i.title||"",
    source:i.source||"",
    severity:i.severity||"Средний",
    status:i.status||"Новый",
    description:i.description||"",
    events:Array.isArray(i.events)?i.events:[],
    auto:!!i.auto,
    aetherSent:!!i.aetherSent,
    ruleKey:i.ruleKey||null,
    createdAt:i.createdAt||new Date().toISOString()
  };
}
async function saveIncidentApi(i){
  try{
    await apiJson("/api/v1/incidents/"+encodeURIComponent(i.id),{
      method:"PUT",
      body:JSON.stringify(normalizeIncidentForApi(i))
    });
  }catch(error){console.warn("Incident persistence failed",error)}
}
async function loadPersistentIncidents(){
  try{
    const stored=await apiJson("/api/v1/incidents");
    if(Array.isArray(stored)&&stored.length){
      state.incidents=stored.map(i=>({...i,time:new Date(i.createdAt).toLocaleTimeString("ru-RU",{hour:"2-digit",minute:"2-digit"})}));
      if(!state.incidents.some(i=>i.id===state.current))state.current=state.incidents[0]?.id||"";
    }else{
      for(const i of state.incidents){
        if(!i.createdAt)i.createdAt=new Date().toISOString();
        await saveIncidentApi(i);
      }
    }
    incidentStoreOnline=true;
    renderIncidents();updateAutoQueueState();updateOverview();
  }catch(error){
    incidentStoreOnline=false;updateOverview();

    console.warn("Persistent incidents unavailable; local fallback remains",error);
  }
}
async function appendAudit(type,subject,detail,source="AEGIS UI"){
  try{
    await apiJson("/api/v1/audit",{method:"POST",body:JSON.stringify({
      timestamp:new Date().toISOString(),type,subject,detail,source
    })});
  }catch(error){console.warn("Audit write failed",error)}
}
function renderAuditItems(){
  const box=q("#auditList");if(!box)return;
  const search=(q("#auditSearch")?.value||"").trim().toLowerCase();
  const visible=search?lastAuditItems.filter(item=>
    [item.type,item.subject,item.detail,item.source,item.timestamp]
      .some(value=>String(value??"").toLowerCase().includes(search))
  ):lastAuditItems;

  box.innerHTML=visible.length?visible.map(x=>{
    const dt=new Date(x.timestamp);
    return '<div class="audit-row"><time>'+escapeHtml(dt.toLocaleString("ru-RU"))+'</time><b>'+escapeHtml(x.type)+'</b><span>'+escapeHtml(x.subject||"—")+'</span><p>'+escapeHtml(x.detail||"")+'</p><em>'+escapeHtml(x.source||"")+'</em></div>';
  }).join(""):'<p class="muted">Audit-записи не найдены.</p>';
}
async function loadAudit(){
  const box=q("#auditList");if(!box)return;
  box.innerHTML='<p class="muted">Загрузка…</p>';
  try{
    lastAuditItems=await apiJson("/api/v1/audit?limit=300");
    renderAuditItems();
  }catch(error){box.innerHTML='<p class="muted">Audit API недоступен: '+escapeHtml(error.message)+'</p>'}
}
q("#auditSearch")?.addEventListener("input",renderAuditItems);
function renderWindowsEvents(){
  const box=q("#windowsEventList");if(!box)return;
  const search=(q("#eventSearch")?.value||"").trim().toLowerCase();
  const visible=search?lastWindowsEvents.filter(item=>
    [item.eventId,item.levelName,item.provider,item.message,item.machineName]
      .some(value=>String(value??"").toLowerCase().includes(search))
  ):lastWindowsEvents;

  box.innerHTML=visible.length?visible.map(item=>{
    const originalIndex=lastWindowsEvents.indexOf(item);
    const time=item.timeCreated?new Date(item.timeCreated).toLocaleString("ru-RU"):"—";
    return '<button class="windows-event-row" data-event-index="'+originalIndex+'"><div><b>'+escapeHtml(item.levelName||("Level "+(item.level??"—")))+'</b><span>'+escapeHtml(time)+'</span></div><strong>'+escapeHtml((item.provider||"Unknown")+" • Event "+item.eventId)+'</strong><p>'+escapeHtml(item.message||"Описание события недоступно.")+'</p></button>';
  }).join(""):'<p class="muted">События не найдены.</p>';

  qa(".windows-event-row").forEach(row=>row.addEventListener("click",()=>{
    const item=lastWindowsEvents[Number(row.dataset.eventIndex)];
    const log=q("#eventLogSelect")?.value||"System";
    q("#incidentTitleInput").value="Windows Event "+item.eventId+" • "+(item.provider||log);
    q("#incidentSourceInput").value=log+" Event Log";
    q("#incidentSeverityInput").value=(String(item.levelName||"").toLowerCase().includes("critical")||String(item.levelName||"").toLowerCase().includes("крит"))?"Критический":"Средний";
    q("#incidentDescriptionInput").value=(item.message||"Описание недоступно.")+"\nProvider: "+(item.provider||"—")+"\nEvent ID: "+item.eventId;
    openModal("incidentModal");
  }));
}
async function loadWindowsEvents(){
  const box=q("#windowsEventList");if(!box)return;
  const log=q("#eventLogSelect")?.value||"System";
  box.innerHTML='<p class="muted">Загрузка '+escapeHtml(log)+'…</p>';
  try{
    lastWindowsEvents=await apiJson("/api/v1/windows/events?log="+encodeURIComponent(log)+"&limit=150");
    renderWindowsEvents();
  }catch(error){
    box.innerHTML='<p class="muted">Event Log API: '+escapeHtml(error.message)+'</p>';
  }
}
q("#refreshWindowsEvents")?.addEventListener("click",loadWindowsEvents);
q("#eventLogSelect")?.addEventListener("change",loadWindowsEvents);
q("#eventSearch")?.addEventListener("input",renderWindowsEvents);

function renderWindowsServices(){
  const box=q("#windowsServiceList");if(!box)return;
  const search=(q("#serviceSearch")?.value||"").trim().toLowerCase();
  const status=q("#serviceStatusFilter")?.value||"";
  const visible=lastWindowsServices.filter(item=>{
    if(status&&item.status!==status)return false;
    if(!search)return true;
    return [item.name,item.displayName,item.status,item.serviceType]
      .some(value=>String(value??"").toLowerCase().includes(search));
  });

  box.innerHTML=visible.length?visible.map((item,index)=>{
    const originalIndex=lastWindowsServices.indexOf(item);
    return '<button class="windows-service-row" data-service-index="'+originalIndex+'"><div><b>'+escapeHtml(item.displayName||item.name)+'</b><span class="service-status '+escapeHtml(String(item.status||"").toLowerCase())+'">'+escapeHtml(item.status||"Unknown")+'</span></div><strong>'+escapeHtml(item.name)+'</strong><p>'+escapeHtml(item.serviceType||"Service")+(item.canStop===true?" • can stop":"")+'</p></button>';
  }).join(""):'<p class="muted">Службы не найдены.</p>';

  qa(".windows-service-row").forEach(row=>row.addEventListener("click",()=>{
    const item=lastWindowsServices[Number(row.dataset.serviceIndex)];
    q("#incidentTitleInput").value="Проверка службы "+(item.displayName||item.name);
    q("#incidentSourceInput").value="Windows Service: "+item.name;
    q("#incidentDescriptionInput").value="Status: "+(item.status||"Unknown")+"\nService type: "+(item.serviceType||"—")+"\nCan stop: "+(item.canStop===true?"yes":"no/unknown");
    openModal("incidentModal");
  }));
}
async function loadWindowsServices(){
  const box=q("#windowsServiceList");if(!box)return;
  box.innerHTML='<p class="muted">Загрузка служб…</p>';
  try{
    lastWindowsServices=await apiJson("/api/v1/windows/services?limit=1000");
    renderWindowsServices();
  }catch(error){
    box.innerHTML='<p class="muted">Services API: '+escapeHtml(error.message)+'</p>';
  }
}
q("#refreshWindowsServices")?.addEventListener("click",loadWindowsServices);
q("#serviceSearch")?.addEventListener("input",renderWindowsServices);
q("#serviceStatusFilter")?.addEventListener("change",renderWindowsServices);

async function loadReportSummary(){
  const box=q("#reportSummary");if(!box)return;
  box.innerHTML='<p class="muted">Формирование…</p>';
  try{
    const data=await apiJson("/api/v1/reports/current.json");
    const s=data.system||{},g=(s.gpus||[])[0];
    box.innerHTML='<div class="report-grid">'+
      '<div><span>Компьютер</span><b>'+escapeHtml(data.agent?.machine||s.machineName||"—")+'</b></div>'+
      '<div><span>CPU</span><b>'+fmtPercent(s.cpuLoadPercent)+' / '+fmtTemp(s.cpuTemperatureC)+'</b></div>'+
      '<div><span>RAM</span><b>'+fmtPercent(s.memory?.loadPercent)+'</b></div>'+
      '<div><span>GPU</span><b>'+(g?fmtPercent(g.loadPercent)+" / "+fmtTemp(g.temperatureC):"—")+'</b></div>'+
      '<div><span>Процессы</span><b>'+Number(data.processes?.length||0)+'</b></div>'+
      '<div><span>Инциденты</span><b>'+Number(data.incidents?.length||0)+'</b></div>'+
      '<div><span>Audit events</span><b>'+Number(data.audit?.length||0)+'</b></div>'+
      '<div><span>Сформирован</span><b>'+escapeHtml(new Date(data.generatedAt).toLocaleString("ru-RU"))+'</b></div>'+
      '</div>';
  }catch(error){box.innerHTML='<p class="muted">Report API недоступен: '+escapeHtml(error.message)+'</p>'}
}
function openModal(id){const el=q("#"+id);if(el){el.classList.add("show");el.setAttribute("aria-hidden","false")}}
function closeModal(id){const el=q("#"+id);if(el){el.classList.remove("show");el.setAttribute("aria-hidden","true")}}
qa("[data-close-modal]").forEach(b=>b.addEventListener("click",()=>closeModal(b.dataset.closeModal)));
q("#refreshAudit")?.addEventListener("click",loadAudit);
q("#refreshReport")?.addEventListener("click",loadReportSummary);

function toast(t){const e=q("#toast");e.textContent=t;e.classList.add("show");clearTimeout(window.tt);window.tt=setTimeout(()=>e.classList.remove("show"),1800)}

// Main shell navigation and integrated secure chat shortcut.
const pageSummaries={
  overview:"Контроль компонентов, процессов и событий Windows в реальном времени.",
  monitoring:"Живые показания CPU, RAM, GPU, сети, дисков, датчиков и процессов этого компьютера.",
  lab:"Пять реальных тестов CPU, RAM, GPU, диска и сети с живым графиком; демонстрация синего экрана.",
  incidents:"Расследования, приоритеты, статусы и отправка через AETHER.",
  assets:"Обнаруженное оборудование этого Windows-компьютера.",
  access:"Подключение СКУД. Источники данных не симулируются.",
  cameras:"Интеграция видеосистем по API или SDK производителя.",
  events:"Системный журнал Windows и события безопасности.",
  services:"Состояние установленных служб Windows.",
  audit:"Хронология действий оператора и автоматических правил.",
  reports:"Экспорт данных мониторинга и расследований."
};
q("#headerChat")?.addEventListener("click",()=>{
  const chat=q("#aetherPanel");
  if(!chat)return;
  chat.classList.remove("is-collapsed");
  const toggle=q("#aetherCollapseToggle");
  if(toggle){toggle.textContent="−";toggle.setAttribute("aria-expanded","true")}
  chat.scrollIntoView({behavior:"smooth",block:"nearest"});
  q("#aetherSetupToggle")?.focus();
});

function view(id){qa(".view").forEach(v=>v.classList.toggle("active",v.id===id));qa(".nav").forEach(n=>n.classList.toggle("active",n.dataset.view===id));q("#pageTitle").textContent=titles[id]||"AEGIS SOC";if(q("#pageDescription"))q("#pageDescription").textContent=pageSummaries[id]||"";if(id==="incidents")renderIncidents();if(id==="monitoring"&&!agentSocket)connectAgent();if(id==="monitoring")renderLiveCharts();if(id==="overview")renderHistoryChart();if(id==="audit")loadAudit();if(id==="reports")loadReportSummary();if(id==="events")loadWindowsEvents();if(id==="services")loadWindowsServices()}
qa(".nav").forEach(b=>b.onclick=()=>view(b.dataset.view));