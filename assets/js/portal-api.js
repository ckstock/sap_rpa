    function normalizeTransactionFromApi(item) {
      const code = String(item.code || item.tcode || item.tCode || item.transactionCode || "").trim().toUpperCase();
      return {
        code,
        name: item.name || item.transactionName || code,
        module: item.module || "",
        stage: item.stage || "未分组",
        script: item.script || item.scriptFile || (code + ".vbs"),
        icon: item.icon || "terminal",
        params: item.paramsList || item.params || [],
        factoryRule: item.factoryRule || "",
        fixedPlants: item.fixedPlants || [],
        plants: toArray(item.plants || item.defaultPlants || item.allowedPlants),
        defaultPlantGroup: item.defaultPlantGroup || "PINGHU_30",
        automation: item.automation || "openOnly",
        selectableGroupIds: toArray(item.selectableGroupIds || item.selectableGroups),
        businessAreaMode: item.businessAreaMode || "byPlant",
        businessAreas: toArray(item.businessAreas),
        scriptVersion: item.scriptVersion || "",
        scriptHash: item.scriptHash || "",
        timeout: Number(item.timeoutSeconds || item.timeout || 180),
        retry: Number(item.retryCount || item.retry || item.maxAttempts || 2),
        enabled: boolValue(item.enabled ?? item.isActive, true)
      };
    }

    function normalizePlantFromApi(item) {
      const code = String(item.code || item.plantCode || item.id || "").trim().toUpperCase();
      return {
        code,
        name: item.name || item.plantName || code,
        area: item.area || item.businessArea || "",
        groups: toArray(item.groups || item.groupIds || item.plantGroups),
        sortOrder: Number(item.sortOrder || item.sort_order || item.order || item.displayOrder || 0),
        enabled: boolValue(item.enabled ?? item.isActive, true)
      };
    }

    function normalizePlantGroupFromApi(item) {
      const id = String(item.id || item.code || item.groupId || "").trim();
      const plantsInGroup = toArray(item.plants || item.plantCodes || item.members);
      const rawName = item.name || item.groupName || id;
      const displayName = legacyBusinessRangeNames[rawName] || businessRangeLabels[id] || rawName || id;
      return {
        id,
        name: displayName,
        shortName: legacyBusinessRangeNames[item.shortName] || businessRangeLabels[id] || item.shortName || displayName,
        plants: plantsInGroup,
        zfi019nlAreas: toArray(item.zfi019nlAreas || item.zfi019nlBusinessAreas || item.businessAreas),
        zfi080Areas: toArray(item.zfi080Areas || item.zfi080BusinessAreas || item.businessAreas),
        zfi072Plants: toArray(item.zfi072Plants || item.zfi072PlantCodes || plantsInGroup),
        zco019Plants: toArray(item.zco019Plants || item.zco019PlantCodes || plantsInGroup),
        enabled: boolValue(item.enabled ?? item.isActive, true)
      };
    }

    function preserveConfiguredPlantRefs(item) {
      // Group and transaction rules can explicitly target a valid SAP plant before
      // that plant has been added to the optional portal master-data catalog.
      return {
        ...item,
        plants: toArray(item.plants),
        zfi072Plants: toArray(item.zfi072Plants),
        zco019Plants: toArray(item.zco019Plants),
        fixedPlants: toArray(item.fixedPlants)
      };
    }

    function normalizeTransactionRuleFromApi(item) {
      const code = String(item.code || item.tcode || item.tCode || item.transactionCode || "").trim().toUpperCase();
      const base = getTCode(code) || {};
      const params = getDisplayParamsForTransaction({ ...base, code, params: item.paramsList || item.params || base.params });
      const isBusinessAreaRange = code === "ZFI019NL" || code === "ZFI057" || code === "ZCO020" || (params.includes("businessAreas") && !params.includes("plants"));
      const isDateRange = code === "ZFIR034" || (params.includes("period") && params.includes("weekEnd") && !params.includes("plants") && !params.includes("businessAreas"));
      const sourcePlants = toArray(item.plants || item.defaultPlants || item.allowedPlants || item.plantCodes || item.fixedPlants || item.fixedPlantCodes);
      const sourceFixedPlants = toArray(item.fixedPlants || item.fixedPlantCodes || item.plants || item.defaultPlants || item.allowedPlants || item.plantCodes);
      const sourceBusinessAreas = toArray(item.businessAreas || item.businessAreaCodes || base.businessAreas);
      return {
        ...base,
        code,
        name: item.name || base.name || code,
        module: item.module || base.module || "",
        stage: item.stage || base.stage || "未分组",
        script: item.script || item.scriptFile || base.script || (code + ".vbs"),
        icon: item.icon || base.icon || "terminal",
        params: toArray(item.paramsList || item.params || base.params),
        factoryRule: item.factoryRule || item.ruleName || base.factoryRule || "",
        fixedPlants: isBusinessAreaRange || isDateRange ? [] : sourceFixedPlants,
        plants: isBusinessAreaRange || isDateRange ? [] : sourcePlants,
        selectableGroupIds: toArray(item.selectableGroupIds || item.selectableGroups || base.selectableGroupIds),
        businessAreaMode: isDateRange ? "none" : (item.businessAreaMode || base.businessAreaMode || "byPlant"),
        businessAreas: isDateRange ? [] : sourceBusinessAreas,
        defaultPlantGroup: item.defaultPlantGroup || item.plantGroupId || item.groupId || base.defaultPlantGroup || "PINGHU_30",
        automation: item.automation || base.automation || "openOnly",
        timeout: Number(item.timeoutSeconds || item.timeout || base.timeout || 180),
        retry: Number(item.retryCount || item.retry || item.maxAttempts || base.retry || 2),
        enabled: boolValue(item.enabled ?? item.isActive, true),
        configSource: "api-config"
      };
    }

    function mergeTransactionRuleIntoExecutionDefinition(transaction, rule) {
      if (!rule) return transaction;
      // Card metadata is stored with the transaction, while the separately persisted
      // rule owns the executable range.
      return {
        ...transaction,
        defaultPlantGroup: rule.defaultPlantGroup || transaction.defaultPlantGroup,
        factoryRule: rule.factoryRule || transaction.factoryRule,
        fixedPlants: toArray(rule.fixedPlants),
        selectableGroupIds: toArray(rule.selectableGroupIds),
        businessAreaMode: rule.businessAreaMode || transaction.businessAreaMode,
        businessAreas: toArray(rule.businessAreas),
        enabled: transaction.enabled !== false && rule.enabled !== false,
        configSource: rule.configSource || transaction.configSource || "api-config"
      };
    }

    function normalizeRobotFromApi(item) {
      const id = String(item.id || item.robotId || item.code || "").trim();
      const bindings = Array.isArray(item.bindings) ? item.bindings : [];
      return {
        id,
        name: item.name || item.robotName || id,
        robotType: item.robotType || item.type || "dingtalk",
        group: item.group || item.groupName || item.targetName || item.targetLabel || "",
        events: toArray(item.events || item.eventTypes || item.subscriptions || bindings.map(binding => binding.eventName || binding.event || binding.tcode || binding.code)),
        bindings,
        enabled: boolValue(item.enabled ?? item.isActive, true),
        lastPush: item.lastPush || item.lastSentAt || "",
        hasWebhook: !!(item.hasWebhook || item.webhookConfigured || item.webhookMasked),
        hasSecret: !!(item.hasSecret || item.secretConfigured || item.secretMasked)
      };
    }

    function normalizeScheduleTaskFromApi(item) {
      const tCode = String(item.tCode || item.tcode || item.transactionCode || item.code || "").trim().toUpperCase();
      const factoryGroup = item.factoryGroup || item.plantGroupId || item.groupId || item.defaultPlantGroup || getTCode(tCode)?.defaultPlantGroup || state.scheduleForm.factoryGroup || "";
      let params = item.params && typeof item.params === "object" ? item.params : {};
      if (!Object.keys(params).length && typeof item.paramsJson === "string") {
        try {
          const parsed = JSON.parse(item.paramsJson);
          if (parsed && typeof parsed === "object") params = parsed;
        } catch (_) { }
      }
      [
        "dateMode",
        "testDateMode",
        "testDateKind",
        "testIsoWeek",
        "testDateStart",
        "testDateEnd",
        "period",
        "weekEnd",
        "year",
        "week"
      ].forEach(key => {
        if (item[key] !== undefined && item[key] !== null && item[key] !== "") params[key] = item[key];
      });
      const isDateRange = getRuleRangeKind(getTCode(tCode)) === "dateRange";
      const isBusinessAreaRange = getRuleRangeKind(getTCode(tCode)) === "businessArea";
      const hasExplicitPlants = item.plants !== undefined || item.plantCodes !== undefined || item.factoryCodes !== undefined ||
        item.plantsCsv !== undefined || item.plantCodesCsv !== undefined || item.factoryCodesCsv !== undefined ||
        params.plants !== undefined || params.plantCodes !== undefined || params.factoryCodes !== undefined ||
        params.plantsCsv !== undefined || params.plantCodesCsv !== undefined || params.factoryCodesCsv !== undefined;
      const plantsValue = normalizeRulePlantList(item.plants ?? item.plantCodes ?? item.factoryCodes ??
        item.plantsCsv ?? item.plantCodesCsv ?? item.factoryCodesCsv ??
        params.plants ?? params.plantCodes ?? params.factoryCodes ??
        params.plantsCsv ?? params.plantCodesCsv ?? params.factoryCodesCsv);
      const businessAreasValue = toArray(item.businessAreas || item.businessAreaCodes || params.businessAreas);
      const fixedBusinessAreas = !allowsCustomScheduleBusinessAreaScope(tCode) && isBusinessAreaRange && hasFixedConfiguredBusinessAreas(getTCode(tCode))
        ? getDefaultRunRangeForTCode(tCode, factoryGroup)
        : [];
      const plantsForTask = isDateRange
        ? []
        : isBusinessAreaRange
          ? (fixedBusinessAreas.length ? fixedBusinessAreas : (businessAreasValue.length ? businessAreasValue : (hasExplicitPlants ? plantsValue : getDefaultRunRangeForTCode(tCode, factoryGroup))))
          : (hasExplicitPlants ? plantsValue : getDefaultPlantsForTCode(tCode, factoryGroup));
      const enabled = boolValue(item.enabled ?? item.isActive, true);
      const frequencyCode = scheduleFrequencyCode(item.frequency || item.frequencyText || item.scheduleType || item.cronLabel || "weekly");
      const weekday = frequencyCode === "weekly" || frequencyCode === "monthly"
        ? normalizeScheduleWeekday(
          item.weekday || item.weekDay || item.dayOfWeek || item.scheduleWeekday || params.weekday || item.frequency || item.frequencyText || item.scheduleType,
          frequencyCode === "weekly" ? "monday" : ""
        )
        : "";
      return {
        id: String(item.id || item.taskId || item.scheduleId || "").trim(),
        name: item.name || item.taskName || item.title || defaultScheduleName(tCode),
        tCode,
        factoryGroup,
        factoryGroupName: item.factoryGroupName || item.plantGroupName || "",
        plants: plantsForTask,
        businessAreas: isDateRange ? [] : (isBusinessAreaRange ? plantsForTask : businessAreasValue),
        params,
        rangeKind: isDateRange ? "dateRange" : (isBusinessAreaRange ? "businessArea" : ""),
        dateRange: isDateRange ? { period: params.period || "", weekEnd: params.weekEnd || "" } : null,
        time: item.time || item.execTime || item.runAt || item.startTime || "20:00",
        frequency: formatScheduleFrequency(item.frequency || item.frequencyText || item.scheduleType || item.cronLabel || "weekly", weekday),
        frequencyCode,
        weekday,
        dayOfWeek: weekday,
        weekdayLabel: formatScheduleWeekday(weekday),
        status: formatScheduleStatus(item.status, enabled),
        next: item.next || item.nextRunAt || item.nextFireTime || item.nextExecutionTime || "-",
        enabled,
        notify: item.notify || {},
        notifyTarget: item.notifyTarget || item.notify_target || item.notify?.target || "",
        notifyStart: boolValue(item.notifyStart ?? item.notify_on_start ?? item.notify_start ?? item.notify?.onStart, false),
        notifySuccess: boolValue(item.notifyOnSuccess ?? item.notifySuccess ?? item.notify_success ?? item.notify?.onSuccess, false),
        notifyFail: boolValue(item.notifyOnFailure ?? item.notifyFail ?? item.notify_failure ?? item.notify?.onFailure, true),
        createdBy: item.createdBy || item.created_by || "",
        updatedBy: item.updatedBy || item.updated_by || "",
        source: item.source || "api"
      };
    }

    function getCurrentScheduleOwnerName() {
      return String(state.user?.name || state.externalAuth?.claimedUserName || "").trim();
    }

    function isCurrentUserScheduleTask(task) {
      const owner = getCurrentScheduleOwnerName().toLowerCase();
      if (!owner) return true;
      const createdBy = String(task.createdBy || "").trim().toLowerCase();
      const updatedBy = String(task.updatedBy || "").trim().toLowerCase();
      if (!createdBy && !updatedBy) return true;
      return [createdBy, updatedBy].includes(owner);
    }

    function filterCurrentUserScheduleTasks(items) {
      return items.filter(isCurrentUserScheduleTask);
    }

    function appendScheduleOwnerQuery(path) {
      const owner = getCurrentScheduleOwnerName();
      if (!owner) return path;
      const separator = String(path).includes("?") ? "&" : "?";
      return path + separator + "scheduleOwner=" + encodeURIComponent(owner);
    }

    function applyConfigData(data) {
      const hasTransactions = Array.isArray(data.transactions);
      const hasPlants = Array.isArray(data.plants);
      const hasGroups = Array.isArray(data.plantGroups);
      const hasRules = Array.isArray(data.transactionRules);
      const hasRobots = Array.isArray(data.notificationRobots);
      const scheduleSource = data.scheduleTasks || data.schedules || data.scheduledTasks;
      const hasSchedules = Array.isArray(scheduleSource);
      const nextTransactions = hasTransactions
        ? data.transactions.map(normalizeTransactionFromApi).filter(item => item.code)
        : [];
      const nextPlants = hasPlants ? data.plants.map(normalizePlantFromApi).filter(item => item.code && item.enabled !== false) : [];
      const nextGroups = hasGroups ? data.plantGroups.map(normalizePlantGroupFromApi).filter(item => item.id).map(preserveConfiguredPlantRefs) : [];
      const nextRules = hasRules ? data.transactionRules.map(normalizeTransactionRuleFromApi).filter(item => item.code).map(preserveConfiguredPlantRefs) : [];
      const nextRobots = hasRobots ? data.notificationRobots.map(normalizeRobotFromApi).filter(item => item.id) : [];

      if (hasPlants) {
        plants = nextPlants;
        Object.keys(plantCatalog).forEach(code => delete plantCatalog[code]);
        plants.forEach(plant => { plantCatalog[plant.code] = plant; });
      }
      if (hasGroups) factoryGroups = nextGroups;
      if (hasRules) {
        transactionRules = nextRules;
      } else if (hasTransactions) {
        transactionRules = nextTransactions.map(item => ({ ...item }));
      }
      if (hasTransactions) {
        const rulesByCode = new Map(transactionRules.map(rule => [rule.code, rule]));
        tCodes = nextTransactions.map(transaction => mergeTransactionRuleIntoExecutionDefinition(transaction, rulesByCode.get(transaction.code)));
      }
      if (hasRobots) robots = nextRobots;
      if (hasSchedules) {
        scheduleTasks = filterCurrentUserScheduleTasks(scheduleSource.map(normalizeScheduleTaskFromApi).filter(item => item.id && item.tCode));
      }

      state.config = {
        online: true,
        source: "api",
        lastLoaded: new Date().toLocaleTimeString("zh-CN", { hour12: false }),
        error: ""
      };
      syncExecutionDefaults(false);
    }

    async function refreshConfigData({ silent = true } = {}) {
      try {
        const data = await bridgeFetch(appendScheduleOwnerQuery(CONFIG_API_PATHS.root));
        applyConfigData(data || {});
        if (!silent) toast("基础配置已从 API 刷新", "ok");
      } catch (err) {
        state.config = {
          online: false,
          source: "fallback",
          lastLoaded: new Date().toLocaleTimeString("zh-CN", { hour12: false }),
          error: err.message || "基础配置 API 不可用"
        };
        restoreFallbackConfig({ includeTransactions: false });
        if (!silent) toast("基础配置 API 未接通，当前显示只读静态默认值", "warn");
      }
    }

    function normalizeRunFromApi(run) {
      const params = readRunParams(run.requestJson);
      const plantsCsv = readRunParamCsv(params, ["plants", "plant", "plantsCsv", "plantCodes", "factoryCodes"]);
      const businessAreasCsv = readRunParamCsv(params, ["businessAreas", "businessArea", "businessAreasCsv", "businessAreaList", "gsberlist", "gsber"]);
      const factoryGroup = readRunParamText(params, ["factoryGroup", "defaultBusinessScope", "defaultGroup", "plantGroup", "plantGroupId", "businessScope"]);
      const statusText = run.status === "success" ? "成功" : run.status === "no_data" ? "无数据" : run.status === "failed" ? "失败" : run.status === "running" ? "执行中" : run.status === "canceled" ? "已取消" : "排队中";
      return {
        id: run.runId,
        time: run.finishedAt || run.startedAt || run.queuedAt || "",
        task: (getTCode(run.transactionCode)?.name || run.transactionCode) + " / " + (run.operatorName || "本机用户"),
        tCode: run.transactionCode,
        scheduleTaskId: run.scheduleTaskId || "",
        scheduleTaskName: run.scheduleTaskName || "",
        scheduleSetter: run.scheduleSetter || run.operatorName || "",
        operatorName: run.operatorName || "",
        plant: plantsCsv || params.plant || "",
        plantsCsv,
        businessAreasCsv,
        factoryGroup,
        runParams: params,
        duration: formatDuration(run.durationMs || 0),
        status: statusText,
        notify: "本地记录",
        result: run.message || run.sapStatusText || run.status,
        logs: run.logs || [],
        files: run.files || []
      };
    }

    function readRunParams(requestJson) {
      try {
        if (requestJson && typeof requestJson === "object") {
          if (requestJson.params && typeof requestJson.params === "object") return requestJson.params;
          return requestJson;
        }
        const data = JSON.parse(requestJson || "{}");
        return data.params && typeof data.params === "object" ? data.params : {};
      } catch {
        return {};
      }
    }

    function readRunParamCsv(params, keys) {
      const values = [];
      for (const key of keys) {
        if (!params || params[key] === undefined || params[key] === null || params[key] === "") continue;
        values.push(...toArray(params[key]));
      }
      return Array.from(new Set(values)).join(",");
    }

    function readRunParamText(params, keys) {
      for (const key of keys) {
        const value = params?.[key];
        if (value === undefined || value === null || value === "") continue;
        const text = String(value).trim();
        if (text) return text;
      }
      return "";
    }

    function formatDuration(ms) {
      if (!ms) return "-";
      if (ms < 1000) return ms + " 毫秒";
      if (ms < 60000) return Math.round(ms / 1000) + " 秒";
      return Math.floor(ms / 60000) + " 分 " + Math.round((ms % 60000) / 1000) + " 秒";
    }

    function toQueueNumber(value) {
      if (value === undefined || value === null || value === "") return null;
      const num = Number(value);
      return Number.isFinite(num) ? num : null;
    }

    function pickQueueField(source, ...keys) {
      for (const key of keys) {
        if (source && source[key] !== undefined && source[key] !== null && source[key] !== "") return source[key];
      }
      return null;
    }

    function normalizeRunStatus(status) {
      const value = String(status || "").toLowerCase();
      if (value === "succeeded" || value === "completed") return "success";
      if (value === "nodata" || value === "no-data") return "no_data";
      if (value === "error") return "failed";
      if (value === "partial_failed" || value === "partialfailed") return "partial_failed";
      if (value === "cancelled") return "canceled";
      if (value === "pending" || value === "created") return "queued";
      if (value === "in_progress" || value === "processing" || value === "executing") return "running";
      return value || "queued";
    }

    function mergeQueueInfo(data = {}, runId = "") {
      const source = data.queue || data.queueStatus || data;
      const queuedRuns = source.queuedRuns || source.queued || source.items || data.queuedRuns || data.queued || data.items;
      let queuePosition = toQueueNumber(pickQueueField(source, "queuePosition", "position") ?? pickQueueField(data, "queuePosition", "position"));
      let runsAhead = toQueueNumber(pickQueueField(source, "runsAhead", "ahead") ?? pickQueueField(data, "runsAhead", "ahead"));
      let workItemsAhead = toQueueNumber(pickQueueField(source, "workItemsAhead") ?? pickQueueField(data, "workItemsAhead"));
      let queuedCount = toQueueNumber(pickQueueField(source, "queuedCount", "queueCount", "pendingCount", "waitingCount") ?? pickQueueField(data, "queuedCount", "queueCount", "pendingCount", "waitingCount"));
      const runningRunId = String(
        pickQueueField(source, "runningRunId", "currentRunId") ??
        pickQueueField(data, "runningRunId", "currentRunId") ??
        source.running?.runId ??
        source.running?.id ??
        data.running?.runId ??
        data.running?.id ??
        ""
      );
      if (runsAhead === null && queuePosition !== null) runsAhead = Math.max(queuePosition - 1, 0);
      if (workItemsAhead === null) {
        const hasOtherRunning = !!runningRunId && (!runId || runningRunId !== runId);
        workItemsAhead = runsAhead !== null ? runsAhead + (hasOtherRunning ? 1 : 0) : null;
      }
      if (queuedCount === null && Array.isArray(queuedRuns)) queuedCount = queuedRuns.length;

      const hasRunningField = (source && ("runningRunId" in source || "currentRunId" in source || "running" in source)) ||
        (data && ("runningRunId" in data || "currentRunId" in data || "running" in data));

      state.queue = {
        online: true,
        runningRunId: hasRunningField ? runningRunId : (runningRunId || state.queue.runningRunId || ""),
        queuedCount: queuedCount !== null ? queuedCount : state.queue.queuedCount,
        queuePosition: queuePosition !== null ? queuePosition : state.queue.queuePosition,
        runsAhead: runsAhead !== null ? runsAhead : state.queue.runsAhead,
        workItemsAhead: workItemsAhead !== null ? workItemsAhead : state.queue.workItemsAhead,
        lastChecked: nowTime()
      };

      if (state.currentRun && runId && state.currentRun.id === runId) {
        state.currentRun.queuePosition = queuePosition !== null ? queuePosition : state.currentRun.queuePosition;
        state.currentRun.runsAhead = runsAhead !== null ? runsAhead : state.currentRun.runsAhead;
        state.currentRun.workItemsAhead = workItemsAhead !== null ? workItemsAhead : state.currentRun.workItemsAhead;
        state.currentRun.queuedCount = queuedCount !== null ? queuedCount : state.currentRun.queuedCount;
        state.currentRun.runningRunId = state.queue.runningRunId;
        state.currentRun.status = normalizeRunStatus(data.status || state.currentRun.status);
      }
    }

    function queueAheadText(run = state.currentRun) {
      const status = normalizeRunStatus(run?.status);
      if (status === "running") return "正在执行";
      if (status === "success") return "已完成";
      if (status === "no_data") return "无数据";
      if (status === "partial_failed") return "部分失败";
      if (status === "failed") return "执行失败";
      if (status === "canceled") return "已取消";
      const workItemsAhead = toQueueNumber(run?.workItemsAhead ?? state.queue.workItemsAhead);
      if (workItemsAhead !== null) return "前方 " + workItemsAhead + " 个任务";
      const runsAhead = toQueueNumber(run?.runsAhead ?? state.queue.runsAhead);
      if (runsAhead !== null) return "前方 " + runsAhead + " 个任务";
      const queuePosition = toQueueNumber(run?.queuePosition ?? state.queue.queuePosition);
      if (queuePosition !== null) return "排队位置 " + queuePosition;
      return "等待执行器领取";
    }

    function addRunStatusLog(type, key, message) {
      if (state.currentRun && state.currentRun.lastStatusLogKey === key) return;
      if (state.currentRun) state.currentRun.lastStatusLogKey = key;
      addLog(type, message);
    }

    function mergeRunLogs(run) {
      const lines = Array.isArray(run?.logs) ? run.logs : [];
      if (!state.currentRun) return;
      state.currentRun.seenBackendLogs ||= new Set();
      lines.forEach(line => {
        const message = line.message || "";
        if (!message) return;
        const key = (line.createdAt || "") + "|" + (line.level || "") + "|" + message;
        if (state.currentRun.seenBackendLogs.has(key)) return;
        state.currentRun.seenBackendLogs.add(key);
        const level = String(line.level || "INFO").toUpperCase();
        addLog(level === "ERROR" || level === "ERR" ? "ERR" : level === "WARN" ? "WARN" : "INFO", message);
      });
    }

    async function refreshQueueStatus({ silent = true, runId = "" } = {}) {
      if (!state.bridge.online || state.bridge.queueMode === "disabled") return null;
      try {
        const data = await bridgeFetch("/api/queue/status");
        mergeQueueInfo(data, runId);
        return data;
      } catch (err) {
        state.queue.online = false;
        if (!silent) toast("队列状态接口暂不可用", "warn");
        return null;
      }
    }

    function bridgeStatusText() {
      if (!state.bridge.online) return "sap-rpa://";
      return state.bridge.queueMode === "disabled" ? "API 已连接 / 队列禁用" : "API 已连接 / 串行队列";
    }

    function bridgeStatusSubtext() {
      if (!state.bridge.online) return "点击执行时由浏览器唤醒 SapWebLauncher";
      if (state.bridge.queueMode === "disabled") return "当前只创建 SQLite 任务，不会启动 SAP";
      return "本机 API 会提交任务，SAP GUI 由后台串行执行";
    }
