const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const root = path.join(__dirname, "..");
const indexSource = fs.readFileSync(path.join(root, "index.html"), "utf8");
const renderSource = fs.readFileSync(path.join(root, "assets", "js", "portal-render.js"), "utf8");
const actionsSource = fs.readFileSync(path.join(root, "assets", "js", "portal-actions.js"), "utf8");
const vendorPath = path.join(root, "assets", "vendor", "lucide-1.33.0.min.js");

assert.doesNotMatch(
  indexSource,
  /<script[^>]+src=["']https?:\/\//i,
  "portal startup scripts must not depend on an external CDN"
);
assert.match(
  indexSource,
  /<script src=["']assets\/vendor\/lucide-1\.33\.0\.min\.js["']><\/script>/,
  "portal must load the vendored Lucide build"
);
assert.ok(fs.statSync(vendorPath).size > 100000, "vendored Lucide build is missing or incomplete");

const renderBody = renderSource.match(/function render\(\) \{([\s\S]*?)\n    \}/)?.[1];
assert.ok(renderBody, "render function was not found");

let bound = false;
const runRender = new Function("app", "renderShell", "bindEvents", "window", "console", renderBody);
const app = { innerHTML: "" };
const silentConsole = { warn: () => {} };
runRender(app, () => "<main>ready</main>", () => { bound = true; }, {}, silentConsole);
assert.equal(app.innerHTML, "<main>ready</main>");
assert.equal(bound, true, "event binding must continue when the icon library is unavailable");

let iconsCreated = false;
runRender(app, () => "<main>ready</main>", () => {}, {
  lucide: { createIcons: () => { iconsCreated = true; } }
}, silentConsole);
assert.equal(iconsCreated, true, "icons must still initialize when the vendored library is available");

assert.doesNotThrow(
  () => runRender(app, () => "<main>ready</main>", () => {}, {
    lucide: { createIcons: () => { throw new Error("icon failure"); } }
  }, silentConsole),
  "icon rendering failures must not block portal initialization"
);

assert.doesNotMatch(
  renderSource + actionsSource,
  /启动本机 API|请启动 API/,
  "remote users must not be told to start an API on their own computer"
);
assert.match(
  renderSource + actionsSource,
  /执行服务器 API/,
  "offline messages must identify the shared execution-server API"
);
assert.match(
  actionsSource,
  /async function saveScheduleFromModal\(\)[\s\S]*?if \(!state\.bridge\.online \|\| !state\.config\.online\) \{\s*await refreshBridgeData\(\{ silent: true \}\);\s*\}[\s\S]*?if \(!state\.bridge\.online \|\| !state\.config\.online\) \{\s*toast\(/,
  "schedule save must retry the execution-server API before reporting an offline failure"
);

console.log("FRONTEND_API_INITIALIZATION_DEPENDENCY_OK");
