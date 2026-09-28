const fs = require('fs');
const path = require('path');

const script = fs.readFileSync(
  path.join(__dirname, '..', '网页启动登录', 'transactions', 'ZCO020.vbs'),
  'utf8'
);

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

const rowCount = script.indexOf('filteredRowCount = GetFilteredGridRowCount()');
const noDataBranch = script.indexOf('If filteredRowCount <= 0 Then', rowCount);
const marker = script.indexOf('ZCO020_FILTERED_NO_DATA=1', noDataBranch);
const selectAll = script.indexOf('SelectAllGrid filteredRowCount', noDataBranch);
const save = script.indexOf('save/export selected rows', selectAll);

assert(rowCount >= 0, 'ZCO020 must read the filtered ALV row count.');
assert(noDataBranch > rowCount, 'ZCO020 must branch on an empty filtered ALV.');
assert(marker > noDataBranch, 'ZCO020 must emit the explicit no-data marker.');
assert(selectAll > marker, 'ZCO020 must select rows only after the no-data branch.');
assert(save > selectAll, 'ZCO020 must save only after selecting filtered rows.');

console.log('PASS zco020 filtered no-data contract');
