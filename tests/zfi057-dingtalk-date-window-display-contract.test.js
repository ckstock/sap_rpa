const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const programPath = path.join(__dirname, "..", "网页启动登录", "SapWebLauncher", "Program.cs");
const source = fs.readFileSync(programPath, "utf8");

assert.match(
  source,
  /static List<string> FormatZfi057DingTalkDateWindowValues\([\s\S]*?if \(ordered\.Count == 0\)[\s\S]*?return new List<string>\(\);/,
  "ZFI057 DingTalk input must display a single date window"
);
console.log("ZFI057_DINGTALK_DATE_WINDOW_DISPLAY_OK");
