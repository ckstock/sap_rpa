const assert = require("assert");
const fs = require("fs");
const path = require("path");

const scriptPath = path.resolve(__dirname, "..", "网页启动登录", "transactions", "ZFI057.vbs");
const script = fs.readFileSync(scriptPath, "utf8");

assert.match(script, /alvExportDir = "\{ALV_EXPORT_DIR\}"/);
assert.match(script, /alvExportFilename = "\{ALV_EXPORT_FILENAME\}"/);
assert.match(script, /scriptDir = "\{SCRIPT_DIR\}"/);
assert.match(script, /Function LoadAlvExportHelper\(\)/);
assert.match(script, /Sub ExportAlvForWindow\(index\)/);
assert.match(script, /If runCount > 1 Then exportFilename = AlvBuildExportFilename\(alvExportFilename, "part" & CStr\(index\)\)/);
assert.match(script, /If Not AlvExportIfConfigured\(session, alvExportDir, exportFilename, exportTimeoutMs\) Then Fail "ALV export returned false for ZFI057 group #" & index, 8/);
assert.match(script, /ExportAlvForWindow index/);

console.log("ZFI057_ALV_EXPORT_CONTRACT_OK");
