const state={current:"INC-2401",incidents:[
{id:"INC-2401",title:"Подозрительная авторизация",source:"VPN-GW-02",severity:"Критический",status:"Расследование",time:"02:13",description:"Успешная авторизация после серии отказов. Новый ASN и нетипичная география.",events:["01:58 — 4 отклонённых входа","02:07 — ещё 2 отклонённых входа","02:13 — успешная авторизация","02:14 — корреляция SIEM повысила риск"]},
{id:"INC-2398",title:"Аномальный исходящий трафик",source:"WS-FIN-14",severity:"Высокий",status:"В работе",time:"01:47",description:"Рабочая станция финансового отдела установила соединение с ранее не наблюдавшимся узлом.",events:["01:41 — EDR отметил новую сессию","01:47 — превышен сетевой baseline","01:52 — оператор начал проверку"]},
{id:"INC-2394",title:"Повторный отказ доступа",source:"Door B-17",severity:"Средний",status:"Проверка",time:"00:58",description:"Несколько попыток прохода по карте сотрудника вне разрешённой зоны.",events:["00:54 — первый отказ","00:56 — второй отказ","00:58 — создано событие СКУД"]}
]};
const titles={overview:"Обзор инфраструктуры",incidents:"Управление инцидентами",assets:"Активы предприятия",access:"Контроль доступа",cameras:"Видеонаблюдение",reports:"Отчёты и аналитика"};
const q=s=>document.querySelector(s),qa=s=>[...document.querySelectorAll(s)];
function toast(t){const e=q("#toast");e.textContent=t;e.classList.add("show");clearTimeout(window.tt);window.tt=setTimeout(()=>e.classList.remove("show"),1800)}
function view(id){qa(".view").forEach(v=>v.classList.toggle("active",v.id===id));qa(".nav").forEach(n=>n.classList.toggle("active",n.dataset.view===id));q("#pageTitle").textContent=titles[id]||"AEGIS SOC";if(id==="incidents")renderIncidents()}
qa(".nav").forEach(b=>b.onclick=()=>view(b.dataset.view));qa("[data-go]").forEach(b=>b.onclick=()=>view(b.dataset.go));
function renderIncidents(){q("#incidentList").innerHTML=state.incidents.map(i=>`<div class="incident-item ${i.id===state.current?"active":""}" data-id="${i.id}"><div><span>${i.id}</span><span>${i.time}</span></div><h3>${i.title}</h3><p>${i.source} • ${i.severity} • ${i.status}</p></div>`).join("");qa(".incident-item").forEach(x=>x.onclick=()=>{state.current=x.dataset.id;renderIncidents()});const i=state.incidents.find(x=>x.id===state.current);q("#incidentDetail").innerHTML=`<h3 class="detail-title">${i.title}</h3><div class="detail-meta"><span>${i.id}</span><span>${i.source}</span><span>${i.severity}</span><span>${i.status}</span></div><p class="muted">${i.description}</p><div class="timeline">${i.events.map(e=>{const p=e.split(" — ");return `<div class="event"><b>${p[0]}</b><p>${p[1]}</p></div>`}).join("")}</div><button class="primary" id="shareIncident">Отправить в AETHER.chat</button>`;q("#shareIncident").onclick=()=>shareIncident(i)}
function addMessage(text){const box=q("#messages"),d=document.createElement("div");d.className="message outgoing";d.innerHTML=`<div><b>Вы</b><span>${new Date().toLocaleTimeString("ru-RU",{hour:"2-digit",minute:"2-digit"})}</span></div><p></p>`;d.querySelector("p").textContent=text;box.appendChild(d);box.scrollTop=box.scrollHeight}
async function shareIncident(i){await window.aether.shareIncident(i);addMessage(`${i.id} • ${i.title} • ${i.source}`);toast("Карточка отправлена через AETHER adapter")}
qa(".incident-jump").forEach(r=>r.onclick=()=>{state.current=r.dataset.id;view("incidents")});qa("[data-open]").forEach(b=>b.onclick=()=>{state.current=b.dataset.open;view("incidents")});
q("#chatForm").onsubmit=async e=>{e.preventDefault();const input=q("#chatInput"),text=input.value.trim();if(!text)return;await window.aether.sendMessage({text});addMessage(text);input.value="";toast("Сообщение отправлено через AETHER adapter")};
qa("[data-text]").forEach(b=>b.onclick=()=>{q("#chatInput").value=b.dataset.text;q("#chatInput").focus()});
q("#newIncident").onclick=()=>toast("Создание инцидента — demo UI");q("#report").onclick=()=>toast("Отчёт SOC сформирован");q("#notify").onclick=()=>toast("3 уведомления высокого приоритета");
const access=[["02:12","Серверная A-02","Разрешён • Иван П."],["01:58","Door B-17","Отказ • Карта #1842"],["01:35","Главный вход","Разрешён • Анна К."],["00:49","Архив C-04","Разрешён • Сервисная карта"]];
q("#accessLog").innerHTML=access.map(x=>`<div class="access-entry"><b>${x[0]}</b><span>${x[1]}</span><em>${x[2]}</em></div>`).join("");

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

function tick(){q("#clock").textContent=new Date().toLocaleTimeString("ru-RU",{hour12:false})}tick();setInterval(tick,1000);renderIncidents();