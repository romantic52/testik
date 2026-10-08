import fs from "node:fs";
import path from "node:path";

const html = fs.readFileSync("index.html", "utf8");
const runtimeFiles = [
  "aether.js",
  "frontend/core.js",
  "frontend/incidents.js",
  "frontend/aether-ui.js",
  "frontend/automation.js",
  "frontend/monitoring.js",
  "frontend/crash-demo.js",
  "app.js"
];

function fail(message) {
  console.error("[frontend-check] " + message);
  process.exitCode = 1;
}

const ids = [...html.matchAll(/\sid="([^"]+)"/g)].map(match => match[1]);
const idSet = new Set(ids);
const duplicates = ids.filter((id, index) => ids.indexOf(id) !== index);

if (duplicates.length) {
  fail("duplicate ids: " + [...new Set(duplicates)].join(", "));
}

for (const [open, close, label] of [
  [/<section\b/g, /<\/section>/g, "section"],
  [/<main\b/g, /<\/main>/g, "main"],
  [/<aside\b/g, /<\/aside>/g, "aside"],
  [/<form\b/g, /<\/form>/g, "form"]
]) {
  const opens = (html.match(open) || []).length;
  const closes = (html.match(close) || []).length;
  if (opens !== closes) {
    fail(label + " tags are unbalanced: " + opens + " / " + closes);
  }
}

const requiredIds = [
  "overview",
  "incidents",
  "monitoring",
  "reports",
  "audit",
  "events",
  "services",
  "messages",
  "processRows",
  "networkConnectionRows",
  "windowsEventList",
  "cpuHistoryLine",
  "incidentModal",
  "processModal",
  "agentDiagnosticsGrid"
];

for (const id of requiredIds) {
  if (!idSet.has(id)) fail("required element #" + id + " is missing");
}

for (const match of html.matchAll(/<(?:script|link)[^>]+(?:src|href)="([^"]+)"/g)) {
  const url = match[1];
  if (/^https?:\/\//i.test(url)) {
    fail("external frontend resource is not allowed: " + url);
  }
}

const scriptSources = [...html.matchAll(/<script[^>]+src="([^"]+)"/g)]
  .map(match => match[1]);

let previousIndex = -1;
for (const file of runtimeFiles) {
  if (!fs.existsSync(file)) {
    fail("runtime file is missing: " + file);
    continue;
  }

  const index = scriptSources.indexOf(file);
  if (index < 0) {
    fail("runtime file is not loaded by index.html: " + file);
  } else if (index <= previousIndex) {
    fail("runtime script order is invalid around: " + file);
  } else {
    previousIndex = index;
  }
}

const runtimeSource = runtimeFiles
  .filter(file => fs.existsSync(file))
  .map(file => fs.readFileSync(file, "utf8"))
  .join("\n");

const referencedIds = new Set();
const dynamicallyCreatedIds = new Set([
  "shareIncident",
  "closeIncident",
  "deleteIncident",
  "processIncidentButton"
]);
for (const pattern of [
  /\bq\("#([^"]+)"\)/g,
  /\bq\('#([^']+)'\)/g,
  /document\.getElementById\("([^"]+)"\)/g,
  /document\.getElementById\('([^']+)'\)/g
]) {
  for (const match of runtimeSource.matchAll(pattern)) {
    referencedIds.add(match[1]);
  }
}

for (const id of referencedIds) {
  if (!idSet.has(id) && !dynamicallyCreatedIds.has(id)) {
    fail("runtime references missing element #" + id);
  }
}

if (!runtimeSource.includes("/api/v1/")) {
  fail("operator runtime is not using versioned /api/v1 endpoints");
}

if (runtimeSource.includes("shutdown /") || runtimeSource.includes("Stop-Computer")) {
  fail("destructive shutdown command found in frontend runtime");
}


const embeddedChatIds = [
  "aetherPanel",
  "aetherMessenger",
  "aetherContacts",
  "aetherPeerForm",
  "aetherNewPeer",
  "aetherPeerName",
  "aetherPeerAvatar",
  "aetherConnectionDot",
  "aetherCollapseToggle",
  "chatForm",
  "chatInput",
  "chatSendButton",
  "aetherLoginStatus"
];

for (const id of embeddedChatIds) {
  if (!idSet.has(id)) fail("embedded AETHER mini-chat is missing element #" + id);
}
const embeddedUi = fs.readFileSync("frontend/aether-ui.js", "utf8");
const adapter = fs.readFileSync("aether.js", "utf8");
if (!embeddedUi.includes("aether-message") || !embeddedUi.includes("aether-sent")) {
  fail("mini-chat must subscribe to both incoming and outgoing AETHER events");
}
if (!adapter.includes("encryptLocalState") || !adapter.includes("saveChatHistory")) {
  fail("mini-chat local history must be encrypted");
}
if (!embeddedUi.includes("document.createElement") || !embeddedUi.includes(".textContent")) {
  fail("mini-chat must render decrypted text using safe DOM text nodes");
}

if (!process.exitCode) {
  console.log(
    "[frontend-check] structure OK, unique ids=" +
      ids.length +
      ", runtime files=" +
      runtimeFiles.length +
      ", checked refs=" +
      referencedIds.size
  );
}
