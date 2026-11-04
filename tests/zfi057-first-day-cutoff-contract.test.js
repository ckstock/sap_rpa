const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const root = path.join(__dirname, "..");
const program = fs.readFileSync(
  path.join(root, "网页启动登录", "SapWebLauncher", "Program.cs"),
  "utf8"
);
const vbs = fs.readFileSync(
  path.join(root, "网页启动登录", "transactions", "ZFI057.vbs"),
  "utf8"
);

assert.match(
  program,
  /DateTime kadatLow = windowHigh == currentMonthStart[\s\S]*?\? currentMonthStart[\s\S]*?: currentMonthStart\.AddDays\(1\);[\s\S]*?windows\.Add/,
  "ZFI057 backend must use day 1 when the cutoff-month window contains only day 1"
);
assert.match(
  program,
  /ResolveZfi057Step2DateWindows\("2026\.10\.26", "2026\.11\.01"\)[\s\S]*?firstDayCutoffWindows\.Count == 3[\s\S]*?KadatLow\.Equals\("2026\.11\.01"/,
  "ZFI057 backend self-test must cover a cutoff on the first day of a month"
);
assert.match(
  vbs,
  /If windowHigh = currentMonthStart Then[\s\S]*?kadatWindowStart = currentMonthStart[\s\S]*?Else[\s\S]*?kadatWindowStart = DateAdd\("d", 1, currentMonthStart\)[\s\S]*?runCount = runCount \+ 1/,
  "ZFI057 VBS must use day 1 when the cutoff-month window contains only day 1"
);
assert.match(
  program,
  /static bool IsZfi057Step2NoDataResult[\s\S]*?IsNoDataRunStatus\(result\.Status\)[\s\S]*?IsExplicitNoDataResult\(result\)/,
  "ZFI057 must not classify a real final error as no-data because an earlier window had no data"
);

console.log("ZFI057_FIRST_DAY_CUTOFF_CONTRACT_OK");
