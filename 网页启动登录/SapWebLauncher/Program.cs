using Microsoft.Win32;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace SapWebLauncher;

static class Program
{
    private const string PrimaryProtocolName = "sap-rpa";
    private const string MutexId = "SapWebLauncher-SingleInstance-Mutex";
    private const int BridgePort = 17890;
    private const int DefaultRunListLimit = 50;
    private static readonly string ExeDirectory = AppContext.BaseDirectory;
    private static readonly string LocalConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SapWebLauncher");
    private static readonly string RuntimeRoot = ResolveRuntimeRoot();
    private static readonly string DataDirectory = Path.Combine(RuntimeRoot, "data");
    private static readonly string LogDirectory = Path.Combine(RuntimeRoot, "logs");
    private static readonly string OutputDirectory = Path.Combine(RuntimeRoot, "outputs");
    private static readonly string RuntimeTransactionsDirectory = Path.Combine(RuntimeRoot, "transactions");
    private static readonly string LogFilePath = Path.Combine(LogDirectory, "launcher.log");
    private static readonly string ConfigFilePath = Path.Combine(LocalConfigDirectory, "config.json");
    private static readonly string DatabaseFilePath = Path.Combine(DataDirectory, "sap-rpa-config.db");
    private static readonly string LegacyDatabaseFilePath = Path.Combine(LocalConfigDirectory, "sap-rpa-config.db");
    private static readonly string ExecutorId = $"{Environment.MachineName}\\{Environment.UserName}";
    private static readonly object DatabaseInitLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    static void Main(string[] args)
    {
        Log($"启动参数: {MaskRawArg(args.FirstOrDefault())}");

        if (args.Length > 0 &&
            (args[0].Equals("--serve", StringComparison.OrdinalIgnoreCase) ||
             args[0].Equals("serve", StringComparison.OrdinalIgnoreCase)))
        {
            RunBridgeServer();
            return;
        }

        if (args.Length > 0 &&
            (args[0].Equals("--init-db", StringComparison.OrdinalIgnoreCase) ||
             args[0].Equals("init-db", StringComparison.OrdinalIgnoreCase)))
        {
            InitializeDatabase(seedFromScripts: true);
            Console.WriteLine($"SQLite 数据库已初始化: {DatabaseFilePath}");
            return;
        }

        using var mutex = new Mutex(true, MutexId);
        if (!mutex.WaitOne(TimeSpan.Zero, true))
        {
            Log("检测到已有实例在运行，当前实例退出");
            return;
        }

        if (args.Length == 0)
        {
            RunDirect();
            return;
        }

        string raw = args[0];
        if (IsSupportedUri(raw))
        {
            var query = ParseUri(raw);
            MergePayload(query);
            Log($"URI 解析结果: {DescribeQuery(query)}");
            RunFromUri(query, GetProtocolName(raw));
            return;
        }

        if (raw.Equals("test", StringComparison.OrdinalIgnoreCase))
        {
            Environment.Exit(RunSelfTest());
            return;
        }

        if (raw.Equals("--register", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("/register", StringComparison.OrdinalIgnoreCase))
        {
            RegisterProtocols();
            Console.WriteLine($"{PrimaryProtocolName}:// 协议已注册");
            return;
        }

        Console.WriteLine($"用法: {Process.GetCurrentProcess().ProcessName}.exe [--register]");
        Console.WriteLine($"  初始化本机数据库: {Process.GetCurrentProcess().ProcessName}.exe --init-db");
        Console.WriteLine($"  启动本机 Bridge API: {Process.GetCurrentProcess().ProcessName}.exe --serve");
        Console.WriteLine($"  或从浏览器跳转 {PrimaryProtocolName}://run?action=run&tcode=ZFI019NL&script=openOnly&plants=1022,1024");
    }

    static bool IsSupportedUri(string raw)
    {
        return raw.StartsWith(PrimaryProtocolName + "://", StringComparison.OrdinalIgnoreCase);
    }

    static string ResolveRuntimeRoot()
    {
        string configured = Environment.GetEnvironmentVariable("SAP_RPA_HOME") ?? "";
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));

        const string handoffRoot = @"D:\sap_ai";
        if (Directory.Exists(handoffRoot))
            return handoffRoot;

