    const icon = (name, cls = "icon") => `<i class="${cls}" data-lucide="${name}"></i>`;
    const esc = value => String(value ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
    const app = document.getElementById("app");

    function clonePlain(value) {
      return JSON.parse(JSON.stringify(value));
    }

    function toArray(value) {
      if (Array.isArray(value)) return value.map(item => String(item).trim()).filter(Boolean);
      if (typeof value === "string") return value.split(/[,\n，、;]/).map(item => item.trim()).filter(Boolean);
      return [];
    }

    function boolValue(value, fallback = true) {
      if (typeof value === "boolean") return value;
      if (typeof value === "number") return value !== 0;
      if (typeof value === "string") return !["false", "0", "停用", "disabled"].includes(value.toLowerCase());
      return fallback;
    }

    function formatSapDateValue(date) {
      const year = date.getFullYear();
      const month = String(date.getMonth() + 1).padStart(2, "0");
      const day = String(date.getDate()).padStart(2, "0");
      return `${year}.${month}.${day}`;
    }

    function sapDateToInputValue(value) {
      const text = String(value || "").trim();
      const match = text.match(/^(\d{4})[.-](\d{1,2})[.-](\d{1,2})$/);
      if (!match) return "";
      return `${match[1]}-${match[2].padStart(2, "0")}-${match[3].padStart(2, "0")}`;
    }

    function inputDateToSapDate(value) {
      const input = sapDateToInputValue(value);
      return input ? input.replace(/-/g, ".") : "";
    }

    // This follows actual VBS placeholder consumption and SAP fields, not stale @params
    // comments. ZFI057 exposes its ISO week and date range together because its VBS
    // accepts year/week as well as period/weekEnd; all other date-range flows receive period/weekEnd directly.
    const TEST_DATE_INPUT_MODES = Object.freeze({
      ZFI072A: "week",
      ZFI057: "weekAndRange",
      ZCO019: "range",
      ZCO020: "range",
      ZFI019NA: "range",
      ZFI019NL: "range",
      ZFI072N: "range",
      ZFI080: "range",
      ZFI080B: "range",
      ZFI148: "range",
      ZFIR034: "range"
    });

    function normalizeTCodeValue(tCode) {
      return String(tCode || "").trim().toUpperCase();
    }

    function getTestDateInputMode(tCode) {
      return TEST_DATE_INPUT_MODES[normalizeTCodeValue(tCode)] || "none";
    }

    function supportsTestDateWeekInput(tCode) {
      const mode = getTestDateInputMode(tCode);
      return mode === "week" || mode === "weekOrRange" || mode === "weekAndRange";
    }

    function supportsTestDateRangeInput(tCode) {
      const mode = getTestDateInputMode(tCode);
      return mode === "range" || mode === "weekOrRange" || mode === "weekAndRange";
    }

    function canSelectTestDateInputMode(tCode) {
      return getTestDateInputMode(tCode) === "weekOrRange";
    }

    function usesExecutionDateParams(tCode) {
      return getTestDateInputMode(tCode) !== "none";
    }

    function allowTestDateOverride() {
      return state.bridge.allowTestDateOverride === true;
    }

    function shouldUseTestDateOverrideForTCode(tCode, formState = state.form) {
      return allowTestDateOverride() && formState.useTestDateOverride === true && usesExecutionDateParams(tCode);
    }

    function getServerDefaultExecutionDateRange() {
      const range = state.bridge.defaultExecutionDateRange || {};
      if (range.period && range.weekEnd) {
        return {
          period: String(range.period),
          weekEnd: String(range.weekEnd)
        };
      }
      return getLastFullWeekDateRange();
    }

    function parseDateInputValue(value) {
      const text = String(value || "").trim().replace(/[./]/g, "-");
      const match = text.match(/^(\d{4})-(\d{1,2})-(\d{1,2})$/);
      if (!match) return null;
      const year = Number(match[1]);
      const month = Number(match[2]);
      const day = Number(match[3]);
      const date = new Date(year, month - 1, day);
      if (date.getFullYear() !== year || date.getMonth() !== month - 1 || date.getDate() !== day) return null;
      return date;
    }

    function getIsoWeekNumber(date) {
      const d = new Date(date.getFullYear(), date.getMonth(), date.getDate());
      const day = d.getDay() || 7;
      const thursday = new Date(d);
      thursday.setDate(d.getDate() + 4 - day);
      const yearStart = new Date(thursday.getFullYear(), 0, 1);
      return Math.ceil((((thursday - yearStart) / 86400000) + 1) / 7);
    }

    function getIsoWeekInputParts(date) {
      const d = new Date(date.getFullYear(), date.getMonth(), date.getDate());
      const day = d.getDay() || 7;
      const thursday = new Date(d);
      thursday.setDate(d.getDate() + 4 - day);
      return {
        year: thursday.getFullYear(),
        week: getIsoWeekNumber(d)
      };
    }

    function formatIsoWeekInput(date) {
      const info = getIsoWeekInputParts(date);
      return `${info.year}-W${String(info.week).padStart(2, "0")}`;
    }

    function normalizeIsoWeekText(value) {
      const text = String(value || "").trim().toUpperCase();
      const match = text.match(/^(?:(\d{4})\s*(?:-|\s)?\s*(?:W\s*)?(\d{1,2})(?:\s*年?\s*周)?|(\d{4})年\s*(\d{1,2})周)$/);
      if (!match) return "";
      const year = match[1] || match[3];
      const week = match[2] || match[4];
      return `${year}-W${String(Number(week)).padStart(2, "0")}`;
    }

    function isoWeekStartDate(year, week) {
      if (!Number.isInteger(year) || !Number.isInteger(week) || week < 1 || week > 53) return null;
      const jan4 = new Date(year, 0, 4);
      const jan4Day = jan4.getDay() || 7;
      const monday = new Date(jan4);
      monday.setDate(jan4.getDate() - jan4Day + 1 + (week - 1) * 7);
      const check = getIsoWeekInputParts(monday);
      if (check.year !== year || check.week !== week) return null;
      return monday;
    }

    function parseIsoWeekRange(value) {
      const normalized = normalizeIsoWeekText(value);
      const match = normalized.match(/^(\d{4})-W(\d{2})$/);
      if (!match) return null;
      const start = isoWeekStartDate(Number(match[1]), Number(match[2]));
      if (!start) return null;
      const end = new Date(start);
      end.setDate(start.getDate() + 6);
      return {
        period: formatSapDateValue(start),
        weekEnd: formatSapDateValue(end),
        year: start.getFullYear(),
        week: getIsoWeekNumber(start),
        testDateKind: "week",
        testIsoWeek: normalized,
        testDateStart: formatDateInputForForm(start),
        testDateEnd: formatDateInputForForm(end)
      };
    }

    function formatDateInputForForm(date) {
      const year = date.getFullYear();
      const month = String(date.getMonth() + 1).padStart(2, "0");
      const day = String(date.getDate()).padStart(2, "0");
      return `${year}-${month}-${day}`;
    }

    function getDefaultTestDateFormState() {
      const range = getServerDefaultExecutionDateRange();
      const start = parseDateInputValue(range.period) || parseDateInputValue(getLastFullWeekDateRange().period);
      const end = parseDateInputValue(range.weekEnd) || parseDateInputValue(getLastFullWeekDateRange().weekEnd);
      return {
        useTestDateOverride: false,
        testDateKind: "range",
        testIsoWeek: start ? formatIsoWeekInput(start) : "",
        testDateStart: start ? formatDateInputForForm(start) : "",
        testDateEnd: end ? formatDateInputForForm(end) : ""
      };
    }

    function getTestDateRangeFromForm(formState = state.form, tCode = "") {
      const defaultState = getDefaultTestDateFormState();
      const inputMode = getTestDateInputMode(tCode);
      if (inputMode === "none") return null;
      const kind = inputMode === "week" || ((inputMode === "weekOrRange" || inputMode === "weekAndRange") && formState.testDateKind === "week")
        ? "week"
        : "range";
      if (kind === "week") {
        return parseIsoWeekRange(formState.testIsoWeek || defaultState.testIsoWeek);
      }

      let start = parseDateInputValue(formState.testDateStart || defaultState.testDateStart);
      let end = parseDateInputValue(formState.testDateEnd || defaultState.testDateEnd);
      if (!start || !end) return null;
      if (end < start) {
        const tmp = start;
        start = end;
        end = tmp;
      }
      return {
        period: formatSapDateValue(start),
        weekEnd: formatSapDateValue(end),
        year: start.getFullYear(),
        week: getIsoWeekNumber(start),
        testDateKind: "range",
        testIsoWeek: "",
        testDateStart: formatDateInputForForm(start),
        testDateEnd: formatDateInputForForm(end)
      };
    }

    function buildTestDateOverrideForTCode(tCode, formState = state.form) {
      if (!shouldUseTestDateOverrideForTCode(tCode, formState)) return null;
      const range = getTestDateRangeFromForm(formState, tCode);
      if (!range) return null;
      const payload = {
        dateMode: "testOverride",
        testDateMode: "testOverride",
        testDateKind: range.testDateKind,
        period: range.period,
        weekEnd: range.weekEnd,
        year: String(range.year),
        week: String(range.week)
      };
      if (range.testDateKind === "week") payload.testIsoWeek = range.testIsoWeek;
      else {
        payload.testDateStart = range.testDateStart;
        payload.testDateEnd = range.testDateEnd;
      }
      return payload;
    }

    function getDisplayExecutionDateRangeForTCode(tCode) {
      return buildTestDateOverrideForTCode(tCode) || getServerDefaultExecutionDateRange();
    }

    function getTestDateFormStateFromParams(tCode, params = {}) {
      const defaults = getDefaultTestDateFormState();
      if (!allowTestDateOverride() || !usesExecutionDateParams(tCode)) return defaults;
      const dateMode = String(params.dateMode || "").trim();
      const testDateMode = String(params.testDateMode || "").trim();
      const hasMarker = dateMode.toLowerCase() === "testoverride" || testDateMode.toLowerCase() === "testoverride";
      if (!hasMarker) return defaults;
      const inputMode = getTestDateInputMode(tCode);
      const kind = inputMode === "week" || ((inputMode === "weekOrRange" || inputMode === "weekAndRange") && String(params.testDateKind || "").trim().toLowerCase() === "week")
        ? "week"
        : "range";
      const startInput = sapDateToInputValue(params.testDateStart || params.period || params.startDate || params.fromDate || params.dateFrom || params.beginDate || params.dateBegin || "");
      const endInput = sapDateToInputValue(params.testDateEnd || params.weekEnd || params.endDate || params.toDate || params.dateTo || params.dateEnd || "");
      return {
        useTestDateOverride: true,
        testDateKind: kind,
        testIsoWeek: normalizeIsoWeekText(params.testIsoWeek || "") || defaults.testIsoWeek,
        testDateStart: startInput || defaults.testDateStart,
        testDateEnd: endInput || defaults.testDateEnd
      };
    }

    function disableTestDateOverrideState() {
      state.form.useTestDateOverride = false;
      if (state.scheduleForm) state.scheduleForm.useTestDateOverride = false;
    }

    function getLastFullWeekDateRange(baseDate = new Date()) {
      const today = new Date(baseDate.getFullYear(), baseDate.getMonth(), baseDate.getDate());
      const day = today.getDay() || 7;
      const currentMonday = new Date(today);
      currentMonday.setDate(today.getDate() - day + 1);
      const start = new Date(currentMonday);
      start.setDate(currentMonday.getDate() - 7);
      const end = new Date(start);
      end.setDate(start.getDate() + 6);
      return {
        period: formatSapDateValue(start),
        weekEnd: formatSapDateValue(end)
      };
    }

    function stripBearerPrefix(value) {
      return String(value || "").trim().replace(/^Bearer\s+/i, "");
    }

    function decodeBase64UrlJson(segment) {
      const normalized = String(segment || "").replace(/-/g, "+").replace(/_/g, "/");
      const padded = normalized.padEnd(normalized.length + ((4 - normalized.length % 4) % 4), "=");
      const binary = atob(padded);
      const bytes = Uint8Array.from(binary, char => char.charCodeAt(0));
      return JSON.parse(new TextDecoder("utf-8").decode(bytes));
    }

    function parseJwtPayload(tokenValue) {
      const token = stripBearerPrefix(tokenValue);
      const parts = token.split(".");
      if (parts.length < 2 || !parts[1]) throw new Error("invalid jwt");
      return decodeBase64UrlJson(parts[1]);
    }

    function readExternalTokenFromUrl() {
      const url = new URL(window.location.href);
      for (const key of EXTERNAL_TOKEN_QUERY_KEYS) {
        const value = url.searchParams.get(key);
        if (value) return { key, value };
      }
      return null;
    }

    function removeExternalTokenFromUrl() {
      if (!window.history?.replaceState) return;
      const url = new URL(window.location.href);
      let changed = false;
      EXTERNAL_TOKEN_QUERY_KEYS.forEach(key => {
        if (url.searchParams.has(key)) {
          url.searchParams.delete(key);
          changed = true;
        }
      });
      if (changed) window.history.replaceState({}, document.title, url.href);
    }

    function applyExternalTokenLogin(claimedAccount, claimedUserName) {
      const displayName = claimedUserName || claimedAccount;
      state.form.useDefaultNotifyUser = false;
      state.user.name = displayName;
      state.user.dingTalkUserId = claimedAccount;
      state.loggedIn = true;
      state.page = "execute";
      localStorage.setItem("portalUser", displayName);
      localStorage.setItem("portalDingTalkUserId", claimedAccount);
      localStorage.setItem("portalUseDefaultNotifyUser", "0");
      localStorage.setItem("portalLoggedIn", "1");
    }

    function firstPayloadString(payload, keys) {
      for (const key of keys) {
        const value = payload[key];
        if (value === null || value === undefined) continue;
        const text = String(value).trim();
        if (text) return text;
      }
      return "";
    }

    function consumeExternalTokenAccountFromUrl() {
      const tokenParam = readExternalTokenFromUrl();
      if (!tokenParam) {
        state.externalAuth = { claimedAccount: "", claimedUserName: "", status: "none" };
        state.user.name = "张三";
        state.user.dingTalkUserId = DEFAULT_DINGTALK_USER_ID;
        state.form.useDefaultNotifyUser = true;
        localStorage.removeItem("portalUser");
        localStorage.removeItem("portalDingTalkUserId");
        localStorage.removeItem("portalLoggedIn");
        localStorage.setItem("portalUseDefaultNotifyUser", "1");
        return;
      }
      try {
        const payload = parseJwtPayload(tokenParam.value);
        const claimedAccount = firstPayloadString(payload, [
          "Account",
          "account",
          "Ddid",
          "ddid",
          "DDID",
          "DingTalkUserId",
          "dingTalkUserId",
          "dingTalkId",
          "UserId",
          "userId",
          "userid"
        ]);
        const claimedUserName = firstPayloadString(payload, [
          "UserName",
          "userName",
          "Name",
          "name",
          "RealName",
          "realName",
          "DisplayName",
          "displayName",
          "NickName",
          "nickName"
        ]);
        state.externalAuth = {
          claimedAccount,
          claimedUserName,
          status: claimedAccount ? "parsed" : "missing-account"
        };
        if (claimedAccount) {
          applyExternalTokenLogin(claimedAccount, state.externalAuth.claimedUserName);
        } else {
          state.form.useDefaultNotifyUser = true;
          localStorage.setItem("portalUseDefaultNotifyUser", "1");
        }
      } catch {
        state.externalAuth = { claimedAccount: "", claimedUserName: "", status: "invalid" };
        state.form.useDefaultNotifyUser = true;
        localStorage.setItem("portalUseDefaultNotifyUser", "1");
      } finally {
        removeExternalTokenFromUrl();
      }
    }

    function getResolvedNotifyUserId() {
      if (state.form.useDefaultNotifyUser !== false) return DEFAULT_DINGTALK_USER_ID;
      if (state.externalAuth.claimedAccount) return state.externalAuth.claimedAccount;
      return "";
    }

    function isNotifyUserBlocked() {
      return state.form.useDefaultNotifyUser === false && !state.externalAuth.claimedAccount;
    }

    function notifyUserBlockingText() {
      if (!isNotifyUserBlocked()) return "";
      if (state.externalAuth.status === "invalid") return "token 无效，取消固定通知后不能提交。";
      if (state.externalAuth.status === "missing-account") return "token 未包含钉钉 ID，取消固定通知后不能提交。";
      return "未解析到 token 钉钉 ID，取消固定通知后不能提交。";
    }

    function notifyUserSourceText() {
      if (state.form.useDefaultNotifyUser !== false) return "联调默认";
      if (state.externalAuth.claimedAccount) return "URL token 钉钉 ID";
      if (state.externalAuth.status === "invalid") return "token 无效";
      if (state.externalAuth.status === "missing-account") return "token 未包含钉钉 ID";
      return "未识别 token";
    }

    const SCHEDULE_WEEKDAY_OPTIONS = [
      { value: "monday", label: "周一" },
      { value: "tuesday", label: "周二" },
      { value: "wednesday", label: "周三" },
      { value: "thursday", label: "周四" },
      { value: "friday", label: "周五" },
      { value: "saturday", label: "周六" },
      { value: "sunday", label: "周日" }
    ];

    function getScheduleWeekdayOptions() {
      return SCHEDULE_WEEKDAY_OPTIONS.map(item => ({ ...item }));
    }

    function normalizeScheduleWeekday(value, fallback = "monday") {
      const raw = String(value ?? "").trim();
      const normalized = raw.toLowerCase().replace(/[\s_\-]+/g, "");
      const map = {
        "1": "monday", "01": "monday", mon: "monday", monday: "monday", "周一": "monday", "星期一": "monday", "礼拜一": "monday", "每周一": "monday", "每星期一": "monday",
        "2": "tuesday", "02": "tuesday", tue: "tuesday", tues: "tuesday", tuesday: "tuesday", "周二": "tuesday", "星期二": "tuesday", "礼拜二": "tuesday", "每周二": "tuesday", "每星期二": "tuesday",
        "3": "wednesday", "03": "wednesday", wed: "wednesday", wednesday: "wednesday", "周三": "wednesday", "星期三": "wednesday", "礼拜三": "wednesday", "每周三": "wednesday", "每星期三": "wednesday",
        "4": "thursday", "04": "thursday", thu: "thursday", thur: "thursday", thurs: "thursday", thursday: "thursday", "周四": "thursday", "星期四": "thursday", "礼拜四": "thursday", "每周四": "thursday", "每星期四": "thursday",
        "5": "friday", "05": "friday", fri: "friday", friday: "friday", "周五": "friday", "星期五": "friday", "礼拜五": "friday", "每周五": "friday", "每星期五": "friday",
        "6": "saturday", "06": "saturday", sat: "saturday", saturday: "saturday", "周六": "saturday", "星期六": "saturday", "礼拜六": "saturday", "每周六": "saturday", "每星期六": "saturday",
        "0": "sunday", "7": "sunday", "00": "sunday", "07": "sunday", sun: "sunday", sunday: "sunday", "周日": "sunday", "周天": "sunday", "星期日": "sunday", "星期天": "sunday", "礼拜日": "sunday", "礼拜天": "sunday", "每周日": "sunday", "每周天": "sunday", "每星期日": "sunday", "每星期天": "sunday"
      };
      const monthlyMatch = normalized.match(/^(?:\u6BCF\u6708(?:\u9996\u4E2A|\u7B2C\u4E00\u4E2A)?)(.+)$/);
      if (monthlyMatch && map[monthlyMatch[1]]) return map[monthlyMatch[1]];
      if (map[normalized]) return map[normalized];
      if (fallback === "") return "";
      return SCHEDULE_WEEKDAY_OPTIONS.some(item => item.value === fallback) ? fallback : "monday";
    }

    function formatScheduleWeekday(value) {
      const normalized = normalizeScheduleWeekday(value, "");
      return SCHEDULE_WEEKDAY_OPTIONS.find(item => item.value === normalized)?.label || "";
    }

    function formatScheduleFrequency(value, weekday = "") {
      const code = scheduleFrequencyCode(value);
      if (code === "weekly") {
        const label = formatScheduleWeekday(weekday || value);
        return label ? `每${label}` : "每周";
      }
      if (code === "monthly") {
        const label = formatScheduleWeekday(weekday || value);
        return label ? `\u6BCF\u6708\u9996\u4E2A${label}` : "\u6BCF\u6708";
      }
      const map = {
        daily: "\u6BCF\u5929",
        monthly: "\u6BCF\u6708",
        "\u6BCF\u5929": "\u6BCF\u5929",
        "\u6BCF\u6708": "\u6BCF\u6708"
      };
      return map[value] || value || "每周";
    }

    function scheduleFrequencyCode(value) {
      const raw = String(value ?? "").trim();
      const normalized = raw.toLowerCase().replace(/[\s_\-]+/g, "");
      const map = {
        "每天": "daily",
        day: "daily",
        daily: "daily",
        everyday: "daily",
        "每周": "weekly",
        week: "weekly",
        weekly: "weekly",
        everyweek: "weekly",
        "每周一": "weekly",
        "每周二": "weekly",
        "每周三": "weekly",
        "每周四": "weekly",
        "每周五": "weekly",
        "每周六": "weekly",
        "每周日": "weekly",
        "每周天": "weekly",
        "每星期一": "weekly",
        "每星期二": "weekly",
        "每星期三": "weekly",
        "每星期四": "weekly",
        "每星期五": "weekly",
        "每星期六": "weekly",
        "每星期日": "weekly",
        "每星期天": "weekly",
        "每月": "monthly",
        month: "monthly",
        monthly: "monthly",
        everymonth: "monthly"
      };
      if (/^\u6BCF\u6708/.test(raw)) return "monthly";
      return map[normalized] || map[raw] || value || "weekly";
    }

    function formatScheduleStatus(value, enabled = true) {
      const raw = String(value || "").trim();
      const lower = raw.toLowerCase();
      if (enabled === false || ["disabled", "inactive", "stopped", "停用"].includes(lower)) return "停用";
      if (["pending", "draft", "未落库"].includes(lower)) return "未落库";
      if (["failed", "error", "异常"].includes(lower)) return "异常";
      return raw || "启用";
    }

    function restoreFallbackConfig({ includeTransactions = true } = {}) {
      if (includeTransactions) {
        tCodes = clonePlain(fallbackConfig.tCodes);
        transactionRules = clonePlain(fallbackConfig.transactionRules);
      }
      plants = clonePlain(fallbackConfig.plants);
      factoryGroups = clonePlain(fallbackConfig.factoryGroups);
      robots = clonePlain(fallbackConfig.robots);
      scheduleTasks = clonePlain(fallbackConfig.scheduleTasks);
      Object.keys(plantCatalog).forEach(code => delete plantCatalog[code]);
      plants.forEach(plant => { plantCatalog[plant.code] = plant; });
    }
