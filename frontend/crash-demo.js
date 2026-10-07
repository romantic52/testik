// Safe visual-only crash simulation. No destructive Windows action is performed.
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