const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const root = path.resolve(__dirname, '..');
const transactionsDir = path.join(root, '网页启动登录', 'transactions');

test('every executable transaction has a packaged VBS script', () => {
  const config = JSON.parse(
    fs.readFileSync(path.join(transactionsDir, 'transaction-config.json'), 'utf8').replace(/^\uFEFF/, ''),
  );
  const executable = config.transactions.filter((item) => item.automation === 'script');
  const missing = executable
    .filter((item) => !item.script || !fs.existsSync(path.join(transactionsDir, item.script)))
    .map((item) => item.code + ' -> ' + (item.script || '(empty)'));

  assert.deepEqual(missing, []);
  assert.ok(executable.length > 0);
});

test('open-only cards do not require fake VBS files', () => {
  const config = JSON.parse(
    fs.readFileSync(path.join(transactionsDir, 'transaction-config.json'), 'utf8').replace(/^\uFEFF/, ''),
  );
  const openOnly = config.transactions.filter((item) => item.automation === 'openOnly');
  assert.ok(openOnly.length > 0);
});
