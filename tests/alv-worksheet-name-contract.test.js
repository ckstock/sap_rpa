const assert = require("assert");
const fs = require("fs");
const path = require("path");

const root = path.resolve(__dirname, "..");
const exportCode = fs.readFileSync(
  path.join(root, "网页启动登录", "SapWebLauncher", "AlvOrganizationExport.cs"),
  "utf8"
);
const programCode = fs.readFileSync(
  path.join(root, "网页启动登录", "SapWebLauncher", "Program.cs"),
  "utf8"
);

assert.match(exportCode, /public static string NormalizeWorksheetName\(string value\)/);
assert.match(exportCode, /Worksheets\.Add\(NormalizeWorksheetName\(worksheetName\)\)/);
assert.match(exportCode, /normalized\.Length <= 31 \? normalized : normalized\[\.\.31\]/);
assert.match(programCode, /static string BuildAlvWorksheetName\(SapRunParams p\)/);
assert.match(programCode, /string\.Join\("_", new\[\] \{ personnelNumber, operatorName \}/);
assert.match(programCode, /pars\.OperatorId = request\.Operator\?\.Id/);
assert.match(programCode, /pars\.OperatorName = request\.Operator\?\.Name/);
assert.match(programCode, /worksheetName: BuildAlvWorksheetName\(p\)/);

console.log("ALV_WORKSHEET_NAME_CONTRACT_OK");
