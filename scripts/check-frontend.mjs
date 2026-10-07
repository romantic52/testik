import fs from "node:fs";

const html = fs.readFileSync("index.html", "utf8");

function fail(message) {
  console.error("[frontend-check] " + message);
  process.exitCode = 1;
}

const ids = [...html.matchAll(/\sid="([^"]+)"/g)].map(match => match[1]);
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
  if (opens !== closes) fail(label + " tags are unbalanced: " + opens + " / " + closes);
}

const requiredIds = [
  "overview",
  "incidents",
  "monitoring",
  "reports",
  "audit",
  "events",
  "messages",
  "processRows",
  "networkConnectionRows",
  "windowsEventList",
  "cpuHistoryLine",
  "incidentModal",
  "processModal"
];

for (const id of requiredIds) {
  if (!ids.includes(id)) fail("required element #" + id + " is missing");
}

for (const match of html.matchAll(/<(?:script|link)[^>]+(?:src|href)="([^"]+)"/g)) {
  const url = match[1];
  if (/^https?:\/\//i.test(url)) {
    fail("external frontend resource is not allowed: " + url);
  }
}

if (!html.includes('vendor/nacl.min.js') || !html.includes('aether.js') || !html.includes('app.js')) {
  fail("required frontend scripts are missing");
}

if (!process.exitCode) {
  console.log("[frontend-check] structure OK, unique ids=" + ids.length);
}
