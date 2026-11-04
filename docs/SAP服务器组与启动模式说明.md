# SAP 服务器组与启动模式说明

## 交付边界

仓库保存可复现的源代码、配置脚本、测试和文档，包括定时任务集中管理工具、SAP 登录配置、可视化/后台启动切换脚本及功能说明书。密码、令牌、本机 config.local.json、数据库、日志、临时 VBS、证书、构建产物和共享目录凭据不得提交。

## SAP 连接模式

系统同时支持应用服务器直连和消息服务器/登录组模式，并允许在已保存的系统配置之间切换。

### 应用服务器直连

- connectionMode: direct
- appServerHost / ipAddress: 应用服务器地址
- systemNumber / instanceNumber: 实例编号
- systemId: SAP SID
- client、user、password、language: 登录信息
- sapRouter: 可选

### 消息服务器 / 登录组

- connectionMode: messageServer
- messageServerHost / MSHOST: 消息服务器主机，例如 S4PRD01
- messageServerService: 消息服务器服务或端口，例如 3611
- systemId / SID: 系统标识，例如 EP1
- logonGroup: 登录组，例如 HUANAN_PRD
- client、user、password、language: 登录信息
- sapRouter: 可选

NCo 分别映射 MessageServerHost、MessageServerService、SystemID 和 LogonGroup；SAP GUI 使用 sapshcut 的消息服务器和登录组参数。禁止把组服务器配置降级成普通应用服务器直连。

## API 启动模式

- 可视化模式：由 marcus 登录触发，在 marcus 的交互桌面运行，SAP GUI 操作窗口可见。
- 后台模式：由 Windows 启动任务运行，适合无人值守，但堡垒机桌面看不到 GUI。
- 切换模式前必须确认执行队列为空，再停止当前实例并切换计划任务，避免双实例和中断任务。
- 其他堡垒机用户登录或退出不得重启健康 API，也不得改变当前运行会话。
- 若计划任务保存了 marcus 密码，密码变更后需要更新任务凭据；仅登录触发且“仅用户登录时运行”的任务不保存密码。

脚本：启动脚本/start_sap_rpa_interactive.* 与 启动脚本/restore_sap_rpa_background.*。

## 上线复核

1. 配置脚本可选择已有系统或新增系统，并正确保存 direct/messageServer 字段。
2. SAP GUI 登录、NCo/RFC、API 健康检查均正常。
3. 队列串行执行、钉钉通知、ALV 保存/导出、共享目录归档均完成真实场景验证。
4. 服务器重启后按选定模式恢复，其他堡垒机账号登录不会重复启动。
5. 推送前执行测试、敏感信息扫描、远端 SHA 与提交树核对。
