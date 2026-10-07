const state={current:"INC-2401",incidents:[
{id:"INC-2401",title:"Подозрительная авторизация",source:"VPN-GW-02",severity:"Критический",status:"Расследование",time:"02:13",description:"Успешная авторизация после серии отказов. Новый ASN и нетипичная география.",events:["01:58 — 4 отклонённых входа","02:07 — ещё 2 отклонённых входа","02:13 — успешная авторизация","02:14 — корреляция SIEM повысила риск"]},
{id:"INC-2398",title:"Аномальный исходящий трафик",source:"WS-FIN-14",severity:"Высокий",status:"В работе",time:"01:47",description:"Рабочая станция финансового отдела установила соединение с ранее не наблюдавшимся узлом.",events:["01:41 — EDR отметил новую сессию","01:47 — превышен сетевой baseline","01:52 — оператор начал проверку"]},
{id:"INC-2394",title:"Повторный отказ доступа",source:"Door B-17",severity:"Средний",status:"Проверка",time:"00:58",description:"Несколько попыток прохода по карте сотрудника вне разрешённой зоны.",events:["00:54 — первый отказ","00:56 — второй отказ","00:58 — создано событие СКУД"]}
]};
try{
  const savedAuto=JSON.parse(localStorage.getItem("aegis_auto_incidents")||"[]");
  if(Array.isArray(savedAuto))state.incidents=[...savedAuto,...state.incidents];
}catch{}
const titles={overview:"Обзор инфраструктуры",incidents:"Управление инцидентами",assets:"Активы предприятия",access:"Контроль доступа",cameras:"Видеонаблюдение",reports:"Отчёты и аналитика",monitoring:"Мониторинг компьютера"};
const q=s=>document.querySelector(s),qa=s=>[...document.querySelectorAll(s)];
function toast(t){const e=q("#toast");e.textContent=t;e.classList.add("show");clearTimeout(window.tt);window.tt=setTimeout(()=>e.classList.remove("show"),1800)}
function view(id){qa(".view").forEach(v=>v.classList.toggle("active",v.id===id));qa(".nav").forEach(n=>n.classList.toggle("active",n.dataset.view===id));q("#pageTitle").textContent=titles[id]||"AEGIS SOC";if(id==="incidents")renderIncidents();if(id==="monitoring"&&!agentSocket)connectAgent()}
qa(".nav").forEach(b=>b.onclick=()=>view(b.dataset.view));qa("[data-go]").forEach(b=>b.onclick=()=>view(b.dataset.go));
function renderIncidents(){
  const badge=q("#incidentBadge");if(badge)badge.textContent=String(state.incidents.length);
  const list=q("#incidentList");
  list.innerHTML=state.incidents.map(i=>`<div class="incident-item ${i.id===state.current?"active":""}" data-id="${escapeHtml(i.id)}"><div><span>${escapeHtml(i.id)}</span><span>${escapeHtml(i.time)}</span></div><h3>${escapeHtml(i.title)}</h3><p>${escapeHtml(i.source)} • ${escapeHtml(i.severity)} • ${escapeHtml(i.status)}</p></div>`).join("");
  qa(".incident-item").forEach(x=>x.onclick=()=>{state.current=x.dataset.id;renderIncidents()});
  const i=state.incidents.find(x=>x.id===state.current)||state.incidents[0];if(!i)return;
  q("#incidentDetail").innerHTML=`<h3 class="detail-title">${escapeHtml(i.title)}</h3><div class="detail-meta"><span>${escapeHtml(i.id)}</span><span>${escapeHtml(i.source)}</span><span>${escapeHtml(i.severity)}</span><span>${escapeHtml(i.status)}</span></div><p class="muted">${escapeHtml(i.description)}</p><div class="timeline">${(i.events||[]).map(e=>{const p=String(e).split(" — ");return `<div class="event"><b>${escapeHtml(p[0]||"")}</b><p>${escapeHtml(p.slice(1).join(" — "))}</p></div>`}).join("")}</div><button class="primary" id="shareIncident">Отправить в AETHER.chat</button>`;
  q("#shareIncident").onclick=()=>shareIncident(i);
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
    if(i.auto){i.aetherSent=true;persistAutoIncidents();updateAutoQueueState();}
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

q("#newIncident").onclick=()=>toast("Создание инцидента — demo UI");q("#report").onclick=()=>toast("Отчёт SOC сформирован");q("#notify").onclick=()=>toast("3 уведомления высокого приоритета");
const access=[["02:12","Серверная A-02","Разрешён • Иван П."],["01:58","Door B-17","Отказ • Карта #1842"],["01:35","Главный вход","Разрешён • Анна К."],["00:49","Архив C-04","Разрешён • Сервисная карта"]];
q("#accessLog").innerHTML=access.map(x=>`<div class="access-entry"><b>${x[0]}</b><span>${x[1]}</span><em>${x[2]}</em></div>`).join("");



const ALERT_DEFAULTS={
  enabled:true,
  autoAether:true,
  cpuLoad:95,
  cpuTemp:90,
  gpuTemp:90,
  ram:95,
  processCpu:80,
  disk:95,
  sustainSec:15,
  cooldownMin:10
};
let alertSettings={...ALERT_DEFAULTS};
try{alertSettings={...ALERT_DEFAULTS,...JSON.parse(localStorage.getItem("aegis_alert_settings")||"{}")}}catch{}
let alertLastTriggers={};
try{alertLastTriggers=JSON.parse(localStorage.getItem("aegis_alert_last")||"{}")||{}}catch{}
const alertStarted=Object.create(null);
let autoShareBusy=false;
let agentEverOnline=false;
let agentOfflineTimer=null;

function alertClock(){
  return new Date().toLocaleTimeString("ru-RU",{hour:"2-digit",minute:"2-digit"});
}
function persistAlertSettings(){
  localStorage.setItem("aegis_alert_settings",JSON.stringify(alertSettings));
}
function persistAlertLast(){
  localStorage.setItem("aegis_alert_last",JSON.stringify(alertLastTriggers));
}
function persistAutoIncidents(){
  const auto=state.incidents.filter(i=>i.auto).slice(0,50);
  localStorage.setItem("aegis_auto_incidents",JSON.stringify(auto));
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
    ruleSustain:["sustainSec","value"],
    ruleCooldown:["cooldownMin","value"]
  };
  for(const [id,[key,prop]] of Object.entries(map)){
    const el=q("#"+id);if(!el)continue;
    el[prop]=prop==="checked"?!!alertSettings[key]:alertSettings[key];
  }
  const stateEl=q("#autoEngineState");
  if(stateEl){
    stateEl.textContent=alertSettings.enabled?"ARMED":"PAUSED";
    stateEl.classList.toggle("auto-armed",alertSettings.enabled);
    stateEl.classList.toggle("auto-paused",!alertSettings.enabled);
  }
  updateAutoQueueState();
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
    ruleSustain:["sustainSec","number"],
    ruleCooldown:["cooldownMin","number"]
  };
  for(const [id,[key,type]] of Object.entries(bindings)){
    const el=q("#"+id);if(!el)continue;
    el.addEventListener("change",()=>{
      alertSettings[key]=type==="checked"?el.checked:Number(el.value);
      persistAlertSettings();syncAutomationUi();
      if(key==="autoAether"&&alertSettings.autoAether)flushAutoShares();
    });
  }
  syncAutomationUi();
}
function autoIncidentId(){
  return "AUTO-"+Date.now().toString(36).toUpperCase();
}
function createAutoIncident(ruleKey,data){
  const now=Date.now();
  const cooldown=Math.max(1,Number(alertSettings.cooldownMin)||10)*60000;
  if(now-(Number(alertLastTriggers[ruleKey])||0)<cooldown)return null;
  alertLastTriggers[ruleKey]=now;persistAlertLast();
  const incident={
    id:autoIncidentId(),
    title:data.title,
    source:data.source,
    severity:data.severity||"Высокий",
    status:"Авто",
    time:alertClock(),
    description:data.description,
    events:[alertClock()+" — правило "+ruleKey+" сработало",...(data.events||[])],
    auto:true,
    aetherSent:false,
    ruleKey,
    createdAt:new Date().toISOString()
  };
  state.incidents.unshift(incident);
  state.current=incident.id;
  persistAutoIncidents();
  renderIncidents();
  updateAutoQueueState();
  toast("AEGIS создал "+incident.id+": "+incident.title);
  if(alertSettings.autoAether)flushAutoShares();
  return incident;
}
function evaluateSustainedRule(key,condition,build,sustainMs){
  if(!alertSettings.enabled){delete alertStarted[key];return;}
  const now=Date.now();
  if(!condition){delete alertStarted[key];return;}
  if(!alertStarted[key])alertStarted[key]=now;
  if(now-alertStarted[key]<(sustainMs??Math.max(5,Number(alertSettings.sustainSec)||15)*1000))return;
  const created=createAutoIncident(key,build());
  if(created)alertStarted[key]=now;
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
        persistAutoIncidents();updateAutoQueueState();
      }catch(error){
        console.warn("AEGIS auto AETHER send failed",incident.id,error);
        break;
      }
    }
  }finally{autoShareBusy=false}
}
function evaluateTelemetryAlerts(payload){
  if(!alertSettings.enabled)return;
  const system=payload?.system||{};
  const machine=system.machineName||"Windows PC";
  const sustain=Math.max(5,Number(alertSettings.sustainSec)||15)*1000;
  const cpu=Number(system.cpuLoadPercent);
  evaluateSustainedRule("cpu-load",Number.isFinite(cpu)&&cpu>=alertSettings.cpuLoad,()=>({
    title:"Критическая нагрузка CPU",
    source:machine,
    severity:cpu>=99?"Критический":"Высокий",
    description:"CPU удерживается на "+cpu.toFixed(1)+"%, порог "+alertSettings.cpuLoad+"%.",
    events:[alertClock()+" — CPU "+cpu.toFixed(1)+"%"]
  }),sustain);

  const cpuTemp=Number(system.cpuTemperatureC);
  evaluateSustainedRule("cpu-temp",Number.isFinite(cpuTemp)&&cpuTemp>=alertSettings.cpuTemp,()=>({
    title:"Перегрев CPU",
    source:machine,
    severity:"Критический",
    description:"Температура CPU "+cpuTemp.toFixed(1)+" °C, порог "+alertSettings.cpuTemp+" °C.",
    events:[alertClock()+" — CPU package "+cpuTemp.toFixed(1)+" °C"]
  }),Math.min(sustain,10000));

  const ram=Number(system.memory?.loadPercent);
  evaluateSustainedRule("ram-load",Number.isFinite(ram)&&ram>=alertSettings.ram,()=>({
    title:"Критическая загрузка памяти",
    source:machine,
    severity:"Высокий",
    description:"Использование RAM удерживается на "+ram.toFixed(1)+"%, порог "+alertSettings.ram+"%.",
    events:[alertClock()+" — RAM "+ram.toFixed(1)+"%"]
  }),sustain);

  for(const gpu of system.gpus||[]){
    const temp=Number(gpu.temperatureC);
    const key="gpu-temp:"+String(gpu.name||"gpu");
    evaluateSustainedRule(key,Number.isFinite(temp)&&temp>=alertSettings.gpuTemp,()=>({
      title:"Перегрев GPU",
      source:(gpu.name||"GPU")+" / "+machine,
      severity:"Критический",
      description:"Температура GPU "+temp.toFixed(1)+" °C, порог "+alertSettings.gpuTemp+" °C.",
      events:[alertClock()+" — GPU "+temp.toFixed(1)+" °C"]
    }),Math.min(sustain,10000));
  }

  for(const disk of system.disks||[]){
    const used=Number(disk.usedPercent);
    const key="disk:"+String(disk.name||"disk");
    evaluateSustainedRule(key,Number.isFinite(used)&&used>=alertSettings.disk,()=>({
      title:"Заканчивается место на диске",
      source:(disk.name||"Disk")+" / "+machine,
      severity:used>=99?"Критический":"Высокий",
      description:"Диск заполнен на "+used.toFixed(1)+"%, порог "+alertSettings.disk+"%.",
      events:[alertClock()+" — свободно "+Number(disk.freeGb||0).toFixed(1)+" GB"]
    }),5000);
  }

  const hot=(system.temperatures||[]).reduce((max,t)=>Math.max(max,Number(t.celsius)||0),0);
  const rpms=(system.fans||[]).map(f=>Number(f.rpm)).filter(Number.isFinite);
  const maxRpm=rpms.length?Math.max(...rpms):null;
  evaluateSustainedRule("fan-stall",hot>=85&&maxRpm!==null&&maxRpm<=100,()=>({
    title:"Возможная остановка охлаждения",
    source:machine,
    severity:"Критический",
    description:"Температура датчика достигла "+hot.toFixed(1)+" °C, при этом доступные вентиляторы показывают не более "+Math.round(maxRpm)+" RPM.",
    events:[alertClock()+" — проверка RPM/температуры"]
  }),10000);

  const processes=payload?.processes||[];
  const top=processes.find(p=>Number(p.cpuPercent)>=alertSettings.processCpu);
  if(top){
    const procCpu=Number(top.cpuPercent);
    const key="process-cpu:"+String(top.name||top.pid);
    evaluateSustainedRule(key,true,()=>({
      title:"Аномальная нагрузка процесса",
      source:(top.name||"process")+" (PID "+top.pid+")",
      severity:procCpu>=95?"Высокий":"Средний",
      description:"Процесс удерживает "+procCpu.toFixed(1)+"% CPU. Это ресурсная аномалия, а не автоматический вывод о вредоносности.",
      events:[alertClock()+" — RAM "+Number(top.memoryMb||0).toFixed(1)+" MB"]
    }),sustain);
  }
  for(const key of Object.keys(alertStarted)){
    if(key.startsWith("process-cpu:")&&(!top||key!=="process-cpu:"+String(top.name||top.pid)))delete alertStarted[key];
  }
}
function markAgentOnlineForAlerts(){
  agentEverOnline=true;
  if(agentOfflineTimer){clearTimeout(agentOfflineTimer);agentOfflineTimer=null}
}
function scheduleAgentOfflineAlert(){
  if(!agentEverOnline||!alertSettings.enabled)return;
  if(agentOfflineTimer)clearTimeout(agentOfflineTimer);
  agentOfflineTimer=setTimeout(()=>{
    if(!agentSocket||agentSocket.readyState!==WebSocket.OPEN){
      createAutoIncident("agent-offline",{
        title:"Потеря связи с AEGIS Agent",
        source:"127.0.0.1:8765",
        severity:"Высокий",
        description:"Локальный агент мониторинга недоступен более 15 секунд.",
        events:[alertClock()+" — WebSocket telemetry offline"]
      });
    }
  },15000);
}

