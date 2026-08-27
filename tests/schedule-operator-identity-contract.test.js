const assert = require("assert");
const fs = require("fs");
const path = require("path");

const root = path.resolve(__dirname, "..");
const render = fs.readFileSync(path.join(root, "assets", "js", "portal-render.js"), "utf8");
const program = fs.readFileSync(path.join(root, "网页启动登录", "SapWebLauncher", "Program.cs"), "utf8");

assert.match(render, /const personnelNumber = getResolvedPersonnelNumber\(\);/);
assert.match(render, /operatorId: personnelNumber/);
assert.match(render, /operatorName: currentUserName/);
assert.match(program, /Dictionary<string, string> scheduleParams = ParseScheduleParams\(task\.ParamsJson\)/);
assert.match(program, /GetParamValue\(scheduleParams, "operatorId"\)/);
assert.match(program, /GetParamValue\(scheduleParams, "operatorName"\)/);
assert.match(program, /GetParamValue\(scheduleParams, "pernr"\),[\s\S]*?ResolveDingTalkUserIdFromNotifyTarget\(task\.NotifyTarget\)/);

console.log("SCHEDULE_OPERATOR_IDENTITY_CONTRACT_OK");
