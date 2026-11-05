# VBS 事务脚本清单

VBS 唯一源码目录：`网页启动登录/transactions/`。正式运行目录 `D:\RPA\transactions\` 和跨电脑迁移包中的 `transactions/` 必须由该目录同步生成，不允许维护多个分叉版本。

## 可执行卡片脚本

以下 11 张卡片在 `transaction-config.json` 中配置为 `automation=script`，打包时必须存在对应 VBS，否则直接终止打包。

| 卡片/事务 | 脚本 |
| --- | --- |
| ZFI072A | `ZFI072A.vbs` |
| ZFI072N | `ZFI072N.vbs` |
| ZFI057 | `ZFI057.vbs` |
| ZCO020 | `ZCO020.vbs` |
| ZFI080 | `ZFI080.vbs` |
| ZCO019 | `ZCO019.vbs` |
| ZFI019NA | `ZFI019NA.vbs` |
| ZFIR034 | `ZFIR034.vbs` |
| ZFI019NL | `ZFI019NL.vbs` |
| ZFI080B | `ZFI080B.vbs` |
| ZFI148 | `ZFI148.vbs` |

公共脚本：`sap_alv_export_helper.vbs`。因此迁移包合计包含 12 个 `.vbs` 文件。

## OpenOnly 卡片

以下卡片只打开 SAP 事务或当前处于禁用状态，不由执行器调用 VBS，因此不应创建假的空脚本：

- ZFI085
- ZFI014D
- ZPP063
- ZPP063X
- ZFI019NC

## 验收

1. `tests/package-transactions-contract.test.js` 必须通过。
2. `PACKAGE_MANIFEST.json` 的文件数量和 SHA-256 必须与迁移包一致。
3. 迁移 ZIP 中必须存在 11 个卡片 VBS、1 个 ALV 公共助手和 `transaction-config.json`。
4. 迁移包不得包含 `config.local.json`、数据库、日志、证书、密码或导出的 Excel。
