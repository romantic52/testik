// AEGIS BSOD presentation: always simulated. Optional real Windows shutdown is explicit,
// opt-in, cancellable, and only initiated by a dedicated local operator action.
const crashConfirm=q("#crashConfirm"),bsod=q("#bsod"),fakeOff=q("#fakeOff");
let crashDemoTimer=null;
let shutdownCountdownTimer=null;
let scheduledShutdown=false;

function selectedCrashMode(){
  return q('input[name="crashMode"]:checked')?.value||"demo";
}
function updateCrashMode(){
  const real=selectedCrashMode()==="shutdown";
  q("#shutdownConsent").hidden=!real;
  q("#crashDemoChoice").classList.toggle("selected",!real);
  q("#crashShutdownChoice").classList.toggle("selected",real);
  q("#confirmCrash").textContent=real?"Подтвердить выключение":"Запустить демонстрацию";
  q("#confirmCrash").disabled=real&&q("#shutdownPhrase").value.trim()!=="ВЫКЛЮЧИТЬ";
}
function openCrashDialog(){
  q('input[name="crashMode"][value="demo"]').checked=true;
  q("#shutdownPhrase").value="";
  updateCrashMode();
  crashConfirm.classList.add("show");
  crashConfirm.setAttribute("aria-hidden","false");
  q("#cancelCrash").focus();
}
function closeCrashDialog(){
  crashConfirm.classList.remove("show");
  crashConfirm.setAttribute("aria-hidden","true");
}
q("#crashButton").addEventListener("click",openCrashDialog);
q("#cancelCrash").addEventListener("click",closeCrashDialog);
qa('input[name="crashMode"]').forEach(input=>input.addEventListener("change",updateCrashMode));
q("#shutdownPhrase").addEventListener("input",updateCrashMode);
q("#confirmCrash").addEventListener("click",async()=>{
  if(selectedCrashMode()==="shutdown"){
    if(q("#shutdownPhrase").value.trim()!=="ВЫКЛЮЧИТЬ")return;
    const button=q("#confirmCrash");
    button.disabled=true;
    try{
      const response=await fetch("/api/v1/system/shutdown",{
        method:"POST",
        headers:{"Content-Type":"application/json","X-AEGIS-Operator-Intent":"local-shutdown-confirmed"},
        body:JSON.stringify({confirmation:"ВЫКЛЮЧИТЬ"})
      });
      const result=await response.json();
      if(!response.ok||!result.success)throw new Error(result.detail||result.message||"Не удалось запланировать выключение");
      scheduledShutdown=true;
      startBsodPresentation(true,result.delaySeconds||45);
    }catch(error){
      scheduledShutdown=false;
      toast("Windows не выключается: "+error.message);
    }finally{updateCrashMode()}
  }else{
    startBsodPresentation(false,0);
  }
});
q("#restoreSystem").addEventListener("click",()=>{
  if(scheduledShutdown){
    toast("Сначала отмени реальное выключение Windows");
    return;
  }
  hideCrashPresentation();
});
q("#cancelShutdown").addEventListener("click",async()=>{
  const button=q("#cancelShutdown");
  button.disabled=true;
  try{
    const response=await fetch("/api/v1/system/shutdown/cancel",{
      method:"POST",
      headers:{"X-AEGIS-Operator-Intent":"local-shutdown-confirmed"}
    });
    const result=await response.json();
    if(!response.ok||!result.success)throw new Error(result.detail||result.message||"Отмена недоступна");
    scheduledShutdown=false;
    hideCrashPresentation();
    toast("Выключение Windows отменено");
  }catch(error){toast("Не удалось отменить: "+error.message)}
  finally{button.disabled=false}
});
document.addEventListener("keydown",event=>{
  if(event.key==="Escape"&&!scheduledShutdown){
    if(crashConfirm.classList.contains("show"))closeCrashDialog();
    if(bsod.classList.contains("show")||fakeOff.classList.contains("show"))hideCrashPresentation();
  }
});
function hideCrashPresentation(){
  if(crashDemoTimer)clearInterval(crashDemoTimer);
  if(shutdownCountdownTimer)clearInterval(shutdownCountdownTimer);
  crashDemoTimer=null;shutdownCountdownTimer=null;
  bsod.classList.remove("show");fakeOff.classList.remove("show");
  bsod.setAttribute("aria-hidden","true");
  fakeOff.setAttribute("aria-hidden","true");
  document.exitFullscreen?.().catch(()=>{});
}
async function startBsodPresentation(real,delaySeconds){
  closeCrashDialog();
  q("#bsodModeLabel").textContent=real
    ?"AEGIS SIMULATED BSOD • REAL WINDOWS SHUTDOWN SCHEDULED"
    :"AEGIS SIMULATION — no real OS crash";
  q("#crashProgress").textContent="0";
  q("#fakeOffMessage").textContent=real
    ?"Windows получила команду завершения работы."
    :"Симуляция завершена. Компьютер не выключен.";
  q("#cancelShutdown").hidden=!real;
  q("#shutdownCountdown").hidden=!real;
  q("#restoreSystem").hidden=real;
  try{await document.documentElement.requestFullscreen?.()}catch{}
  bsod.classList.add("show");
  bsod.setAttribute("aria-hidden","false");
  let progress=0;
  crashDemoTimer=setInterval(()=>{
    progress=Math.min(100,progress+Math.floor(Math.random()*11)+6);
    q("#crashProgress").textContent=String(progress);
    if(progress===100){
      clearInterval(crashDemoTimer);
      crashDemoTimer=null;
      setTimeout(()=>{
        bsod.classList.remove("show");
        bsod.setAttribute("aria-hidden","true");
        fakeOff.classList.add("show");
        fakeOff.setAttribute("aria-hidden","false");
      },550);
    }
  },210);
  if(real){
    const stopAt=Date.now()+delaySeconds*1000;
    const refresh=()=>{
      q("#shutdownSeconds").textContent=String(Math.max(0,Math.ceil((stopAt-Date.now())/1000)));
    };
    refresh();
    shutdownCountdownTimer=setInterval(refresh,250);
  }
}