const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const script = fs.readFileSync(
  path.join(__dirname, "..", "gateway", "start-rpa-gateway.ps1"),
  "utf8"
);

assert.match(script, /function Get-ProjectGatewayProcess/);
assert.match(script, /CommandLine\.IndexOf\(\$GatewayScript/);
assert.match(script, /function Test-GatewayHealth/);
assert.match(script, /\/api\/health/);
assert.match(script, /\$payload\.ok -eq \$true/);
assert.match(script, /\$payload\.app -eq "SapWebLauncher Bridge"/);
assert.match(
  script,
  /if \(-not \$projectProcess\) \{[\s\S]*?not the configured SAP RPA gateway; it will not be stopped or reused/
);
assert.match(
  script,
  /if \(Test-GatewayHealth -Protocol \$Protocol -Port \$Port\) \{[\s\S]*?reusing process/
);
assert.match(script, /Stop-Process -Id \$projectProcess\.ProcessId -Force/);
assert.doesNotMatch(
  script,
  /if \(Test-PortListener -Port \$\w+Port\) \{[\s\S]*?reusing it/
);

console.log("GATEWAY_STARTUP_HEALTH_CONTRACT_OK");