        return LocalConfigDirectory;
    }

    static void EnsureRuntimeDirectories()
    {
        Directory.CreateDirectory(RuntimeRoot);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(OutputDirectory);
        Directory.CreateDirectory(RuntimeTransactionsDirectory);
    }

    static void MigrateLegacyDatabaseIfNeeded()
    {
        try
        {
            if (File.Exists(DatabaseFilePath) || !File.Exists(LegacyDatabaseFilePath))
                return;

            File.Copy(LegacyDatabaseFilePath, DatabaseFilePath, overwrite: false);
            Log($"Legacy SQLite database copied to runtime data folder: {LegacyDatabaseFilePath} -> {DatabaseFilePath}");
        }
        catch (Exception ex)
        {
            Log($"Legacy SQLite database migration skipped: {ex.Message}");
        }
    }

    static string GetProtocolName(string raw)
    {
        int pos = raw.IndexOf("://", StringComparison.Ordinal);
        return pos > 0 ? raw[..pos].ToLowerInvariant() : PrimaryProtocolName;
    }

    static void RegisterProtocols()
    {
        string exePath = Process.GetCurrentProcess().MainModule!.FileName;
        RegisterProtocol(PrimaryProtocolName, exePath);
    }

    static void RegisterProtocol(string protocolName, string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{protocolName}");
            key.SetValue("", $"URL:{protocolName} Protocol");
            key.SetValue("URL Protocol", "");

            using var cmdKey = key.CreateSubKey(@"shell\open\command");
            cmdKey.SetValue("", $"\"{exePath}\" \"%1\"");
            Log($"协议注册成功: {protocolName}, exe={exePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"注册 {protocolName} 协议失败: {ex.Message}");
            Log($"协议注册失败: {protocolName}, {ex}");
        }
    }

    static void RunDirect()
    {
        var p = ApplyLocalConfig(new SapRunParams
        {
            TCode = "ZFI019NL",
            Script = "openOnly"
        });
        LaunchSapGuiAndExecute(p);
    }

    static NameValueCollection ParseUri(string raw)
    {
        var result = new NameValueCollection();
        int schemePos = raw.IndexOf("://", StringComparison.Ordinal);
        string rest = schemePos >= 0 ? raw[(schemePos + 3)..] : raw;

        int queryPos = rest.IndexOf('?');
        if (queryPos >= 0)
        {
            string path = rest[..queryPos].Trim('/');
            if (!string.IsNullOrWhiteSpace(path) && !path.Contains('='))
                result["action"] = path;

            ParseQueryPart(rest[(queryPos + 1)..], result);
        }
        else
        {
            ParseQueryPart(rest.Trim('/'), result);
        }

        return result;
    }

    static void ParseQueryPart(string queryPart, NameValueCollection result)
    {
        var parts = queryPart.Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            int eq = part.IndexOf('=');
            if (eq < 0) continue;

            string key = Uri.UnescapeDataString(part[..eq]).Trim().ToLowerInvariant();
            string val = Uri.UnescapeDataString(part[(eq + 1)..]).Trim().TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(key))
                result[key] = val;
        }
    }

    static void MergePayload(NameValueCollection query)
    {
        string? payload = query["payload"];
        if (string.IsNullOrWhiteSpace(payload))
            return;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                string key = prop.Name.ToLowerInvariant();
                string value = JsonValueToString(prop.Value);

                if (query[key] == null)
                    query[key] = value;
            }
        }
        catch (Exception ex)
        {
            Log($"payload 解析失败: {ex.Message}");
        }
    }

    static string JsonValueToString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Array => string.Join(",", value.EnumerateArray().Select(JsonValueToString).Where(v => !string.IsNullOrWhiteSpace(v))),
            _ => value.GetRawText()
        };
    }

    static void RunFromUri(NameValueCollection query, string protocolName)
    {
        string action = query["action"] ?? "run";
        if (!action.Equals("run", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"未知 action: {action}");
            return;
        }

        string runId = First(query, "runid", "run_id") ?? "";
        try
        {
            var pars = BuildParams(query, protocolName);
            Log($"准备执行: {DescribeParams(pars)}");
            if (!string.IsNullOrWhiteSpace(pars.RunId))
                MarkRunStarted(pars.RunId);

            var result = LaunchSapGuiAndExecute(pars);
            if (!string.IsNullOrWhiteSpace(pars.RunId))
                CompleteRun(pars.RunId, result);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"执行请求失败: {ex.Message}");
            Log($"执行请求失败: {ex}");
            if (!string.IsNullOrWhiteSpace(runId))
                CompleteRun(runId, FailedRunResult(ex.Message, DateTime.UtcNow));
        }
    }

    static SapLocalConfig LoadLocalConfig()
    {
        try
        {
            if (!File.Exists(ConfigFilePath))
                return new SapLocalConfig();

            string json = File.ReadAllText(ConfigFilePath, Encoding.UTF8);
            var config = JsonSerializer.Deserialize<SapLocalConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            }) ?? new SapLocalConfig();

            config.Password = ResolveLocalPassword(config);
            return config;
        }
        catch (Exception ex)
        {
            Log($"读取本机配置失败: {ConfigFilePath}, {ex.Message}");
            return new SapLocalConfig();
        }
    }

    static string ResolveLocalPassword(SapLocalConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.PasswordProtected))
        {
            try
            {
                byte[] protectedBytes = Convert.FromBase64String(config.PasswordProtected);
                byte[] plainBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch (Exception ex)
            {
                Log($"本机密码解密失败。请重新运行 04_配置SAP登录信息.bat。{ex.Message}");
                return "";
            }
        }

        // 兼容旧版明文配置。重新运行配置脚本后会迁移到 passwordProtected。
        return config.Password ?? "";
    }

    static SapRunParams ApplyLocalConfig(SapRunParams p)
    {
        var local = LoadLocalConfig();
        p.System = FirstNonEmpty(local.System ?? "", p.System);
        p.Client = FirstNonEmpty(local.Client ?? "", p.Client);
        p.User = FirstNonEmpty(local.User ?? "", p.User);
        p.Password = FirstNonEmpty(local.Password ?? "", p.Password);
        p.Language = FirstNonEmpty(local.Language ?? "", p.Language, "ZH");
        p.SysNr = FirstNonEmpty(local.SysNr ?? "", p.SysNr);
        ValidateLoginConfig(p);
        return p;
    }

    static void ValidateLoginConfig(SapRunParams p)
    {
        if (!string.IsNullOrWhiteSpace(p.System) &&
            !string.IsNullOrWhiteSpace(p.Client) &&
            !string.IsNullOrWhiteSpace(p.User) &&
            !string.IsNullOrWhiteSpace(p.Password))
            return;

        string message = $"SAP 登录配置不完整。请先运行上线安装包里的 04_配置SAP登录信息.bat，维护 system/client/user/password，或手工维护 {ConfigFilePath}";
        Console.Error.WriteLine(message);
        Log(message);
        throw new InvalidOperationException(message);
    }

    static SapRunParams BuildParams(NameValueCollection query, string protocolName)
    {
        return BuildParams(query, protocolName, LoadLocalConfig());
    }

    static SapRunParams BuildParams(NameValueCollection query, string protocolName, SapLocalConfig local)
    {
        string tcode = First(query, "tcode", "t-code", "transaction", "transactioncode") ?? "ZFI019NL";
        string script = First(query, "script", "scriptmode", "mode") ?? DefaultScriptForTCode(tcode);

        var p = new SapRunParams
        {
            System = FirstNonEmpty(local.System ?? "", First(query, "system", "sys") ?? ""),
            Client = FirstNonEmpty(local.Client ?? "", First(query, "client", "cli") ?? ""),
            User = FirstNonEmpty(local.User ?? "", First(query, "user", "usr") ?? ""),
            Password = FirstNonEmpty(local.Password ?? "", First(query, "pw", "password") ?? ""),
            Language = FirstNonEmpty(local.Language ?? "", First(query, "lang", "language") ?? "", "ZH"),
            SysNr = FirstNonEmpty(local.SysNr ?? "", First(query, "sysnr") ?? ""),
            TCode = SanitizeTCode(tcode),
            Script = script,
            Plant = First(query, "plant", "werks") ?? "",
            Plants = First(query, "plants", "werkslist", "plantlist") ?? "",
            Year = First(query, "year", "gjahr") ?? "",
            Week = First(query, "week", "weekno", "wk") ?? "",
            Period = First(query, "period", "periodtext") ?? "",
            BusinessArea = First(query, "businessarea", "gsber") ?? "",
            BusinessAreas = First(query, "businessareas", "gsberlist", "businessarealist") ?? "",
            WeekEnd = First(query, "weekend", "date") ?? "",
            FactoryGroup = First(query, "factorygroup", "plantgroup") ?? "",
            RunStrategy = First(query, "runstrategy", "strategy") ?? "",
            Field1Name = First(query, "field1", "field1name") ?? "",
            Field1Value = First(query, "value1", "field1value") ?? "",
            Field2Name = First(query, "field2", "field2name") ?? "",
            Field2Value = First(query, "value2", "field2value") ?? "",
            ButtonId = First(query, "button", "buttonid") ?? "",
            RunId = First(query, "runid", "run_id") ?? ""
        };

        ApplyScriptDefaults(p);
        NormalizeBatchParams(p);
        ApplyTransactionConfigForRun(p);
        ValidateLoginConfig(p);
        return p;
    }

    static void NormalizeBatchParams(SapRunParams p)
    {
        p.Plants = NormalizeCsv(FirstNonEmpty(p.Plants, p.Plant));
        p.BusinessAreas = NormalizeCsv(FirstNonEmpty(p.BusinessAreas, p.BusinessArea));
        p.Plant = FirstCsvValue(p.Plants);
        p.BusinessArea = FirstCsvValue(p.BusinessAreas);
    }

    static string NormalizeCsv(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        return string.Join(",",
            value.Split(new[] { ',', ';', '|', '，', '；', '、', '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Trim())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    static string NormalizeCsvPreserveOrder(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        return string.Join(",",
            value.Split(new[] { ',', ';', '|', '，', '；', '、', '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Trim())
                .Where(v => !string.IsNullOrWhiteSpace(v)));
    }

    static string[] NormalizeStringArray(string value)
    {
        return NormalizeCsvPreserveOrder(value)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => v.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    static string FirstCsvValue(string value)
    {
        return NormalizeCsv(value).Split(',', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
    }

    static void ApplyTransactionConfigForRun(SapRunParams p)
    {
        if (string.IsNullOrWhiteSpace(p.TCode))
            return;

        InitializeDatabase(seedFromScripts: true);
        using var connection = OpenDatabaseConnection();
        string scriptFile = "";
        string automation = "";
        string defaultGroup = "";

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
SELECT script_file, automation, default_group
FROM transactions
WHERE tcode=$tcode AND enabled=1;
""";
            command.Parameters.AddWithValue("$tcode", p.TCode.ToUpperInvariant());
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                scriptFile = reader.GetString(0);
                automation = reader.GetString(1);
                defaultGroup = reader.GetString(2);
            }
        }

        bool mustRunScript =
            automation.Equals("script", StringComparison.OrdinalIgnoreCase) ||
            p.TCode.Equals("ZFI072A", StringComparison.OrdinalIgnoreCase);

        if (mustRunScript && IsOpenOnlyScript(p.Script))
            p.Script = FirstNonEmpty(scriptFile, DefaultScriptForTCode(p.TCode));

        if (p.TCode.Equals("ZFI072A", StringComparison.OrdinalIgnoreCase))
        {
            if (IsOpenOnlyScript(p.Script))
                p.Script = "ZFI072A.vbs";

            if (string.IsNullOrWhiteSpace(p.FactoryGroup))
                p.FactoryGroup = defaultGroup;
        }
    }

    static bool IsOpenOnlyScript(string script)
    {
        return string.IsNullOrWhiteSpace(script) ||
               script.Equals("openOnly", StringComparison.OrdinalIgnoreCase);
    }

    static string? First(NameValueCollection query, params string[] keys)
    {
        foreach (string key in keys)
        {
            string? value = query[key.ToLowerInvariant()];
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    static string SanitizeTCode(string tcode)
    {
        string value = tcode.Trim();
        if (!Regex.IsMatch(value, @"^[A-Za-z0-9_/\.-]{1,32}$"))
            throw new ArgumentException($"事务码不合法: {value}");
        return value;
    }

    static string SanitizePlantCode(string code)
    {
        string value = (code ?? "").Trim().ToUpperInvariant();
        if (!Regex.IsMatch(value, @"^[A-Z0-9_.-]{1,16}$"))
            throw new ArgumentException($"plant code is invalid: {value}");
        return value;
    }

    static string SanitizeConfigId(string id, string label)
    {
        string value = (id ?? "").Trim().ToUpperInvariant();
        if (!Regex.IsMatch(value, @"^[A-Z0-9_.-]{1,64}$"))
            throw new ArgumentException($"{label} is invalid: {value}");
        return value;
    }

    static void ApplyScriptDefaults(SapRunParams p)
    {
        if (p.Script.Equals("zck", StringComparison.OrdinalIgnoreCase) ||
            p.TCode.Equals("zck", StringComparison.OrdinalIgnoreCase))
        {
            p.Script = "zck";
            p.Field1Name = string.IsNullOrWhiteSpace(p.Field1Name) ? "txtS_NAME-LOW" : p.Field1Name;
            p.Field1Value = string.IsNullOrWhiteSpace(p.Field1Value) ? "z*" : p.Field1Value;
            p.CaretPos = "2";
            p.ButtonId = string.IsNullOrWhiteSpace(p.ButtonId) ? "8" : p.ButtonId;
            return;
        }

        p.CaretPos = FirstNonEmpty(p.CaretPos, "0");
    }

    static string DefaultScriptForTCode(string tcode)
    {
        if (tcode.Equals("zck", StringComparison.OrdinalIgnoreCase))
            return "zck";

        return $"{SanitizeTCode(tcode).ToUpperInvariant()}.vbs";
    }

    static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
    }

    static int FirstPresent(int? first, int? second, int fallback)
    {
        return first ?? second ?? fallback;
    }

    static bool HasPresent(params int?[] values)
    {
        return values.Any(v => v.HasValue);
    }

    static int NormalizeNonNegative(int value)
    {
        return Math.Max(0, value);
    }

    static void RunBridgeServer()
    {
        InitializeDatabase(seedFromScripts: true);

        using var listener = new HttpListener();
        string prefix = GetApiPrefix();
        listener.Prefixes.Add(prefix);
        listener.Start();
        Log($"Bridge API 已启动: {prefix}");
        Console.WriteLine($"Bridge API running: {prefix}");

        if (IsQueueDisabled())
        {
            Log("串行执行队列已通过 SAP_RPA_DISABLE_QUEUE=1 禁用");
            Console.WriteLine("Queue worker disabled by SAP_RPA_DISABLE_QUEUE=1");
        }
        else
        {
            var worker = new Thread(ProcessRunQueueLoop)
            {
                IsBackground = true,
                Name = "SapRpaSerialQueueWorker"
            };
            worker.Start();
            Log("串行执行队列后台线程已启动");
        }

        while (true)
        {
            var context = listener.GetContext();
            ThreadPool.QueueUserWorkItem(_ => HandleBridgeRequest(context));
        }
    }

    static void HandleBridgeRequest(HttpListenerContext context)
    {
        try
        {
            AddCorsHeaders(context.Response);
            if (context.Request.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 204;
                context.Response.Close();
                return;
            }

            string path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            if (path.Equals("", StringComparison.OrdinalIgnoreCase))
                path = "/";

            if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                path.Equals("/api/health", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context.Response, new
                {
                    ok = true,
                    app = "SapWebLauncher Bridge",
                    version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "",
                    runtimeRoot = RuntimeRoot,
                    database = DatabaseFilePath,
                    logFile = LogFilePath,
                    outputRoot = OutputDirectory,
                    transactionRoot = RuntimeTransactionsDirectory,
                    credentialConfig = ConfigFilePath,
                    executor = ExecutorId,
                    queueMode = IsQueueDisabled() ? "disabled" : "serial",
                    time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                });
                return;
            }

            if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                path.Equals("/api/transactions", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context.Response, LoadTransactionsFromDatabase());
                return;
            }

            if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                path.Equals("/api/schema", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context.Response, LoadDatabaseSchema());
                return;
            }

            if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                path.Equals("/api/reports/execution", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context.Response, LoadExecutionReport(context.Request));
                return;
            }

            Match tablePreviewMatch = Regex.Match(path, @"^/api/schema/tables/([A-Za-z0-9_]+)$", RegexOptions.IgnoreCase);
            if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && tablePreviewMatch.Success)
            {
                WriteJson(context.Response, LoadTablePreview(tablePreviewMatch.Groups[1].Value, context.Request));
                return;
            }

            if (context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                path.Equals("/api/transactions", StringComparison.OrdinalIgnoreCase))
            {
                var item = ReadJson<TransactionConfigRequest>(context.Request);
                string code = UpsertTransaction(item, routeCode: "");
                WriteJson(context.Response, new { ok = true, code });
                return;
            }

            Match transactionMatch = Regex.Match(path, @"^/api/transactions/([A-Za-z0-9_./-]+)$", RegexOptions.IgnoreCase);
            if (transactionMatch.Success &&
                context.Request.HttpMethod.Equals("PUT", StringComparison.OrdinalIgnoreCase))
            {
                var item = ReadJson<TransactionConfigRequest>(context.Request);
                string code = UpsertTransaction(item, transactionMatch.Groups[1].Value);
                WriteJson(context.Response, new { ok = true, code });
                return;
            }

            if (transactionMatch.Success &&
                context.Request.HttpMethod.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                string tcode = SanitizeTCode(transactionMatch.Groups[1].Value).ToUpperInvariant();
                SetTransactionEnabled(tcode, enabled: false);
                WriteJson(context.Response, new { ok = true, code = tcode, enabled = false });
                return;
            }

            if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                path.Equals("/api/config", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context.Response, LoadBasicConfig());
                return;
            }

            Match plantMatch = Regex.Match(path, @"^/api/config/plants/([A-Za-z0-9_.-]+)$", RegexOptions.IgnoreCase);
            if (plantMatch.Success &&
                context.Request.HttpMethod.Equals("PUT", StringComparison.OrdinalIgnoreCase))
            {
                var item = ReadJson<PlantConfigRequest>(context.Request);
                string code = UpsertPlant(item, plantMatch.Groups[1].Value);
                WriteJson(context.Response, new { ok = true, code });
                return;
            }

            if (plantMatch.Success &&
                context.Request.HttpMethod.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                string code = SanitizePlantCode(plantMatch.Groups[1].Value);
                SetPlantEnabled(code, enabled: false);
                WriteJson(context.Response, new { ok = true, code, enabled = false });
                return;
            }

            Match plantGroupMatch = Regex.Match(path, @"^/api/config/plant-groups/([A-Za-z0-9_.-]+)$", RegexOptions.IgnoreCase);
            if (plantGroupMatch.Success &&
                context.Request.HttpMethod.Equals("PUT", StringComparison.OrdinalIgnoreCase))
            {
                var item = ReadJson<PlantGroupConfigRequest>(context.Request);
                string id = UpsertPlantGroup(item, plantGroupMatch.Groups[1].Value);
                WriteJson(context.Response, new { ok = true, id });
                return;
            }

            if (plantGroupMatch.Success &&
                context.Request.HttpMethod.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                string id = SanitizeConfigId(plantGroupMatch.Groups[1].Value, "plant group id");
                SetPlantGroupEnabled(id, enabled: false);
                WriteJson(context.Response, new { ok = true, id, enabled = false });
                return;
            }

            Match transactionRuleMatch = Regex.Match(path, @"^/api/config/transaction-rules/([A-Za-z0-9_./-]+)$", RegexOptions.IgnoreCase);
            if (transactionRuleMatch.Success &&
                context.Request.HttpMethod.Equals("PUT", StringComparison.OrdinalIgnoreCase))
            {
                var item = ReadJson<TransactionPlantRuleRequest>(context.Request);
                string tcode = UpsertTransactionPlantRule(item, transactionRuleMatch.Groups[1].Value);
                WriteJson(context.Response, new { ok = true, tcode, code = tcode });
                return;
            }

            if (transactionRuleMatch.Success &&
                context.Request.HttpMethod.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                string tcode = SanitizeTCode(transactionRuleMatch.Groups[1].Value).ToUpperInvariant();
                SetTransactionPlantRuleEnabled(tcode, enabled: false);
                WriteJson(context.Response, new { ok = true, tcode, code = tcode, enabled = false });
                return;
            }

            Match notificationRobotMatch = Regex.Match(path, @"^/api/config/notification-robots/([A-Za-z0-9_.-]+)$", RegexOptions.IgnoreCase);
            if (notificationRobotMatch.Success &&
                context.Request.HttpMethod.Equals("PUT", StringComparison.OrdinalIgnoreCase))
            {
                var item = ReadJson<NotificationRobotConfigRequest>(context.Request);
                string id = UpsertNotificationRobot(item, notificationRobotMatch.Groups[1].Value);
                WriteJson(context.Response, new { ok = true, id });
                return;
            }

            if (notificationRobotMatch.Success &&
                context.Request.HttpMethod.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                string id = SanitizeConfigId(notificationRobotMatch.Groups[1].Value, "notification robot id");
                SetNotificationRobotEnabled(id, enabled: false);
                WriteJson(context.Response, new { ok = true, id, enabled = false });
                return;
            }

            if (path.Equals("/api/schedules", StringComparison.OrdinalIgnoreCase) &&
                context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context.Response, LoadScheduleTasks());
                return;
            }

            if (path.Equals("/api/schedules", StringComparison.OrdinalIgnoreCase) &&
                context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                var item = ReadJson<ScheduleTaskRequest>(context.Request);
                string id = UpsertScheduleTask(item, routeId: "");
                WriteJson(context.Response, new { ok = true, id });
                return;
            }

            Match scheduleMatch = Regex.Match(path, @"^/api/schedules/([A-Za-z0-9_.-]+)$", RegexOptions.IgnoreCase);
            if (scheduleMatch.Success &&
                context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                var item = LoadScheduleTask(scheduleMatch.Groups[1].Value);
                if (item == null)
                {
                    WriteJson(context.Response, new { error = $"schedule not found: {scheduleMatch.Groups[1].Value}" }, 404);
                    return;
                }

                WriteJson(context.Response, item);
                return;
            }

            if (scheduleMatch.Success &&
                context.Request.HttpMethod.Equals("PUT", StringComparison.OrdinalIgnoreCase))
            {
                var item = ReadJson<ScheduleTaskRequest>(context.Request);
                string id = UpsertScheduleTask(item, scheduleMatch.Groups[1].Value);
                WriteJson(context.Response, new { ok = true, id });
                return;
            }

            if (scheduleMatch.Success &&
                context.Request.HttpMethod.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                string id = SanitizeConfigId(scheduleMatch.Groups[1].Value, "schedule id");
                SetScheduleTaskEnabled(id, enabled: false);
                WriteJson(context.Response, new { ok = true, id, enabled = false });
                return;
            }

            if (path.Equals("/api/runs", StringComparison.OrdinalIgnoreCase) &&
                context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                var request = ReadJson<CreateRunRequest>(context.Request);
                var run = CreateRun(request);
                WriteJson(context.Response, new { runId = run.RunId, status = run.Status, queuedAt = run.QueuedAt });
                return;
            }

            if (path.Equals("/api/runs", StringComparison.OrdinalIgnoreCase) &&
                context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context.Response, LoadRuns(context.Request));
                return;
            }

            Match runResultMatch = Regex.Match(path, @"^/api/runs/([A-Za-z0-9_.-]+)/result$", RegexOptions.IgnoreCase);
            if (runResultMatch.Success &&
                context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                var result = ReadJson<RunResultRequest>(context.Request);
                string runId = runResultMatch.Groups[1].Value;
                CompleteRun(runId, result);
                WriteJson(context.Response, new { ok = true, runId });
                return;
            }

            Match runMatch = Regex.Match(path, @"^/api/runs/([A-Za-z0-9_.-]+)$", RegexOptions.IgnoreCase);
            if (runMatch.Success &&
                context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                var run = LoadRun(runMatch.Groups[1].Value, includeDetails: true);
                if (run == null)
                {
                    WriteJson(context.Response, new { error = $"run not found: {runMatch.Groups[1].Value}" }, 404);
                    return;
                }

                WriteJson(context.Response, run);
                return;
            }

            Match metadataMatch = Regex.Match(path, @"^/api/scripts/([A-Za-z0-9_.-]+)/metadata$", RegexOptions.IgnoreCase);
            if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && metadataMatch.Success)
            {
                string tcode = SanitizeTCode(metadataMatch.Groups[1].Value).ToUpperInvariant();
                var metadata = LoadScriptMetadataFromDatabase(tcode);
                if (metadata == null)
                {
                    WriteJson(context.Response, new { error = $"metadata not found: {tcode}" }, 404);
                    return;
                }

                WriteJson(context.Response, metadata);
                return;
            }

            WriteJson(context.Response, new { error = $"not found: {path}" }, 404);
        }
        catch (Exception ex)
        {
            Log($"Bridge API 请求失败: {ex}");
            try
            {
                WriteJson(context.Response, new { error = ex.Message }, 500);
            }
            catch
            {
                try { context.Response.Close(); } catch { }
            }
        }
    }

    static void AddCorsHeaders(HttpListenerResponse response)
    {
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.Headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, DELETE, OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
        response.Headers["Access-Control-Allow-Private-Network"] = "true";
    }

    static T ReadJson<T>(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
        string json = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("Request body is empty.");

        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonOptions)
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }) ?? throw new InvalidOperationException("Invalid JSON request body.");
    }

    static void WriteJson(HttpListenerResponse response, object value, int statusCode = 200)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions));
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes, 0, bytes.Length);
        response.Close();
    }

    static void InitializeDatabase(bool seedFromScripts)
    {
        lock (DatabaseInitLock)
        {
            EnsureRuntimeDirectories();
            MigrateLegacyDatabaseIfNeeded();
            using var connection = OpenDatabaseConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
CREATE TABLE IF NOT EXISTS schema_migrations (
    version INTEGER PRIMARY KEY,
    applied_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS transactions (
    tcode TEXT PRIMARY KEY,
    name TEXT NOT NULL DEFAULT '',
    stage TEXT NOT NULL DEFAULT '',
    script_file TEXT NOT NULL DEFAULT '',
    icon TEXT NOT NULL DEFAULT '',
    params_json TEXT NOT NULL DEFAULT '[]',
    factory_rule TEXT NOT NULL DEFAULT '',
    fixed_plants_json TEXT NOT NULL DEFAULT '[]',
    default_group TEXT NOT NULL DEFAULT '',
    automation TEXT NOT NULL DEFAULT '',
    timeout_seconds INTEGER NOT NULL DEFAULT 0,
    retry_count INTEGER NOT NULL DEFAULT 0,
    script_version TEXT NOT NULL DEFAULT '',
    script_hash TEXT NOT NULL DEFAULT '',
    script_metadata_json TEXT NOT NULL DEFAULT '{}',
    enabled INTEGER NOT NULL DEFAULT 1,
    updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS run_logs (
    run_id TEXT PRIMARY KEY,
    tcode TEXT NOT NULL,
    status TEXT NOT NULL,
    started_at TEXT NOT NULL,
    finished_at TEXT NOT NULL DEFAULT '',
    duration_ms INTEGER NOT NULL DEFAULT 0,
    message TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS script_cache (
    tcode TEXT PRIMARY KEY,
    script_file TEXT NOT NULL,
    script_hash TEXT NOT NULL DEFAULT '',
    script_text TEXT NOT NULL DEFAULT '',
    cached_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS runs (
    run_id TEXT PRIMARY KEY,
    transaction_code TEXT NOT NULL,
    operator_id TEXT NOT NULL DEFAULT '',
    operator_name TEXT NOT NULL DEFAULT '',
    operator_dept TEXT NOT NULL DEFAULT '',
    status TEXT NOT NULL DEFAULT 'queued',
    request_json TEXT NOT NULL DEFAULT '{}',
    sap_status_type TEXT NOT NULL DEFAULT '',
    sap_status_text TEXT NOT NULL DEFAULT '',
    message TEXT NOT NULL DEFAULT '',
    script_file TEXT NOT NULL DEFAULT '',
    script_hash TEXT NOT NULL DEFAULT '',
    source TEXT NOT NULL DEFAULT '',
    notify_target TEXT NOT NULL DEFAULT '',
    priority INTEGER NOT NULL DEFAULT 0,
    attempt INTEGER NOT NULL DEFAULT 0,
    max_attempts INTEGER NOT NULL DEFAULT 1,
    locked_by TEXT NOT NULL DEFAULT '',
    locked_at TEXT NOT NULL DEFAULT '',
    queued_at TEXT NOT NULL DEFAULT '',
    started_at TEXT NOT NULL DEFAULT '',
    finished_at TEXT NOT NULL DEFAULT '',
    duration_ms INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_runs_status_queued_at ON runs(status, queued_at);
CREATE INDEX IF NOT EXISTS idx_runs_transaction_finished ON runs(transaction_code, finished_at);
CREATE TABLE IF NOT EXISTS run_params (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id TEXT NOT NULL,
    param_key TEXT NOT NULL,
    param_value TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL DEFAULT (datetime('now', 'localtime')),
    UNIQUE(run_id, param_key)
);
CREATE INDEX IF NOT EXISTS idx_run_params_run_id ON run_params(run_id, id);
CREATE TABLE IF NOT EXISTS run_result_logs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id TEXT NOT NULL,
    level TEXT NOT NULL DEFAULT 'INFO',
    message TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT (datetime('now', 'localtime'))
);
CREATE INDEX IF NOT EXISTS idx_run_result_logs_run_id ON run_result_logs(run_id, id);
CREATE TABLE IF NOT EXISTS run_files (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id TEXT NOT NULL,
    file_type TEXT NOT NULL DEFAULT 'output',
    file_name TEXT NOT NULL,
    file_path TEXT NOT NULL,
    file_size INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now', 'localtime'))
);
CREATE INDEX IF NOT EXISTS idx_run_files_run_id ON run_files(run_id, id);
CREATE TABLE IF NOT EXISTS app_settings (
    setting_key TEXT PRIMARY KEY,
    setting_value TEXT NOT NULL DEFAULT '',
    updated_at TEXT NOT NULL DEFAULT (datetime('now', 'localtime'))
);
CREATE TABLE IF NOT EXISTS plants (
    code TEXT PRIMARY KEY,
    name TEXT NOT NULL DEFAULT '',
    business_area TEXT NOT NULL DEFAULT '',
    enabled INTEGER NOT NULL DEFAULT 1,
    sort_order INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now', 'localtime')),
    updated_at TEXT NOT NULL DEFAULT (datetime('now', 'localtime')),
    created_by TEXT NOT NULL DEFAULT '',
    updated_by TEXT NOT NULL DEFAULT ''
);
CREATE TABLE IF NOT EXISTS plant_groups (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL DEFAULT '',
    short_name TEXT NOT NULL DEFAULT '',
    description TEXT NOT NULL DEFAULT '',
    zfi019nl_areas_json TEXT NOT NULL DEFAULT '[]',
    zfi080_areas_json TEXT NOT NULL DEFAULT '[]',
    zfi072_plants_json TEXT NOT NULL DEFAULT '[]',
    zco019_plants_json TEXT NOT NULL DEFAULT '[]',
    enabled INTEGER NOT NULL DEFAULT 1,
    sort_order INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now', 'localtime')),
    updated_at TEXT NOT NULL DEFAULT (datetime('now', 'localtime')),
    created_by TEXT NOT NULL DEFAULT '',
    updated_by TEXT NOT NULL DEFAULT ''
);
CREATE TABLE IF NOT EXISTS plant_group_members (
    group_id TEXT NOT NULL,
    plant_code TEXT NOT NULL,
    sort_order INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now', 'localtime')),
    PRIMARY KEY(group_id, plant_code)
);
CREATE INDEX IF NOT EXISTS idx_plant_group_members_plant ON plant_group_members(plant_code);
CREATE TABLE IF NOT EXISTS transaction_plant_rules (
    tcode TEXT PRIMARY KEY,
    factory_rule TEXT NOT NULL DEFAULT '',
    default_group TEXT NOT NULL DEFAULT '',
    fixed_plants_json TEXT NOT NULL DEFAULT '[]',
    selectable_group_ids_json TEXT NOT NULL DEFAULT '[]',
    business_area_mode TEXT NOT NULL DEFAULT 'byPlant',
    business_areas_json TEXT NOT NULL DEFAULT '[]',
    enabled INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL DEFAULT (datetime('now', 'localtime')),
    updated_at TEXT NOT NULL DEFAULT (datetime('now', 'localtime')),
    created_by TEXT NOT NULL DEFAULT '',
    updated_by TEXT NOT NULL DEFAULT ''
);
CREATE TABLE IF NOT EXISTS notification_robots (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL DEFAULT '',
    robot_type TEXT NOT NULL DEFAULT 'dingtalk',
    target_label TEXT NOT NULL DEFAULT '',
    webhook_protected TEXT NOT NULL DEFAULT '',
    secret_protected TEXT NOT NULL DEFAULT '',
    enabled INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL DEFAULT (datetime('now', 'localtime')),
    updated_at TEXT NOT NULL DEFAULT (datetime('now', 'localtime')),
    created_by TEXT NOT NULL DEFAULT '',
    updated_by TEXT NOT NULL DEFAULT ''
);
CREATE TABLE IF NOT EXISTS notification_robot_bindings (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    robot_id TEXT NOT NULL,
    event_name TEXT NOT NULL DEFAULT '',
    tcode TEXT NOT NULL DEFAULT '',
    plant_group_id TEXT NOT NULL DEFAULT '',
    enabled INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL DEFAULT (datetime('now', 'localtime')),
    updated_at TEXT NOT NULL DEFAULT (datetime('now', 'localtime')),
    UNIQUE(robot_id, event_name, tcode, plant_group_id)
);
CREATE INDEX IF NOT EXISTS idx_notification_robot_bindings_robot ON notification_robot_bindings(robot_id);
CREATE TABLE IF NOT EXISTS schedule_tasks (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL DEFAULT '',
    tcode TEXT NOT NULL DEFAULT '',
    plants_json TEXT NOT NULL DEFAULT '[]',
    default_business_scope TEXT NOT NULL DEFAULT '',
    cron TEXT NOT NULL DEFAULT '',
    frequency TEXT NOT NULL DEFAULT '',
    run_time TEXT NOT NULL DEFAULT '',
    enabled INTEGER NOT NULL DEFAULT 1,
    notify_enabled INTEGER NOT NULL DEFAULT 0,
    notify_on_success INTEGER NOT NULL DEFAULT 0,
    notify_on_failure INTEGER NOT NULL DEFAULT 1,
    notify_target TEXT NOT NULL DEFAULT '',
    params_json TEXT NOT NULL DEFAULT '{}',
    created_at TEXT NOT NULL DEFAULT (datetime('now', 'localtime')),
    updated_at TEXT NOT NULL DEFAULT (datetime('now', 'localtime')),
    created_by TEXT NOT NULL DEFAULT '',
    updated_by TEXT NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS idx_schedule_tasks_enabled_time ON schedule_tasks(enabled, frequency, run_time);
CREATE INDEX IF NOT EXISTS idx_schedule_tasks_tcode ON schedule_tasks(tcode);
CREATE TABLE IF NOT EXISTS schedule_task_runs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    task_id TEXT NOT NULL,
    run_id TEXT NOT NULL DEFAULT '',
    trigger_type TEXT NOT NULL DEFAULT '',
    scheduled_at TEXT NOT NULL DEFAULT '',
    triggered_at TEXT NOT NULL DEFAULT '',
    status TEXT NOT NULL DEFAULT '',
    message TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL DEFAULT (datetime('now', 'localtime'))
);
CREATE INDEX IF NOT EXISTS idx_schedule_task_runs_task_time ON schedule_task_runs(task_id, triggered_at);
CREATE INDEX IF NOT EXISTS idx_schedule_task_runs_run_id ON schedule_task_runs(run_id);
INSERT OR IGNORE INTO schema_migrations(version, applied_at) VALUES(1, datetime('now'));
INSERT OR IGNORE INTO schema_migrations(version, applied_at) VALUES(2, datetime('now'));
INSERT OR IGNORE INTO schema_migrations(version, applied_at) VALUES(3, datetime('now'));
INSERT OR IGNORE INTO app_settings(setting_key, setting_value)
VALUES
    ('sap_password_storage', 'DPAPI_CURRENT_USER'),
    ('queue_mode', 'serial');
""";
            command.ExecuteNonQuery();

            UpsertAppSetting(connection, "runtime_root", RuntimeRoot);
            UpsertAppSetting(connection, "script_root", RuntimeTransactionsDirectory);
            UpsertAppSetting(connection, "output_root", OutputDirectory);
            UpsertAppSetting(connection, "database_path", DatabaseFilePath);
            UpsertAppSetting(connection, "credential_config_path", ConfigFilePath);

            EnsureColumn(connection, "runs", "source", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, "runs", "notify_target", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, "runs", "priority", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, "runs", "attempt", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, "runs", "max_attempts", "INTEGER NOT NULL DEFAULT 1");
            EnsureColumn(connection, "runs", "locked_by", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, "runs", "locked_at", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, "transactions", "timeout_seconds", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, "transactions", "retry_count", "INTEGER NOT NULL DEFAULT 0");
            EnsureScheduleColumns(connection);
            EnsureBasicConfigColumns(connection);

            if (seedFromScripts)
            {
                if (CountTransactions(connection) == 0)
                    SeedTransactions(connection);

                SeedBasicConfig(connection);
            }

            Log($"SQLite 数据库初始化完成: {DatabaseFilePath}");
        }
    }

    static SqliteConnection OpenDatabaseConnection()
    {
        EnsureRuntimeDirectories();
        var connection = new SqliteConnection($"Data Source={DatabaseFilePath}");
        connection.Open();
        return connection;
    }

    static void UpsertAppSetting(SqliteConnection connection, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO app_settings(setting_key, setting_value, updated_at)
VALUES($key, $value, datetime('now', 'localtime'))
ON CONFLICT(setting_key) DO UPDATE SET
    setting_value=excluded.setting_value,
    updated_at=excluded.updated_at;
""";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    static string GetApiPrefix()
    {
        string value = Environment.GetEnvironmentVariable("SAP_RPA_API_PREFIX") ?? "";
        if (string.IsNullOrWhiteSpace(value))
            value = $"http://127.0.0.1:{BridgePort}/";

        value = value.Replace("://0.0.0.0:", "://+:", StringComparison.OrdinalIgnoreCase);

        return value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/";
    }

    static bool IsQueueDisabled()
    {
        string value = Environment.GetEnvironmentVariable("SAP_RPA_DISABLE_QUEUE") ?? "";
        return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    static void EnsureColumn(SqliteConnection connection, string tableName, string columnName, string definition)
    {
        using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({tableName})";
        using (var reader = check.ExecuteReader())
        {
            while (reader.Read())
            {
                if (reader.GetString(1).Equals(columnName, StringComparison.OrdinalIgnoreCase))
                    return;
            }
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition}";
        alter.ExecuteNonQuery();
    }

    static long CountTransactions(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM transactions";
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    static void EnsureScheduleColumns(SqliteConnection connection)
    {
        EnsureColumn(connection, "schedule_tasks", "name", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "schedule_tasks", "tcode", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "schedule_tasks", "plants_json", "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumn(connection, "schedule_tasks", "default_business_scope", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "schedule_tasks", "cron", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "schedule_tasks", "frequency", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "schedule_tasks", "run_time", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "schedule_tasks", "enabled", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(connection, "schedule_tasks", "notify_enabled", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "schedule_tasks", "notify_on_success", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "schedule_tasks", "notify_on_failure", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(connection, "schedule_tasks", "notify_target", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "schedule_tasks", "params_json", "TEXT NOT NULL DEFAULT '{}'");
        EnsureColumn(connection, "schedule_tasks", "created_at", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "schedule_tasks", "updated_at", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "schedule_tasks", "created_by", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "schedule_tasks", "updated_by", "TEXT NOT NULL DEFAULT ''");

        EnsureColumn(connection, "schedule_task_runs", "task_id", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "schedule_task_runs", "run_id", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "schedule_task_runs", "trigger_type", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "schedule_task_runs", "scheduled_at", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "schedule_task_runs", "triggered_at", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "schedule_task_runs", "status", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "schedule_task_runs", "message", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "schedule_task_runs", "created_at", "TEXT NOT NULL DEFAULT ''");
    }

    static void EnsureBasicConfigColumns(SqliteConnection connection)
    {
        EnsureColumn(connection, "plants", "business_area", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "plants", "enabled", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(connection, "plants", "sort_order", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "plants", "created_at", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "plants", "updated_at", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "plants", "created_by", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "plants", "updated_by", "TEXT NOT NULL DEFAULT ''");

        EnsureColumn(connection, "plant_groups", "short_name", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "plant_groups", "description", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "plant_groups", "zfi019nl_areas_json", "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumn(connection, "plant_groups", "zfi080_areas_json", "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumn(connection, "plant_groups", "zfi072_plants_json", "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumn(connection, "plant_groups", "zco019_plants_json", "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumn(connection, "plant_groups", "enabled", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(connection, "plant_groups", "sort_order", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "plant_groups", "created_at", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "plant_groups", "updated_at", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "plant_groups", "created_by", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "plant_groups", "updated_by", "TEXT NOT NULL DEFAULT ''");

        EnsureColumn(connection, "plant_group_members", "sort_order", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "plant_group_members", "created_at", "TEXT NOT NULL DEFAULT ''");

        EnsureColumn(connection, "transaction_plant_rules", "factory_rule", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "transaction_plant_rules", "default_group", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "transaction_plant_rules", "fixed_plants_json", "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumn(connection, "transaction_plant_rules", "selectable_group_ids_json", "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumn(connection, "transaction_plant_rules", "business_area_mode", "TEXT NOT NULL DEFAULT 'byPlant'");
        EnsureColumn(connection, "transaction_plant_rules", "business_areas_json", "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumn(connection, "transaction_plant_rules", "enabled", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(connection, "transaction_plant_rules", "created_at", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "transaction_plant_rules", "updated_at", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "transaction_plant_rules", "created_by", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "transaction_plant_rules", "updated_by", "TEXT NOT NULL DEFAULT ''");

        EnsureColumn(connection, "notification_robots", "robot_type", "TEXT NOT NULL DEFAULT 'dingtalk'");
        EnsureColumn(connection, "notification_robots", "target_label", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "notification_robots", "webhook_protected", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "notification_robots", "secret_protected", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "notification_robots", "enabled", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(connection, "notification_robots", "created_at", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "notification_robots", "updated_at", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "notification_robots", "created_by", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "notification_robots", "updated_by", "TEXT NOT NULL DEFAULT ''");

        EnsureColumn(connection, "notification_robot_bindings", "event_name", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "notification_robot_bindings", "tcode", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "notification_robot_bindings", "plant_group_id", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "notification_robot_bindings", "enabled", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(connection, "notification_robot_bindings", "created_at", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "notification_robot_bindings", "updated_at", "TEXT NOT NULL DEFAULT ''");
    }

    static void SeedBasicConfig(SqliteConnection connection)
    {
        var plants = new[]
        {
            new PlantSeed("5021", "集采工厂", "", 10),
            new PlantSeed("9301", "集采工厂", "", 20),
            new PlantSeed("1101", "集采工厂", "", 30),
            new PlantSeed("207M", "集采工厂", "", 40),
            new PlantSeed("1022", "平湖三厂", "2800", 100),
            new PlantSeed("1024", "平湖三厂", "2900", 110),
            new PlantSeed("1032", "平湖三厂", "9200", 120),
            new PlantSeed("6041", "平湖三厂", "2800", 130),
            new PlantSeed("103C", "平湖七厂", "2910", 200),
            new PlantSeed("1031", "平湖一厂", "3400", 300),
            new PlantSeed("1033", "平湖九厂", "2920", 310),
            new PlantSeed("103D", "平湖九厂", "2920", 320),
            new PlantSeed("1035", "平湖二厂", "5100", 400),
            new PlantSeed("1036", "平湖五厂", "2790", 410)
        };

        foreach (var plant in plants)
            InsertDefaultPlant(connection, plant);

        InsertDefaultGroup(connection, new PlantGroupSeed("PROCUREMENT", "集采工厂", "集采", new[] { "5021", "9301", "1101", "207M" }, 10)
        {
            Zfi072Plants = new[] { "5021", "9301", "1101", "207M" }
        });
        InsertDefaultGroup(connection, new PlantGroupSeed("PINGHU_30", "平湖三厂 / 平湖十厂", "三厂/十厂", new[] { "1022", "1024", "1032", "6041" }, 20)
        {
            Zfi019nlAreas = new[] { "2900", "9200", "2800", "3960" },
            Zfi080Areas = new[] { "2900", "9200", "2800", "3960" },
            Zfi072Plants = new[] { "1024", "1032", "6041", "1022" },
            Zco019Plants = new[] { "1022", "1024", "1032", "6041" }
        });
        InsertDefaultGroup(connection, new PlantGroupSeed("PINGHU_7", "平湖七厂", "七厂", new[] { "103C" }, 30)
        {
            Zfi019nlAreas = new[] { "2910" },
            Zfi080Areas = new[] { "2910" },
            Zfi072Plants = new[] { "103C" },
            Zco019Plants = new[] { "103C" }
        });
        InsertDefaultGroup(connection, new PlantGroupSeed("PINGHU_19", "平湖一厂 / 平湖九厂", "一厂/九厂", new[] { "1031", "1033", "103C", "103D" }, 40)
        {
            Zfi019nlAreas = new[] { "3400", "2920" },
            Zfi080Areas = new[] { "3400", "2920" },
            Zfi072Plants = new[] { "1031", "1033", "103C", "103D" },
            Zco019Plants = new[] { "1031", "1033", "103C", "103D" }
        });
        InsertDefaultGroup(connection, new PlantGroupSeed("PINGHU_25", "平湖二厂 / 平湖五厂", "二厂/五厂", new[] { "1035", "1036" }, 50)
        {
            Zfi019nlAreas = new[] { "5100", "2790" },
            Zfi080Areas = new[] { "5100", "2790" },
            Zfi072Plants = new[] { "1035", "1036" },
            Zco019Plants = new[] { "1035", "1036" }
        });
        InsertDefaultGroup(connection, new PlantGroupSeed("PINGHU_ALL", "全部平湖业务范围", "全部", new[] { "1022", "1024", "1032", "6041", "103C", "1031", "1033", "103D", "1035", "1036" }, 60)
        {
            Zfi019nlAreas = new[] { "2900", "9200", "2800", "3960", "2910", "3400", "2920", "5100", "2790" },
            Zfi080Areas = new[] { "2900", "9200", "2800", "3960", "2910", "3400", "2920", "5100", "2790" },
            Zfi072Plants = new[] { "5021", "9301", "1101", "207M", "1024", "1032", "6041", "1022", "103C", "1031", "1033", "103D", "1035", "1036" },
            Zco019Plants = new[] { "1022", "1024", "1032", "6041", "103C", "1031", "1033", "103D", "1035", "1036" }
        });

        NormalizeSeedLabels(connection);
        SeedTransactionRulesFromTransactions(connection);
    }

    static void NormalizeSeedLabels(SqliteConnection connection)
    {
        var groupLabels = new[]
        {
            ("PROCUREMENT", "集采工厂", "集采", new[] { "Procurement plants", "Procurement" }),
            ("PINGHU_30", "平湖三厂 / 平湖十厂", "三厂/十厂", new[] { "Pinghu 30", "30", "平湖三十厂", "三十厂" }),
            ("PINGHU_7", "平湖七厂", "七厂", new[] { "Pinghu 7", "7" }),
            ("PINGHU_19", "平湖一厂 / 平湖九厂", "一厂/九厂", new[] { "Pinghu 19", "19", "平湖一九厂", "一九厂" }),
            ("PINGHU_25", "平湖二厂 / 平湖五厂", "二厂/五厂", new[] { "Pinghu 25", "25", "平湖二五厂", "二五厂" }),
            ("PINGHU_ALL", "全部平湖业务范围", "全部", new[] { "All Pinghu plants", "All", "全部平湖厂区" })
        };

        foreach (var (id, name, shortName, legacyNames) in groupLabels)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
UPDATE plant_groups
SET name=$name,
    short_name=$shortName,
    updated_at=datetime('now', 'localtime'),
    updated_by='seed-migration'
WHERE id=$id
  AND (
      name<>$name
      OR short_name<>$shortName
      OR name IN ({string.Join(",", legacyNames.Select((_, index) => "$legacyName" + index))})
      OR short_name IN ({string.Join(",", legacyNames.Select((_, index) => "$legacyShortName" + index))})
  )
  AND (
      updated_by IN ('seed', 'seed-migration')
      OR name IN ({string.Join(",", legacyNames.Select((_, index) => "$legacyName" + index))})
      OR short_name IN ({string.Join(",", legacyNames.Select((_, index) => "$legacyShortName" + index))})
  );
""";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$shortName", shortName);
            for (int i = 0; i < legacyNames.Length; i++)
            {
                command.Parameters.AddWithValue("$legacyName" + i, legacyNames[i]);
                command.Parameters.AddWithValue("$legacyShortName" + i, legacyNames[i]);
            }

            command.ExecuteNonQuery();
        }

        var plantLabels = new[]
        {
            ("5021", "集采工厂"), ("9301", "集采工厂"), ("1101", "集采工厂"), ("207M", "集采工厂"),
            ("1022", "平湖三厂"), ("1024", "平湖三厂"), ("1032", "平湖三厂"), ("6041", "平湖三厂"),
            ("103C", "平湖七厂"), ("1031", "平湖一厂"), ("1033", "平湖九厂"), ("103D", "平湖九厂"),
            ("1035", "平湖二厂"), ("1036", "平湖五厂")
        };

        foreach (var (code, name) in plantLabels)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
UPDATE plants
SET name=$name,
    updated_at=datetime('now', 'localtime'),
    updated_by='seed-migration'
WHERE code=$code
  AND name<>$name
  AND (updated_by IN ('seed', 'seed-migration') OR name LIKE 'PINGHU_%' OR name='PROCUREMENT');
""";
            command.Parameters.AddWithValue("$code", code);
            command.Parameters.AddWithValue("$name", name);
            command.ExecuteNonQuery();
        }
    }

    static void InsertDefaultPlant(SqliteConnection connection, PlantSeed seed)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
INSERT OR IGNORE INTO plants(code, name, business_area, enabled, sort_order, created_by, updated_by)
VALUES($code, $name, $businessArea, 1, $sortOrder, 'seed', 'seed');
""";
        command.Parameters.AddWithValue("$code", seed.Code);
        command.Parameters.AddWithValue("$name", seed.Name);
        command.Parameters.AddWithValue("$businessArea", seed.BusinessArea);
        command.Parameters.AddWithValue("$sortOrder", seed.SortOrder);
        command.ExecuteNonQuery();
    }

    static void InsertDefaultGroup(SqliteConnection connection, PlantGroupSeed seed)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
INSERT OR IGNORE INTO plant_groups(
    id, name, short_name, description, zfi019nl_areas_json, zfi080_areas_json,
    zfi072_plants_json, zco019_plants_json, enabled, sort_order, created_by, updated_by
)
VALUES(
    $id, $name, $shortName, $description, $zfi019nlAreasJson, $zfi080AreasJson,
    $zfi072PlantsJson, $zco019PlantsJson, 1, $sortOrder, 'seed', 'seed'
);
""";
        command.Parameters.AddWithValue("$id", seed.Id);
        command.Parameters.AddWithValue("$name", seed.Name);
        command.Parameters.AddWithValue("$shortName", seed.ShortName);
        command.Parameters.AddWithValue("$description", seed.Description);
        command.Parameters.AddWithValue("$zfi019nlAreasJson", JsonSerializer.Serialize(seed.Zfi019nlAreas, JsonOptions));
        command.Parameters.AddWithValue("$zfi080AreasJson", JsonSerializer.Serialize(seed.Zfi080Areas, JsonOptions));
        command.Parameters.AddWithValue("$zfi072PlantsJson", JsonSerializer.Serialize(seed.Zfi072Plants, JsonOptions));
        command.Parameters.AddWithValue("$zco019PlantsJson", JsonSerializer.Serialize(seed.Zco019Plants, JsonOptions));
        command.Parameters.AddWithValue("$sortOrder", seed.SortOrder);
        command.ExecuteNonQuery();

        for (int i = 0; i < seed.Plants.Length; i++)
        {
            using var memberCommand = connection.CreateCommand();
            memberCommand.CommandText = """
INSERT OR IGNORE INTO plant_group_members(group_id, plant_code, sort_order)
VALUES($groupId, $plantCode, $sortOrder);
""";
            memberCommand.Parameters.AddWithValue("$groupId", seed.Id);
            memberCommand.Parameters.AddWithValue("$plantCode", seed.Plants[i]);
            memberCommand.Parameters.AddWithValue("$sortOrder", i);
            memberCommand.ExecuteNonQuery();
        }
    }

    static void SeedTransactionRulesFromTransactions(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
INSERT OR IGNORE INTO transaction_plant_rules(
    tcode, factory_rule, default_group, fixed_plants_json, selectable_group_ids_json,
    business_area_mode, business_areas_json, enabled, created_by, updated_by
)
SELECT tcode, factory_rule, default_group, fixed_plants_json, '[]',
       CASE WHEN instr(params_json, 'businessAreas') > 0 THEN 'byPlant' ELSE 'none' END,
       '[]', enabled, 'seed', 'seed'
FROM transactions;
""";
        command.ExecuteNonQuery();
    }

    static void SeedTransactions(SqliteConnection connection)
    {
        string configPath = FindTransactionConfigPath();
        if (!File.Exists(configPath))
        {
            Log($"事务码配置文件不存在，跳过种子数据: {configPath}");
            return;
        }

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(configPath, Encoding.UTF8));
        if (!doc.RootElement.TryGetProperty("transactions", out JsonElement transactions) ||
            transactions.ValueKind != JsonValueKind.Array)
            return;

        foreach (JsonElement item in transactions.EnumerateArray())
        {
            string tcode = GetJsonString(item, "code").ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(tcode))
                continue;

            string scriptFile = FirstNonEmpty(GetJsonString(item, "script"), $"{tcode}.vbs");
            string scriptText = ReadScriptTextIfExists(scriptFile, tcode);
            var metadata = ExtractScriptMetadata(scriptText);
            string fixedPlants = FirstNonEmpty(
                JsonArrayToCsv(item, "fixedPlants"),
                GetMetadataFixedPlants(tcode, metadata));
            string scriptVersion = metadata.TryGetValue("version", out string? version) ? version ?? "" : "";
            string scriptHash = string.IsNullOrWhiteSpace(scriptText) ? "" : Sha256Hex(scriptText);
            int timeoutSeconds = GetJsonInt(item, "timeoutSeconds", GetJsonInt(item, "timeout", 0));
            int retryCount = GetJsonInt(item, "retryCount", GetJsonInt(item, "retry", 0));

            using var command = connection.CreateCommand();
            command.CommandText = """
INSERT INTO transactions (
    tcode, name, stage, script_file, icon, params_json, factory_rule, fixed_plants_json,
    default_group, automation, timeout_seconds, retry_count,
    script_version, script_hash, script_metadata_json, enabled, updated_at
) VALUES (
    $tcode, $name, $stage, $scriptFile, $icon, $paramsJson, $factoryRule, $fixedPlantsJson,
    $defaultGroup, $automation, $timeoutSeconds, $retryCount,
    $scriptVersion, $scriptHash, $metadataJson, 1, $updatedAt
)
ON CONFLICT(tcode) DO UPDATE SET
    name=excluded.name,
    stage=excluded.stage,
    script_file=excluded.script_file,
    icon=excluded.icon,
    params_json=excluded.params_json,
    factory_rule=excluded.factory_rule,
    fixed_plants_json=excluded.fixed_plants_json,
    default_group=excluded.default_group,
    automation=excluded.automation,
    timeout_seconds=excluded.timeout_seconds,
    retry_count=excluded.retry_count,
    script_version=excluded.script_version,
    script_hash=excluded.script_hash,
    script_metadata_json=excluded.script_metadata_json,
    enabled=excluded.enabled,
    updated_at=excluded.updated_at;
""";
            command.Parameters.AddWithValue("$tcode", tcode);
            command.Parameters.AddWithValue("$name", GetJsonString(item, "name"));
            command.Parameters.AddWithValue("$stage", GetJsonString(item, "stage"));
            command.Parameters.AddWithValue("$scriptFile", scriptFile);
            command.Parameters.AddWithValue("$icon", GetJsonString(item, "icon"));
            command.Parameters.AddWithValue("$paramsJson", JsonArrayPropertyToJson(item, "params"));
            command.Parameters.AddWithValue("$factoryRule", GetJsonString(item, "factoryRule"));
            command.Parameters.AddWithValue("$fixedPlantsJson", CsvToJsonArray(fixedPlants));
            command.Parameters.AddWithValue("$defaultGroup", GetJsonString(item, "defaultPlantGroup"));
            command.Parameters.AddWithValue("$automation", GetJsonString(item, "automation"));
            command.Parameters.AddWithValue("$timeoutSeconds", timeoutSeconds);
            command.Parameters.AddWithValue("$retryCount", retryCount);
            command.Parameters.AddWithValue("$scriptVersion", scriptVersion);
            command.Parameters.AddWithValue("$scriptHash", scriptHash);
            command.Parameters.AddWithValue("$metadataJson", JsonSerializer.Serialize(metadata, JsonOptions));
            command.Parameters.AddWithValue("$updatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            command.ExecuteNonQuery();

            if (!string.IsNullOrWhiteSpace(scriptText))
                UpsertScriptCache(connection, tcode, scriptFile, scriptHash, scriptText);
        }
    }

    static void UpsertScriptCache(SqliteConnection connection, string tcode, string scriptFile, string scriptHash, string scriptText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO script_cache(tcode, script_file, script_hash, script_text, cached_at)
VALUES($tcode, $scriptFile, $scriptHash, $scriptText, $cachedAt)
ON CONFLICT(tcode) DO UPDATE SET
    script_file=excluded.script_file,
    script_hash=excluded.script_hash,
    script_text=excluded.script_text,
    cached_at=excluded.cached_at;
""";
        command.Parameters.AddWithValue("$tcode", tcode);
        command.Parameters.AddWithValue("$scriptFile", scriptFile);
        command.Parameters.AddWithValue("$scriptHash", scriptHash);
        command.Parameters.AddWithValue("$scriptText", scriptText);
        command.Parameters.AddWithValue("$cachedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        command.ExecuteNonQuery();
    }

    static object LoadTransactionsFromDatabase()
    {
        InitializeDatabase(seedFromScripts: true);
        var list = new List<object>();
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
SELECT tcode, name, stage, script_file, icon, params_json, factory_rule, fixed_plants_json,
       default_group, automation, timeout_seconds, retry_count,
       script_version, script_hash, enabled, updated_at
FROM transactions
ORDER BY stage, tcode;
""";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new
            {
                code = reader.GetString(0),
                name = reader.GetString(1),
                stage = reader.GetString(2),
                script = reader.GetString(3),
                icon = reader.GetString(4),
                paramsList = JsonSerializer.Deserialize<string[]>(reader.GetString(5)) ?? Array.Empty<string>(),
                factoryRule = reader.GetString(6),
                fixedPlants = JsonSerializer.Deserialize<string[]>(reader.GetString(7)) ?? Array.Empty<string>(),
                defaultPlantGroup = reader.GetString(8),
                automation = reader.GetString(9),
                timeoutSeconds = reader.GetInt32(10),
                retryCount = reader.GetInt32(11),
                scriptVersion = reader.GetString(12),
                scriptHash = reader.GetString(13),
                enabled = reader.GetInt32(14) == 1,
                updatedAt = reader.GetString(15)
            });
        }

        return new
        {
            version = 1,
            source = "sqlite",
            database = DatabaseFilePath,
            transactions = list
        };
    }

    static object? LoadScriptMetadataFromDatabase(string tcode)
    {
        InitializeDatabase(seedFromScripts: true);
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
SELECT tcode, name, script_file, fixed_plants_json, script_version, script_hash, script_metadata_json, updated_at
FROM transactions
WHERE tcode=$tcode;
""";
        command.Parameters.AddWithValue("$tcode", tcode.ToUpperInvariant());
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return new
        {
            code = reader.GetString(0),
            name = reader.GetString(1),
            script = reader.GetString(2),
            fixedPlants = JsonSerializer.Deserialize<string[]>(reader.GetString(3)) ?? Array.Empty<string>(),
            scriptVersion = reader.GetString(4),
            scriptHash = reader.GetString(5),
            metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(6)) ?? new Dictionary<string, string>(),
            updatedAt = reader.GetString(7)
        };
    }

    static object LoadDatabaseSchema()
    {
        InitializeDatabase(seedFromScripts: true);
        var tables = new List<object>();
        using var connection = OpenDatabaseConnection();
        foreach (string tableName in GetUserTableNames(connection))
        {
            tables.Add(new
            {
                name = tableName,
                rowCount = CountRows(connection, tableName),
                columns = LoadTableColumns(connection, tableName),
                indexes = LoadTableIndexes(connection, tableName)
            });
        }

        return new
        {
            version = 1,
            source = "sqlite",
            database = DatabaseFilePath,
            tables
        };
    }

    static object LoadTablePreview(string tableName, HttpListenerRequest request)
    {
        InitializeDatabase(seedFromScripts: true);
        tableName = tableName.Trim();
        using var connection = OpenDatabaseConnection();
        var tableNames = GetUserTableNames(connection);
        if (!tableNames.Contains(tableName, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unknown table: {tableName}");

        string actualName = tableNames.First(v => v.Equals(tableName, StringComparison.OrdinalIgnoreCase));
        int limit = DefaultRunListLimit;
        if (int.TryParse(request.QueryString["limit"], out int parsedLimit))
            limit = Math.Clamp(parsedLimit, 1, 200);

        var rows = new List<Dictionary<string, object?>>();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM \"{actualName.Replace("\"", "\"\"")}\" LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
            {
                string columnName = reader.GetName(i);
                row[columnName] = reader.IsDBNull(i)
                    ? null
                    : IsSensitivePreviewColumn(columnName) ? "已隐藏" : reader.GetValue(i);
            }
            rows.Add(row);
        }

        return new
        {
            version = 1,
            source = "sqlite",
            database = DatabaseFilePath,
            table = actualName,
            rowCount = CountRows(connection, actualName),
            columns = LoadTableColumns(connection, actualName),
            rows
        };
    }

    static bool IsSensitivePreviewColumn(string columnName)
    {
        return Regex.IsMatch(columnName ?? "", "password|passwd|pwd|secret|token|webhook|credential|protected", RegexOptions.IgnoreCase);
    }

    static List<string> GetUserTableNames(SqliteConnection connection)
    {
        var result = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = """
SELECT name
FROM sqlite_master
WHERE type='table'
  AND name NOT LIKE 'sqlite_%'
ORDER BY name;
""";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    static long CountRows(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{tableName.Replace("\"", "\"\"")}\"";
        return Convert.ToInt64(command.ExecuteScalar() ?? 0);
    }

    static List<object> LoadTableColumns(SqliteConnection connection, string tableName)
    {
        var columns = new List<object>();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{tableName.Replace("\"", "\"\"")}\")";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(new
            {
                cid = reader.GetInt32(0),
                name = reader.GetString(1),
                type = reader.GetString(2),
                notNull = reader.GetInt32(3) == 1,
                defaultValue = reader.IsDBNull(4) ? "" : reader.GetString(4),
                primaryKey = reader.GetInt32(5) == 1
            });
        }

        return columns;
    }

    static List<object> LoadTableIndexes(SqliteConnection connection, string tableName)
    {
        var indexes = new List<object>();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_list(\"{tableName.Replace("\"", "\"\"")}\")";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            indexes.Add(new
            {
                name = reader.GetString(1),
                unique = reader.GetInt32(2) == 1,
                origin = reader.GetString(3),
                partial = reader.GetInt32(4) == 1
            });
        }

        return indexes;
    }

    static string UpsertTransaction(TransactionConfigRequest item, string routeCode)
    {
        InitializeDatabase(seedFromScripts: true);
        string tcode = SanitizeTCode(FirstNonEmpty(routeCode, item.Code)).ToUpperInvariant();
        string scriptFile = NormalizeScriptFileName(FirstNonEmpty(item.ScriptFile, item.Script, $"{tcode}.vbs"), tcode);
        if (string.IsNullOrWhiteSpace(scriptFile))
            scriptFile = $"{tcode}.vbs";

        string scriptText = ReadScriptTextIfExists(scriptFile, tcode);
        var metadata = ExtractScriptMetadata(scriptText);
        string fixedPlants = FirstNonEmpty(
            item.FixedPlantsCsv,
            JsonElementArrayToCsv(item.FixedPlants),
            GetMetadataFixedPlants(tcode, metadata));
        string scriptVersion = FirstNonEmpty(
            item.ScriptVersion,
            metadata.TryGetValue("version", out string? version) ? version ?? "" : "");
        string scriptHash = string.IsNullOrWhiteSpace(scriptText)
            ? FirstNonEmpty(item.ScriptHash)
            : Sha256Hex(scriptText);
        bool hasTimeoutSeconds = HasPresent(item.TimeoutSeconds, item.Timeout);
        bool hasRetryCount = HasPresent(item.RetryCount, item.Retry);
        int timeoutSeconds = NormalizeNonNegative(FirstPresent(item.TimeoutSeconds, item.Timeout, 0));
        int retryCount = NormalizeNonNegative(FirstPresent(item.RetryCount, item.Retry, 0));

        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO transactions (
    tcode, name, stage, script_file, icon, params_json, factory_rule, fixed_plants_json,
    default_group, automation, timeout_seconds, retry_count,
    script_version, script_hash, script_metadata_json, enabled, updated_at
) VALUES (
    $tcode, $name, $stage, $scriptFile, $icon, $paramsJson, $factoryRule, $fixedPlantsJson,
    $defaultGroup, $automation, $timeoutSeconds, $retryCount,
    $scriptVersion, $scriptHash, $metadataJson, $enabled, $updatedAt
)
ON CONFLICT(tcode) DO UPDATE SET
    name=excluded.name,
    stage=excluded.stage,
    script_file=excluded.script_file,
    icon=excluded.icon,
    params_json=excluded.params_json,
    factory_rule=excluded.factory_rule,
    fixed_plants_json=excluded.fixed_plants_json,
    default_group=excluded.default_group,
    automation=excluded.automation,
    timeout_seconds=CASE WHEN $hasTimeoutSeconds = 1 THEN excluded.timeout_seconds ELSE transactions.timeout_seconds END,
    retry_count=CASE WHEN $hasRetryCount = 1 THEN excluded.retry_count ELSE transactions.retry_count END,
    script_version=excluded.script_version,
    script_hash=excluded.script_hash,
    script_metadata_json=excluded.script_metadata_json,
    enabled=excluded.enabled,
    updated_at=excluded.updated_at;
""";
        command.Parameters.AddWithValue("$tcode", tcode);
        command.Parameters.AddWithValue("$name", FirstNonEmpty(item.Name, tcode));
        command.Parameters.AddWithValue("$stage", item.Stage ?? "");
        command.Parameters.AddWithValue("$scriptFile", scriptFile);
        command.Parameters.AddWithValue("$icon", FirstNonEmpty(item.Icon, "terminal"));
        command.Parameters.AddWithValue("$paramsJson", TransactionParamsToJson(item.Params));
        command.Parameters.AddWithValue("$factoryRule", item.FactoryRule ?? "");
        command.Parameters.AddWithValue("$fixedPlantsJson", CsvToJsonArray(fixedPlants));
        command.Parameters.AddWithValue("$defaultGroup", item.DefaultPlantGroup ?? "");
        command.Parameters.AddWithValue("$automation", FirstNonEmpty(item.Automation, item.DefaultRunMode, "openOnly"));
        command.Parameters.AddWithValue("$timeoutSeconds", timeoutSeconds);
        command.Parameters.AddWithValue("$retryCount", retryCount);
        command.Parameters.AddWithValue("$hasTimeoutSeconds", hasTimeoutSeconds ? 1 : 0);
        command.Parameters.AddWithValue("$hasRetryCount", hasRetryCount ? 1 : 0);
        command.Parameters.AddWithValue("$scriptVersion", scriptVersion);
        command.Parameters.AddWithValue("$scriptHash", scriptHash);
        command.Parameters.AddWithValue("$metadataJson", JsonSerializer.Serialize(metadata, JsonOptions));
        command.Parameters.AddWithValue("$enabled", item.Enabled.GetValueOrDefault(true) ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        command.ExecuteNonQuery();

        if (!string.IsNullOrWhiteSpace(scriptText))
            UpsertScriptCache(connection, tcode, scriptFile, scriptHash, scriptText);

        return tcode;
    }

    static void SetTransactionEnabled(string tcode, bool enabled)
    {
        InitializeDatabase(seedFromScripts: true);
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE transactions SET enabled=$enabled, updated_at=$updatedAt WHERE tcode=$tcode";
        command.Parameters.AddWithValue("$tcode", tcode.ToUpperInvariant());
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        command.ExecuteNonQuery();
    }

    static object LoadBasicConfig()
    {
        InitializeDatabase(seedFromScripts: true);
        using var connection = OpenDatabaseConnection();
        var transactions = LoadTransactionConfig(connection);
        var plants = LoadPlantConfig(connection);
        var groups = LoadPlantGroupConfig(connection);
        var rules = LoadTransactionRuleConfig(connection);
        var robotBindings = LoadNotificationRobotBindingsConfig(connection);
        var robots = LoadNotificationRobotConfig(connection);

        return new
        {
            version = 1,
            source = "sqlite",
            database = DatabaseFilePath,
            transactions,
            plants,
            plantGroups = groups,
            rules,
            transactionRules = rules,
            notificationRobots = robots,
            notificationRobotBindings = robotBindings,
            notificationBindings = robotBindings
        };
    }

    static List<object> LoadTransactionConfig(SqliteConnection connection)
    {
        var transactions = new List<object>();
        using var command = connection.CreateCommand();
        command.CommandText = """
SELECT tcode, name, stage, script_file, icon, params_json, factory_rule, fixed_plants_json,
       default_group, automation, timeout_seconds, retry_count,
       script_version, script_hash, enabled, updated_at
FROM transactions
ORDER BY stage, tcode;
""";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            transactions.Add(new
            {
                code = reader.GetString(0),
                tcode = reader.GetString(0),
                name = reader.GetString(1),
                stage = reader.GetString(2),
                script = reader.GetString(3),
                scriptFile = reader.GetString(3),
                icon = reader.GetString(4),
                paramsList = SafeJsonArray(reader.GetString(5)),
                @params = SafeJsonArray(reader.GetString(5)),
                factoryRule = reader.GetString(6),
                fixedPlants = SafeJsonArray(reader.GetString(7)),
                defaultPlantGroup = reader.GetString(8),
                defaultGroup = reader.GetString(8),
                automation = reader.GetString(9),
                timeoutSeconds = reader.GetInt32(10),
                retryCount = reader.GetInt32(11),
                timeout = reader.GetInt32(10),
                retry = reader.GetInt32(11),
                scriptVersion = reader.GetString(12),
                scriptHash = reader.GetString(13),
                enabled = reader.GetInt32(14) == 1,
                updatedAt = reader.GetString(15)
            });
        }

        return transactions;
    }

    static List<object> LoadPlantConfig(SqliteConnection connection)
    {
        var groupMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        using (var groupCommand = connection.CreateCommand())
        {
            groupCommand.CommandText = """
SELECT plant_code, group_id
FROM plant_group_members
ORDER BY sort_order, group_id;
""";
            using var reader = groupCommand.ExecuteReader();
            while (reader.Read())
            {
                string plantCode = reader.GetString(0);
                string groupId = reader.GetString(1);
                if (!groupMap.TryGetValue(plantCode, out var list))
                {
                    list = new List<string>();
                    groupMap[plantCode] = list;
                }

                if (!list.Contains(groupId, StringComparer.OrdinalIgnoreCase))
                    list.Add(groupId);
            }
        }

        var plants = new List<object>();
        using var command = connection.CreateCommand();
        command.CommandText = """
SELECT code, name, business_area, enabled, sort_order, updated_at
FROM plants
ORDER BY sort_order, code;
""";
        using var plantReader = command.ExecuteReader();
        while (plantReader.Read())
        {
            string code = plantReader.GetString(0);
            plants.Add(new
            {
                code,
                name = plantReader.GetString(1),
                businessArea = plantReader.GetString(2),
                area = plantReader.GetString(2),
                groups = groupMap.TryGetValue(code, out var groups) ? groups.ToArray() : Array.Empty<string>(),
                enabled = plantReader.GetInt32(3) == 1,
                sortOrder = plantReader.GetInt32(4),
                updatedAt = plantReader.GetString(5)
            });
        }

        return plants;
    }

    static List<object> LoadPlantGroupConfig(SqliteConnection connection)
    {
        var members = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        using (var memberCommand = connection.CreateCommand())
        {
            memberCommand.CommandText = """
SELECT group_id, plant_code
FROM plant_group_members
ORDER BY group_id, sort_order, plant_code;
""";
            using var reader = memberCommand.ExecuteReader();
            while (reader.Read())
            {
                string groupId = reader.GetString(0);
                if (!members.TryGetValue(groupId, out var list))
                {
                    list = new List<string>();
                    members[groupId] = list;
                }

                list.Add(reader.GetString(1));
            }
        }

        var groups = new List<object>();
        using var command = connection.CreateCommand();
        command.CommandText = """
SELECT id, name, short_name, description, zfi019nl_areas_json, zfi080_areas_json,
       zfi072_plants_json, zco019_plants_json, enabled, sort_order, updated_at
FROM plant_groups
ORDER BY sort_order, id;
""";
        using var groupReader = command.ExecuteReader();
        while (groupReader.Read())
        {
            string id = groupReader.GetString(0);
            groups.Add(new
            {
                id,
                name = groupReader.GetString(1),
                shortName = groupReader.GetString(2),
                description = groupReader.GetString(3),
                plants = members.TryGetValue(id, out var plantCodes) ? plantCodes.ToArray() : Array.Empty<string>(),
                zfi019nlAreas = JsonSerializer.Deserialize<string[]>(groupReader.GetString(4)) ?? Array.Empty<string>(),
                zfi080Areas = JsonSerializer.Deserialize<string[]>(groupReader.GetString(5)) ?? Array.Empty<string>(),
                zfi072Plants = JsonSerializer.Deserialize<string[]>(groupReader.GetString(6)) ?? Array.Empty<string>(),
                zco019Plants = JsonSerializer.Deserialize<string[]>(groupReader.GetString(7)) ?? Array.Empty<string>(),
                enabled = groupReader.GetInt32(8) == 1,
                sortOrder = groupReader.GetInt32(9),
                updatedAt = groupReader.GetString(10)
            });
        }

        return groups;
    }

    static List<object> LoadTransactionRuleConfig(SqliteConnection connection)
    {
        var rules = new List<object>();
        using var command = connection.CreateCommand();
        command.CommandText = """
SELECT r.tcode,
       COALESCE(t.name, ''),
       COALESCE(t.stage, ''),
       COALESCE(t.script_file, ''),
       r.factory_rule,
       r.default_group,
       r.fixed_plants_json,
       r.selectable_group_ids_json,
       r.business_area_mode,
       r.business_areas_json,
       COALESCE(t.timeout_seconds, 0),
       COALESCE(t.retry_count, 0),
       r.enabled,
       r.updated_at
FROM transaction_plant_rules r
LEFT JOIN transactions t ON t.tcode = r.tcode
ORDER BY COALESCE(t.stage, ''), r.tcode;
""";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string fixedPlantsJson = reader.GetString(6);
            rules.Add(new
            {
                tcode = reader.GetString(0),
                code = reader.GetString(0),
                transactionName = reader.GetString(1),
                name = reader.GetString(1),
                stage = reader.GetString(2),
                script = reader.GetString(3),
                factoryRule = reader.GetString(4),
                defaultPlantGroup = reader.GetString(5),
                defaultGroup = reader.GetString(5),
                fixedPlants = SafeJsonArray(fixedPlantsJson),
                fixedPlantsCsv = string.Join(",", SafeJsonArray(fixedPlantsJson)),
                selectableGroupIds = SafeJsonArray(reader.GetString(7)),
                businessAreaMode = reader.GetString(8),
                businessAreas = SafeJsonArray(reader.GetString(9)),
                timeoutSeconds = reader.GetInt32(10),
                retryCount = reader.GetInt32(11),
                timeout = reader.GetInt32(10),
                retry = reader.GetInt32(11),
                enabled = reader.GetInt32(12) == 1,
                updatedAt = reader.GetString(13)
            });
        }

        return rules;
    }

    static List<object> LoadNotificationRobotBindingsConfig(SqliteConnection connection)
    {
        var bindings = new List<object>();
        using var command = connection.CreateCommand();
        command.CommandText = """
SELECT robot_id, event_name, tcode, plant_group_id, enabled, updated_at
FROM notification_robot_bindings
ORDER BY robot_id, event_name, tcode, plant_group_id;
""";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            bindings.Add(new
            {
                robotId = reader.GetString(0),
                eventName = reader.GetString(1),
                tcode = reader.GetString(2),
                code = reader.GetString(2),
                plantGroupId = reader.GetString(3),
                enabled = reader.GetInt32(4) == 1,
                updatedAt = reader.GetString(5)
            });
        }

        return bindings;
    }

    static List<object> LoadNotificationRobotConfig(SqliteConnection connection)
    {
        var bindings = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
        using (var bindingCommand = connection.CreateCommand())
        {
            bindingCommand.CommandText = """
SELECT robot_id, event_name, tcode, plant_group_id, enabled
FROM notification_robot_bindings
ORDER BY robot_id, event_name, tcode, plant_group_id;
""";
            using var bindingReader = bindingCommand.ExecuteReader();
            while (bindingReader.Read())
            {
                string robotId = bindingReader.GetString(0);
                if (!bindings.TryGetValue(robotId, out var list))
                {
                    list = new List<object>();
                    bindings[robotId] = list;
                }

                list.Add(new
                {
                    eventName = bindingReader.GetString(1),
                    tcode = bindingReader.GetString(2),
                    code = bindingReader.GetString(2),
                    plantGroupId = bindingReader.GetString(3),
                    enabled = bindingReader.GetInt32(4) == 1
                });
            }
        }

        var robots = new List<object>();
        using var command = connection.CreateCommand();
        command.CommandText = """
SELECT id, name, robot_type, target_label, webhook_protected, secret_protected, enabled, updated_at
FROM notification_robots
ORDER BY name, id;
""";
        using var robotReader = command.ExecuteReader();
        while (robotReader.Read())
        {
            string id = robotReader.GetString(0);
            robots.Add(new
            {
                id,
                name = robotReader.GetString(1),
                robotType = robotReader.GetString(2),
                type = robotReader.GetString(2),
                targetLabel = robotReader.GetString(3),
                hasWebhook = !string.IsNullOrWhiteSpace(robotReader.GetString(4)),
                hasSecret = !string.IsNullOrWhiteSpace(robotReader.GetString(5)),
                webhookLabel = string.IsNullOrWhiteSpace(robotReader.GetString(4)) ? "" : "configured",
                secretLabel = string.IsNullOrWhiteSpace(robotReader.GetString(5)) ? "" : "configured",
                enabled = robotReader.GetInt32(6) == 1,
                updatedAt = robotReader.GetString(7),
                bindings = bindings.TryGetValue(id, out var robotBindings) ? robotBindings.ToArray() : Array.Empty<object>()
            });
        }

        return robots;
    }

    static string UpsertPlant(PlantConfigRequest item, string routeCode)
    {
        InitializeDatabase(seedFromScripts: true);
        string code = SanitizePlantCode(FirstNonEmpty(routeCode, item.Code));
        string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string[] groups = NormalizeStringArray(FirstNonEmpty(item.GroupsCsv, JsonElementArrayToCsv(item.Groups)));

        using var connection = OpenDatabaseConnection();
        using var tx = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = """
INSERT INTO plants(code, name, business_area, enabled, sort_order, updated_at, updated_by)
VALUES($code, $name, $businessArea, $enabled, $sortOrder, $updatedAt, $updatedBy)
ON CONFLICT(code) DO UPDATE SET
    name=excluded.name,
    business_area=excluded.business_area,
    enabled=excluded.enabled,
    sort_order=excluded.sort_order,
    updated_at=excluded.updated_at,
    updated_by=excluded.updated_by;
""";
            command.Parameters.AddWithValue("$code", code);
            command.Parameters.AddWithValue("$name", FirstNonEmpty(item.Name, code));
            command.Parameters.AddWithValue("$businessArea", FirstNonEmpty(item.BusinessArea, item.Area));
            command.Parameters.AddWithValue("$enabled", item.Enabled.GetValueOrDefault(true) ? 1 : 0);
            command.Parameters.AddWithValue("$sortOrder", item.SortOrder);
            command.Parameters.AddWithValue("$updatedAt", now);
            command.Parameters.AddWithValue("$updatedBy", FirstNonEmpty(item.UpdatedBy, "api"));
            command.ExecuteNonQuery();
        }

        if (groups.Length > 0 || item.Groups.ValueKind == JsonValueKind.Array || !string.IsNullOrWhiteSpace(item.GroupsCsv))
        {
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM plant_group_members WHERE plant_code=$plantCode";
                delete.Parameters.AddWithValue("$plantCode", code);
                delete.ExecuteNonQuery();
            }

            for (int i = 0; i < groups.Length; i++)
            {
                using var memberCommand = connection.CreateCommand();
                memberCommand.Transaction = tx;
                memberCommand.CommandText = """
INSERT OR IGNORE INTO plant_group_members(group_id, plant_code, sort_order)
VALUES($groupId, $plantCode, $sortOrder);
""";
                memberCommand.Parameters.AddWithValue("$groupId", SanitizeConfigId(groups[i], "plant group id"));
                memberCommand.Parameters.AddWithValue("$plantCode", code);
                memberCommand.Parameters.AddWithValue("$sortOrder", i);
                memberCommand.ExecuteNonQuery();
            }
        }

        tx.Commit();
        return code;
    }

    static void SetPlantEnabled(string code, bool enabled)
    {
        InitializeDatabase(seedFromScripts: true);
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
UPDATE plants
SET enabled=$enabled, updated_at=$updatedAt, updated_by='api'
WHERE code=$code;
""";
        command.Parameters.AddWithValue("$code", code);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        command.ExecuteNonQuery();
    }

    static string UpsertPlantGroup(PlantGroupConfigRequest item, string routeId)
    {
        InitializeDatabase(seedFromScripts: true);
        string id = SanitizeConfigId(FirstNonEmpty(routeId, item.Id), "plant group id");
        string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string[] plants = NormalizeStringArray(FirstNonEmpty(item.PlantsCsv, JsonElementArrayToCsv(item.Plants)));
        string zfi019nlAreasJson = CsvToJsonArray(FirstNonEmpty(item.Zfi019nlAreasCsv, JsonElementArrayToCsv(item.Zfi019nlAreas)));
        string zfi080AreasJson = CsvToJsonArray(FirstNonEmpty(item.Zfi080AreasCsv, JsonElementArrayToCsv(item.Zfi080Areas)));
        string zfi072PlantsJson = CsvToJsonArray(FirstNonEmpty(item.Zfi072PlantsCsv, JsonElementArrayToCsv(item.Zfi072Plants)));
        string zco019PlantsJson = CsvToJsonArray(FirstNonEmpty(item.Zco019PlantsCsv, JsonElementArrayToCsv(item.Zco019Plants)));

        using var connection = OpenDatabaseConnection();
        using var tx = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = """
INSERT INTO plant_groups(
    id, name, short_name, description, zfi019nl_areas_json, zfi080_areas_json,
    zfi072_plants_json, zco019_plants_json, enabled, sort_order, updated_at, updated_by
)
VALUES(
    $id, $name, $shortName, $description, $zfi019nlAreasJson, $zfi080AreasJson,
    $zfi072PlantsJson, $zco019PlantsJson, $enabled, $sortOrder, $updatedAt, $updatedBy
)
ON CONFLICT(id) DO UPDATE SET
    name=excluded.name,
    short_name=excluded.short_name,
    description=excluded.description,
    zfi019nl_areas_json=excluded.zfi019nl_areas_json,
    zfi080_areas_json=excluded.zfi080_areas_json,
    zfi072_plants_json=excluded.zfi072_plants_json,
    zco019_plants_json=excluded.zco019_plants_json,
    enabled=excluded.enabled,
    sort_order=excluded.sort_order,
    updated_at=excluded.updated_at,
    updated_by=excluded.updated_by;
""";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$name", FirstNonEmpty(item.Name, id));
            command.Parameters.AddWithValue("$shortName", item.ShortName ?? "");
            command.Parameters.AddWithValue("$description", item.Description ?? "");
            command.Parameters.AddWithValue("$zfi019nlAreasJson", zfi019nlAreasJson);
            command.Parameters.AddWithValue("$zfi080AreasJson", zfi080AreasJson);
            command.Parameters.AddWithValue("$zfi072PlantsJson", zfi072PlantsJson);
            command.Parameters.AddWithValue("$zco019PlantsJson", zco019PlantsJson);
            command.Parameters.AddWithValue("$enabled", item.Enabled.GetValueOrDefault(true) ? 1 : 0);
            command.Parameters.AddWithValue("$sortOrder", item.SortOrder);
            command.Parameters.AddWithValue("$updatedAt", now);
            command.Parameters.AddWithValue("$updatedBy", FirstNonEmpty(item.UpdatedBy, "api"));
            command.ExecuteNonQuery();
        }

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM plant_group_members WHERE group_id=$groupId";
            delete.Parameters.AddWithValue("$groupId", id);
            delete.ExecuteNonQuery();
        }

        for (int i = 0; i < plants.Length; i++)
        {
            using var memberCommand = connection.CreateCommand();
            memberCommand.Transaction = tx;
            memberCommand.CommandText = """
INSERT INTO plant_group_members(group_id, plant_code, sort_order)
VALUES($groupId, $plantCode, $sortOrder);
""";
            memberCommand.Parameters.AddWithValue("$groupId", id);
            memberCommand.Parameters.AddWithValue("$plantCode", plants[i]);
            memberCommand.Parameters.AddWithValue("$sortOrder", i);
            memberCommand.ExecuteNonQuery();
        }

        tx.Commit();
        return id;
    }

    static void SetPlantGroupEnabled(string id, bool enabled)
    {
        InitializeDatabase(seedFromScripts: true);
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
UPDATE plant_groups
SET enabled=$enabled, updated_at=$updatedAt, updated_by='api'
WHERE id=$id;
""";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        command.ExecuteNonQuery();
    }

    static string UpsertTransactionPlantRule(TransactionPlantRuleRequest item, string routeTCode)
    {
        InitializeDatabase(seedFromScripts: true);
        string tcode = SanitizeTCode(FirstNonEmpty(routeTCode, item.TCode, item.Code)).ToUpperInvariant();
        string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string fixedPlantsJson = CsvToJsonArray(FirstNonEmpty(
            item.FixedPlantsCsv,
            JsonElementArrayToCsv(item.FixedPlants),
            item.PlantsCsv,
            JsonElementArrayToCsv(item.Plants)));
        string selectableGroupsJson = CsvToJsonArray(FirstNonEmpty(item.SelectableGroupIdsCsv, JsonElementArrayToCsv(item.SelectableGroupIds)));
        string businessAreasJson = CsvToJsonArray(FirstNonEmpty(item.BusinessAreasCsv, JsonElementArrayToCsv(item.BusinessAreas)));
        bool hasTimeoutSeconds = HasPresent(item.TimeoutSeconds, item.Timeout);
        bool hasRetryCount = HasPresent(item.RetryCount, item.Retry);
        int timeoutSeconds = NormalizeNonNegative(FirstPresent(item.TimeoutSeconds, item.Timeout, 0));
        int retryCount = NormalizeNonNegative(FirstPresent(item.RetryCount, item.Retry, 0));

        using var connection = OpenDatabaseConnection();
        using var tx = connection.BeginTransaction();
        using (var txCommand = connection.CreateCommand())
        {
            txCommand.Transaction = tx;
            txCommand.CommandText = """
INSERT INTO transactions(
    tcode, name, stage, script_file, icon, params_json, factory_rule, fixed_plants_json,
    default_group, automation, timeout_seconds, retry_count, enabled, updated_at
) VALUES(
    $tcode, $name, $stage, $scriptFile, $icon, $paramsJson, $factoryRule, $fixedPlantsJson,
    $defaultGroup, $automation, $timeoutSeconds, $retryCount, $enabled, $updatedAt
)
ON CONFLICT(tcode) DO UPDATE SET
    name=CASE WHEN excluded.name <> '' THEN excluded.name ELSE transactions.name END,
    stage=CASE WHEN excluded.stage <> '' THEN excluded.stage ELSE transactions.stage END,
    script_file=CASE WHEN excluded.script_file <> '' THEN excluded.script_file ELSE transactions.script_file END,
    icon=CASE WHEN excluded.icon <> '' THEN excluded.icon ELSE transactions.icon END,
    params_json=CASE WHEN excluded.params_json <> '[]' THEN excluded.params_json ELSE transactions.params_json END,
    factory_rule=excluded.factory_rule,
    fixed_plants_json=excluded.fixed_plants_json,
    default_group=excluded.default_group,
    automation=CASE WHEN excluded.automation <> '' THEN excluded.automation ELSE transactions.automation END,
    timeout_seconds=CASE WHEN $hasTimeoutSeconds = 1 THEN excluded.timeout_seconds ELSE transactions.timeout_seconds END,
    retry_count=CASE WHEN $hasRetryCount = 1 THEN excluded.retry_count ELSE transactions.retry_count END,
    enabled=excluded.enabled,
    updated_at=excluded.updated_at;
""";
            txCommand.Parameters.AddWithValue("$tcode", tcode);
            txCommand.Parameters.AddWithValue("$name", item.Name ?? "");
            txCommand.Parameters.AddWithValue("$stage", item.Stage ?? "");
            txCommand.Parameters.AddWithValue("$scriptFile", FirstNonEmpty(item.Script, item.ScriptFile));
            txCommand.Parameters.AddWithValue("$icon", item.Icon ?? "");
            txCommand.Parameters.AddWithValue("$paramsJson", JsonElementArrayToJson(item.Params));
            txCommand.Parameters.AddWithValue("$factoryRule", item.FactoryRule ?? "");
            txCommand.Parameters.AddWithValue("$fixedPlantsJson", fixedPlantsJson);
            txCommand.Parameters.AddWithValue("$defaultGroup", FirstNonEmpty(item.DefaultPlantGroup, item.DefaultGroup));
            txCommand.Parameters.AddWithValue("$automation", item.Automation ?? "");
            txCommand.Parameters.AddWithValue("$timeoutSeconds", timeoutSeconds);
            txCommand.Parameters.AddWithValue("$retryCount", retryCount);
            txCommand.Parameters.AddWithValue("$hasTimeoutSeconds", hasTimeoutSeconds ? 1 : 0);
            txCommand.Parameters.AddWithValue("$hasRetryCount", hasRetryCount ? 1 : 0);
            txCommand.Parameters.AddWithValue("$enabled", item.Enabled.GetValueOrDefault(true) ? 1 : 0);
            txCommand.Parameters.AddWithValue("$updatedAt", now);
            txCommand.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
INSERT INTO transaction_plant_rules(
    tcode, factory_rule, default_group, fixed_plants_json, selectable_group_ids_json,
    business_area_mode, business_areas_json, enabled, updated_at, updated_by
) VALUES(
    $tcode, $factoryRule, $defaultGroup, $fixedPlantsJson, $selectableGroupIdsJson,
    $businessAreaMode, $businessAreasJson, $enabled, $updatedAt, $updatedBy
)
ON CONFLICT(tcode) DO UPDATE SET
    factory_rule=excluded.factory_rule,
    default_group=excluded.default_group,
    fixed_plants_json=excluded.fixed_plants_json,
    selectable_group_ids_json=excluded.selectable_group_ids_json,
    business_area_mode=excluded.business_area_mode,
    business_areas_json=excluded.business_areas_json,
    enabled=excluded.enabled,
    updated_at=excluded.updated_at,
    updated_by=excluded.updated_by;
""";
        command.Parameters.AddWithValue("$tcode", tcode);
        command.Parameters.AddWithValue("$factoryRule", item.FactoryRule ?? "");
        command.Parameters.AddWithValue("$defaultGroup", FirstNonEmpty(item.DefaultPlantGroup, item.DefaultGroup));
        command.Parameters.AddWithValue("$fixedPlantsJson", fixedPlantsJson);
        command.Parameters.AddWithValue("$selectableGroupIdsJson", selectableGroupsJson);
        command.Parameters.AddWithValue("$businessAreaMode", FirstNonEmpty(item.BusinessAreaMode, "byPlant"));
        command.Parameters.AddWithValue("$businessAreasJson", businessAreasJson);
        command.Parameters.AddWithValue("$enabled", item.Enabled.GetValueOrDefault(true) ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", now);
        command.Parameters.AddWithValue("$updatedBy", FirstNonEmpty(item.UpdatedBy, "api"));
        command.ExecuteNonQuery();
        tx.Commit();
        return tcode;
    }

    static void SetTransactionPlantRuleEnabled(string tcode, bool enabled)
    {
        InitializeDatabase(seedFromScripts: true);
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
UPDATE transaction_plant_rules
SET enabled=$enabled, updated_at=$updatedAt, updated_by='api'
WHERE tcode=$tcode;
""";
        command.Parameters.AddWithValue("$tcode", tcode);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        command.ExecuteNonQuery();
    }

    static string UpsertNotificationRobot(NotificationRobotConfigRequest item, string routeId)
    {
        InitializeDatabase(seedFromScripts: true);
        string id = SanitizeConfigId(FirstNonEmpty(routeId, item.Id), "notification robot id");
        string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string webhookProtected = item.ClearWebhook ? "" : ProtectSecretIfPresent(FirstNonEmpty(item.Webhook, item.WebhookUrl, item.WebhookReplacement));
        string secretProtected = item.ClearSecret ? "" : ProtectSecretIfPresent(FirstNonEmpty(item.Secret, item.SecretReplacement));

        using var connection = OpenDatabaseConnection();
        using var tx = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = """
INSERT INTO notification_robots(
    id, name, robot_type, target_label, webhook_protected, secret_protected,
    enabled, updated_at, updated_by
) VALUES(
    $id, $name, $robotType, $targetLabel, $webhookProtected, $secretProtected,
    $enabled, $updatedAt, $updatedBy
)
ON CONFLICT(id) DO UPDATE SET
    name=excluded.name,
    robot_type=excluded.robot_type,
    target_label=excluded.target_label,
    webhook_protected=CASE
        WHEN $clearWebhook = 1 THEN ''
        WHEN excluded.webhook_protected <> '' THEN excluded.webhook_protected
        ELSE notification_robots.webhook_protected
    END,
    secret_protected=CASE
        WHEN $clearSecret = 1 THEN ''
        WHEN excluded.secret_protected <> '' THEN excluded.secret_protected
        ELSE notification_robots.secret_protected
    END,
    enabled=excluded.enabled,
    updated_at=excluded.updated_at,
    updated_by=excluded.updated_by;
""";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$name", FirstNonEmpty(item.Name, id));
            command.Parameters.AddWithValue("$robotType", FirstNonEmpty(item.RobotType, item.Type, "dingtalk"));
            command.Parameters.AddWithValue("$targetLabel", FirstNonEmpty(item.TargetLabel, item.Group));
            command.Parameters.AddWithValue("$webhookProtected", webhookProtected);
            command.Parameters.AddWithValue("$secretProtected", secretProtected);
            command.Parameters.AddWithValue("$clearWebhook", item.ClearWebhook ? 1 : 0);
            command.Parameters.AddWithValue("$clearSecret", item.ClearSecret ? 1 : 0);
            command.Parameters.AddWithValue("$enabled", item.Enabled.GetValueOrDefault(true) ? 1 : 0);
            command.Parameters.AddWithValue("$updatedAt", now);
            command.Parameters.AddWithValue("$updatedBy", FirstNonEmpty(item.UpdatedBy, "api"));
            command.ExecuteNonQuery();
        }

        if (item.Bindings.ValueKind == JsonValueKind.Array)
        {
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM notification_robot_bindings WHERE robot_id=$robotId";
                delete.Parameters.AddWithValue("$robotId", id);
                delete.ExecuteNonQuery();
            }

            foreach (JsonElement binding in item.Bindings.EnumerateArray())
            {
                string eventName = GetJsonString(binding, "eventName");
                if (string.IsNullOrWhiteSpace(eventName))
                    eventName = GetJsonString(binding, "event");

                using var bindingCommand = connection.CreateCommand();
                bindingCommand.Transaction = tx;
                bindingCommand.CommandText = """
INSERT OR IGNORE INTO notification_robot_bindings(
    robot_id, event_name, tcode, plant_group_id, enabled, updated_at
) VALUES(
    $robotId, $eventName, $tcode, $plantGroupId, $enabled, $updatedAt
);
""";
                bindingCommand.Parameters.AddWithValue("$robotId", id);
                bindingCommand.Parameters.AddWithValue("$eventName", eventName);
                bindingCommand.Parameters.AddWithValue("$tcode", FirstNonEmpty(GetJsonString(binding, "tcode"), GetJsonString(binding, "code")).ToUpperInvariant());
                bindingCommand.Parameters.AddWithValue("$plantGroupId", GetJsonString(binding, "plantGroupId"));
                bindingCommand.Parameters.AddWithValue("$enabled", GetJsonBool(binding, "enabled", defaultValue: true) ? 1 : 0);
                bindingCommand.Parameters.AddWithValue("$updatedAt", now);
                bindingCommand.ExecuteNonQuery();
            }
        }

        tx.Commit();
        return id;
    }

    static void SetNotificationRobotEnabled(string id, bool enabled)
    {
        InitializeDatabase(seedFromScripts: true);
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
UPDATE notification_robots
SET enabled=$enabled, updated_at=$updatedAt, updated_by='api'
WHERE id=$id;
""";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        command.ExecuteNonQuery();
    }

    static object LoadExecutionReport(HttpListenerRequest request)
    {
        InitializeDatabase(seedFromScripts: true);
        DateTime to = ParseReportDate(request.QueryString["to"], DateTime.Now);
        DateTime from = ParseReportDate(request.QueryString["from"], to.AddDays(-30));
        if (from > to)
            (from, to) = (to, from);

        double savedMinutesPerSuccess = 20;
        if (double.TryParse(request.QueryString["savedMinutesPerSuccess"], out double parsedSavedMinutes))
            savedMinutesPerSuccess = Math.Max(0, parsedSavedMinutes);

        string fromText = from.ToString("yyyy-MM-dd HH:mm:ss");
        string toText = to.ToString("yyyy-MM-dd HH:mm:ss");
        using var connection = OpenDatabaseConnection();

        long totalRuns = 0;
        long successRuns = 0;
        double avgDurationSeconds = 0;
        using (var summary = connection.CreateCommand())
        {
            summary.CommandText = """
SELECT COUNT(*),
       SUM(CASE WHEN status='success' THEN 1 ELSE 0 END),
       AVG(CASE WHEN duration_ms > 0 THEN duration_ms / 1000.0 ELSE NULL END)
FROM runs
WHERE COALESCE(NULLIF(finished_at, ''), queued_at) >= $from
  AND COALESCE(NULLIF(finished_at, ''), queued_at) <= $to;
""";
            summary.Parameters.AddWithValue("$from", fromText);
            summary.Parameters.AddWithValue("$to", toText);
            using var reader = summary.ExecuteReader();
            if (reader.Read())
            {
                totalRuns = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                successRuns = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                avgDurationSeconds = reader.IsDBNull(2) ? 0 : Math.Round(reader.GetDouble(2), 1);
            }
        }

        var transactionRanking = new List<object>();
        using (var ranking = connection.CreateCommand())
        {
            ranking.CommandText = """
SELECT r.transaction_code,
       COALESCE(NULLIF(t.name, ''), r.transaction_code) AS transaction_name,
       COUNT(*) AS total_runs,
       SUM(CASE WHEN r.status='success' THEN 1 ELSE 0 END) AS success_runs,
       AVG(CASE WHEN r.duration_ms > 0 THEN r.duration_ms / 1000.0 ELSE NULL END) AS avg_duration_seconds
FROM runs r
LEFT JOIN transactions t ON t.tcode = r.transaction_code
WHERE COALESCE(NULLIF(r.finished_at, ''), r.queued_at) >= $from
  AND COALESCE(NULLIF(r.finished_at, ''), r.queued_at) <= $to
GROUP BY r.transaction_code, transaction_name
ORDER BY total_runs DESC, success_runs DESC, r.transaction_code
LIMIT 20;
""";
            ranking.Parameters.AddWithValue("$from", fromText);
            ranking.Parameters.AddWithValue("$to", toText);
            using var reader = ranking.ExecuteReader();
            while (reader.Read())
            {
                long txTotal = reader.GetInt64(2);
                long txSuccess = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
                transactionRanking.Add(new
                {
                    transactionCode = reader.GetString(0),
                    transactionName = reader.GetString(1),
                    totalRuns = txTotal,
                    successRuns = txSuccess,
                    successRate = txTotal == 0 ? 0 : Math.Round(txSuccess * 1.0 / txTotal, 4),
                    avgDurationSeconds = reader.IsDBNull(4) ? 0 : Math.Round(reader.GetDouble(4), 1),
                    savedHours = Math.Round(txSuccess * savedMinutesPerSuccess / 60.0, 2)
                });
            }
        }

        var plantsByRun = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var statusByRun = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var durationByRun = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using (var plants = connection.CreateCommand())
        {
            plants.CommandText = """
SELECT r.run_id, r.status, COALESCE(NULLIF(r.duration_ms, 0), 0), rp.param_value
FROM runs r
JOIN run_params rp ON rp.run_id = r.run_id
WHERE LOWER(rp.param_key) IN ('plants', 'plant', 'werks', 'werkslist', 'plantlist')
  AND COALESCE(NULLIF(r.finished_at, ''), r.queued_at) >= $from
  AND COALESCE(NULLIF(r.finished_at, ''), r.queued_at) <= $to;
""";
            plants.Parameters.AddWithValue("$from", fromText);
            plants.Parameters.AddWithValue("$to", toText);
            using var reader = plants.ExecuteReader();
            while (reader.Read())
            {
                string runId = reader.GetString(0);
                statusByRun[runId] = reader.GetString(1);
                durationByRun[runId] = reader.GetInt64(2);
                if (!plantsByRun.TryGetValue(runId, out var runPlants))
                {
                    runPlants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    plantsByRun[runId] = runPlants;
                }

                foreach (string plant in NormalizeStringArray(reader.GetString(3)))
                    runPlants.Add(plant);
            }
        }

        var plantCounts = new Dictionary<string, PlantReportAccumulator>(StringComparer.OrdinalIgnoreCase);
        foreach (var runPlants in plantsByRun)
        {
            string status = statusByRun.TryGetValue(runPlants.Key, out string? value) ? value : "";
            long durationMs = durationByRun.TryGetValue(runPlants.Key, out long durationValue) ? durationValue : 0;
            foreach (string plant in runPlants.Value)
            {
                if (!plantCounts.TryGetValue(plant, out var item))
                {
                    item = new PlantReportAccumulator();
                    plantCounts[plant] = item;
                }

                item.TotalRuns++;
                if (status.Equals("success", StringComparison.OrdinalIgnoreCase))
                    item.SuccessRuns++;
                if (durationMs > 0)
                {
                    item.DurationTotalMs += durationMs;
                    item.DurationCount++;
                }
            }
        }

        var plantStats = plantCounts
            .OrderByDescending(p => p.Value.TotalRuns)
            .ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .Select(p => new
            {
                plant = p.Key,
                totalRuns = p.Value.TotalRuns,
                successRuns = p.Value.SuccessRuns,
                successRate = p.Value.TotalRuns == 0 ? 0 : Math.Round(p.Value.SuccessRuns * 1.0 / p.Value.TotalRuns, 4),
                avgDurationSeconds = p.Value.DurationCount == 0 ? 0 : Math.Round(p.Value.DurationTotalMs / 1000.0 / p.Value.DurationCount, 1),
                savedHours = Math.Round(p.Value.SuccessRuns * savedMinutesPerSuccess / 60.0, 2)
            })
            .ToList();

        return new
        {
            version = 1,
            source = "sqlite",
            database = DatabaseFilePath,
            period = new { from = fromText, to = toText },
            assumptions = new { savedMinutesPerSuccess },
            summary = new
            {
                totalRuns,
                successRuns,
                failedRuns = Math.Max(0, totalRuns - successRuns),
                successRate = totalRuns == 0 ? 0 : Math.Round(successRuns * 1.0 / totalRuns, 4),
                avgDurationSeconds,
                savedHours = Math.Round(successRuns * savedMinutesPerSuccess / 60.0, 2)
            },
            transactionRanking,
            plantStats
        };
    }

    static DateTime ParseReportDate(string? value, DateTime fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        return DateTime.TryParse(value, out DateTime parsed) ? parsed : fallback;
    }

    static object LoadScheduleTasks()
    {
        InitializeDatabase(seedFromScripts: true);
        var items = new List<object>();
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
SELECT id, name, tcode, plants_json, default_business_scope, cron, frequency, run_time,
       enabled, notify_enabled, notify_on_success, notify_on_failure, notify_target,
       params_json, created_at, updated_at, created_by, updated_by
FROM schedule_tasks
ORDER BY enabled DESC, updated_at DESC, id;
""";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            items.Add(ReadScheduleTask(reader));

        return new
        {
            version = 1,
            source = "sqlite",
            database = DatabaseFilePath,
            schedules = items
        };
    }

    static object? LoadScheduleTask(string id)
    {
        InitializeDatabase(seedFromScripts: true);
        id = SanitizeConfigId(id, "schedule id");
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
SELECT id, name, tcode, plants_json, default_business_scope, cron, frequency, run_time,
       enabled, notify_enabled, notify_on_success, notify_on_failure, notify_target,
       params_json, created_at, updated_at, created_by, updated_by
FROM schedule_tasks
WHERE id=$id;
""";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadScheduleTask(reader) : null;
    }

    static string UpsertScheduleTask(ScheduleTaskRequest item, string routeId)
    {
        InitializeDatabase(seedFromScripts: true);
        string tcode = SanitizeTCode(FirstNonEmpty(item.TCode, item.Code, item.TransactionCode)).ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(tcode))
            throw new InvalidOperationException("schedule tcode is required");

        string id = SanitizeConfigId(FirstNonEmpty(routeId, item.Id, $"sched-{tcode.ToLowerInvariant()}-{DateTime.Now:yyyyMMddHHmmss}"), "schedule id");
        string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string plantsJson = CsvToJsonArray(FirstNonEmpty(item.PlantsCsv, JsonElementArrayToCsv(item.Plants)));
        string paramsJson = item.Params.ValueKind == JsonValueKind.Object ? item.Params.GetRawText() : "{}";

        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO schedule_tasks(
    id, name, tcode, plants_json, default_business_scope, cron, frequency, run_time,
    enabled, notify_enabled, notify_on_success, notify_on_failure, notify_target,
    params_json, created_at, updated_at, created_by, updated_by
) VALUES(
    $id, $name, $tcode, $plantsJson, $defaultBusinessScope, $cron, $frequency, $runTime,
    $enabled, $notifyEnabled, $notifyOnSuccess, $notifyOnFailure, $notifyTarget,
    $paramsJson, $createdAt, $updatedAt, $createdBy, $updatedBy
)
ON CONFLICT(id) DO UPDATE SET
    name=excluded.name,
    tcode=excluded.tcode,
    plants_json=excluded.plants_json,
    default_business_scope=excluded.default_business_scope,
    cron=excluded.cron,
    frequency=excluded.frequency,
    run_time=excluded.run_time,
    enabled=excluded.enabled,
    notify_enabled=excluded.notify_enabled,
    notify_on_success=excluded.notify_on_success,
    notify_on_failure=excluded.notify_on_failure,
    notify_target=excluded.notify_target,
    params_json=excluded.params_json,
    updated_at=excluded.updated_at,
    updated_by=excluded.updated_by;
""";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$name", FirstNonEmpty(item.Name, tcode));
        command.Parameters.AddWithValue("$tcode", tcode);
        command.Parameters.AddWithValue("$plantsJson", plantsJson);
        command.Parameters.AddWithValue("$defaultBusinessScope", item.DefaultBusinessScope ?? "");
        command.Parameters.AddWithValue("$cron", item.Cron ?? "");
        command.Parameters.AddWithValue("$frequency", FirstNonEmpty(item.Frequency, string.IsNullOrWhiteSpace(item.Cron) ? "daily" : ""));
        command.Parameters.AddWithValue("$runTime", FirstNonEmpty(item.Time, item.RunTime));
        command.Parameters.AddWithValue("$enabled", item.Enabled.GetValueOrDefault(true) ? 1 : 0);
        command.Parameters.AddWithValue("$notifyEnabled", item.NotifyEnabled.GetValueOrDefault(false) ? 1 : 0);
        command.Parameters.AddWithValue("$notifyOnSuccess", item.NotifyOnSuccess.GetValueOrDefault(false) ? 1 : 0);
        command.Parameters.AddWithValue("$notifyOnFailure", item.NotifyOnFailure.GetValueOrDefault(true) ? 1 : 0);
        command.Parameters.AddWithValue("$notifyTarget", item.NotifyTarget ?? "");
        command.Parameters.AddWithValue("$paramsJson", paramsJson);
        command.Parameters.AddWithValue("$createdAt", now);
        command.Parameters.AddWithValue("$updatedAt", now);
        command.Parameters.AddWithValue("$createdBy", FirstNonEmpty(item.CreatedBy, item.UpdatedBy, "api"));
        command.Parameters.AddWithValue("$updatedBy", FirstNonEmpty(item.UpdatedBy, item.CreatedBy, "api"));
        command.ExecuteNonQuery();
        return id;
    }

    static void SetScheduleTaskEnabled(string id, bool enabled)
    {
        InitializeDatabase(seedFromScripts: true);
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
UPDATE schedule_tasks
SET enabled=$enabled, updated_at=$updatedAt, updated_by='api'
WHERE id=$id;
""";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        command.ExecuteNonQuery();
    }

    static object ReadScheduleTask(SqliteDataReader reader)
    {
        return new
        {
            id = reader.GetString(0),
            name = reader.GetString(1),
            tcode = reader.GetString(2),
            code = reader.GetString(2),
            plants = SafeJsonArray(reader.GetString(3)),
            defaultBusinessScope = reader.GetString(4),
            cron = reader.GetString(5),
            frequency = reader.GetString(6),
            time = reader.GetString(7),
            enabled = reader.GetInt32(8) == 1,
            notify = new
            {
                enabled = reader.GetInt32(9) == 1,
                onSuccess = reader.GetInt32(10) == 1,
                onFailure = reader.GetInt32(11) == 1,
                target = reader.GetString(12)
            },
            paramsJson = reader.GetString(13),
            createdAt = reader.GetString(14),
            updatedAt = reader.GetString(15),
            createdBy = reader.GetString(16),
            updatedBy = reader.GetString(17)
        };
    }

    static void NormalizeCreateRunParams(CreateRunRequest request)
    {
        request.Params ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string plants = FirstNonEmpty(
            GetParamValue(request.Params, "plants"),
            GetParamValue(request.Params, "werkslist"),
            GetParamValue(request.Params, "plantlist"),
            GetParamValue(request.Params, "plant"),
            GetParamValue(request.Params, "werks"));
        plants = NormalizeCsv(plants);
        if (!string.IsNullOrWhiteSpace(plants))
        {
            request.Params["plants"] = plants;
            request.Params["plant"] = FirstCsvValue(plants);
        }
    }

    static string GetParamValue(Dictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out string? value) ? value ?? "" : "";
    }

    static RunRecordView CreateRun(CreateRunRequest request)
    {
        InitializeDatabase(seedFromScripts: true);
        string tcode = SanitizeTCode(FirstNonEmpty(request.TransactionCode, request.TCode, request.Code)).ToUpperInvariant();
        var script = LoadScriptInfo(tcode);
        string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string rawRunId = $"RUN-{DateTime.Now:yyyyMMddHHmmss}-{tcode}-{Guid.NewGuid():N}";
        string runId = rawRunId[..Math.Min(56, rawRunId.Length)];

        request.TransactionCode = tcode;
        request.TCode = tcode;
        request.Code = tcode;
        NormalizeCreateRunParams(request);

        using var connection = OpenDatabaseConnection();
        using var tx = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
INSERT INTO runs(
    run_id, transaction_code, operator_id, operator_name, operator_dept, status, request_json,
    script_file, script_hash, source, notify_target, priority, max_attempts, queued_at
) VALUES(
    $runId, $tcode, $operatorId, $operatorName, $operatorDept, 'queued', $requestJson,
    $scriptFile, $scriptHash, $source, $notifyTarget, $priority, $maxAttempts, $queuedAt
);
""";
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$tcode", tcode);
        command.Parameters.AddWithValue("$operatorId", request.Operator.Id ?? "");
        command.Parameters.AddWithValue("$operatorName", request.Operator.Name ?? "");
        command.Parameters.AddWithValue("$operatorDept", request.Operator.Dept ?? "");
        command.Parameters.AddWithValue("$requestJson", JsonSerializer.Serialize(request, JsonOptions));
        command.Parameters.AddWithValue("$scriptFile", script.ScriptFile);
        command.Parameters.AddWithValue("$scriptHash", script.ScriptHash);
        command.Parameters.AddWithValue("$source", request.Source ?? "");
        command.Parameters.AddWithValue("$notifyTarget", request.NotifyTarget ?? "");
        command.Parameters.AddWithValue("$priority", request.Priority);
        command.Parameters.AddWithValue("$maxAttempts", request.MaxAttempts <= 0 ? 1 : request.MaxAttempts);
        command.Parameters.AddWithValue("$queuedAt", now);
        command.ExecuteNonQuery();

        foreach (var pair in request.Params)
        {
            using var paramCommand = connection.CreateCommand();
            paramCommand.Transaction = tx;
            paramCommand.CommandText = """
INSERT INTO run_params(run_id, param_key, param_value)
VALUES($runId, $key, $value)
ON CONFLICT(run_id, param_key) DO UPDATE SET param_value=excluded.param_value;
""";
            paramCommand.Parameters.AddWithValue("$runId", runId);
            paramCommand.Parameters.AddWithValue("$key", pair.Key);
            paramCommand.Parameters.AddWithValue("$value", pair.Value ?? "");
            paramCommand.ExecuteNonQuery();
        }

        tx.Commit();

        AppendRunLog(runId, "INFO", $"queued {tcode}");

        return new RunRecordView
        {
            RunId = runId,
            TransactionCode = tcode,
            OperatorId = request.Operator.Id ?? "",
            OperatorName = request.Operator.Name ?? "",
            OperatorDept = request.Operator.Dept ?? "",
            Status = "queued",
            RequestJson = JsonSerializer.Serialize(request, JsonOptions),
            ScriptFile = script.ScriptFile,
            ScriptHash = script.ScriptHash,
            QueuedAt = now
        };
    }

    static object LoadRuns(HttpListenerRequest request)
    {
        InitializeDatabase(seedFromScripts: true);
        int limit = DefaultRunListLimit;
        if (int.TryParse(request.QueryString["limit"], out int parsedLimit))
            limit = Math.Clamp(parsedLimit, 1, 200);

        string status = request.QueryString["status"] ?? "";
        var runs = new List<RunRecordView>();
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        if (string.IsNullOrWhiteSpace(status))
        {
            command.CommandText = """
SELECT run_id, transaction_code, operator_id, operator_name, operator_dept, status, request_json,
       sap_status_type, sap_status_text, message, script_file, script_hash,
       queued_at, started_at, finished_at, duration_ms,
       source, notify_target, priority, attempt, max_attempts, locked_by, locked_at
FROM runs
ORDER BY COALESCE(NULLIF(finished_at, ''), queued_at) DESC
LIMIT $limit;
""";
        }
        else
        {
            command.CommandText = """
SELECT run_id, transaction_code, operator_id, operator_name, operator_dept, status, request_json,
       sap_status_type, sap_status_text, message, script_file, script_hash,
       queued_at, started_at, finished_at, duration_ms,
       source, notify_target, priority, attempt, max_attempts, locked_by, locked_at
FROM runs
WHERE status=$status
ORDER BY queued_at
LIMIT $limit;
""";
            command.Parameters.AddWithValue("$status", status.ToLowerInvariant());
        }

        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            runs.Add(ReadRunRecord(reader));

        return new
        {
            version = 2,
            source = "sqlite",
            database = DatabaseFilePath,
            runs
        };
    }

    static RunRecordView? LoadRun(string runId, bool includeDetails)
    {
        InitializeDatabase(seedFromScripts: true);
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
SELECT run_id, transaction_code, operator_id, operator_name, operator_dept, status, request_json,
       sap_status_type, sap_status_text, message, script_file, script_hash,
       queued_at, started_at, finished_at, duration_ms,
       source, notify_target, priority, attempt, max_attempts, locked_by, locked_at
FROM runs
WHERE run_id=$runId;
""";
        command.Parameters.AddWithValue("$runId", runId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var run = ReadRunRecord(reader);
        if (includeDetails)
        {
            run.Logs = LoadRunLogs(runId);
            run.Files = LoadRunFiles(runId);
        }

        return run;
    }

    static void ProcessRunQueueLoop()
    {
        while (true)
        {
            try
            {
                var item = ClaimNextQueuedRun();
                if (item == null)
                {
                    Thread.Sleep(1500);
                    continue;
                }

                ExecuteQueuedRun(item);
            }
            catch (Exception ex)
            {
                Log($"队列工作线程异常: {ex}");
                Thread.Sleep(3000);
            }
        }
    }

    static QueuedRunWorkItem? ClaimNextQueuedRun()
    {
        InitializeDatabase(seedFromScripts: false);
        using var connection = OpenDatabaseConnection();
        using var tx = connection.BeginTransaction();

        string runId;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = """
SELECT run_id
FROM runs
WHERE status='queued'
ORDER BY priority DESC, queued_at
LIMIT 1;
""";
            runId = select.ExecuteScalar() as string ?? "";
        }

        if (string.IsNullOrWhiteSpace(runId))
        {
            tx.Commit();
            return null;
        }

        string startedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        using (var update = connection.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = """
UPDATE runs
SET status='running',
    started_at=$startedAt,
    locked_by=$lockedBy,
    locked_at=$startedAt,
    attempt=attempt + 1
WHERE run_id=$runId AND status='queued';
""";
            update.Parameters.AddWithValue("$runId", runId);
            update.Parameters.AddWithValue("$startedAt", startedAt);
            update.Parameters.AddWithValue("$lockedBy", ExecutorId);
            if (update.ExecuteNonQuery() != 1)
            {
                tx.Commit();
                return null;
            }
        }

        QueuedRunWorkItem? item = null;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = """
SELECT run_id, transaction_code, request_json, script_file
FROM runs
WHERE run_id=$runId;
""";
            select.Parameters.AddWithValue("$runId", runId);
            using var reader = select.ExecuteReader();
            if (reader.Read())
            {
                item = new QueuedRunWorkItem
                {
                    RunId = reader.GetString(0),
                    TransactionCode = reader.GetString(1),
                    RequestJson = reader.GetString(2),
                    ScriptFile = reader.GetString(3)
                };
            }
        }

        tx.Commit();
        if (item != null)
            AppendRunLog(item.RunId, "INFO", "queue worker claimed run");
        return item;
    }

    static void ExecuteQueuedRun(QueuedRunWorkItem item)
    {
        var started = DateTime.UtcNow;
        try
        {
            var request = JsonSerializer.Deserialize<CreateRunRequest>(item.RequestJson, new JsonSerializerOptions(JsonOptions)
            {
                PropertyNameCaseInsensitive = true
            }) ?? new CreateRunRequest { TransactionCode = item.TransactionCode };

            var query = BuildQueryFromRunRequest(request, item);
            var pars = BuildParams(query, PrimaryProtocolName);
            pars.RunId = item.RunId;
            Log($"队列执行: runId={item.RunId}, {DescribeParams(pars)}");

            var result = LaunchSapGuiAndExecute(pars);
            CompleteRun(item.RunId, result);
        }
        catch (Exception ex)
        {
            Log($"队列执行失败: runId={item.RunId}, {ex}");
            CompleteRun(item.RunId, FailedRunResult(ex.Message, started));
        }
    }

    static NameValueCollection BuildQueryFromRunRequest(CreateRunRequest request, QueuedRunWorkItem item)
    {
        var query = new NameValueCollection
        {
            ["action"] = "run",
            ["tcode"] = item.TransactionCode,
            ["script"] = FirstNonEmpty(item.ScriptFile, DefaultScriptForTCode(item.TransactionCode)),
            ["runid"] = item.RunId
        };

        foreach (var pair in request.Params)
            query[pair.Key.ToLowerInvariant()] = pair.Value ?? "";

        return query;
    }

    static void MarkRunStarted(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return;

        InitializeDatabase(seedFromScripts: true);
        string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
UPDATE runs
SET status='running',
    started_at=CASE WHEN started_at='' THEN $startedAt ELSE started_at END
WHERE run_id=$runId AND status IN ('queued', 'running');
""";
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$startedAt", now);
        command.ExecuteNonQuery();
        AppendRunLog(runId, "INFO", "executor started");
    }

    static void CompleteRun(string runId, RunResultRequest result)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return;

        InitializeDatabase(seedFromScripts: true);
        string status = NormalizeRunStatus(result.Status);
        string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        using var connection = OpenDatabaseConnection();
        using var tx = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = """
UPDATE runs
SET status=$status,
    sap_status_type=$sapStatusType,
    sap_status_text=$sapStatusText,
    message=$message,
    finished_at=$finishedAt,
    duration_ms=$durationMs
WHERE run_id=$runId;
""";
            command.Parameters.AddWithValue("$runId", runId);
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$sapStatusType", result.SapStatusType ?? "");
            command.Parameters.AddWithValue("$sapStatusText", result.SapStatusText ?? "");
            command.Parameters.AddWithValue("$message", result.Message ?? "");
            command.Parameters.AddWithValue("$finishedAt", now);
            command.Parameters.AddWithValue("$durationMs", result.DurationMs);
            command.ExecuteNonQuery();
        }

        using (var deleteLogs = connection.CreateCommand())
        {
            deleteLogs.Transaction = tx;
            deleteLogs.CommandText = "DELETE FROM run_result_logs WHERE run_id=$runId";
            deleteLogs.Parameters.AddWithValue("$runId", runId);
            deleteLogs.ExecuteNonQuery();
        }

        using (var deleteFiles = connection.CreateCommand())
        {
            deleteFiles.Transaction = tx;
            deleteFiles.CommandText = "DELETE FROM run_files WHERE run_id=$runId";
            deleteFiles.Parameters.AddWithValue("$runId", runId);
            deleteFiles.ExecuteNonQuery();
        }

        foreach (var line in result.Logs)
        {
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "INSERT INTO run_result_logs(run_id, level, message) VALUES($runId, $level, $message)";
            command.Parameters.AddWithValue("$runId", runId);
            command.Parameters.AddWithValue("$level", FirstNonEmpty(line.Level, "INFO"));
            command.Parameters.AddWithValue("$message", line.Message ?? "");
            command.ExecuteNonQuery();
        }

        foreach (var file in result.Files)
        {
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
INSERT INTO run_files(run_id, file_type, file_name, file_path, file_size)
VALUES($runId, $type, $name, $path, $size);
""";
            command.Parameters.AddWithValue("$runId", runId);
            command.Parameters.AddWithValue("$type", FirstNonEmpty(file.Type, "output"));
            command.Parameters.AddWithValue("$name", file.Name ?? "");
            command.Parameters.AddWithValue("$path", file.Path ?? "");
            command.Parameters.AddWithValue("$size", file.Size);
            command.ExecuteNonQuery();
        }

        tx.Commit();
        Log($"run result updated: {runId}, status={status}, sap={result.SapStatusType}");
    }

    static TransactionScriptInfo LoadScriptInfo(string tcode)
    {
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT script_file, script_hash FROM transactions WHERE tcode=$tcode";
        command.Parameters.AddWithValue("$tcode", tcode.ToUpperInvariant());
        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            return new TransactionScriptInfo
            {
                ScriptFile = reader.GetString(0),
                ScriptHash = reader.GetString(1)
            };
        }

        return new TransactionScriptInfo { ScriptFile = $"{tcode.ToUpperInvariant()}.vbs" };
    }

    static RunRecordView ReadRunRecord(SqliteDataReader reader)
    {
        return new RunRecordView
        {
            RunId = reader.GetString(0),
            TransactionCode = reader.GetString(1),
            OperatorId = reader.GetString(2),
            OperatorName = reader.GetString(3),
            OperatorDept = reader.GetString(4),
            Status = reader.GetString(5),
            RequestJson = reader.GetString(6),
            SapStatusType = reader.GetString(7),
            SapStatusText = reader.GetString(8),
            Message = reader.GetString(9),
            ScriptFile = reader.GetString(10),
            ScriptHash = reader.GetString(11),
            QueuedAt = reader.GetString(12),
            StartedAt = reader.GetString(13),
            FinishedAt = reader.GetString(14),
            DurationMs = reader.GetInt64(15),
            Source = reader.GetString(16),
            NotifyTarget = reader.GetString(17),
            Priority = reader.GetInt32(18),
            Attempt = reader.GetInt32(19),
            MaxAttempts = reader.GetInt32(20),
            LockedBy = reader.GetString(21),
            LockedAt = reader.GetString(22)
        };
    }

    static List<RunLogLine> LoadRunLogs(string runId)
    {
        var logs = new List<RunLogLine>();
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
SELECT level, message, created_at
FROM run_result_logs
WHERE run_id=$runId
ORDER BY id;
""";
        command.Parameters.AddWithValue("$runId", runId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            logs.Add(new RunLogLine
            {
                Level = reader.GetString(0),
                Message = reader.GetString(1),
                CreatedAt = reader.GetString(2)
            });
        }

        return logs;
    }

    static List<RunFile> LoadRunFiles(string runId)
    {
        var files = new List<RunFile>();
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
SELECT file_type, file_name, file_path, file_size, created_at
FROM run_files
WHERE run_id=$runId
ORDER BY id;
""";
        command.Parameters.AddWithValue("$runId", runId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            files.Add(new RunFile
            {
                Type = reader.GetString(0),
                Name = reader.GetString(1),
                Path = reader.GetString(2),
                Size = reader.GetInt64(3),
                CreatedAt = reader.GetString(4)
            });
        }

        return files;
    }

    static void AppendRunLog(string runId, string level, string message)
    {
        try
        {
            using var connection = OpenDatabaseConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO run_result_logs(run_id, level, message) VALUES($runId, $level, $message)";
            command.Parameters.AddWithValue("$runId", runId);
            command.Parameters.AddWithValue("$level", level);
            command.Parameters.AddWithValue("$message", message);
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Log($"append run log failed: {runId}, {ex.Message}");
        }
    }

    static string TransactionParamsToJson(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
            return "[]";

        var keys = value.EnumerateArray()
            .Select(ReadParamKey)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return JsonSerializer.Serialize(keys, JsonOptions);
    }

    static string ReadParamKey(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? "";

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (string key in new[] { "key", "name", "paramKey" })
            {
                if (value.TryGetProperty(key, out JsonElement prop))
                    return JsonValueToString(prop);
            }
        }

        return "";
    }

    static string JsonElementArrayToCsv(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
            return "";

        return string.Join(",", value.EnumerateArray().Select(JsonValueToString).Where(v => !string.IsNullOrWhiteSpace(v)));
    }

    static string JsonElementArrayToJson(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.Array ? value.GetRawText() : "[]";
    }

    static string[] SafeJsonArray(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<string>();

        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    static string NormalizeRunStatus(string status)
    {
        string value = (status ?? "").Trim().ToLowerInvariant();
        return value switch
        {
            "queued" or "running" or "success" or "failed" or "canceled" => value,
            "ok" or "done" => "success",
            "error" or "abort" => "failed",
            _ => "failed"
        };
    }

    static string FindTransactionConfigPath()
    {
        string[] candidates =
        {
            Path.Combine(RuntimeTransactionsDirectory, "transaction-config.json"),
            Path.Combine(ExeDirectory, "transactions", "transaction-config.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "transactions", "transaction-config.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "网页启动登录", "transactions", "transaction-config.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SapRpaLauncher", "transactions", "transaction-config.json")
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    static string ReadScriptTextIfExists(string script, string tcode)
    {
        string? path = FindExternalScript(script, tcode);
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path)
            ? File.ReadAllText(path, Encoding.UTF8)
            : "";
    }

    static Dictionary<string, string> ExtractScriptMetadata(string scriptText)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(scriptText))
            return result;

        foreach (Match match in Regex.Matches(scriptText, @"(?im)^\s*'\s*@([A-Za-z0-9_.-]+)\s*=\s*(.+?)\s*$"))
            result[match.Groups[1].Value.Trim()] = match.Groups[2].Value.Trim();

        return result;
    }

    static string GetMetadataFixedPlants(string tcode, Dictionary<string, string> metadata)
    {
        if (tcode.Equals("ZFI072A", StringComparison.OrdinalIgnoreCase))
            return "";

        return metadata.TryGetValue("fixedPlants", out string? plants) ? plants ?? "" : "";
    }

    static string GetJsonString(JsonElement item, string property)
    {
        return item.TryGetProperty(property, out JsonElement value) && value.ValueKind != JsonValueKind.Null
            ? JsonValueToString(value)
            : "";
    }

    static int GetJsonInt(JsonElement item, string property, int defaultValue)
    {
        if (!item.TryGetProperty(property, out JsonElement value))
            return defaultValue;

        int parsed = value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out int number) ? number : defaultValue,
            JsonValueKind.String => int.TryParse(value.GetString(), out int number) ? number : defaultValue,
            _ => defaultValue
        };
        return NormalizeNonNegative(parsed);
    }

    static string JsonArrayPropertyToJson(JsonElement item, string property)
    {
        return item.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? value.GetRawText()
            : "[]";
    }

    static string JsonArrayToCsv(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
            return "";

        return string.Join(",", value.EnumerateArray().Select(JsonValueToString).Where(v => !string.IsNullOrWhiteSpace(v)));
    }

    static bool GetJsonBool(JsonElement item, string property, bool defaultValue)
    {
        if (!item.TryGetProperty(property, out JsonElement value))
            return defaultValue;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(value.GetString(), out bool parsed) ? parsed : defaultValue,
            JsonValueKind.Number => value.TryGetInt32(out int parsed) ? parsed != 0 : defaultValue,
            _ => defaultValue
        };
    }

    static string CsvToJsonArray(string value)
    {
        string[] items = NormalizeCsvPreserveOrder(value).Split(',', StringSplitOptions.RemoveEmptyEntries);
        return JsonSerializer.Serialize(items, JsonOptions);
    }

    static string ProtectSecretIfPresent(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        byte[] protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    static string Sha256Hex(string value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    static RunResultRequest LaunchSapGuiAndExecute(SapRunParams p)
    {
        var started = DateTime.UtcNow;
        if (p.TCode.Equals("ZFI072A", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(p.Plants))
        {
            string message = "ERROR=ZFI072A requires plants from launcher input/API selection";
            Console.Error.WriteLine(message);
            Log(message);
            return FailedRunResult(message, started);
        }

        if (HasReadySapSession())
        {
            Log("检测到已登录 SAP GUI 会话，跳过 sapshcut 登录，直接执行事务码脚本");
        }
        else
        {
            string? sapshcut = FindSapshcut();
            if (string.IsNullOrEmpty(sapshcut))
            {
                Console.Error.WriteLine("未找到 sapshcut.exe，请安装 SAP GUI");
                Log("未找到 sapshcut.exe，请安装 SAP GUI");
                return FailedRunResult("未找到 sapshcut.exe，请安装 SAP GUI", started);
            }

            var args = new[]
            {
                $"-sysname={EscapeArg(p.System)}",
                $"-client={EscapeArg(p.Client)}",
                $"-user={EscapeArg(p.User)}",
                $"-pw={EscapeArg(p.Password)}",
                "-GuiSize=Maximized",
                $"-language={EscapeArg(p.Language)}"
            };

            var startInfo = new ProcessStartInfo
            {
                FileName = sapshcut,
                UseShellExecute = false,
                CreateNoWindow = false
            };
            foreach (string arg in args)
                startInfo.ArgumentList.Add(arg);

            Log($"未检测到可用 SAP GUI 会话，启动 SAP GUI: path={sapshcut}, args={MaskSapArgs(string.Join(" ", args))}");
            Process.Start(startInfo);

            Log("SAP GUI 已启动，3 秒后开始执行 VBS 自动化");
            Thread.Sleep(3000);
        }

        try
        {
            var result = ExecuteViaGuiScripting(p);
            result.DurationMs = EnsureDuration(result.DurationMs, started);
            Console.WriteLine($"{p.TCode} 执行完成");
            Log($"{p.TCode} 执行完成");
            return result;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{p.TCode} 执行失败: {ex.Message}");
            Log($"{p.TCode} 执行失败: {ex}");
            return FailedRunResult(ex.Message, started);
        }
    }

    static bool HasReadySapSession()
    {
        string probeFile = Path.Combine(Path.GetTempPath(), $"sap_rpa_probe_{Guid.NewGuid():N}.vbs");
        string probeScript = """
On Error Resume Next
Dim SapGuiAuto, application, connection, session
Set SapGuiAuto = GetObject("SAPGUI")
If Err.Number <> 0 Then
   WScript.Echo "NO: SAPGUI object not found"
   WScript.Quit 1
End If
Set application = SapGuiAuto.GetScriptingEngine
If Err.Number <> 0 Or Not IsObject(application) Or application.Children.Count = 0 Then
   WScript.Echo "NO: scripting engine or connection not ready"
   WScript.Quit 2
End If
Set connection = application.Children(0)
If Err.Number <> 0 Or Not IsObject(connection) Or connection.Children.Count = 0 Then
   WScript.Echo "NO: connection/session not ready"
   WScript.Quit 3
End If
Set session = connection.Children(0)
If Err.Number <> 0 Or Not IsObject(session) Then
   WScript.Echo "NO: session not ready"
   WScript.Quit 4
End If
Err.Clear
Dim okcd
Set okcd = session.findById("wnd[0]/tbar[0]/okcd")
If Err.Number <> 0 Or Not IsObject(okcd) Then
   WScript.Echo "NO: command field not ready"
   WScript.Quit 5
End If
WScript.Echo "OK: user=" & session.Info.User & ", transaction=" & session.Info.Transaction
WScript.Quit 0
""";

        try
        {
            File.WriteAllText(probeFile, probeScript, Encoding.Default);
            var psi = new ProcessStartInfo("cscript.exe", $"//T:8 //nologo \"{probeFile}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
                return false;

            proc.WaitForExit(10_000);
            string output = proc.StandardOutput.ReadToEnd().Trim();
            string error = proc.StandardError.ReadToEnd().Trim();
            string merged = string.Join(" ", new[] { output, error }.Where(x => !string.IsNullOrWhiteSpace(x)));
            Log($"SAP 会话探测: exit={proc.ExitCode}, {merged}");
            return proc.ExitCode == 0 && output.StartsWith("OK:", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log($"SAP 会话探测失败: {ex.Message}");
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(probeFile))
                    File.Delete(probeFile);
            }
            catch { }
        }
    }

    static string EscapeArg(string value)
    {
        return value.Replace("\"", "");
    }

    static string? FindSapshcut()
    {
        string[] candidates =
        {
            @"C:\Program Files (x86)\SAP\FrontEnd\SAPgui\sapshcut.exe",
            @"C:\Program Files\SAP\FrontEnd\SAPgui\sapshcut.exe",
            @"C:\SAP\FrontEnd\SAPgui\sapshcut.exe",
            @"C:\software\SAPgui\sapshcut.exe",
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        try
        {
            var psi = new ProcessStartInfo("where", "sapshcut.exe")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                string output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(3000);
                if (!string.IsNullOrEmpty(output))
                {
                    string first = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                    if (File.Exists(first))
                        return first;
                }
            }
        }
        catch { }

        return null;
    }

    static RunResultRequest ExecuteViaGuiScripting(SapRunParams p)
    {
        var started = DateTime.UtcNow;
        string template = ReadTransactionScript(p);
        string effectivePlants = p.Plants;
        string scriptFixedPlants = ExtractScriptMetadataValue(template, "fixedPlants");
        if (!p.TCode.Equals("ZFI072A", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(effectivePlants) &&
            !string.IsNullOrWhiteSpace(scriptFixedPlants))
        {
            effectivePlants = NormalizeCsvPreserveOrder(scriptFixedPlants);
            Log($"script fixedPlants metadata used because request plants are empty: {effectivePlants}");
        }

        string vbsScript = template
            .Replace("{OK_CODE}", VbsEscape(p.TCode))
            .Replace("{SCRIPT_MODE}", VbsEscape(p.Script))
            .Replace("{FIELD1_NAME}", VbsEscape(p.Field1Name))
            .Replace("{FIELD1_VALUE}", VbsEscape(p.Field1Value))
            .Replace("{FIELD2_NAME}", VbsEscape(p.Field2Name))
            .Replace("{FIELD2_VALUE}", VbsEscape(p.Field2Value))
            .Replace("{PLANTS}", VbsEscape(effectivePlants))
            .Replace("{BUSINESS_AREAS}", VbsEscape(p.BusinessAreas))
            .Replace("{FACTORY_GROUP}", VbsEscape(p.FactoryGroup))
            .Replace("{RUN_STRATEGY}", VbsEscape(p.RunStrategy))
            .Replace("{PERIOD}", VbsEscape(p.Period))
            .Replace("{YEAR}", VbsEscape(p.Year))
            .Replace("{WEEK}", VbsEscape(p.Week))
            .Replace("{WEEK_END}", VbsEscape(p.WeekEnd))
            .Replace("{CARET_POS}", string.IsNullOrWhiteSpace(p.CaretPos) ? "0" : p.CaretPos)
            .Replace("{BUTTON_ID}", VbsEscape(p.ButtonId));

        string tmpFile = Path.Combine(Path.GetTempPath(), $"sap_rpa_{p.TCode}_{Guid.NewGuid():N}.vbs");
        bool keepTempFile = false;
        try
        {
            File.WriteAllText(tmpFile, vbsScript, Encoding.Unicode);
            Log($"执行 VBS: {tmpFile}, tcode={p.TCode}, script={p.Script}, plants={effectivePlants}");

            var psi = new ProcessStartInfo("cscript.exe", $"//T:35 //nologo \"{tmpFile}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(70_000);
            string stdOut = proc?.StandardOutput.ReadToEnd() ?? string.Empty;
            string stdErr = proc?.StandardError.ReadToEnd() ?? string.Empty;
            string mergedOutput = string.Join(Environment.NewLine,
                new[] { stdOut.Trim(), stdErr.Trim() }.Where(s => !string.IsNullOrWhiteSpace(s)));

            if (!string.IsNullOrWhiteSpace(stdOut))
            {
                Console.WriteLine(stdOut.Trim());
                Log($"VBS 输出: {stdOut.Trim()}");
            }

            if (!string.IsNullOrWhiteSpace(stdErr))
            {
                Console.WriteLine(stdErr.Trim());
                Log($"VBS 错误输出: {stdErr.Trim()}");
            }

            if (proc?.ExitCode != 0 && proc?.ExitCode != null)
            {
                Console.WriteLine($"VBS 退出码: {proc.ExitCode}");
                Log($"VBS 退出码: {proc.ExitCode}");
                keepTempFile = true;
                var failed = BuildRunResultFromVbs(stdOut, stdErr, proc.ExitCode, started);
                if (string.IsNullOrWhiteSpace(failed.Message))
                    failed.Message = $"VBS 执行失败，退出码 {proc.ExitCode}";
                failed.Status = "failed";
                failed.Logs.Add(new RunLogLine
                {
                    Level = "ERROR",
                    Message = $"VBS exit code {proc.ExitCode}; temp script retained: {tmpFile}"
                });
                return failed;
            }

            if (stdOut.Contains("ERROR:", StringComparison.OrdinalIgnoreCase) ||
                stdErr.Contains("ERROR:", StringComparison.OrdinalIgnoreCase))
            {
                keepTempFile = true;
                Log($"VBS 返回错误，保留脚本文件: {tmpFile}");
                var failed = BuildRunResultFromVbs(stdOut, stdErr, proc?.ExitCode ?? 0, started);
                failed.Status = "failed";
                if (string.IsNullOrWhiteSpace(failed.Message))
                    failed.Message = string.IsNullOrWhiteSpace(mergedOutput)
                        ? $"VBS 返回错误，脚本已保留: {tmpFile}"
                        : mergedOutput;
                failed.Logs.Add(new RunLogLine
                {
                    Level = "ERROR",
                    Message = $"temp script retained: {tmpFile}"
                });
                return failed;
            }

            if (!stdOut.Contains("INFO: transaction script executed", StringComparison.OrdinalIgnoreCase))
            {
                keepTempFile = true;
                Log($"VBS 未返回成功标记，保留脚本文件: {tmpFile}");
                var failed = BuildRunResultFromVbs(stdOut, stdErr, proc?.ExitCode ?? 0, started);
                failed.Status = "failed";
                failed.Message = string.IsNullOrWhiteSpace(mergedOutput)
                    ? $"VBS 未返回成功标记，脚本已保留: {tmpFile}"
                    : $"VBS 未返回成功标记: {mergedOutput}";
                failed.Logs.Add(new RunLogLine
                {
                    Level = "ERROR",
                    Message = $"temp script retained: {tmpFile}"
                });
                return failed;
            }

            return BuildRunResultFromVbs(stdOut, stdErr, proc?.ExitCode ?? 0, started);
        }
        finally
        {
            try
            {
                if (!keepTempFile && File.Exists(tmpFile))
                    File.Delete(tmpFile);
            }
            catch { }
        }
    }

    static string ReadTransactionScript(SapRunParams p)
    {
        string? externalScript = FindExternalScript(p.Script, p.TCode);
        if (!string.IsNullOrWhiteSpace(externalScript))
        {
            Log($"加载外部事务码脚本: {externalScript}");
            return File.ReadAllText(externalScript, Encoding.UTF8);
        }

        if (!p.Script.Equals("openOnly", StringComparison.OrdinalIgnoreCase) &&
            !p.Script.Equals("zck", StringComparison.OrdinalIgnoreCase))
        {
            Log($"未找到外部脚本 {p.Script}，回退到通用模板打开事务码");
        }

        return ReadEmbeddedTemplate("transaction_template.vbs");
    }

    static string ExtractScriptMetadataValue(string scriptText, string key)
    {
        if (string.IsNullOrWhiteSpace(scriptText) || string.IsNullOrWhiteSpace(key))
            return "";

        var match = Regex.Match(
            scriptText,
            @"(?im)^\s*'\s*@" + Regex.Escape(key) + @"\s*=\s*(.+?)\s*$");
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }

    static string? FindExternalScript(string script, string tcode)
    {
        string fileName = NormalizeScriptFileName(script, tcode);
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        string[] roots =
        {
            RuntimeTransactionsDirectory,
            Path.Combine(ExeDirectory, "transactions"),
            Path.Combine(Directory.GetCurrentDirectory(), "transactions"),
            Path.Combine(Directory.GetCurrentDirectory(), "网页启动登录", "transactions"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SapRpaLauncher", "transactions")
        };

        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string candidate = Path.Combine(root, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    static string NormalizeScriptFileName(string script, string tcode)
    {
        string raw = FirstNonEmpty(script, $"{tcode}.vbs").Trim();
        if (raw.Equals("openOnly", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("zck", StringComparison.OrdinalIgnoreCase))
            return "";

        string fileName = Path.GetFileName(raw);
        if (!fileName.EndsWith(".vbs", StringComparison.OrdinalIgnoreCase))
            fileName += ".vbs";

        if (!Regex.IsMatch(fileName, @"^[A-Za-z0-9_.-]{1,80}\.vbs$"))
            throw new ArgumentException($"脚本文件名不合法: {raw}");

        return fileName;
    }

    static string ReadEmbeddedTemplate(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        string resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new Exception($"未找到嵌入的 VBS 模板资源 {fileName}");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    static string VbsEscape(string value)
    {
        return (value ?? "").Replace("\"", "\"\"");
    }

    static int RunSelfTest()
    {
        Console.WriteLine("=== SapWebLauncher 自测试 ===\n");
        int passed = 0, failed = 0;

        void Check(string name, bool ok, string detail)
        {
            Console.WriteLine($"[{name}] {(ok ? "PASS" : "FAIL")} - {detail}");
            if (ok) passed++; else failed++;
        }

        {
            string uri = "sap-rpa://run?action=run&tcode=ZFI019NL&script=openOnly&plants=1022,1024&businessAreas=2900,3960";
            var q = ParseUri(uri);
            Check("新协议URI", q["action"] == "run" && q["tcode"] == "ZFI019NL" && q["plants"] == "1022,1024" && q["businessareas"] == "2900,3960", uri);
        }

        {
            string uri = "sap-rpa://run?user=MYUSER&pw=MYPASS&payload=%7B%22tCode%22%3A%22ZFI019NL%22%2C%22plants%22%3A%5B%221022%22%2C%221024%22%5D%2C%22businessAreas%22%3A%5B%222900%22%2C%223960%22%5D%7D";
            var q = ParseUri(uri);
            MergePayload(q);
            var p = BuildParams(q, PrimaryProtocolName, new SapLocalConfig
            {
                System = "TEST_SYSTEM",
                Client = "TEST_CLIENT",
                User = "TEST_USER",
                Password = "TEST_PASSWORD",
                Language = "ZH"
            });
            Check("payload兼容", p.TCode == "ZFI019NL" && p.Plants == "1022,1024" && p.BusinessAreas == "2900,3960", $"tcode={p.TCode}, plants={p.Plants}, businessAreas={p.BusinessAreas}");
        }

        {
            var q = new NameValueCollection
            {
                ["tcode"] = "ZFI072A",
                ["script"] = "openOnly",
                ["factorygroup"] = "PINGHU_ALL"
            };
            var p = BuildParams(q, PrimaryProtocolName, new SapLocalConfig
            {
                System = "TEST_SYSTEM",
                Client = "TEST_CLIENT",
                User = "TEST_USER",
                Password = "TEST_PASSWORD",
                Language = "ZH"
            });
            bool ok = p.Script.Equals("ZFI072A.vbs", StringComparison.OrdinalIgnoreCase) &&
                      string.IsNullOrWhiteSpace(p.Plants) &&
                      p.FactoryGroup.Equals("PINGHU_ALL", StringComparison.OrdinalIgnoreCase);
            Check("ZFI072A uri requires plants", ok, $"script={p.Script}, plants={p.Plants}, factoryGroup={p.FactoryGroup}");
        }

        {
            var q = new NameValueCollection
            {
                ["tcode"] = "ZFI072A",
                ["script"] = "openOnly",
                ["plants"] = "1022,1024"
            };
            var p = BuildParams(q, PrimaryProtocolName, new SapLocalConfig
            {
                System = "TEST_SYSTEM",
                Client = "TEST_CLIENT",
                User = "TEST_USER",
                Password = "TEST_PASSWORD",
                Language = "ZH"
            });
            bool ok = p.Script.Equals("ZFI072A.vbs", StringComparison.OrdinalIgnoreCase) &&
                      p.Plants.Equals("1022,1024", StringComparison.OrdinalIgnoreCase);
            Check("ZFI072A openOnly guard", ok, $"script={p.Script}, plants={p.Plants}");
        }

        {
            var request = new CreateRunRequest
            {
                TransactionCode = "ZFI072A",
                Params = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["factoryGroup"] = "PINGHU_ALL"
                }
            };
            NormalizeCreateRunParams(request);
            bool ok = string.IsNullOrWhiteSpace(GetParamValue(request.Params, "plants"));
            Check("ZFI072A run requires explicit plants", ok, $"plants={GetParamValue(request.Params, "plants")}");
        }

        {
            var request = new CreateRunRequest
            {
                TransactionCode = "ZFI072A",
                Params = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["factoryGroup"] = "PINGHU_ALL",
                    ["plants"] = "5021, 9301, 1101, 207M"
                }
            };
            NormalizeCreateRunParams(request);
            bool ok = GetParamValue(request.Params, "plants").Equals("5021,9301,1101,207M", StringComparison.OrdinalIgnoreCase) &&
                      GetParamValue(request.Params, "plant").Equals("5021", StringComparison.OrdinalIgnoreCase);
            Check("ZFI072A run explicit plants", ok, $"plants={GetParamValue(request.Params, "plants")}, plant={GetParamValue(request.Params, "plant")}");
        }

        {
            var q = new NameValueCollection
            {
                ["system"] = "URLSYS",
                ["client"] = "630",
                ["user"] = "URLUSER",
                ["pw"] = "URLPASS",
                ["lang"] = "EN",
                ["sysnr"] = "00"
            };
            var local = new SapLocalConfig
            {
                System = "TEST_SYSTEM",
                Client = "TEST_CLIENT",
                User = "TEST_USER",
                Password = "TEST_PASSWORD",
                Language = "ZH",
                SysNr = "TEST_SYSNR"
            };
            var p = BuildParams(q, PrimaryProtocolName, local);
            bool ok = p.System == "TEST_SYSTEM" && p.Client == "TEST_CLIENT" && p.User == "TEST_USER" &&
                      p.Password == "TEST_PASSWORD" && p.Language == "ZH" && p.SysNr == "TEST_SYSNR";
            Check("本机配置优先", ok, $"system={p.System}, client={p.Client}, user={p.User}, lang={p.Language}, sysnr={p.SysNr}");
        }

        {
            var asm = Assembly.GetExecutingAssembly();
            var names = asm.GetManifestResourceNames();
            bool found = names.Any(n => n.EndsWith("transaction_template.vbs", StringComparison.OrdinalIgnoreCase));
            Check("VBS嵌入", found, $"资源数={names.Length}");
        }

        {
            string template = ReadEmbeddedTemplate("transaction_template.vbs");
            string result = template
                .Replace("{OK_CODE}", "ZFI019NL")
                .Replace("{SCRIPT_MODE}", "openOnly")
                .Replace("{FIELD1_NAME}", "")
                .Replace("{FIELD1_VALUE}", "")
                .Replace("{FIELD2_NAME}", "")
                .Replace("{FIELD2_VALUE}", "")
                .Replace("{PLANTS}", "1022,1024")
                .Replace("{BUSINESS_AREAS}", "2900,3960")
                .Replace("{FACTORY_GROUP}", "PINGHU_30")
                .Replace("{RUN_STRATEGY}", "byPlant")
                .Replace("{YEAR}", "2026")
                .Replace("{WEEK}", "23")
                .Replace("{PERIOD}", "2026-W23")
                .Replace("{WEEK_END}", "2026-06-07")
                .Replace("{CARET_POS}", "0")
                .Replace("{BUTTON_ID}", "");
            bool ok = result.Contains("ZFI019NL") &&
                      !Regex.IsMatch(result, @"\{[A-Z0-9_]+\}") &&
                      result.Contains("SAP command field is not ready") &&
                      result.Contains("SAP rejected transaction") &&
                      result.Contains("transaction script executed");
            Check("VBS替换", ok, $"模板 {template.Length} 字节 -> {result.Length} 字节");
        }

        {
            string secret = "TEST_SECRET_VALUE";
            byte[] protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser);
            string plainText = Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser));
            Check("DPAPI密码保护", plainText == secret, "CurrentUser protect/unprotect");
        }

        {
            string runtimeRoot = Path.GetFullPath(RuntimeRoot);
            bool ok = Path.GetFullPath(DatabaseFilePath).StartsWith(runtimeRoot, StringComparison.OrdinalIgnoreCase) &&
                      Path.GetFullPath(LogFilePath).StartsWith(runtimeRoot, StringComparison.OrdinalIgnoreCase) &&
                      Path.GetFullPath(OutputDirectory).StartsWith(runtimeRoot, StringComparison.OrdinalIgnoreCase) &&
                      Path.GetFullPath(ConfigFilePath).StartsWith(Path.GetFullPath(LocalConfigDirectory), StringComparison.OrdinalIgnoreCase);
            Check("V2 runtime root", ok, $"runtime={RuntimeRoot}, db={DatabaseFilePath}, config={ConfigFilePath}");
        }

        Console.WriteLine($"\n=== 总计: {passed} PASS, {failed} FAIL, {(failed == 0 ? "全部通过" : "有失败项")} ===");
        return failed == 0 ? 0 : 1;
    }

    static RunResultRequest BuildRunResultFromVbs(string stdout, string stderr, int exitCode, DateTime started)
    {
        var result = new RunResultRequest
        {
            Status = exitCode == 0 ? "success" : "failed",
            DurationMs = EnsureDuration(0, started)
        };

        foreach (string rawLine in SplitLines(stdout))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (TryReadOutputKey(line, "STATUS_TYPE", out string statusType))
            {
                result.SapStatusType = statusType;
                result.Logs.Add(new RunLogLine { Level = "INFO", Message = line });
            }
            else if (TryReadOutputKey(line, "STATUS_TEXT", out string statusText))
            {
                result.SapStatusText = statusText;
                result.Logs.Add(new RunLogLine { Level = "INFO", Message = line });
            }
            else if (TryReadOutputKey(line, "OUTPUT_FILE", out string outputFile))
            {
                result.Files.Add(BuildRunFile(outputFile));
                result.Logs.Add(new RunLogLine { Level = "INFO", Message = line });
            }
            else if (TryReadOutputKey(line, "ERROR", out string errorValue))
            {
                result.Status = "failed";
                result.Logs.Add(new RunLogLine { Level = "ERROR", Message = errorValue });
            }
            else if (line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
            {
                result.Status = "failed";
                result.Logs.Add(new RunLogLine { Level = "ERROR", Message = line });
            }
            else if (line.StartsWith("WARN:", StringComparison.OrdinalIgnoreCase))
            {
                result.Logs.Add(new RunLogLine { Level = "WARN", Message = line });
            }
            else
            {
                result.Logs.Add(new RunLogLine { Level = "INFO", Message = line });
            }
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            result.Status = "failed";
            foreach (string line in SplitLines(stderr).Select(v => v.Trim()).Where(v => v.Length > 0))
                result.Logs.Add(new RunLogLine { Level = "ERROR", Message = line });
        }

        if (result.SapStatusType.Equals("E", StringComparison.OrdinalIgnoreCase) ||
            result.SapStatusType.Equals("A", StringComparison.OrdinalIgnoreCase))
        {
            result.Status = "failed";
        }

        result.Message = FirstNonEmpty(
            result.SapStatusText,
            result.Logs.LastOrDefault(l => l.Level.Equals("ERROR", StringComparison.OrdinalIgnoreCase))?.Message ?? "",
            result.Logs.LastOrDefault()?.Message ?? "",
            exitCode == 0 ? "transaction script executed" : $"VBS exit code {exitCode}");

        return result;
    }

    static RunResultRequest FailedRunResult(string message, DateTime started)
    {
        return new RunResultRequest
        {
            Status = "failed",
            Message = message,
            DurationMs = EnsureDuration(0, started),
            Logs = { new RunLogLine { Level = "ERROR", Message = message } }
        };
    }

    static long EnsureDuration(long durationMs, DateTime started)
    {
        if (durationMs > 0)
            return durationMs;

        return Math.Max(0, (long)(DateTime.UtcNow - started).TotalMilliseconds);
    }

    static RunFile BuildRunFile(string path)
    {
        string expanded = Environment.ExpandEnvironmentVariables(path ?? "");
        var file = new RunFile
        {
            Type = "output",
            Name = Path.GetFileName(expanded),
            Path = path ?? ""
        };

        try
        {
            if (File.Exists(expanded))
                file.Size = new FileInfo(expanded).Length;
        }
        catch { }

        return file;
    }

    static IEnumerable<string> SplitLines(string value)
    {
        return (value ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    static bool TryReadOutputKey(string line, string key, out string value)
    {
        value = "";
        string equalsPrefix = key + "=";
        if (line.StartsWith(equalsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            value = line[equalsPrefix.Length..].Trim();
            return true;
        }

        string colonPrefix = key + ":";
        if (line.StartsWith(colonPrefix, StringComparison.OrdinalIgnoreCase))
        {
            value = line[colonPrefix.Length..].Trim();
            return true;
        }

        return false;
    }

    static void Log(string message)
    {
        try
        {
            EnsureRuntimeDirectories();
            File.AppendAllText(LogFilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch
        {
        }
    }

    static string DescribeQuery(NameValueCollection query)
    {
        return string.Join(", ",
            query.AllKeys
                .Where(k => !string.IsNullOrEmpty(k))
                .Select(k => $"{k}={MaskValue(k!, query[k] ?? string.Empty)}"));
    }

    static string DescribeParams(SapRunParams p)
    {
        return $"tcode={p.TCode}, script={p.Script}, system={p.System}, client={p.Client}, user={p.User}, pw={MaskValue("pw", p.Password)}, lang={p.Language}, sysnr={p.SysNr}, year={p.Year}, week={p.Week}, plant={p.Plant}, plants={p.Plants}, period={p.Period}, businessArea={p.BusinessArea}, businessAreas={p.BusinessAreas}, weekEnd={p.WeekEnd}, factoryGroup={p.FactoryGroup}, runStrategy={p.RunStrategy}";
    }

    static string MaskRawArg(string? arg)
    {
        if (string.IsNullOrEmpty(arg))
            return "(none)";

        return Regex.Replace(
            arg,
            @"(?i)(pw|password)=([^&\s""]+)",
            m => $"{m.Groups[1].Value}=***");
    }

    static string MaskSapArgs(string args)
    {
        return Regex.Replace(
            args,
            @"(?i)-pw=([^\s""]+|""[^""]*"")",
            "-pw=***");
    }

    static string MaskValue(string key, string value)
    {
        return key.Equals("pw", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("password", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("payload", StringComparison.OrdinalIgnoreCase)
            ? "***"
            : value;
    }
}

class SapRunParams
{
    public string System { get; set; } = "";
    public string Client { get; set; } = "";
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string Language { get; set; } = "ZH";
    public string SysNr { get; set; } = "";
    public string TCode { get; set; } = "ZFI019NL";
    public string Script { get; set; } = "openOnly";
    public string Plant { get; set; } = "";
    public string Plants { get; set; } = "";
    public string Year { get; set; } = "";
    public string Week { get; set; } = "";
    public string Period { get; set; } = "";
    public string BusinessArea { get; set; } = "";
    public string BusinessAreas { get; set; } = "";
    public string WeekEnd { get; set; } = "";
    public string FactoryGroup { get; set; } = "";
    public string RunStrategy { get; set; } = "";
    public string Field1Name { get; set; } = "";
    public string Field1Value { get; set; } = "";
    public string Field2Name { get; set; } = "";
    public string Field2Value { get; set; } = "";
    public string CaretPos { get; set; } = "0";
    public string ButtonId { get; set; } = "";
    public string RunId { get; set; } = "";
}

class SapLocalConfig
{
    public string? System { get; set; }
    public string? Client { get; set; }
    public string? User { get; set; }
    public string? Password { get; set; }
    public string? PasswordProtected { get; set; }
    public string? Language { get; set; }
    public string? SysNr { get; set; }
}

class TransactionConfigRequest
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Script { get; set; } = "";
    public string ScriptFile { get; set; } = "";
    public string Icon { get; set; } = "";
    public JsonElement Params { get; set; }
    public string FactoryRule { get; set; } = "";
    public JsonElement FixedPlants { get; set; }
    public string FixedPlantsCsv { get; set; } = "";
    public string DefaultPlantGroup { get; set; } = "";
    public string Automation { get; set; } = "";
    public string DefaultRunMode { get; set; } = "";
    public int? TimeoutSeconds { get; set; }
    public int? Timeout { get; set; }
    public int? RetryCount { get; set; }
    public int? Retry { get; set; }
    public string ScriptVersion { get; set; } = "";
    public string ScriptHash { get; set; } = "";
    public bool? Enabled { get; set; }
}

record PlantSeed(string Code, string Name, string BusinessArea, int SortOrder);

record PlantGroupSeed(string Id, string Name, string ShortName, string[] Plants, int SortOrder)
{
    public string Description { get; init; } = "";
    public string[] Zfi019nlAreas { get; init; } = Array.Empty<string>();
    public string[] Zfi080Areas { get; init; } = Array.Empty<string>();
    public string[] Zfi072Plants { get; init; } = Array.Empty<string>();
    public string[] Zco019Plants { get; init; } = Array.Empty<string>();
}

class PlantConfigRequest
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string BusinessArea { get; set; } = "";
    public string Area { get; set; } = "";
    public JsonElement Groups { get; set; }
    public string GroupsCsv { get; set; } = "";
    public int SortOrder { get; set; }
    public bool? Enabled { get; set; }
    public string UpdatedBy { get; set; } = "";
}

class PlantGroupConfigRequest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ShortName { get; set; } = "";
    public string Description { get; set; } = "";
    public JsonElement Plants { get; set; }
    public string PlantsCsv { get; set; } = "";
    public JsonElement Zfi019nlAreas { get; set; }
    public string Zfi019nlAreasCsv { get; set; } = "";
    public JsonElement Zfi080Areas { get; set; }
    public string Zfi080AreasCsv { get; set; } = "";
    public JsonElement Zfi072Plants { get; set; }
    public string Zfi072PlantsCsv { get; set; } = "";
    public JsonElement Zco019Plants { get; set; }
    public string Zco019PlantsCsv { get; set; } = "";
    public int SortOrder { get; set; }
    public bool? Enabled { get; set; }
    public string UpdatedBy { get; set; } = "";
}

class TransactionPlantRuleRequest
{
    public string TCode { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Module { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Script { get; set; } = "";
    public string ScriptFile { get; set; } = "";
    public string Icon { get; set; } = "";
    public JsonElement Params { get; set; }
    public string FactoryRule { get; set; } = "";
    public string DefaultPlantGroup { get; set; } = "";
    public string DefaultGroup { get; set; } = "";
    public JsonElement FixedPlants { get; set; }
    public string FixedPlantsCsv { get; set; } = "";
    public JsonElement Plants { get; set; }
    public string PlantsCsv { get; set; } = "";
    public JsonElement SelectableGroupIds { get; set; }
    public string SelectableGroupIdsCsv { get; set; } = "";
    public string BusinessAreaMode { get; set; } = "";
    public JsonElement BusinessAreas { get; set; }
    public string BusinessAreasCsv { get; set; } = "";
    public string Automation { get; set; } = "";
    public int? TimeoutSeconds { get; set; }
    public int? Timeout { get; set; }
    public int? RetryCount { get; set; }
    public int? Retry { get; set; }
    public bool? Enabled { get; set; }
    public string UpdatedBy { get; set; } = "";
}

class NotificationRobotConfigRequest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string RobotType { get; set; } = "";
    public string Type { get; set; } = "";
    public string TargetLabel { get; set; } = "";
    public string Group { get; set; } = "";
    public string Webhook { get; set; } = "";
    public string WebhookUrl { get; set; } = "";
    public string WebhookReplacement { get; set; } = "";
    public string Secret { get; set; } = "";
    public string SecretReplacement { get; set; } = "";
    public bool ClearWebhook { get; set; }
    public bool ClearSecret { get; set; }
    public JsonElement Bindings { get; set; }
    public bool? Enabled { get; set; }
    public string UpdatedBy { get; set; } = "";
}

class ScheduleTaskRequest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string TCode { get; set; } = "";
    public string Code { get; set; } = "";
    public string TransactionCode { get; set; } = "";
    public JsonElement Plants { get; set; }
    public string PlantsCsv { get; set; } = "";
    public string DefaultBusinessScope { get; set; } = "";
    public string Cron { get; set; } = "";
    public string Frequency { get; set; } = "";
    public string Time { get; set; } = "";
    public string RunTime { get; set; } = "";
    public bool? Enabled { get; set; }
    public bool? NotifyEnabled { get; set; }
    public bool? NotifyOnSuccess { get; set; }
    public bool? NotifyOnFailure { get; set; }
    public string NotifyTarget { get; set; } = "";
    public JsonElement Params { get; set; }
    public string CreatedBy { get; set; } = "";
    public string UpdatedBy { get; set; } = "";
}

class CreateRunRequest
{
    public string TransactionCode { get; set; } = "";
    public string TCode { get; set; } = "";
    public string Code { get; set; } = "";
    public string Source { get; set; } = "";
    public string NotifyTarget { get; set; } = "";
    public int Priority { get; set; }
    public int MaxAttempts { get; set; } = 1;
    public OperatorIdentity Operator { get; set; } = new();
    public Dictionary<string, string> Params { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

class OperatorIdentity
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Dept { get; set; } = "";
}

class RunResultRequest
{
    public string Status { get; set; } = "";
    public string SapStatusType { get; set; } = "";
    public string SapStatusText { get; set; } = "";
    public string Message { get; set; } = "";
    public long DurationMs { get; set; }
    public List<RunLogLine> Logs { get; set; } = new();
    public List<RunFile> Files { get; set; } = new();
}

class RunRecordView
{
    public string RunId { get; set; } = "";
    public string TransactionCode { get; set; } = "";
    public string OperatorId { get; set; } = "";
    public string OperatorName { get; set; } = "";
    public string OperatorDept { get; set; } = "";
    public string Status { get; set; } = "";
    public string RequestJson { get; set; } = "";
    public string SapStatusType { get; set; } = "";
    public string SapStatusText { get; set; } = "";
    public string Message { get; set; } = "";
    public string ScriptFile { get; set; } = "";
    public string ScriptHash { get; set; } = "";
    public string QueuedAt { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public string FinishedAt { get; set; } = "";
    public long DurationMs { get; set; }
    public string Source { get; set; } = "";
    public string NotifyTarget { get; set; } = "";
    public int Priority { get; set; }
    public int Attempt { get; set; }
    public int MaxAttempts { get; set; }
    public string LockedBy { get; set; } = "";
    public string LockedAt { get; set; } = "";
    public List<RunLogLine> Logs { get; set; } = new();
    public List<RunFile> Files { get; set; } = new();
}

class RunLogLine
{
    public string Level { get; set; } = "INFO";
    public string Message { get; set; } = "";
    public string CreatedAt { get; set; } = "";
}

class RunFile
{
    public string Type { get; set; } = "output";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string CreatedAt { get; set; } = "";
}

class TransactionScriptInfo
{
    public string ScriptFile { get; set; } = "";
    public string ScriptHash { get; set; } = "";
}

class QueuedRunWorkItem
{
    public string RunId { get; set; } = "";
    public string TransactionCode { get; set; } = "";
    public string RequestJson { get; set; } = "";
    public string ScriptFile { get; set; } = "";
}

class PlantReportAccumulator
{
    public long TotalRuns { get; set; }
    public long SuccessRuns { get; set; }
    public long DurationTotalMs { get; set; }
    public long DurationCount { get; set; }
}
