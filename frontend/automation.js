// Alert settings UI and queued AETHER delivery. Backend remains source of truth for rule evaluation.\nconst ALERT_DEFAULTS={
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
    const value=await apiJson("/api/v1/settings/alerts");
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
    const saved=await apiJson("/api/v1/settings/alerts",{
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
}\n