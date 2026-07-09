    const DEFAULT_DINGTALK_USER_ID = "11464769";
    const EXTERNAL_TOKEN_QUERY_KEYS = ["token", "authorization", "access_token"];
    const savedUseDefaultNotifyUser = localStorage.getItem("portalUseDefaultNotifyUser");

    const state = {
      loggedIn: true,
      page: "dashboard",
      role: localStorage.getItem("portalRole") || "executor",
      user: {
        name: localStorage.getItem("portalUser") || "张三",
        dept: "财务共享中心",
        dingTalkUserId: localStorage.getItem("portalDingTalkUserId") || DEFAULT_DINGTALK_USER_ID
      },
      externalAuth: {
        claimedAccount: "",
        claimedUserName: "",
        status: "none"
      },
      activeConfigTab: "plant",
      selectedTCode: "ZFI019NL",
      executing: false,
      currentRun: null,
      bridge: { online: false, lastChecked: "", database: "", queueMode: "" },
      queue: { online: false, runningRunId: "", queuedCount: null, queuePosition: null, runsAhead: null, workItemsAhead: null, lastChecked: "" },
      config: { online: false, source: "fallback", lastLoaded: "", error: "" },
      report: { online: false, loading: false, from: "", to: "", summary: null, transactionRanking: [], error: "" },
      modal: null,
      form: {
        account: "",
        password: "",
        tCode: "ZFI019NL",
        factoryGroup: "PINGHU_30",
        plants: ["1022", "1024", "1032", "6041"],
        notify: true,
        useDefaultNotifyUser: savedUseDefaultNotifyUser === null ? true : savedUseDefaultNotifyUser !== "0",
        remark: ""
      },
      steps: [
        { text: "提交任务入队", status: "pending", time: "" },
        { text: "等待串行执行器领取", status: "pending", time: "" },
        { text: "SAP 会话与事务码跳转", status: "pending", time: "" },
        { text: "执行脚本并保存/导出", status: "pending", time: "" },
        { text: "记录日志并推送通知", status: "pending", time: "" }
      ],
      logs: [],
      scheduleForm: {
        id: "",
        name: "周一 ZFI019NL 周损益链路",
        tCode: "ZFI019NL",
        factoryGroup: "PINGHU_ALL",
        plants: ["1022", "1024", "1032", "6041", "103C", "1031", "1033", "103D", "1035", "1036"],
        execTime: "08:00",
        frequency: "weekly",
        notifyStart: true,
        notifySuccess: true,
        notifyFail: true,
        enabled: true,
        nameEdited: false
      }
    };

    const DEFAULT_BRIDGE_API = "http://127.0.0.1:8080";
    if (localStorage.getItem("sapRpaApiBase") === "http://127.0.0.1:17890") {
      localStorage.removeItem("sapRpaApiBase");
    }
    const BRIDGE_API = window.SAP_RPA_API_BASE || localStorage.getItem("sapRpaApiBase") || DEFAULT_BRIDGE_API;
    const CONFIG_API_PATHS = {
      root: "/api/config",
      plants: code => "/api/config/plants/" + encodeURIComponent(code),
      plantGroups: id => "/api/config/plant-groups/" + encodeURIComponent(id),
      transactionRules: tcode => "/api/config/transaction-rules/" + encodeURIComponent(tcode),
      notificationRobots: id => "/api/config/notification-robots/" + encodeURIComponent(id),
      schedules: id => "/api/schedules/" + encodeURIComponent(id)
    };
    let tCodes = [
      { code: "ZFI072A", name: "采购价月表", module: "FI", stage: "并行启动", script: "ZFI072A.vbs", icon: "circle-dollar-sign", params: ["year", "week", "plants"], factoryRule: "先四个集采工厂，再其他工厂", defaultPlantGroup: "PINGHU_ALL", automation: "script", timeout: 300, retry: 2, enabled: true },
      { code: "ZFI085", name: "维护特殊价格", module: "FI", stage: "并行启动", script: "ZFI085.vbs", icon: "badge-dollar-sign", params: ["year", "week", "plants"], factoryRule: "按配置工厂执行", defaultPlantGroup: "PINGHU_ALL", automation: "openOnly", timeout: 180, retry: 2, enabled: false },
      { code: "ZFI014D", name: "维护仓领退料", module: "FI", stage: "并行启动", script: "ZFI014D.vbs", icon: "package-check", params: ["year", "week", "plants"], factoryRule: "按配置工厂执行", defaultPlantGroup: "PINGHU_ALL", automation: "openOnly", timeout: 180, retry: 2, enabled: true },
      { code: "ZFI072N", name: "维护采购价", module: "FI", stage: "顺序执行", script: "ZFI072N.vbs", icon: "receipt-text", params: ["year", "week", "plants"], factoryRule: "按配置工厂执行", defaultPlantGroup: "PINGHU_ALL", automation: "openOnly", timeout: 180, retry: 2, enabled: true },
      { code: "ZFI057", name: "产值拆分", module: "CO", stage: "顺序执行", script: "ZFI057.vbs", icon: "trending-up", params: ["year", "week", "plants"], factoryRule: "按配置工厂执行", defaultPlantGroup: "PINGHU_ALL", automation: "openOnly", timeout: 600, retry: 3, enabled: true },
      { code: "ZCO020", name: "拆分验证", module: "CO", stage: "顺序执行", script: "ZCO020.vbs", icon: "list-checks", params: ["year", "week", "plants"], factoryRule: "按配置工厂执行", defaultPlantGroup: "PINGHU_ALL", automation: "openOnly", timeout: 240, retry: 2, enabled: true },
      { code: "ZPP063", name: "验证备注", module: "PP", stage: "顺序执行", script: "ZPP063.vbs", icon: "clipboard-check", params: ["year", "week", "plants"], factoryRule: "按配置工厂执行", defaultPlantGroup: "PINGHU_ALL", automation: "openOnly", timeout: 240, retry: 2, enabled: true },
      { code: "ZPP063X", name: "回检验证", module: "PP", stage: "顺序执行", script: "ZPP063X.vbs", icon: "rotate-ccw", params: ["year", "week", "plants"], factoryRule: "按配置工厂执行", defaultPlantGroup: "PINGHU_ALL", automation: "openOnly", timeout: 240, retry: 2, enabled: true },
      { code: "ZFI019NC", name: "验证修正", module: "FI", stage: "顺序执行", script: "ZFI019NC.vbs", icon: "file-pen-line", params: ["year", "week", "plants"], factoryRule: "按配置工厂执行", defaultPlantGroup: "PINGHU_ALL", automation: "openOnly", timeout: 240, retry: 2, enabled: true },
      { code: "ZFI080", name: "实际材料保存", module: "FI", stage: "核心并行", script: "ZFI080.vbs", icon: "boxes", params: ["plants"], factoryRule: "按业务范围和周结日期保存", defaultPlantGroup: "PINGHU_ALL", automation: "script", timeout: 1200, retry: 2, enabled: true },
      { code: "ZFI019NI", name: "实际材料结果保存", module: "FI", stage: "核心并行", script: "ZFI019NI.vbs", icon: "save", params: ["year", "week", "plants", "businessAreas"], factoryRule: "按配置工厂执行", defaultPlantGroup: "PINGHU_ALL", automation: "openOnly", timeout: 180, retry: 2, enabled: true },
      { code: "ZCO019", name: "标准材料成本", module: "CO", stage: "核心并行", script: "ZCO019.vbs", icon: "tag", params: ["plants"], factoryRule: "明细保存与汇总保存", defaultPlantGroup: "PINGHU_ALL", automation: "script", timeout: 600, retry: 2, enabled: true },
      { code: "ZFI019NA", name: "周损益生成", module: "FI", stage: "最终生成", script: "ZFI019NA.vbs", icon: "file-spreadsheet", params: ["plants"], factoryRule: "东台、黄江等范围保存", defaultPlantGroup: "PINGHU_ALL", automation: "script", timeout: 600, retry: 2, enabled: true },
      { code: "ZFIR034", name: "维护特殊价格（ZFI085）", module: "FI", stage: "并行启动", script: "ZFIR034.vbs", icon: "calendar-days", params: ["period", "weekEnd"], factoryRule: "按系统日期上一完整周执行", defaultPlantGroup: "PINGHU_ALL", automation: "script", timeout: 300, retry: 2, enabled: true },
      { code: "ZFI019NL", name: "周损益保存导出", module: "FI", stage: "最终生成", script: "ZFI019NL.vbs", icon: "bar-chart-3", params: ["businessAreas"], factoryRule: "按选中厂区工厂和业务范围导出", defaultPlantGroup: "PINGHU_30", automation: "script", timeout: 300, retry: 3, enabled: true },
      { code: "ZFI080B", name: "工费率保存", module: "FI", stage: "周结推送", script: "ZFI080B.vbs", icon: "calculator", params: ["year", "week", "plants"], factoryRule: "东台、黄江保存", defaultPlantGroup: "PINGHU_ALL", automation: "openOnly", timeout: 240, retry: 2, enabled: true },
      { code: "ZFI148", name: "推送大数据平台", module: "FI", stage: "周结推送", script: "ZFI148.vbs", icon: "send", params: ["year", "week", "plants"], factoryRule: "保存后推送", defaultPlantGroup: "PINGHU_ALL", automation: "openOnly", timeout: 240, retry: 2, enabled: true }
    ];
    const stageOrder = ["并行启动", "顺序执行", "核心并行", "最终生成", "周结推送"];
    const zfi072PlantRule = ["5021", "9301", "1101", "207M", "1024", "1032", "6041", "1022", "103C", "103D", "1031", "1033", "103C", "103D", "1035", "1036"];
    const workflow = [
      {
        title: "并行启动阶段",
        items: [
          { name: "ZFI085", desc: "维护特殊价格后保存", kind: "primary" },
          { name: "ZFI014D", desc: "维护仓领退料后保存", kind: "primary" },
          { name: "ZFI072A", desc: "先跑 4 个集采工厂，再跑其他工厂并保存" }
        ],
        note: "启动阶段可并行准备，减少主链路等待。"
      },
      {
        title: "顺序执行阶段",
        items: [
          { name: "ZFI072N", desc: "维护采购价后保存" },
          { name: "ZFI057", desc: "产值拆分后进入 ZCO020 结果验证" },
          { name: "ZCO020", desc: "验证通过进入下一环节；不通过回检修正", kind: "check" }
        ],
        note: "验证点必须保留，避免遗漏带入后续保存。"
      },
      {
        title: "核心并行阶段",
        items: [
          { name: "ZCO019", desc: "明细保存与汇总保存，失败后重跑", kind: "primary" },
          { name: "ZFI080", desc: "实际领料保存，按业务范围和日期保存", kind: "primary" }
        ],
        note: "两个保存点都完成后，才能生成最终周损益。"
      },
      {
        title: "最终生成阶段",
        items: [
          { name: "ZFI019NL", desc: "生成目标表格并保存导出", kind: "check" },
          { name: "底稿同步", desc: "保存后同步到大数据平台，抽取最新结果" },
          { name: "日志通知", desc: "记录状态、截图、失败原因并推送" }
        ],
        note: "当前页面优先跑通事务码登录和脚本执行。"
      }
    ];
    const plantCatalog = {
      "5021": { code: "5021", name: "集采工厂", area: "", groups: ["PROCUREMENT"], enabled: true },
      "9301": { code: "9301", name: "集采工厂", area: "", groups: ["PROCUREMENT"], enabled: true },
      "1101": { code: "1101", name: "集采工厂", area: "", groups: ["PROCUREMENT"], enabled: true },
      "207M": { code: "207M", name: "集采工厂", area: "", groups: ["PROCUREMENT"], enabled: true },
      "1022": { code: "1022", name: "平湖三厂", area: "2800", groups: ["PINGHU_30"], enabled: true },
      "1024": { code: "1024", name: "平湖三厂", area: "2900", groups: ["PINGHU_30"], enabled: true },
      "1032": { code: "1032", name: "平湖三厂", area: "9200", groups: ["PINGHU_30"], enabled: true },
      "6041": { code: "6041", name: "平湖三厂", area: "2800", groups: ["PINGHU_30"], enabled: true },
      "103C": { code: "103C", name: "平湖七厂", area: "2910", groups: ["PINGHU_7", "PINGHU_19"], enabled: true },
      "1031": { code: "1031", name: "平湖一厂", area: "3400", groups: ["PINGHU_19"], enabled: true },
      "1033": { code: "1033", name: "平湖九厂", area: "2920", groups: ["PINGHU_19"], enabled: true },
      "103D": { code: "103D", name: "平湖九厂", area: "2920", groups: ["PINGHU_19"], enabled: true },
      "1035": { code: "1035", name: "平湖二厂", area: "5100", groups: ["PINGHU_25"], enabled: true },
      "1036": { code: "1036", name: "平湖五厂", area: "2790", groups: ["PINGHU_25"], enabled: true }
    };
    let plants = Object.values(plantCatalog);
    const procurementPlants = ["5021", "9301", "1101", "207M"];
    const businessRangeLabels = {
      PROCUREMENT: "集采工厂",
      PINGHU_30: "平湖三厂 / 平湖十厂",
      PINGHU_7: "平湖七厂",
      PINGHU_19: "平湖一厂 / 平湖九厂",
      PINGHU_25: "平湖二厂 / 平湖五厂",
      PINGHU_ALL: "全部平湖业务范围"
    };
    const legacyBusinessRangeNames = {
      "Procurement plants": "集采工厂",
      "Procurement Plants": "集采工厂",
      "Pinghu 30": "平湖三厂 / 平湖十厂",
      "Pinghu 7": "平湖七厂",
      "Pinghu 19": "平湖一厂 / 平湖九厂",
      "Pinghu 25": "平湖二厂 / 平湖五厂",
      "All Pinghu plants": "全部平湖业务范围",
      "All Pinghu Plants": "全部平湖业务范围"
    };
    let factoryGroups = [
      { id: "PINGHU_30", name: "平湖三厂 / 平湖十厂", shortName: "三厂/十厂", plants: ["1022", "1024", "1032", "6041"], zfi019nlAreas: ["2900", "9200", "2800", "3960"], zfi080Areas: ["2900", "9200", "2800", "3960"], zfi072Plants: ["1024", "1032", "6041", "1022"], zco019Plants: ["1022", "1024", "1032", "6041"] },
      { id: "PINGHU_7", name: "平湖七厂", shortName: "七厂", plants: ["103C"], zfi019nlAreas: ["2910"], zfi080Areas: ["2910"], zfi072Plants: ["103C"], zco019Plants: ["103C"] },
      { id: "PINGHU_19", name: "平湖一厂 / 平湖九厂", shortName: "一厂/九厂", plants: ["1031", "1033", "103C", "103D"], zfi019nlAreas: ["3400", "2920"], zfi080Areas: ["3400", "2920"], zfi072Plants: ["1031", "1033", "103C", "103D"], zco019Plants: ["1031", "1033", "103C", "103D"] },
      { id: "PINGHU_25", name: "平湖二厂 / 平湖五厂", shortName: "二厂/五厂", plants: ["1035", "1036"], zfi019nlAreas: ["5100", "2790"], zfi080Areas: ["5100", "2790"], zfi072Plants: ["1035", "1036"], zco019Plants: ["1035", "1036"] },
      { id: "PINGHU_ALL", name: "全部平湖业务范围", shortName: "全部", plants: ["1022", "1024", "1032", "6041", "103C", "1031", "1033", "103D", "1035", "1036"], zfi019nlAreas: ["2900", "9200", "2800", "3960", "2910", "3400", "2920", "5100", "2790"], zfi080Areas: ["2900", "9200", "2800", "3960", "2910", "3400", "2920", "5100", "2790"], zfi072Plants: ["5021", "9301", "1101", "207M", "1024", "1032", "6041", "1022", "103C", "1031", "1033", "103D", "1035", "1036"], zco019Plants: ["1022", "1024", "1032", "6041", "103C", "1031", "1033", "103D", "1035", "1036"] }
    ];
    const businessAreaCatalog = [
      { code: "2900", name: "平湖三厂" },
      { code: "9200", name: "平湖三厂" },
      { code: "2800", name: "平湖三厂" },
      { code: "3960", name: "平湖十厂" },
      { code: "2910", name: "平湖七厂" },
      { code: "3400", name: "平湖一厂" },
      { code: "2920", name: "平湖九厂" },
      { code: "5100", name: "平湖二厂" },
      { code: "2790", name: "平湖五厂" }
    ];
    let robots = [
      { id: "rbt-001", name: "财务通知群", group: "SAP 自动化执行通知", events: ["执行完成", "执行失败"], enabled: true, lastPush: "2026/06/07 08:05", hasWebhook: true, hasSecret: true },
      { id: "rbt-002", name: "IT 运维告警群", group: "SAP 运维告警", events: ["系统异常", "脚本失败"], enabled: true, lastPush: "2026/06/07 14:30", hasWebhook: true, hasSecret: true }
    ];
    let scheduleTasks = [
      { id: "SCH-001", name: "周一 ZFI019NL 周损益链路", tCode: "ZFI019NL", factoryGroup: "PINGHU_ALL", plants: ["1022", "1024", "1032", "6041", "103C", "1031", "1033", "103D", "1035", "1036"], time: "08:00", frequency: "每周一", status: "启用", next: "2026/06/15 08:00" }
    ];
    let history = [
      { id: "RUN-20260608-001", time: "2026/06/08 08:00", task: "周一 ZFI019NL 周损益链路", tCode: "ZFI019NL", plant: "1022,1024,1032,6041", duration: "45 秒", status: "成功", notify: "已推送", result: "保存并导出周损益结果 18 行" },
      { id: "RUN-20260607-002", time: "2026/06/07 08:00", task: "ZFI057 产值拆分", tCode: "ZFI057", plant: "1032", duration: "1 分 23 秒", status: "失败", notify: "告警已推送", result: "SAP 连接超时" }
    ];
    const pages = {
      dashboard: "工作台",
      execute: "执行任务",
      reports: "统计报表",
      schedule: "定时任务",
      config: "基础配置"
    };
    const fallbackConfig = JSON.parse(JSON.stringify({
      tCodes,
      plants,
      factoryGroups,
      robots,
      scheduleTasks
    }));
