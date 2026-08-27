const assert = require('assert');
const fs = require('fs');

const program = fs.readFileSync(
  'D:/RPA/RpaProject/网页启动登录/SapWebLauncher/Program.cs',
  'utf8'
);

assert.match(program, /step1Fetch\.Materials\.Length == 0[\s\S]*?IsNoDataRunStatus\(step1Fetch\.Result\.Status\)/);
assert.match(program, /"no_material"[\s\S]*?"zfi057_no_data"[\s\S]*?"zco020_no_data"/);
assert.match(program, /zco020_no_data[\s\S]*?运行成功（ZCO020过滤后无数据）/);
assert.match(program, /success_with_no_data[\s\S]*?运行成功（部分工厂无数据）/);

const formatterStart = program.indexOf('static string FormatZfi057ScopeResultForMessage');
const formatterEnd = program.indexOf('static bool IsZfi057ScopeNoDataStatus', formatterStart);
const formatter = program.slice(formatterStart, formatterEnd);
assert.match(formatter, /no_material[\s\S]*?没物料/);
assert.doesNotMatch(formatter, /zco020_no_data[\s\S]*?没物料/);

console.log('zfi057 scope summary contract: passed');
