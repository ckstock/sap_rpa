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
        const claimedAccount = String(payload.Account ?? payload.account ?? "").trim();
        state.externalAuth = {
          claimedAccount,
          claimedUserName: String(payload.UserName ?? payload.userName ?? "").trim(),
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
      if (state.externalAuth.status === "missing-account") return "token 未包含 Account，取消固定通知后不能提交。";
      return "未解析到 token 员工号，取消固定通知后不能提交。";
    }

    function notifyUserSourceText() {
      if (state.form.useDefaultNotifyUser !== false) return "联调默认";
      if (state.externalAuth.claimedAccount) return "URL token Account";
      if (state.externalAuth.status === "invalid") return "token 无效";
      if (state.externalAuth.status === "missing-account") return "token 未包含 Account";
      return "未识别 token";
    }

    function formatScheduleFrequency(value) {
      const map = {
        daily: "每天",
        weekly: "每周",
        monthly: "每月",
        "每天": "每天",
        "每周": "每周",
        "每月": "每月",
        "每周一": "每周一"
      };
      return map[value] || value || "每周";
    }

    function scheduleFrequencyCode(value) {
      const map = {
        "每天": "daily",
        "每周": "weekly",
        "每周一": "weekly",
        "每月": "monthly"
      };
      return map[value] || value || "weekly";
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
      if (includeTransactions) tCodes = clonePlain(fallbackConfig.tCodes);
      plants = clonePlain(fallbackConfig.plants);
      factoryGroups = clonePlain(fallbackConfig.factoryGroups);
      robots = clonePlain(fallbackConfig.robots);
      scheduleTasks = clonePlain(fallbackConfig.scheduleTasks);
      Object.keys(plantCatalog).forEach(code => delete plantCatalog[code]);
      plants.forEach(plant => { plantCatalog[plant.code] = plant; });
    }
