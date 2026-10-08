// AEGIS incident lifecycle and chat composition.
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
    try{await fetch("/api/v1/incidents/"+encodeURIComponent(i.id),{method:"DELETE"});}catch{}
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
    // Sent message is rendered once by the aether-sent event.
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