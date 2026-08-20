const assert = require("assert");
const fs = require("fs");
const path = require("path");

const root = path.resolve(__dirname, "..");
const render = fs.readFileSync(path.join(root, "assets/js/portal-render.js"), "utf8");
const program = fs.readFileSync(path.join(root, "网页启动登录", "SapWebLauncher", "Program.cs"), "utf8");

assert.match(render, /function allowsCustomScheduleBusinessAreaScope\(tCode\)/);
assert.match(render, /code === "ZFI019NL"/);
assert.match(render, /code === "ZFI019NA"/);
assert.match(render, /!allowsCustomScheduleBusinessAreaScope\(tCode\)/);
assert.match(program, /static bool AllowsCustomBusinessAreaScope\(string tcode\)/);
assert.match(program, /normalizedTcode\.Equals\("ZFI019NL", StringComparison\.OrdinalIgnoreCase\)/);
assert.match(program, /normalizedTcode\.Equals\("ZFI019NA", StringComparison\.OrdinalIgnoreCase\)/);
assert.match(program, /ApplyCustomBusinessAreaScope\(p, GetFixedBusinessAreasCsv/);
assert.doesNotMatch(program, /AllowsCustomZfi057BusinessAreaScope/);

console.log("ZFI019NL_SCHEDULE_CUSTOM_SCOPE_CONTRACT_OK");
