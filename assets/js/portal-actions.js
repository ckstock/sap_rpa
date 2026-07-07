    function bindEvents() {
      document.querySelectorAll("[data-page]").forEach(btn => btn.addEventListener("click", () => { state.page = btn.dataset.page; render(); }));
      document.querySelectorAll("[data-config-tab]").forEach(btn => btn.addEventListener("click", () => { state.activeConfigTab = btn.dataset.configTab; render(); }));
      document.querySelectorAll("[data-bind]").forEach(input => {
        const update = () => {
          state.form[input.dataset.bind] = input.value;
          if (input.dataset.bind === "tCode" || input.dataset.bind === "factoryGroup") {
            syncExecutionDefaults(true);
            render();
          }
        };
        input.addEventListener("input", update);
        input.addEventListener("change", update);
      });
      document.querySelectorAll("[data-plant-code]").forEach(input => input.addEventListener("change", () => {
        const code = input.dataset.plantCode;
        state.form.plants = input.checked ? unique([...state.form.plants, code]) : state.form.plants.filter(item => item !== code);
        syncExecutionDefaults(false);
        render();
      }));
      const notify = document.getElementById("notify");
      if (notify) notify.addEventListener("change", () => { state.form.notify = notify.checked; });
      const useDefaultNotifyUser = document.getElementById("useDefaultNotifyUser");
      if (useDefaultNotifyUser) useDefaultNotifyUser.addEventListener("change", () => {
        state.form.useDefaultNotifyUser = useDefaultNotifyUser.checked;
        localStorage.setItem("portalUseDefaultNotifyUser", state.form.useDefaultNotifyUser ? "1" : "0");
        render();
      });
      const scheduleTCode = document.getElementById("scheduleTCode");
      if (scheduleTCode) scheduleTCode.addEventListener("change", () => updateScheduleTCode(scheduleTCode.value));
      const scheduleFactoryGroup = document.getElementById("scheduleFactoryGroup");
      if (scheduleFactoryGroup) scheduleFactoryGroup.addEventListener("change", () => updateScheduleFactoryGroup(scheduleFactoryGroup.value));
      const scheduleName = document.getElementById("scheduleName");
      if (scheduleName) scheduleName.addEventListener("input", () => {
        state.scheduleForm.name = scheduleName.value;
        state.scheduleForm.nameEdited = scheduleName.value.trim() !== defaultScheduleName(state.scheduleForm.tCode);
      });
      const scheduleTime = document.getElementById("scheduleTime");
      if (scheduleTime) scheduleTime.addEventListener("input", () => { state.scheduleForm.execTime = scheduleTime.value; });
      const scheduleFrequency = document.getElementById("scheduleFrequency");
      if (scheduleFrequency) scheduleFrequency.addEventListener("change", () => { state.scheduleForm.frequency = scheduleFrequency.value; });
      const scheduleEnabled = document.getElementById("scheduleEnabled");
      if (scheduleEnabled) scheduleEnabled.addEventListener("change", () => { state.scheduleForm.enabled = scheduleEnabled.checked; });
      [
        ["scheduleNotifyStart", "notifyStart"],
        ["scheduleNotifySuccess", "notifySuccess"],
        ["scheduleNotifyFail", "notifyFail"]
      ].forEach(([id, key]) => {
        const input = document.getElementById(id);
        if (input) input.addEventListener("change", () => { state.scheduleForm[key] = input.checked; });
      });
      const ruleGroup = document.getElementById("cfgRuleDefaultGroup");
      if (ruleGroup) ruleGroup.addEventListener("change", () => resetRuleRangeInput());
      const schedulePlantAdd = document.getElementById("schedulePlantAdd");
      if (schedulePlantAdd) schedulePlantAdd.addEventListener("keydown", event => {
        if (event.key === "Enter") {
          event.preventDefault();
          addSchedulePlant();
        }
      });
      document.querySelectorAll("[data-action]").forEach(el => el.addEventListener("click", () => handleAction(el)));
    }

    function defaultScheduleName(tCode) {
      const t = getTCode(tCode);
      return `${t.code} - ${t.name}`;
    }

    function updateScheduleTCode(tCode) {
      state.scheduleForm.tCode = tCode;
      state.scheduleForm.plants = getRuleRangeKind(getTCode(tCode)) === "dateRange" ? [] : getDefaultRunRangeForTCode(tCode, state.scheduleForm.factoryGroup);
      if (!state.scheduleForm.nameEdited) {
        state.scheduleForm.name = defaultScheduleName(tCode);
      }
      render();
    }

    function updateScheduleFactoryGroup(groupId) {
      state.scheduleForm.factoryGroup = groupId;
      state.scheduleForm.plants = getRuleRangeKind(getTCode(state.scheduleForm.tCode)) === "dateRange" ? [] : getDefaultRunRangeForTCode(state.scheduleForm.tCode, groupId);
      render();
    }

    function handleAction(el) {
      const action = el.dataset.action;
      if (action === "login") return login();
      if (action === "logout") return logout();
      if (action === "toggle-role") return toggleRole();
      if (action === "go-execute") return goExecute(el.dataset.tcode);
      if (action === "wake-protocol") return wakeProtocol();
      if (action === "start-run") return startRun();
      if (action === "select-factory-group") {
        state.form.factoryGroup = el.dataset.group;
        syncExecutionDefaults(true);
        return render();
      }
      if (action === "select-all-plants") {
        state.form.plants = getDefaultRunRangeForTCode(state.form.tCode);
        syncExecutionDefaults(false);
        return render();
      }
      if (action === "clear-plants") {
        state.form.plants = [];
        syncExecutionDefaults(false);
        return render();
      }
      if (action === "open-register-help") { state.modal = "register"; return render(); }
      if (action === "open-schedule-modal") { openScheduleModal(el.dataset.scheduleId || ""); return render(); }
      if (action === "close-modal") { state.modal = null; return render(); }
      if (action === "save-schedule") return saveScheduleFromModal();
      if (action === "open-config-modal") return openConfigModal(el.dataset.kind, el.dataset.mode, el.dataset.id || "");
      if (action === "save-config") return saveConfigFromModal(el.dataset.kind, el.dataset.mode, el.dataset.id || "");
      if (action === "delete-config") return deleteConfigItem(el.dataset.kind, el.dataset.id || "");
      if (action === "add-rule-plant") return addRulePlant();
      if (action === "remove-rule-plant") return removeRulePlant(el.dataset.plant || "");
      if (action === "reset-rule-plants") return resetRuleRangeInput();
      if (action === "add-schedule-plant") return addSchedulePlant();
      if (action === "remove-schedule-plant") return removeSchedulePlant(el.dataset.plant || "");
      if (action === "reset-schedule-plants") {
        const tCode = readInputValue("scheduleTCode");
        return setSchedulePlantInput(getRuleRangeKind(getTCode(tCode)) === "dateRange" ? [] : getDefaultPlantsForTCode(tCode, readInputValue("scheduleFactoryGroup")));
      }
      if (action === "test-robot") return testRobot(el.dataset.id || "");
      if (action === "toast-edit") return toast("编辑弹窗后续接入后台保存", "info");
      if (action === "refresh-config") return refreshConfigWithRender();
      if (action === "refresh-bridge") return refreshBridgeData({ silent: false });
      if (action === "refresh-report") return refreshReportData({ ...getReportRangeFromInputs(), silent: false });
      if (action === "export-history") return exportHistory();
      if (action === "view-log") return openRunLog(el.dataset.run);
    }

    function openScheduleModal(id = "") {
      const task = id ? scheduleTasks.find(item => item.id === id) : null;
      if (task) {
        state.scheduleForm = {
          id: task.id,
          name: task.name,
          tCode: task.tCode,
          factoryGroup: task.factoryGroup || getTCode(task.tCode)?.defaultPlantGroup || factoryGroups[0]?.id || "",
          plants: toArray(task.plants),
          execTime: task.time || "08:00",
          frequency: scheduleFrequencyCode(task.frequencyCode || task.frequency),
          notifyStart: task.notifyStart !== false,
          notifySuccess: task.notifySuccess !== false,
          notifyFail: task.notifyFail !== false,
          enabled: task.enabled !== false && task.status !== "停用",
          nameEdited: true
        };
      } else {
        const tCode = state.scheduleForm.tCode || tCodes[0]?.code || "";
        const defaultGroup = getTCode(tCode)?.defaultPlantGroup || state.scheduleForm.factoryGroup || factoryGroups[0]?.id || "";
        state.scheduleForm = {
          id: "",
          name: defaultScheduleName(tCode),
          tCode,
          factoryGroup: defaultGroup,
          plants: getDefaultPlantsForTCode(tCode, defaultGroup),
          execTime: state.scheduleForm.execTime || "08:00",
          frequency: state.scheduleForm.frequency || "weekly",
          notifyStart: true,
          notifySuccess: true,
          notifyFail: true,
          enabled: true,
          nameEdited: false
        };
      }
      state.scheduleForm.plants = normalizeRulePlantList(state.scheduleForm.plants);
      state.modal = "schedule";
    }

    async function saveScheduleFromModal() {
      if (!state.bridge.online || !state.config.online) {
        toast("保存失败：本机 API 未启动或配置接口不可用，定时任务没有落库", "err");
        return;
      }
      try {
        const payload = buildScheduleConfigPayload();
        if (!payload.tCode) throw new Error("缺少事务码");
        if (!payload.factoryGroup) throw new Error("缺少业务范围");
        await saveScheduleTask(payload);
        state.modal = null;
        await refreshConfigData({ silent: true });
        if (!scheduleTasks.some(item => item.id === payload.id)) {
          upsertScheduleTaskLocal(payload);
        }
        toast("定时任务已保存并刷新列表", "ok");
        render();
      } catch (err) {
        toast("定时任务保存失败：" + err.message, "err");
      }
    }

    async function saveScheduleTask(payload) {
      try {
        await bridgeFetch(CONFIG_API_PATHS.schedules(payload.id), {
          method: "PUT",
          body: JSON.stringify(payload)
        });
        return;
      } catch (err) {
        if (!/404|not found/i.test(err.message || "")) throw err;
      }
      const currentConfig = await bridgeFetch(CONFIG_API_PATHS.root);
      const currentSchedules = Array.isArray(currentConfig.scheduleTasks)
        ? currentConfig.scheduleTasks
        : Array.isArray(currentConfig.schedules)
          ? currentConfig.schedules
          : Array.isArray(currentConfig.scheduledTasks)
            ? currentConfig.scheduledTasks
            : scheduleTasks;
      const nextSchedules = currentSchedules.filter(item => String(item.id || item.taskId || item.scheduleId || "") !== payload.id);
      nextSchedules.push(payload);
      await bridgeFetch(CONFIG_API_PATHS.root, {
        method: "POST",
        body: JSON.stringify({
          ...currentConfig,
          scheduleTasks: nextSchedules,
          schedules: nextSchedules,
          scheduledTasks: nextSchedules
        })
      });
    }

    function upsertScheduleTaskLocal(payload) {
      const normalized = normalizeScheduleTaskFromApi({
        ...payload,
        frequency: payload.frequencyText,
        status: payload.status,
        next: payload.next || "-"
      });
      const index = scheduleTasks.findIndex(item => item.id === normalized.id);
      if (index >= 0) scheduleTasks[index] = normalized;
      else scheduleTasks.push(normalized);
    }

    function openConfigModal(kind, mode, id = "") {
      state.modal = `config:${kind}:${mode}:${encodeURIComponent(id)}`;
      render();
      if (!canWriteConfig()) toast("当前 API 未连接，可查看和修改表单；保存时需要先启动本机 API", "warn");
    }

    async function refreshConfigWithRender() {
      if (!state.bridge.online) {
        await refreshBridgeData({ silent: false });
        return;
      }
      await refreshConfigData({ silent: false });
      render();
    }

    function readInputValue(id) {
      return document.getElementById(id)?.value?.trim() || "";
    }

    function readInputChecked(id) {
      return !!document.getElementById(id)?.checked;
    }

    function getRulePlantInput() {
      return toArray(readInputValue("cfgRulePlants"));
    }

    function setRulePlantInput(plantsValue) {
      const input = document.getElementById("cfgRulePlants");
      const chips = document.getElementById("cfgRulePlantChips");
      if (!input || !chips) return;
      const plantsList = normalizeRulePlantList(plantsValue);
      input.value = plantsList.join(",");
      chips.innerHTML = renderRulePlantChips(plantsList, getTCode(readInputValue("cfgRuleCode")));
      chips.querySelectorAll("[data-action='remove-rule-plant']").forEach(btn => btn.addEventListener("click", () => removeRulePlant(btn.dataset.plant || "")));
    }

    function resetRuleRangeInput() {
      const code = readInputValue("cfgRuleCode");
      const groupId = readInputValue("cfgRuleDefaultGroup");
      const transaction = getTCode(code);
      const rangeKind = getRuleRangeKind(transaction);
      const values = rangeKind === "dateRange"
        ? []
        : rangeKind === "businessArea"
        ? getDefaultBusinessAreasForTCode(code, groupId)
        : getDefaultPlantsForTCode(code, groupId);
      return setRulePlantInput(values);
    }

    function getSchedulePlantInput() {
      const input = document.getElementById("schedulePlants");
      return normalizeRulePlantList(input ? input.value : state.scheduleForm.plants);
    }

    function renderSchedulePlantChips(plantsValue) {
      const plantsList = normalizeRulePlantList(plantsValue);
      return plantsList.length
        ? plantsList.map(code => `<span class="area-chip editable">${esc(code)}<button type="button" class="chip-x" data-action="remove-schedule-plant" data-plant="${esc(code)}" aria-label="删除工厂 ${esc(code)}" title="删除工厂 ${esc(code)}">X</button></span>`).join("")
        : `<span class="table-hint">未配置工厂</span>`;
    }

    function setSchedulePlantInput(plantsValue) {
      const input = document.getElementById("schedulePlants");
      const chips = document.getElementById("schedulePlantChips");
      const areaChips = document.getElementById("scheduleBusinessAreaChips");
      const plantsList = normalizeRulePlantList(plantsValue);
      state.scheduleForm.plants = plantsList;
      if (input) input.value = plantsList.join(",");
      if (chips) {
        chips.innerHTML = renderSchedulePlantChips(plantsList);
        chips.querySelectorAll("[data-action='remove-schedule-plant']").forEach(btn => btn.addEventListener("click", () => removeSchedulePlant(btn.dataset.plant || "")));
      }
      if (areaChips) {
        areaChips.innerHTML = renderCodeChips(getBusinessAreasForSelection(state.scheduleForm.tCode, plantsList, state.scheduleForm.factoryGroup));
      }
    }

    function addSchedulePlant() {
      const input = document.getElementById("schedulePlantAdd");
      const code = normalizeRulePlantCode(input?.value || "");
      if (!code) return toast("请输入工厂代码", "warn");
      setSchedulePlantInput([...getSchedulePlantInput(), code]);
      if (input) input.value = "";
    }

    function removeSchedulePlant(code) {
      const target = normalizeRulePlantCode(code);
      setSchedulePlantInput(getSchedulePlantInput().filter(item => normalizeRulePlantCode(item) !== target));
    }

    function addRulePlant() {
      const input = document.getElementById("cfgRulePlantAdd");
      const code = normalizeRulePlantCode(input?.value);
      if (!code) return;
      setRulePlantInput([...getRulePlantInput(), code]);
      input.value = "";
    }

    function removeRulePlant(code) {
      const target = normalizeRulePlantCode(code);
      setRulePlantInput(getRulePlantInput().filter(item => normalizeRulePlantCode(item) !== target));
    }

    function buildPlantConfigPayload(mode, id) {
      const code = (mode === "edit" ? id : readInputValue("cfgPlantCode")).toUpperCase();
      return {
        code,
        name: readInputValue("cfgPlantName"),
        area: readInputValue("cfgPlantArea"),
        businessArea: readInputValue("cfgPlantArea"),
        groups: toArray(readInputValue("cfgPlantGroups")),
        groupsCsv: toArray(readInputValue("cfgPlantGroups")).join(","),
        sortOrder: Number(readInputValue("cfgPlantSortOrder") || 0),
        enabled: readInputChecked("cfgPlantEnabled")
      };
    }

    function buildGroupConfigPayload(mode, id) {
      const groupId = mode === "edit" ? id : readInputValue("cfgGroupId");
      const plantsValue = toArray(readInputValue("cfgGroupPlants"));
      return {
        id: groupId,
        name: readInputValue("cfgGroupName"),
        shortName: readInputValue("cfgGroupShortName"),
        plants: plantsValue,
        zfi019nlAreas: toArray(readInputValue("cfgGroupZfi019nlAreas")),
        zfi019nlAreasCsv: toArray(readInputValue("cfgGroupZfi019nlAreas")).join(","),
        zfi080Areas: toArray(readInputValue("cfgGroupZfi080Areas")),
        zfi080AreasCsv: toArray(readInputValue("cfgGroupZfi080Areas")).join(","),
        zfi072Plants: toArray(readInputValue("cfgGroupZfi072Plants")),
        zfi072PlantsCsv: toArray(readInputValue("cfgGroupZfi072Plants")).join(","),
        zco019Plants: toArray(readInputValue("cfgGroupZco019Plants")),
        zco019PlantsCsv: toArray(readInputValue("cfgGroupZco019Plants")).join(","),
        enabled: readInputChecked("cfgGroupEnabled")
      };
    }

    function buildExistingTransactionPayload(code) {
      const existing = tCodes.find(item => item.code === code) || {};
      const plantsValue = toArray(existing.plants).length ? toArray(existing.plants) : toArray(existing.fixedPlants);
      return {
        code,
        tcode: code,
        name: existing.name || code,
        module: existing.module || "",
        stage: existing.stage || "",
        script: existing.script || (code + ".vbs"),
        scriptFile: existing.script || (code + ".vbs"),
        icon: existing.icon || "terminal",
        params: toArray(existing.params),
        factoryRule: existing.factoryRule || "",
        defaultPlantGroup: existing.defaultPlantGroup || "",
        defaultGroup: existing.defaultPlantGroup || "",
        plants: plantsValue,
        plantsCsv: plantsValue.join(","),
        fixedPlants: plantsValue,
        fixedPlantsCsv: plantsValue.join(","),
        selectableGroupIds: toArray(existing.selectableGroupIds),
        selectableGroupIdsCsv: toArray(existing.selectableGroupIds).join(","),
        businessAreaMode: existing.businessAreaMode || "byPlant",
        businessAreas: toArray(existing.businessAreas),
        businessAreasCsv: toArray(existing.businessAreas).join(","),
        automation: existing.automation || "openOnly",
        timeout: Number(existing.timeoutSeconds || existing.timeout || 180),
        timeoutSeconds: Number(existing.timeoutSeconds || existing.timeout || 180),
        retry: Number(existing.retryCount || existing.retry || 0),
        retryCount: Number(existing.retryCount || existing.retry || 0),
        enabled: existing.enabled !== false
      };
    }

    function buildTCodeConfigPayload(mode, id) {
      const code = (mode === "edit" ? id : readInputValue("cfgTCodeCode")).toUpperCase();
      const payload = buildExistingTransactionPayload(code);
      payload.name = readInputValue("cfgTCodeName");
      payload.module = readInputValue("cfgTCodeModule");
      payload.stage = readInputValue("cfgTCodeStage");
      payload.script = readInputValue("cfgTCodeScript") || payload.script || (code + ".vbs");
      payload.scriptFile = payload.script;
      payload.icon = readInputValue("cfgTCodeIcon") || payload.icon || "terminal";
      payload.params = toArray(readInputValue("cfgTCodeParams"));
      payload.automation = readInputValue("cfgTCodeAutomation") || payload.automation || "openOnly";
      payload.timeout = Number(readInputValue("cfgTCodeTimeout") || 180);
      payload.timeoutSeconds = payload.timeout;
      payload.retry = Number(readInputValue("cfgTCodeRetry") || 0);
      payload.retryCount = payload.retry;
      payload.enabled = readInputChecked("cfgTCodeEnabled");
      return payload;
    }

    function buildRuleConfigPayload(mode, id) {
      const code = (mode === "edit" ? id : readInputValue("cfgRuleCode")).toUpperCase();
      const payload = buildExistingTransactionPayload(code);
      const rangeValue = normalizeRulePlantList(readInputValue("cfgRulePlants"));
      const rangeKind = getRuleRangeKind({ ...payload, code });
      const isBusinessAreaRange = rangeKind === "businessArea";
      const isDateRange = rangeKind === "dateRange";
      const selectableInput = readInputValue("cfgRuleSelectableGroups");
      const businessAreasInput = readInputValue("cfgRuleBusinessAreas");
      const selectableGroupIds = selectableInput ? toArray(selectableInput) : toArray(payload.selectableGroupIds);
      const businessAreas = isBusinessAreaRange ? rangeValue : (businessAreasInput ? toArray(businessAreasInput) : toArray(payload.businessAreas));
      payload.name = readInputValue("cfgRuleName") || payload.name || code;
      payload.script = readInputValue("cfgRuleScript") || payload.script || (code + ".vbs");
      payload.scriptFile = payload.script;
      payload.factoryRule = readInputValue("cfgRuleFactoryRule");
      payload.defaultPlantGroup = readInputValue("cfgRuleDefaultGroup");
      payload.defaultGroup = payload.defaultPlantGroup;
      if (isDateRange) {
        payload.plants = [];
        payload.plantsCsv = "";
        payload.fixedPlants = [];
        payload.fixedPlantsCsv = "";
        payload.businessAreaMode = "none";
        payload.businessAreas = [];
        payload.businessAreasCsv = "";
      } else if (isBusinessAreaRange) {
        payload.plants = [];
        payload.plantsCsv = "";
        payload.fixedPlants = [];
        payload.fixedPlantsCsv = "";
        payload.businessAreaMode = "fixed";
      } else {
        payload.plants = rangeValue;
        payload.plantsCsv = rangeValue.join(",");
        payload.fixedPlants = rangeValue;
        payload.fixedPlantsCsv = rangeValue.join(",");
      }
      payload.selectableGroupIds = selectableGroupIds;
      payload.selectableGroupIdsCsv = selectableGroupIds.join(",");
      payload.businessAreaMode = payload.businessAreaMode || readInputValue("cfgRuleBusinessAreaMode") || "byPlant";
      if (!isDateRange) {
        payload.businessAreas = businessAreas;
        payload.businessAreasCsv = businessAreas.join(",");
      }
      payload.enabled = readInputChecked("cfgRuleEnabled");
      return payload;
    }

    function buildRobotConfigPayload(mode, id) {
      const robotId = mode === "edit" ? id : readInputValue("cfgRobotId");
      const events = toArray(readInputValue("cfgRobotEvents"));
      const payload = {
        id: robotId,
        name: readInputValue("cfgRobotName"),
        robotType: readInputValue("cfgRobotType") || "dingtalk",
        type: readInputValue("cfgRobotType") || "dingtalk",
        group: readInputValue("cfgRobotGroup"),
        targetLabel: readInputValue("cfgRobotGroup"),
        events,
        bindings: events.map(eventName => ({ eventName, enabled: true })),
        enabled: readInputChecked("cfgRobotEnabled")
      };
      const webhookReplacement = document.getElementById("cfgRobotWebhookReplacement")?.value || "";
      const secretReplacement = document.getElementById("cfgRobotSecretReplacement")?.value || "";
      if (webhookReplacement) {
        payload.webhookReplacement = webhookReplacement;
      }
      if (secretReplacement) {
        payload.secretReplacement = secretReplacement;
      }
      return payload;
    }

    function buildConfigPayload(kind, mode, id) {
      if (kind === "plant") return buildPlantConfigPayload(mode, id);
      if (kind === "group") return buildGroupConfigPayload(mode, id);
      if (kind === "tcode") return buildTCodeConfigPayload(mode, id);
      if (kind === "rule") return buildRuleConfigPayload(mode, id);
      if (kind === "robot") return buildRobotConfigPayload(mode, id);
      throw new Error("未知配置类型：" + kind);
    }

    function getConfigResourceId(kind, payload, fallbackId = "") {
      if (kind === "plant") return payload.code || fallbackId;
      if (kind === "group") return payload.id || fallbackId;
      if (kind === "tcode") return payload.code || payload.tcode || fallbackId;
      if (kind === "rule") return payload.code || payload.tcode || fallbackId;
      if (kind === "robot") return payload.id || fallbackId;
      return fallbackId;
    }

    function getConfigResourcePath(kind, id) {
      if (kind === "plant") return CONFIG_API_PATHS.plants(id);
      if (kind === "group") return CONFIG_API_PATHS.plantGroups(id);
      if (kind === "tcode") return CONFIG_API_PATHS.transactionRules(id);
      if (kind === "rule") return CONFIG_API_PATHS.transactionRules(id);
      if (kind === "robot") return CONFIG_API_PATHS.notificationRobots(id);
      throw new Error("未知配置类型：" + kind);
    }

    async function saveConfigFromModal(kind, mode, id = "") {
      if (!canWriteConfig()) return toast("当前 API 未连接，不能保存基础配置；请启动本机 API 后刷新再保存", "warn");
      try {
        const payload = buildConfigPayload(kind, mode, id);
        const resourceId = getConfigResourceId(kind, payload, id);
        if (!resourceId) throw new Error("缺少配置主键");
        await bridgeFetch(getConfigResourcePath(kind, resourceId), {
          method: "PUT",
          body: JSON.stringify(payload)
        });
        state.modal = null;
        await refreshConfigData({ silent: true });
        toast("基础配置已保存", "ok");
        render();
      } catch (err) {
        toast("保存失败：" + err.message, "err");
      }
    }

    async function deleteConfigItem(kind, id) {
      if (!canWriteConfig()) return toast("当前 API 未连接，不能删除基础配置；请启动本机 API 后刷新再删除", "warn");
      if (!id) return toast("缺少配置主键，无法删除", "warn");
      try {
        await bridgeFetch(getConfigResourcePath(kind, id), { method: "DELETE" });
        await refreshConfigData({ silent: true });
        toast("基础配置已删除", "ok");
        render();
      } catch (err) {
        toast("删除失败：" + err.message, "err");
      }
    }

    function testRobot(id) {
      const robot = robots.find(item => item.id === id);
      if (!canWriteConfig()) return toast("当前离线，只显示机器人配置状态，不能测试推送", "warn");
      toast("已请求测试通知：" + (robot?.name || id || "通知机器人"), "ok");
    }

    function login() {
      const account = document.getElementById("loginAccount")?.value || "张三";
      state.user.name = account.trim() || "张三";
      state.form.useDefaultNotifyUser = readInputChecked("useDefaultNotifyUser");
      state.user.dingTalkUserId = getResolvedNotifyUserId();
      localStorage.setItem("portalUser", state.user.name);
      localStorage.setItem("portalDingTalkUserId", state.user.dingTalkUserId);
      localStorage.setItem("portalUseDefaultNotifyUser", state.form.useDefaultNotifyUser ? "1" : "0");
      localStorage.setItem("portalLoggedIn", "1");
      state.loggedIn = true;
      if (!state.form.useDefaultNotifyUser && !state.externalAuth.claimedAccount) {
        toast("未从 URL token 解析到 Account，已使用联调默认通知人", "warn");
      }
      toast("登录成功", "ok");
      render();
      refreshBridgeData({ silent: true });
    }

    async function openRunLog(id) {
      if (state.bridge.online && id) {
        try {
          const run = await bridgeFetch("/api/runs/" + encodeURIComponent(id));
          const index = history.findIndex(x => x.id === id);
          const normalized = normalizeRunFromApi(run);
          if (index >= 0) history[index] = normalized;
        } catch (err) {
          toast("读取日志详情失败：" + err.message, "warn");
        }
      }

      state.modal = "log:" + id;
      render();
    }

    function logout() {
      localStorage.removeItem("portalLoggedIn");
      state.loggedIn = false;
      state.page = "dashboard";
      render();
    }

    function toggleRole() {
      state.role = state.role === "viewer" ? "executor" : "viewer";
      localStorage.setItem("portalRole", state.role);
      toast("已切换为" + (state.role === "viewer" ? "查询员" : "执行员"), "ok");
      render();
    }

    function goExecute(tCode) {
      state.form.tCode = tCode;
      state.selectedTCode = tCode;
      syncExecutionDefaults(true);
      state.page = "execute";
      render();
    }

    function wakeProtocol() {
      launchProtocol();
      addLog("WARN", "协议唤醒模式没有浏览器回传，结果需要查看本地日志。");
      toast("已发起协议唤醒", "info");
    }

    async function startRun() {
      if (state.role !== "executor" || state.executing) return;
      state.executing = true;
      resetRun();
      const payload = buildRunPayload();
      state.currentRun = { id: "RUN-" + new Date().toISOString().replace(/\D/g, "").slice(0, 14), payload };
      setStep(0, "run");
      addLog("INFO", "提交任务：" + state.currentRun.id);
      addLog("INFO", "执行范围：" + payload.factoryGroupName + " / " + payload.rangeLabel + " " + payload.rangeCsv);
      render();

      try {
        const created = await createBridgeRun(payload);
        state.currentRun.id = created.runId;
        mergeQueueInfo(created, created.runId);
        setStep(0, "done");
        setStep(1, "run");
        addLog("INFO", "任务已入队 runId：" + created.runId);
        if (state.bridge.queueMode === "disabled") {
          setStep(1, "pending");
          addLog("WARN", "当前后台队列已禁用，只写入 SQLite，不会启动 SAP。请重启执行器并取消 SAP_RPA_DISABLE_QUEUE 后再执行。");
          toast("已创建任务，但队列禁用：只入库不执行", "warn");
        } else {
          addRunStatusLog("INFO", "queued-" + created.runId, "已进入串行队列，" + queueAheadText(state.currentRun));
          toast("已提交入队，" + queueAheadText(state.currentRun), "ok");
        }
        render();
        pollRunStatus(created.runId, 0);
      } catch (err) {
        addLog("WARN", "本机 API 不可用，回退到协议唤醒：" + err.message);
        wakeProtocol();
        setTimeout(() => {
          setStep(1, "done");
          setStep(2, "done");
          setStep(3, "run");
          addLog("WARN", "协议唤醒模式没有 API 回写，真实执行结果以本机日志为准。");
          finishLocalSimulation("协议唤醒已完成，结果请查看本地日志", "WARN");
        }, 850);
      }
    }

    async function createBridgeRun(payload) {
      const notifyUserId = getResolvedNotifyUserId();
      state.user.dingTalkUserId = notifyUserId;
      const runParams = payload.rangeKind === "dateRange"
        ? {
            period: payload.period,
            weekEnd: payload.weekEnd
          }
        : {
            plants: payload.plantsCsv,
            plant: payload.plant,
            plantCodes: payload.plantsCsv,
            factoryCodes: payload.plantsCsv,
            factoryGroup: payload.factoryGroup,
            businessAreas: payload.businessAreasCsv,
            period: payload.period,
            weekEnd: payload.weekEnd,
            remark: payload.remark
          };
      return bridgeFetch("/api/runs", {
        method: "POST",
        body: JSON.stringify({
          transactionCode: payload.tcode,
          source: "company-portal-v2",
          notifyTarget: payload.notify ? "dingtalk" : "",
          priority: 0,
          maxAttempts: 1,
          operator: {
            id: state.user.name,
            name: state.user.name,
            dept: state.user.dept,
            dingTalkUserId: notifyUserId,
            ddid: notifyUserId
          },
          params: runParams
        })
      });
    }

    function launchProtocol(runId = "") {
      const url = buildProtocolUrl(runId);
      const payload = buildRunPayload();
      addLog("INFO", "协议唤醒 SapWebLauncher：" + payload.tcode + "，" + payload.rangeLabel + " " + payload.rangeCsv);
      location.href = url;
    }

    async function pollRunStatus(runId, count) {
      if (state.bridge.queueMode === "disabled") {
        state.executing = false;
        await refreshBridgeData({ silent: true });
        return;
      }

      if (!runId || count > 1200) {
        state.executing = false;
        await refreshBridgeData({ silent: true });
        return;
      }

      try {
        if (count % 5 === 0) await refreshQueueStatus({ silent: true, runId });
        const run = await bridgeFetch("/api/runs/" + encodeURIComponent(runId));
        const status = normalizeRunStatus(run.status);
        mergeQueueInfo(run, runId);
        mergeRunLogs(run);
        if (status === "queued") {
          setStep(0, "done");
          setStep(1, "run");
          addRunStatusLog("INFO", "queued-" + queueAheadText(state.currentRun), "排队中，" + queueAheadText(state.currentRun));
        }
        if (status === "running") {
          setStep(1, "done");
          setStep(2, "done");
          setStep(3, "run");
          addRunStatusLog("INFO", "running-" + runId, "执行器已领取任务，开始占用 SAP GUI 桌面会话");
        }
        if (status === "success" || status === "partial_failed" || status === "failed" || status === "canceled") {
          setStep(1, "done");
          setStep(2, "done");
          setStep(3, status === "success" ? "done" : "fail");
          setStep(4, status === "success" ? "done" : "fail");
          if (!Array.isArray(run.logs) || run.logs.length === 0) {
            addLog(status === "success" ? "OK" : "ERR", run.message || run.sapStatusText || status);
          }
          state.executing = false;
          await refreshBridgeData({ silent: true });
          toast(status === "success" ? "执行成功，结果已写入 SQLite" : "执行结束，存在失败项，结果已写入 SQLite", status === "success" ? "ok" : "warn");
          return;
        }
        render();
      } catch (err) {
        addLog("WARN", "查询 run 状态失败：" + err.message);
      }

      setTimeout(() => pollRunStatus(runId, count + 1), 1500);
    }

    function simulateRun() {
      const payload = buildRunPayload();
      const flow = [
        () => { setStep(1, "done"); setStep(2, "run"); addLog("INFO", "准备 SAP GUI 会话并注入事务码参数"); },
        () => { setStep(2, "done"); setStep(3, "run"); addLog("INFO", "执行 " + payload.tcode + "，" + payload.rangeLabel + "参数：" + payload.rangeCsv); },
        () => { setStep(3, "done"); setStep(4, "run"); addLog("OK", payload.rangeLabel + "参数：" + payload.rangeCsv); },
        () => finishLocalSimulation("执行完成，已模拟推送钉钉通知", "OK")
      ];
      flow.forEach((fn, i) => setTimeout(() => { fn(); render(); }, 650 * (i + 1)));
    }

    function finishLocalSimulation(message, type) {
      if (state.steps[2].status !== "done") setStep(2, "done");
      if (state.steps[3].status !== "done") setStep(3, "done");
      setStep(4, type === "OK" ? "done" : "run");
      addLog(type, message);
      state.executing = false;
      toast(message, type === "OK" ? "ok" : "warn");
      render();
    }

    function buildRunPayload() {
      syncExecutionDefaults(false);
      const t = getTCode(state.form.tCode);
      const group = getFactoryGroup(state.form.factoryGroup);
      const rangeMeta = getRuleRangeMeta(t);
      const isBusinessAreaRange = rangeMeta.isBusinessArea;
      const isDateRange = rangeMeta.isDateRange;
      const selectedRange = getRunRangeForTCode(state.form.tCode, state.form.plants, state.form.factoryGroup);
      const dateRange = getLastFullWeekDateRange();
      const selectedPlants = isBusinessAreaRange || isDateRange ? [] : selectedRange;
      const businessAreas = isDateRange
        ? []
        : isBusinessAreaRange
          ? selectedRange
          : getBusinessAreasForSelection(state.form.tCode, selectedPlants, state.form.factoryGroup);
      const rangeValues = isDateRange ? [dateRange.period, dateRange.weekEnd] : selectedRange;
      const rangeLabel = isDateRange ? "日期范围" : (isBusinessAreaRange ? "业务范围" : "工厂");
      return {
        source: "netlify-static-portal",
        action: "run",
        tcode: state.form.tCode,
        script: t.script,
        factoryGroup: state.form.factoryGroup,
        factoryGroupName: group.name,
        plants: selectedPlants,
        plant: selectedPlants[0] || "",
        plantsCsv: selectedPlants.join(","),
        businessAreas,
        businessAreasCsv: businessAreas.join(","),
        period: dateRange.period,
        weekEnd: dateRange.weekEnd,
        rangeKind: isDateRange ? "dateRange" : (isBusinessAreaRange ? "businessArea" : "plant"),
        rangeLabel,
        rangeValues,
        rangeCsv: rangeValues.join(","),
        rangeEmptyText: isDateRange ? "按系统日期上一完整周执行" : (isBusinessAreaRange ? "未配置业务范围" : "未配置工厂"),
        notify: state.form.notify,
        remark: state.form.remark,
        createdBy: state.user.name,
        createdAt: new Date().toISOString()
      };
    }

    function buildProtocolUrl(runId = "") {
      const p = buildRunPayload();
      const params = new URLSearchParams({
        action: "run",
        tcode: p.tcode,
        script: p.script,
        period: p.period,
        weekEnd: p.weekEnd
      });
      if (p.rangeKind !== "dateRange") {
        params.set("plant", p.plant);
        params.set("plants", p.plantsCsv);
        params.set("plantCodes", p.plantsCsv);
        params.set("factoryCodes", p.plantsCsv);
        params.set("businessAreas", p.businessAreasCsv);
        params.set("factoryGroup", p.factoryGroup);
      }
      if (runId) params.set("runId", runId);
      return `sap-rpa://run?${params.toString()}`;
    }

    function resetRun() {
      state.steps = state.steps.map(step => ({ ...step, status: "pending", time: "" }));
      state.logs = [];
    }

    function setStep(index, status) {
      state.steps[index].status = status;
      state.steps[index].time = nowTime();
    }

    function addLog(type, message) {
      state.logs.push({ type, message, time: nowTime() });
      if (state.logs.length > 80) state.logs.shift();
    }

    function exportHistory() {
      const rows = [["执行时间","任务名称","事务码","工厂","时长","状态","通知","结果摘要"], ...history.map(x => [x.time, x.task, x.tCode, x.plant, x.duration, x.status, x.notify, x.result])];
      const csv = rows.map(row => row.map(cell => `"${String(cell).replace(/"/g, '""')}"`).join(",")).join("\n");
      const blob = new Blob(["\ufeff" + csv], { type: "text/csv;charset=utf-8" });
      const link = document.createElement("a");
      link.href = URL.createObjectURL(blob);
      link.download = "sap-rpa-history.csv";
      link.click();
      URL.revokeObjectURL(link.href);
      toast("执行历史已导出", "ok");
    }

    function nowTime() {
      return new Date().toLocaleTimeString("zh-CN", { hour12: false });
    }

    function toast(message, type = "info") {
      const wrap = document.getElementById("toastWrap");
      const node = document.createElement("div");
      node.className = "toast " + type;
      node.textContent = message;
      wrap.appendChild(node);
      setTimeout(() => node.remove(), 3600);
    }
