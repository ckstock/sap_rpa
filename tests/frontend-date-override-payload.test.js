const assert = require("assert");
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const repoRoot = path.resolve(__dirname, "..");
const scripts = [
  "assets/js/portal-state.js",
  "assets/js/portal-utils.js",
  "assets/js/portal-api.js",
  "assets/js/portal-render.js",
  "assets/js/portal-actions.js"
];

const context = {
  console,
  URL,
  URLSearchParams,
  TextDecoder,
  Uint8Array,
  atob: value => Buffer.from(value, "base64").toString("binary"),
  setTimeout: () => 0,
  clearTimeout: () => {},
  Blob: function Blob() {},
  location: { href: "http://localhost/rpa/" },
  localStorage: {
    data: new Map(),
    getItem(key) { return this.data.has(key) ? this.data.get(key) : null; },
    setItem(key, value) { this.data.set(key, String(value)); },
    removeItem(key) { this.data.delete(key); }
  },
  __inputs: Object.create(null),
  document: {
    title: "SAP RPA",
    getElementById(id) {
      return context.__inputs[id] || null;
    },
    querySelectorAll() {
      return [];
    },
    createElement() {
      return { click() {}, style: {}, setAttribute() {} };
    },
    body: { appendChild() {}, removeChild() {} }
  },
  window: {
    self: null,
    top: null,
    history: { replaceState() {} },
    location: { href: "http://localhost/rpa/" }
  },
  navigator: {},
  lucide: { createIcons() {} }
};
context.window.self = context.window;
context.window.top = context.window;
context.globalThis = context;

vm.createContext(context);
for (const script of scripts) {
  vm.runInContext(fs.readFileSync(path.join(repoRoot, script), "utf8"), context, { filename: script });
}

async function runInPortal(code) {
  return vm.runInContext(`(async () => { ${code} })()`, context);
}

async function captureRunParams(setup) {
  return runInPortal(`
    ${setup}
    let captured = null;
    bridgeFetch = async (path, options = {}) => {
      captured = JSON.parse(options.body);
      return { runId: "RUN-FRONTEND-TEST" };
    };
    await createBridgeRun(buildRunPayload());
    return captured.params;
  `);
}

