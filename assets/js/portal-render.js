    function render() {
      app.innerHTML = renderShell();
      bindEvents();
      lucide.createIcons();
    }

    async function refreshBridgeData({ silent = true } = {}) {
      const checkedAt = new Date().toLocaleTimeString("zh-CN", { hour12: false });
      try {
        const health = await bridgeFetch("/api/health");
        state.bridge = {
          online: !!health.ok,
          lastChecked: checkedAt,
          database: health.database || "",
          queueMode: health.queueMode || ""
        };
      } catch (err) {
        state.bridge.online = false;
        state.bridge.queueMode = "";
        state.bridge.lastChecked = checkedAt;
        state.queue = { online: false, runningRunId: "", queuedCount: null, queuePosition: null, runsAhead: null, workItemsAhead: null, lastChecked: checkedAt };
        state.config = { online: false, source: "fallback", lastLoaded: checkedAt, error: err.message || "本机 API 不可用" };
        clearReportData("本机 API 不可用，请启动 API");
        restoreFallbackConfig();
        if (!silent) toast("本机执行器未启动，基础配置暂不能落库", "warn");
        render();
        return;
      }

      try {
        const txData = await bridgeFetch("/api/transactions");
        if (Array.isArray(txData.transactions) && txData.transactions.length) {
          tCodes = txData.transactions.map(normalizeTransactionFromApi);
        }
      } catch (err) {
        if (!silent) toast("事务码接口暂不可用，继续使用当前列表", "warn");
      }

      await refreshConfigData({ silent: true });
      await refreshQueueStatus({ silent: true });
      await refreshReportData({ silent: true, renderAfter: false, refreshHistory: false });
      await refreshHistoryData({ silent });

      if (!silent) toast(state.config.online ? "已连接本机执行器并刷新数据" : "API 已连接，但基础配置接口不可用", state.config.online ? "ok" : "warn");
      render();
    }

    function reportDefaultRange() {
      const toDate = new Date();
      return { from: formatDateInputValue(toDate), to: formatDateInputValue(toDate) };
    }

    function formatDateInputValue(date) {
      const year = date.getFullYear();
      const month = String(date.getMonth() + 1).padStart(2, "0");
      const day = String(date.getDate()).padStart(2, "0");
      return `${year}-${month}-${day}`;
    }

    function getReportRangeFromState() {
      const defaults = reportDefaultRange();
      return {
        from: state.report.from || defaults.from,
        to: state.report.to || defaults.to
      };
    }

    function getReportRangeFromInputs() {
      const defaults = getReportRangeFromState();
      return {
        from: readInputValue("reportStart") || defaults.from,
        to: readInputValue("reportEnd") || defaults.to
      };
    }

    function buildReportQuery(range) {
      const params = new URLSearchParams();
      if (range.from) params.set("from", `${range.from} 00:00:00`);
      if (range.to) params.set("to", `${range.to} 23:59:59`);
      const query = params.toString();
      return "/api/reports/execution" + (query ? "?" + query : "");
    }

    function buildRunsQuery({ limit = 20, from = "", to = "", status = "" } = {}) {
      const params = new URLSearchParams();
      params.set("limit", String(limit));
      if (status) params.set("status", status);
      if (from) params.set("from", `${from} 00:00:00`);
      if (to) params.set("to", `${to} 23:59:59`);
      return "/api/runs?" + params.toString();
    }

    async function refreshHistoryData({ silent = true, range = getReportRangeFromState(), limit = 20 } = {}) {
      try {
        const runData = await bridgeFetch(buildRunsQuery({ limit, ...range }));
        if (Array.isArray(runData.runs)) {
          history = runData.runs.map(normalizeRunFromApi);
        }
      } catch (err) {
        if (!silent) toast("执行历史接口暂不可用，基础配置仍可维护", "warn");
      }
    }

    async function refreshReportData({ silent = true, renderAfter = true, refreshHistory = true, from = "", to = "" } = {}) {
      const range = from || to ? { from, to } : getReportRangeFromState();
      state.report = { ...state.report, loading: true, from: range.from, to: range.to, error: "" };
      try {
        const data = await bridgeFetch(buildReportQuery(range));
        state.report = {
          online: true,
          loading: false,
          from: data.period?.from ? String(data.period.from).slice(0, 10) : range.from,
          to: data.period?.to ? String(data.period.to).slice(0, 10) : range.to,
          summary: data.summary || null,
          transactionRanking: Array.isArray(data.transactionRanking) ? data.transactionRanking : [],
          error: ""
        };
        if (refreshHistory) await refreshHistoryData({ silent: true, range: getReportRangeFromState() });
        if (!silent) toast("报表数据已刷新", "ok");
      } catch (err) {
        clearReportData(err.message || "本机 API 不可用，请启动 API");
        if (!silent) toast("报表接口不可用，请启动 API", "warn");
      } finally {
        state.report.loading = false;
        if (renderAfter) render();
      }
    }

    function clearReportData(error = "") {
      const range = getReportRangeFromState();
      state.report = {
        online: false,
        loading: false,
        from: range.from,
        to: range.to,
        summary: null,
        transactionRanking: [],
        error
      };
    }

    async function bridgeFetch(path, options = {}) {
      const response = await fetch(BRIDGE_API + path, {
        ...options,
        headers: { "Content-Type": "application/json", ...(options.headers || {}) }
      });
      const text = await response.text();
      const data = text ? JSON.parse(text) : {};
      if (!response.ok) throw new Error(data.error || response.statusText);
      return data;
    }

    function renderNotifyUserControl() {
      const notifyUserId = getResolvedNotifyUserId();
      const isBlocked = isNotifyUserBlocked();
      const detailText = notifyUserId
        ? `当前提交通知人：${notifyUserId}（${notifyUserSourceText()}）`
        : notifyUserBlockingText();
      return `
        <div class="field">
          <label class="checkbox-card">
            <input id="useDefaultNotifyUser" type="checkbox" ${state.form.useDefaultNotifyUser === false ? "" : "checked"}>
            <span>
              <strong>勾选固定通知 11464769</strong>
              <span>${esc(detailText)}</span>
            </span>
          </label>
          ${isBlocked ? `<div class="notice warn">${esc(notifyUserBlockingText())}</div>` : ""}
        </div>
      `;
    }

    function renderShell() {
      const headerUserName = state.user.name || state.externalAuth.claimedUserName || "本机用户";
      const headerDingTalkId = state.user.dingTalkUserId || state.externalAuth.claimedAccount || DEFAULT_DINGTALK_USER_ID;
      const headerAvatarText = headerUserName.slice(0, 1) || "用";
      return `
        <div class="app-shell">
          <aside class="sidebar">
            <div class="logo"><span class="brand-mark">${icon("workflow")}</span><span>SAP 门户</span></div>
            <nav class="nav">
              ${navButton("dashboard", "layout-dashboard", "工作台")}
              ${navButton("execute", "play-circle", "执行任务")}
              ${navButton("reports", "bar-chart-3", "统计报表")}
              ${navButton("schedule", "calendar-clock", "定时任务")}
              ${navButton("config", "sliders-horizontal", "基础配置")}
            </nav>
          </aside>
          <main class="main">
            <header class="topbar">
              <div>
                <div class="breadcrumb">首页 / <span class="page-title">${pages[state.page] || ""}</span></div>
                <div class="panel-sub">${renderPageSubTitle()}</div>
              </div>
              <div class="top-actions">
                <span class="badge ${state.role === "viewer" ? "viewer" : "executor"}">${icon(state.role === "viewer" ? "search" : "shield-check")} ${state.role === "viewer" ? "查询员" : "执行员"}</span>
                <button class="btn small" data-action="toggle-role">${icon("refresh-cw")}切换角色</button>
                <span class="avatar">${esc(headerAvatarText)}</span>
                <span class="identity-pill" title="姓名：${esc(headerUserName)}&#10;钉钉ID：${esc(headerDingTalkId)}">
                  <span class="identity-name">${esc(headerUserName)}</span>
                  <span class="identity-ddid">钉钉ID：${esc(headerDingTalkId)}</span>
                </span>
              </div>
            </header>
            ${renderCurrentPage()}
          </main>
        </div>
        ${state.modal ? renderModal() : ""}
      `;
    }

    function navButton(page, iconName, text) {
      return `<button class="${state.page === page ? "active" : ""}" data-page="${page}">${icon(iconName)}${text}</button>`;
    }

    function renderPageSubTitle() {
      const subtitles = {
        dashboard: "",
        execute: "选择事务码和执行范围，提交到后台串行队列。",
        reports: "",
        schedule: "",
        config: "维护工厂、业务范围、事务码和工厂规则。"
      };
      return subtitles[state.page] || "";
    }

    function renderCurrentPage() {
      const map = {
        dashboard: renderDashboard,
        execute: renderExecute,
        reports: renderReports,
        schedule: renderSchedule,
        config: renderConfig
      };
      return (map[state.page] || renderDashboard)();
    }

    function renderDashboard() {
      return `
        <section class="panel">
          <div class="panel-body">
            ${renderTransactionGroups()}
          </div>
        </section>
      `;
    }

    function renderReports() {
      const range = getReportRangeFromState();
      const stats = buildReportStats(state.report);
      return `
        <section class="panel">
          <div class="panel-header"><div><div class="panel-title">${icon("bar-chart-3")}任务执行统计报表</div></div></div>
          <div class="panel-body">
            <div class="report-toolbar">
              <div class="field"><label>统计周期</label><input id="reportStart" type="date" value="${esc(range.from)}"></div>
              <div class="field"><label>至</label><input id="reportEnd" type="date" value="${esc(range.to)}"></div>
              <button class="btn primary" data-action="refresh-report" ${state.report.loading ? "disabled" : ""}>${icon("search")}${state.report.loading ? "查询中" : "查询"}</button>
              <button class="btn" data-action="export-history">${icon("download")}导出报表</button>
            </div>
            ${state.report.error ? `<div class="notice warn">${esc(state.report.error)}</div>` : ""}
            <div class="report-metrics">
              ${reportMetric("执行总次数", stats.total)}
              ${reportMetric("成功率", stats.successRate + "%")}
              ${reportMetric("平均耗时", stats.avgDuration)}
              ${reportMetric("节省工时", stats.totalDuration)}
            </div>
            ${renderTCodeRankTable(stats.tcodes)}
          </div>
        </section>
      `;
    }

    function reportMetric(label, value) {
      return `<div class="report-metric"><div class="report-metric-value">${esc(value)}</div><div class="report-metric-label">${esc(label)}</div></div>`;
    }

    function buildReportStats(report) {
      const summary = report?.summary || {};
      const total = Number(summary.totalRuns || 0);
      const success = Number(summary.successRuns || 0);
      const ranking = Array.isArray(report?.transactionRanking) ? report.transactionRanking : [];
      const totalDurationSeconds = Number(summary.totalDurationSeconds || 0);
      return {
        total,
        successRate: formatRate(summary.successRate),
        avgDuration: formatSeconds(summary.avgDurationSeconds),
        totalDuration: formatSeconds(totalDurationSeconds),
        tcodes: ranking.map(row => ({
          code: row.transactionCode || "-",
          name: row.transactionName || row.transactionCode || "-",
          total: Number(row.totalRuns || 0),
          success: Number(row.successRuns || 0),
          failed: Number(row.failedRuns || 0),
          successRate: formatRate(row.successRate),
          avgDuration: formatSeconds(row.avgDurationSeconds)
        }))
      };
    }

    function formatRate(value) {
      const rate = Number(value || 0);
      return Math.round(rate * 1000) / 10;
    }

    function formatSeconds(value) {
      const seconds = Number(value || 0);
      if (!Number.isFinite(seconds) || seconds <= 0) return "-";
      const rounded = Math.round(seconds);
      if (rounded < 60) return rounded + "秒";
      const hours = Math.floor(rounded / 3600);
      const minutes = Math.floor((rounded % 3600) / 60);
      const remainSeconds = rounded % 60;
      if (hours > 0) return `${hours}小时${String(minutes).padStart(2, "0")}分`;
      return `${minutes}分${String(remainSeconds).padStart(2, "0")}秒`;
    }

    function formatNumber(value) {
      const number = Number(value || 0);
      return Number.isInteger(number) ? String(number) : String(Math.round(number * 10) / 10);
    }

    function renderTCodeRankTable(rows) {
      return `<section class="panel"><div class="panel-header"><div class="panel-title">${icon("trophy")}事务码执行排行</div></div><div class="panel-body table-wrap"><table><thead><tr><th>排名</th><th>事务码</th><th>执行次数</th><th>成功</th><th>失败</th><th>成功率</th><th>平均耗时</th></tr></thead><tbody>${rows.length ? rows.map((row, i) => `<tr><td>${i + 1}</td><td><span class="area-chip">${esc(row.code)}</span><span class="muted"> ${esc(row.name)}</span></td><td>${row.total}</td><td>${row.success}</td><td>${row.failed}</td><td>${row.successRate}%</td><td>${row.avgDuration}</td></tr>`).join("") : `<tr><td colspan="7" class="empty-cell">暂无数据</td></tr>`}</tbody></table></div></section>`;
    }

    function metric(label, value, hint) {
      return `<div class="metric"><div class="label">${esc(label)}</div><div class="value">${esc(value)}</div><div class="hint">${esc(hint)}</div></div>`;
    }

    function renderHistoryPanel() {
      return `
        <section class="panel">
          <div class="panel-header">
            <div><div class="panel-title">${icon("history")}执行历史</div><div class="panel-sub">${state.bridge.online ? "来自本机 SQLite run 记录" : "本机 API 未连接，显示示例记录"}</div></div>
            <div class="top-actions">
              <button class="btn small" data-action="refresh-bridge">${icon("refresh-cw")}刷新</button>
              <button class="btn small" data-action="export-history">${icon("download")}导出</button>
            </div>
          </div>
          <div class="panel-body">
            <div class="table-wrap">
              <table>
                <thead><tr><th>执行时间</th><th>事务码</th><th>工厂</th><th>耗时</th><th>状态</th><th>结果摘要</th><th>操作</th></tr></thead>
                <tbody>${history.map(row => `
                  <tr>
                    <td>${esc(row.time || "-")}</td>
                    <td><strong>${esc(row.tCode)}</strong></td>
                    <td>${esc(row.plant || "-")}</td>
                    <td>${esc(row.duration)}</td>
                    <td><span class="tag ${row.status === "成功" ? "ok" : row.status === "失败" ? "warn" : "info"}">${esc(row.status)}</span></td>
                    <td>${esc(row.result || "-")}</td>
                    <td><button class="btn small" data-action="view-log" data-run="${esc(row.id)}">${icon("file-text")}日志</button></td>
                  </tr>
                `).join("")}</tbody>
              </table>
            </div>
          </div>
        </section>
      `;
    }

    const dashboardWorkflowGroups = [
      {
        title: "采购价",
        summary: "ZFI072A → ZFI072N",
        entries: [
          { code: "ZFI072A", note: "四个集采工厂 + 其他工厂" },
          { code: "ZFI072N" }
        ]
      },
      {
        title: "产值拆分",
        summary: "ZFI057 single workflow entry",
        entries: [
          { code: "ZFI057" }
        ]
      },
      {
        title: "实际领料",
        summary: "ZFIR034 → ZFI080 → ZFI080B",
        entries: [
          { code: "ZFIR034", displayName: "实际领料日期范围" },
          { code: "ZFI080" },
          { code: "ZFI080B" }
        ]
      },
      {
        title: "标准价",
        summary: "ZCO019-明细、ZCO019汇总",
        entries: [
          { code: "ZCO019", displayCode: "ZCO019-明细", note: "明细" },
          { code: "ZCO019", displayCode: "ZCO019汇总", note: "汇总" }
        ]
      },
      {
        title: "周结完工成本明细表",
        summary: "ZFI019NL、ZFI019NA、ZFI148",
        entries: [
          { code: "ZFI019NL" },
          { code: "ZFI019NA" },
          { code: "ZFI148" }
        ]
      }
    ];

    function renderTransactionGroups() {
      const activeByCode = new Map(activeTCodes().map(item => [String(item.code || "").toUpperCase(), item]));
      const groups = dashboardWorkflowGroups.map((group, groupIndex) => {
        const items = group.entries.map((entry, index) => {
          const base = activeByCode.get(String(entry.code || "").toUpperCase());
          if (!base) return null;
          return {
            ...base,
            displayCode: entry.displayCode || base.code,
            displayName: entry.displayName || base.name,
            dashboardNote: entry.note || "",
            workflowTitle: group.title,
            workflowStep: index + 1,
            runCode: base.code
          };
        }).filter(Boolean);
        if (!items.length) return "";
        return `
          <div class="transaction-group">
            <div class="transaction-group-head">
              <div class="transaction-group-title">
                <span class="workflow-index">${String(groupIndex + 1).padStart(2, "0")}</span>
                <div>
                  <div class="transaction-group-name">${esc(group.title)}</div>
                </div>
              </div>
              <span class="tag info">${items.length}个入口</span>
            </div>
            <div class="transaction-grid">${items.map(renderTransactionCard).join("")}</div>
          </div>
        `;
      }).filter(Boolean).join("");
      return `<div class="transaction-groups">${groups || `<div class="notice warn">暂无可展示事务码，请在基础配置启用对应事务码。</div>`}</div>`;
    }

    function renderTransactionCard(item) {
      const displayCode = item.displayCode || item.code;
      const displayName = item.displayName || item.name || item.code;
      const saveAction = isSaveActionTransaction(item);
      const displayNameText = saveAction && !String(displayName).includes("保存") ? `${displayName}（保存）` : displayName;
      const runCode = item.runCode || item.code;
      return `
        <div class="transaction-card">
          <div class="transaction-step">步骤 ${esc(item.workflowStep || "-")}</div>
          <div class="transaction-card-head">
            <div class="action-icon">${icon(item.icon || "terminal")}</div>
            <div class="transaction-meta">
              <div class="transaction-code">${esc(displayCode)}</div>
              <div class="transaction-name">${renderSaveName(displayNameText)}</div>
              ${item.dashboardNote ? `<div class="transaction-note">${esc(item.dashboardNote)}</div>` : ""}
              <div class="transaction-tags">
                <span class="tag ${item.automation === "script" ? "ok" : "info"}">${item.automation === "script" ? "脚本" : "打开"}</span>
              </div>
            </div>
          </div>
          <button class="btn primary small" data-action="go-execute" data-tcode="${esc(runCode)}">${icon("send")}提交</button>
        </div>
      `;
    }

    function renderSaveName(value) {
      return esc(value).replaceAll("保存", '<span class="transaction-save-word">保存</span>');
    }

    function isSaveActionTransaction(item) {
      const saveActionCodes = new Set(["ZFI072A", "ZFI072N", "ZFI080", "ZFI080B", "ZCO019", "ZCO020", "ZFI019NA", "ZFI019NL", "ZFI019NI", "ZFI148"]);
      const code = String(item.runCode || item.code || "").trim().toUpperCase();
      const text = [
        item.name,
        item.displayName,
        item.dashboardNote,
        item.factoryRule,
        item.script,
        item.scriptFile
      ].filter(Boolean).join(" ");
      return saveActionCodes.has(code) || /保存|导出|save\/export|save button|export/i.test(text);
    }

    function quickAction(code, desc, moduleName, iconName) {
      return `
        <div class="action-card">
          <div class="action-top">
            <div class="action-icon">${icon(iconName)}</div>
            <div class="action-copy">
              <div class="action-name">${esc(code)}</div>
              <div class="action-desc">${esc(desc)}</div>
            </div>
          </div>
          <button class="btn primary small" data-action="go-execute" data-tcode="${esc(code)}">${icon("send")}提交</button>
        </div>
      `;
    }

    function renderWorkflowStage(stage, index) {
      return `
        <div class="workflow-stage">
          <div class="stage-head"><span class="stage-num">${index + 1}</span><span>${esc(stage.title)}</span></div>
          <div class="logic-list">
            ${stage.items.map(item => `
              <div class="logic-node ${item.kind || ""}">
                <strong>${esc(item.name)}</strong>
                <span>${esc(item.desc)}</span>
              </div>
            `).join("")}
          </div>
          ${stage.note ? `<div class="logic-note">${esc(stage.note)}</div>` : ""}
        </div>
      `;
    }

    function renderExecute() {
      syncExecutionDefaults();
      const executableTCodes = dashboardExecutableTCodes();
      const notifyBlocked = isNotifyUserBlocked();
      return `
        <section class="grid cols-2">
          <div class="panel">
            <div class="panel-header">
              <div><div class="panel-title">${icon("play-circle")}执行任务</div><div class="panel-sub">选择事务码后，按后台配置自动带入执行范围。</div></div>
              <span class="tag ${state.role === "executor" ? "ok" : "warn"}">${state.role === "executor" ? "可执行" : "只读"}</span>
            </div>
            <div class="panel-body">
              <div class="execute-shell">
                ${state.role === "viewer" ? `<div class="notice warn">当前是查询员角色，不能发起 SAP 执行。切换为执行员后再提交任务。</div>` : ""}
                <div class="execute-form-grid">
                  <div class="field">
                    <label for="tCode">事务码</label>
                    <select id="tCode" data-bind="tCode">${executableTCodes.map(x => `<option value="${x.code}" ${state.form.tCode === x.code ? "selected" : ""}>${x.code} - ${x.name}</option>`).join("")}</select>
                  </div>
                </div>
                ${renderExecutePlants()}
                ${renderNotifyUserControl()}
                <div class="execute-actions">
                  <button class="btn primary" data-action="start-run" ${state.role !== "executor" || state.executing || notifyBlocked ? "disabled" : ""}>${icon("send")}提交入队</button>
                </div>
              </div>
            </div>
          </div>
          <div class="panel">
            <div class="panel-header"><div><div class="panel-title">${icon("list-checks")}执行进度与日志</div><div class="panel-sub">${state.currentRun ? esc(state.currentRun.id) : "尚未提交任务"}</div></div></div>
            <div class="panel-body">
              ${renderQueueStrip()}
              <div class="steps">${state.steps.map((step, i) => renderStep(step, i)).join("")}</div>
              <div style="height:14px"></div>
              <div class="log" id="logBox">${state.logs.length ? state.logs.map(renderLog).join("") : `<div class="log-line"><span class="log-time">[--:--:--]</span><span class="INFO">等待执行。</span></div>`}</div>
            </div>
          </div>
        </section>
      `;
    }

    function renderQueueStrip() {
      const activeRun = state.currentRun;
      const queuedCount = toQueueNumber(activeRun?.queuedCount ?? state.queue.queuedCount);
      const runningRunId = activeRun?.runningRunId || state.queue.runningRunId || "";
      const status = activeRun ? queueAheadText(activeRun) : (state.queue.online ? "队列可查" : "等待提交");
      return `
        <div class="queue-strip">
          <div class="queue-line">
            <div class="queue-main">${esc(status)}</div>
            <span class="tag ${runningRunId ? "info" : "ok"}">${runningRunId ? "SAP 正在执行" : "SAP 空闲/待领取"}</span>
          </div>
          <div class="queue-meta">
            <span>等待任务：${queuedCount === null ? "-" : queuedCount}</span>
            <span>运行中：${runningRunId ? esc(runningRunId) : "-"}</span>
            <span>更新：${esc(state.queue.lastChecked || "--:--:--")}</span>
          </div>
        </div>
        <div style="height:12px"></div>
      `;
    }

    function renderExecutePlants() {
      const payload = buildRunPayload();
      const chips = payload.rangeValues.map((code, index) => {
        const label = payload.rangeKind === "dateRange" ? (index === 0 ? "开始" : "截止") + " " : "";
        return `<span class="area-chip">${esc(label + code)}</span>`;
      }).join("") || `<span class="area-chip">${esc(payload.rangeEmptyText)}</span>`;
      const weekOverride = state.form.tCode === "ZFI057" ? renderZfi057WeekOverride(payload) : "";
      return `
        <div class="execute-range">
          <div class="execute-range-row">
            <div class="execute-range-label">执行${esc(payload.rangeLabel)}</div>
            <div class="area-chips">${chips}</div>
          </div>
          ${weekOverride}
        </div>
      `;
    }

    function renderZfi057WeekOverride(payload) {
      const currentRange = normalizeZfi057WeekOverride();
      const inputText = payload.dateRangeSource === "server"
        ? "服务器上一完整周（提交时由后端计算）"
        : `${payload.period} 至 ${payload.weekEnd}`;
      return `
          <div class="zfi057-week-override">
            <label class="checkbox-card compact">
              <input id="useCustomZfi057Week" type="checkbox" ${state.form.useCustomZfi057Week === false ? "" : "checked"}>
              <span>
                <strong>指定测试周</strong>
                <span>勾选后按下方日期执行；取消勾选后不传日期，由后端按服务器日期计算上一完整周。</span>
              </span>
            </label>
            <div class="week-range-inputs">
              <label>开始日期<input id="customZfi057WeekStart" type="date" value="${esc(sapDateToInputValue(currentRange.period))}" ${state.form.useCustomZfi057Week === false ? "disabled" : ""}></label>
              <label>截止日期<input id="customZfi057WeekEnd" type="date" value="${esc(sapDateToInputValue(currentRange.weekEnd))}" ${state.form.useCustomZfi057Week === false ? "disabled" : ""}></label>
              <span class="table-hint">本次入参：${esc(inputText)}</span>
            </div>
          </div>
      `;
    }
    function unique(list) {
      return [...new Set((list || []).filter(Boolean))];
    }

    function getTCode(code) {
      return tCodes.find(x => x.code === code) || tCodes.find(x => x.code === "ZFI019NL") || tCodes[0];
    }

    function activeTCodes() {
      return tCodes.filter(t => t.enabled !== false);
    }

    function dashboardExecutableTCodes() {
      const activeByCode = new Map(activeTCodes().map(item => [String(item.code || "").toUpperCase(), item]));
      const seen = new Set();
      const result = [];
      dashboardWorkflowGroups.forEach(group => {
        group.entries.forEach(entry => {
          const key = String(entry.runCode || entry.code || "").toUpperCase();
          const item = activeByCode.get(key);
          const code = String(item?.code || "").toUpperCase();
          if (!item || seen.has(code)) return;
          seen.add(code);
          result.push(item);
        });
      });
      return result;
    }

    function getFactoryGroup(id) {
      const group = factoryGroups.find(group => group.id === id);
      if (group) {
        const displayName = legacyBusinessRangeNames[group.name] || businessRangeLabels[group.id] || group.name;
        return { ...group, name: displayName, shortName: legacyBusinessRangeNames[group.shortName] || businessRangeLabels[group.id] || group.shortName || displayName };
      }
      return factoryGroups[0] ? {
        ...factoryGroups[0],
        name: legacyBusinessRangeNames[factoryGroups[0].name] || businessRangeLabels[factoryGroups[0].id] || factoryGroups[0].name,
        shortName: legacyBusinessRangeNames[factoryGroups[0].shortName] || businessRangeLabels[factoryGroups[0].id] || factoryGroups[0].shortName || factoryGroups[0].name
      } : {
        id: "",
        name: "未配置业务范围",
        shortName: "未配置",
        plants: [],
        zfi019nlAreas: [],
        zfi080Areas: [],
        zfi072Plants: [],
        zco019Plants: []
      };
    }

    function getBusinessAreaName(code) {
      return (businessAreaCatalog.find(item => item.code === code) || {}).name || "";
    }

    function getRulePlantList(t, group) {
      const explicitPlants = toArray(t?.plants);
      if (explicitPlants.length) return explicitPlants;
      const fixedPlants = toArray(t?.fixedPlants);
      if (state.config.online && t?.configSource === "api-config") {
        if (fixedPlants.length) return fixedPlants;
        if (t?.code === "ZFI072A" || t?.code === "ZFI072" || t?.code === "ZFI072N") return toArray(group?.zfi072Plants || group?.plants);
        if (t?.code === "ZCO019") return toArray(group?.zco019Plants || group?.plants);
        return toArray(group?.plants);
      }
      return fixedPlants;
    }

    function getDefaultPlantsForTCode(tCode, groupId = state.form.factoryGroup) {
      const t = getTCode(tCode);
      const group = getFactoryGroup(groupId);
      const rulePlants = getRulePlantList(t, group);
      if (rulePlants.length) return unique(rulePlants);
      if (tCode === "ZFI072A") return unique(toArray(group.zfi072Plants).length ? group.zfi072Plants : (state.config.online ? group.plants : zfi072PlantRule));
      if (tCode === "ZFI072" || tCode === "ZFI072N") return unique([...procurementPlants, ...group.zfi072Plants]);
      if (tCode === "ZCO019") return unique(group.zco019Plants);
      return unique(group.plants);
    }

    function getResolvedPlantsForRule(t, groupId = t?.defaultPlantGroup) {
      if (getRuleRangeKind(t) === "dateRange") return [];
      if (getRuleRangeKind(t) === "businessArea") {
        const explicitAreas = toArray(t?.businessAreas);
        return explicitAreas.length ? unique(explicitAreas) : getDefaultBusinessAreasForTCode(t?.code || "", groupId);
      }
      return getDefaultPlantsForTCode(t?.code || "", groupId);
    }

    function getRunPlantsForTCode(tCode, selectedPlants) {
      const t = getTCode(tCode);
      if (state.config.online && t?.configSource === "api-config") {
        const selected = unique(selectedPlants);
        return selected.length ? selected : getDefaultPlantsForTCode(tCode);
      }
      if (tCode === "ZFI072A") return [...zfi072PlantRule];
      return unique(selectedPlants);
    }

    function getDefaultRunRangeForTCode(tCode, groupId = state.form.factoryGroup) {
      const t = getTCode(tCode);
      if (getRuleRangeKind(t) === "dateRange") {
        const range = getLastFullWeekDateRange();
        return [range.period, range.weekEnd];
      }
      if (getRuleRangeKind(t) === "businessArea") {
        const explicitAreas = toArray(t?.businessAreas);
        return explicitAreas.length ? unique(explicitAreas) : getDefaultBusinessAreasForTCode(tCode, groupId);
      }
      return getDefaultPlantsForTCode(tCode, groupId);
    }

    function getRunRangeForTCode(tCode, selectedValues, groupId = state.form.factoryGroup) {
      const t = getTCode(tCode);
      const selected = unique(toArray(selectedValues));
      if (getRuleRangeKind(t) === "dateRange") return getDefaultRunRangeForTCode(tCode, groupId);
      if (getRuleRangeKind(t) === "businessArea") {
        return selected.length ? selected : getDefaultRunRangeForTCode(tCode, groupId);
      }
      return getRunPlantsForTCode(tCode, selected);
    }

    function getBusinessAreasForSelection(tCode, selectedPlants = state.form.plants, groupId = state.form.factoryGroup) {
      const t = getTCode(tCode);
      const group = getFactoryGroup(groupId);
      const plantList = toArray(selectedPlants);
      if (getRuleRangeKind(t) === "businessArea") {
        const explicitAreas = toArray(t?.businessAreas);
        if (explicitAreas.length) return unique(explicitAreas);
        if (tCode === "ZFI019NL") return unique(toArray(group.zfi019nlAreas).length ? group.zfi019nlAreas : plantList.map(code => plantCatalog[code]?.area));
      }
      if (!plantList.length) return [];
      if (tCode === "ZFI019NL") return unique(toArray(group.zfi019nlAreas).length ? group.zfi019nlAreas : plantList.map(code => plantCatalog[code]?.area));
      if (tCode === "ZFI080") return unique(toArray(group.zfi080Areas).length ? group.zfi080Areas : plantList.map(code => plantCatalog[code]?.area));
      if (tCode === "ZCO019" || tCode === "ZFI057") {
        return unique(plantList.map(code => plantCatalog[code]?.area));
      }
      return unique(plantList.map(code => plantCatalog[code]?.area));
    }

    function syncExecutionDefaults(forcePlants = false) {
      const active = dashboardExecutableTCodes();
      if (!active.some(t => t.code === state.form.tCode)) {
        state.form.tCode = active[0]?.code || "";
      }
      const t = getTCode(state.form.tCode);
      if (forcePlants && t?.defaultPlantGroup) {
        state.form.factoryGroup = t.defaultPlantGroup;
      }
      if (!Array.isArray(state.form.plants)) state.form.plants = getDefaultRunRangeForTCode(state.form.tCode);
      if (!state.form.factoryGroup) state.form.factoryGroup = "PINGHU_30";
      if (forcePlants) {
        state.form.plants = getDefaultRunRangeForTCode(state.form.tCode);
      }
      state.form.plants = unique(state.form.plants);
    }

    function renderStep(step, i) {
      const cls = step.status === "done" ? "done" : step.status === "run" ? "run" : step.status === "fail" ? "fail" : "";
      const mark = step.status === "done" ? "?" : step.status === "fail" ? "!" : (i + 1);
      return `<div class="step ${cls}"><span class="step-icon">${mark}</span><span class="step-text">${esc(step.text)}</span><span class="step-time">${esc(step.time)}</span></div>`;
    }

    function renderLog(line) {
      return `<div class="log-line"><span class="log-time">[${esc(line.time)}]</span><span class="${esc(line.type)}">${esc(line.message)}</span></div>`;
    }

    function renderSchedule() {
      const online = state.bridge.online && state.config.online;
      const noticeClass = online ? "ok" : "warn";
      const noticeText = online
        ? `API 已连接，保存定时任务会写入 ${BRIDGE_API}${CONFIG_API_PATHS.root} 并刷新列表。`
        : `本机 API 未启动或配置接口不可用，当前仅显示只读示例/缓存列表；保存不会生效，请启动 API 后再保存。${state.config.error ? "原因：" + state.config.error : ""}`;
      return `
        <section class="panel">
          <div class="panel-header">
            <div><div class="panel-title">${icon("calendar-clock")}定时自动执行配置</div></div>
            <button class="btn primary" data-action="open-schedule-modal">${icon("plus")}新建定时任务</button>
          </div>
          <div class="panel-body">
            <div class="notice ${noticeClass}">${esc(noticeText)}</div>
            <div style="height:14px"></div>
            <div class="table-wrap">
              <table>
                <thead><tr><th>任务 ID</th><th>任务名称</th><th>事务码</th><th>业务范围</th><th>业务范围代码</th><th>工厂</th><th>时间</th><th>频率</th><th>状态</th><th>设置人</th><th>下次执行</th><th>操作</th></tr></thead>
                <tbody>${scheduleTasks.length ? scheduleTasks.map(task => `
                  <tr>
                    <td>${esc(task.id)}</td>
                    <td>${esc(task.name)}</td>
                    <td>${esc(task.tCode)}</td>
                    <td>${esc(getScheduleFactoryGroupName(task))}</td>
                    <td>${renderCodeChips(getScheduleBusinessAreas(task))}</td>
                    <td>${getScheduleScopeChips(task)}</td>
                    <td>${esc(task.time || "-")}</td>
                    <td>${esc(task.frequency || "-")}</td>
                    <td><span class="tag ${getScheduleStatusClass(task.status)}">${esc(task.status || "-")}</span></td>
                    <td><span class="tag info">${esc(getScheduleSetterName(task))}</span></td>
                    <td>${esc(task.next || "-")}</td>
                    <td><span class="inline-actions"><button class="btn small" data-action="open-schedule-modal" data-schedule-id="${esc(task.id)}">${icon("pencil")}编辑</button><button class="btn small red" data-action="delete-schedule" data-schedule-id="${esc(task.id)}">${icon("trash-2")}删除</button></span></td>
                  </tr>`).join("") : `<tr><td colspan="12" class="empty-cell">暂无定时任务配置</td></tr>`}</tbody>
              </table>
            </div>
          </div>
        </section>
      `;
    }

    function renderConfig() {
      if (state.activeConfigTab === "robot") state.activeConfigTab = "plant";
      return `
        <section class="panel">
          <div class="panel-header">
            <div><div class="panel-title">${icon("sliders-horizontal")}基础配置</div></div>
            <div class="top-actions">
              <a class="btn small" href="db-viewer.html" target="_blank" rel="noopener">${icon("database")}数据库查看</a>
              <button class="btn small" data-action="refresh-config">${icon("refresh-cw")}刷新配置</button>
            </div>
          </div>
          <div class="panel-body">
            <div class="tabs">
              ${tabButton("plant", "factory", "工厂配置")}
              ${tabButton("group", "layers", "业务范围配置")}
              ${tabButton("tcode", "terminal", "事务码配置")}
              ${tabButton("rule", "git-branch", "事务码与工厂/业务范围规则")}
            </div>
            ${renderConfigTab()}
          </div>
        </section>
      `;
    }

    function tabButton(tab, iconName, text) {
      return `<button class="tab ${state.activeConfigTab === tab ? "active" : ""}" data-config-tab="${tab}">${icon(iconName)}${text}</button>`;
    }

    function renderConfigStatus() {
      const online = state.bridge.online && state.config.online;
      const title = online ? "API 配置源已连接" : "离线只读模式";
      const sub = online
        ? `基础配置保存到本地 SQLite，经 API 写入数据库。数据来自 ${BRIDGE_API}${CONFIG_API_PATHS.root}，最近刷新 ${state.config.lastLoaded || state.bridge.lastChecked || "-"}`
        : `当前显示页面内置静态默认值，可打开编辑表单查看；保存、删除和新增需要先启动本机 API。${state.config.error ? "原因：" + state.config.error : "请启动本机 Bridge 后刷新。"}`;
      return `
        <div class="config-status">
          <div class="config-status-main">
            <div class="config-status-title">${icon(online ? "database" : "wifi-off")}<span>${esc(title)}</span><span class="tag ${online ? "ok" : "warn"}">${online ? "可保存" : "只读"}</span></div>
            <div class="config-status-sub">${esc(sub)}</div>
          </div>
          <button class="btn small" data-action="refresh-config">${icon("refresh-cw")}刷新</button>
        </div>
      `;
    }

    function renderConfigTab() {
      if (state.activeConfigTab === "plant") {
        return renderPlantConfigTab();
      }
      if (state.activeConfigTab === "group") {
        return renderPlantGroupConfigTab();
      }
      if (state.activeConfigTab === "tcode") {
        return renderTransactionConfigTab();
      }
      if (state.activeConfigTab === "rule") {
        return renderTransactionRuleConfigTab();
      }
      if (state.activeConfigTab === "robot") {
        return renderRobotConfigTab();
      }
      return `<div class="notice">请选择上方配置页签。</div>`;
    }

    function canWriteConfig() {
      return state.bridge.online && state.config.online;
    }

    function configWriteDisabledAttr() {
      return canWriteConfig() ? "" : "disabled";
    }

    function renderPlantConfigTab() {
      const disabled = configWriteDisabledAttr();
      return `
        <div class="config-section">
          <div class="config-section-head">
            <div><div class="config-section-title">${icon("factory")}工厂主数据</div><div class="table-hint">普通维护只看工厂代码、名称/说明、所属业务范围、启用和排序；页面不保存 SAP 账号或密码。</div></div>
            <button class="btn primary small" data-action="open-config-modal" data-kind="plant" data-mode="add" ${disabled}>${icon("plus")}新增工厂</button>
          </div>
          <div class="table-wrap">
            <table>
              <thead><tr><th>工厂代码</th><th>名称/说明</th><th>所属业务范围</th><th>排序</th><th>状态</th><th>操作</th></tr></thead>
              <tbody>${plants.length ? plants.map(p => `
                <tr>
                  <td><strong>${esc(p.code)}</strong></td>
                  <td>${esc(p.name)}</td>
                  <td>${renderGroupNames(p.groups)}</td>
                  <td>${esc(p.sortOrder ?? 0)}</td>
                  <td><span class="tag ${p.enabled ? "ok" : "warn"}">${p.enabled ? "启用" : "停用"}</span></td>
                  <td><span class="inline-actions"><button class="btn small" data-action="open-config-modal" data-kind="plant" data-mode="edit" data-id="${esc(p.code)}">${icon("pencil")}编辑</button><button class="btn small red" data-action="delete-config" data-kind="plant" data-id="${esc(p.code)}">${icon("trash-2")}删除</button></span></td>
                </tr>`).join("") : `<tr><td colspan="6" class="empty-cell">暂无工厂配置</td></tr>`}</tbody>
            </table>
          </div>
        </div>
      `;
    }

    function renderPlantGroupConfigTab() {
      const disabled = configWriteDisabledAttr();
      return `
        <div class="config-section">
          <div class="config-section-head">
            <div><div class="config-section-title">${icon("layers")}业务范围</div><div class="table-hint">执行页对应工厂和业务范围优先使用这里的 API 配置。</div></div>
            <button class="btn primary small" data-action="open-config-modal" data-kind="group" data-mode="add" ${disabled}>${icon("plus")}新增业务范围</button>
          </div>
          <div class="table-wrap">
            <table>
              <thead><tr><th>业务范围</th><th>包含工厂</th><th>ZFI019NL 业务范围</th><th>ZFI080 业务范围</th><th>ZFI072 工厂</th><th>ZCO019 工厂</th><th>状态</th><th>操作</th></tr></thead>
              <tbody>${factoryGroups.length ? factoryGroups.map(group => `
                <tr>
                  <td><strong>${esc(getFactoryGroup(group.id).name)}</strong><div class="table-hint">${esc(group.id)}</div></td>
                  <td>${renderCodeChips(group.plants)}</td>
                  <td>${renderCodeChips(group.zfi019nlAreas)}</td>
                  <td>${renderCodeChips(group.zfi080Areas)}</td>
                  <td>${renderCodeChips(group.zfi072Plants)}</td>
                  <td>${renderCodeChips(group.zco019Plants)}</td>
                  <td><span class="tag ${group.enabled === false ? "warn" : "ok"}">${group.enabled === false ? "停用" : "启用"}</span></td>
                  <td><span class="inline-actions"><button class="btn small" data-action="open-config-modal" data-kind="group" data-mode="edit" data-id="${esc(group.id)}">${icon("pencil")}编辑</button><button class="btn small red" data-action="delete-config" data-kind="group" data-id="${esc(group.id)}">${icon("trash-2")}删除</button></span></td>
                </tr>`).join("") : `<tr><td colspan="8" class="empty-cell">暂无业务范围配置</td></tr>`}</tbody>
            </table>
          </div>
        </div>
      `;
    }

    function renderTransactionConfigTab() {
      const disabled = configWriteDisabledAttr();
      return `
        <div class="config-section">
          <div class="config-section-head">
            <div><div class="config-section-title">${icon("terminal")}事务码配置</div><div class="table-hint">维护事务码名称、模块、阶段、参数、超时、重试和启停。</div></div>
            <button class="btn primary small" data-action="open-config-modal" data-kind="tcode" data-mode="add" ${disabled}>${icon("plus")}新增事务码</button>
          </div>
          <div class="table-wrap">
            <table>
              <thead><tr><th>阶段</th><th>事务码</th><th>名称</th><th>模块</th><th>参数</th><th>超时（秒）</th><th>重试次数</th><th>状态</th><th>操作</th></tr></thead>
              <tbody>${tCodes.length ? tCodes.map(t => `
                <tr>
                  <td>${esc(t.stage)}</td>
                  <td><strong>${esc(t.code)}</strong></td>
                  <td>${esc(t.name)}</td>
                  <td>${esc(t.module || "-")}</td>
                  <td>${renderCodeChips(getDisplayParamsForTransaction(t))}</td>
                  <td>${esc(String(t.timeoutSeconds ?? t.timeout ?? "-"))}</td>
                  <td>${esc(String(t.retryCount ?? t.retry ?? "-"))}</td>
                  <td><span class="tag ${t.enabled === false ? "warn" : t.automation === "script" ? "ok" : "info"}">${t.enabled === false ? "停用" : t.automation === "script" ? "已接脚本" : "打开事务码"}</span></td>
                  <td><span class="inline-actions"><button class="btn small" data-action="open-config-modal" data-kind="tcode" data-mode="edit" data-id="${esc(t.code)}">${icon("pencil")}编辑</button><button class="btn small red" data-action="delete-config" data-kind="tcode" data-id="${esc(t.code)}">${icon("trash-2")}停用</button></span></td>
                </tr>`).join("") : `<tr><td colspan="9" class="empty-cell">暂无事务码配置</td></tr>`}</tbody>
            </table>
          </div>
        </div>
      `;
    }

    function renderTransactionRuleConfigTab() {
      const disabled = configWriteDisabledAttr();
      return `
        <div class="config-section">
          <div class="config-section-head">
            <div><div class="config-section-title">${icon("git-branch")}事务码与工厂/业务范围规则</div><div class="table-hint">维护事务码默认业务范围、对应工厂、规则说明和启停。</div></div>
            <button class="btn primary small" data-action="open-config-modal" data-kind="rule" data-mode="add" ${disabled}>${icon("plus")}新增规则</button>
          </div>
          <div class="table-wrap">
            <table>
              <thead><tr><th>事务码</th><th>默认业务范围</th><th>对应工厂</th><th>规则说明</th><th>状态</th><th>操作</th></tr></thead>
              <tbody>${tCodes.length ? tCodes.map(t => `
                <tr>
                  <td><strong>${esc(t.code)}</strong><div class="table-hint">${esc(t.name)}</div></td>
                  <td>${esc(getFactoryGroup(t.defaultPlantGroup)?.name || t.defaultPlantGroup || "-")}</td>
                  <td>${renderCodeChips(getResolvedPlantsForRule(t))}</td>
                  <td>${esc(t.factoryRule || "-")}</td>
                  <td><span class="tag ${t.enabled === false ? "warn" : "ok"}">${t.enabled === false ? "停用" : "启用"}</span></td>
                  <td><span class="inline-actions"><button class="btn small" data-action="open-config-modal" data-kind="rule" data-mode="edit" data-id="${esc(t.code)}">${icon("pencil")}编辑</button><button class="btn small red" data-action="delete-config" data-kind="rule" data-id="${esc(t.code)}">${icon("trash-2")}停用</button></span></td>
                </tr>`).join("") : `<tr><td colspan="6" class="empty-cell">暂无事务码与工厂/业务范围规则</td></tr>`}</tbody>
            </table>
          </div>
        </div>
      `;
    }

    function renderRobotConfigTab() {
      const disabled = configWriteDisabledAttr();
      return `
        <div class="config-section">
          <div class="config-section-head">
            <div><div class="config-section-title">${icon("message-circle")}通知机器人</div><div class="table-hint">前端只显示是否已配置密钥。Webhook URL、secret、token 不展示、不缓存；保存时可输入替换值。</div></div>
            <button class="btn primary small" data-action="open-config-modal" data-kind="robot" data-mode="add" ${disabled}>${icon("plus")}新增机器人</button>
          </div>
          <div class="table-wrap">
            <table>
              <thead><tr><th>机器人</th><th>通知群/别名</th><th>事件</th><th>Webhook 状态</th><th>Secret 状态</th><th>状态</th><th>最近推送</th><th>操作</th></tr></thead>
              <tbody>${robots.length ? robots.map(r => `
                <tr>
                  <td><strong>${esc(r.name)}</strong><div class="table-hint">${esc(r.id)}</div></td>
                  <td>${esc(r.group || "-")}</td>
                  <td>${renderCodeChips(r.events)}</td>
                  <td>${renderSecretState(r.hasWebhook)}</td>
                  <td>${renderSecretState(r.hasSecret)}</td>
                  <td><span class="tag ${r.enabled ? "ok" : "warn"}">${r.enabled ? "启用" : "停用"}</span></td>
                  <td>${esc(r.lastPush || "-")}</td>
                  <td><span class="inline-actions"><button class="btn small" data-action="open-config-modal" data-kind="robot" data-mode="edit" data-id="${esc(r.id)}">${icon("pencil")}编辑</button><button class="btn small red" data-action="delete-config" data-kind="robot" data-id="${esc(r.id)}">${icon("trash-2")}删除</button><button class="btn small" data-action="test-robot" data-id="${esc(r.id)}" ${disabled}>${icon("send")}测试</button></span></td>
                </tr>`).join("") : `<tr><td colspan="8" class="empty-cell">暂无通知机器人</td></tr>`}</tbody>
            </table>
          </div>
        </div>
      `;
    }

    function getDisplayParamsForTransaction(transaction) {
      const params = toArray(transaction?.params);
      if (transaction?.code === "ZFI019NL") {
        return unique(params.map(param => param === "plants" ? "businessAreas" : param));
      }
      return params;
    }

    function renderCodeChips(values) {
      const items = toArray(values);
      if (!items.length) return `<span class="table-hint">-</span>`;
      return `<div class="area-chips">${items.map(item => `<span class="area-chip">${esc(item)}</span>`).join("")}</div>`;
    }

    function normalizeRulePlantCode(value) {
      return String(value || "").trim().toUpperCase();
    }

    function normalizeRulePlantList(value) {
      return unique(toArray(value).map(normalizeRulePlantCode).filter(Boolean));
    }

    function getRuleRangeKind(transaction) {
      const code = String(transaction?.code || "").toUpperCase();
      const params = getDisplayParamsForTransaction(transaction);
      if (code === "ZFIR034" || (params.includes("period") && params.includes("weekEnd") && !params.includes("plants") && !params.includes("businessAreas"))) return "dateRange";
      if (code === "ZFI019NL" || code === "ZFI057" || code === "ZCO020") return "businessArea";
      return params.includes("businessAreas") && !params.includes("plants") ? "businessArea" : "plant";
    }

    function getRuleRangeMeta(transaction) {
      const rangeKind = getRuleRangeKind(transaction);
      const isBusinessArea = rangeKind === "businessArea";
      const isDateRange = rangeKind === "dateRange";
      return {
        isBusinessArea,
        isDateRange,
        title: isDateRange ? "事务码执行日期范围" : (isBusinessArea ? "事务码执行业务范围" : "事务码执行工厂"),
        addPlaceholder: isDateRange ? "日期范围由系统日期自动计算" : (isBusinessArea ? "新增业务范围代码" : "新增工厂代码"),
        emptyText: isDateRange ? "按系统日期上一完整周执行" : (isBusinessArea ? "未配置业务范围" : "未配置工厂")
      };
    }

    function getDefaultBusinessAreasForTCode(tCode, groupId = state.form.factoryGroup) {
      const t = getTCode(tCode);
      const explicitAreas = toArray(t?.businessAreas);
      if (getRuleRangeKind(t) === "businessArea" && explicitAreas.length) return unique(explicitAreas);
      return getBusinessAreasForSelection(tCode, getDefaultPlantsForTCode(tCode, groupId), groupId);
    }

    function renderRulePlantChips(plantsValue, transaction) {
      const plantsList = normalizeRulePlantList(plantsValue);
      const meta = getRuleRangeMeta(transaction || { code: readInputValue("cfgRuleCode") });
      return plantsList.length
        ? plantsList.map(code => `<span class="area-chip editable">${esc(code)}<button type="button" class="chip-x" data-action="remove-rule-plant" data-plant="${esc(code)}" aria-label="删除 ${esc(code)}" title="删除 ${esc(code)}">X</button></span>`).join("")
        : `<span class="table-hint">${meta.emptyText}</span>`;
    }

    function renderGroupNames(groupIds) {
      const names = toArray(groupIds).map(id => getFactoryGroup(id)?.shortName || getFactoryGroup(id)?.name || id);
      return names.length ? esc(names.join(",")) : `<span class="table-hint">-</span>`;
    }

    function getScheduleBusinessAreas(task) {
      if (getRuleRangeKind(getTCode(task?.tCode || "")) === "dateRange") return [];
      if (getRuleRangeKind(getTCode(task?.tCode || "")) === "businessArea") {
        const explicitAreas = toArray(task?.businessAreas);
        if (explicitAreas.length) return explicitAreas;
        return toArray(task?.plants);
      }
      const explicitAreas = toArray(task?.businessAreas);
      if (explicitAreas.length) return explicitAreas;
      return getBusinessAreasForSelection(task?.tCode || "", toArray(task?.plants), task?.factoryGroup || state.scheduleForm.factoryGroup);
    }

    function getScheduleScopeChips(task) {
      const rangeKind = getRuleRangeKind(getTCode(task?.tCode || ""));
      if (rangeKind === "dateRange") {
        const params = task?.params || {};
        const range = params.period && params.weekEnd
          ? { period: params.period, weekEnd: params.weekEnd }
          : getLastFullWeekDateRange();
        return renderCodeChips([`开始 ${range.period}`, `截止 ${range.weekEnd}`]);
      }
      if (rangeKind === "businessArea") return renderCodeChips(getScheduleBusinessAreas(task));
      return renderCodeChips(task?.plants);
    }

    function getScheduleFactoryGroupName(task) {
      const id = task?.factoryGroup || "";
      const group = factoryGroups.find(item => item.id === id);
      if (!group) return id || "-";
      return legacyBusinessRangeNames[group.name] || businessRangeLabels[group.id] || group.name || id;
    }

    function getScheduleStatusClass(status) {
      if (status === "启用") return "ok";
      if (status === "停用" || status === "未落库" || status === "异常") return "warn";
      return "info";
    }

    function getScheduleSetterName(task) {
      return task?.updatedBy || task?.createdBy || "-";
    }

    function buildScheduleNotifyTarget() {
      const dingTalkId = getResolvedNotifyUserId();
      return dingTalkId ? `dingtalk:${dingTalkId}` : "dingtalk";
    }

    function buildScheduleConfigPayload() {
      const tCode = readInputValue("scheduleTCode") || state.scheduleForm.tCode || tCodes[0]?.code || "";
      const factoryGroup = readInputValue("scheduleFactoryGroup") || state.scheduleForm.factoryGroup || getTCode(tCode)?.defaultPlantGroup || factoryGroups[0]?.id || "";
      const schedulePlantsInput = document.getElementById("schedulePlants");
      const rangeKind = getRuleRangeKind(getTCode(tCode));
      const isDateRange = rangeKind === "dateRange";
      const isBusinessAreaRange = rangeKind === "businessArea";
      const rangeValue = isDateRange ? [] : normalizeRulePlantList(schedulePlantsInput ? schedulePlantsInput.value : state.scheduleForm.plants);
      const plantsValue = isBusinessAreaRange || isDateRange ? [] : rangeValue;
      const businessAreas = isDateRange ? [] : (isBusinessAreaRange ? rangeValue : getBusinessAreasForSelection(tCode, plantsValue, factoryGroup));
      const frequency = readInputValue("scheduleFrequency") || state.scheduleForm.frequency || "weekly";
      const enabled = readInputChecked("scheduleEnabled");
      const notifyEnabled = readInputChecked("scheduleNotifyStart") || readInputChecked("scheduleNotifySuccess") || readInputChecked("scheduleNotifyFail");
      if (notifyEnabled && !getResolvedNotifyUserId()) throw new Error(notifyUserBlockingText() || "缺少定时任务通知人钉钉 ID");
      const currentUserName = state.user.name || state.externalAuth.claimedUserName || "portal";
      const task = {
        id: state.scheduleForm.id || nextScheduleTaskId(),
        name: readInputValue("scheduleName") || defaultScheduleName(tCode),
        tCode,
        transactionCode: tCode,
        factoryGroup,
        plantGroupId: factoryGroup,
        factoryGroupName: getFactoryGroup(factoryGroup).name,
        plants: plantsValue,
        plantCodes: plantsValue,
        factoryCodes: plantsValue,
        plantsCsv: plantsValue.join(","),
        plantCodesCsv: plantsValue.join(","),
        factoryCodesCsv: plantsValue.join(","),
        businessAreas,
        businessAreasCsv: businessAreas.join(","),
        rangeKind: isDateRange ? "dateRange" : (isBusinessAreaRange ? "businessArea" : "plant"),
        dateRule: isDateRange ? "LAST_FULL_WEEK_BY_SYSTEM_DATE" : "",
        time: readInputValue("scheduleTime") || "08:00",
        execTime: readInputValue("scheduleTime") || "08:00",
        frequency,
        frequencyText: formatScheduleFrequency(frequency),
        defaultBusinessScope: factoryGroup,
        params: {
          plants: plantsValue.join(","),
          plant: plantsValue[0] || "",
          plantCodes: plantsValue.join(","),
          factoryCodes: plantsValue.join(","),
          businessAreas: businessAreas.join(","),
          runStrategy: tCode === "ZFI057" ? "auto3step" : "",
          rangeKind: isDateRange ? "dateRange" : (isBusinessAreaRange ? "businessArea" : ""),
          dateRule: isDateRange ? "LAST_FULL_WEEK_BY_SYSTEM_DATE" : "",
          factoryGroup
        },
        enabled,
        status: enabled ? "启用" : "停用",
        notifyEnabled,
        notifyStart: readInputChecked("scheduleNotifyStart"),
        notifySuccess: readInputChecked("scheduleNotifySuccess"),
        notifyFail: readInputChecked("scheduleNotifyFail"),
        notifyOnSuccess: readInputChecked("scheduleNotifySuccess"),
        notifyOnFailure: readInputChecked("scheduleNotifyFail"),
        notifyTarget: notifyEnabled ? buildScheduleNotifyTarget() : "",
        createdBy: state.scheduleForm.createdBy || currentUserName,
        updatedBy: currentUserName
      };
      return task;
    }

    function nextScheduleTaskId() {
      const next = scheduleTasks.length + 1;
      return "SCH-" + String(next).padStart(3, "0");
    }

    function renderSecretState(value) {
      return `<span class="secret-state">${value ? "已配置（已隐藏）" : "未配置"}</span>`;
    }

    function renderBusinessAreaModeText(value) {
      const map = {
        byPlant: "按工厂业务范围",
        fixed: "固定业务范围",
        byGroup: "按业务范围配置",
        none: "不传业务范围"
      };
      return map[value] || value || "-";
    }

    function getConfiguredPlantsForRule(rule) {
      if (toArray(rule.plants).length) return toArray(rule.plants);
      if (toArray(rule.fixedPlants).length) return toArray(rule.fixedPlants);
      const group = getFactoryGroup(rule.defaultPlantGroup);
      return group ? group.plants : [];
    }

    function renderModal() {
      if (state.modal === "register") {
        return `
          <div class="modal-backdrop">
            <div class="modal">
              <div class="modal-header"><strong>本机协议注册</strong><button class="btn small" data-action="close-modal">${icon("x")}关闭</button></div>
              <div class="modal-body">
                <div class="notice">当前服务端上线以手工安装清单为准。旧的临时测试协议不再安装；如电脑上曾导入过旧协议，按安装文档手工核对并清理。</div>
                <div style="height:12px"></div>
                <div class="launch-link">D:\\RPA\\RpaProject\\上线安装包\\上线安装文档清单.md<br>D:\\RPA\\RpaProject\\上线安装包\\04_配置SAP登录信息.bat</div>
              </div>
              <div class="modal-footer"><button class="btn primary" data-action="close-modal">关闭</button></div>
            </div>
          </div>
        `;
      }
      if (state.modal === "schedule") {
        const executableTCodes = dashboardExecutableTCodes();
        if (!executableTCodes.some(t => t.code === state.scheduleForm.tCode)) {
          state.scheduleForm.tCode = executableTCodes[0]?.code || "";
        }
        const schedulePlants = normalizeRulePlantList(state.scheduleForm.plants);
        const scheduleRangeKind = getRuleRangeKind(getTCode(state.scheduleForm.tCode));
        const isDateRange = scheduleRangeKind === "dateRange";
        const isBusinessAreaRange = scheduleRangeKind === "businessArea";
        const scheduleAreas = isDateRange ? [] : (isBusinessAreaRange ? schedulePlants : getBusinessAreasForSelection(state.scheduleForm.tCode, schedulePlants, state.scheduleForm.factoryGroup));
        const scheduleRange = getLastFullWeekDateRange();
        const scheduleScopeEditor = isDateRange ? `
                  <div class="schedule-plant-preview">
                    <div class="schedule-plant-title">日期范围</div>
                    <input type="hidden" id="schedulePlants" value="">
                    <div id="schedulePlantChips" class="area-chips">${renderCodeChips([`开始 ${scheduleRange.period}`, `截止 ${scheduleRange.weekEnd}`])}</div>
                    <div class="table-hint">实际定时触发时按服务器系统日期重新计算上一完整周。</div>
                  </div>` : `
                  <div class="schedule-plant-preview">
                    <div class="rule-plant-head">
                      <div class="schedule-plant-title">${isBusinessAreaRange ? "业务范围代码" : "对应工厂"}</div>
                      <button type="button" class="btn small" data-action="reset-schedule-plants">${icon("rotate-ccw")}恢复默认</button>
                    </div>
                    <input type="hidden" id="schedulePlants" value="${esc(schedulePlants.join(","))}">
                    <div id="schedulePlantChips" class="area-chips">${renderSchedulePlantChips(schedulePlants, isBusinessAreaRange ? "businessArea" : "plant")}</div>
                    <div class="rule-plant-add">
                      <input id="schedulePlantAdd" placeholder="${isBusinessAreaRange ? "新增业务范围代码" : "新增工厂代码"}">
                      <button type="button" class="btn small" data-action="add-schedule-plant">${icon("plus")}新增</button>
                    </div>
                  </div>
                  <div class="schedule-plant-preview" ${isBusinessAreaRange ? "hidden" : ""}>
                    <div class="schedule-plant-title">业务范围代码</div>
                    <div id="scheduleBusinessAreaChips">${renderCodeChips(scheduleAreas)}</div>
                  </div>`;
        return `
          <div class="modal-backdrop">
            <div class="modal">
              <div class="modal-header"><strong>${state.scheduleForm.id ? "编辑定时任务" : "新建定时任务"}</strong><button class="btn small" data-action="close-modal">${icon("x")}关闭</button></div>
              <div class="modal-body">
                <div class="form-grid">
                  <div class="field"><label>事务码</label><select id="scheduleTCode">${executableTCodes.map(t => `<option value="${t.code}" ${state.scheduleForm.tCode === t.code ? "selected" : ""}>${t.code} - ${t.name}</option>`).join("")}</select></div>
                  <div class="field span-2"><label>任务名称</label><input id="scheduleName" value="${esc(state.scheduleForm.name)}" placeholder="选择事务码后自动带出，可继续补充"></div>
                  <div class="field"><label>业务范围</label><select id="scheduleFactoryGroup">${factoryGroups.map(group => `<option value="${group.id}" ${state.scheduleForm.factoryGroup === group.id ? "selected" : ""}>${getFactoryGroup(group.id).name}</option>`).join("")}</select></div>
                  <div class="field"><label>执行时间</label><input id="scheduleTime" type="time" value="${esc(state.scheduleForm.execTime)}"></div>
                  <div class="field"><label>执行频率</label><select id="scheduleFrequency"><option value="daily" ${state.scheduleForm.frequency === "daily" ? "selected" : ""}>每天</option><option value="weekly" ${state.scheduleForm.frequency === "weekly" ? "selected" : ""}>每周</option><option value="monthly" ${state.scheduleForm.frequency === "monthly" ? "selected" : ""}>每月</option></select></div>
                  <div class="field"><label>状态</label><label class="radio-chip"><input id="scheduleEnabled" type="checkbox" ${state.scheduleForm.enabled === false ? "" : "checked"}>启用</label></div>
${scheduleScopeEditor}
                  <div class="field span-2"><label>通知规则</label><div class="check-row"><label class="radio-chip"><input id="scheduleNotifyStart" type="checkbox" ${state.scheduleForm.notifyStart === false ? "" : "checked"}>执行前提醒</label><label class="radio-chip"><input id="scheduleNotifySuccess" type="checkbox" ${state.scheduleForm.notifySuccess === false ? "" : "checked"}>成功通知</label><label class="radio-chip"><input id="scheduleNotifyFail" type="checkbox" ${state.scheduleForm.notifyFail === false ? "" : "checked"}>失败告警</label></div></div>
                </div>
              </div>
              <div class="modal-footer"><button class="btn" data-action="close-modal">取消</button><button class="btn primary" data-action="save-schedule">${icon("save")}保存任务</button></div>
            </div>
          </div>
        `;
      }
      if (state.modal && state.modal.startsWith("config:")) {
        return renderConfigModal();
      }
      if (state.modal && state.modal.startsWith("log:")) {
        const id = state.modal.slice(4);
        const row = history.find(x => x.id === id) || history[0];
        if (!row) {
          return `
            <div class="modal-backdrop">
              <div class="modal">
                <div class="modal-header"><strong>日志详情</strong><button class="btn small" data-action="close-modal">${icon("x")}关闭</button></div>
                <div class="modal-body"><div class="notice">暂无执行历史。</div></div>
                <div class="modal-footer"><button class="btn primary" data-action="close-modal">关闭</button></div>
              </div>
            </div>
          `;
        }
        const logLines = row.logs?.length ? row.logs.map(line => `
          <div class="log-line"><span class="log-time">[${esc((line.createdAt || "").slice(11, 19) || "--:--:--")}]</span><span class="${esc((line.level || "INFO").toUpperCase() === "ERROR" ? "ERR" : (line.level || "INFO").toUpperCase())}">${esc(line.message)}</span></div>
        `).join("") : `
          <div class="log-line"><span class="log-time">[08:00:00]</span><span class="INFO">任务触发：${esc(row.task)}</span></div>
          <div class="log-line"><span class="log-time">[08:00:03]</span><span class="INFO">提交本机 SapWebLauncher</span></div>
          <div class="log-line"><span class="log-time">[08:00:45]</span><span class="${row.status === "成功" ? "OK" : "ERR"}">${esc(row.result)}</span></div>
        `;
        const fileLines = row.files?.length ? `<div style="height:14px"></div><div class="table-wrap"><table><thead><tr><th>文件</th><th>路径</th><th>大小</th></tr></thead><tbody>${row.files.map(file => `<tr><td>${esc(file.name || "-")}</td><td>${esc(file.path || "-")}</td><td>${esc(file.size || 0)}</td></tr>`).join("")}</tbody></table></div>` : "";
        return `
          <div class="modal-backdrop">
            <div class="modal">
              <div class="modal-header"><strong>${row.id} 日志详情</strong><button class="btn small" data-action="close-modal">${icon("x")}关闭</button></div>
              <div class="modal-body">
                <div class="grid cols-2">
                  ${metric("事务码", row.tCode, row.task)}
                  ${metric("执行结果", row.status, row.result)}
                </div>
                <div style="height:14px"></div>
                <div class="log">${logLines}</div>
                ${fileLines}
              </div>
              <div class="modal-footer"><button class="btn primary" data-action="close-modal">关闭</button></div>
            </div>
          </div>
        `;
      }
      return "";
    }

    function renderConfigModal() {
      const [, kind, mode, rawId = ""] = state.modal.split(":");
      const id = decodeURIComponent(rawId);
      const isEdit = mode === "edit";
      const titleMap = {
        plant: isEdit ? "编辑工厂" : "新增工厂",
        group: isEdit ? "编辑业务范围" : "新增业务范围",
        tcode: isEdit ? "编辑事务码" : "新增事务码",
        rule: isEdit ? "编辑事务码规则" : "新增事务码规则",
        robot: isEdit ? "编辑通知机器人" : "新增通知机器人"
      };
      return `
        <div class="modal-backdrop">
          <div class="modal">
            <div class="modal-header"><strong>${esc(titleMap[kind] || "配置")}</strong><button class="btn small" data-action="close-modal">${icon("x")}关闭</button></div>
            <div class="modal-body">${renderConfigModalBody(kind, mode, id)}</div>
            <div class="modal-footer">
              <button class="btn" data-action="close-modal">取消</button>
              <button class="btn primary" data-action="save-config" data-kind="${esc(kind)}" data-mode="${esc(mode)}" data-id="${esc(id)}">${icon("save")}保存</button>
            </div>
          </div>
        </div>
      `;
    }

    function renderConfigModalBody(kind, mode, id) {
      if (kind === "plant") return renderPlantModalBody(mode, id);
      if (kind === "group") return renderGroupModalBody(mode, id);
      if (kind === "tcode") return renderTCodeModalBody(mode, id);
      if (kind === "rule") return renderRuleModalBody(mode, id);
      if (kind === "robot") return renderRobotModalBody(mode, id);
      return `<div class="notice warn">未知配置类型。</div>`;
    }

    function renderPlantModalBody(mode, id) {
      const item = mode === "edit" ? plants.find(p => p.code === id) : null;
      const data = item || { code: "", name: "", area: "", groups: [], sortOrder: 0, enabled: true };
      return `
        <div class="form-grid">
          <div class="field"><label>工厂代码</label><input id="cfgPlantCode" value="${esc(data.code)}" ${mode === "edit" ? "disabled" : ""} placeholder="例如 1022"></div>
          <div class="field"><label>名称/说明</label><input id="cfgPlantName" value="${esc(data.name)}" placeholder="例如 平湖九厂"></div>
          <div class="field"><label>所属业务范围</label><input id="cfgPlantGroups" value="${esc(toArray(data.groups).join(","))}" placeholder="例如 PINGHU_30"></div>
          <div class="field"><label>业务范围</label><input id="cfgPlantArea" value="${esc(data.area)}" placeholder="例如 2800"></div>
          <div class="field"><label>排序</label><input id="cfgPlantSortOrder" type="number" min="0" value="${esc(data.sortOrder ?? 0)}"></div>
          <div class="field"><label>状态</label><label class="radio-chip"><input id="cfgPlantEnabled" type="checkbox" ${data.enabled !== false ? "checked" : ""}>启用</label></div>
        </div>
      `;
    }

    function renderGroupModalBody(mode, id) {
      const item = mode === "edit" ? factoryGroups.find(group => group.id === id) : null;
      const data = item || { id: "", name: "", shortName: "", plants: [], zfi019nlAreas: [], zfi080Areas: [], zfi072Plants: [], zco019Plants: [], enabled: true };
      return `
        <div class="form-grid">
          <div class="field"><label>业务范围 ID</label><input id="cfgGroupId" value="${esc(data.id)}" ${mode === "edit" ? "disabled" : ""} placeholder="例如 PINGHU_30"></div>
          <div class="field"><label>名称</label><input id="cfgGroupName" value="${esc(data.name)}" placeholder="例如 平湖二厂 / 平湖五厂"></div>
          <div class="field"><label>短名称</label><input id="cfgGroupShortName" value="${esc(data.shortName)}" placeholder="例如 二厂/五厂"></div>
          <div class="field span-3"><label>包含工厂</label><input id="cfgGroupPlants" value="${esc(toArray(data.plants).join(","))}" placeholder="多个用逗号分隔"></div>
          <div class="field span-3"><label>ZFI019NL 业务范围</label><input id="cfgGroupZfi019nlAreas" value="${esc(toArray(data.zfi019nlAreas).join(","))}" placeholder="多个用逗号分隔"></div>
          <div class="field span-3"><label>ZFI080 业务范围</label><input id="cfgGroupZfi080Areas" value="${esc(toArray(data.zfi080Areas).join(","))}" placeholder="多个用逗号分隔"></div>
          <div class="field span-3"><label>ZFI072 工厂</label><input id="cfgGroupZfi072Plants" value="${esc(toArray(data.zfi072Plants).join(","))}" placeholder="留空则按包含工厂"></div>
          <div class="field span-3"><label>ZCO019 工厂</label><input id="cfgGroupZco019Plants" value="${esc(toArray(data.zco019Plants).join(","))}" placeholder="留空则按包含工厂"></div>
          <div class="field"><label>状态</label><label class="radio-chip"><input id="cfgGroupEnabled" type="checkbox" ${data.enabled !== false ? "checked" : ""}>启用</label></div>
        </div>
      `;
    }

    function renderTCodeModalBody(mode, id) {
      const item = mode === "edit" ? getTCode(id) : null;
      const data = item || { code: "", name: "", module: "", stage: "并行启动", script: "", params: ["year", "week", "plants"], icon: "terminal", automation: "openOnly", timeout: 180, retry: 2, enabled: true };
      return `
        <div class="form-grid">
          <div class="field"><label>事务码</label><input id="cfgTCodeCode" value="${esc(data.code)}" ${mode === "edit" ? "disabled" : ""} placeholder="例如 ZFI072A"></div>
          <div class="field"><label>名称</label><input id="cfgTCodeName" value="${esc(data.name)}"></div>
          <div class="field"><label>模块</label><input id="cfgTCodeModule" value="${esc(data.module || "")}" placeholder="FI/CO/PP"></div>
          <div class="field"><label>阶段</label><input id="cfgTCodeStage" value="${esc(data.stage || "")}" placeholder="例如 并行启动"></div>
          <div class="field span-3"><label>参数</label><input id="cfgTCodeParams" value="${esc(toArray(data.params).join(","))}" placeholder="例如 year,week,plants"></div>
          <div class="field"><label>超时秒数</label><input id="cfgTCodeTimeout" type="number" min="1" value="${esc(data.timeoutSeconds ?? data.timeout ?? 180)}"></div>
          <div class="field"><label>重试次数</label><input id="cfgTCodeRetry" type="number" min="0" value="${esc(data.retryCount ?? data.retry ?? 0)}"></div>
          <div class="field"><label>状态</label><label class="radio-chip"><input id="cfgTCodeEnabled" type="checkbox" ${data.enabled !== false ? "checked" : ""}>启用</label></div>
        </div>
      `;
    }

    function renderRuleModalBody(mode, id) {
      const item = mode === "edit" ? getTCode(id) : null;
      const data = item || { code: "", name: "", module: "", stage: "并行启动", script: "", params: ["year", "week", "plants"], factoryRule: "", defaultPlantGroup: factoryGroups[0]?.id || "", plants: [], fixedPlants: [], selectableGroupIds: [], businessAreaMode: "byPlant", businessAreas: [], automation: "openOnly", timeout: 180, retry: 2, enabled: true };
      const selectedGroupId = data.defaultPlantGroup || factoryGroups[0]?.id || "";
      const defaultPlants = getDefaultPlantsForTCode(data.code, selectedGroupId);
      const defaultBusinessAreas = getDefaultBusinessAreasForTCode(data.code, selectedGroupId);
      const rangeMeta = getRuleRangeMeta(data);
      const customPlants = unique(toArray(data.plants).length ? data.plants : data.fixedPlants);
      const customBusinessAreas = unique(toArray(data.businessAreas));
      const resolvedPlants = rangeMeta.isDateRange
        ? []
        : normalizeRulePlantList(rangeMeta.isBusinessArea
          ? (customBusinessAreas.length ? customBusinessAreas : defaultBusinessAreas)
          : (customPlants.length ? customPlants : defaultPlants));
      const rangeEditorAdd = rangeMeta.isDateRange ? "" : `
            <div class="rule-plant-add">
              <input id="cfgRulePlantAdd" placeholder="${rangeMeta.addPlaceholder}">
              <button type="button" class="btn small" data-action="add-rule-plant">${icon("plus")}新增</button>
            </div>`;
      return `
        <div class="form-grid">
          <div class="field"><label>事务码</label><input id="cfgRuleCode" value="${esc(data.code)}" ${mode === "edit" ? "disabled" : ""} placeholder="例如 ZFI072A"></div>
          <div class="field"><label>名称</label><input id="cfgRuleName" value="${esc(data.name)}"></div>
          <div class="field"><label>默认业务范围</label><select id="cfgRuleDefaultGroup">${factoryGroups.map(group => `<option value="${esc(group.id)}" ${data.defaultPlantGroup === group.id ? "selected" : ""}>${esc(getFactoryGroup(group.id).name)}</option>`).join("")}</select></div>
          <div class="field span-3"><label>规则说明</label><input id="cfgRuleFactoryRule" value="${esc(data.factoryRule || "")}" placeholder="例如 按配置工厂执行"></div>
          <div class="rule-plant-editor">
            <div class="rule-plant-head">
              <div class="rule-plant-title">${rangeMeta.title}</div>
              <button type="button" class="btn small" data-action="reset-rule-plants">${icon("rotate-ccw")}恢复默认</button>
            </div>
            <input type="hidden" id="cfgRulePlants" value="${esc(resolvedPlants.join(","))}">
            <div id="cfgRulePlantChips" class="area-chips">${renderRulePlantChips(resolvedPlants, data)}</div>
${rangeEditorAdd}
          </div>
          <div class="field"><label>状态</label><label class="radio-chip"><input id="cfgRuleEnabled" type="checkbox" ${data.enabled !== false ? "checked" : ""}>启用</label></div>
        </div>
      `;
    }

    function renderRobotModalBody(mode, id) {
      const item = mode === "edit" ? robots.find(robot => robot.id === id) : null;
      const data = item || { id: "", name: "", robotType: "dingtalk", group: "", events: ["执行成功", "执行失败"], enabled: true, hasWebhook: false, hasSecret: false };
      return `
        <div class="notice warn">不会显示已有 webhook URL、secret 或 token。下方替换字段只在保存请求中发送，留空表示不变，前端不缓存输入值。</div>
        <div style="height:14px"></div>
        <div class="form-grid">
          <div class="field"><label>机器人 ID</label><input id="cfgRobotId" value="${esc(data.id)}" ${mode === "edit" ? "disabled" : ""} placeholder="例如 dingtalk-finance"></div>
          <div class="field"><label>机器人名称</label><input id="cfgRobotName" value="${esc(data.name)}"></div>
          <div class="field"><label>机器人类型</label><select id="cfgRobotType"><option value="dingtalk" ${data.robotType !== "webhook" ? "selected" : ""}>钉钉</option><option value="webhook" ${data.robotType === "webhook" ? "selected" : ""}>通用 Webhook</option></select></div>
          <div class="field"><label>通知群/别名</label><input id="cfgRobotGroup" value="${esc(data.group || "")}"></div>
          <div class="field span-2"><label>事件</label><input id="cfgRobotEvents" value="${esc(toArray(data.events).join(","))}" placeholder="多个用逗号分隔"></div>
          <div class="field"><label>Webhook 状态</label><input value="${data.hasWebhook ? "已配置（已隐藏）" : "未配置"}" disabled></div>
          <div class="field"><label>Secret 状态</label><input value="${data.hasSecret ? "已配置（已隐藏）" : "未配置"}" disabled></div>
          <div class="field span-2"><label>替换 Webhook URL</label><input id="cfgRobotWebhookReplacement" type="password" autocomplete="new-password" value="" placeholder="留空则保持不变"></div>
          <div class="field"><label>替换 Secret</label><input id="cfgRobotSecretReplacement" type="password" autocomplete="new-password" value="" placeholder="留空则保持不变"></div>
          <div class="field"><label>状态</label><label class="radio-chip"><input id="cfgRobotEnabled" type="checkbox" ${data.enabled !== false ? "checked" : ""}>启用</label></div>
        </div>
      `;
    }
