const assert = require('assert');
const fs = require('fs');

const fetcher = fs.readFileSync(
  'D:/RPA/RpaProject/网页启动登录/SapWebLauncher/Zfi019NlMemoryFetcher.cs',
  'utf8'
);
const program = fs.readFileSync(
  'D:/RPA/RpaProject/网页启动登录/SapWebLauncher/Program.cs',
  'utf8'
);

assert.match(fetcher, /dongtaiBusinessAreas\.Contains\(area\)\)[\s\S]*?StartsWith\("800"/);
assert.match(fetcher, /dongtaiBusinessAreas\.Contains\(area\)\)\s*keep\s*=\s*Normalize\(values\[inboundIndex\]\)\.StartsWith\("800"/);
assert.doesNotMatch(fetcher, /dongtaiBusinessAreas\.Contains\(area\)\)\s*keep\s*=\s*Normalize\(values\[productIndex\]\)\.StartsWith\("800"/);
assert.match(fetcher, /areaIndex < 0 && dongtaiRequested\)\s*keep\s*=\s*Normalize\(values\[inboundIndex\]\)\.StartsWith\("800"/);
assert.match(fetcher, /if \(!isDongtai\) return \(true, ""\);/);
assert.match(fetcher, /BUKRS = '\{EscapeSqlLiteral\(bukrs\)\}'/);
assert.match(fetcher, /AND WERKS = '\{EscapeSqlLiteral\(werks\)\}'/);
assert.match(fetcher, /AddFinalMaterial\(finalRows, matnr, source\)/);
assert.match(program, /ExecuteZfi057Step1Memory\(p, area, plants\)/);
assert.doesNotMatch(program, /ExecuteZfi057Step1Memory\(p, area, Array\.Empty<string>\(\)\)/);

console.log('zfi057 Dongtai material contract: passed');
