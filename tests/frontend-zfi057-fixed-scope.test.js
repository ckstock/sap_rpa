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
    getElementById(id) { return context.__inputs[id] || null; },
    querySelectorAll() { return []; },
    createElement() { return { click() {}, style: {}, setAttribute() {} }; },
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

(async () => {
  const result = await runInPortal(`
    const selectedAreas = ["5100", "2790"];
    state.user.name = "Portal Tester";
    applyConfigData({
      transactions: [{
        code: "ZFI057",
        name: "ZFI057",
        script: "ZFI057.vbs",
        params: ["businessAreas", "year", "week", "period", "weekEnd"],
        defaultPlantGroup: "PINGHU_ALL",
        enabled: true
      }],
      transactionRules: [{
        code: "ZFI057",
        name: "ZFI057",
        defaultPlantGroup: "PINGHU_ALL",
        businessAreaMode: "fixed",
        businessAreas: ["2790", "2800"],
        enabled: true
      }],
      plants: [],
      plantGroups: [{ id: "PINGHU_ALL", name: "Pinghu", plants: [], enabled: true }],
      notificationRobots: [],
      scheduleTasks: [{
        id: "SCH-004",
        tCode: "ZFI057",
        factoryGroup: "PINGHU_ALL",
        paramsJson: JSON.stringify({ businessAreas: selectedAreas.join(","), factoryGroup: "PINGHU_ALL" }),
        createdBy: "Portal Tester",
        updatedBy: "Portal Tester",
        enabled: true
      }]
    });

    state.form.tCode = "ZFI057";
    state.form.factoryGroup = "PINGHU_ALL";
    state.form.plants = selectedAreas;
    state.form.notify = false;
    state.scheduleForm = {
      id: "SCH-004",
      tCode: "ZFI057",
      factoryGroup: "PINGHU_ALL",
      plants: selectedAreas,
      weekday: "friday",
      enabled: true,
      notifyStart: false,
      notifySuccess: false,
      notifyFail: false
    };
    state.modal = "schedule";
    __inputs = {
      scheduleTCode: { value: "ZFI057" },
      scheduleFactoryGroup: { value: "PINGHU_ALL" },
      schedulePlants: { value: selectedAreas.join(",") },
      scheduleFrequency: { value: "weekly" },
      scheduleWeekday: { value: "friday" },
      scheduleEnabled: { checked: true },
      scheduleTime: { value: "08:00" },
      scheduleName: { value: "SCH-004" },
      scheduleUseTestDateOverride: { checked: false },
      scheduleNotifyStart: { checked: false },
      scheduleNotifySuccess: { checked: false },
      scheduleNotifyFail: { checked: false }
    };

    return {
      transaction: getTCode("ZFI057"),
      schedule: scheduleTasks[0],
      run: buildRunPayload(),
      savedSchedule: buildScheduleConfigPayload(),
      normalizedWeekly: normalizeScheduleTaskFromApi({
        id: "SCH-WEEKDAY",
        tCode: "ZFI057",
        factoryGroup: "PINGHU_ALL",
        frequency: "weekly",
        weekday: "friday",
        enabled: true
      }),
      normalizedMonthly: normalizeScheduleTaskFromApi({
        id: "SCH-MONTHLY-WEEKDAY",
        tCode: "ZFI057",
        factoryGroup: "PINGHU_ALL",
        frequency: "monthly",
        weekday: "friday",
        enabled: true
      }),
      executionMarkup: renderExecutePlants(),
      scheduleMarkup: renderModal()
    };
  `);

  assert.deepEqual([...result.transaction.businessAreas], ["2790", "2800"]);
  assert.deepEqual([...result.schedule.plants], ["5100", "2790"]);
  assert.deepEqual([...result.run.businessAreas], ["5100", "2790"]);
  assert.deepEqual([...result.run.plants], []);
  assert.deepEqual([...result.savedSchedule.businessAreas], ["5100", "2790"]);
  assert.deepEqual([...result.savedSchedule.plants], []);
  assert.equal(result.savedSchedule.time, "08:00");
  assert.equal(result.savedSchedule.weekday, "friday");
  assert.equal(result.savedSchedule.frequencyText, "每周五");
  assert.equal(result.savedSchedule.params.businessAreas, "5100,2790");
  assert.equal(Object.prototype.hasOwnProperty.call(result.savedSchedule.params, "plants"), true);
  assert.equal(result.savedSchedule.params.plants, "");
  assert.equal(Object.prototype.hasOwnProperty.call(result.savedSchedule.params, "zfi057PlantFilter"), false);
  assert.match(result.executionMarkup, /5100/);
  assert.match(result.executionMarkup, /2790/);
  assert.doesNotMatch(result.executionMarkup, /2800/);
  assert.match(result.scheduleMarkup, /id="schedulePlants" value="5100,2790"/);
  assert.match(result.scheduleMarkup, /id="scheduleWeekday"/);
  assert.match(result.scheduleMarkup, /新增业务范围代码/);
  assert.doesNotMatch(result.scheduleMarkup, /scheduleZfi057PlantFilter/);
  assert.match(result.scheduleMarkup, /data-action="remove-schedule-plant"/);
  assert.equal(result.normalizedWeekly.weekday, "friday");
  assert.equal(result.normalizedWeekly.frequency, "每周五");
  assert.equal(result.normalizedWeekly.time, "20:00");
  assert.equal(result.normalizedMonthly.weekday, "friday");
  assert.equal(result.normalizedMonthly.frequency, "\u6BCF\u6708\u9996\u4E2A\u5468\u4E94");
  assert.equal(result.normalizedMonthly.time, "20:00");
  assert.equal(await runInPortal('return isSaveActionTransaction({ code: "ZFI057", name: "产值拆分" })'), true);

  const monthlyPayload = await runInPortal(`
    state.scheduleForm = {
      id: "",
      name: "SCH-MONTHLY",
      tCode: "ZFI057",
      factoryGroup: "PINGHU_ALL",
      plants: ["5100", "2790"],
      execTime: "09:30",
      frequency: "monthly",
      weekday: "friday",
      enabled: true,
      notifyStart: false,
      notifySuccess: false,
      notifyFail: false
    };
    __inputs = {
      scheduleTCode: { value: "ZFI057" },
      scheduleFactoryGroup: { value: "PINGHU_ALL" },
      schedulePlants: { value: "5100,2790" },
      scheduleFrequency: { value: "monthly" },
      scheduleWeekday: { value: "friday" },
      scheduleEnabled: { checked: true },
      scheduleTime: { value: "09:30" },
      scheduleName: { value: "SCH-MONTHLY" },
      scheduleUseTestDateOverride: { checked: false },
      scheduleNotifyStart: { checked: false },
      scheduleNotifySuccess: { checked: false },
      scheduleNotifyFail: { checked: false }
    };
    return buildScheduleConfigPayload();
  `);
  assert.equal(monthlyPayload.frequency, "monthly");
  assert.equal(monthlyPayload.weekday, "friday");
  assert.equal(monthlyPayload.frequencyText, "\u6BCF\u6708\u9996\u4E2A\u5468\u4E94");

  const legacyMonthlyMarkup = await runInPortal(`
    state.scheduleForm = {
      id: "SCH-LEGACY-MONTHLY",
      name: "SCH-LEGACY-MONTHLY",
      tCode: "ZFI057",
      factoryGroup: "PINGHU_ALL",
      plants: ["5100"],
      execTime: "09:30",
      frequency: "monthly",
      weekday: "",
      enabled: true,
      notifyStart: false,
      notifySuccess: false,
      notifyFail: false
    };
    state.modal = "schedule";
    return renderModal();
  `);
  assert.match(legacyMonthlyMarkup, /id="scheduleWeekday"/);
  assert.match(legacyMonthlyMarkup, /\u6309\u539F\u6BCF\u6708\u65E5\u671F/);

  const legacyMonthlyPayload = await runInPortal(`
    state.scheduleForm = {
      id: "SCH-LEGACY-MONTHLY",
      name: "SCH-LEGACY-MONTHLY",
      tCode: "ZFI057",
      factoryGroup: "PINGHU_ALL",
      plants: ["5100"],
      execTime: "09:30",
      frequency: "monthly",
      weekday: "",
      enabled: true,
      notifyStart: false,
      notifySuccess: false,
      notifyFail: false
    };
    __inputs = {
      scheduleTCode: { value: "ZFI057" },
      scheduleFactoryGroup: { value: "PINGHU_ALL" },
      schedulePlants: { value: "5100" },
      scheduleFrequency: { value: "monthly" },
      scheduleWeekday: { value: "" },
      scheduleEnabled: { checked: true },
      scheduleTime: { value: "09:30" },
      scheduleName: { value: "SCH-LEGACY-MONTHLY" },
      scheduleUseTestDateOverride: { checked: false },
      scheduleNotifyStart: { checked: false },
      scheduleNotifySuccess: { checked: false },
      scheduleNotifyFail: { checked: false }
    };
    return buildScheduleConfigPayload();
  `);
  assert.equal(legacyMonthlyPayload.weekday, "");
  assert.equal(legacyMonthlyPayload.frequencyText, "\u6BCF\u6708");

  const legacyMonthlyRevertPayload = await runInPortal(`
    state.scheduleForm = {
      id: "SCH-LEGACY-MONTHLY",
      name: "SCH-LEGACY-MONTHLY",
      tCode: "ZFI057",
      factoryGroup: "PINGHU_ALL",
      plants: ["5100"],
      execTime: "09:30",
      frequency: "monthly",
      weekday: "friday",
      enabled: true,
      notifyStart: false,
      notifySuccess: false,
      notifyFail: false
    };
    __inputs = {
      scheduleTCode: { value: "ZFI057" },
      scheduleFactoryGroup: { value: "PINGHU_ALL" },
      schedulePlants: { value: "5100" },
      scheduleFrequency: { value: "monthly" },
      scheduleWeekday: { value: "" },
      scheduleEnabled: { checked: true },
      scheduleTime: { value: "09:30" },
      scheduleName: { value: "SCH-LEGACY-MONTHLY" },
      scheduleUseTestDateOverride: { checked: false },
      scheduleNotifyStart: { checked: false },
      scheduleNotifySuccess: { checked: false },
      scheduleNotifyFail: { checked: false }
    };
    return buildScheduleConfigPayload();
  `);
  assert.equal(legacyMonthlyRevertPayload.weekday, "");
  assert.equal(legacyMonthlyRevertPayload.frequencyText, "\u6BCF\u6708");

  const newScheduleWeekday = await runInPortal(`
    state.scheduleForm = {
      id: "SCH-OLD-FRIDAY",
      tCode: "ZFI057",
      factoryGroup: "PINGHU_ALL",
      plants: ["2800"],
      execTime: "09:00",
      frequency: "weekly",
      weekday: "friday"
    };
    openScheduleModal("");
    return {
      weekday: state.scheduleForm.weekday,
      execTime: state.scheduleForm.execTime
    };
  `);
  assert.equal(newScheduleWeekday.weekday, "monday");
  assert.equal(newScheduleWeekday.execTime, "20:00");

  const businessAreaInteraction = await runInPortal(`
    state.scheduleForm.tCode = "ZFI057";
    state.scheduleForm.plants = ["5100"];
    __inputs = {
      schedulePlants: { value: "5100" },
      schedulePlantAdd: { value: "2790" }
    };
    addSchedulePlant();
    removeSchedulePlant("5100");
    return {
      businessAreas: state.scheduleForm.plants,
      hiddenValue: __inputs.schedulePlants.value,
      addValue: __inputs.schedulePlantAdd.value
    };
  `);
  assert.deepEqual([...businessAreaInteraction.businessAreas], ["2790"]);
  assert.equal(businessAreaInteraction.hiddenValue, "2790");
  assert.equal(businessAreaInteraction.addValue, "");

  console.log("FRONTEND_ZFI057_BUSINESS_AREA_SCOPE_OK");
})().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
