const assert = require("assert");
const fs = require("fs");
const path = require("path");

const root = path.resolve(__dirname, "..");
const fetcher = fs.readFileSync(path.join(root, "网页启动登录", "SapWebLauncher", "Zfi019NlMemoryFetcher.cs"), "utf8");
const program = fs.readFileSync(path.join(root, "网页启动登录", "SapWebLauncher", "Program.cs"), "utf8");
const vbs = fs.readFileSync(path.join(root, "网页启动登录", "transactions", "ZFI019NL.vbs"), "utf8");

assert.match(fetcher, /CompanyCodeRequiredBusinessAreas[\s\S]*?"1440", "1180", "4330"/);
assert.match(fetcher, /BusinessAreaCompanyCodeTable = "ZFIT_RPA_BUKRS"/);
assert.match(fetcher, /new\[\] \{ "GSBER", CompanyCodeField \}/);
assert.match(fetcher, /BuildOptions\(request, memoryId, memoryName, companyCode\.CompanyCode\)/);
assert.match(fetcher, /conditions\.RemoveAll\(x => IsCompanyCode\(x\.Selname\)\)/);
assert.match(fetcher, /Selname = "S_BUKRS"[\s\S]*?Low = companyCode\.Trim\(\)/);
assert.match(fetcher, /specialAreas\.Length == 0[\s\S]*?Success = true/);

assert.match(program, /p\.TCode\.Equals\("ZFI019NL"[\s\S]*?RequiresSpecialCompanyCode\(businessArea\)/);
assert.match(program, /NormalizeStringArray\(FirstNonEmpty\(p\.BusinessAreas, p\.BusinessArea\)\)/);
assert.match(program, /ResolveSpecialCompanyCode\([\s\S]*?new\[\] \{ businessArea \}/);
assert.match(program, /\.Replace\("\{COMPANY_CODE\}", VbsEscape\(companyCode\)\)/);

assert.match(vbs, /companyCodeValue = "\{COMPANY_CODE\}"/);
assert.match(vbs, /If Trim\(CStr\(companyCodeValue\)\) <> "" Then SetField "bukrs-low", "wnd\[0\]\/usr\/ctxtS_BUKRS-LOW", companyCodeValue/);
assert.doesNotMatch(vbs, /companyCodeValue\s*=\s*"5090"/);

console.log("ZFI019NL_SPECIAL_COMPANY_CODE_CONTRACT_OK");
