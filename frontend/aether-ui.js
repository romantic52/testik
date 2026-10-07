// AETHER operator UI bindings and manual incident creation.\nconst aetherConfig=q("#aetherConfig"),aetherTotpField=q("#aetherTotpField");
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
q("#notify").onclick=()=>toast(state.incidents.filter(i=>i.status!=="Закрыт").length+" активных инцидентов");\n