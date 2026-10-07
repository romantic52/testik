const state={current:"",incidents:[]};
const titles={overview:"Обзор инфраструктуры",incidents:"Управление инцидентами",assets:"Активы предприятия",access:"Контроль доступа",cameras:"Видеонаблюдение",reports:"Отчёты и аналитика",monitoring:"Мониторинг компьютера",audit:"Журнал аудита",events:"События Windows",services:"Службы Windows"};
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

function numeric(value){
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
    gpuLoadPercent:numeric(system.gpus?.[0]?.loadPercent)
  };
}
function pushHistory(system){
  if(!system)return;
  telemetryHistory.push(historyPointFromSystem(system));
  const cutoff=Date.now()-15*60*1000;
  telemetryHistory=telemetryHistory.filter(p=>new Date(p.timestamp).getTime()>=cutoff);
  renderHistoryChart();
}
function pathForHistory(key){
  const now=Date.now(),start=now-15*60*1000;
  const points=telemetryHistory.map(p=>({t:new Date(p.timestamp).getTime(),v:numeric(p[key])}))
    .filter(p=>Number.isFinite(p.t)&&p.v!==null&&p.t>=start);
  if(!points.length)return "";
  return points.map((p,index)=>{
    const x=20+Math.max(0,Math.min(1,(p.t-start)/(now-start)))*720;
    const y=185-Math.max(0,Math.min(100,p.v))/100*150;
    return (index?"L":"M")+x.toFixed(1)+" "+y.toFixed(1);
  }).join(" ");
}
function renderHistoryChart(){
  const cpu=pathForHistory("cpuLoadPercent");
  const ram=pathForHistory("memoryLoadPercent");
  const gpu=pathForHistory("gpuLoadPercent");
  if(q("#cpuHistoryLine"))q("#cpuHistoryLine").setAttribute("d",cpu);
  if(q("#ramHistoryLine"))q("#ramHistoryLine").setAttribute("d",ram);
  if(q("#gpuHistoryLine"))q("#gpuHistoryLine").setAttribute("d",gpu);
  if(q("#cpuHistoryArea")){
    if(cpu){
      const pts=cpu.match(/(?:M|L)([0-9.]+) ([0-9.]+)/g)||[];
      const first=pts[0]?.match(/([0-9.]+) ([0-9.]+)/),last=pts.at(-1)?.match(/([0-9.]+) ([0-9.]+)/);
      q("#cpuHistoryArea").setAttribute("d",first&&last?cpu+" L"+last[1]+" 210 L"+first[1]+" 210 Z":"");
    }else q("#cpuHistoryArea").setAttribute("d","");
  }
}
async function loadTelemetryHistory(){
  if(historyLoaded)return;
  try{
    const points=await apiJson("/api/history?seconds=900");
    telemetryHistory=Array.isArray(points)?points:[];
    historyLoaded=true;renderHistoryChart();
  }catch(error){console.warn("History unavailable",error)}
}


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
    await apiJson("/api/incidents/"+encodeURIComponent(i.id),{
      method:"PUT",
      body:JSON.stringify(normalizeIncidentForApi(i))
    });
  }catch(error){console.warn("Incident persistence failed",error)}
}
async function loadPersistentIncidents(){
  try{
    const stored=await apiJson("/api/incidents");
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
    await apiJson("/api/audit",{method:"POST",body:JSON.stringify({
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
    lastAuditItems=await apiJson("/api/audit?limit=300");
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
    lastWindowsEvents=await apiJson("/api/windows/events?log="+encodeURIComponent(log)+"&limit=150");
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
    lastWindowsServices=await apiJson("/api/windows/services?limit=1000");
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
    const data=await apiJson("/api/reports/current.json");
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
function view(id){qa(".view").forEach(v=>v.classList.toggle("active",v.id===id));qa(".nav").forEach(n=>n.classList.toggle("active",n.dataset.view===id));q("#pageTitle").textContent=titles[id]||"AEGIS SOC";if(id==="incidents")renderIncidents();if(id==="monitoring"&&!agentSocket)connectAgent();if(id==="audit")loadAudit();if(id==="reports")loadReportSummary();if(id==="events")loadWindowsEvents();if(id==="services")loadWindowsServices()}
qa(".nav").forEach(b=>b.onclick=()=>view(b.dataset.view));
q("#incidentSeverityFilter")?.addEventListener("change",renderIncidents);
q("#incidentStatusFilter")?.addEventListener("change",renderIncidents);
q("#incidentSearch")?.addEventListener("input",renderIncidents);qa("[data-go]").forEach(b=>b.onclick=()=>view(b.dataset.go));
function renderIncidents(){
  const badge=q("#incidentBadge");if(badge)badge.textContent=String(state.incidents.filter(i=>i.status!=="Закрыт").length);
  const list=q("#incidentList");if(!list)return;

  const search=(q("#incidentSearch")?.value||"").trim().toLowerCase();
  const status=q("#incidentStatusFilter")?.value||"";
  const severity=q("#incidentSeverityFilter")?.value||"";
  const visible=state.incidents.filter(i=>{
    if(status&&i.status!==status)return false;
    if(severity&&i.severity!==severity)return false;
    if(!search)return true;
    return [i.id,i.title,i.source,i.description,i.status,i.severity]
      .some(value=>String(value||"").toLowerCase().includes(search));
  });

  list.innerHTML=visible.length?visible.map(i=>`<div class="incident-item ${i.id===state.current?"active":""}" data-id="${escapeHtml(i.id)}"><div><span>${escapeHtml(i.id)}</span><span>${escapeHtml(i.time||"")}</span></div><h3>${escapeHtml(i.title)}</h3><p>${escapeHtml(i.source)} • ${escapeHtml(i.severity)} • ${escapeHtml(i.status)}</p></div>`).join(""):'<div class="empty-process">Ничего не найдено.</div>';
  qa(".incident-item").forEach(x=>x.onclick=()=>{state.current=x.dataset.id;renderIncidents()});

  const i=state.incidents.find(x=>x.id===state.current)||visible[0]||state.incidents[0];
  const detail=q("#incidentDetail");if(!i){if(detail)detail.innerHTML='<p class="muted">Инцидентов нет.</p>';updateOverview();return}
  detail.innerHTML=`<h3 class="detail-title">${escapeHtml(i.title)}</h3><div class="detail-meta"><span>${escapeHtml(i.id)}</span><span>${escapeHtml(i.source)}</span><span>${escapeHtml(i.severity)}</span><span>${escapeHtml(i.status)}</span></div><p class="muted">${escapeHtml(i.description)}</p><div class="timeline">${(i.events||[]).map(e=>{const p=String(e).split(" — ");return `<div class="event"><b>${escapeHtml(p[0]||"")}</b><p>${escapeHtml(p.slice(1).join(" — "))}</p></div>`}).join("")}</div><div class="incident-actions"><button class="primary" id="shareIncident">Отправить в AETHER.chat</button>${i.status!=="Закрыт"?'<button class="secondary" id="closeIncident">Закрыть</button>':""}<button class="danger-mini" id="deleteIncident">Удалить</button></div>`;
  q("#shareIncident").onclick=()=>shareIncident(i);
  q("#closeIncident")?.addEventListener("click",async()=>{
    i.status="Закрыт";i.events=[...(i.events||[]),alertClock()+" — инцидент закрыт оператором"];
    await saveIncidentApi(i);await appendAudit("incident.closed",i.id,i.title);
    renderIncidents();toast("Инцидент закрыт");
  });
  updateOverview();
  q("#deleteIncident").onclick=async()=>{
    try{await fetch("/api/incidents/"+encodeURIComponent(i.id),{method:"DELETE"});}catch{}
    state.incidents=state.incidents.filter(x=>x.id!==i.id);state.current=state.incidents[0]?.id||"";
    persistAutoIncidents();renderIncidents();toast("Инцидент удалён");
  };
}
function addMessage(text,type="outgoing",author="Вы",time=null){
  const box=q("#messages"),d=document.createElement("div");
  d.className="message "+type;
  const stamp=time||new Date().toLocaleTimeString("ru-RU",{hour:"2-digit",minute:"2-digit"});
  d.innerHTML='<div><b></b><span></span></div><p></p>';
  d.querySelector("b").textContent=author;
  d.querySelector("span").textContent=stamp;
  d.querySelector("p").textContent=text;
  box.appendChild(d);box.scrollTop=box.scrollHeight;
}
async function shareIncident(i){
  try{
    await window.aether.shareIncident(i);
    i.aetherSent=true;persistAutoIncidents();updateAutoQueueState();saveIncidentApi(i);appendAudit("incident.aether_sent",i.id,i.title);
    addMessage(i.id+" • "+i.title+" • "+i.source);
    toast("Инцидент отправлен в AETHER.chat");
  }catch(error){toast(error.message||"AETHER: ошибка отправки")}
}
qa(".incident-jump").forEach(r=>r.onclick=()=>{state.current=r.dataset.id;view("incidents")});qa("[data-open]").forEach(b=>b.onclick=()=>{state.current=b.dataset.open;view("incidents")});
q("#chatForm").onsubmit=async e=>{
  e.preventDefault();const input=q("#chatInput"),text=input.value.trim();if(!text)return;
  try{
    await window.aether.sendMessage({text});
    addMessage(text);input.value="";
  }catch(error){toast(error.message||"AETHER: ошибка отправки")}
};
qa("[data-text]").forEach(b=>b.onclick=()=>{q("#chatInput").value=b.dataset.text;q("#chatInput").focus()});

const aetherConfig=q("#aetherConfig"),aetherTotpField=q("#aetherTotpField");
const rememberedAether=JSON.parse(localStorage.getItem("aegis_aether_ui")||"{}");
q("#aetherServer").value=rememberedAether.server||"";
q("#aetherUser").value=rememberedAether.user||"";
q("#aetherPeer").value=rememberedAether.peer||"";

q("#aetherSetupToggle").onclick=()=>aetherConfig.classList.toggle("open");
q("#aetherDisconnect").onclick=async()=>{
  await window.aether.disconnect();
  q("#aetherPassword").value="";q("#aetherTotp").value="";
  aetherConfig.classList.add("open");toast("AETHER отключён");
};

aetherConfig.onsubmit=async e=>{
  e.preventDefault();
  const server=q("#aetherServer").value.trim();
  const user=q("#aetherUser").value.trim();
  const peer=q("#aetherPeer").value.trim();
  const password=q("#aetherPassword").value;
  const totp=q("#aetherTotp").value.trim();
  q("#aetherConnect").disabled=true;
  q("#aetherLoginStatus").textContent="Подключение и инициализация Double Ratchet…";
  try{
    const result=await window.aether.connect({serverUrl:server,userId:user,password,totpCode:totp,peerId:peer});
    localStorage.setItem("aegis_aether_ui",JSON.stringify({server,user,peer}));
    q("#aetherLoginStatus").textContent="Подключено как @"+result.userId+" • device "+result.deviceId;
    q("#aetherPassword").value="";q("#aetherTotp").value="";
    aetherTotpField.classList.remove("show");
    aetherConfig.classList.remove("open");
    const box=q("#messages");box.innerHTML="";
    addMessage("Защищённый SOC-канал подключён.","incoming","AEGIS");
  }catch(error){
    if(error.code==="TOTP_REQUIRED"||error.code==="TOTP_INVALID"){
      aetherTotpField.classList.add("show");q("#aetherTotp").focus();
    }
    q("#aetherLoginStatus").textContent=error.message||"Ошибка AETHER";
    toast(error.message||"Ошибка подключения AETHER");
  }finally{q("#aetherConnect").disabled=false}
};

window.addEventListener("aether-state",event=>{
  const d=event.detail||{},online=!!d.connected;
  aetherOnline=online;updateOverview();
  q("#aetherRelayState").classList.toggle("relay-offline",!online);
  q("#aetherRelayState").classList.toggle("relay-online",online);
  q("#aetherRelayState").innerHTML="<i></i> "+(online?"Relay connected":"Relay offline");
  q("#aetherSecureTitle").textContent=online?"Double Ratchet активен":"AETHER не подключён";
  q("#aetherSecureText").textContent=online
    ?("SOC device "+d.deviceId+" • relay видит только ciphertext")
    :"Войди в аккаунт и выбери peer. Plaintext не передаётся relay.";
  q("#aetherUserLabel").textContent=online?("@"+d.userId):"offline";
  q("#aetherPeerLabel").childNodes[0].nodeValue=online&&d.peerId?("@"+d.peerId+" "):"SOC secure channel ";
  q("#aetherE2eBadge").textContent=online?"E2E ✓":"E2E";
  if(online)flushAutoShares();
});

window.addEventListener("aether-message",event=>{
  const d=event.detail||{};
  let time=null;
  if(d.createdAt){const date=new Date(d.createdAt);if(!Number.isNaN(date.getTime()))time=date.toLocaleTimeString("ru-RU",{hour:"2-digit",minute:"2-digit"});}
  addMessage(d.text||"","incoming","@"+(d.senderId||"unknown"),time);
});
window.addEventListener("aether-error",event=>{
  const message=event.detail?.message;if(message)toast("AETHER: "+message);
});

q("#newIncident").onclick=()=>openModal("incidentModal");
q("#incidentForm")?.addEventListener("submit",async e=>{
  e.preventDefault();
  const incident={
    id:"INC-"+Date.now().toString(36).toUpperCase(),
    title:q("#incidentTitleInput").value.trim(),
    source:q("#incidentSourceInput").value.trim(),
    severity:q("#incidentSeverityInput").value,
    status:"Новый",
    description:q("#incidentDescriptionInput").value.trim(),
    events:[alertClock()+" — создан вручную оператором"],
    auto:false,aetherSent:false,ruleKey:null,
    createdAt:new Date().toISOString(),time:alertClock()
  };
  state.incidents.unshift(incident);state.current=incident.id;
  await saveIncidentApi(incident);await appendAudit("incident.manual",incident.id,incident.title);
  e.target.reset();closeModal("incidentModal");renderIncidents();toast("Инцидент создан");
});
q("#notify").onclick=()=>toast(state.incidents.filter(i=>i.status!=="Закрыт").length+" активных инцидентов");



const ALERT_DEFAULTS={
  enabled:true,
  autoAether:true,
  cpuLoad:95,
  cpuTemp:90,
  gpuTemp:90,
  ram:95,
  processCpu:80,
  disk:95,
  sustainSeconds:15,
  cooldownMinutes:10
};
let alertSettings={...ALERT_DEFAULTS};
let autoShareBusy=false;
let agentEverOnline=false;
let agentOfflineTimer=null;
let lastIncidentRevision=null;

function alertClock(){
  return new Date().toLocaleTimeString("ru-RU",{hour:"2-digit",minute:"2-digit"});
}
function persistAutoIncidents(){
  // Persistence is owned by Aegis.Agent; kept as a compatibility no-op for older UI call sites.
}
function updateAutoQueueState(){
  const pending=state.incidents.filter(i=>i.auto&&!i.aetherSent).length;
  const qState=q("#autoQueueState");if(qState)qState.textContent=pending+" queued for AETHER";
}
function syncAutomationUi(){
  const map={
    autoAlertsEnabled:["enabled","checked"],
    autoAetherEnabled:["autoAether","checked"],
    ruleCpuLoad:["cpuLoad","value"],
    ruleCpuTemp:["cpuTemp","value"],
    ruleGpuTemp:["gpuTemp","value"],
    ruleRam:["ram","value"],
    ruleProcessCpu:["processCpu","value"],
    ruleDisk:["disk","value"],
    ruleSustain:["sustainSeconds","value"],
    ruleCooldown:["cooldownMinutes","value"]
  };
  for(const [id,[key,prop]] of Object.entries(map)){
    const el=q("#"+id);if(!el)continue;
    el[prop]=prop==="checked"?!!alertSettings[key]:alertSettings[key];
  }
  const stateEl=q("#autoEngineState");
  if(stateEl){
    stateEl.textContent=alertSettings.enabled?"ARMED / BACKEND":"PAUSED";
    stateEl.classList.toggle("auto-armed",alertSettings.enabled);
    stateEl.classList.toggle("auto-paused",!alertSettings.enabled);
  }
  updateAutoQueueState();
}
async function loadAlertSettings(){
  try{
    const value=await apiJson("/api/settings/alerts");
    alertSettings={...ALERT_DEFAULTS,...value};
    syncAutomationUi();
    if(alertSettings.autoAether)flushAutoShares();
  }catch(error){
    console.warn("Backend alert settings unavailable",error);
    syncAutomationUi();
  }
}
async function saveAlertSettings(){
  try{
    const saved=await apiJson("/api/settings/alerts",{
      method:"PUT",
      body:JSON.stringify(alertSettings)
    });
    alertSettings={...ALERT_DEFAULTS,...saved};
    syncAutomationUi();
  }catch(error){
    toast("Не удалось сохранить alert settings: "+error.message);
  }
}
function bindAutomationUi(){
  const bindings={
    autoAlertsEnabled:["enabled","checked"],
    autoAetherEnabled:["autoAether","checked"],
    ruleCpuLoad:["cpuLoad","number"],
    ruleCpuTemp:["cpuTemp","number"],
    ruleGpuTemp:["gpuTemp","number"],
    ruleRam:["ram","number"],
    ruleProcessCpu:["processCpu","number"],
    ruleDisk:["disk","number"],
    ruleSustain:["sustainSeconds","number"],
    ruleCooldown:["cooldownMinutes","number"]
  };
  for(const [id,[key,type]] of Object.entries(bindings)){
    const el=q("#"+id);if(!el)continue;
    el.addEventListener("change",async()=>{
      alertSettings[key]=type==="checked"?el.checked:Number(el.value);
      await saveAlertSettings();
      if(key==="autoAether"&&alertSettings.autoAether)flushAutoShares();
    });
  }
  syncAutomationUi();
  loadAlertSettings();
}
async function flushAutoShares(){
  if(autoShareBusy||!alertSettings.autoAether||!window.aether?.connected)return;
  autoShareBusy=true;
  try{
    const pending=state.incidents.filter(i=>i.auto&&!i.aetherSent).slice().reverse();
    for(const incident of pending){
      try{
        await window.aether.shareIncident(incident);
        incident.aetherSent=true;
        addMessage("AUTO • "+incident.id+" • "+incident.title);
        persistAutoIncidents();
        await saveIncidentApi(incident);
        await appendAudit("incident.aether_sent",incident.id,incident.title);
        updateAutoQueueState();
      }catch(error){
        console.warn("AEGIS auto AETHER send failed",incident.id,error);
        break;
      }
    }
  }finally{autoShareBusy=false}
}
function markAgentOnlineForAlerts(){
  agentEverOnline=true;
  if(agentOfflineTimer){clearTimeout(agentOfflineTimer);agentOfflineTimer=null}
}
function scheduleAgentOfflineAlert(){
  if(!agentEverOnline)return;
  if(agentOfflineTimer)clearTimeout(agentOfflineTimer);
  agentOfflineTimer=setTimeout(()=>{
    if(!agentSocket||agentSocket.readyState!==WebSocket.OPEN)
      toast("AEGIS Agent недоступен более 15 секунд");
  },15000);
}
function handleTelemetryMessage(payload){
  renderTelemetry(payload);
  const revision=Number(payload?.incidentRevision);
  if(Number.isFinite(revision)&&revision!==lastIncidentRevision){
    lastIncidentRevision=revision;
    loadPersistentIncidents().then(()=>{
      if(alertSettings.autoAether)flushAutoShares();
    });
  }
}

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
}

function fmtPercent(v){
  return Number.isFinite(Number(v))?Number(v).toFixed(1)+"%":"—";
}
function fmtTemp(v){
  return Number.isFinite(Number(v))?Number(v).toFixed(1)+" °C":"—";
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
    [item.localAddress,item.localPort,item.remoteAddress,item.remotePort,item.state]
      .some(value=>String(value??"").toLowerCase().includes(search))
  ):lastConnections;

  box.innerHTML=visible.length?visible.map(item=>
    '<div class="connection-row"><span>'+escapeHtml(item.localAddress+":"+item.localPort)+'</span><span>'+escapeHtml(item.remoteAddress+":"+item.remotePort)+'</span><b>'+escapeHtml(item.state)+'</b></div>'
  ).join(""):'<div class="empty-process">Соединения не найдены.</div>';
}
async function loadNetworkConnections(){
  const box=q("#networkConnectionRows");if(!box)return;
  try{
    lastConnections=await apiJson("/api/network/connections?limit=100");
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
    const p=await apiJson("/api/processes/"+pid);
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
      ["SHA-256",p.sha256||"Недоступен"]
    ];
    q("#processDetailBody").innerHTML='<div class="process-detail-grid">'+rows.map(x=>'<div><span>'+escapeHtml(x[0])+'</span><b>'+escapeHtml(x[1])+'</b></div>').join("")+'</div><button class="secondary process-create-incident" id="processIncidentButton">Создать инцидент по процессу</button>';
    q("#processIncidentButton").onclick=()=>{
      q("#incidentTitleInput").value="Проверка процесса "+(p.name||pid);
      q("#incidentSourceInput").value=(p.name||"process")+" (PID "+p.pid+")";
      q("#incidentDescriptionInput").value="Path: "+(p.path||"недоступен")+"\nRAM: "+Number(p.memoryMb||0).toFixed(1)+" MB";
      closeModal("processModal");openModal("incidentModal");
    };
  }catch(error){q("#processDetailBody").innerHTML='<p class="muted">Не удалось получить процесс: '+escapeHtml(error.message)+'</p>'}
}

async function probeAgent(){
  try{
    const response=await fetch(AGENT_HTTP+"/api/health",{cache:"no-store"});
    if(!response.ok)throw new Error("HTTP "+response.status);
    const health=await response.json();
    setAgentState(true,"AGENT ONLINE");markAgentOnlineForAlerts();
    loadTelemetryHistory();
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

    ws.onopen=()=>{setAgentState(true,"AGENT ONLINE");markAgentOnlineForAlerts();loadNetworkConnections()};
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

const crashConfirm=q("#crashConfirm"),bsod=q("#bsod"),fakeOff=q("#fakeOff");
q("#crashButton").onclick=()=>{crashConfirm.classList.add("show");crashConfirm.setAttribute("aria-hidden","false")};
q("#cancelCrash").onclick=()=>{crashConfirm.classList.remove("show");crashConfirm.setAttribute("aria-hidden","true")};
q("#confirmCrash").onclick=()=>startFakeCrash();
q("#restoreSystem").onclick=()=>{fakeOff.classList.remove("show");fakeOff.setAttribute("aria-hidden","true");toast("AEGIS demo system restored")};

async function startFakeCrash(){
  crashConfirm.classList.remove("show");
  try{await document.documentElement.requestFullscreen?.()}catch{}
  bsod.classList.add("show");bsod.setAttribute("aria-hidden","false");
  let progress=0;const p=q("#crashProgress");
  const timer=setInterval(()=>{
    progress+=Math.floor(Math.random()*13)+4;
    if(progress>=100){progress=100;clearInterval(timer);setTimeout(()=>{
      bsod.classList.remove("show");bsod.setAttribute("aria-hidden","true");
      fakeOff.classList.add("show");fakeOff.setAttribute("aria-hidden","false");
    },650)}
    p.textContent=progress;
  },260);
}

function tick(){q("#clock").textContent=new Date().toLocaleTimeString("ru-RU",{hour12:false})}tick();setInterval(tick,1000);setInterval(()=>{if(agentSocket?.readyState===WebSocket.OPEN)loadNetworkConnections()},5000);bindAutomationUi();renderIncidents();updateOverview();loadPersistentIncidents();connectAgent();