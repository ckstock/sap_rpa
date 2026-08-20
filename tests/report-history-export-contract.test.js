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
  Blob: function Blob(parts, options) {
    this.parts = parts;
    this.options = options;
  },
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
    createElement(tag) {
      if (tag === "a") {
        return {
          href: "",
          download: "",
          click() { this.clicked = true; },
          setAttribute() {},
          style: {}
        };
      }
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

let capturedBlob = null;
let createdLink = null;
const calls = [];
context.toast = () => {};
context.URL.createObjectURL = blob => {
  capturedBlob = blob;
  return "blob:mock";
};
context.URL.revokeObjectURL = () => {};
context.document.createElement = tag => {
  if (tag === "a") {
    createdLink = {
      href: "",
      download: "",
      clicked: false,
      click() { this.clicked = true; },
      setAttribute() {},
      style: {}
    };
    return createdLink;
  }
  return { click() {}, style: {}, setAttribute() {} };
};
context.bridgeFetch = async path => {
  calls.push(path);
  const url = new URL("http://localhost" + path);
  if (url.pathname !== "/api/runs") {
    throw new Error("Unexpected path: " + path);
  }

  const offset = Number(url.searchParams.get("offset") || "0");
  const makeRun = (runId, requestJson, transactionCode = "ZFI019NL") => ({
    runId,
    transactionCode,
    operatorName: "测试用户",
    status: "success",
    requestJson,
    durationMs: 1234,
    message: "ok",
    sapStatusText: "ok",
    startedAt: "2026-08-19 10:00:00",
    finishedAt: "2026-08-19 10:00:01",
    logs: [],
    files: []
  });

  if (offset === 0) {
    const firstPage = Array.from({ length: 200 }, (_, index) => {
      if (index === 0) {
        return makeRun("RUN-1", JSON.stringify({ params: { plants: "1022,1032", factoryGroup: "PINGHU_ALL" } }), "ZFI072A");
      }
      if (index === 1) {
        return makeRun("RUN-2", JSON.stringify({ params: { businessAreas: "2900,9200", factoryGroup: "PINGHU_ALL" } }), "ZFI019NL");
      }
      return makeRun(`RUN-${index + 1}`, JSON.stringify({ params: { plants: "1022", factoryGroup: "PINGHU_ALL" } }), "ZFI072A");
    });
    return {
      runs: firstPage
    };
  }

  if (offset === 200) {
    return {
      runs: [
        makeRun("RUN-3", JSON.stringify({ params: { businessAreas: "5100,2790", factoryGroup: "PINGHU_ALL" } }), "ZFI019NA")
      ]
    };
  }

  return { runs: [] };
};

(async () => {
  vm.runInContext(`
    state.bridge.online = true;
    __inputs.reportStart = { value: "2026-08-01" };
    __inputs.reportEnd = { value: "2026-08-31" };
  `, context);
  await vm.runInContext("(async () => { await exportHistory(); })()", context);

  assert.equal(calls.length, 2, "exportHistory should page through all run records");
  assert.match(calls[0], /offset=0/);
  assert.match(calls[1], /offset=200/);
  assert.ok(capturedBlob, "CSV blob should be created");
  const csv = String(capturedBlob.parts[0] || "");
  assert.match(csv, /任务ID/);
  assert.match(csv, /工厂入参/);
  assert.match(csv, /业务范围入参/);
  assert.match(csv, /执行参数/);
  assert.match(csv, /1022,1032/);
  assert.match(csv, /2900,9200/);
  assert.match(csv, /5100,2790/);
  assert.equal(createdLink.download, "sap-rpa-history.csv");
  assert.equal(createdLink.clicked, true);
  console.log("REPORT_HISTORY_EXPORT_CONTRACT_OK");
})().catch(err => {
  console.error(err);
  process.exitCode = 1;
});
