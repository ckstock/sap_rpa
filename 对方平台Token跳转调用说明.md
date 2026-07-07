# 对方平台 Token 跳转调用说明

## 目标

对方平台点击图标后跳转到 SAP RPA V2 页面，并把当前用户身份通过 URL token 带过来。SAP RPA 页面只解析 JWT payload 里的 `Account` 作为联调通知员工号，提交任务时写入 `operator.dingTalkUserId` 和 `operator.ddid`。

当前联调默认通知人为 `11464769`。如果 URL token 解析失败或缺少 `Account`，页面会继续使用默认通知人。

当前页面不再提供“钉钉扫码登录”按钮；对方平台按本说明直接拼接 URL 并跳转即可。

## 推荐 URL

```text
https://sap-rpa-v2.netlify.app/index.html?token=<URL编码后的JWT>
```

也兼容下面两种格式：

```text
https://sap-rpa-v2.netlify.app/index.html?authorization=Bearer%20<URL编码后的JWT>
https://sap-rpa-v2.netlify.app/index.html?access_token=<URL编码后的JWT>
```

取值优先级为 `token`、`authorization`、`access_token`。`authorization` 支持 `Bearer <jwt>` 前缀，URL 中空格请编码为 `%20`。

## JWT payload 要求

必须包含：

```json
{
  "Account": "员工号"
}
```

兼容小写 `account`。`UserName` / `userName` 可选，仅用于页面提示，不作为通知目标。

## Vue 跳转示例

```js
function openSapRpa(jwt) {
  const base = "https://sap-rpa-v2.netlify.app/index.html";
  const url = `${base}?token=${encodeURIComponent(jwt)}`;
  window.open(url, "_blank", "noopener");
}
```

如果传的是 Bearer 字符串：

```js
function openSapRpaWithBearer(jwt) {
  const base = "https://sap-rpa-v2.netlify.app/index.html";
  const authorization = `Bearer ${jwt}`;
  window.open(`${base}?authorization=${encodeURIComponent(authorization)}`, "_blank", "noopener");
}
```

## SAP RPA 页面行为

- 页面只在浏览器前端解析 JWT payload，不验签、不校验过期时间。
- 解析成功后会从地址栏清理 token 参数。
- 页面不保存 JWT/Bearer 原文，不写入 localStorage。
- 页面没有钉钉扫码登录入口，对方平台图标点击后直接跳转 URL。
- 识别到合法 `Account` 后，页面会直接进入执行任务页，自动取消“使用联调默认通知人 11464769”，当前提交用户改为 token 中的 `Account`。
- 用户重新勾选默认通知人后，会切回 `11464769`。

## 联调测试步骤

1. 打开 `D:\sap_ai\打开Token跳转测试.bat`。
2. 在测试页粘贴测试 JWT 或 `Bearer <JWT>`。
3. 点击解析，确认能看到 `Account`。
4. 点击跳转到正式 SAP RPA 页面。
5. 正式 SAP RPA 页面应直接进入执行任务页，确认默认通知人复选框自动取消，当前提交显示为 token 中的 `Account`。
6. 提交任务后检查本地 API 请求或钉钉通知目标，应该使用该 `Account`。

## 安全边界

当前方案只用于联调身份传递，不能作为生产可信身份。生产上线必须由后端或公司 SSO 对 token 验签、校验有效期和来源，再写入最终操作人和通知目标。

对方平台不要在日志、截图、埋点、Referer 或群消息中记录完整 URL。文档和测试记录中也不要粘贴真实 token。