const AGENT_HTTP="http://127.0.0.1:8765";
const AGENT_WS="ws://127.0.0.1:8765/ws/monitor";
let agentSocket=null;
let reconnectTimer=null;

function setAgentState(online,text){
  const el=q("#agentState");
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
  evaluateTelemetryAlerts(payload);
}

function renderProcesses(processes){
  q("#processCount").textContent=processes.length+" процессов";
  q("#processRows").innerHTML=processes.length?processes.map(p=>
    '<div class="process-row"><span>'+p.pid+'</span><span class="process-name" title="'+escapeHtml(p.path||"")+'">'+escapeHtml(p.name)+'</span><span>'+fmtPercent(p.cpuPercent)+'</span><span>'+Number(p.memoryMb).toFixed(1)+' MB</span><span>'+(p.threads??"—")+'</span></div>'
  ).join(""):'<div class="empty-process">Нет данных. Запусти локальный агент.</div>';
}

async function probeAgent(){
  try{
    const response=await fetch(AGENT_HTTP+"/api/health",{cache:"no-store"});
    if(!response.ok)throw new Error("HTTP "+response.status);
    const health=await response.json();
    setAgentState(true,"AGENT ONLINE");markAgentOnlineForAlerts();
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

    ws.onopen=()=>{setAgentState(true,"AGENT ONLINE");markAgentOnlineForAlerts()};
    ws.onmessage=event=>{
      try{renderTelemetry(JSON.parse(event.data))}catch(error){console.error("AEGIS telemetry parse error",error)}
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

function tick(){q("#clock").textContent=new Date().toLocaleTimeString("ru-RU",{hour12:false})}tick();setInterval(tick,1000);bindAutomationUi();renderIncidents();connectAgent();