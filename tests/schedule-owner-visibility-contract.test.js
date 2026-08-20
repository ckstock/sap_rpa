const assert = require("assert");
const fs = require("fs");
const path = require("path");

const root = path.resolve(__dirname, "..");
const portalApi = fs.readFileSync(path.join(root, "assets/js/portal-api.js"), "utf8");
const program = fs.readFileSync(path.join(root, "网页启动登录", "SapWebLauncher", "Program.cs"), "utf8");

assert.match(portalApi, /if \(!owner\) return true;/);
assert.match(portalApi, /if \(!createdBy && !updatedBy\) return true;/);
assert.match(program, /OR \(trim\(created_by\)='' AND trim\(updated_by\)=''\)/);

console.log("SCHEDULE_OWNER_VISIBILITY_CONTRACT_OK");
