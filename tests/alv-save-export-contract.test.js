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

const sapSaveContracts = {
  "ZFI072A.vbs": [[
    "alvResult = ExportAlvIfConfigured(alvExportDir, alvExportFilename, exportTimeoutMs)",
    "If Not PressSaveAfterReady(saveButtonTimeoutMs) Then",
    "If Not WaitForSaveComplete(saveTimeoutMs) Then"
  ]],
  "ZFI072N.vbs": [[
    "If Not ExportAlvIfConfigured(alvExportDir, currentWindowExportFileName, exportTimeoutMs) Then",
    "SelectAllGrid",
    'PressToolbarButton "wnd[0]/tbar[1]/btn[14]"'
  ]],
  "ZFI080.vbs": [["ExportAlvBeforeSave \"save selected columns\"", 'PressToolbarButton "wnd[0]/tbar[1]/btn[16]"']],
  "ZFI080B.vbs": [["ExportAlvBeforeSave \"save selected rows\"", 'PressToolbarButton "wnd[0]/tbar[1]/btn[16]"']],
  "ZCO019.vbs": [
    ["ExportAlvBeforeSave \"export detail before save\"", 'PressToolbarButton "wnd[0]/tbar[1]/btn[26]"'],
    ["ExportAlvBeforeSave \"export summary before save\"", 'PressToolbarButton "wnd[0]/tbar[1]/btn[26]"']
  ],
  "ZFI019NA.vbs": [["ExportAlvBeforeSave \"save result\"", 'PressToolbarButton "wnd[0]/tbar[1]/btn[16]"']],
  "ZFI019NL.vbs": [["ExportAlvBeforeSave \"save result\"", 'PressToolbarButton "wnd[0]/tbar[1]/btn[16]"']],
  "ZFI148.vbs": [["ExportAlvResult \"execute\"", 'PressToolbarButton "wnd[0]/tbar[1]/btn[16]"', 'PressToolbarButton "wnd[0]/tbar[1]/btn[13]"']]
};

for (const [fileName, sequences] of Object.entries(sapSaveContracts)) {
  const script = readTransactionScript(fileName);
  assert.doesNotMatch(script, /skip SAP save/i, `${fileName} must not unconditionally skip its recorded SAP save action.`);
  if (fileName === "ZFI072A.vbs") {
    assert.match(script, /WaitForAnyObject\("save button", saveButtonIds, 0\)/, "ZFI072A must wait for the recorded save control to exist.");
    assert.doesNotMatch(script, /WaitForAnyEnabledObject\("save button"/, "ZFI072A must not gate the recorded save click on SAP GUI Enabled.");
    assert.match(script, /saveButtonIds = Array\("wnd\[0\]\/tbar\[1\]\/btn\[14\]"\)/, "ZFI072A must use only the recorded save control.");
    assert.doesNotMatch(script, /btn\[11\].*save|save.*btn\[11\]/i, "ZFI072A must not fall back to an unrecorded save control.");
    assert.match(script, /EchoSaveButtonProperties "wnd\[0\]\/tbar\[1\]\/btn\[14\]"/, "ZFI072A must log the recorded save control properties.");
    assert.match(script, /Function IsExplicitSaveSuccessText\(value\)/, "ZFI072A must require an explicit SAP save success message.");
    assert.match(script, /If Not IsExplicitSaveSuccessText\(statusText\) Then/, "ZFI072A must not treat an arbitrary S status as save success.");
  }
  for (const sequence of sequences) {
    let cursor = -1;
    for (const token of sequence) {
      const index = script.indexOf(token, cursor + 1);
      assert.ok(index > cursor, `${fileName} must export first and then retain its recorded SAP save action: ${token}`);
      cursor = index;
    }
  }
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

for (const code of ["ZFI072A", "ZFI072N", "ZFI080", "ZFI080B", "ZCO019", "ZFI019NA", "ZFI019NL", "ZFI148"]) {
  const isSaveAction = vm.runInContext(`isSaveActionTransaction({ code: "${code}", name: "plain card name" })`, context);
  assert.equal(isSaveAction, true, `${code} must render as a save/export card.`);
  const displayName = vm.runInContext(`{
    const label = "plain card name";
    renderSaveName(isSaveActionTransaction({ code: "${code}", name: label }) && !/保存/.test(label) ? label + "（保存）" : label);
  }`, context);
  assert.match(displayName, /（<span class="transaction-save-word">保存<\/span>）/, `${code} must append the save marker when the name has no save text.`);
}

for (const code of ["ZFIR034", "ZFI057"]) {
  const isSaveAction = vm.runInContext(`isSaveActionTransaction({ code: "${code}", name: "${code}" })`, context);
  assert.equal(isSaveAction, false, `${code} must not be marked as SAP save when it only exports/workflows data.`);
}

console.log("ALV_SAVE_EXPORT_CONTRACT_OK");
