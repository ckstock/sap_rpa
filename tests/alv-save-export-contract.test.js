const assert = require("assert");
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const repoRoot = path.resolve(__dirname, "..");

function readTransactionScript(fileName) {
  return fs.readFileSync(path.join(repoRoot, "网页启动登录", "transactions", fileName), "utf8");
}

for (const fileName of ["ZFI148.vbs", "ZFIR034.vbs"]) {
  const script = readTransactionScript(fileName);
  assert.match(script, /alvExportDir = "\{ALV_EXPORT_DIR\}"/, `${fileName} must receive the ALV export directory placeholder.`);
  assert.match(script, /alvExportFilename = "\{ALV_EXPORT_FILENAME\}"/, `${fileName} must receive the ALV export filename placeholder.`);
  assert.match(script, /scriptDir = "\{SCRIPT_DIR\}"/, `${fileName} must receive the script directory placeholder.`);
  assert.match(script, /Function LoadAlvExportHelper\(\)/, `${fileName} must load the shared ALV export helper.`);
  assert.match(script, /Sub ExportAlvResult\(label\)/, `${fileName} must expose a single ALV export call.`);
  assert.match(script, /If Not AlvExportIfConfigured\(session, alvExportDir, alvExportFilename, exportTimeoutMs\) Then Fail "ALV export returned false after " & label, 8/, `${fileName} must fail if ALV export cannot be confirmed.`);
  assert.match(script, /ExportAlvResult "execute"/, `${fileName} must export immediately after execution.`);
}

{
  const script = readTransactionScript("ZFIR034.vbs");
  assert.match(script, /P_GJAHR=/, "ZFIR034 must log the derived fiscal year sent to SAP.");
  assert.match(script, /SetFieldByCandidates "p-gjahr", Array\("wnd\[0\]\/usr\/txtP_GJAHR", "wnd\[0\]\/usr\/ctxtP_GJAHR"\), yearValue/, "ZFIR034 must send P_GJAHR from the run week/year.");
}

const scripts = [
  "assets/js/portal-state.js",
  "assets/js/portal-utils.js",
  "assets/js/portal-api.js",
  "assets/js/portal-render.js",
  "assets/js/portal-actions.js"
];

const context = {
  console,
  URL,
  URLSearchParams,
  TextDecoder,
  Uint8Array,
  atob: value => Buffer.from(value, "base64").toString("binary"),
  setTimeout: () => 0,
  clearTimeout: () => {},
  Blob: function Blob() {},
  location: { href: "http://localhost/rpa/" },
  localStorage: {
    data: new Map(),
    getItem(key) { return this.data.has(key) ? this.data.get(key) : null; },
    setItem(key, value) { this.data.set(key, String(value)); },
    removeItem(key) { this.data.delete(key); }
  },
  document: {
    title: "SAP RPA",
    getElementById() { return null; },
    querySelectorAll() { return []; },
    createElement() { return { click() {}, style: {}, setAttribute() {} }; },
    body: { appendChild() {}, removeChild() {} }
  },
  window: {
    self: null,
    top: null,
    history: { replaceState() {} },
    location: { href: "http://localhost/rpa/" }
  },
  navigator: {},
  lucide: { createIcons() {} }
};
context.window.self = context.window;
context.window.top = context.window;
context.globalThis = context;

vm.createContext(context);
for (const script of scripts) {
  vm.runInContext(fs.readFileSync(path.join(repoRoot, script), "utf8"), context, { filename: script });
}

for (const code of ["ZFI148", "ZFIR034"]) {
  const isSaveAction = vm.runInContext(`isSaveActionTransaction({ code: "${code}", name: "plain card name" })`, context);
  assert.equal(isSaveAction, true, `${code} must render as a save/export card.`);
  const displayName = vm.runInContext(`{
    const label = "plain card name";
    renderSaveName(isSaveActionTransaction({ code: "${code}", name: label }) && !/保存/.test(label) ? label + "（保存）" : label);
  }`, context);
  assert.match(displayName, /（<span class="transaction-save-word">保存<\/span>）/, `${code} must append the save marker when the name has no save text.`);
}

console.log("ALV_SAVE_EXPORT_CONTRACT_OK");
