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

    static string FirstCsvValue(string value)
    {
        return NormalizeCsv(value).Split(',', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
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
INSERT OR IGNORE INTO schema_migrations(version, applied_at) VALUES(1, datetime('now'));
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

            if (seedFromScripts && CountTransactions(connection) == 0)
                SeedTransactions(connection);

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
                metadata.TryGetValue("fixedPlants", out string? metaPlants) ? metaPlants ?? "" : "",
                JsonArrayToCsv(item, "fixedPlants"));
            string scriptVersion = metadata.TryGetValue("version", out string? version) ? version ?? "" : "";
            string scriptHash = string.IsNullOrWhiteSpace(scriptText) ? "" : Sha256Hex(scriptText);

            using var command = connection.CreateCommand();
            command.CommandText = """
INSERT INTO transactions (
    tcode, name, stage, script_file, icon, params_json, factory_rule, fixed_plants_json,
    default_group, automation, script_version, script_hash, script_metadata_json, enabled, updated_at
) VALUES (
    $tcode, $name, $stage, $scriptFile, $icon, $paramsJson, $factoryRule, $fixedPlantsJson,
    $defaultGroup, $automation, $scriptVersion, $scriptHash, $metadataJson, 1, $updatedAt
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
       default_group, automation, script_version, script_hash, enabled, updated_at
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
                scriptVersion = reader.GetString(10),
                scriptHash = reader.GetString(11),
                enabled = reader.GetInt32(12) == 1,
                updatedAt = reader.GetString(13)
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
            metadata.TryGetValue("fixedPlants", out string? metaPlants) ? metaPlants ?? "" : "");
        string scriptVersion = FirstNonEmpty(
            item.ScriptVersion,
            metadata.TryGetValue("version", out string? version) ? version ?? "" : "");
        string scriptHash = string.IsNullOrWhiteSpace(scriptText)
            ? FirstNonEmpty(item.ScriptHash)
            : Sha256Hex(scriptText);

        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO transactions (
    tcode, name, stage, script_file, icon, params_json, factory_rule, fixed_plants_json,
    default_group, automation, script_version, script_hash, script_metadata_json, enabled, updated_at
) VALUES (
    $tcode, $name, $stage, $scriptFile, $icon, $paramsJson, $factoryRule, $fixedPlantsJson,
    $defaultGroup, $automation, $scriptVersion, $scriptHash, $metadataJson, $enabled, $updatedAt
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

    static string GetJsonString(JsonElement item, string property)
    {
        return item.TryGetProperty(property, out JsonElement value) && value.ValueKind != JsonValueKind.Null
            ? JsonValueToString(value)
            : "";
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

    static string CsvToJsonArray(string value)
    {
        string[] items = NormalizeCsvPreserveOrder(value).Split(',', StringSplitOptions.RemoveEmptyEntries);
        return JsonSerializer.Serialize(items, JsonOptions);
    }

    static string Sha256Hex(string value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    static RunResultRequest LaunchSapGuiAndExecute(SapRunParams p)
    {
        var started = DateTime.UtcNow;
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
        if (!string.IsNullOrWhiteSpace(scriptFixedPlants))
        {
            effectivePlants = NormalizeCsvPreserveOrder(scriptFixedPlants);
            Log($"脚本元数据固定工厂覆盖页面参数: {effectivePlants}");
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
            Log($"执行 VBS: {tmpFile}");

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
                throw new Exception($"VBS 执行失败，退出码 {proc.ExitCode}");
            }

            if (stdOut.Contains("ERROR:", StringComparison.OrdinalIgnoreCase) ||
                stdErr.Contains("ERROR:", StringComparison.OrdinalIgnoreCase))
            {
                keepTempFile = true;
                Log($"VBS 返回错误，保留脚本文件: {tmpFile}");
                throw new Exception(string.IsNullOrWhiteSpace(mergedOutput)
                    ? $"VBS 返回错误，脚本已保留: {tmpFile}"
                    : $"VBS 返回错误: {mergedOutput}");
            }

            if (!stdOut.Contains("INFO: transaction script executed", StringComparison.OrdinalIgnoreCase))
            {
                keepTempFile = true;
                Log($"VBS 未返回成功标记，保留脚本文件: {tmpFile}");
                throw new Exception(string.IsNullOrWhiteSpace(mergedOutput)
                    ? $"VBS 未返回成功标记，脚本已保留: {tmpFile}"
                    : $"VBS 未返回成功标记: {mergedOutput}");
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
                System = "LOCAL",
                Client = "300",
                User = "LOCALUSER",
                Password = "LOCALPASS",
                Language = "ZH"
            });
            Check("payload兼容", p.TCode == "ZFI019NL" && p.Plants == "1022,1024" && p.BusinessAreas == "2900,3960", $"tcode={p.TCode}, plants={p.Plants}, businessAreas={p.BusinessAreas}");
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
                System = "dev300",
                Client = "300",
                User = "LOCALUSER",
                Password = "LOCALPASS",
                Language = "ZH",
                SysNr = "10"
            };
            var p = BuildParams(q, PrimaryProtocolName, local);
            bool ok = p.System == "dev300" && p.Client == "300" && p.User == "LOCALUSER" &&
                      p.Password == "LOCALPASS" && p.Language == "ZH" && p.SysNr == "10";
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
            string secret = "dummy-secret";
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
    public string ScriptVersion { get; set; } = "";
    public string ScriptHash { get; set; } = "";
    public bool? Enabled { get; set; }
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