(async () => {
  const test888Payload = await captureRunParams(`
    state.bridge.allowTestDateOverride = true;
    state.bridge.defaultExecutionDateRange = { period: "2026.07.27", weekEnd: "2026.08.02", year: 2026, week: 31 };
    state.form.tCode = "ZFI057";
    state.form.plants = ["2800"];
    state.form.factoryGroup = "PINGHU_ALL";
    state.form.useTestDateOverride = true;
    state.form.testDateKind = "week";
    state.form.testIsoWeek = "2026-W18";
    state.form.notify = true;
    state.form.useDefaultNotifyUser = true;
  `);
  assert.equal(test888Payload.dateMode, "testOverride");
  assert.equal(test888Payload.testDateMode, "testOverride");
  assert.equal(test888Payload.testDateKind, "week");
  assert.equal(test888Payload.testIsoWeek, "2026-W18");
  assert.equal(test888Payload.period, "2026.04.27");
  assert.equal(test888Payload.weekEnd, "2026.05.03");
  assert.equal(test888Payload.year, "2026");
  assert.equal(test888Payload.week, "18");
  assert.equal(test888Payload.runStrategy, "auto3step");

  const normalizedWeekFormats = await runInPortal(`
    return ["2026W18", "2026-18", "2026 18", "2026年18周"].map(normalizeIsoWeekText);
  `);
  assert.deepEqual([...normalizedWeekFormats], ["2026-W18", "2026-W18", "2026-W18", "2026-W18"]);

  const rangePreviewMarkup = await runInPortal(`
    state.bridge.allowTestDateOverride = true;
    state.form.tCode = "ZFI057";
    state.form.useTestDateOverride = true;
    state.form.testDateKind = "range";
    state.form.testDateStart = "2026-04-27";
    state.form.testDateEnd = "2026-05-03";
    return renderTestDateOverrideControl({ tcode: "ZFI057" });
  `);
  assert.match(rangePreviewMarkup, /测试周<input id="testIsoWeek"[^>]*value="2026-W18"[^>]*readonly/);
  assert.match(rangePreviewMarkup, /开始日期<input id="testDateStart"[^>]*value="2026-04-27"(?![^>]*readonly)/);
  assert.match(rangePreviewMarkup, /截止日期<input id="testDateEnd"[^>]*value="2026-05-03"(?![^>]*readonly)/);

  const weekPreviewMarkup = await runInPortal(`
    state.bridge.allowTestDateOverride = true;
    state.form.tCode = "ZFI057";
    state.form.useTestDateOverride = true;
    state.form.testDateKind = "week";
    state.form.testIsoWeek = "2026-W18";
    return renderTestDateOverrideControl({ tcode: "ZFI057" });
  `);
  assert.match(weekPreviewMarkup, /测试周<input id="testIsoWeek"[^>]*value="2026-W18"(?![^>]*readonly)/);
  assert.match(weekPreviewMarkup, /开始日期<input id="testDateStart"[^>]*value="2026-04-27"[^>]*readonly/);
  assert.match(weekPreviewMarkup, /截止日期<input id="testDateEnd"[^>]*value="2026-05-03"[^>]*readonly/);

  const scheduleRangePreviewMarkup = await runInPortal(`
    state.bridge.allowTestDateOverride = true;
    state.scheduleForm.tCode = "ZFI057";
    state.scheduleForm.useTestDateOverride = true;
    state.scheduleForm.testDateKind = "range";
    state.scheduleForm.testDateStart = "2026-04-27";
    state.scheduleForm.testDateEnd = "2026-05-03";
    return renderScheduleTestDateOverrideControl("ZFI057");
  `);
  assert.match(scheduleRangePreviewMarkup, /测试周<input id="scheduleTestIsoWeek"[^>]*value="2026-W18"[^>]*readonly/);
  assert.match(scheduleRangePreviewMarkup, /开始日期<input id="scheduleTestDateStart"[^>]*value="2026-04-27"(?![^>]*readonly)/);
  assert.match(scheduleRangePreviewMarkup, /截止日期<input id="scheduleTestDateEnd"[^>]*value="2026-05-03"(?![^>]*readonly)/);

  const scheduleDateOnlyMarkup = await runInPortal(`
    state.bridge.allowTestDateOverride = true;
    state.scheduleForm.tCode = "ZFI072N";
    state.scheduleForm.useTestDateOverride = true;
    state.scheduleForm.testDateKind = "week";
    state.scheduleForm.testDateStart = "2026-04-27";
    state.scheduleForm.testDateEnd = "2026-05-03";
    return renderScheduleTestDateOverrideControl("ZFI072N");
  `);
  assert.doesNotMatch(scheduleDateOnlyMarkup, /id="scheduleTestDateKind"/);
  assert.doesNotMatch(scheduleDateOnlyMarkup, /id="scheduleTestIsoWeek"/);
  assert.match(scheduleDateOnlyMarkup, /id="scheduleTestDateStart"[^>]*value="2026-04-27"(?![^>]*readonly)/);
  assert.match(scheduleDateOnlyMarkup, /id="scheduleTestDateEnd"[^>]*value="2026-05-03"(?![^>]*readonly)/);

  const dateControlCodes = [
    "ZFI072A", "ZFI072N", "ZFI057", "ZCO020", "ZFI080", "ZCO019",
    "ZFI019NA", "ZFIR034", "ZFI019NL", "ZFI080B", "ZFI148"
  ];
  const dateControlMarkup = await runInPortal(`
    state.bridge.allowTestDateOverride = true;
    state.form.useTestDateOverride = true;
    state.form.testDateKind = "week";
    return ${JSON.stringify(dateControlCodes)}.map(code => [code, renderTestDateOverrideControl({ tcode: code })]);
  `);
  dateControlMarkup.forEach(([code, markup]) => {
    assert.match(markup, /id="useTestDateOverride"/, `${code} must show the test-date control`);
    assert.match(markup, /id="testDateStart"/, `${code} must show the start-date preview`);
    assert.match(markup, /id="testDateEnd"/, `${code} must show the end-date preview`);
  });

  const weekInputCodes = ["ZFI072A", "ZFI057", "ZFIR034", "ZFI148"];
  const dateOnlyCodes = dateControlCodes.filter(code => !weekInputCodes.includes(code));
  const dateInputMarkup = await runInPortal(`
    return {
      week: ${JSON.stringify(weekInputCodes)}.map(code => [code, renderTestDateOverrideControl({ tcode: code })]),
      dateOnly: ${JSON.stringify(dateOnlyCodes)}.map(code => [code, renderTestDateOverrideControl({ tcode: code })])
    };
  `);
  dateInputMarkup.week.forEach(([code, markup]) => {
    assert.match(markup, /id="testDateKind"/, `${code} must show the source selector`);
    assert.match(markup, /id="testIsoWeek"/, `${code} must show the ISO week input`);
  });
  dateInputMarkup.dateOnly.forEach(([code, markup]) => {
    assert.doesNotMatch(markup, /id="testDateKind"/, `${code} must not show the source selector`);
    assert.doesNotMatch(markup, /id="testIsoWeek"/, `${code} must not show an ISO week input`);
  });

  const dateOnlyPayload = await captureRunParams(`
    state.bridge.allowTestDateOverride = true;
    state.form.tCode = "ZFI072N";
    state.form.plants = ["2800"];
    state.form.factoryGroup = "PINGHU_ALL";
    state.form.useTestDateOverride = true;
    state.form.testDateKind = "week";
    state.form.testIsoWeek = "2026-W18";
    state.form.testDateStart = "2026-04-27";
    state.form.testDateEnd = "2026-05-03";
    state.form.notify = true;
    state.form.useDefaultNotifyUser = true;
  `);
  assert.equal(dateOnlyPayload.testDateKind, "range");
  assert.equal(Object.prototype.hasOwnProperty.call(dateOnlyPayload, "testIsoWeek"), false);
  assert.equal(dateOnlyPayload.period, "2026.04.27");
  assert.equal(dateOnlyPayload.weekEnd, "2026.05.03");
  assert.equal(dateOnlyPayload.year, "2026");
  assert.equal(dateOnlyPayload.week, "18");

  const prodPayload = await captureRunParams(`
    state.bridge.allowTestDateOverride = false;
    state.form.tCode = "ZFI057";
    state.form.plants = ["2800"];
    state.form.factoryGroup = "PINGHU_ALL";
    state.form.useTestDateOverride = true;
    state.form.testDateKind = "week";
    state.form.testIsoWeek = "2026-W18";
    state.form.notify = true;
    state.form.useDefaultNotifyUser = true;
  `);
  assert.equal(prodPayload.runStrategy, "auto3step");
  ["dateMode", "testDateMode", "testDateKind", "testIsoWeek", "period", "weekEnd", "year", "week"].forEach(key => {
    assert.equal(Object.prototype.hasOwnProperty.call(prodPayload, key), false, `prod payload must not include ${key}`);
  });

  const protocol = await runInPortal(`
    state.bridge.allowTestDateOverride = false;
    state.form.tCode = "ZFI057";
    state.form.plants = ["2800"];
    state.form.factoryGroup = "PINGHU_ALL";
    state.form.useTestDateOverride = true;
    state.form.testIsoWeek = "2026-W18";
    return buildProtocolUrl("RUN-PROTOCOL-TEST");
  `);
  assert.equal(protocol.includes("dateMode=testOverride"), false);
  assert.equal(protocol.includes("period=2026.04.27"), false);
  assert.equal(protocol.includes("weekEnd=2026.05.03"), false);
  assert.equal(protocol.includes("runStrategy=auto3step"), true);

  const schedulePayload = await runInPortal(`
    state.bridge.allowTestDateOverride = true;
    state.bridge.defaultExecutionDateRange = { period: "2026.07.27", weekEnd: "2026.08.02", year: 2026, week: 31 };
    state.scheduleForm = {
      id: "SCH-FRONTEND-TEST",
      tCode: "ZFI057",
      factoryGroup: "PINGHU_ALL",
      plants: ["2800"],
      useTestDateOverride: true,
      testDateKind: "range",
      testDateStart: "2026-04-27",
      testDateEnd: "2026-05-03",
      notifyStart: false,
      notifySuccess: false,
      notifyFail: false,
      enabled: true
    };
    __inputs = {
      scheduleTCode: { value: "ZFI057" },
      scheduleFactoryGroup: { value: "PINGHU_ALL" },
      schedulePlants: { value: "2800" },
      scheduleFrequency: { value: "weekly" },
      scheduleEnabled: { checked: true },
      scheduleTime: { value: "08:00" },
      scheduleName: { value: "SCH-FRONTEND-TEST" },
      scheduleUseTestDateOverride: { checked: true },
      scheduleTestDateKind: { value: "range" },
      scheduleTestDateStart: { value: "2026-04-27" },
      scheduleTestDateEnd: { value: "2026-05-03" },
      scheduleNotifyStart: { checked: false },
      scheduleNotifySuccess: { checked: false },
      scheduleNotifyFail: { checked: false }
    };
    return buildScheduleConfigPayload();
  `);
  assert.equal(schedulePayload.params.dateMode, "testOverride");
  assert.equal(schedulePayload.params.testDateMode, "testOverride");
  assert.equal(schedulePayload.params.testDateKind, "range");
  assert.equal(schedulePayload.params.period, "2026.04.27");
  assert.equal(schedulePayload.params.weekEnd, "2026.05.03");
  assert.equal(schedulePayload.params.year, "2026");
  assert.equal(schedulePayload.params.week, "18");
  assert.equal(schedulePayload.params.runStrategy, "auto3step");

  console.log("FRONTEND_DATE_OVERRIDE_PAYLOAD_OK");
})().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
