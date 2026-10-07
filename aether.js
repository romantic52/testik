class AetherAdapter {
  constructor(){this.connected=true;this.channel="shift-42";this.mode="demo";}
  async sendMessage(payload){
    if(!this.connected) throw new Error("AETHER relay unavailable");
    return {id:(crypto.randomUUID?.()||String(Date.now())),channel:this.channel,sentAt:new Date().toISOString(),ciphertext:"[demo-encrypted]",metadata:payload.metadata||{}};
  }
  async shareIncident(incident){
    return this.sendMessage({text:incident.title,metadata:{type:"incident",incidentId:incident.id,source:incident.source,severity:incident.severity}});
  }
}
window.aether=new AetherAdapter();