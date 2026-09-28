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
assert.match(program, /AddScheduleExecutionSnapshot\(\s*connection,\s*tcode,\s*paramsJson,\s*existingSnapshot\?\.TCode/);
assert.match(program, /existingSnapshot\?\.ParamsJson/);
assert.match(program, /existingSnapshot\?\.TCode/);
assert.match(program, /previousParamsJson/);
assert.match(program, /previousValues\.TryGetValue\(key/);
assert.match(program, /transactionChanged/);
assert.match(program, /!hasScriptSnapshot \|\| transactionChanged/);
assert.match(program, /!hasTimeoutSnapshot \|\| transactionChanged/);
assert.match(program, /BackfillScheduleExecutionSnapshots\(connection\)/);
assert.match(program, /UPDATE schedule_tasks SET params_json=\$params/);
assert.match(program, /if \(IsScheduleSnapshotSource\(request\.Source\)\)\s*\{\s*string savedScript/);
assert.match(program, /script\.ScriptFile = savedScript/);
assert.match(program, /script\.ScriptHash = savedHash/);

console.log("SCHEDULE_SNAPSHOT_ISOLATION_CONTRACT_OK");
