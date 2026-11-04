# SAP 财务管报自动化 AI 交接文档

## 项目定位

本项目包含网页卡片、定时任务、Windows 本地 API、SAP GUI VBS 自动化、NCo/RFC 取数、ALV 保存/Excel 导出、组织归档、钉钉通知和统计报表。改动必须同时检查前端、API、数据库快照、执行队列、SAP 脚本、导出归档、通知与功能说明书。

## 代码与运行目录

- Git 仓库：D:\RPA\RpaProject
- GitHub：https://github.com/ckstock/sap_rpa
- 正式运行根目录：D:\RPA
- 网关：RpaProject/gateway
- 本地 API / SAP 启动器：RpaProject/网页启动登录/SapWebLauncher
- VBS：RpaProject/SapVbs
- 安装包：RpaProject/上线安装包
- 启动脚本：RpaProject/启动脚本
- 定时任务集中管理：RpaProject/tools/schedule-admin
- 功能说明书：RpaProject/SapRpa_V2_功能说明书.html
- 复盘：RpaProject/反思.md

运行目录可能有旧副本。经过测试的仓库源码才是发布源，不能把旧运行副本直接覆盖回仓库。

## 禁止提交

不得提交密码、Token、Cookie、个人访问令牌、config.local.json、SQLite/数据库、日志、临时 VBS、证书私钥、共享目录凭据、bin/obj/node_modules、打包 EXE 和其他构建产物。

## SAP 连接

必须同时保留 direct 和 messageServer 两种模式。

- direct：appServerHost/ipAddress + systemNumber/instanceNumber + systemId
- messageServer：messageServerHost + messageServerService + systemId + logonGroup
- 两种模式均包含 client、user、password、language，可选 sapRouter
- NCo 映射 MessageServerHost、MessageServerService、SystemID、LogonGroup
- SAP GUI 使用 sapshcut 的消息服务器/登录组参数
- 切换 T/P/其他已保存系统时选择已有配置，不强制重复录入

## API 与桌面会话

可视化模式在 marcus 交互会话运行，SAP GUI 可见；后台模式可无人值守，但堡垒机桌面看不到 GUI。切换前确认队列为空。健康 API 不因其他堡垒机用户登录而重启。服务器重启后的恢复策略以当前计划任务为准。

## 定时任务核心约束

- 每条任务使用唯一 ID，创建时保存完整执行快照；基础卡片配置之后变化不得反向改写已保存任务。
- 只有用户编辑该任务并保存时，才按任务 ID 更新数据库和调度时间。
- 删除任务同时删除数据库记录、调度器登记和尚未开始的排队项。
- 普通用户只查看和管理自己创建的任务；集中管理工具用于离职人员和异常任务清理。
- 同名卡片或相同参数不能以业务键互相覆盖，所有权和任务 ID 必须进入唯一性与查询条件。
- 执行前提醒、开始、完成/失败通知遵循同一串行执行语义。

## SAP 与导出关键规则

- 卡片带“（保存）”表示 VBS 必须真实点击 SAP 保存按钮；Excel 导出不能替代 SAP 保存。
- 无数据与失败必须区分：无数据跳过导出并在钉钉提示，不能把找不到 ALV 控件统一判为失败。
- 导出按组织归档；同组织、同周、同文件名有则合并更新、无则新增。合并键按卡片定义，不能全局复用。
- ZFI057 的物料来源、东台 800* 过滤、业务范围到工厂映射和六字段 Excel modify 键均为专用规则。
- ZFI057 发布成本月为 1/3/5/7/9/11 月；跨月从截止月份向前找到最近发布成本月，再按自然月拆分。S_KADAT 通常从 2 号起，但截止日为 1 号造成下限大于上限时，该段从 1 号起。
- 钉钉仅在发送时调用 ZFI_GET_DDID；OV_DDID 有值使用它，无值回退网页人员编号。

## 修改与发布流程

1. 阅读项目规则、功能说明书和相关 skill。
2. 搜索真实调用链与正式运行路径，不能只改页面或单个 VBS。
3. 小范围实现并补测试；运行目录变更先检查队列和进程。
4. 执行构建、单测、真实场景回归、导出/通知/数据库验证。
5. 更新功能说明书、README、反思和本交接文档。
6. 检查 git diff、敏感信息、未跟踪文件与运行目录差异。
7. 提交推送后核对默认分支、远端 SHA 与远端提交树。

## 常用验证

- Node：tests 下 node:test 用例
- Python：tools/schedule-admin/test_schedule_api.py
- C#：SapWebLauncher 构建与自测
- PowerShell：配置与启动脚本语法检查
- Git：diff --check、敏感信息扫描、ls-remote、ls-tree
- 真实回归：任务保存/编辑/删除、执行前提醒、SAP 登录、保存按钮、ALV 导出、共享目录、钉钉通知

## 排查原则

不要根据历史聊天猜测正式代码。先核对仓库 HEAD、正式目录、运行进程、数据库和 RUN ID。遇到“以前可以、现在不行”，优先比较进程启动时间、运行账号/会话、配置来源、部署版本和任务快照。修复必须定位根因，不能用成功提示掩盖 SAP 未保存、未导出或通知未发送。
