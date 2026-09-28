const assert = require("assert");
const fs = require("fs");
const path = require("path");

const repoRoot = path.resolve(__dirname, "..");
const vbs = fs.readFileSync(path.join(repoRoot, "网页启动登录", "transactions", "ZFIR034.vbs"), "utf8");
const program = fs.readFileSync(path.join(repoRoot, "网页启动登录", "SapWebLauncher", "Program.cs"), "utf8");
const skill = fs.readFileSync(path.join("D:", "RPA", ".codex", "skills", "sap-rpa-alv-export", "SKILL.md"), "utf8");

assert.match(vbs, /NO_DATA=1/);
assert.match(vbs, /STATUS_TYPE=W/);
assert.match(vbs, /S_BUDAT=/);
assert.match(vbs, /ALV export was skipped/);
assert.match(program, /explicitNoDataMarker/);
assert.match(program, /classified SAP result as no_data/);
assert.match(skill, /NO_DATA=1/);
assert.match(skill, /No-Data and ALV Export Outcomes/);
console.log("ZFIR034_NO_DATA_CONTRACT_OK");
