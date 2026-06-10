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
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SapWebLauncher");
    private static readonly string LogFilePath = Path.Combine(LogDirectory, "launcher.log");
    private static readonly string ConfigFilePath = Path.Combine(LogDirectory, "config.json");
    private static readonly string DatabaseFilePath = Path.Combine(LogDirectory, "sap-rpa-config.db");
    private static readonly string ExeDirectory = AppContext.BaseDirectory;
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

        var pars = BuildParams(query, protocolName);
        Log($"准备执行: {DescribeParams(pars)}");
        LaunchSapGuiAndExecute(pars);
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
            ButtonId = First(query, "button", "buttonid") ?? ""
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
        string prefix = $"http://127.0.0.1:{BridgePort}/";
        listener.Prefixes.Add(prefix);
        listener.Start();
        Log($"Bridge API 已启动: {prefix}");
        Console.WriteLine($"Bridge API running: {prefix}");

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
                    database = DatabaseFilePath,
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
        response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
        response.Headers["Access-Control-Allow-Private-Network"] = "true";
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
        Directory.CreateDirectory(LogDirectory);
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
INSERT OR IGNORE INTO schema_migrations(version, applied_at) VALUES(1, datetime('now'));
""";
        command.ExecuteNonQuery();

        if (seedFromScripts)
            SeedTransactions(connection);

        Log($"SQLite 数据库初始化完成: {DatabaseFilePath}");
    }

    static SqliteConnection OpenDatabaseConnection()
    {
        var connection = new SqliteConnection($"Data Source={DatabaseFilePath}");
        connection.Open();
        return connection;
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

    static string FindTransactionConfigPath()
    {
        string[] candidates =
        {
            Path.Combine(ExeDirectory, "transactions", "transaction-config.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SapRpaLauncher", "transactions", "transaction-config.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "transactions", "transaction-config.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "网页启动登录", "transactions", "transaction-config.json")
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

    static void LaunchSapGuiAndExecute(SapRunParams p)
    {
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
                return;
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
            ExecuteViaGuiScripting(p);
            Console.WriteLine($"{p.TCode} 执行完成");
            Log($"{p.TCode} 执行完成");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{p.TCode} 执行失败: {ex.Message}");
            Log($"{p.TCode} 执行失败: {ex}");
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

    static void ExecuteViaGuiScripting(SapRunParams p)
    {
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
            Path.Combine(ExeDirectory, "transactions"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SapRpaLauncher", "transactions"),
            Path.Combine(Directory.GetCurrentDirectory(), "transactions"),
            Path.Combine(Directory.GetCurrentDirectory(), "网页启动登录", "transactions")
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

        Console.WriteLine($"\n=== 总计: {passed} PASS, {failed} FAIL, {(failed == 0 ? "全部通过" : "有失败项")} ===");
        return failed == 0 ? 0 : 1;
    }

    static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
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
