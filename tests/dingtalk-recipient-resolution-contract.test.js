const assert = require('assert');
const fs = require('fs');

const source = fs.readFileSync(
  'D:/RPA/RpaProject/网页启动登录/SapWebLauncher/Program.cs',
  'utf8'
);
const fetcher = fs.readFileSync(
  'D:/RPA/RpaProject/网页启动登录/SapWebLauncher/DingTalkUserIdFetcher.cs',
  'utf8'
);
const actions = fs.readFileSync('D:/RPA/RpaProject/assets/js/portal-actions.js', 'utf8');
const render = fs.readFileSync('D:/RPA/RpaProject/assets/js/portal-render.js', 'utf8');

assert.match(fetcher, /CreateFunction\(FunctionName\)/);
assert.match(fetcher, /SetValue\(PersonnelNumberParameter, normalizedPernr\)/);
assert.match(fetcher, /GetString\(DingTalkUserIdParameter\)/);
assert.match(source, /Id = FirstNonEmpty\(/);
assert.match(source, /GetParamValue\(scheduleParams, "operatorId"\)/);
assert.match(source, /GetParamValue\(scheduleParams, "pernr"\),[\s\S]*?ResolveDingTalkUserIdFromNotifyTarget\(task\.NotifyTarget\)/);
assert.match(source, /new DingTalkUserIdFetcher\(\)\.Fetch/);
assert.match(source, /ApplyLocalConfig\(new SapRunParams/);
assert.match(source, /userid_list = request\.DingTalkId/);
assert.doesNotMatch(source, /userid_list = FirstNonEmpty\(request\.DingTalkId, DefaultDingTalkId\)/);
assert.match(source, /Id = personnelNumber,[\s\S]*?DingTalkUserId = "",[\s\S]*?Ddid = ""/);
assert.match(source, /IsWebpagePersonnelNumber\(string value\)[\s\S]*?normalized\.All\(char\.IsDigit\)/);
assert.doesNotMatch(source, /SAP_RPA_DINGTALK_ID/);
assert.match(source, /ResolveDingTalkRecipientId\(pernr, userIdResult, out bool usedPersonnelNumberFallback\)/);
assert.match(source, /usedPersonnelNumberFallback[\s\S]*?source=\{DingTalkUserIdFetcher\.PersonnelNumberParameter\}/);
assert.match(source, /usedPersonnelNumberFallback = string\.IsNullOrWhiteSpace\(resolvedDdid\)/);
assert.match(source, /\? FirstNonEmpty\(webpagePersonnelNumber, ""\)\.Trim\(\)/);
assert.doesNotMatch(source, /if \(!userIdResult\.Success\)[\s\S]*?sap dingtalk notify skipped/);
assert.match(actions, /async function createBridgeRun\(payload\)[\s\S]*?id: personnelNumber/);
assert.doesNotMatch(actions, /async function createBridgeRun\(payload\)[\s\S]*?if \(!notifyUserId\) throw/);
assert.match(render, /function buildScheduleNotifyTarget\(\)[\s\S]*?getResolvedPersonnelNumber\(\)/);
assert.doesNotMatch(render, /if \(notifyEnabled && !getResolvedNotifyUserId\(\)\) throw/);

console.log('dingtalk recipient resolution contract: passed');
