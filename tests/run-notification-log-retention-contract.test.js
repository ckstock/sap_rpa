const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const programPath = path.join(__dirname, "..", "网页启动登录", "SapWebLauncher", "Program.cs");
const source = fs.readFileSync(programPath, "utf8");

assert.match(
  source,
  /run_result_logs[\s\S]*?log_kind TEXT NOT NULL DEFAULT 'result'/,
  "run_result_logs must distinguish execution result logs from lifecycle notification events"
);
assert.match(
  source,
  /DELETE FROM run_result_logs WHERE run_id=\$runId AND log_kind='result'/,
  "completing a run must replace only execution-result logs"
);
assert.match(
  source,
  /INSERT INTO run_result_logs\(run_id, log_kind, level, message\) VALUES\(\$runId, 'event'/,
  "queue and notification diagnostics must be stored as durable event logs"
);
assert.doesNotMatch(
  source,
  /DELETE FROM run_result_logs WHERE run_id=\$runId"\s*;/,
  "run completion must not erase start-notification diagnostics"
);
assert.match(
  source,
  /static void DispatchRunNotification\([\s\S]*?bool isStartEvent = eventName\.Equals\("start", StringComparison\.OrdinalIgnoreCase\);[\s\S]*?if \(IsRunFinishedEvent\(eventName\) \|\| isStartEvent\)/,
  "single-run start events must enter the SAP DingTalk notification path"
);

console.log("run notification lifecycle log retention contract passed");
