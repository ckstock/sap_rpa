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
