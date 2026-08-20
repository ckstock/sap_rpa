const assert = require("assert");
const fs = require("fs");
const path = require("path");

const root = path.resolve(__dirname, "..");
const program = fs.readFileSync(path.join(root, "网页启动登录", "SapWebLauncher", "Program.cs"), "utf8");

assert.match(program, /ScheduleTaskScopeSnapshot\? existingSnapshot/);
assert.match(program, /applyConfiguredScope: !isExistingSchedule/);
assert.match(program, /if \(!IsScheduleSnapshotSource\(request\.Source\)\)\s*ApplyConfiguredTransactionScope/);
assert.match(program, /query\["schedulesnapshot"\] = "1"/);
assert.match(program, /IsScheduleSnapshot = First\(query, "schedulesnapshot"\) == "1"/);
assert.match(program, /if \(!p\.IsScheduleSnapshot\)\s*\{/);

console.log("SCHEDULE_SNAPSHOT_ISOLATION_CONTRACT_OK");
