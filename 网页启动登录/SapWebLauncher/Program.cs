using ClosedXML.Excel;
using Microsoft.Win32;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;

namespace SapWebLauncher;

static class Program
{
    private const string PrimaryProtocolName = "sap-rpa";
    private const string MutexId = "SapWebLauncher-SingleInstance-Mutex";
    private const int BridgePort = 8080;
    private const int DefaultRunListLimit = 50;
    private const int DefaultQueueStatusLimit = 5;
    private const int MaxQueueStatusLimit = 20;
    private const int StaleRunningTimeoutHours = 6;
    private const int QueueHeartbeatIntervalSeconds = 60;
    private const int SchedulePollIntervalMilliseconds = 30_000;
    private const int ScheduleTriggerLookbackMinutes = 15;
    // Temporary default until DingTalk scan login writes the real user id into runs.ding_talk_user_id.
    private const string DefaultDingTalkId = "11464769";
    private const int NotificationWorkerTimeoutSeconds = 12;
    private const string Zfi057TbtcoJobName = "ZFI057";
    private const int Zfi057TbtcoPollTimeoutSeconds = 600;
    private const int Zfi057TbtcoPollIntervalSeconds = 10;
    private static readonly string ExeDirectory = AppContext.BaseDirectory;
    private static readonly string LocalConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SapWebLauncher");
    private static readonly string RuntimeRoot = ResolveRuntimeRoot();
    private static readonly string DataDirectory = Path.Combine(RuntimeRoot, "data");
    private static readonly string LogDirectory = Path.Combine(RuntimeRoot, "logs");
    private static readonly string OutputDirectory = Path.Combine(RuntimeRoot, "outputs");
    private static readonly string RuntimeTransactionsDirectory = Path.Combine(RuntimeRoot, "transactions");
    private static readonly string RuntimeLocalConfigFilePath = Path.Combine(RuntimeRoot, "config.local.json");
    private static readonly string LogFilePath = Path.Combine(LogDirectory, "launcher.log");
    private static readonly string ConfigFilePath = Path.Combine(LocalConfigDirectory, "config.json");
    private static readonly string AlvExportDataDirectory = ResolveAlvExportDataDirectory();
    private static readonly string DatabaseFilePath = Path.Combine(DataDirectory, "sap-rpa-config.db");
    private static readonly string LegacyDatabaseFilePath = Path.Combine(LocalConfigDirectory, "sap-rpa-config.db");
    private static readonly string ExecutorId = $"{Environment.MachineName}\\{Environment.UserName}";
    private static readonly object DatabaseInitLock = new();
    private static bool DatabaseInitialized;
    private static readonly object ActiveRunLock = new();
    private static readonly HashSet<string> ActiveExecutingRunIds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object SapLoginStateLock = new();
    private static readonly TimeSpan SapLoginFailureCooldown = TimeSpan.FromMinutes(2);
    private static SapLoginFailure? LastSapLoginFailure;
    private static readonly HttpClient NotificationHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };
    // Keep strict SAP session matching off during multi-client testing. Enable for production with SAP_RPA_STRICT_SAP_SESSION=1.
    private static readonly bool StrictSapSessionMatching =
        (Environment.GetEnvironmentVariable("SAP_RPA_STRICT_SAP_SESSION") ?? "").Trim() is "1" or "true" or "TRUE" or "on" or "ON" or "yes" or "YES";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private static readonly DingTalkParamGroup[] DingTalkParamGroups =
    {
        new("业务范围", new[] { "businessAreas", "businessareas", "businessArea", "businessarea", "businessAreaList", "businessareaslist", "gsberlist", "gsber" }, true),
        new("工厂", new[] { "plants", "plant", "plantCodes", "factoryCodes", "werkslist", "plantlist", "werks" }, true),
        new("业务范围组", new[] { "factoryGroup", "defaultGroup", "defaultBusinessScope", "businessScope", "plantGroup", "plantGroupId" }, true),
        new("年度", new[] { "year", "gjahr", "fiscalYear", "fiscal_year" }, false),
        new("周次", new[] { "week", "weekNo", "weekno", "weekNumber", "week_number" }, false),
        new("期间", new[] { "period", "month", "poper", "fiscalPeriod", "fiscal_period" }, false),
        new("开始日期", new[] { "startDate", "fromDate", "dateFrom", "beginDate", "dateBegin" }, false),
        new("截止日期", new[] { "weekEnd", "week_end", "endDate", "toDate", "dateTo", "dateEnd" }, false),
        new("执行方式", new[] { "runStrategy", "strategy", "runMode", "mode" }, false),
        new("字段1名称", new[] { "field1Name" }, false),
        new("字段1值", new[] { "field1Value" }, true),
        new("字段2名称", new[] { "field2Name" }, false),
        new("字段2值", new[] { "field2Value" }, true),
        new("超时秒数", new[] { "timeoutSeconds", "timeout", "vbsTimeoutSeconds" }, false),
        new("备注", new[] { "remark", "remarks", "note", "comment" }, false)
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

        if (args.Length > 0 &&
            (args[0].Equals("--test-zfi019nl-memory", StringComparison.OrdinalIgnoreCase) ||
             args[0].Equals("test-zfi019nl-memory", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.Exit(RunZfi019NlMemoryDiagnostic(args.Skip(1).ToArray()));
            return;
        }

        if (args.Length > 0 &&
            (args[0].Equals("--test-zfi057-get-gs03", StringComparison.OrdinalIgnoreCase) ||
             args[0].Equals("test-zfi057-get-gs03", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.Exit(RunZfi057GetGs03Diagnostic(args.Skip(1).ToArray()));
            return;
        }

        if (args.Length > 0 && args[0].Equals("test", StringComparison.OrdinalIgnoreCase))
        {
            Environment.Exit(RunSelfTest());
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
        Console.WriteLine($"  诊断 ZFI019NL memory 取数: {Process.GetCurrentProcess().ProcessName}.exe --test-zfi019nl-memory --businessArea 2800 --period 2026.04.27 --weekEnd 2026.05.03");
        Console.WriteLine($"  诊断 ZFI057 业务范围工厂: {Process.GetCurrentProcess().ProcessName}.exe --test-zfi057-get-gs03 --businessArea 2800 --setName Z31");
        Console.WriteLine($"  或从浏览器跳转 {PrimaryProtocolName}://run?action=run&tcode=ZFI019NL&script=ZFI019NL.vbs&businessAreas=2800");
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

        string installedRuntimeRoot = Path.GetFullPath(Path.Combine(ExeDirectory, ".."));
        if (LooksLikeRuntimeRoot(installedRuntimeRoot))
            return installedRuntimeRoot;

        const string handoffRoot = @"D:\sap_ai";
        if (Directory.Exists(handoffRoot))
            return handoffRoot;

        return LocalConfigDirectory;
    }

    static bool LooksLikeRuntimeRoot(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return false;

            string binPath = Path.Combine(path, "bin");
            return Directory.Exists(binPath) &&
                   Path.GetFullPath(binPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       .Equals(Path.GetFullPath(ExeDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) &&
                   (File.Exists(Path.Combine(path, "index.html")) ||
                    Directory.Exists(Path.Combine(path, "transactions")) ||
                    Directory.Exists(Path.Combine(path, "assets")));
        }
        catch
        {
            return false;
        }
    }

    static string ResolveAlvExportDataDirectory()
    {
        string defaultPath = Path.Combine(RuntimeRoot, "\u4E34\u65F6\u6587\u4EF6", "\u6587\u4EF6\u6570\u636E");
        string configured = (Environment.GetEnvironmentVariable("SAP_RPA_ALV_EXPORT_DIR") ?? "").Trim();

        if (string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                using JsonDocument? document = LoadLocalConfigDocument();
                if (document != null && document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    JsonElement root = document.RootElement;
                    JsonElement? fileStorage = TryGetObject(root, "fileStorage");
                    JsonElement? output = TryGetObject(root, "output");
                    JsonElement? outputs = TryGetObject(root, "outputs");
                    configured = FirstNonEmpty(
                        GetConfigString(fileStorage, "alvExportDataDirectory"),
                        GetConfigString(fileStorage, "alvExportDirectory"),
                        GetConfigString(fileStorage, "fileDataDirectory"),
                        GetConfigString(output, "alvExportDataDirectory"),
                        GetConfigString(output, "alvExportDirectory"),
                        GetConfigString(outputs, "alvExportDataDirectory"),
                        GetConfigString(outputs, "alvExportDirectory"),
                        GetConfigString(root, "alvExportDataDirectory"),
                        GetConfigString(root, "alvExportDirectory"));
                }
            }
            catch (Exception ex)
            {
                Log($"resolve ALV export directory from config failed: {ex.Message}");
            }
        }

        return ResolveDirectoryPath(configured, defaultPath, "ALV export data directory");
    }

    static string ResolveDirectoryPath(string configured, string defaultPath, string label)
    {
        configured = Environment.ExpandEnvironmentVariables((configured ?? "").Trim());
        if (string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(defaultPath);

        try
        {
            string path = configured;
            if (!Path.IsPathRooted(path))
                path = Path.Combine(RuntimeRoot, path);

            return Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            Log($"{label} is invalid; fallback to default. value={configured}, error={ex.Message}");
            return Path.GetFullPath(defaultPath);
        }
    }

    static void EnsureRuntimeDirectories()
    {
        Directory.CreateDirectory(RuntimeRoot);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(OutputDirectory);
        Directory.CreateDirectory(RuntimeTransactionsDirectory);
        Directory.CreateDirectory(AlvExportDataDirectory);
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

    static int RunZfi019NlMemoryDiagnostic(string[] args)
    {
        try
        {
            EnsureRuntimeDirectories();
            var values = ParseCliKeyValueArgs(args);
            string businessArea = FirstNonEmpty(
                First(values, "businessArea", "businessarea", "gsber") ?? "",
                "2800");
            string period = FirstNonEmpty(
                First(values, "period", "startDate", "dateFrom") ?? "",
                "2026.04.27");
            string weekEnd = FirstNonEmpty(
                First(values, "weekEnd", "endDate", "dateTo") ?? "",
                "2026.05.03");
            string plants = FirstNonEmpty(
                First(values, "plants", "werks", "plant") ?? "",
                "");

            var p = ApplyLocalConfig(new SapRunParams
            {
                TCode = "ZFI057",
                Script = "diagnostic",
                BusinessAreas = businessArea,
                BusinessArea = businessArea,
                Plants = plants,
                Plant = FirstCsvValue(plants),
                Period = period,
                WeekEnd = weekEnd,
                RunStrategy = "diagnostic",
                RunId = $"DIAG-ZFI019NL-{DateTime.Now:yyyyMMddHHmmss}"
            });

            string[] diagnosticPlants = ResolveZfi057DiagnosticPlants(p, businessArea, plants);
            var fetch = ExecuteZfi057Step1Memory(p, businessArea, Array.Empty<string>());
            var aggregate = new RunResultRequest { Status = fetch.Result.Status, Message = fetch.Result.Message };
            AddStepResult(aggregate, "diagnostic ZFI019NL memory", fetch.Result);
            AddZfi057MaterialAuditFile(
                aggregate,
                p,
                1,
                businessArea,
                diagnosticPlants,
                "ZFI019NL_MEMORY",
                Array.Empty<string>(),
                fetch.Materials,
                fetch.Materials,
                fetch.FetchResult.SplitRows);

            Console.WriteLine("ZFI019NL memory diagnostic");
            Console.WriteLine($"status={fetch.Result.Status}");
            Console.WriteLine($"message={fetch.Result.Message}");
            Console.WriteLine($"businessArea={businessArea}");
            Console.WriteLine($"period={period}");
            Console.WriteLine($"weekEnd={weekEnd}");
            Console.WriteLine($"method={fetch.FetchResult.ActualMethod}");
            Console.WriteLine($"rawLines={fetch.FetchResult.RawLines.Count}");
            Console.WriteLine($"headers={fetch.FetchResult.Headers.Count}");
            Console.WriteLine($"alvRows={fetch.FetchResult.AlvRows.Count}");
            Console.WriteLine($"finalRows={fetch.FetchResult.FinalRows.Count}");
            Console.WriteLine($"materialCount={fetch.Materials.Length}");
            Console.WriteLine("materials=omitted");
            foreach (var file in aggregate.Files)
                Console.WriteLine($"file={file.Path}");
            Console.WriteLine("logs:");
            foreach (var line in SanitizeZfi057DiagnosticLogs(aggregate.Logs))
                Console.WriteLine($"{FirstNonEmpty(line.Level, "INFO")}: {line.Message}");

            string diagnosticLog = WriteZfi019NlDiagnosticLog(p, businessArea, diagnosticPlants, period, weekEnd, fetch, aggregate);
            Console.WriteLine($"diagnosticLog={diagnosticLog}");

            return IsSuccessResult(fetch.Result) && fetch.Materials.Length > 0 ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ZFI019NL memory diagnostic failed: {ex.Message}");
            Log($"ZFI019NL memory diagnostic failed: {ex}");
            return 1;
        }
    }

    static int RunZfi057GetGs03Diagnostic(string[] args)
    {
        try
        {
            EnsureRuntimeDirectories();
            var values = ParseCliKeyValueArgs(args);
            string businessArea = FirstNonEmpty(
                First(values, "businessArea", "businessarea", "gsber") ?? "",
                "2800");
            string setName = FirstNonEmpty(
                First(values, "setName", "setname", "gs03SetName", "gs03setname") ?? "",
                LoadZfi057Gs03SetName(),
                SapGs03PlantFetcher.DefaultSetName);

            var p = ApplyLocalConfig(new SapRunParams
            {
                TCode = "ZFI057",
                Script = "diagnostic",
                BusinessAreas = businessArea,
                BusinessArea = businessArea,
                RunStrategy = "diagnostic"
            });

            SapNcoConnectionConfig connectionConfig = BuildSapNcoConnectionConfig(p);
            var result = new SapGs03PlantFetcher().FetchPlantsForBusinessArea(connectionConfig, businessArea, setName);
            Console.WriteLine("ZFI057 GET_GS03 diagnostic");
            Console.WriteLine($"status={(result.Success ? "success" : "failed")}");
            Console.WriteLine($"message={result.Message}");
            Console.WriteLine($"action={result.Action}");
            Console.WriteLine($"setName={result.SetName}");
            Console.WriteLine($"businessArea={businessArea}");
            Console.WriteLine($"plants={string.Join(",", result.Plants)}");
            Console.WriteLine($"plantCount={result.Plants.Count}");
            Console.WriteLine($"jsonInput={result.JsonInput}");
            Console.WriteLine($"jsonOutput={Truncate(result.JsonOutput, 4000)}");
            Console.WriteLine($"rawLines={string.Join(" | ", result.RawLines.Take(20))}");
            return result.Success && result.Plants.Count > 0 ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ZFI057 GET_GS03 diagnostic failed: {ex.Message}");
            Log($"ZFI057 GET_GS03 diagnostic failed: {ex}");
            return 1;
        }
    }

    static NameValueCollection ParseCliKeyValueArgs(string[] args)
    {
        var result = new NameValueCollection();
        for (int i = 0; i < args.Length; i++)
        {
            string raw = args[i] ?? "";
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            string token = raw.Trim();
            string key;
            string value;
            int equals = token.IndexOf('=');
            if (equals > 0)
            {
                key = token[..equals];
                value = token[(equals + 1)..];
            }
            else
            {
                key = token;
                value = i + 1 < args.Length && !(args[i + 1] ?? "").StartsWith("-", StringComparison.Ordinal)
                    ? args[++i]
                    : "true";
            }

            key = key.TrimStart('-', '/').Trim();
            if (key.Length > 0)
                result[key] = value.Trim();
        }

        return result;
    }

    static string WriteZfi019NlDiagnosticLog(
        SapRunParams p,
        string businessArea,
        string[] plants,
        string period,
        string weekEnd,
        Zfi057Step1MaterialFetch fetch,
        RunResultRequest aggregate)
    {
        string directory = Path.Combine(OutputDirectory, "zfi057");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{SafeFileNamePart(FirstNonEmpty(p.RunId, DateTime.Now.ToString("yyyyMMddHHmmss")))}_diagnostic.log");
        var lines = new List<string>
        {
            "ZFI019NL memory diagnostic",
            $"runId={p.RunId}",
            $"status={fetch.Result.Status}",
            $"message={fetch.Result.Message}",
            $"businessArea={businessArea}",
            $"period={period}",
            $"weekEnd={weekEnd}",
            $"method={fetch.FetchResult.ActualMethod}",
            $"rawLines={fetch.FetchResult.RawLines.Count}",
            $"headers={fetch.FetchResult.Headers.Count}",
            $"headerList={string.Join("|", fetch.FetchResult.Headers)}",
            $"rawHeaderLine={Truncate(fetch.FetchResult.RawLines.FirstOrDefault(line => line.StartsWith("HEADER=", StringComparison.OrdinalIgnoreCase)) ?? "", 2000)}",
            $"rawRowLine1={Truncate(FindLogicalSapLine(fetch.FetchResult.RawLines, "ROW="), 2000)}",
            $"alvRows={fetch.FetchResult.AlvRows.Count}",
            $"finalRows={fetch.FetchResult.FinalRows.Count}",
            $"materialCount={fetch.Materials.Length}",
            "materials=omitted",
            $"options={fetch.FetchResult.Options}",
            "",
            "step2Inputs:"
        };

        string[] step2Plants = plants.Where(plant => !string.IsNullOrWhiteSpace(plant)).ToArray();
        if (step2Plants.Length == 0)
        {
            lines.Add("no step2 plants resolved by GET_GS03");
        }

        if (step2Plants.Length > 0)
        {
            var step2 = CloneSapRunParams(p);
            step2.TCode = "ZFI057";
            step2.Script = "ZFI057.vbs";
            step2.BusinessAreas = businessArea;
            step2.BusinessArea = businessArea;
            step2.Plants = string.Join(",", step2Plants);
            step2.Plant = "";
            step2.Materials = "";
            step2.RunStrategy = "workflow-step";
            step2.TimeoutSeconds = Math.Max(p.TimeoutSeconds.GetValueOrDefault(0), 1800);
            lines.Add(BuildZfi057Step2InputSummary(step2, businessArea, step2.Plants, 1, 1, fetch.Materials.Length));
        }

        lines.AddRange(new[]
        {
            "",
            "logs:"
        });
        lines.AddRange(SanitizeZfi057DiagnosticLogs(aggregate.Logs).Select(line => $"{FirstNonEmpty(line.Level, "INFO")}: {line.Message}"));
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
        return path;
    }

    static IEnumerable<RunLogLine> SanitizeZfi057DiagnosticLogs(IEnumerable<RunLogLine> logs)
    {
        foreach (var line in logs)
        {
            string message = line.Message ?? "";
            if (message.Contains("memory fetch materials:", StringComparison.OrdinalIgnoreCase))
            {
                yield return new RunLogLine
                {
                    Level = FirstNonEmpty(line.Level, "INFO"),
                    Message = RedactMaterialSampleAndHash(message),
                    CreatedAt = line.CreatedAt
                };
                continue;
            }

            yield return line;
        }
    }

    static string RedactMaterialSampleAndHash(string message)
    {
        string result = Regex.Replace(message, @";\s*sample=[^;]*", "; sample=omitted", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @";\s*hash=[^;]*", "; hash=omitted", RegexOptions.IgnoreCase);
        return result;
    }

    static string[] ResolveZfi057DiagnosticPlants(SapRunParams p, string businessArea, string plants)
    {
        var scopeParams = CloneSapRunParams(p);
        scopeParams.BusinessAreas = businessArea;
        scopeParams.BusinessArea = businessArea;
        scopeParams.Plants = "";
        scopeParams.Plant = "";
        return ResolveZfi057WorkflowScopes(scopeParams)
            .Where(scope => scope.BusinessArea.Equals(businessArea, StringComparison.OrdinalIgnoreCase))
            .SelectMany(scope => scope.Plants)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    static string BuildZfi057Step2InputSummary(SapRunParams step2, string businessArea, string plants, int attemptIndex, int attemptTotal, int materialCount)
    {
        var windows = ResolveZfi057Step2DateWindows(step2.Period, step2.WeekEnd);
        var config = LoadZfi019NlMemoryConfig();
        string[] plantItems = NormalizeStringArray(FirstNonEmpty(plants, step2.Plants, step2.Plant));
        string plantText = plantItems.Length > 0 ? string.Join(",", plantItems) : "(script-mapped)";
        string werksSeed = plantItems.Length > 0 ? plantItems[0] : "(script-mapped)";
        string werksMode = plantItems.Length > 1 ? "LOW seed + multiSelection" : "LOW only";
        var parts = new List<string>
        {
            $"step2Input attempt={attemptIndex}/{Math.Max(1, attemptTotal)}",
            $"script={FirstNonEmpty(step2.Script, "ZFI057.vbs")}",
            $"tcode={FirstNonEmpty(step2.TCode, "ZFI057")}",
            $"businessArea={businessArea}",
            $"plantCount={plantItems.Length}",
            $"plants={plantText}",
            $"GET_GS03.TITLE={businessArea}",
            $"GET_GS03.FROM={plantText}",
            $"S_WERKS.mode={werksMode}",
            $"S_WERKS-LOW.seed={werksSeed}",
            $"S_WERKS.items={plantText}",
            "S_MTART-LOW=*",
            $"period={step2.Period}",
            $"weekEnd={step2.WeekEnd}",
            $"runCount={windows.Count}",
            $"windowCount={windows.Count}",
            "materialSource=step1.ZFI019NL_MEMORY",
            "S_MATNR.source=step1FinalMaterials",
            $"materialCount={materialCount}",
            "materials=omitted",
            $"timeoutSeconds={step2.TimeoutSeconds.GetValueOrDefault(0)}",
            $"ZFI_SPLIT.table={FirstNonEmpty(config.SplitTable, "ZFI_SPLIT")}",
            $"ZFI_SPLIT.BUKRS={FirstNonEmpty(config.SplitBukrs, "2030")}",
            "ZFI_SPLIT.fields=BUKRS,WERKS,MATNR,BEGDA,ENDDA,MTART"
        };

        foreach (var window in windows)
        {
            parts.Add($"window{window.Index}.S_KADKY-LOW={window.KadkyLow}");
            parts.Add($"window{window.Index}.S_KADKY-HIGH={window.KadkyHigh}");
            parts.Add($"window{window.Index}.S_KADAT-LOW={window.KadatLow}");
            parts.Add($"window{window.Index}.S_KADAT-HIGH={window.KadatHigh}");
            parts.Add($"window{window.Index}.ZFI_SPLIT.WERKS={plantText}");
            parts.Add($"window{window.Index}.ZFI_SPLIT.BEGDA<={window.KadkyHigh}");
            parts.Add($"window{window.Index}.ZFI_SPLIT.ENDDA>={window.KadkyLow}");
        }

        return string.Join("; ", parts);
    }

    static List<Zfi057Step2DateWindow> ResolveZfi057Step2DateWindows(string period, string weekEnd)
    {
        DateTime defaultStart = StartOfWeek(DateTime.Today).AddDays(-7);
        DateTime defaultEnd = defaultStart.AddDays(6);
        DateTime start = ParseFlexibleDateOrDefault(period, defaultStart);
        DateTime end = ParseFlexibleDateOrDefault(weekEnd, defaultEnd);
        DateTime firstOfStartMonth = new(start.Year, start.Month, 1);
        DateTime firstOfEndMonth = new(end.Year, end.Month, 1);

        if (start.Year == end.Year && start.Month == end.Month)
        {
            return new List<Zfi057Step2DateWindow>
            {
                new(1, FormatSapDate(firstOfStartMonth), FormatSapDate(end), FormatSapDate(firstOfStartMonth.AddDays(1)), FormatSapDate(end))
            };
        }

        DateTime previousMonthOfStart = start.AddMonths(-1);
        DateTime previousMonthStart = new(previousMonthOfStart.Year, previousMonthOfStart.Month, 1);
        DateTime startMonthEnd = firstOfEndMonth.AddDays(-1);
        return new List<Zfi057Step2DateWindow>
        {
            new(1, FormatSapDate(previousMonthStart), FormatSapDate(startMonthEnd), FormatSapDate(previousMonthStart.AddDays(1)), FormatSapDate(startMonthEnd)),
            new(2, FormatSapDate(firstOfEndMonth), FormatSapDate(end), FormatSapDate(firstOfEndMonth.AddDays(1)), FormatSapDate(end))
        };
    }

    static string FindLogicalSapLine(IEnumerable<string> rawLines, string prefix)
    {
        StringBuilder? buffer = null;
        foreach (var raw in rawLines)
        {
            string line = raw ?? "";
            string marker = line.Trim();
            if (marker.Equals("LONG_BEGIN", StringComparison.OrdinalIgnoreCase))
            {
                buffer = new StringBuilder();
                continue;
            }

            if (marker.StartsWith("LONG_PART=", StringComparison.OrdinalIgnoreCase))
            {
                buffer ??= new StringBuilder();
                buffer.Append(line.TrimStart()["LONG_PART=".Length..]);
                continue;
            }

            if (marker.Equals("LONG_END", StringComparison.OrdinalIgnoreCase))
            {
                string logical = buffer?.ToString() ?? "";
                buffer = null;
                if (logical.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return logical;
                continue;
            }

            if (buffer == null && line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return line;
        }

        return "";
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

            var result = ShouldRunZfi057Workflow(pars)
                ? ExecuteZfi057Workflow(pars)
                : LaunchSapGuiAndExecute(pars);
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
            {
                return new SapLocalConfig
                {
                    MultiLogonPolicy = LoadRuntimeMultiLogonPolicy()
                };
            }

            string json = File.ReadAllText(ConfigFilePath, Encoding.UTF8);
            var config = JsonSerializer.Deserialize<SapLocalConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            }) ?? new SapLocalConfig();

            config.Password = ResolveLocalPassword(config);
            config.MultiLogonPolicy = FirstNonEmpty(LoadRuntimeMultiLogonPolicy(), config.MultiLogonPolicy ?? "");
            return config;
        }
        catch (Exception ex)
        {
            Log($"读取本机配置失败: {ConfigFilePath}, {ex.Message}");
            return new SapLocalConfig();
        }
    }

    static string LoadRuntimeMultiLogonPolicy()
    {
        try
        {
            if (!File.Exists(RuntimeLocalConfigFilePath))
                return "";

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(RuntimeLocalConfigFilePath, Encoding.UTF8), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

            return FirstNonEmpty(
                GetJsonString(doc.RootElement, "multiLogonPolicy"),
                GetJsonString(doc.RootElement, "multi_logon_policy"),
                GetJsonString(doc.RootElement, "sapMultiLogonPolicy"));
        }
        catch (Exception ex)
        {
            Log($"read runtime multi-logon policy failed: {RuntimeLocalConfigFilePath}, {ex.Message}");
            return "";
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
        p.MultiLogonPolicy = ResolveSapMultiLogonPolicy(FirstNonEmpty(
            Environment.GetEnvironmentVariable("SAP_RPA_MULTI_LOGON_POLICY") ?? "",
            local.MultiLogonPolicy ?? "",
            p.MultiLogonPolicy));
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
            MultiLogonPolicy = ResolveSapMultiLogonPolicy(FirstNonEmpty(
                Environment.GetEnvironmentVariable("SAP_RPA_MULTI_LOGON_POLICY") ?? "",
                local.MultiLogonPolicy ?? "",
                First(query, "multilogonpolicy", "multi_logon_policy", "sapmultilogonpolicy") ?? "")),
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
            Materials = First(query, "materials", "materiallist", "matnrs", "matnrlist", "s_matnr") ?? "",
            FactoryGroup = First(query, "factorygroup", "plantgroup") ?? "",
            RunStrategy = First(query, "runstrategy", "strategy") ?? "",
            Field1Name = First(query, "field1", "field1name") ?? "",
            Field1Value = First(query, "value1", "field1value") ?? "",
            Field2Name = First(query, "field2", "field2name") ?? "",
            Field2Value = First(query, "value2", "field2value") ?? "",
            ButtonId = First(query, "button", "buttonid") ?? "",
            RunId = First(query, "runid", "run_id") ?? "",
            ParentRunId = First(query, "parentrunid", "parent_run_id") ?? "",
            TimeoutSeconds = ParseOptionalPositiveInt(First(query, "timeoutseconds", "timeout", "vbstimeoutseconds"))
        };

        ApplyScriptDefaults(p);
        NormalizeBatchParams(p);
        ApplyTransactionConfigForRun(p);
        ApplyProtocolDefaultExecutionDateParams(p);
        ValidateLoginConfig(p);
        return p;
    }

    static void NormalizeBatchParams(SapRunParams p)
    {
        if (UsesDateRangeOnlyInputs(p.TCode))
        {
            p.Plants = "";
            p.Plant = "";
            p.BusinessAreas = "";
            p.BusinessArea = "";
            p.FactoryGroup = "";
            return;
        }

        p.Plants = NormalizeCsv(FirstNonEmpty(p.Plants, p.Plant));
        p.BusinessAreas = NormalizeCsv(FirstNonEmpty(p.BusinessAreas, p.BusinessArea));
        p.Plant = FirstCsvValue(p.Plants);
        p.BusinessArea = FirstCsvValue(p.BusinessAreas);
    }

    static void ApplyProtocolDefaultExecutionDateParams(SapRunParams p)
    {
        if (!UsesWeeklyDateFallback(p.TCode) && !UsesBudatDateRange(p.TCode))
            return;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["year"] = p.Year,
            ["week"] = p.Week,
            ["period"] = p.Period,
            ["weekEnd"] = p.WeekEnd
        };

        AddDefaultExecutionDateParams(p.TCode, values);
        p.Year = GetParamValue(values, "year");
        p.Week = GetParamValue(values, "week");
        p.Period = GetParamValue(values, "period");
        p.WeekEnd = GetParamValue(values, "weekEnd");
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

    static int? ParseOptionalPositiveInt(string? value)
    {
        if (int.TryParse(value, out int parsed) && parsed > 0)
            return parsed;

        return null;
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
        int timeoutSeconds = 0;

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
SELECT script_file, automation, default_group, timeout_seconds
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
                timeoutSeconds = reader.GetInt32(3);
            }
        }

        if (timeoutSeconds > 0 && (!p.TimeoutSeconds.HasValue || p.TimeoutSeconds.Value <= 0))
            p.TimeoutSeconds = timeoutSeconds;

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

    static string ResolveSapMultiLogonPolicy(string value)
    {
        string normalized = Regex.Replace((value ?? "").Trim().ToLowerInvariant(), @"[\s_\-]+", "");
        if (string.IsNullOrWhiteSpace(normalized))
            return "takeover";

        if (normalized is "fail" or "stop" or "error" or "off" or "false" or "0" or "disabled" or "disable")
            return "fail";

        if (normalized is "takeover" or "takeoverlogin" or "force" or "kill" or "killothers" or
            "terminateothers" or "terminateotherlogons" or "terminatelogons" or "continueandterminateothers")
            return "takeover";

        Log($"Unknown SAP multi-logon policy '{value}', fallback to fail");
        return "fail";
    }

    static bool ShouldTakeOverSapMultiLogon(SapRunParams p)
    {
        return ResolveSapMultiLogonPolicy(p.MultiLogonPolicy).Equals("takeover", StringComparison.OrdinalIgnoreCase);
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

        var scheduleWorker = new Thread(ProcessScheduleLoop)
        {
            IsBackground = true,
            Name = "SapRpaScheduleWorker"
        };
        scheduleWorker.Start();
        Log("定时任务调度后台线程已启动");

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
                    alvExportDataRoot = AlvExportDataDirectory,
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
                (path.Equals("/api/schema", StringComparison.OrdinalIgnoreCase) ||
                 path.Equals("/api/config/schema", StringComparison.OrdinalIgnoreCase)))
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

            if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                path.Equals("/api/queue/status", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context.Response, LoadQueueStatus(context.Request));
                return;
            }

            Match tablePreviewMatch = Regex.Match(path, @"^/api/(?:config/)?schema/tables/([A-Za-z0-9_]+)$", RegexOptions.IgnoreCase);
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

            if ((path.Equals("/api/schedules", StringComparison.OrdinalIgnoreCase) ||
                 path.Equals("/api/config/schedule-tasks", StringComparison.OrdinalIgnoreCase)) &&
                context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context.Response, LoadScheduleTasks());
                return;
            }

            if ((path.Equals("/api/schedules", StringComparison.OrdinalIgnoreCase) ||
                 path.Equals("/api/config/schedule-tasks", StringComparison.OrdinalIgnoreCase)) &&
                context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                var item = ReadJson<ScheduleTaskRequest>(context.Request);
                string id = UpsertScheduleTask(item, routeId: "");
                WriteJson(context.Response, new { ok = true, id, schedule = LoadScheduleTask(id) });
                return;
            }

            Match scheduleMatch = Regex.Match(path, @"^/api/(?:schedules|config/schedule-tasks)/([A-Za-z0-9_.-]+)$", RegexOptions.IgnoreCase);
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
                WriteJson(context.Response, new { ok = true, id, schedule = LoadScheduleTask(id) });
                return;
            }

            if (scheduleMatch.Success &&
                context.Request.HttpMethod.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                string id = SanitizeConfigId(scheduleMatch.Groups[1].Value, "schedule id");
                bool deleted = DeleteScheduleTask(id);
                if (!deleted)
                {
                    WriteJson(context.Response, new { error = $"schedule not found: {id}" }, 404);
                    return;
                }

                WriteJson(context.Response, new { ok = true, id, deleted = true });
                return;
            }

            if (path.Equals("/api/runs", StringComparison.OrdinalIgnoreCase) &&
                context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                var request = ReadJson<CreateRunRequest>(context.Request);
                var run = CreateRun(request);
                var position = LoadQueuePosition(run.RunId);
                WriteJson(context.Response, new
                {
                    runId = run.RunId,
                    status = run.Status,
                    runType = run.RunType,
                    parentRunId = run.ParentRunId,
                    childRunIds = run.ChildRunIds,
                    batchTotal = run.BatchTotal,
                    queuedAt = run.QueuedAt,
                    queuePosition = position.QueuePosition,
                    runsAhead = position.RunsAhead,
                    workItemsAhead = position.WorkItemsAhead,
                    runningRunId = position.RunningRunId,
                    queuedCount = position.QueuedCount
                });
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

            Match rerunFailedMatch = Regex.Match(path, @"^/api/runs/([A-Za-z0-9_.-]+)/rerun-failed$", RegexOptions.IgnoreCase);
            if (rerunFailedMatch.Success &&
                context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                var result = RerunFailedBatchItems(rerunFailedMatch.Groups[1].Value);
                WriteJson(context.Response, result);
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

        var value = JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonOptions)
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }) ?? throw new InvalidOperationException("Invalid JSON request body.");

        if (value is IRawJsonRequest rawJsonRequest)
            rawJsonRequest.CaptureRawJson(json);

        return value;
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
        if (DatabaseInitialized)
            return;

        lock (DatabaseInitLock)
        {
            if (DatabaseInitialized)
                return;

            EnsureRuntimeDirectories();
            MigrateLegacyDatabaseIfNeeded();
            using var connection = OpenDatabaseConnection();
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = """
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA busy_timeout=10000;
""";
                pragma.ExecuteNonQuery();
            }

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
    ding_talk_user_id TEXT NOT NULL DEFAULT '',
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
    run_type TEXT NOT NULL DEFAULT 'single',
    parent_run_id TEXT NOT NULL DEFAULT '',
    batch_item_key TEXT NOT NULL DEFAULT '',
    batch_index INTEGER NOT NULL DEFAULT 0,
    batch_total INTEGER NOT NULL DEFAULT 0,
    attempt_no INTEGER NOT NULL DEFAULT 1,
    summary_json TEXT NOT NULL DEFAULT '',
    source_parent_run_id TEXT NOT NULL DEFAULT '',
    rerun_of_run_id TEXT NOT NULL DEFAULT '',
    locked_by TEXT NOT NULL DEFAULT '',
    locked_at TEXT NOT NULL DEFAULT '',
    queued_at TEXT NOT NULL DEFAULT '',
    started_at TEXT NOT NULL DEFAULT '',
    finished_at TEXT NOT NULL DEFAULT '',
    duration_ms INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_runs_status_queued_at ON runs(status, queued_at);
CREATE INDEX IF NOT EXISTS idx_runs_transaction_finished ON runs(transaction_code, finished_at);
CREATE TABLE IF NOT EXISTS run_batch_items (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    parent_run_id TEXT NOT NULL,
    child_run_id TEXT NOT NULL,
    plant_code TEXT NOT NULL DEFAULT '',
    batch_index INTEGER NOT NULL DEFAULT 0,
    attempt_no INTEGER NOT NULL DEFAULT 1,
    status TEXT NOT NULL DEFAULT 'queued',
    message TEXT NOT NULL DEFAULT '',
    started_at TEXT NOT NULL DEFAULT '',
    finished_at TEXT NOT NULL DEFAULT '',
    duration_ms INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now', 'localtime')),
    updated_at TEXT NOT NULL DEFAULT (datetime('now', 'localtime')),
    UNIQUE(child_run_id)
);
CREATE INDEX IF NOT EXISTS idx_run_batch_items_parent ON run_batch_items(parent_run_id, batch_index, attempt_no);
CREATE INDEX IF NOT EXISTS idx_run_batch_items_status ON run_batch_items(parent_run_id, status);
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
    notify_on_start INTEGER NOT NULL DEFAULT 1,
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
            UpsertAppSetting(connection, "alv_export_data_root", AlvExportDataDirectory);
            UpsertAppSetting(connection, "database_path", DatabaseFilePath);
            UpsertAppSetting(connection, "credential_config_path", ConfigFilePath);

            EnsureColumn(connection, "runs", "source", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, "runs", "notify_target", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, "runs", "ding_talk_user_id", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, "runs", "priority", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, "runs", "attempt", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, "runs", "max_attempts", "INTEGER NOT NULL DEFAULT 1");
            EnsureColumn(connection, "runs", "run_type", "TEXT NOT NULL DEFAULT 'single'");
            EnsureColumn(connection, "runs", "parent_run_id", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, "runs", "batch_item_key", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, "runs", "batch_index", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, "runs", "batch_total", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, "runs", "attempt_no", "INTEGER NOT NULL DEFAULT 1");
            EnsureColumn(connection, "runs", "summary_json", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, "runs", "source_parent_run_id", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, "runs", "rerun_of_run_id", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, "runs", "locked_by", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, "runs", "locked_at", "TEXT NOT NULL DEFAULT ''");
            EnsureIndex(connection, "idx_runs_parent", "runs", "parent_run_id, batch_index, attempt_no");
            EnsureColumn(connection, "transactions", "timeout_seconds", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, "transactions", "retry_count", "INTEGER NOT NULL DEFAULT 0");
            EnsureScheduleColumns(connection);
            EnsureBasicConfigColumns(connection);

        if (seedFromScripts)
        {
            bool hasTransactions = CountTransactions(connection) > 0;
            SeedTransactions(connection, upsertExisting: !hasTransactions);

            SeedBasicConfig(connection);
            SyncDisabledTransactionsFromConfig(connection);
            SyncTransactionRulesFromTransactions(connection);
        }

            Log($"SQLite 数据库初始化完成: {DatabaseFilePath}");
            DatabaseInitialized = true;
        }
    }

    static SqliteConnection OpenDatabaseConnection()
    {
        EnsureRuntimeDirectories();
        var connection = new SqliteConnection($"Data Source={DatabaseFilePath};Cache=Shared;Default Timeout=10");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=10000;";
        command.ExecuteNonQuery();
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

    static void EnsureIndex(SqliteConnection connection, string indexName, string tableName, string columns)
    {
        if (!Regex.IsMatch(indexName, @"^[A-Za-z0-9_]+$") ||
            !Regex.IsMatch(tableName, @"^[A-Za-z0-9_]+$") ||
            !Regex.IsMatch(columns, @"^[A-Za-z0-9_,\s]+$"))
        {
            throw new InvalidOperationException($"Unsafe SQLite index definition: {indexName}");
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE INDEX IF NOT EXISTS {indexName} ON {tableName}({columns})";
        command.ExecuteNonQuery();
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
        EnsureColumn(connection, "schedule_tasks", "notify_on_start", "INTEGER NOT NULL DEFAULT 1");
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

    static void SyncTransactionRulesFromTransactions(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
UPDATE transaction_plant_rules
SET factory_rule = COALESCE((SELECT factory_rule FROM transactions WHERE transactions.tcode = transaction_plant_rules.tcode), factory_rule),
    default_group = COALESCE((SELECT default_group FROM transactions WHERE transactions.tcode = transaction_plant_rules.tcode), default_group),
    enabled = COALESCE((SELECT enabled FROM transactions WHERE transactions.tcode = transaction_plant_rules.tcode), enabled),
    updated_at = datetime('now', 'localtime'),
    updated_by = CASE
        WHEN updated_by IN ('', 'seed', 'seed-migration') THEN 'seed-migration'
        ELSE updated_by
    END
WHERE EXISTS (SELECT 1 FROM transactions WHERE transactions.tcode = transaction_plant_rules.tcode)
  AND (
      factory_rule <> COALESCE((SELECT factory_rule FROM transactions WHERE transactions.tcode = transaction_plant_rules.tcode), factory_rule)
      OR default_group <> COALESCE((SELECT default_group FROM transactions WHERE transactions.tcode = transaction_plant_rules.tcode), default_group)
      OR enabled <> COALESCE((SELECT enabled FROM transactions WHERE transactions.tcode = transaction_plant_rules.tcode), enabled)
  );
""";
        command.ExecuteNonQuery();
    }

    static void SeedTransactions(SqliteConnection connection, bool upsertExisting)
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
            bool enabled = GetJsonBool(item, "enabled", defaultValue: true);

            using var command = connection.CreateCommand();
            command.CommandText = upsertExisting ? """
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
    timeout_seconds=excluded.timeout_seconds,
    retry_count=excluded.retry_count,
    script_version=excluded.script_version,
    script_hash=excluded.script_hash,
    script_metadata_json=excluded.script_metadata_json,
    enabled=excluded.enabled,
    updated_at=excluded.updated_at;
""" : """
INSERT OR IGNORE INTO transactions (
    tcode, name, stage, script_file, icon, params_json, factory_rule, fixed_plants_json,
    default_group, automation, timeout_seconds, retry_count,
    script_version, script_hash, script_metadata_json, enabled, updated_at
) VALUES (
    $tcode, $name, $stage, $scriptFile, $icon, $paramsJson, $factoryRule, $fixedPlantsJson,
    $defaultGroup, $automation, $timeoutSeconds, $retryCount,
    $scriptVersion, $scriptHash, $metadataJson, $enabled, $updatedAt
);
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
            command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            command.Parameters.AddWithValue("$updatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            command.ExecuteNonQuery();

            if (!string.IsNullOrWhiteSpace(scriptText))
                UpsertScriptCache(connection, tcode, scriptFile, scriptHash, scriptText);
        }
    }

    static void SyncDisabledTransactionsFromConfig(SqliteConnection connection)
    {
        string configPath = FindTransactionConfigPath();
        if (!File.Exists(configPath))
            return;

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(configPath, Encoding.UTF8));
        if (!doc.RootElement.TryGetProperty("transactions", out JsonElement transactions) ||
            transactions.ValueKind != JsonValueKind.Array)
            return;

        string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        foreach (JsonElement item in transactions.EnumerateArray())
        {
            string tcode = GetJsonString(item, "code").ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(tcode) ||
                !item.TryGetProperty("enabled", out JsonElement enabledValue) ||
                enabledValue.ValueKind != JsonValueKind.False)
                continue;

            using var command = connection.CreateCommand();
            command.CommandText = """
UPDATE transactions
SET enabled=0, updated_at=$updatedAt
WHERE tcode=$tcode;

UPDATE transaction_plant_rules
SET enabled=0, updated_at=$updatedAt, updated_by='seed-migration'
WHERE tcode=$tcode
  AND updated_by IN ('', 'seed', 'seed-migration');
""";
            command.Parameters.AddWithValue("$tcode", tcode);
            command.Parameters.AddWithValue("$updatedAt", now);
            command.ExecuteNonQuery();
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
        using var tx = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "UPDATE transactions SET enabled=$enabled, updated_at=$updatedAt WHERE tcode=$tcode";
        command.Parameters.AddWithValue("$tcode", tcode.ToUpperInvariant());
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        command.ExecuteNonQuery();

        using var ruleCommand = connection.CreateCommand();
        ruleCommand.Transaction = tx;
        ruleCommand.CommandText = "UPDATE transaction_plant_rules SET enabled=$enabled, updated_at=$updatedAt, updated_by='api' WHERE tcode=$tcode";
        ruleCommand.Parameters.AddWithValue("$tcode", tcode.ToUpperInvariant());
        ruleCommand.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        ruleCommand.Parameters.AddWithValue("$updatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        ruleCommand.ExecuteNonQuery();
        tx.Commit();
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
        var schedules = LoadScheduleTaskConfig(connection);

        return new
        {
            version = 2,
            source = "sqlite",
            database = DatabaseFilePath,
            transactions,
            plants,
            plantGroups = groups,
            rules,
            transactionRules = rules,
            notificationRobots = robots,
            notificationRobotBindings = robotBindings,
            notificationBindings = robotBindings,
            scheduleTasks = schedules,
            schedules
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
        DateTime defaultTo = DateTime.Now.Date.AddDays(1).AddTicks(-1);
        DateTime to = ParseReportDate(request.QueryString["to"], defaultTo, isEndDate: true);
        DateTime from = ParseReportDate(request.QueryString["from"], to.Date, isEndDate: false);
        if (from > to)
            (from, to) = (to, from);

        string fromText = from.ToString("yyyy-MM-dd HH:mm:ss");
        string toText = to.ToString("yyyy-MM-dd HH:mm:ss");
        using var connection = OpenDatabaseConnection();

        long totalRuns = 0;
        long successRuns = 0;
        long failedRuns = 0;
        double avgDurationSeconds = 0;
        double totalDurationSeconds = 0;
        using (var summary = connection.CreateCommand())
        {
            summary.CommandText = """
SELECT COUNT(*),
       SUM(CASE WHEN status='success' THEN 1 ELSE 0 END),
       SUM(CASE WHEN status IN ('failed', 'partial_failed') THEN 1 ELSE 0 END),
       AVG(CASE WHEN duration_ms > 0 THEN duration_ms / 1000.0 ELSE NULL END),
       SUM(CASE WHEN duration_ms > 0 THEN duration_ms / 1000.0 ELSE 0 END)
FROM runs
WHERE COALESCE(NULLIF(finished_at, ''), queued_at) >= $from
  AND COALESCE(NULLIF(finished_at, ''), queued_at) <= $to
  AND COALESCE(NULLIF(run_type, ''), 'single') IN ('single', 'child')
  AND status IN ('success', 'failed', 'partial_failed');
""";
            summary.Parameters.AddWithValue("$from", fromText);
            summary.Parameters.AddWithValue("$to", toText);
            using var reader = summary.ExecuteReader();
            if (reader.Read())
            {
                totalRuns = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                successRuns = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                failedRuns = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                avgDurationSeconds = reader.IsDBNull(3) ? 0 : Math.Round(reader.GetDouble(3), 1);
                totalDurationSeconds = reader.IsDBNull(4) ? 0 : Math.Round(reader.GetDouble(4), 1);
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
       SUM(CASE WHEN r.status IN ('failed', 'partial_failed') THEN 1 ELSE 0 END) AS failed_runs,
       AVG(CASE WHEN r.duration_ms > 0 THEN r.duration_ms / 1000.0 ELSE NULL END) AS avg_duration_seconds,
       SUM(CASE WHEN r.duration_ms > 0 THEN r.duration_ms / 1000.0 ELSE 0 END) AS total_duration_seconds
FROM runs r
LEFT JOIN transactions t ON t.tcode = r.transaction_code
WHERE COALESCE(NULLIF(r.finished_at, ''), r.queued_at) >= $from
  AND COALESCE(NULLIF(r.finished_at, ''), r.queued_at) <= $to
  AND COALESCE(NULLIF(r.run_type, ''), 'single') IN ('single', 'child')
  AND r.status IN ('success', 'failed', 'partial_failed')
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
                long txFailed = reader.IsDBNull(4) ? 0 : reader.GetInt64(4);
                transactionRanking.Add(new
                {
                    transactionCode = reader.GetString(0),
                    transactionName = reader.GetString(1),
                    totalRuns = txTotal,
                    successRuns = txSuccess,
                    failedRuns = txFailed,
                    successRate = txTotal == 0 ? 0 : Math.Round(txSuccess * 1.0 / txTotal, 4),
                    avgDurationSeconds = reader.IsDBNull(5) ? 0 : Math.Round(reader.GetDouble(5), 1),
                    totalDurationSeconds = reader.IsDBNull(6) ? 0 : Math.Round(reader.GetDouble(6), 1)
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
  AND COALESCE(NULLIF(r.finished_at, ''), r.queued_at) <= $to
  AND COALESCE(NULLIF(r.run_type, ''), 'single') IN ('single', 'child')
  AND r.status IN ('success', 'failed', 'partial_failed');
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
                else
                    item.FailedRuns++;
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
                failedRuns = p.Value.FailedRuns,
                successRate = p.Value.TotalRuns == 0 ? 0 : Math.Round(p.Value.SuccessRuns * 1.0 / p.Value.TotalRuns, 4),
                avgDurationSeconds = p.Value.DurationCount == 0 ? 0 : Math.Round(p.Value.DurationTotalMs / 1000.0 / p.Value.DurationCount, 1),
                totalDurationSeconds = Math.Round(p.Value.DurationTotalMs / 1000.0, 1)
            })
            .ToList();

        return new
        {
            version = 1,
            source = "sqlite",
            database = DatabaseFilePath,
            period = new { from = fromText, to = toText },
            assumptions = new
            {
                grain = "transaction_execution",
                includedRunTypes = new[] { "single", "child" },
                includedStatuses = new[] { "success", "failed", "partial_failed" },
                excludedRunTypes = new[] { "parent" },
                excludedStatuses = new[] { "queued", "running", "canceled" }
            },
            summary = new
            {
                totalRuns,
                successRuns,
                failedRuns,
                successRate = totalRuns == 0 ? 0 : Math.Round(successRuns * 1.0 / totalRuns, 4),
                avgDurationSeconds,
                totalDurationSeconds
            },
            transactionRanking,
            plantStats
        };
    }

    static DateTime ParseReportDate(string? value, DateTime fallback, bool isEndDate)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        if (!DateTime.TryParse(value, out DateTime parsed))
            return fallback;

        string trimmed = value.Trim();
        bool dateOnly = !trimmed.Contains(':') && !trimmed.Contains('T') && !trimmed.Contains(' ');
        if (dateOnly)
            return isEndDate ? parsed.Date.AddDays(1).AddTicks(-1) : parsed.Date;

        return parsed;
    }

    static object LoadScheduleTasks()
    {
        InitializeDatabase(seedFromScripts: true);
        using var connection = OpenDatabaseConnection();
        var items = LoadScheduleTaskConfig(connection);

        return new
        {
            version = 2,
            source = "sqlite",
            database = DatabaseFilePath,
            scheduleTasks = items,
            schedules = items
        };
    }

    static List<object> LoadScheduleTaskConfig(SqliteConnection connection)
    {
        var items = new List<object>();
        using var command = connection.CreateCommand();
        command.CommandText = """
SELECT id, name, tcode, plants_json, default_business_scope, cron, frequency, run_time,
       enabled, notify_enabled, notify_on_start, notify_on_success, notify_on_failure, notify_target,
       params_json, created_at, updated_at, created_by, updated_by
FROM schedule_tasks
ORDER BY enabled DESC, updated_at DESC, id;
""";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            items.Add(ReadScheduleTask(reader));

        return items;
    }

    static object? LoadScheduleTask(string id)
    {
        InitializeDatabase(seedFromScripts: true);
        id = SanitizeConfigId(id, "schedule id");
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
SELECT id, name, tcode, plants_json, default_business_scope, cron, frequency, run_time,
       enabled, notify_enabled, notify_on_start, notify_on_success, notify_on_failure, notify_target,
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
        string defaultBusinessScope = FirstNonEmpty(item.DefaultBusinessScope, item.FactoryGroup, item.PlantGroupId, item.GroupId, item.DefaultPlantGroup);
        string plantsCsv = ResolveSchedulePlantsCsvFromRequest(item);
        if (UsesDateRangeOnlyInputs(tcode))
            plantsCsv = "";
        else if (!item.HasExplicitPlantSelection)
            plantsCsv = ResolveSchedulePlants(tcode, defaultBusinessScope);
        string plantsJson = CsvToJsonArray(plantsCsv);
        string paramsJson = BuildScheduleParamsJson(item, tcode, defaultBusinessScope, plantsCsv);
        string rawFrequency = FirstNonEmpty(item.Frequency, item.ScheduleType, item.FrequencyCode);
        string frequency = string.IsNullOrWhiteSpace(rawFrequency) && !string.IsNullOrWhiteSpace(item.Cron)
            ? ""
            : NormalizeScheduleFrequency(FirstNonEmpty(rawFrequency, "daily"));
        string runTime = NormalizeScheduleRunTime(FirstNonEmpty(item.Time, item.RunTime, item.ExecTime, item.RunAt, item.StartTime));
        bool notifyOnStart = item.NotifyStart ?? false;
        bool notifyOnSuccess = item.NotifyOnSuccess ?? item.NotifySuccess ?? false;
        bool notifyOnFailure = item.NotifyOnFailure ?? item.NotifyFail ?? true;
        bool notifyEnabled = item.NotifyEnabled ?? item.Notify ?? (notifyOnStart || notifyOnSuccess || notifyOnFailure);

        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO schedule_tasks(
    id, name, tcode, plants_json, default_business_scope, cron, frequency, run_time,
    enabled, notify_enabled, notify_on_start, notify_on_success, notify_on_failure, notify_target,
    params_json, created_at, updated_at, created_by, updated_by
) VALUES(
    $id, $name, $tcode, $plantsJson, $defaultBusinessScope, $cron, $frequency, $runTime,
    $enabled, $notifyEnabled, $notifyOnStart, $notifyOnSuccess, $notifyOnFailure, $notifyTarget,
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
    notify_on_start=excluded.notify_on_start,
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
        command.Parameters.AddWithValue("$defaultBusinessScope", defaultBusinessScope);
        command.Parameters.AddWithValue("$cron", item.Cron ?? "");
        command.Parameters.AddWithValue("$frequency", frequency);
        command.Parameters.AddWithValue("$runTime", runTime);
        command.Parameters.AddWithValue("$enabled", item.Enabled.GetValueOrDefault(true) ? 1 : 0);
        command.Parameters.AddWithValue("$notifyEnabled", notifyEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$notifyOnStart", notifyOnStart ? 1 : 0);
        command.Parameters.AddWithValue("$notifyOnSuccess", notifyOnSuccess ? 1 : 0);
        command.Parameters.AddWithValue("$notifyOnFailure", notifyOnFailure ? 1 : 0);
        command.Parameters.AddWithValue("$notifyTarget", item.NotifyTarget ?? "");
        command.Parameters.AddWithValue("$paramsJson", paramsJson);
        command.Parameters.AddWithValue("$createdAt", now);
        command.Parameters.AddWithValue("$updatedAt", now);
        command.Parameters.AddWithValue("$createdBy", FirstNonEmpty(item.CreatedBy, item.UpdatedBy, "api"));
        command.Parameters.AddWithValue("$updatedBy", FirstNonEmpty(item.UpdatedBy, item.CreatedBy, "api"));
        command.ExecuteNonQuery();
        return id;
    }

    static string ResolveSchedulePlantsCsvFromRequest(ScheduleTaskRequest item)
    {
        return FirstNonEmpty(
            item.PlantsCsv,
            JsonElementToCsv(item.Plants),
            JsonElementToCsv(item.PlantCodes),
            JsonElementToCsv(item.FactoryCodes));
    }

    static string JsonElementToCsv(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Array => JsonElementArrayToCsv(value),
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            _ => ""
        };
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

    static bool DeleteScheduleTask(string id)
    {
        InitializeDatabase(seedFromScripts: true);
        using var connection = OpenDatabaseConnection();
        using var tx = connection.BeginTransaction();

        using (var exists = connection.CreateCommand())
        {
            exists.Transaction = tx;
            exists.CommandText = "SELECT COUNT(*) FROM schedule_tasks WHERE id=$id";
            exists.Parameters.AddWithValue("$id", id);
            if (Convert.ToInt64(exists.ExecuteScalar() ?? 0L) == 0)
            {
                tx.Rollback();
                return false;
            }
        }

        using (var deleteRuns = connection.CreateCommand())
        {
            deleteRuns.Transaction = tx;
            deleteRuns.CommandText = "DELETE FROM schedule_task_runs WHERE task_id=$id";
            deleteRuns.Parameters.AddWithValue("$id", id);
            deleteRuns.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "DELETE FROM schedule_tasks WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        int affected = command.ExecuteNonQuery();
        tx.Commit();
        return affected > 0;
    }

    static object ReadScheduleTask(SqliteDataReader reader)
    {
        string id = reader.GetString(0);
        string tcode = reader.GetString(2);
        string defaultBusinessScope = reader.GetString(4);
        string frequency = reader.GetString(6);
        string runTime = reader.GetString(7);
        bool enabled = reader.GetInt32(8) == 1;
        string createdAt = reader.GetString(15);
        string updatedAt = reader.GetString(16);
        string nextRunAt = enabled ? CalculateNextScheduleRunAt(frequency, runTime, createdAt) : "";
        return new
        {
            id,
            taskId = id,
            name = reader.GetString(1),
            tcode,
            transactionCode = tcode,
            code = tcode,
            plants = SafeJsonArray(reader.GetString(3)),
            defaultBusinessScope,
            factoryGroup = defaultBusinessScope,
            plantGroupId = defaultBusinessScope,
            defaultPlantGroup = defaultBusinessScope,
            cron = reader.GetString(5),
            frequency,
            scheduleType = frequency,
            time = runTime,
            execTime = runTime,
            runTime,
            enabled,
            status = enabled ? "enabled" : "disabled",
            nextRunAt,
            nextExecutionTime = nextRunAt,
            notify = new
            {
                enabled = reader.GetInt32(9) == 1,
                onStart = reader.GetInt32(10) == 1,
                onSuccess = reader.GetInt32(11) == 1,
                onFailure = reader.GetInt32(12) == 1,
                target = reader.GetString(13)
            },
            paramsJson = reader.GetString(14),
            createdAt,
            updatedAt,
            createdBy = reader.GetString(17),
            updatedBy = reader.GetString(18)
        };
    }

    static string ResolveSchedulePlants(string tcode, string plantGroupId)
    {
        if (string.IsNullOrWhiteSpace(plantGroupId))
            return "";

        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        if (tcode.Equals("ZFI072A", StringComparison.OrdinalIgnoreCase))
        {
            command.CommandText = """
SELECT zfi072_plants_json
FROM plant_groups
WHERE id=$id AND enabled=1;
""";
            command.Parameters.AddWithValue("$id", plantGroupId);
            string zfi072PlantsJson = command.ExecuteScalar() as string ?? "";
            string zfi072Plants = string.Join(",", SafeJsonArray(zfi072PlantsJson));
            if (!string.IsNullOrWhiteSpace(zfi072Plants))
                return zfi072Plants;
        }

        command.CommandText = """
SELECT plant_code
FROM plant_group_members
WHERE group_id=$id
ORDER BY sort_order, plant_code;
""";
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$id", plantGroupId);
        var plants = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            plants.Add(reader.GetString(0));

        return string.Join(",", plants);
    }

    static string BuildScheduleParamsJson(ScheduleTaskRequest item, string tcode, string defaultBusinessScope, string plantsCsv)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (item.Params.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in item.Params.EnumerateObject())
            {
                string value = JsonValueToString(prop.Value);
                if (!string.IsNullOrWhiteSpace(value))
                    values[prop.Name] = value;
            }
        }

        string plants = NormalizeCsv(FirstNonEmpty(
            plantsCsv,
            !item.HasExplicitPlantSelection && values.TryGetValue("plants", out string? existingPlants) ? existingPlants ?? "" : ""));
        bool dateRangeOnly = UsesDateRangeOnlyInputs(tcode);
        if (!dateRangeOnly && !string.IsNullOrWhiteSpace(plants))
        {
            values["plants"] = plants;
            values["plant"] = FirstCsvValue(plants);
        }
        else if (item.HasExplicitPlantSelection)
        {
            values.Remove("plants");
            values.Remove("plant");
        }

        if (!string.IsNullOrWhiteSpace(defaultBusinessScope))
            values["factoryGroup"] = defaultBusinessScope;

        string businessAreas = FirstNonEmpty(
            JsonElementArrayToCsv(item.BusinessAreas),
            item.BusinessAreasCsv,
            values.TryGetValue("businessAreas", out string? existingAreas) ? existingAreas ?? "" : "");
        businessAreas = NormalizeCsv(businessAreas);
        if (!dateRangeOnly && !string.IsNullOrWhiteSpace(businessAreas))
        {
            values["businessAreas"] = businessAreas;
            values["businessArea"] = FirstCsvValue(businessAreas);
        }
        else if (dateRangeOnly)
        {
            RemoveScopeParamKeys(values);
        }

        values["tcode"] = tcode;
        return JsonSerializer.Serialize(values, JsonOptions);
    }

    static string NormalizeScheduleFrequency(string value)
    {
        value = (value ?? "").Trim().ToLowerInvariant();
        return value switch
        {
            "day" or "daily" or "everyday" => "daily",
            "week" or "weekly" => "weekly",
            "month" or "monthly" => "monthly",
            _ => string.IsNullOrWhiteSpace(value) ? "daily" : value
        };
    }

    static string NormalizeScheduleRunTime(string value)
    {
        value = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value))
            return "08:00";

        if (TimeSpan.TryParse(value, out TimeSpan parsed))
            return new TimeSpan(parsed.Hours, parsed.Minutes, 0).ToString(@"hh\:mm");

        if (DateTime.TryParse(value, out DateTime parsedDate))
            return parsedDate.ToString("HH:mm");

        return value;
    }

    static string CalculateNextScheduleRunAt(string frequency, string runTime, string anchorText)
    {
        if (string.IsNullOrWhiteSpace(frequency))
            return "";

        if (!TryResolveScheduleSlot(frequency, runTime, anchorText, DateTime.Now, out DateTime slot))
            return "";

        DateTime now = DateTime.Now;
        if (slot <= now)
        {
            string normalized = NormalizeScheduleFrequency(frequency);
            slot = normalized switch
            {
                "weekly" => slot.AddDays(7),
                "monthly" => AddOneScheduleMonth(slot, anchorText),
                _ => slot.AddDays(1)
            };
        }

        return slot.ToString("yyyy-MM-dd HH:mm:ss");
    }

    static void ProcessScheduleLoop()
    {
        while (true)
        {
            try
            {
                TriggerDueScheduleTasks();
            }
            catch (Exception ex)
            {
                Log($"schedule worker failed: {ex}");
            }

            Thread.Sleep(SchedulePollIntervalMilliseconds);
        }
    }

    static void TriggerDueScheduleTasks()
    {
        InitializeDatabase(seedFromScripts: true);
        DateTime now = DateTime.Now;
        var dueTasks = LoadDueScheduleTasks(now);
        foreach (var task in dueTasks)
            TriggerScheduleTask(task, now);
    }

    static List<ScheduleTaskDue> LoadDueScheduleTasks(DateTime now)
    {
        var due = new List<ScheduleTaskDue>();
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
SELECT id, name, tcode, plants_json, default_business_scope, cron, frequency, run_time,
       notify_enabled, notify_on_start, notify_on_success, notify_on_failure, notify_target, params_json, created_at, created_by, updated_by
FROM schedule_tasks
WHERE enabled=1
ORDER BY run_time, id;
""";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string cron = reader.GetString(5);
            string frequency = reader.GetString(6);
            string runTime = reader.GetString(7);
            string createdAt = reader.GetString(14);
            if (!string.IsNullOrWhiteSpace(cron) && string.IsNullOrWhiteSpace(frequency))
                continue;
            if (!TryResolveScheduleSlot(frequency, runTime, createdAt, now, out DateTime scheduledAt))
                continue;

            DateTime lowerBound = now.AddMinutes(-ScheduleTriggerLookbackMinutes);
            if (scheduledAt > now || scheduledAt < lowerBound)
                continue;

            string scheduledAtText = scheduledAt.ToString("yyyy-MM-dd HH:mm:ss");
            string taskId = reader.GetString(0);
            if (ScheduleTriggerExists(taskId, scheduledAtText))
                continue;

            due.Add(new ScheduleTaskDue
            {
                Id = taskId,
                Name = reader.GetString(1),
                TCode = reader.GetString(2),
                Plants = string.Join(",", SafeJsonArray(reader.GetString(3))),
                DefaultBusinessScope = reader.GetString(4),
                Cron = cron,
                Frequency = frequency,
                RunTime = runTime,
                NotifyEnabled = reader.GetInt32(8) == 1,
                NotifyOnStart = reader.GetInt32(9) == 1,
                NotifyOnSuccess = reader.GetInt32(10) == 1,
                NotifyOnFailure = reader.GetInt32(11) == 1,
                NotifyTarget = reader.GetString(12),
                ParamsJson = reader.GetString(13),
                ScheduledAt = scheduledAtText,
                CreatedBy = reader.GetString(15),
                UpdatedBy = reader.GetString(16)
            });
        }

        return due;
    }

    static bool ScheduleTriggerExists(string taskId, string scheduledAt)
    {
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
SELECT COUNT(*)
FROM schedule_task_runs
WHERE task_id=$taskId
  AND scheduled_at=$scheduledAt
  AND trigger_type='schedule';
""";
        command.Parameters.AddWithValue("$taskId", taskId);
        command.Parameters.AddWithValue("$scheduledAt", scheduledAt);
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L) > 0;
    }

    static void TriggerScheduleTask(ScheduleTaskDue task, DateTime triggeredAt)
    {
        long triggerId = InsertScheduleTaskRun(task.Id, "", "schedule", task.ScheduledAt, triggeredAt, "triggering", "");
        try
        {
            var request = BuildRunRequestFromSchedule(task);
            var run = CreateRun(request);
            UpdateScheduleTaskRun(triggerId, run.RunId, "queued", $"queued run {run.RunId}");
            AppendRunLog(run.RunId, "INFO", $"scheduled task triggered: {task.Id}, scheduledAt={task.ScheduledAt}");
            Log($"schedule task triggered: task={task.Id}, run={run.RunId}, scheduledAt={task.ScheduledAt}");
        }
        catch (Exception ex)
        {
            UpdateScheduleTaskRun(triggerId, "", "failed", ex.Message);
            Log($"schedule task trigger failed: task={task.Id}, scheduledAt={task.ScheduledAt}, {ex}");
        }
    }

    static CreateRunRequest BuildRunRequestFromSchedule(ScheduleTaskDue task)
    {
        string scheduleOwner = FirstNonEmpty(task.UpdatedBy, task.CreatedBy, "Schedule Worker");
        string dingTalkUserId = task.NotifyEnabled ? ResolveDingTalkUserIdFromNotifyTarget(task.NotifyTarget) : "";
        var request = new CreateRunRequest
        {
            TransactionCode = task.TCode,
            TCode = task.TCode,
            Code = task.TCode,
            Source = $"schedule:{task.Id}",
            NotifyTarget = task.NotifyEnabled ? NormalizeNotifyTarget(task.NotifyTarget) : "",
            Operator = new OperatorIdentity
            {
                Id = "schedule",
                Name = scheduleOwner,
                Dept = "SapRpa",
                DingTalkUserId = dingTalkUserId,
                Ddid = dingTalkUserId
            },
            Params = ParseScheduleParams(task.ParamsJson)
        };

        string plants = NormalizeCsv(FirstNonEmpty(task.Plants, GetParamValue(request.Params, "plants")));
        if (!UsesDateRangeOnlyInputs(request.TransactionCode ?? request.TCode ?? request.Code ?? "") && !string.IsNullOrWhiteSpace(plants))
        {
            request.Params["plants"] = plants;
            request.Params["plant"] = FirstCsvValue(plants);
        }

        if (!string.IsNullOrWhiteSpace(task.DefaultBusinessScope))
            request.Params["factoryGroup"] = task.DefaultBusinessScope;

        return request;
    }

    static Dictionary<string, string> ParseScheduleParams(string json)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
            return values;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return values;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                string value = JsonValueToString(prop.Value);
                if (!string.IsNullOrWhiteSpace(value))
                    values[prop.Name] = value;
            }
        }
        catch (Exception ex)
        {
            Log($"parse schedule params failed: {ex.Message}");
        }

        return values;
    }

    static long InsertScheduleTaskRun(string taskId, string runId, string triggerType, string scheduledAt, DateTime triggeredAt, string status, string message)
    {
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO schedule_task_runs(task_id, run_id, trigger_type, scheduled_at, triggered_at, status, message, created_at)
VALUES($taskId, $runId, $triggerType, $scheduledAt, $triggeredAt, $status, $message, $createdAt);
SELECT last_insert_rowid();
""";
        string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        command.Parameters.AddWithValue("$taskId", taskId);
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$triggerType", triggerType);
        command.Parameters.AddWithValue("$scheduledAt", scheduledAt);
        command.Parameters.AddWithValue("$triggeredAt", triggeredAt.ToString("yyyy-MM-dd HH:mm:ss"));
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$createdAt", now);
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
    }

    static void UpdateScheduleTaskRun(long id, string runId, string status, string message)
    {
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
UPDATE schedule_task_runs
SET run_id=CASE WHEN $runId='' THEN run_id ELSE $runId END,
    status=$status,
    message=$message
WHERE id=$id;
""";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$message", message);
        command.ExecuteNonQuery();
    }

    static void UpdateScheduleRunStatusForRun(string runId, string status, string message)
    {
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
UPDATE schedule_task_runs
SET status=$status,
    message=$message
WHERE run_id=$runId;
""";
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$message", message);
        command.ExecuteNonQuery();
    }

    static bool TryResolveScheduleSlot(string frequency, string runTime, string anchorText, DateTime now, out DateTime slot)
    {
        slot = default;
        if (!TimeSpan.TryParse(NormalizeScheduleRunTime(runTime), out TimeSpan timeOfDay))
            return false;

        string normalized = NormalizeScheduleFrequency(frequency);
        DateTime anchor = ParseDateOrDefault(anchorText, now);
        slot = normalized switch
        {
            "weekly" => ResolveWeeklyScheduleSlot(anchor, now, timeOfDay),
            "monthly" => ResolveMonthlyScheduleSlot(anchor, now, timeOfDay),
            "daily" => now.Date.Add(timeOfDay),
            _ => default
        };

        return slot != default;
    }

    static DateTime ResolveWeeklyScheduleSlot(DateTime anchor, DateTime now, TimeSpan timeOfDay)
    {
        int diff = ((int)anchor.DayOfWeek - (int)now.DayOfWeek + 7) % 7;
        DateTime slot = now.Date.AddDays(diff).Add(timeOfDay);
        if (slot > now.AddDays(1))
            slot = slot.AddDays(-7);
        return slot;
    }

    static DateTime ResolveMonthlyScheduleSlot(DateTime anchor, DateTime now, TimeSpan timeOfDay)
    {
        int day = Math.Max(1, Math.Min(anchor.Day, DateTime.DaysInMonth(now.Year, now.Month)));
        DateTime slot = new DateTime(now.Year, now.Month, day).Add(timeOfDay);
        if (slot > now)
        {
            DateTime previous = now.AddMonths(-1);
            day = Math.Max(1, Math.Min(anchor.Day, DateTime.DaysInMonth(previous.Year, previous.Month)));
            slot = new DateTime(previous.Year, previous.Month, day).Add(timeOfDay);
        }
        return slot;
    }

    static DateTime AddOneScheduleMonth(DateTime slot, string anchorText)
    {
        DateTime anchor = ParseDateOrDefault(anchorText, slot);
        DateTime next = slot.AddMonths(1);
        int day = Math.Max(1, Math.Min(anchor.Day, DateTime.DaysInMonth(next.Year, next.Month)));
        return new DateTime(next.Year, next.Month, day, slot.Hour, slot.Minute, slot.Second);
    }

    static DateTime ParseDateOrDefault(string value, DateTime fallback)
    {
        return DateTime.TryParse(value, out DateTime parsed) ? parsed : fallback;
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
        if (!UsesDateRangeOnlyInputs(request.TransactionCode ?? request.TCode ?? request.Code ?? "") && !string.IsNullOrWhiteSpace(plants))
        {
            request.Params["plants"] = plants;
            request.Params["plant"] = FirstCsvValue(plants);
        }

        string businessAreas = FirstNonEmpty(
            GetParamValue(request.Params, "businessAreas"),
            GetParamValue(request.Params, "businessareas"),
            GetParamValue(request.Params, "businessArea"),
            GetParamValue(request.Params, "businessarea"),
            GetParamValue(request.Params, "businessAreaList"),
            GetParamValue(request.Params, "businessareaslist"),
            GetParamValue(request.Params, "gsberlist"),
            GetParamValue(request.Params, "gsber"));
        businessAreas = NormalizeCsv(businessAreas);
        if (!UsesDateRangeOnlyInputs(request.TransactionCode ?? request.TCode ?? request.Code ?? "") && !string.IsNullOrWhiteSpace(businessAreas))
        {
            request.Params["businessAreas"] = businessAreas;
            request.Params["businessArea"] = FirstCsvValue(businessAreas);
        }

        if (UsesDateRangeOnlyInputs(request.TransactionCode ?? request.TCode ?? request.Code ?? ""))
        {
            RemoveScopeParamKeys(request.Params);
        }

        AddDefaultExecutionDateParams(request.TransactionCode ?? request.TCode ?? request.Code ?? "", request.Params);
    }

    static void RemoveScopeParamKeys(Dictionary<string, string> values)
    {
        RemoveParamKeys(values,
            "plants", "plant", "plantCodes", "factoryCodes", "werkslist", "plantlist", "werks",
            "businessAreas", "businessareas", "businessArea", "businessarea", "businessAreaList", "businessareaslist", "gsberlist", "gsber",
            "factoryGroup", "factorygroup", "defaultGroup", "defaultgroup", "defaultBusinessScope", "defaultbusinessscope",
            "businessScope", "businessscope", "plantGroup", "plantgroup", "plantGroupId", "plantgroupid", "groupId", "groupid",
            "defaultPlantGroup", "defaultplantgroup");
    }

    static void RemoveParamKeys(Dictionary<string, string> values, params string[] keys)
    {
        foreach (string key in keys)
            values.Remove(key);
    }

    static void AddDefaultExecutionDateParams(string tcode, Dictionary<string, string> values)
    {
        if (!UsesWeeklyDateFallback(tcode) && !UsesBudatDateRange(tcode))
            return;

        DateTime defaultStart = StartOfWeek(DateTime.Today).AddDays(-7);
        DateTime defaultEnd = defaultStart.AddDays(6);

        string period = FirstNonEmpty(
            GetParamValue(values, "period"),
            GetParamValue(values, "startDate"),
            GetParamValue(values, "fromDate"),
            GetParamValue(values, "dateFrom"),
            GetParamValue(values, "beginDate"),
            GetParamValue(values, "dateBegin"));
        string weekEnd = FirstNonEmpty(
            GetParamValue(values, "weekEnd"),
            GetParamValue(values, "week_end"),
            GetParamValue(values, "endDate"),
            GetParamValue(values, "toDate"),
            GetParamValue(values, "dateTo"),
            GetParamValue(values, "dateEnd"));

        DateTime start = ParseFlexibleDateOrDefault(period, defaultStart);
        DateTime end = ParseFlexibleDateOrDefault(weekEnd, defaultEnd);

        if (UsesWeeklyDateFallback(tcode) && string.IsNullOrWhiteSpace(GetParamValue(values, "year")))
            values["year"] = start.Year.ToString();
        if (UsesWeeklyDateFallback(tcode) && string.IsNullOrWhiteSpace(GetParamValue(values, "week")))
            values["week"] = ISOWeek.GetWeekOfYear(start).ToString();
        if (UsesBudatDateRange(tcode) && string.IsNullOrWhiteSpace(GetParamValue(values, "period")))
            values["period"] = FormatSapDate(start);
        if (UsesBudatDateRange(tcode) && string.IsNullOrWhiteSpace(GetParamValue(values, "weekEnd")))
            values["weekEnd"] = FormatSapDate(end);
    }

    static bool UsesWeeklyDateFallback(string tcode)
    {
        string code = FirstNonEmpty(tcode, "").Trim().ToUpperInvariant();
        return code is "ZFI072A" or "ZFI148";
    }

    static bool UsesBudatDateRange(string tcode)
    {
        string code = FirstNonEmpty(tcode, "").Trim().ToUpperInvariant();
        return code is "ZFI072N" or "ZFI080B" or "ZFI080" or "ZCO019" or "ZFI019NA" or "ZFI019NL" or "ZFIR034" or "ZFI057" or "ZCO020" or "ZFI148";
    }

    static bool UsesDateRangeOnlyInputs(string tcode)
    {
        string code = FirstNonEmpty(tcode, "").Trim().ToUpperInvariant();
        return code is "ZFIR034";
    }

    static DateTime StartOfWeek(DateTime date)
    {
        int diff = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return date.Date.AddDays(-diff);
    }

    static DateTime ParseFlexibleDateOrDefault(string value, DateTime fallback)
    {
        string text = FirstNonEmpty(value, "").Trim().Replace(".", "-").Replace("/", "-");
        if (string.IsNullOrWhiteSpace(text))
            return fallback;

        return DateTime.TryParse(text, out DateTime parsed) ? parsed.Date : fallback;
    }

    static string FormatSapDate(DateTime value)
    {
        return value.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);
    }

    static string FormatNullableSapJobTime(DateTime? value)
    {
        return value.HasValue ? value.Value.ToString("yyyyMMdd HHmmss", CultureInfo.InvariantCulture) : "-";
    }

    static string MaskForLog(string value)
    {
        value = FirstNonEmpty(value, "").Trim();
        if (value.Length == 0) return "-";
        if (value.Length <= 2) return "***";
        return value[..1] + "***" + value[^1..];
    }

    static string GetParamValue(Dictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out string? value) ? value ?? "" : "";
    }

    static string ResolveDingTalkUserId(OperatorIdentity? op)
    {
        return FirstNonEmpty(
            op?.DingTalkUserId ?? "",
            op?.Ddid ?? "",
            Environment.GetEnvironmentVariable("SAP_RPA_DINGTALK_ID") ?? "",
            DefaultDingTalkId);
    }

    static void EnsureCreateRunRequestDefaults(CreateRunRequest request)
    {
        request.Operator ??= new OperatorIdentity();
        request.Params ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    static RunRecordView CreateRun(CreateRunRequest request)
    {
        InitializeDatabase(seedFromScripts: true);
        MarkStaleRunningRuns();
        EnsureCreateRunRequestDefaults(request);
        string tcode = SanitizeTCode(FirstNonEmpty(request.TransactionCode, request.TCode, request.Code)).ToUpperInvariant();
        var script = LoadScriptInfo(tcode);
        string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        request.TransactionCode = tcode;
        request.TCode = tcode;
        request.Code = tcode;
        NormalizeCreateRunParams(request);
        string dingTalkUserId = ResolveDingTalkUserId(request.Operator);
        request.Operator.DingTalkUserId = dingTalkUserId;
        if (string.IsNullOrWhiteSpace(request.Operator.Ddid))
            request.Operator.Ddid = dingTalkUserId;

        string[] plants = NormalizeStringArray(GetParamValue(request.Params, "plants"));
        string[] businessAreas = NormalizeStringArray(GetParamValue(request.Params, "businessAreas"));
        var batchPlan = ResolveBatchPlan(tcode, plants, businessAreas);
        if (batchPlan != null)
            return CreateBatchRun(request, script, batchPlan, dingTalkUserId, now);

        string runId = NewRunId(tcode);
        using var connection = OpenDatabaseConnection();
        using var tx = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
INSERT INTO runs(
    run_id, transaction_code, operator_id, operator_name, operator_dept, ding_talk_user_id, status, request_json,
    script_file, script_hash, source, notify_target, priority, max_attempts,
    run_type, batch_total, attempt_no, queued_at
) VALUES(
    $runId, $tcode, $operatorId, $operatorName, $operatorDept, $dingTalkUserId, 'queued', $requestJson,
    $scriptFile, $scriptHash, $source, $notifyTarget, $priority, $maxAttempts,
    'single', 0, 1, $queuedAt
);
""";
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$tcode", tcode);
        command.Parameters.AddWithValue("$operatorId", request.Operator.Id ?? "");
        command.Parameters.AddWithValue("$operatorName", request.Operator.Name ?? "");
        command.Parameters.AddWithValue("$operatorDept", request.Operator.Dept ?? "");
        command.Parameters.AddWithValue("$dingTalkUserId", dingTalkUserId);
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
        AppendRunLog(runId, "INFO", "任务已提交到 SAP 串行队列");

        return new RunRecordView
        {
            RunId = runId,
            TransactionCode = tcode,
            OperatorId = request.Operator.Id ?? "",
            OperatorName = request.Operator.Name ?? "",
            OperatorDept = request.Operator.Dept ?? "",
            DingTalkUserId = dingTalkUserId,
            Status = "queued",
            RequestJson = JsonSerializer.Serialize(request, JsonOptions),
            ScriptFile = script.ScriptFile,
            ScriptHash = script.ScriptHash,
            QueuedAt = now
        };
    }

    static BatchRunPlan? ResolveBatchPlan(string tcode, string[] plants, string[] businessAreas)
    {
        if (tcode.Equals("ZFI072A", StringComparison.OrdinalIgnoreCase) && plants.Length > 1)
            return new BatchRunPlan(tcode, "plants", "plant", "plantlist", "工厂", plants);

        if (UsesPlantBatchItems(tcode) && plants.Length > 1)
            return new BatchRunPlan(tcode, "plants", "plant", "plantlist", "工厂", plants);

        if (UsesBusinessAreaBatchItems(tcode) && businessAreas.Length > 1)
            return new BatchRunPlan(tcode, "businessAreas", "businessArea", "businessAreaList", "业务范围", businessAreas);

        return null;
    }

    static bool UsesPlantBatchItems(string tcode)
    {
        return tcode.Equals("ZFI072N", StringComparison.OrdinalIgnoreCase) ||
               tcode.Equals("ZFI080B", StringComparison.OrdinalIgnoreCase) ||
               tcode.Equals("ZFI148", StringComparison.OrdinalIgnoreCase) ||
               tcode.Equals("ZCO019", StringComparison.OrdinalIgnoreCase) ||
               tcode.Equals("ZFI019NA", StringComparison.OrdinalIgnoreCase) ||
               tcode.Equals("ZFI080", StringComparison.OrdinalIgnoreCase);
    }

    static bool UsesBusinessAreaBatchItems(string tcode)
    {
        return tcode.Equals("ZFI019NL", StringComparison.OrdinalIgnoreCase);
    }

    static RunRecordView CreateBatchRun(CreateRunRequest request, TransactionScriptInfo script, BatchRunPlan plan, string dingTalkUserId, string now)
    {
        string tcode = plan.TCode;
        string parentRunId = NewRunId(tcode);
        int batchTotal = plan.Items.Length;
        var childRunIds = new List<string>();
        string summaryJson = BuildBatchSummaryJson(parentRunId, plan.Items, plan.ItemLabel, Array.Empty<BatchItemStatus>(), "queued");

        using var connection = OpenDatabaseConnection();
        using var tx = connection.BeginTransaction();

        InsertRunRow(connection, tx, parentRunId, request, script, dingTalkUserId, now, "parent", "", "", 0, batchTotal, 1, "running", summaryJson);
        InsertRunParams(connection, tx, parentRunId, request.Params);

        for (int i = 0; i < plan.Items.Length; i++)
        {
            string itemValue = plan.Items[i];
            var childRequest = CloneRunRequestForBatchItem(request, plan, itemValue);
            string childRunId = NewRunId(tcode);
            childRunIds.Add(childRunId);

            InsertRunRow(connection, tx, childRunId, childRequest, script, dingTalkUserId, now, "child", parentRunId, itemValue, i + 1, batchTotal, 1, "queued", "");
            InsertRunParams(connection, tx, childRunId, childRequest.Params);
            InsertRunBatchItem(connection, tx, parentRunId, childRunId, itemValue, i + 1, 1, "queued", "", "", "", 0);
        }

        tx.Commit();

        AppendRunLog(parentRunId, "INFO", $"queued {tcode} parent batch, {plan.ParamKey}={string.Join(",", plan.Items)}");
        foreach (string childRunId in childRunIds)
            AppendRunLog(childRunId, "INFO", $"queued {tcode} child under parent {parentRunId}");

        NotifyRunEvent(parentRunId, "start", $"{tcode} 批次开始：共 {batchTotal} 个{plan.ItemLabel}");

        return new RunRecordView
        {
            RunId = parentRunId,
            TransactionCode = tcode,
            OperatorId = request.Operator.Id ?? "",
            OperatorName = request.Operator.Name ?? "",
            OperatorDept = request.Operator.Dept ?? "",
            DingTalkUserId = dingTalkUserId,
            Status = "running",
            RunType = "parent",
            RequestJson = JsonSerializer.Serialize(request, JsonOptions),
            ScriptFile = script.ScriptFile,
            ScriptHash = script.ScriptHash,
            QueuedAt = now,
            StartedAt = now,
            BatchTotal = batchTotal,
            SummaryJson = summaryJson,
            ChildRunIds = childRunIds
        };
    }

    static string NewRunId(string tcode)
    {
        string rawRunId = $"RUN-{DateTime.Now:yyyyMMddHHmmss}-{tcode}-{Guid.NewGuid():N}";
        return rawRunId[..Math.Min(56, rawRunId.Length)];
    }

    static CreateRunRequest CloneRunRequestForPlant(CreateRunRequest request, string plant)
    {
        return CloneRunRequestForBatchItem(request, new BatchRunPlan(request.TransactionCode ?? request.TCode ?? request.Code ?? "", "plants", "plant", "plantlist", "工厂", new[] { plant }), plant);
    }

    static CreateRunRequest CloneRunRequestForBatchItem(CreateRunRequest request, BatchRunPlan plan, string itemValue)
    {
        var clone = JsonSerializer.Deserialize<CreateRunRequest>(JsonSerializer.Serialize(request, JsonOptions), new JsonSerializerOptions(JsonOptions)
        {
            PropertyNameCaseInsensitive = true
        }) ?? new CreateRunRequest();
        EnsureCreateRunRequestDefaults(clone);
        clone.Params[plan.ParamKey] = itemValue;
        clone.Params[plan.SingleParamKey] = itemValue;
        clone.Params.Remove(plan.ListParamKey);
        if (plan.ParamKey.Equals("plants", StringComparison.OrdinalIgnoreCase))
        {
            clone.Params.Remove("werkslist");
            clone.Params.Remove("plantlist");
            clone.Params.Remove("werks");
        }
        else if (plan.ParamKey.Equals("businessAreas", StringComparison.OrdinalIgnoreCase))
        {
            clone.Params.Remove("businessAreaList");
            clone.Params.Remove("businessareaslist");
            clone.Params.Remove("gsberlist");
            clone.Params.Remove("gsber");
        }
        return clone;
    }

    static void InsertRunRow(SqliteConnection connection, SqliteTransaction tx, string runId, CreateRunRequest request, TransactionScriptInfo script, string dingTalkUserId, string now, string runType, string parentRunId, string batchItemKey, int batchIndex, int batchTotal, int attemptNo, string status, string summaryJson, string sourceParentRunId = "", string rerunOfRunId = "")
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
INSERT INTO runs(
    run_id, transaction_code, operator_id, operator_name, operator_dept, ding_talk_user_id, status, request_json,
    script_file, script_hash, source, notify_target, priority, max_attempts,
    run_type, parent_run_id, batch_item_key, batch_index, batch_total, attempt_no, summary_json,
    source_parent_run_id, rerun_of_run_id,
    queued_at, started_at
) VALUES(
    $runId, $tcode, $operatorId, $operatorName, $operatorDept, $dingTalkUserId, $status, $requestJson,
    $scriptFile, $scriptHash, $source, $notifyTarget, $priority, $maxAttempts,
    $runType, $parentRunId, $batchItemKey, $batchIndex, $batchTotal, $attemptNo, $summaryJson,
    $sourceParentRunId, $rerunOfRunId,
    $queuedAt, $startedAt
);
""";
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$tcode", request.TransactionCode ?? "");
        command.Parameters.AddWithValue("$operatorId", request.Operator.Id ?? "");
        command.Parameters.AddWithValue("$operatorName", request.Operator.Name ?? "");
        command.Parameters.AddWithValue("$operatorDept", request.Operator.Dept ?? "");
        command.Parameters.AddWithValue("$dingTalkUserId", dingTalkUserId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$requestJson", JsonSerializer.Serialize(request, JsonOptions));
        command.Parameters.AddWithValue("$scriptFile", script.ScriptFile);
        command.Parameters.AddWithValue("$scriptHash", script.ScriptHash);
        command.Parameters.AddWithValue("$source", request.Source ?? "");
        command.Parameters.AddWithValue("$notifyTarget", request.NotifyTarget ?? "");
        command.Parameters.AddWithValue("$priority", request.Priority);
        command.Parameters.AddWithValue("$maxAttempts", request.MaxAttempts <= 0 ? 1 : request.MaxAttempts);
        command.Parameters.AddWithValue("$runType", runType);
        command.Parameters.AddWithValue("$parentRunId", parentRunId);
        command.Parameters.AddWithValue("$batchItemKey", batchItemKey);
        command.Parameters.AddWithValue("$batchIndex", batchIndex);
        command.Parameters.AddWithValue("$batchTotal", batchTotal);
        command.Parameters.AddWithValue("$attemptNo", attemptNo);
        command.Parameters.AddWithValue("$summaryJson", summaryJson);
        command.Parameters.AddWithValue("$sourceParentRunId", sourceParentRunId);
        command.Parameters.AddWithValue("$rerunOfRunId", rerunOfRunId);
        command.Parameters.AddWithValue("$queuedAt", now);
        command.Parameters.AddWithValue("$startedAt", status.Equals("running", StringComparison.OrdinalIgnoreCase) ? now : "");
        command.ExecuteNonQuery();
    }

    static void InsertRunParams(SqliteConnection connection, SqliteTransaction tx, string runId, Dictionary<string, string> parameters)
    {
        foreach (var pair in parameters)
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
    }

    static void InsertRunBatchItem(SqliteConnection connection, SqliteTransaction tx, string parentRunId, string childRunId, string plant, int batchIndex, int attemptNo, string status, string message, string startedAt, string finishedAt, long durationMs)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
INSERT INTO run_batch_items(
    parent_run_id, child_run_id, plant_code, batch_index, attempt_no, status,
    message, started_at, finished_at, duration_ms, updated_at
) VALUES(
    $parentRunId, $childRunId, $plant, $batchIndex, $attemptNo, $status,
    $message, $startedAt, $finishedAt, $durationMs, $updatedAt
);
""";
        command.Parameters.AddWithValue("$parentRunId", parentRunId);
        command.Parameters.AddWithValue("$childRunId", childRunId);
        command.Parameters.AddWithValue("$plant", plant);
        command.Parameters.AddWithValue("$batchIndex", batchIndex);
        command.Parameters.AddWithValue("$attemptNo", attemptNo);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$startedAt", startedAt);
        command.Parameters.AddWithValue("$finishedAt", finishedAt);
        command.Parameters.AddWithValue("$durationMs", durationMs);
        command.Parameters.AddWithValue("$updatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        command.ExecuteNonQuery();
    }

    static string UpdateBatchAfterChildCompletion(string childRunId, string status, RunResultRequest result, string finishedAt)
    {
        using var connection = OpenDatabaseConnection();
        string parentRunId = "";
        string itemValue = "";
        int batchIndex = 0;
        int batchTotal = 0;
        using (var lookup = connection.CreateCommand())
        {
            lookup.CommandText = """
SELECT parent_run_id, batch_item_key, batch_index, batch_total
FROM runs
WHERE run_id=$runId AND COALESCE(run_type, 'single')='child' AND parent_run_id<>'';
""";
            lookup.Parameters.AddWithValue("$runId", childRunId);
            using var reader = lookup.ExecuteReader();
            if (!reader.Read())
                return "";

            parentRunId = reader.GetString(0);
            itemValue = reader.GetString(1);
            batchIndex = reader.GetInt32(2);
            batchTotal = reader.GetInt32(3);
        }

        string message = FirstNonEmpty(result.Message ?? "", result.SapStatusText ?? "", status);
        string startedAt = "";
        using (var started = connection.CreateCommand())
        {
            started.CommandText = "SELECT started_at FROM runs WHERE run_id=$runId";
            started.Parameters.AddWithValue("$runId", childRunId);
            startedAt = started.ExecuteScalar() as string ?? "";
        }

        using (var update = connection.CreateCommand())
        {
            update.CommandText = """
UPDATE run_batch_items
SET status=$status,
    message=$message,
    started_at=$startedAt,
    finished_at=$finishedAt,
    duration_ms=$durationMs,
    updated_at=$updatedAt
WHERE child_run_id=$childRunId;
""";
            update.Parameters.AddWithValue("$childRunId", childRunId);
            update.Parameters.AddWithValue("$status", status);
            update.Parameters.AddWithValue("$message", message);
            update.Parameters.AddWithValue("$startedAt", startedAt);
            update.Parameters.AddWithValue("$finishedAt", finishedAt);
            update.Parameters.AddWithValue("$durationMs", result.DurationMs);
            update.Parameters.AddWithValue("$updatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            update.ExecuteNonQuery();
        }

        AppendRunLog(parentRunId, status.Equals("success", StringComparison.OrdinalIgnoreCase) ? "INFO" : "WARN",
            $"batch item {itemValue} finished: status={status}, child={childRunId}");
        TryFinalizeBatchParent(parentRunId, batchTotal);
        return parentRunId;
    }

    static void TryFinalizeBatchParent(string parentRunId, int batchTotalHint)
    {
        var items = LoadBatchItems(parentRunId);
        if (items.Count == 0)
            return;

        var latestItems = LatestBatchItemsByValue(items);
        int finishedCount = latestItems.Count(i => IsTerminalRunStatus(i.Status));
        int expectedTotal = LoadParentBatchTotal(parentRunId);
        int total = Math.Max(expectedTotal, Math.Max(batchTotalHint, latestItems.Count));
        string parentStatus = finishedCount >= total ? ResolveBatchParentStatus(latestItems) : "running";
        string itemLabel = ResolveBatchItemLabel(parentRunId);
        string[] itemValues = latestItems.OrderBy(i => i.BatchIndex).Select(i => i.Plant).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string summaryJson = BuildBatchSummaryJson(parentRunId, itemValues, itemLabel, latestItems, parentStatus);

        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        if (parentStatus.Equals("running", StringComparison.OrdinalIgnoreCase))
        {
            command.CommandText = "UPDATE runs SET summary_json=$summaryJson WHERE run_id=$parentRunId";
            command.Parameters.AddWithValue("$summaryJson", summaryJson);
            command.Parameters.AddWithValue("$parentRunId", parentRunId);
            command.ExecuteNonQuery();
            return;
        }

        long durationMs = latestItems.Sum(i => Math.Max(0, i.DurationMs));
        string finishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var parentFiles = new List<RunFile>();
        string exportMessage = "";
        string parentTransactionCode = "";
        try
        {
            var parentForExport = LoadRun(parentRunId, includeDetails: false);
            parentTransactionCode = parentForExport?.TransactionCode ?? "";
            if (SupportsAlvExport(parentTransactionCode))
            {
                CloseExportedExcelWindowsForTransaction(parentTransactionCode, parentRunId);
                parentFiles.AddRange(CollectBatchAlvFiles(parentRunId, latestItems, parentTransactionCode));
                if (parentFiles.Count > 0)
                {
                    exportMessage = $"\uFF1BALV\u6587\u4EF6\uFF1A{parentFiles.Count}\u4E2A";
                }
            }
        }
        catch (Exception ex)
        {
            AppendRunLog(parentRunId, "ERROR", $"ALV output collection failed: {ex.Message}");
            exportMessage = $"\uFF1BALV\u6587\u4EF6\u6536\u96C6\u5931\u8D25\uFF1A{ex.Message}";
            if (parentStatus.Equals("success", StringComparison.OrdinalIgnoreCase))
                parentStatus = "partial_failed";
        }

        string message = BuildBatchSummaryMessage(parentRunId, latestItems) + exportMessage;
        command.CommandText = """
UPDATE runs
SET status=$status,
    message=$message,
    summary_json=$summaryJson,
    finished_at=$finishedAt,
    duration_ms=$durationMs,
    locked_by='',
    locked_at=''
WHERE run_id=$parentRunId
  AND status IN ('queued', 'running');
""";
        command.Parameters.AddWithValue("$parentRunId", parentRunId);
        command.Parameters.AddWithValue("$status", parentStatus);
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$summaryJson", summaryJson);
        command.Parameters.AddWithValue("$finishedAt", finishedAt);
        command.Parameters.AddWithValue("$durationMs", durationMs);
        int updatedRows = command.ExecuteNonQuery();

        if (updatedRows > 0)
        {
            if (parentFiles.Count > 0)
                ReplaceRunFiles(parentRunId, parentFiles);
            CleanupSapGuiSessionAfterRunId(parentRunId);
            CloseExportedExcelWindowsForTransaction(parentTransactionCode, parentRunId);
            UpdateScheduleRunStatusForRun(parentRunId, parentStatus, message);
            NotifyRunEvent(parentRunId, parentStatus.Equals("success", StringComparison.OrdinalIgnoreCase) ? "success" : "failure", message);
        }
    }

    static List<RunFile> CollectBatchAlvFiles(string parentRunId, List<BatchItemStatus> latestItems, string transactionCode)
    {
        var files = new List<RunFile>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool normalizeDirectPlantOutput = UsesDirectPlantAlvOutput(transactionCode);
        bool normalizeBusinessAreaOutput = UsesBusinessAreaAlvOutput(transactionCode);

        foreach (var item in latestItems.OrderBy(i => i.BatchIndex))
        {
            var childFiles = LoadRunFiles(item.ChildRunId)
                .Where(file =>
                {
                    string expanded = Environment.ExpandEnvironmentVariables(file.Path ?? "");
                    return !string.IsNullOrWhiteSpace(expanded) &&
                           File.Exists(expanded) &&
                           IsExcelWorkbookPath(expanded);
                })
                .ToList();

            if (normalizeDirectPlantOutput)
            {
                childFiles = NormalizeDirectPlantAlvFiles(
                    parentRunId,
                    item.Plant,
                    item.ChildRunId,
                    childFiles,
                    logs: null,
                    updateStoredRunFiles: true);
            }
            else if (normalizeBusinessAreaOutput)
            {
                childFiles = NormalizeBusinessAreaAlvFiles(
                    parentRunId,
                    item.Plant,
                    item.ChildRunId,
                    transactionCode,
                    childFiles,
                    logs: null,
                    updateStoredRunFiles: true);
            }

            foreach (var file in childFiles)
            {
                string expanded = Environment.ExpandEnvironmentVariables(file.Path ?? "");
                if (string.IsNullOrWhiteSpace(expanded) || !File.Exists(expanded) || !IsExcelWorkbookPath(expanded))
                    continue;

                if (!seenPaths.Add(Path.GetFullPath(expanded)))
                    continue;

                files.Add(new RunFile
                {
                    Type = FirstNonEmpty(file.Type, "output"),
                    Name = FirstNonEmpty(file.Name, Path.GetFileName(expanded)),
                    Path = file.Path ?? expanded,
                    Size = file.Size > 0 ? file.Size : new FileInfo(expanded).Length,
                    CreatedAt = file.CreatedAt
                });
            }
        }

        AppendRunLog(parentRunId, "INFO", $"ALV output files collected after plant normalization: {files.Count}");
        return files;
    }

    static List<RunFile> NormalizeDirectPlantAlvFiles(
        string logRunId,
        string plant,
        string childRunId,
        List<RunFile> childFiles,
        List<RunLogLine>? logs,
        bool updateStoredRunFiles)
    {
        if (childFiles.Count == 0)
            return childFiles;

        var normalized = new List<RunFile>();
        bool changed = false;
        var partGroups = new Dictionary<string, List<RunFile>>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in childFiles)
        {
            string expanded = Environment.ExpandEnvironmentVariables(file.Path ?? "");
            if (TryGetAlvWindowPartBasePath(expanded, out string basePath))
            {
                if (!partGroups.TryGetValue(basePath, out var group))
                {
                    group = new List<RunFile>();
                    partGroups[basePath] = group;
                }

                group.Add(file);
                continue;
            }

            normalized.Add(file);
        }

        foreach (var group in partGroups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var parts = group.Value
                .OrderBy(f => ExtractAlvWindowPartNumber(Environment.ExpandEnvironmentVariables(f.Path ?? "")))
                .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (parts.Count == 0)
                continue;

            string finalPath = group.Key;
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath) ?? AlvExportDataDirectory);
            if (parts.Count == 1)
            {
                string partPath = Environment.ExpandEnvironmentVariables(parts[0].Path ?? "");
                if (!Path.GetFullPath(partPath).Equals(Path.GetFullPath(finalPath), StringComparison.OrdinalIgnoreCase))
                {
                    MoveAlvPartToFinalFile(partPath, finalPath);
                    AddAlvNormalizationLog(logRunId, logs, "INFO", $"ALV single part normalized: plant={plant}, child={childRunId}, final={finalPath}");
                    changed = true;
                }
            }
            else
            {
                MergeAlvPartFilesToFinalWorkbook(finalPath, parts);
                DeleteAlvPartFiles(parts, finalPath, logRunId, logs);
                AddAlvNormalizationLog(logRunId, logs, "INFO", $"ALV plant parts merged: plant={plant}, child={childRunId}, parts={parts.Count}, final={finalPath}");
                changed = true;
            }

            var finalFile = BuildRunFile(finalPath);
            finalFile.Type = FirstNonEmpty(parts[0].Type, "output");
            normalized.Add(finalFile);
        }

        normalized = normalized
            .Where(f =>
            {
                string expanded = Environment.ExpandEnvironmentVariables(f.Path ?? "");
                return !string.IsNullOrWhiteSpace(expanded) && File.Exists(expanded) && IsExcelWorkbookPath(expanded);
            })
            .GroupBy(f => Path.GetFullPath(Environment.ExpandEnvironmentVariables(f.Path ?? "")), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (changed && updateStoredRunFiles)
            ReplaceRunFiles(childRunId, normalized);

        return normalized;
    }

    static List<RunFile> NormalizeBusinessAreaAlvFiles(
        string logRunId,
        string businessArea,
        string childRunId,
        string transactionCode,
        List<RunFile> rawFiles,
        List<RunLogLine>? logs,
        bool updateStoredRunFiles)
    {
        if (rawFiles.Count == 0)
            return rawFiles;

        var normalized = new List<RunFile>();
        bool changed = false;

        foreach (var file in rawFiles)
        {
            string rawPath = Environment.ExpandEnvironmentVariables(file.Path ?? "");
            if (string.IsNullOrWhiteSpace(rawPath) || !File.Exists(rawPath) || !IsExcelWorkbookPath(rawPath))
                continue;

            var splitFiles = SplitAlvWorkbookByFactory(
                rawPath,
                transactionCode,
                businessArea,
                DateTime.Now,
                outputRoot: null,
                logRunId,
                logs);

            if (splitFiles.Count == 0)
            {
                AddAlvNormalizationLog(logRunId, logs, "WARN", $"ALV business-area split produced no factory files; raw output removed as no factory data: businessArea={businessArea}, child={childRunId}, raw={rawPath}");
                CleanupBusinessAreaRawAlvFile(rawPath, logRunId, logs);
                changed = true;
                continue;
            }

            normalized.AddRange(splitFiles);
            changed = true;
            AddAlvNormalizationLog(logRunId, logs, "INFO", $"ALV business-area output split by factory: businessArea={businessArea}, child={childRunId}, factories={splitFiles.Count}, raw={rawPath}");

            CleanupBusinessAreaRawAlvFile(rawPath, logRunId, logs);
        }

        normalized = normalized
            .Where(f =>
            {
                string expanded = Environment.ExpandEnvironmentVariables(f.Path ?? "");
                return !string.IsNullOrWhiteSpace(expanded) && File.Exists(expanded) && IsExcelWorkbookPath(expanded);
            })
            .GroupBy(f => Path.GetFullPath(Environment.ExpandEnvironmentVariables(f.Path ?? "")), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (changed && updateStoredRunFiles)
            ReplaceRunFiles(childRunId, normalized);

        return normalized;
    }

    static void NormalizeBusinessAreaAlvFilesForRunResult(
        RunResultRequest result,
        string logRunId,
        string businessArea,
        string childRunId,
        string transactionCode)
    {
        if (result.Files.Count == 0)
            return;

        try
        {
            result.Files = NormalizeBusinessAreaAlvFiles(
                logRunId,
                businessArea,
                childRunId,
                transactionCode,
                result.Files,
                result.Logs,
                updateStoredRunFiles: false);
        }
        catch (Exception ex)
        {
            result.Status = "failed";
            result.Message = ex.Message;
            result.Logs.Add(new RunLogLine
            {
                Level = "ERROR",
                Message = $"ALV business-area split failed; raw output kept for diagnostics: {ex.Message}"
            });
        }
    }

    static void CleanupBusinessAreaRawAlvFile(string rawPath, string logRunId, List<RunLogLine>? logs)
    {
        try
        {
            DeleteFileWithRetry(rawPath);
            DeleteEmptyParentDirectoriesUnder(GetAlvBusinessAreaRawRoot(), rawPath);
            DeleteEmptyDirectoryIfExists(GetAlvBusinessAreaRawRoot());
        }
        catch (Exception ex)
        {
            AddAlvNormalizationLog(logRunId, logs, "WARN", $"ALV business-area raw cleanup failed: {rawPath}; {ex.Message}");
        }
    }

    static void AddAlvNormalizationLog(string runId, List<RunLogLine>? logs, string level, string message)
    {
        if (logs != null)
        {
            logs.Add(new RunLogLine { Level = level, Message = message });
            return;
        }

        AppendRunLog(runId, level, message);
    }

    static void CloseExportedExcelWindowsForTransaction(string transactionCode, string runId)
    {
        if (!SupportsAlvExport(transactionCode))
            return;

        string filePrefix = transactionCode.Trim().ToUpperInvariant() + "_";
        var exportedFiles = LoadBatchItems(runId)
            .OrderBy(i => i.BatchIndex)
            .SelectMany(i => LoadRunFiles(i.ChildRunId))
            .Select(f => Environment.ExpandEnvironmentVariables(f.Path ?? ""))
            .Where(path => IsExcelWorkbookPath(path) && File.Exists(path))
            .Where(path => Path.GetFileName(path).StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (string outputFile in exportedFiles)
        {
            ScheduleDelayedExcelWorkbookClose(outputFile, runId);
        }

        CloseVisibleExportedExcelWindowsByTitle(transactionCode, runId);
    }

    static void CloseVisibleExportedExcelWindowsByTitle(string transactionCode, string runId)
    {
        string titlePrefix = transactionCode.Trim().ToUpperInvariant() + "_";
        foreach (var process in Process.GetProcessesByName("EXCEL"))
        {
            try
            {
                string title = process.MainWindowTitle ?? "";
                if (!title.StartsWith(titlePrefix, StringComparison.OrdinalIgnoreCase) ||
                    !title.Contains(".xls", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool requested = process.CloseMainWindow();
                if (requested)
                    process.WaitForExit(1500);

                process.Refresh();
                string currentTitle = process.HasExited ? "" : process.MainWindowTitle ?? "";
                if (process.HasExited || !currentTitle.Equals(title, StringComparison.OrdinalIgnoreCase))
                    AppendRunLog(runId, "INFO", $"requested close for exported Excel window: {title}");
                else
                    AppendRunLog(runId, "WARN", $"exported Excel window still open after non-destructive close request: {title}");
            }
            catch (Exception ex)
            {
                AppendRunLog(runId, "WARN", $"failed to request exported Excel window close: {ex.Message}");
            }
        }
    }

    static int LoadParentBatchTotal(string parentRunId)
    {
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
SELECT batch_total
FROM runs
WHERE run_id=$runId AND COALESCE(run_type, 'single')='parent';
""";
        command.Parameters.AddWithValue("$runId", parentRunId);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    static bool IsTerminalRunStatus(string status)
    {
        return status.Equals("success", StringComparison.OrdinalIgnoreCase) ||
               status.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
               status.Equals("partial_failed", StringComparison.OrdinalIgnoreCase) ||
               status.Equals("canceled", StringComparison.OrdinalIgnoreCase);
    }

    static List<BatchItemStatus> LatestBatchItemsByPlant(List<BatchItemStatus> items)
    {
        return LatestBatchItemsByValue(items);
    }

    static List<BatchItemStatus> LatestBatchItemsByValue(List<BatchItemStatus> items)
    {
        return items
            .GroupBy(i => i.Plant, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(i => i.AttemptNo).ThenByDescending(i => i.FinishedAt).ThenByDescending(i => i.ChildRunId).First())
            .OrderBy(i => i.BatchIndex)
            .ToList();
    }

    static string ResolveBatchParentStatus(List<BatchItemStatus> items)
    {
        int success = items.Count(i => i.Status.Equals("success", StringComparison.OrdinalIgnoreCase));
        int failed = items.Count(i => !i.Status.Equals("success", StringComparison.OrdinalIgnoreCase));
        if (success == items.Count)
            return "success";
        if (success > 0 && failed > 0)
            return "partial_failed";
        return "failed";
    }

    static string BuildBatchSummaryMessage(string parentRunId, List<BatchItemStatus> items)
    {
        int success = items.Count(i => i.Status.Equals("success", StringComparison.OrdinalIgnoreCase));
        int failed = items.Count(i => !i.Status.Equals("success", StringComparison.OrdinalIgnoreCase));
        var parent = LoadRun(parentRunId, includeDetails: false);
        string tcode = parent?.TransactionCode ?? "";
        string itemLabel = ResolveBatchItemLabel(parentRunId, parent);
        string failedValues = string.Join(",", items.Where(i => !i.Status.Equals("success", StringComparison.OrdinalIgnoreCase)).Select(i => i.Plant));
        return failed == 0
            ? $"{tcode} 批次执行完成：成功 {success}/{items.Count} 个{itemLabel}"
            : $"{tcode} 批次执行完成：成功 {success}/{items.Count} 个{itemLabel}，失败 {failed} 个，失败{itemLabel}：{failedValues.Replace(",", "、")}";
    }

    static string ResolveBatchItemLabel(string parentRunId, RunRecordView? parent = null)
    {
        parent ??= LoadRun(parentRunId, includeDetails: false);
        string tcode = parent?.TransactionCode ?? "";
        if (UsesBusinessAreaBatchItems(tcode))
            return "业务范围";
        return "工厂";
    }

    static BatchRunPlan ResolveBatchPlanForParent(RunRecordView parent, string[] itemValues)
    {
        string tcode = parent.TransactionCode;
        if (UsesBusinessAreaBatchItems(tcode))
            return new BatchRunPlan(tcode, "businessAreas", "businessArea", "businessAreaList", "业务范围", itemValues);
        return new BatchRunPlan(tcode, "plants", "plant", "plantlist", "工厂", itemValues);
    }

    static string BuildBatchSummaryJson(string parentRunId, string[] plants, IEnumerable<BatchItemStatus> items, string status)
    {
        return BuildBatchSummaryJson(parentRunId, plants, ResolveBatchItemLabel(parentRunId), items, status);
    }

    static string BuildBatchSummaryJson(string parentRunId, string[] itemsForRun, string itemLabel, IEnumerable<BatchItemStatus> items, string status)
    {
        var itemList = items.ToList();
        var payload = new
        {
            parentRunId,
            status,
            itemLabel,
            total = itemsForRun.Length > 0 ? itemsForRun.Length : itemList.Count,
            success = itemList.Count(i => i.Status.Equals("success", StringComparison.OrdinalIgnoreCase)),
            failed = itemList.Count(i => IsTerminalRunStatus(i.Status) && !i.Status.Equals("success", StringComparison.OrdinalIgnoreCase)),
            pending = itemList.Count == 0 ? itemsForRun.Length : itemList.Count(i => !IsTerminalRunStatus(i.Status)),
            plants = itemsForRun,
            batchItems = itemsForRun,
            items = itemList.Select(i => new
            {
                plant = i.Plant,
                value = i.Plant,
                label = itemLabel,
                childRunId = i.ChildRunId,
                batchIndex = i.BatchIndex,
                attemptNo = i.AttemptNo,
                status = i.Status,
                message = i.Message,
                startedAt = i.StartedAt,
                finishedAt = i.FinishedAt,
                durationMs = i.DurationMs
            }).ToArray()
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    static object RerunFailedBatchItems(string parentRunId)
    {
        InitializeDatabase(seedFromScripts: true);
        var parent = LoadRun(parentRunId, includeDetails: false);
        if (parent == null || !parent.RunType.Equals("parent", StringComparison.OrdinalIgnoreCase))
            return new { ok = false, error = $"parent run not found: {parentRunId}" };
        if (!IsTerminalRunStatus(parent.Status))
            return new { ok = false, parentRunId, error = $"parent run is not finished: {parent.Status}" };

        var items = LoadBatchItems(parentRunId);
        var latestByValue = LatestBatchItemsByValue(items);
        var failedItems = latestByValue
            .Where(i => IsTerminalRunStatus(i.Status) && !i.Status.Equals("success", StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.BatchIndex)
            .ToList();

        if (failedItems.Count == 0)
            return new { ok = true, parentRunId, created = 0, childRunIds = Array.Empty<string>(), message = $"没有失败{ResolveBatchItemLabel(parentRunId, parent)}需要重跑" };

        var request = JsonSerializer.Deserialize<CreateRunRequest>(parent.RequestJson, new JsonSerializerOptions(JsonOptions)
        {
            PropertyNameCaseInsensitive = true
        }) ?? new CreateRunRequest { TransactionCode = parent.TransactionCode };
        EnsureCreateRunRequestDefaults(request);
        request.TransactionCode = parent.TransactionCode;
        request.TCode = parent.TransactionCode;
        request.Code = parent.TransactionCode;
        string dingTalkUserId = FirstNonEmpty(parent.DingTalkUserId, ResolveDingTalkUserId(request.Operator));
        request.Operator.DingTalkUserId = dingTalkUserId;
        if (string.IsNullOrWhiteSpace(request.Operator.Ddid))
            request.Operator.Ddid = dingTalkUserId;

        var script = LoadScriptInfo(parent.TransactionCode);
        string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string rerunParentRunId = NewRunId(parent.TransactionCode);
        string[] rerunValues = failedItems.Select(i => i.Plant).ToArray();
        var plan = ResolveBatchPlanForParent(parent, rerunValues);
        string summaryJson = BuildBatchSummaryJson(rerunParentRunId, rerunValues, plan.ItemLabel, Array.Empty<BatchItemStatus>(), "queued");
        var newChildRunIds = new List<string>();

        using var connection = OpenDatabaseConnection();
        using var tx = connection.BeginTransaction();
        request.Params[plan.ParamKey] = string.Join(",", rerunValues);
        request.Params[plan.SingleParamKey] = rerunValues.FirstOrDefault() ?? "";
        InsertRunRow(connection, tx, rerunParentRunId, request, script, dingTalkUserId, now, "parent", "", "", 0, rerunValues.Length, 1, "running", summaryJson, parentRunId, parentRunId);
        InsertRunParams(connection, tx, rerunParentRunId, request.Params);

        foreach (var failed in failedItems)
        {
            int nextAttempt = Math.Max(1, failed.AttemptNo + 1);
            var childRequest = CloneRunRequestForBatchItem(request, plan, failed.Plant);
            string childRunId = NewRunId(parent.TransactionCode);
            newChildRunIds.Add(childRunId);
            InsertRunRow(connection, tx, childRunId, childRequest, script, dingTalkUserId, now, "child", rerunParentRunId, failed.Plant, failed.BatchIndex, rerunValues.Length, nextAttempt, "queued", "", parentRunId, failed.ChildRunId);
            InsertRunParams(connection, tx, childRunId, childRequest.Params);
            InsertRunBatchItem(connection, tx, rerunParentRunId, childRunId, failed.Plant, failed.BatchIndex, nextAttempt, "queued", "", "", "", 0);
        }

        tx.Commit();

        AppendRunLog(parentRunId, "INFO", $"rerun parent created: {rerunParentRunId}, {plan.ParamKey}={string.Join(",", rerunValues)}");
        AppendRunLog(rerunParentRunId, "INFO", $"rerun failed {plan.ItemLabel} queued from {parentRunId}: {string.Join(",", rerunValues)}");
        foreach (string childRunId in newChildRunIds)
            AppendRunLog(childRunId, "INFO", $"rerun child queued under parent {rerunParentRunId}, sourceParent={parentRunId}");

        NotifyRunEvent(rerunParentRunId, "start", $"{parent.TransactionCode} 失败{plan.ItemLabel}重跑开始：共 {rerunValues.Length} 个{plan.ItemLabel}");

        return new
        {
            ok = true,
            sourceParentRunId = parentRunId,
            parentRunId = rerunParentRunId,
            rerunParentRunId,
            created = newChildRunIds.Count,
            childRunIds = newChildRunIds,
            batchItems = rerunValues,
            plants = plan.ParamKey.Equals("plants", StringComparison.OrdinalIgnoreCase) ? rerunValues : Array.Empty<string>(),
            businessAreas = plan.ParamKey.Equals("businessAreas", StringComparison.OrdinalIgnoreCase) ? rerunValues : Array.Empty<string>()
        };
    }

    static object LoadRuns(HttpListenerRequest request)
    {
        InitializeDatabase(seedFromScripts: true);
        int limit = DefaultRunListLimit;
        if (int.TryParse(request.QueryString["limit"], out int parsedLimit))
            limit = Math.Clamp(parsedLimit, 1, 200);

        string status = request.QueryString["status"] ?? "";
        string fromRaw = request.QueryString["from"] ?? "";
        string toRaw = request.QueryString["to"] ?? "";
        bool hasDateFilter = !string.IsNullOrWhiteSpace(fromRaw) || !string.IsNullOrWhiteSpace(toRaw);
        DateTime to = ParseReportDate(toRaw, DateTime.Now.Date.AddDays(1).AddTicks(-1), isEndDate: true);
        DateTime from = ParseReportDate(fromRaw, to.Date, isEndDate: false);
        if (from > to)
            (from, to) = (to, from);
        string fromText = from.ToString("yyyy-MM-dd HH:mm:ss");
        string toText = to.ToString("yyyy-MM-dd HH:mm:ss");

        var runs = new List<RunRecordView>();
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(status))
            where.Add("r.status=$status");
        if (hasDateFilter)
            where.Add("COALESCE(NULLIF(r.finished_at, ''), r.queued_at) >= $from AND COALESCE(NULLIF(r.finished_at, ''), r.queued_at) <= $to");
        string whereSql = where.Count == 0 ? "" : "WHERE " + string.Join("\n  AND ", where);
        string orderSql = string.IsNullOrWhiteSpace(status)
            ? "ORDER BY COALESCE(NULLIF(r.finished_at, ''), r.queued_at) DESC"
            : "ORDER BY r.priority DESC, r.queued_at, r.run_id";
        command.CommandText = $"""
SELECT r.run_id, r.transaction_code, r.operator_id, r.operator_name, r.operator_dept, r.ding_talk_user_id, r.status, r.request_json,
       r.sap_status_type, r.sap_status_text, r.message, r.script_file, r.script_hash,
       r.queued_at, r.started_at, r.finished_at, r.duration_ms,
       r.source, r.notify_target, r.priority, r.attempt, r.max_attempts, r.locked_by, r.locked_at,
       r.run_type, r.parent_run_id, r.batch_item_key, r.batch_index, r.batch_total, r.attempt_no, r.summary_json,
       r.source_parent_run_id, r.rerun_of_run_id,
       COALESCE(NULLIF(t.name, ''), '') AS transaction_name
FROM runs r
LEFT JOIN transactions t ON t.tcode = r.transaction_code
{whereSql}
{orderSql}
LIMIT $limit;
""";

        if (!string.IsNullOrWhiteSpace(status))
            command.Parameters.AddWithValue("$status", status.ToLowerInvariant());
        if (hasDateFilter)
        {
            command.Parameters.AddWithValue("$from", fromText);
            command.Parameters.AddWithValue("$to", toText);
        }

        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            runs.Add(ReadRunRecord(reader));

        foreach (var run in runs)
            ApplyQueuePosition(run);

        return new
        {
            version = 2,
            source = "sqlite",
            database = DatabaseFilePath,
            period = hasDateFilter ? new { from = fromText, to = toText } : null,
            runs
        };
    }

    static object LoadQueueStatus(HttpListenerRequest request)
    {
        InitializeDatabase(seedFromScripts: true);
        MarkStaleRunningRuns();

        int limit = DefaultQueueStatusLimit;
        if (int.TryParse(request.QueryString["limit"], out int parsedLimit))
            limit = Math.Clamp(parsedLimit, 1, MaxQueueStatusLimit);

        using var connection = OpenDatabaseConnection();
        var running = LoadRunningRunSummary(connection);
        long queuedCount = CountQueuedRuns(connection);
        var nextRuns = LoadNextRunSummaries(connection, limit);
        bool queueDisabled = IsQueueDisabled();
        string mode = queueDisabled ? "disabled" : "serial";

        return new
        {
            mode,
            queueMode = mode,
            serial = !queueDisabled,
            running,
            runningRunId = running?.RunId ?? "",
            queuedCount,
            nextRuns,
            executor = ExecutorId,
            staleRunningTimeoutHours = StaleRunningTimeoutHours,
            message = queueDisabled
                ? "Queue worker is disabled; runs can be queued but will not execute until the worker is enabled."
                : "SAP GUI execution is serial: one running task owns the desktop session, while later submissions wait in priority and queued time order."
        };
    }

    static QueuePositionInfo LoadQueuePosition(string runId)
    {
        InitializeDatabase(seedFromScripts: true);
        using var connection = OpenDatabaseConnection();
        return LoadQueuePosition(connection, runId);
    }

    static QueuePositionInfo LoadQueuePosition(SqliteConnection connection, string runId)
    {
        var info = new QueuePositionInfo
        {
            RunningRunId = GetRunningRunId(connection),
            QueuedCount = CountQueuedRuns(connection)
        };

        string status;
        int priority;
        string queuedAt;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
SELECT status, priority, queued_at
FROM runs
WHERE run_id=$runId;
""";
            command.Parameters.AddWithValue("$runId", runId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return info;

            status = reader.GetString(0);
            priority = reader.GetInt32(1);
            queuedAt = reader.GetString(2);
        }

        if (status.Equals("queued", StringComparison.OrdinalIgnoreCase))
        {
            info.RunsAhead = CountQueuedRunsAhead(connection, priority, queuedAt, runId);
            info.WorkItemsAhead = info.RunsAhead + (string.IsNullOrWhiteSpace(info.RunningRunId) ? 0 : 1);
            info.QueuePosition = info.RunsAhead + 1;
        }
        else if (status.Equals("running", StringComparison.OrdinalIgnoreCase))
        {
            info.RunsAhead = 0;
            info.WorkItemsAhead = 0;
            info.QueuePosition = 0;
        }

        return info;
    }

    static void ApplyQueuePosition(RunRecordView run)
    {
        var position = LoadQueuePosition(run.RunId);
        run.QueuePosition = position.QueuePosition;
        run.RunsAhead = position.RunsAhead;
        run.WorkItemsAhead = position.WorkItemsAhead;
        run.RunningRunId = position.RunningRunId;
        run.QueuedCount = position.QueuedCount;
    }

    static long CountQueuedRuns(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM runs WHERE status='queued' AND COALESCE(run_type, 'single') <> 'parent';";
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    static int CountQueuedRunsAhead(SqliteConnection connection, int priority, string queuedAt, string runId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
SELECT COUNT(*)
FROM runs
WHERE status='queued'
  AND COALESCE(run_type, 'single') <> 'parent'
  AND (
      priority > $priority
      OR (priority = $priority AND queued_at < $queuedAt)
      OR (priority = $priority AND queued_at = $queuedAt AND run_id < $runId)
  );
""";
        command.Parameters.AddWithValue("$priority", priority);
        command.Parameters.AddWithValue("$queuedAt", queuedAt);
        command.Parameters.AddWithValue("$runId", runId);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    static string GetRunningRunId(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
SELECT run_id
FROM runs
WHERE status='running'
  AND COALESCE(run_type, 'single') <> 'parent'
ORDER BY started_at DESC, locked_at DESC
LIMIT 1;
""";
        return command.ExecuteScalar() as string ?? "";
    }

    static QueueRunSummary? LoadRunningRunSummary(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
SELECT run_id, transaction_code, operator_id, operator_name, operator_dept,
       status, queued_at, started_at, priority, attempt, max_attempts, locked_by, locked_at
FROM runs
WHERE status='running'
  AND COALESCE(run_type, 'single') <> 'parent'
ORDER BY started_at DESC, locked_at DESC
LIMIT 1;
""";
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadQueueRunSummary(reader, queuePosition: 0, runsAhead: 0) : null;
    }

    static List<QueueRunSummary> LoadNextRunSummaries(SqliteConnection connection, int limit)
    {
        var runs = new List<QueueRunSummary>();
        using var command = connection.CreateCommand();
        command.CommandText = """
SELECT run_id, transaction_code, operator_id, operator_name, operator_dept,
       status, queued_at, started_at, priority, attempt, max_attempts, locked_by, locked_at
FROM runs
WHERE status='queued'
  AND COALESCE(run_type, 'single') <> 'parent'
ORDER BY priority DESC, queued_at, run_id
LIMIT $limit;
""";
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            int runsAhead = runs.Count;
            runs.Add(ReadQueueRunSummary(reader, queuePosition: runsAhead + 1, runsAhead));
        }

        return runs;
    }

    static QueueRunSummary ReadQueueRunSummary(SqliteDataReader reader, int queuePosition, int runsAhead)
    {
        return new QueueRunSummary
        {
            RunId = reader.GetString(0),
            TransactionCode = reader.GetString(1),
            OperatorId = reader.GetString(2),
            OperatorName = reader.GetString(3),
            OperatorDept = reader.GetString(4),
            Status = reader.GetString(5),
            QueuedAt = reader.GetString(6),
            StartedAt = reader.GetString(7),
            Priority = reader.GetInt32(8),
            Attempt = reader.GetInt32(9),
            MaxAttempts = reader.GetInt32(10),
            LockedBy = reader.GetString(11),
            LockedAt = reader.GetString(12),
            QueuePosition = queuePosition,
            RunsAhead = runsAhead
        };
    }

    static void MarkStaleRunningRuns()
    {
        InitializeDatabase(seedFromScripts: false);
        string threshold = DateTime.Now.AddHours(-StaleRunningTimeoutHours).ToString("yyyy-MM-dd HH:mm:ss");
        var staleRunIds = new List<string>();

        using (var connection = OpenDatabaseConnection())
        {
            using (var select = connection.CreateCommand())
            {
                select.CommandText = """
SELECT run_id
FROM runs
WHERE status='running'
  AND COALESCE(run_type, 'single') <> 'parent'
  AND COALESCE(NULLIF(locked_at, ''), NULLIF(started_at, ''), queued_at) < $threshold;
""";
                select.Parameters.AddWithValue("$threshold", threshold);
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    string runId = reader.GetString(0);
                    if (!IsRunActiveInCurrentProcess(runId))
                        staleRunIds.Add(runId);
                }
            }

            if (staleRunIds.Count == 0)
                return;

            using var tx = connection.BeginTransaction();
            foreach (string runId in staleRunIds)
            {
                using var update = connection.CreateCommand();
                update.Transaction = tx;
                update.CommandText = """
UPDATE runs
SET status='failed',
    message=$message,
    finished_at=$finishedAt
WHERE run_id=$runId AND status='running';
""";
                update.Parameters.AddWithValue("$runId", runId);
                update.Parameters.AddWithValue("$message", $"运行状态超过 {StaleRunningTimeoutHours} 小时未结束，已由队列守护进程标记失败");
                update.Parameters.AddWithValue("$finishedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                update.ExecuteNonQuery();
            }

            tx.Commit();
        }

        foreach (string runId in staleRunIds)
        {
            AppendRunLog(runId, "ERR", $"stale running timeout after {StaleRunningTimeoutHours} hours");
            UpdateBatchAfterChildCompletion(runId, "failed", new RunResultRequest
            {
                Status = "failed",
                Message = $"stale running timeout after {StaleRunningTimeoutHours} hours"
            }, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            NotifyRunEvent(runId, "finish", "任务运行超时，已释放 SAP 串行队列");
        }
    }

    static RunRecordView? LoadRun(string runId, bool includeDetails)
    {
        InitializeDatabase(seedFromScripts: true);
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
SELECT r.run_id, r.transaction_code, r.operator_id, r.operator_name, r.operator_dept, r.ding_talk_user_id, r.status, r.request_json,
       r.sap_status_type, r.sap_status_text, r.message, r.script_file, r.script_hash,
       r.queued_at, r.started_at, r.finished_at, r.duration_ms,
       r.source, r.notify_target, r.priority, r.attempt, r.max_attempts, r.locked_by, r.locked_at,
       r.run_type, r.parent_run_id, r.batch_item_key, r.batch_index, r.batch_total, r.attempt_no, r.summary_json,
       r.source_parent_run_id, r.rerun_of_run_id,
       COALESCE(NULLIF(t.name, ''), '') AS transaction_name
FROM runs r
LEFT JOIN transactions t ON t.tcode = r.transaction_code
WHERE r.run_id=$runId;
""";
        command.Parameters.AddWithValue("$runId", runId);
        RunRecordView run;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
                return null;

            run = ReadRunRecord(reader);
        }

        ApplyQueuePosition(run);
        if (includeDetails)
        {
            run.Logs = LoadRunLogs(runId);
            run.Files = LoadRunFiles(runId);
            if (run.RunType.Equals("parent", StringComparison.OrdinalIgnoreCase))
            {
                run.BatchItems = LoadBatchItems(runId);
                run.ChildRunIds = run.BatchItems.Select(i => i.ChildRunId).ToList();
            }
        }

        return run;
    }

    static void ProcessRunQueueLoop()
    {
        MarkStaleRunningRuns();
        while (true)
        {
            try
            {
                MarkStaleRunningRuns();
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
  AND COALESCE(run_type, 'single') <> 'parent'
ORDER BY priority DESC,
         queued_at,
         COALESCE(NULLIF(parent_run_id, ''), run_id),
         batch_index,
         run_id
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
SELECT run_id, transaction_code, request_json, script_file, run_type, parent_run_id
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
                    ScriptFile = reader.GetString(3),
                    RunType = reader.GetString(4),
                    ParentRunId = reader.GetString(5)
                };
            }
        }

        tx.Commit();
        if (item != null && item.RunType.Equals("child", StringComparison.OrdinalIgnoreCase))
            MarkBatchItemRunning(item.RunId);

        if (item != null)
            NotifyRunEvent(item.RunId, "start", "任务开始执行，SAP GUI 桌面会话已被当前任务占用");
        return item;
    }

    static void ExecuteQueuedRun(QueuedRunWorkItem item)
    {
        var started = DateTime.UtcNow;
        using var heartbeat = StartRunHeartbeat(item.RunId);
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

            var result = ShouldRunZfi057Workflow(pars)
                ? ExecuteZfi057Workflow(pars)
                : LaunchSapGuiAndExecute(pars);
            CompleteRun(item.RunId, result);
        }
        catch (Exception ex)
        {
            Log($"队列执行失败: runId={item.RunId}, {ex}");
            CompleteRun(item.RunId, FailedRunResult(ex.Message, started));
        }
    }

    static bool ShouldRunZfi057Workflow(SapRunParams p)
    {
        if (!p.TCode.Equals("ZFI057", StringComparison.OrdinalIgnoreCase))
            return false;

        string strategy = FirstNonEmpty(p.RunStrategy, "").Trim();
        return !strategy.Equals("scriptOnly", StringComparison.OrdinalIgnoreCase) &&
               !strategy.Equals("single", StringComparison.OrdinalIgnoreCase);
    }

    static RunResultRequest ExecuteZfi057Workflow(SapRunParams p)
    {
        var started = DateTime.UtcNow;
        var aggregate = new RunResultRequest
        {
            Status = "success",
            Message = "ZFI057 workflow completed"
        };

        var scopes = ResolveZfi057WorkflowScopes(p);
        AddWorkflowLog(aggregate, "workflow", $"ZFI057 auto workflow start; scopeCount={scopes.Count}; period={p.Period}; weekEnd={p.WeekEnd}");
        AddWorkflowLog(aggregate, "workflow", $"raw input: businessAreas={p.BusinessAreas}; plants={p.Plants}; factoryGroup={p.FactoryGroup}; runStrategy={p.RunStrategy}");
        if (scopes.Count == 0)
            return FailZfi057Workflow(aggregate, "ZFI057 workflow requires businessAreas; plants are not used to infer business areas.", started);

        int totalStep2Success = 0;
        int totalStep2NoDataSkipped = 0;
        int scopeIndex = 0;
        var scopeResults = new List<Zfi057WorkflowScopeResult>();
        foreach (var scope in scopes)
        {
            scopeIndex++;
            string area = scope.BusinessArea;
            string[] plants = scope.Plants
                .Where(plant => !string.IsNullOrWhiteSpace(plant))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            AddWorkflowLog(aggregate, "scope", $"#{scopeIndex} businessArea={area}; plants={string.Join(",", plants.Where(x => !string.IsNullOrWhiteSpace(x)))}");

            if (string.IsNullOrWhiteSpace(area))
            {
                string message = $"ZFI057 workflow scope #{scopeIndex} has no business area.";
                aggregate.Logs.Add(new RunLogLine { Level = "ERROR", Message = message });
                scopeResults.Add(new Zfi057WorkflowScopeResult(FirstNonEmpty(area, $"scope#{scopeIndex}"), plants, "failed", message));
                continue;
            }

            AddWorkflowLog(aggregate, "step 1", $"query: method=NCo REPORT_SUBMIT/MEMORY_EXPORT; report=ZFI019NL; businessArea={area}; period={p.Period}; weekEnd={p.WeekEnd}; plants=not_applicable");
            var step1Fetch = ExecuteZfi057Step1Memory(p, area, Array.Empty<string>());
            AddStepResult(aggregate, "step 1 ZFI019NL memory", step1Fetch.Result);
            if (!IsSuccessResult(step1Fetch.Result))
            {
                string message = $"step1 failed: {FirstNonEmpty(step1Fetch.Result.Message, step1Fetch.Result.SapStatusText, "ZFI019NL memory fetch failed")}";
                aggregate.Logs.Add(new RunLogLine { Level = "ERROR", Message = $"ZFI057 workflow scope failed; businessArea={area}; {message}" });
                scopeResults.Add(new Zfi057WorkflowScopeResult(area, plants, "failed", message));
                continue;
            }

            string[] requestMaterialItems = NormalizeStringArray(p.Materials);
            string[] upstreamMaterialItems = step1Fetch.Materials;
            string materialSource = "ZFI019NL_MEMORY";
            string[] materialItems = upstreamMaterialItems;
            AddWorkflowLog(aggregate, "step 1", $"materials: selectedSource={materialSource}; selectedCount={materialItems.Length}; selectedSample={FormatSample(materialItems, 8)}; selectedHash={HashForLog(string.Join(",", materialItems))}; requestCount={requestMaterialItems.Length}; requestMaterialsIgnored=true; upstreamCount={upstreamMaterialItems.Length}; upstreamSample={FormatSample(upstreamMaterialItems, 8)}; upstreamHash={HashForLog(string.Join(",", upstreamMaterialItems))}");
            AddZfi057MaterialAuditFile(aggregate, p, scopeIndex, area, plants, materialSource, requestMaterialItems, upstreamMaterialItems, materialItems, step1Fetch.FetchResult.SplitRows);
            if (materialItems.Length == 0)
            {
                string message = "step1 no material list returned";
                aggregate.Logs.Add(new RunLogLine
                {
                    Level = "WARN",
                    Message = $"[scope] businessArea={area} no data after ZFI019NL memory fetch; skip step2/step3"
                });
                scopeResults.Add(new Zfi057WorkflowScopeResult(area, plants, "no_data", message));
                continue;
            }

            if (plants.Length == 0)
            {
                string message = $"GET_GS03 returned no step2 plants for businessArea={area}";
                aggregate.Logs.Add(new RunLogLine { Level = "ERROR", Message = $"ZFI057 workflow scope failed; businessArea={area}; {message}" });
                scopeResults.Add(new Zfi057WorkflowScopeResult(area, plants, "failed", message));
                continue;
            }

            int scopeStep2Success = 0;
            int scopeStep2NoDataSkipped = 0;
            bool scopeFailed = false;
            string scopeFailureMessage = "";
            string step2Plants = string.Join(",", plants);
            var step2 = CloneSapRunParams(p);
            step2.TCode = "ZFI057";
            step2.Script = "ZFI057.vbs";
            step2.BusinessAreas = area;
            step2.BusinessArea = area;
            step2.Plants = step2Plants;
            step2.Plant = "";
            step2.Materials = string.Join(",", materialItems);
            step2.RunStrategy = "workflow-step";
            step2.TimeoutSeconds = Math.Max(p.TimeoutSeconds.GetValueOrDefault(0), 1800);

            AddWorkflowLog(aggregate, "step 2", $"query: {BuildZfi057Step2InputSummary(step2, area, step2Plants, 1, 1, materialItems.Length)}");
            var step2Result = LaunchSapGuiAndExecute(step2);
            if (!IsSuccessResult(step2Result) && IsZfi057Step2NoDataResult(step2Result))
            {
                scopeStep2NoDataSkipped++;
                totalStep2NoDataSkipped++;
                aggregate.Logs.Add(new RunLogLine { Level = "WARN", Message = $"[step 2] skip no-data scope; businessArea={area}; plants={step2Plants}; message={Truncate(FirstNonEmpty(step2Result.Message, step2Result.SapStatusText), 240)}" });
                AddSkippedStepResult(aggregate, "step 2 ZFI057 skipped(no-data)", step2Result);
            }
            else
            {
                AddStepResult(aggregate, "step 2 ZFI057", step2Result);

                if (!IsSuccessResult(step2Result))
                {
                    scopeFailed = true;
                    scopeFailureMessage = $"step2 failed plants={step2Plants}: {FirstNonEmpty(step2Result.Message, step2Result.SapStatusText, "ZFI057 failed")}";
                    aggregate.Logs.Add(new RunLogLine { Level = "ERROR", Message = $"ZFI057 workflow scope failed; businessArea={area}; {scopeFailureMessage}" });
                }
                else
                {
                    scopeStep2Success++;
                    totalStep2Success++;
                }
            }

            if (scopeFailed)
            {
                scopeResults.Add(new Zfi057WorkflowScopeResult(area, plants, "failed", scopeFailureMessage));
                continue;
            }

            if (scopeStep2Success == 0 && scopeStep2NoDataSkipped > 0)
            {
                aggregate.Logs.Add(new RunLogLine { Level = "WARN", Message = $"[step 3] skip ZCO020 because ZFI057 returned no data for all selected plants; businessArea={area}; plants={step2Plants}" });
                scopeResults.Add(new Zfi057WorkflowScopeResult(area, plants, "no_data", "all selected plants returned no data"));
                continue;
            }

            if (scopeStep2Success == 0)
            {
                string message = "no ZFI057 step2 execution succeeded";
                aggregate.Logs.Add(new RunLogLine { Level = "WARN", Message = $"[step 3] skip ZCO020 because {message}; businessArea={area}" });
                scopeResults.Add(new Zfi057WorkflowScopeResult(area, plants, "no_data", message));
                continue;
            }

            var step3Closure = ExecuteZfi057Step3ScopeClosure(aggregate, p, area, plants);
            if (!step3Closure.Success)
            {
                string message = FirstNonEmpty(step3Closure.Message, "step3 closure failed");
                aggregate.Logs.Add(new RunLogLine { Level = "ERROR", Message = $"ZFI057 workflow scope failed; businessArea={area}; {message}" });
                scopeResults.Add(new Zfi057WorkflowScopeResult(area, plants, "failed", message));
                continue;
            }

            scopeResults.Add(new Zfi057WorkflowScopeResult(area, plants, "success", $"step2Success={scopeStep2Success}; step2NoData={scopeStep2NoDataSkipped}; {step3Closure.Message}"));
        }

        if (scopeResults.Count == 0)
            return FailZfi057Workflow(aggregate, "ZFI057 workflow did not produce any business area result.", started);

        int failedScopes = scopeResults.Count(r => r.Status.Equals("failed", StringComparison.OrdinalIgnoreCase));
        int completedScopes = scopeResults.Count - failedScopes;
        aggregate.Status = failedScopes == 0
            ? "success"
            : completedScopes > 0 ? "partial_failed" : "failed";
        aggregate.Message = BuildZfi057WorkflowScopeResultMessage(aggregate.Status, scopeResults);
        aggregate.SapStatusType = aggregate.Status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            ? "E"
            : aggregate.Status.Equals("partial_failed", StringComparison.OrdinalIgnoreCase) || totalStep2NoDataSkipped > 0 || scopeResults.Any(r => r.Status.Equals("no_data", StringComparison.OrdinalIgnoreCase))
                ? "W"
                : "S";
        aggregate.SapStatusText = aggregate.Message;
        aggregate.DurationMs = EnsureDuration(0, started);
        AddWorkflowLog(aggregate, "workflow", $"ZFI057 auto workflow finished; status={aggregate.Status}; step2Success={totalStep2Success}; step2NoData={totalStep2NoDataSkipped}; {BuildZfi057WorkflowScopeResultMessage(aggregate.Status, scopeResults)}");
        return aggregate;
    }

    static Zfi057Step1MaterialFetch ExecuteZfi057Step1Memory(SapRunParams p, string businessArea, string[] plants)
    {
        var started = DateTime.UtcNow;
        var result = new RunResultRequest
        {
            Status = "failed",
            Message = "ZFI019NL memory fetch failed"
        };

        SapNcoConnectionConfig connectionConfig = BuildSapNcoConnectionConfig(p);
        var request = BuildZfi019NlMemoryRequest(p, businessArea, plants);
        result.Logs.Add(new RunLogLine { Level = "INFO", Message = $"ZFI019NL memory fetch destination: {connectionConfig.SafeSummary()}" });
        result.Logs.Add(new RunLogLine { Level = "INFO", Message = $"ZFI019NL memory fetch selections: S_BUDAT={GetSelectionSummary(request, "S_BUDAT")}; S_GSBER={GetSelectionSummary(request, "S_GSBER")}; splitWerks={FormatSample(request.SplitWerks, 8)}; memoryId={request.MemoryId}; memoryName={request.MemoryName}; splitTable={request.SplitTable}; splitBukrs={request.SplitBukrs}" });

        Zfi019NlFetchResult fetchResult;
        try
        {
            fetchResult = new Zfi019NlMemoryFetcher().Fetch(connectionConfig, request);
        }
        catch (Exception ex)
        {
            fetchResult = new Zfi019NlFetchResult
            {
                Subrc = 8,
                Message = $"ZFI019NL memory fetch threw: {ex.Message}",
                ActualMethod = "MEMORY_EXPORT"
            };
        }

        string[] materials = fetchResult.FinalRows
            .Select(row => row.TryGetValue(Zfi019NlMemoryFetcher.FinalMaterialColumn, out string? value) ? value : "")
            .Select(v => v.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        result.Status = fetchResult.Success ? "success" : "failed";
        result.Message = fetchResult.Success
            ? $"ZFI019NL memory fetch returned {materials.Length} material(s)."
            : FirstNonEmpty(fetchResult.Message, "ZFI019NL memory fetch failed.");
        result.DurationMs = EnsureDuration(0, started);
        result.SapStatusType = fetchResult.Success ? "S" : "E";
        result.SapStatusText = fetchResult.Message;
        result.Logs.Add(new RunLogLine { Level = fetchResult.Success ? "INFO" : "ERROR", Message = $"ZFI019NL memory fetch result: success={fetchResult.Success}; subrc={fetchResult.Subrc}; method={fetchResult.ActualMethod}; message={Truncate(fetchResult.Message, 360)}" });
        result.Logs.Add(new RunLogLine { Level = "INFO", Message = $"ZFI019NL memory fetch options={fetchResult.Options}" });
        result.Logs.Add(new RunLogLine { Level = "INFO", Message = $"ZFI019NL memory fetch counts: rawLines={fetchResult.RawLines.Count}; headers={fetchResult.Headers.Count}; alvRows={fetchResult.AlvRows.Count}; finalRows={fetchResult.FinalRows.Count}; splitMaterials={fetchResult.SplitMaterialCount}" });
        result.Logs.Add(new RunLogLine { Level = "INFO", Message = $"ZFI019NL memory fetch headerSample={Truncate(string.Join("|", fetchResult.Headers), 1200)}" });
        result.Logs.Add(new RunLogLine { Level = "INFO", Message = $"ZFI019NL memory fetch materials: count={materials.Length}; sample={FormatSample(materials, 8)}; hash={HashForLog(string.Join(",", materials))}" });
        string[] splitMaterials = fetchResult.SplitRows
            .Select(row => row.TryGetValue(Zfi019NlMemoryFetcher.FinalMaterialColumn, out string? value) ? value : "")
            .Select(v => v.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToArray();
        result.Logs.Add(new RunLogLine { Level = "INFO", Message = $"ZFI019NL memory fetch custom table materials: count={splitMaterials.Length}; sample={FormatSample(splitMaterials, 8)}; hash={HashForLog(string.Join(",", splitMaterials))}" });

        foreach (var sourceGroup in fetchResult.FinalRows
            .Select(row => row.TryGetValue(Zfi019NlMemoryFetcher.FinalSourceColumn, out string? source) ? FirstNonEmpty(source, "-") : "-")
            .GroupBy(source => source, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key))
        {
            result.Logs.Add(new RunLogLine { Level = "INFO", Message = $"ZFI019NL memory fetch source: {sourceGroup.Key} count={sourceGroup.Count()}" });
        }

        return new Zfi057Step1MaterialFetch(result, materials, fetchResult);
    }

    static RunResultRequest FailZfi057Workflow(RunResultRequest aggregate, string message, DateTime started)
    {
        aggregate.Status = "failed";
        aggregate.Message = message;
        aggregate.DurationMs = EnsureDuration(0, started);
        aggregate.Logs.Add(new RunLogLine { Level = "ERROR", Message = message });
        return aggregate;
    }

    static string BuildZfi057WorkflowScopeResultMessage(string status, IReadOnlyList<Zfi057WorkflowScopeResult> results)
    {
        string prefix = status.Equals("partial_failed", StringComparison.OrdinalIgnoreCase)
            ? "ZFI057流程部分失败"
            : status.Equals("failed", StringComparison.OrdinalIgnoreCase)
                ? "ZFI057流程失败"
                : "ZFI057流程完成";
        string scopeText = string.Join("，", results.Select(FormatZfi057ScopeResultForMessage));
        return $"{prefix}，业务范围：{scopeText}";
    }

    static string FormatZfi057ScopeResultForMessage(Zfi057WorkflowScopeResult result)
    {
        string area = FirstNonEmpty(result.BusinessArea, "-");
        if (result.Status.Equals("success", StringComparison.OrdinalIgnoreCase))
            return $"{area}运行成功";
        if (result.Status.Equals("no_data", StringComparison.OrdinalIgnoreCase))
            return $"{area}无数据";

        string detail = CleanDingTalkDisplayText(FirstNonEmpty(result.Message, ""));
        return string.IsNullOrWhiteSpace(detail)
            ? $"{area}运行失败"
            : $"{area}运行失败（{Truncate(detail, 80)}）";
    }

    static bool IsSuccessResult(RunResultRequest result)
    {
        return NormalizeRunStatus(result.Status).Equals("success", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsZfi057Step2NoDataResult(RunResultRequest result)
    {
        var candidates = new List<string>
        {
            result.Message ?? "",
            result.SapStatusText ?? ""
        };
        candidates.AddRange(result.Logs.Select(line => line.Message ?? ""));

        return candidates.Any(IsZfi057NoDataText);
    }

    static bool IsZfi057NoDataText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string value = text.Trim();
        string compact = Regex.Replace(value, @"\s+", "");
        return compact.Contains("没有符合条件数据", StringComparison.OrdinalIgnoreCase) ||
               compact.Contains("没有符合条件的数据", StringComparison.OrdinalIgnoreCase) ||
               compact.Contains("没有找到符合条件的数据", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("No data found", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("No records found", StringComparison.OrdinalIgnoreCase);
    }

    static void AddWorkflowLog(RunResultRequest aggregate, string step, string message)
    {
        aggregate.Logs.Add(new RunLogLine { Level = "INFO", Message = $"[{step}] {message}" });
    }

    static void AddStepResult(RunResultRequest aggregate, string step, RunResultRequest result, bool propagateSapStatus = true)
    {
        AddWorkflowLog(aggregate, step, $"result: status={result.Status}; durationMs={result.DurationMs}; message={Truncate(result.Message, 240)}; sapStatusType={result.SapStatusType}; sapStatusText={Truncate(result.SapStatusText, 240)}");
        foreach (var line in result.Logs)
        {
            aggregate.Logs.Add(new RunLogLine
            {
                Level = FirstNonEmpty(line.Level, "INFO"),
                Message = $"[{step}] {line.Message}",
                CreatedAt = line.CreatedAt
            });
        }

        foreach (var file in result.Files)
            aggregate.Files.Add(file);

        if (!propagateSapStatus)
            return;

        if (!string.IsNullOrWhiteSpace(result.SapStatusType))
            aggregate.SapStatusType = result.SapStatusType;
        if (!string.IsNullOrWhiteSpace(result.SapStatusText))
            aggregate.SapStatusText = result.SapStatusText;
    }

    static void AddSkippedStepResult(RunResultRequest aggregate, string step, RunResultRequest result)
    {
        aggregate.Logs.Add(new RunLogLine
        {
            Level = "WARN",
            Message = $"[{step}] result skipped: originalStatus={result.Status}; durationMs={result.DurationMs}; message={Truncate(result.Message, 240)}; sapStatusType={result.SapStatusType}; sapStatusText={Truncate(result.SapStatusText, 240)}"
        });

        foreach (var line in result.Logs)
        {
            aggregate.Logs.Add(new RunLogLine
            {
                Level = "WARN",
                Message = $"[{step}] original {FirstNonEmpty(line.Level, "INFO")}: {line.Message}",
                CreatedAt = line.CreatedAt
            });
        }

        foreach (var file in result.Files)
            aggregate.Files.Add(file);
    }

    static Zfi057Step3ScopeResult ExecuteZfi057Step3ScopeClosure(RunResultRequest aggregate, SapRunParams p, string area, string[] plants)
    {
        var step3 = BuildZfi057Step3Params(p, area, plants);

        var firstStep3 = ExecuteZfi057Step3Attempt(aggregate, step3, area, 1, 2);
        if (!IsSuccessResult(firstStep3))
            return new Zfi057Step3ScopeResult(false, $"step3 first run failed: {FirstNonEmpty(firstStep3.Message, firstStep3.SapStatusText, "ZCO020 failed")}", "", "", false);

        var firstStep3CompletedUtc = DateTime.UtcNow;
        var firstCheck = RunZfi057TbtcoJobCheck(step3, area, 1, firstStep3CompletedUtc);
        AddZfi057TbtcoJobCheckResult(aggregate, firstCheck, 1);
        if (!ShouldRepeatZfi057Step3AfterTbtcoCheck(firstCheck))
            return new Zfi057Step3ScopeResult(false, $"TBTCO job check did not reach terminal status after first ZCO020: {firstCheck.Message}", firstCheck.Status, "", false);

        AddWorkflowLog(aggregate, "step 3", $"TBTCO job {Zfi057TbtcoJobName} reached terminal status after first ZCO020; status={firstCheck.Status}; rerun ZCO020 for scope closure");
        var secondStep3 = ExecuteZfi057Step3Attempt(aggregate, step3, area, 2, 2);
        if (!IsSuccessResult(secondStep3))
            return new Zfi057Step3ScopeResult(false, $"step3 repeat failed: {FirstNonEmpty(secondStep3.Message, secondStep3.SapStatusText, "ZCO020 repeat failed")}", firstCheck.Status, "", true);

        return new Zfi057Step3ScopeResult(true, $"step3=success; tbtcoFirst={firstCheck.Status}; step3Repeat=success", firstCheck.Status, "", true);
    }

    static SapRunParams BuildZfi057Step3Params(SapRunParams p, string area, string[] plants)
    {
        var step3 = CloneSapRunParams(p);
        step3.TCode = "ZCO020";
        step3.Script = "ZCO020.vbs";
        step3.BusinessAreas = area;
        step3.BusinessArea = area;
        step3.Plants = string.Join(",", plants.Where(x => !string.IsNullOrWhiteSpace(x)));
        step3.Plant = FirstCsvValue(step3.Plants);
        step3.Materials = "";
        step3.RunStrategy = "workflow-step";
        step3.TimeoutSeconds = Math.Max(900, Math.Min(p.TimeoutSeconds.GetValueOrDefault(900), 1800));
        return step3;
    }

    static RunResultRequest ExecuteZfi057Step3Attempt(RunResultRequest aggregate, SapRunParams step3, string area, int attempt, int totalAttempts)
    {
        AddWorkflowLog(aggregate, "step 3", $"query: attempt={attempt}/{totalAttempts}; script={step3.Script}; tcode={step3.TCode}; businessArea={area}; plants={step3.Plants}; period={step3.Period}; weekEnd={step3.WeekEnd}; timeoutSeconds={step3.TimeoutSeconds}; followUpJob={Zfi057TbtcoJobName}");
        var result = LaunchSapGuiAndExecute(step3);
        AddStepResult(aggregate, attempt == 1 ? "step 3 ZCO020" : "step 3 ZCO020 repeat", result);
        return result;
    }

    static void AddZfi057TbtcoJobCheckResult(RunResultRequest aggregate, Zfi057TbtcoJobCheckResult check, int attempt)
    {
        AddStepResult(aggregate, $"step 3 TBTCO {Zfi057TbtcoJobName} check #{attempt}", check.RawResult, propagateSapStatus: false);
        aggregate.Logs.Add(new RunLogLine
        {
            Level = check.IsTerminal ? (check.IsFailure ? "WARN" : "INFO") : "ERROR",
            Message = $"[step 3] TBTCO job check #{attempt}: status={check.Status}; category={check.Category}; terminal={check.IsTerminal}; failed={check.IsFailure}; message={Truncate(check.Message, 360)}"
        });
    }

    static Zfi057TbtcoJobCheckResult RunZfi057TbtcoJobCheck(SapRunParams p, string businessArea, int attempt, DateTime firstZco020CompletedUtc)
    {
        var started = DateTime.UtcNow;
        var raw = new RunResultRequest
        {
            Status = "failed",
            Message = "TBTCO job check did not reach terminal status"
        };

        SapNcoConnectionConfig connectionConfig = BuildSapNcoConnectionConfig(p);
        string jobUser = ResolveZfi057TbtcoJobUser(p, connectionConfig);
        var lowerUtc = firstZco020CompletedUtc.AddMinutes(-1);
        var deadlineUtc = started.AddSeconds(Zfi057TbtcoPollTimeoutSeconds);
        var poller = new SapJobStatusFetcher();
        string lastStatus = "";
        string lastCategory = "unknown";
        string lastMessage = "";
        int poll = 0;

        raw.Logs.Add(new RunLogLine { Level = "INFO", Message = $"TBTCO job check destination: {connectionConfig.SafeSummary()}" });
        raw.Logs.Add(new RunLogLine { Level = "INFO", Message = $"TBTCO job check scope: jobName={Zfi057TbtcoJobName}; user={MaskForLog(jobUser)}; businessArea={businessArea}; lower={lowerUtc.ToLocalTime():yyyyMMdd HHmmss}; timeoutSeconds={Zfi057TbtcoPollTimeoutSeconds}; intervalSeconds={Zfi057TbtcoPollIntervalSeconds}" });
        if (!string.IsNullOrWhiteSpace(p.User) &&
            !string.IsNullOrWhiteSpace(connectionConfig.User) &&
            !p.User.Trim().Equals(connectionConfig.User.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            raw.Logs.Add(new RunLogLine { Level = "WARN", Message = $"TBTCO job owner uses SAP GUI user {MaskForLog(p.User)}, while NCo connection user is {MaskForLog(connectionConfig.User)}" });
        }

        while (DateTime.UtcNow <= deadlineUtc)
        {
            poll++;
            var upperUtc = DateTime.UtcNow.AddMinutes(1);
            var query = new SapJobStatusQuery
            {
                JobName = Zfi057TbtcoJobName,
                JobUser = jobUser,
                LowerUtc = lowerUtc,
                UpperUtc = upperUtc
            };
            var result = poller.FetchLatest(connectionConfig, query);
            raw.Logs.Add(new RunLogLine { Level = "INFO", Message = $"TBTCO poll #{poll}: sql={result.SqlSummary}" });
            raw.Logs.Add(new RunLogLine { Level = "INFO", Message = $"TBTCO poll #{poll}: options={string.Join(" | ", result.Options)}; rawRows={result.Rows.Count}; message={Truncate(result.Message, 360)}" });

            if (!result.Success)
            {
                raw.Message = result.Message;
                raw.DurationMs = EnsureDuration(0, started);
                raw.SapStatusType = "E";
                raw.SapStatusText = result.Message;
                return new Zfi057TbtcoJobCheckResult(false, false, false, "query_failed", "unknown", result.Message, raw);
            }

            if (result.Latest != null)
            {
                lastStatus = SapJobStatusFetcher.DescribeTbtcoStatus(result.Latest.Status);
                lastCategory = SapJobStatusFetcher.NormalizeTbtcoStatusCategory(result.Latest.Status);
                lastMessage = result.Message;
                raw.Logs.Add(new RunLogLine
                {
                    Level = "INFO",
                    Message = $"TBTCO latest: job={result.Latest.JobName}/{result.Latest.JobCount}; user={MaskForLog(result.Latest.User)}; status={lastStatus}; category={lastCategory}; start={FormatNullableSapJobTime(result.Latest.EffectiveStartLocal)}; end={FormatNullableSapJobTime(result.Latest.EndLocal)}"
                });

                if (SapJobStatusFetcher.IsTbtcoTerminalStatus(result.Latest.Status))
                {
                    bool failed = SapJobStatusFetcher.IsTbtcoFailureStatus(result.Latest.Status);
                    raw.Status = "success";
                    raw.Message = $"TBTCO job ended after first ZCO020: {lastMessage}";
                    raw.SapStatusType = failed ? "W" : "S";
                    raw.SapStatusText = raw.Message;
                    raw.DurationMs = EnsureDuration(0, started);
                    return new Zfi057TbtcoJobCheckResult(true, true, failed, lastStatus, lastCategory, raw.Message, raw);
                }
            }
            else
            {
                lastStatus = "not_found";
                lastCategory = "not_found";
                lastMessage = result.Message;
            }

            if (DateTime.UtcNow.AddSeconds(Zfi057TbtcoPollIntervalSeconds) > deadlineUtc)
                break;

            Thread.Sleep(TimeSpan.FromSeconds(Zfi057TbtcoPollIntervalSeconds));
        }

        string timeoutMessage = $"TBTCO job {Zfi057TbtcoJobName} did not reach terminal status within {Zfi057TbtcoPollTimeoutSeconds}s after first ZCO020; lastStatus={lastStatus}; lastMessage={lastMessage}";
        raw.Status = "failed";
        raw.Message = timeoutMessage;
        raw.SapStatusType = "E";
        raw.SapStatusText = timeoutMessage;
        raw.DurationMs = EnsureDuration(0, started);
        return new Zfi057TbtcoJobCheckResult(false, false, false, lastStatus, lastCategory, timeoutMessage, raw);
    }

    static bool ShouldRepeatZfi057Step3AfterTbtcoCheck(Zfi057TbtcoJobCheckResult check)
    {
        return check.Success && check.IsTerminal;
    }

    static string ResolveZfi057TbtcoJobUser(SapRunParams p, SapNcoConnectionConfig connectionConfig)
    {
        return FirstNonEmpty(p.User, connectionConfig.User);
    }

    static string FormatSample(IEnumerable<string> values, int maxItems)
    {
        var items = values
            .Select(v => FirstNonEmpty(v, "").Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Take(Math.Max(1, maxItems))
            .ToArray();
        return items.Length == 0 ? "-" : string.Join(",", items);
    }

    static string HashForLog(string value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? ""));
        return Convert.ToHexString(bytes).Substring(0, 12);
    }

    static Zfi019NlFetchRequest BuildZfi019NlMemoryRequest(SapRunParams p, string businessArea, string[] plants)
    {
        DateTime defaultStart = StartOfWeek(DateTime.Today).AddDays(-7);
        DateTime defaultEnd = defaultStart.AddDays(6);
        DateTime start = ParseFlexibleDateOrDefault(p.Period, defaultStart);
        DateTime end = ParseFlexibleDateOrDefault(p.WeekEnd, defaultEnd);
        if (end < start)
            (start, end) = (end, start);

        var config = LoadZfi019NlMemoryConfig();
        var request = new Zfi019NlFetchRequest
        {
            Report = FirstNonEmpty(config.Report, "ZFI019NL"),
            Variant = config.Variant,
            MemoryId = FirstNonEmpty(config.MemoryId, "%ZFI019NA%"),
            MemoryName = FirstNonEmpty(config.MemoryName, "GT_ALV"),
            SpoolDevice = FirstNonEmpty(config.SpoolDevice, "LP01"),
            WaitSeconds = config.WaitSeconds > 0 ? config.WaitSeconds : 60,
            SplitTable = FirstNonEmpty(config.SplitTable, "ZFI_SPLIT"),
            SplitBukrs = FirstNonEmpty(config.SplitBukrs, "2030"),
            DongtaiBusinessAreas = config.DongtaiBusinessAreas.ToList(),
            SplitWerks = plants
                .Select(v => FirstNonEmpty(v, "").Trim())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
        request.BusinessAreas.Add(businessArea);
        request.Conditions.Add(new Zfi019NlSelection
        {
            Selname = "S_BUDAT",
            Sign = "I",
            Option = "BT",
            Low = start.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            High = end.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
        });
        request.Conditions.Add(new Zfi019NlSelection
        {
            Selname = "S_GSBER",
            Sign = "I",
            Option = "EQ",
            Low = businessArea
        });
        return request;
    }

    static SapNcoConnectionConfig BuildSapNcoConnectionConfig(SapRunParams p)
    {
        SapNcoLocalConfig config = LoadSapNcoLocalConfig();
        string systemId = FirstNonEmpty(config.SystemId, p.System);
        string ipAddress = FirstNonEmpty(config.IpAddress, config.AppServerHost, config.Ashost);
        string systemNumber = NormalizeSapSystemNumber(FirstNonEmpty(config.SystemNumber, config.SysNr, p.SysNr));
        string router = FirstNonEmpty(config.Router, config.SapRouter);

        if (string.IsNullOrWhiteSpace(ipAddress) || string.IsNullOrWhiteSpace(systemNumber) || string.IsNullOrWhiteSpace(systemId))
        {
            foreach (var entry in ReadSapLogonEntries())
            {
                if (!SapLogonEntryMatches(entry, p.System) &&
                    !SapLogonEntryMatches(entry, config.ConnectionName) &&
                    !SapLogonEntryMatches(entry, config.SystemId))
                    continue;

                ipAddress = FirstNonEmpty(ipAddress, entry.Server);
                systemNumber = NormalizeSapSystemNumber(FirstNonEmpty(systemNumber, entry.SystemNumber));
                systemId = FirstNonEmpty(systemId, entry.SystemId, entry.Description);
                router = FirstNonEmpty(router, entry.Router);
                break;
            }
        }

        return new SapNcoConnectionConfig
        {
            ConnectionName = FirstNonEmpty(config.ConnectionName, config.Name, p.System, "SapWebLauncher"),
            SystemId = systemId,
            IpAddress = ipAddress,
            SystemNumber = systemNumber,
            Client = FirstNonEmpty(config.Client, p.Client),
            User = FirstNonEmpty(config.User, p.User),
            Password = FirstNonEmpty(config.Password, p.Password),
            Language = FirstNonEmpty(config.Language, config.Lang, p.Language, "ZH"),
            Router = router
        };
    }

    static SapNcoLocalConfig LoadSapNcoLocalConfig()
    {
        using JsonDocument? document = LoadLocalConfigDocument();
        JsonElement? sapNco = TryGetObject(document?.RootElement, "sapNco") ??
                              TryGetObject(document?.RootElement, "nco") ??
                              TryGetObject(document?.RootElement, "sapDestination");

        if (!sapNco.HasValue)
            return new SapNcoLocalConfig();

        var config = new SapNcoLocalConfig
        {
            ConnectionName = FirstNonEmpty(GetConfigString(sapNco, "connectionName"), GetConfigString(sapNco, "destinationName")),
            Name = GetConfigString(sapNco, "name"),
            SystemId = FirstNonEmpty(GetConfigString(sapNco, "systemId"), GetConfigString(sapNco, "sysId"), GetConfigString(sapNco, "sid")),
            IpAddress = FirstNonEmpty(GetConfigString(sapNco, "ipAddress"), GetConfigString(sapNco, "server")),
            AppServerHost = GetConfigString(sapNco, "appServerHost"),
            Ashost = GetConfigString(sapNco, "ashost"),
            SystemNumber = FirstNonEmpty(GetConfigString(sapNco, "systemNumber"), GetConfigString(sapNco, "instanceNumber")),
            SysNr = GetConfigString(sapNco, "sysNr"),
            Client = GetConfigString(sapNco, "client"),
            User = GetConfigString(sapNco, "user"),
            Password = FirstNonEmpty(GetConfigString(sapNco, "password"), GetConfigString(sapNco, "passwd")),
            Language = GetConfigString(sapNco, "language"),
            Lang = GetConfigString(sapNco, "lang"),
            Router = GetConfigString(sapNco, "router"),
            SapRouter = GetConfigString(sapNco, "sapRouter")
        };

        string protectedPassword = FirstNonEmpty(
            GetConfigString(sapNco, "passwordProtected"),
            GetConfigString(sapNco, "passwdProtected"));
        if (!string.IsNullOrWhiteSpace(protectedPassword))
            config.Password = FirstNonEmpty(UnprotectSecretIfPresent(protectedPassword), config.Password);

        return config;
    }

    static Zfi019NlMemoryLocalConfig LoadZfi019NlMemoryConfig()
    {
        using JsonDocument? document = LoadLocalConfigDocument();
        JsonElement? root = document?.RootElement;
        JsonElement? zfi057 = TryGetObject(root, "zfi057Workflow");
        JsonElement? zfi019nl = TryGetObject(zfi057, "zfi019nlMemory") ??
                                TryGetObject(root, "zfi019nlMemory");

        if (!zfi019nl.HasValue)
            return new Zfi019NlMemoryLocalConfig();

        return new Zfi019NlMemoryLocalConfig
        {
            Report = GetConfigString(zfi019nl, "report"),
            Variant = GetConfigString(zfi019nl, "variant"),
            MemoryId = GetConfigString(zfi019nl, "memoryId"),
            MemoryName = GetConfigString(zfi019nl, "memoryName"),
            SpoolDevice = GetConfigString(zfi019nl, "spoolDevice"),
            WaitSeconds = GetConfigInt(zfi019nl, "waitSeconds", 60),
            SplitTable = GetConfigString(zfi019nl, "splitTable"),
            SplitBukrs = GetConfigString(zfi019nl, "splitBukrs"),
            DongtaiBusinessAreas = NormalizeStringArray(FirstNonEmpty(
                GetConfigString(zfi019nl, "dongtaiBusinessAreas"),
                GetConfigString(zfi019nl, "specialBusinessAreas")))
        };
    }

    static string LoadZfi057Gs03SetName()
    {
        try
        {
            using JsonDocument? document = LoadLocalConfigDocument();
            JsonElement? root = document?.RootElement;
            JsonElement? zfi057 = TryGetObject(root, "zfi057Workflow");
            return FirstNonEmpty(
                GetConfigString(zfi057, "gs03SetName"),
                GetConfigString(zfi057, "getGs03SetName"),
                GetConfigString(zfi057, "businessAreaSetName"),
                GetConfigString(root, "zfi057Gs03SetName"),
                SapGs03PlantFetcher.DefaultSetName);
        }
        catch
        {
            return SapGs03PlantFetcher.DefaultSetName;
        }
    }

    static string GetSelectionSummary(Zfi019NlFetchRequest request, string selname)
    {
        var items = request.Conditions
            .Where(c => c.Selname.Equals(selname, StringComparison.OrdinalIgnoreCase))
            .Select(c => $"{c.Sign}:{c.Option}:{c.Low}:{c.High}")
            .ToArray();
        return items.Length == 0 ? "-" : string.Join(",", items);
    }

    static int GetConfigInt(JsonElement? item, string property, int defaultValue)
    {
        return item.HasValue ? GetJsonInt(item.Value, property, defaultValue) : defaultValue;
    }

    static void AddZfi057MaterialAuditFile(
        RunResultRequest aggregate,
        SapRunParams p,
        int scopeIndex,
        string businessArea,
        string[] plants,
        string selectedSource,
        string[] requestMaterials,
        string[] upstreamMaterials,
        string[] selectedMaterials,
        IReadOnlyList<Dictionary<string, string>> sourceRows)
    {
        try
        {
            string directory = Path.Combine(OutputDirectory, "zfi057");
            Directory.CreateDirectory(directory);
            string runPart = SafeFileNamePart(FirstNonEmpty(p.RunId, DateTime.Now.ToString("yyyyMMddHHmmss")));
            string areaPart = SafeFileNamePart(FirstNonEmpty(businessArea, "scope"));
            string path = Path.Combine(directory, $"{runPart}_scope{scopeIndex}_{areaPart}_materials.csv");
            var lines = new List<string>
            {
                "section,key,value",
                $"meta,runId,{CsvCell(FirstNonEmpty(p.RunId, "-"))}",
                $"meta,scopeIndex,{scopeIndex}",
                $"meta,businessArea,{CsvCell(businessArea)}",
                $"meta,step2Plants,{CsvCell(string.Join(",", plants.Where(x => !string.IsNullOrWhiteSpace(x))))}",
                $"meta,period,{CsvCell(p.Period)}",
                $"meta,weekEnd,{CsvCell(p.WeekEnd)}",
                $"meta,selectedSource,{CsvCell(selectedSource)}",
                $"summary,requestCount,{requestMaterials.Length}",
                $"summary,upstreamCount,{upstreamMaterials.Length}",
                $"summary,selectedCount,{selectedMaterials.Length}",
                $"summary,selectedHash,{CsvCell(HashForLog(string.Join(",", selectedMaterials)))}",
                $"summary,customTableCount,{sourceRows.Count}",
                "",
                "source,index,material"
            };

            AppendMaterialRows(lines, "selected", selectedMaterials);
            AppendMaterialRows(lines, "upstream", upstreamMaterials);
            AppendMaterialRows(lines, "request", requestMaterials);
            if (sourceRows.Count > 0)
            {
                lines.Add("");
                lines.Add("material,source");
                foreach (var row in sourceRows)
                {
                    row.TryGetValue(Zfi019NlMemoryFetcher.FinalMaterialColumn, out string? material);
                    row.TryGetValue(Zfi019NlMemoryFetcher.FinalSourceColumn, out string? source);
                    lines.Add($"{CsvCell(material ?? "")},{CsvCell(source ?? "")}");
                }
            }
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
            aggregate.Files.Add(BuildRunFile(path));
            AddWorkflowLog(aggregate, "step 1", $"material audit file={path}");
        }
        catch (Exception ex)
        {
            AddWorkflowLog(aggregate, "step 1", $"material audit file failed: {ex.Message}");
        }
    }

    static void AppendMaterialRows(List<string> lines, string source, string[] materials)
    {
        for (int i = 0; i < materials.Length; i++)
            lines.Add($"{CsvCell(source)},{i + 1},{CsvCell(materials[i])}");
    }

    static string SafeFileNamePart(string value)
    {
        string part = Regex.Replace(FirstNonEmpty(value, "-"), @"[^A-Za-z0-9_.-]+", "_").Trim('_', '.');
        return string.IsNullOrWhiteSpace(part) ? "na" : part;
    }

    static string SafeDisplayFileNamePart(string value)
    {
        string text = FirstNonEmpty(value, "na").Trim();
        if (string.IsNullOrWhiteSpace(text))
            return "na";

        foreach (char c in Path.GetInvalidFileNameChars())
            text = text.Replace(c, '_');

        text = Regex.Replace(text, @"\s+", "_").Trim('_', '.');
        return string.IsNullOrWhiteSpace(text) ? "na" : text;
    }

    static bool SupportsAlvExport(string tcode)
    {
        return tcode.Equals("ZFI072A", StringComparison.OrdinalIgnoreCase) ||
               tcode.Equals("ZFI072N", StringComparison.OrdinalIgnoreCase) ||
               tcode.Equals("ZFI080", StringComparison.OrdinalIgnoreCase) ||
               tcode.Equals("ZFI080B", StringComparison.OrdinalIgnoreCase) ||
               tcode.Equals("ZCO019", StringComparison.OrdinalIgnoreCase) ||
               tcode.Equals("ZFI019NA", StringComparison.OrdinalIgnoreCase) ||
               tcode.Equals("ZFI019NL", StringComparison.OrdinalIgnoreCase);
    }

    static AlvExportTarget BuildAlvExportTarget(SapRunParams p, string effectivePlants)
    {
        if (!SupportsAlvExport(p.TCode))
            return AlvExportTarget.Empty;

        try
        {
            string plantValue = FirstNonEmpty(
                FirstCsvValue(effectivePlants),
                p.Plant,
                FirstCsvValue(p.BusinessAreas),
                p.BusinessArea,
                p.FactoryGroup,
                "scope");
            string transactionName = ResolveTransactionDisplayName(p.TCode);
            DateTime archiveDate = DateTime.Now;
            string directory;
            string fileName;
            if (UsesBusinessAreaAlvOutput(p.TCode))
            {
                string businessArea = FirstNonEmpty(FirstCsvValue(p.BusinessAreas), p.BusinessArea, plantValue);
                directory = GetAlvBusinessAreaRawOutputDirectory(archiveDate, businessArea);
                fileName = BuildAlvBusinessAreaRawFileName(p.TCode, transactionName, businessArea, archiveDate);
            }
            else
            {
                string plantPart = SafeFileNamePart(plantValue);
                directory = GetAlvFactoryOutputDirectory(archiveDate, plantPart);
                fileName = BuildAlvDirectPlantFileName(p.TCode, transactionName, plantValue, archiveDate);
            }
            Directory.CreateDirectory(directory);
            return new AlvExportTarget(directory, fileName, Path.Combine(directory, fileName));
        }
        catch (Exception ex)
        {
            Log($"prepare ALV export target failed: tcode={p.TCode}, runId={p.RunId}, {ex}");
            return AlvExportTarget.Empty;
        }
    }

    static bool UsesDirectPlantAlvOutput(string tcode)
    {
        return tcode.Equals("ZFI072A", StringComparison.OrdinalIgnoreCase) ||
               tcode.Equals("ZFI072N", StringComparison.OrdinalIgnoreCase) ||
               tcode.Equals("ZFI080", StringComparison.OrdinalIgnoreCase) ||
               tcode.Equals("ZFI080B", StringComparison.OrdinalIgnoreCase) ||
               tcode.Equals("ZCO019", StringComparison.OrdinalIgnoreCase) ||
               tcode.Equals("ZFI019NA", StringComparison.OrdinalIgnoreCase);
    }

    static bool UsesBusinessAreaAlvOutput(string tcode)
    {
        return tcode.Equals("ZFI019NL", StringComparison.OrdinalIgnoreCase);
    }

    static string BuildAlvDirectPlantFileName(string tcode, string transactionName, string plant, DateTime at)
    {
        string transactionPart = SafeDisplayFileNamePart(string.Join("_",
            new[] { (tcode ?? "").Trim().ToUpperInvariant(), transactionName }.Where(v => !string.IsNullOrWhiteSpace(v))));
        string displayPlant = SafeDisplayFileNamePart(plant);
        string timestamp = at.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        return $"{transactionPart}_\u5DE5\u5382{displayPlant}_{timestamp}.xlsx";
    }

    static string BuildAlvBusinessAreaRawFileName(string tcode, string transactionName, string businessArea, DateTime at)
    {
        string transactionPart = SafeDisplayFileNamePart(string.Join("_",
            new[] { (tcode ?? "").Trim().ToUpperInvariant(), transactionName }.Where(v => !string.IsNullOrWhiteSpace(v))));
        string displayArea = SafeDisplayFileNamePart(businessArea);
        string timestamp = at.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        return $"{transactionPart}_\u4E1A\u52A1\u8303\u56F4{displayArea}_{timestamp}.xlsx";
    }

    static bool TryGetAlvWindowPartBasePath(string path, out string basePath)
    {
        basePath = "";
        if (string.IsNullOrWhiteSpace(path) || !IsExcelWorkbookPath(path))
            return false;

        string directory = Path.GetDirectoryName(path) ?? "";
        string fileName = Path.GetFileName(path);
        var match = Regex.Match(fileName, @"^(?<stem>.+)_part(?<index>[1-9][0-9]*)(?<ext>\.xlsx?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return false;

        basePath = Path.Combine(directory, match.Groups["stem"].Value + match.Groups["ext"].Value);
        return true;
    }

    static int ExtractAlvWindowPartNumber(string path)
    {
        string fileName = Path.GetFileName(path ?? "");
        var match = Regex.Match(fileName, @"_part(?<index>[1-9][0-9]*)\.xlsx?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups["index"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
            ? index
            : int.MaxValue;
    }

    static void MoveAlvPartToFinalFile(string partPath, string finalPath)
    {
        byte[] bytes = ReadAlvFragmentBytesForMerge(partPath);
        if (File.Exists(finalPath))
            File.Delete(finalPath);
        File.WriteAllBytes(finalPath, bytes);
        DeleteFileWithRetry(partPath);
    }

    static void MergeAlvPartFilesToFinalWorkbook(string finalPath, List<RunFile> parts)
    {
        string tempPath = Path.Combine(
            Path.GetDirectoryName(finalPath) ?? AlvExportDataDirectory,
            $"{Path.GetFileNameWithoutExtension(finalPath)}_merge_{Guid.NewGuid():N}{Path.GetExtension(finalPath)}");

        try
        {
            using var output = new XLWorkbook();
            var merged = output.Worksheets.Add("ALV");

            int outputRow = 1;
            bool headerWritten = false;
            int copiedRows = 0;
            int maxColumns = 0;

            foreach (var part in parts)
            {
                string partPath = Environment.ExpandEnvironmentVariables(part.Path ?? "");
                byte[] sourceBytes = ReadAlvFragmentBytesForMerge(partPath);
                using var sourceStream = new MemoryStream(sourceBytes, writable: false);
                using var source = new XLWorkbook(sourceStream);
                var sheet = source.Worksheets.FirstOrDefault();
                var range = sheet?.RangeUsed();
                if (sheet == null || range == null)
                    continue;

                var rows = range.RowsUsed().ToList();
                if (rows.Count == 0)
                    continue;

                int sourceColumnCount = range.ColumnCount();
                maxColumns = Math.Max(maxColumns, sourceColumnCount);
                if (!headerWritten)
                {
                    CopyAlvRowValues(rows[0], merged, outputRow, sourceColumnCount);
                    outputRow++;
                    headerWritten = true;
                }

                foreach (var row in rows.Skip(1))
                {
                    CopyAlvRowValues(row, merged, outputRow, sourceColumnCount);
                    outputRow++;
                    copiedRows++;
                }
            }

            if (!headerWritten)
                merged.Cell(1, 1).Value = "";

            if (maxColumns > 0)
                merged.Columns(1, maxColumns).AdjustToContents();

            output.Properties.Title = Path.GetFileNameWithoutExtension(finalPath);
            output.Properties.Subject = $"SAP RPA ALV plant data; rows={copiedRows}";
            output.SaveAs(tempPath);

            if (File.Exists(finalPath))
                File.Delete(finalPath);
            File.Move(tempPath, finalPath);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch { }
        }
    }

    static List<RunFile> SplitAlvWorkbookByFactory(
        string rawPath,
        string transactionCode,
        string businessArea,
        DateTime archiveDate,
        string? outputRoot,
        string logRunId,
        List<RunLogLine>? logs)
    {
        var outputFiles = new List<RunFile>();
        byte[] sourceBytes = ReadAlvFragmentBytesForMerge(rawPath);
        using var sourceStream = new MemoryStream(sourceBytes, writable: false);
        using var source = new XLWorkbook(sourceStream);
        var sheet = source.Worksheets.FirstOrDefault();
        var range = sheet?.RangeUsed();
        if (sheet == null || range == null)
            return outputFiles;

        var rows = range.RowsUsed().ToList();
        if (rows.Count == 0)
            return outputFiles;

        if (rows.Count == 1)
        {
            AddAlvNormalizationLog(logRunId, logs, "WARN", $"ALV business-area workbook has header only and no data rows: businessArea={businessArea}, raw={rawPath}");
            return outputFiles;
        }

        int sourceColumnCount = range.ColumnCount();
        var factoryColumn = FindAlvFactoryColumn(rows, sourceColumnCount);
        if (factoryColumn.RowIndex < 0 || factoryColumn.ColumnIndex <= 0)
            throw new InvalidOperationException($"ALV factory column not found in exported workbook: {rawPath}");

        var groupedRows = new Dictionary<string, List<IXLRangeRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Skip(factoryColumn.RowIndex + 1))
        {
            string factory = NormalizeAlvFactoryValue(row.Cell(factoryColumn.ColumnIndex).GetString());
            if (string.IsNullOrWhiteSpace(factory))
                continue;

            if (!groupedRows.TryGetValue(factory, out var list))
            {
                list = new List<IXLRangeRow>();
                groupedRows[factory] = list;
            }

            list.Add(row);
        }

        if (groupedRows.Count == 0)
        {
            throw new InvalidOperationException($"ALV business-area workbook has data rows but no factory values: businessArea={businessArea}, raw={rawPath}");
        }

        string transactionName = ResolveTransactionDisplayName(transactionCode);
        foreach (var group in groupedRows.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            string factory = group.Key;
            string directory = GetAlvFactoryOutputDirectory(archiveDate, factory, outputRoot);
            Directory.CreateDirectory(directory);
            string fileName = BuildAlvDirectPlantFileName(transactionCode, transactionName, factory, archiveDate);
            string finalPath = EnsureUniqueAlvOutputPath(Path.Combine(directory, fileName));
            string tempPath = Path.Combine(
                directory,
                $"{Path.GetFileNameWithoutExtension(finalPath)}_split_{Guid.NewGuid():N}{Path.GetExtension(finalPath)}");

            try
            {
                using var output = new XLWorkbook();
                var target = output.Worksheets.Add("ALV");
                int outputRow = 1;
                CopyAlvRowValues(rows[factoryColumn.RowIndex], target, outputRow++, sourceColumnCount);
                foreach (var sourceRow in group.Value)
                    CopyAlvRowValues(sourceRow, target, outputRow++, sourceColumnCount);

                target.Columns(1, sourceColumnCount).AdjustToContents();
                output.Properties.Title = Path.GetFileNameWithoutExtension(finalPath);
                output.Properties.Subject = $"SAP RPA ALV factory split; businessArea={businessArea}; factory={factory}; rows={group.Value.Count}";
                output.SaveAs(tempPath);

                if (File.Exists(finalPath))
                    File.Delete(finalPath);
                File.Move(tempPath, finalPath);
                outputFiles.Add(BuildRunFile(finalPath));
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch { }
            }
        }

        return outputFiles;
    }

    static (int RowIndex, int ColumnIndex) FindAlvFactoryColumn(List<IXLRangeRow> rows, int sourceColumnCount)
    {
        int rowsToScan = Math.Min(rows.Count, 10);
        for (int rowIndex = 0; rowIndex < rowsToScan; rowIndex++)
        {
            for (int col = 1; col <= sourceColumnCount; col++)
            {
                if (IsAlvFactoryHeader(rows[rowIndex].Cell(col).GetString()))
                    return (rowIndex, col);
            }
        }

        return (-1, 0);
    }

    static bool IsAlvFactoryHeader(string value)
    {
        string normalized = Regex.Replace(FirstNonEmpty(value, ""), "[\\s\\u3000:\\uFF1A_\\-]+", "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return normalized.Equals("WERKS", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("WERK", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("PLANT", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("PLANTCODE", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("PLANTNO", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("PLANTNUMBER", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("FACTORY", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("FACTORYCODE", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("BIGBUFACTORY", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("\u5DE5\u5382", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("\u5DE5\u5382\u53F7", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("\u5DE5\u5382\u4EE3\u7801", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("\u5DE5\u5382\u7F16\u7801", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("\u5C0F\u5382", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("\u5927BU\u5DE5\u5382", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("\u4E1A\u52A1\u8303\u56F4\u5C0F\u5382", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("\u751F\u4EA7\u5DE5\u5382", StringComparison.OrdinalIgnoreCase);
    }

    static string NormalizeAlvFactoryValue(string value)
    {
        string factory = FirstNonEmpty(value, "").Trim();
        if (decimal.TryParse(factory, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal numeric) &&
            numeric == Math.Truncate(numeric))
        {
            factory = numeric.ToString("0", CultureInfo.InvariantCulture);
        }

        return Regex.Replace(factory, @"\s+", "");
    }

    static string EnsureUniqueAlvOutputPath(string path)
    {
        if (!File.Exists(path))
            return path;

        string directory = Path.GetDirectoryName(path) ?? AlvExportDataDirectory;
        string stem = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int index = 2; index < 1000; index++)
        {
            string candidate = Path.Combine(directory, $"{stem}_{index}{extension}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(directory, $"{stem}_{Guid.NewGuid():N}{extension}");
    }

    static void CopyAlvRowValues(IXLRangeRow sourceRow, IXLWorksheet targetSheet, int targetRow, int columnCount)
    {
        for (int col = 1; col <= columnCount; col++)
            targetSheet.Cell(targetRow, col).Value = sourceRow.Cell(col).Value;
    }

    static void DeleteAlvPartFiles(List<RunFile> parts, string finalPath, string logRunId, List<RunLogLine>? logs)
    {
        string normalizedFinalPath = Path.GetFullPath(finalPath);
        foreach (var part in parts)
        {
            string partPath = Environment.ExpandEnvironmentVariables(part.Path ?? "");
            if (string.IsNullOrWhiteSpace(partPath) || !File.Exists(partPath))
                continue;

            if (Path.GetFullPath(partPath).Equals(normalizedFinalPath, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                DeleteFileWithRetry(partPath);
            }
            catch (Exception ex)
            {
                AddAlvNormalizationLog(logRunId, logs, "WARN", $"ALV part cleanup failed: {partPath}; {ex.Message}");
            }
        }
    }

    static void DeleteFileWithRetry(string path)
    {
        Exception? lastException = null;
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow <= deadline)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }
            catch (IOException ex)
            {
                lastException = ex;
                Thread.Sleep(500);
            }
            catch (UnauthorizedAccessException ex)
            {
                lastException = ex;
                Thread.Sleep(500);
            }
        }

        throw new IOException($"file remained locked before delete: {path}", lastException);
    }

    static void DeleteEmptyParentDirectoriesUnder(string rootDirectory, string deletedFilePath)
    {
        try
        {
            string root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? current = Path.GetDirectoryName(Path.GetFullPath(deletedFilePath));
            while (!string.IsNullOrWhiteSpace(current))
            {
                string normalized = current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                    normalized.Equals(root, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (Directory.EnumerateFileSystemEntries(normalized).Any())
                    break;

                Directory.Delete(normalized);
                current = Path.GetDirectoryName(normalized);
            }
        }
        catch { }
    }

    static void DeleteEmptyDirectoryIfExists(string directory)
    {
        try
        {
            string normalized = Path.GetFullPath(directory);
            if (Directory.Exists(normalized) && !Directory.EnumerateFileSystemEntries(normalized).Any())
                Directory.Delete(normalized);
        }
        catch { }
    }

    static string ResolveTransactionDisplayName(string tcode)
    {
        string normalized = (tcode ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return "";

        try
        {
            InitializeDatabase(seedFromScripts: true);
            using var connection = OpenDatabaseConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM transactions WHERE tcode=$tcode LIMIT 1";
            command.Parameters.AddWithValue("$tcode", normalized);
            string name = command.ExecuteScalar() as string ?? "";
            return name.Trim();
        }
        catch (Exception ex)
        {
            Log($"resolve transaction display name failed: tcode={normalized}, {ex.Message}");
            return "";
        }
    }

    static string GetAlvWeekFolderName(DateTime date)
    {
        int week = ISOWeek.GetWeekOfYear(date);
        return $"{date.Year.ToString(CultureInfo.InvariantCulture)}_WK{week.ToString("00", CultureInfo.InvariantCulture)}";
    }

    static string GetAlvFactoryOutputDirectory(DateTime date, string plant)
    {
        return GetAlvFactoryOutputDirectory(date, plant, outputRoot: null);
    }

    static string GetAlvFactoryOutputDirectory(DateTime date, string plant, string? outputRoot)
    {
        string plantPart = SafeFileNamePart(FirstNonEmpty(plant, "scope"));
        string root = string.IsNullOrWhiteSpace(outputRoot) ? AlvExportDataDirectory : Path.GetFullPath(outputRoot);
        return Path.Combine(root, $"{GetAlvWeekFolderName(date)}_{plantPart}");
    }

    static string GetAlvBusinessAreaRawRoot()
    {
        return Path.Combine(AlvExportDataDirectory, "_raw_business_area");
    }

    static string GetAlvBusinessAreaRawOutputDirectory(DateTime date, string businessArea)
    {
        string areaPart = SafeFileNamePart(FirstNonEmpty(businessArea, "scope"));
        return Path.Combine(GetAlvBusinessAreaRawRoot(), $"{GetAlvWeekFolderName(date)}_{areaPart}");
    }

    static byte[] ReadAlvFragmentBytesForMerge(string path)
    {
        Exception? lastException = null;
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow <= deadline)
        {
            try
            {
                using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var buffer = new MemoryStream();
                source.CopyTo(buffer);
                return buffer.ToArray();
            }
            catch (IOException ex)
            {
                lastException = ex;
                Thread.Sleep(500);
            }
            catch (UnauthorizedAccessException ex)
            {
                lastException = ex;
                Thread.Sleep(500);
            }
        }

        throw new IOException($"ALV fragment remained locked before merge: {path}", lastException);
    }

    static string CsvCell(string value)
    {
        string text = value ?? "";
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    static string ExtractMaterialsFromResult(RunResultRequest result)
    {
        var materials = new List<string>();
        foreach (var line in result.Logs)
        {
            string message = line.Message ?? "";
            int bracket = message.LastIndexOf(']');
            if (bracket >= 0 && bracket + 1 < message.Length)
                message = message[(bracket + 1)..].Trim();

            if (TryReadOutputKey(message, "MATERIAL", out string material))
                materials.Add(material);
            else if (TryReadOutputKey(message, "MATERIALS_CSV", out string csv))
                materials.AddRange(NormalizeStringArray(csv));
        }

        return string.Join(",", materials
            .Select(v => v.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    static List<Zfi057WorkflowScope> ResolveZfi057WorkflowScopes(SapRunParams p)
    {
        string[] requestedAreas = NormalizeStringArray(FirstNonEmpty(p.BusinessAreas, p.BusinessArea));
        if (requestedAreas.Length == 0)
            return new List<Zfi057WorkflowScope>();

        var result = new List<Zfi057WorkflowScope>();
        foreach (string area in requestedAreas)
        {
            result.Add(new Zfi057WorkflowScope(area, ResolveZfi057Gs03Plants(p, area)));
        }

        return result;
    }

    static string[] ResolveZfi057Gs03Plants(SapRunParams p, string area)
    {
        try
        {
            SapNcoConnectionConfig connectionConfig = BuildSapNcoConnectionConfig(p);
            if (!connectionConfig.IsComplete(out string configError))
            {
                Log($"ZFI057 GET_GS03 mapping skipped: businessArea={area}; SAP NCo config incomplete: {configError}");
                return Array.Empty<string>();
            }

            string setName = LoadZfi057Gs03SetName();
            var result = new SapGs03PlantFetcher().FetchPlantsForBusinessArea(connectionConfig, area, setName);
            if (result.Success && result.Plants.Count > 0)
            {
                Log($"ZFI057 GET_GS03 mapping loaded from SAP: setName={result.SetName}; businessArea={area}; plants={string.Join(",", result.Plants)}");
                return DistinctPreserveOrder(result.Plants);
            }

            Log($"ZFI057 GET_GS03 mapping failed: setName={result.SetName}; businessArea={area}; reason={result.Message}");
        }
        catch (Exception ex)
        {
            Log($"ZFI057 GET_GS03 mapping failed: businessArea={area}; error={ex.Message}");
        }

        return Array.Empty<string>();
    }

    static string[] DistinctPreserveOrder(IEnumerable<string> values)
    {
        return values
            .Select(v => FirstNonEmpty(v, "").Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    static void AddDistinctValuesRaw(List<string> target, IEnumerable<string> values)
    {
        foreach (string value in values)
        {
            string trimmed = FirstNonEmpty(value, "").Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;
            if (!target.Any(v => v.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
                target.Add(trimmed);
        }
    }

    static SapRunParams CloneSapRunParams(SapRunParams p)
    {
        return new SapRunParams
        {
            System = p.System,
            Client = p.Client,
            User = p.User,
            Password = p.Password,
            Language = p.Language,
            SysNr = p.SysNr,
            MultiLogonPolicy = p.MultiLogonPolicy,
            TCode = p.TCode,
            Script = p.Script,
            Plant = p.Plant,
            Plants = p.Plants,
            Year = p.Year,
            Week = p.Week,
            Period = p.Period,
            BusinessArea = p.BusinessArea,
            BusinessAreas = p.BusinessAreas,
            WeekEnd = p.WeekEnd,
            Materials = p.Materials,
            FactoryGroup = p.FactoryGroup,
            RunStrategy = p.RunStrategy,
            Field1Name = p.Field1Name,
            Field1Value = p.Field1Value,
            Field2Name = p.Field2Name,
            Field2Value = p.Field2Value,
            CaretPos = p.CaretPos,
            ButtonId = p.ButtonId,
            RunId = p.RunId,
            TimeoutSeconds = p.TimeoutSeconds
        };
    }

    static IDisposable StartRunHeartbeat(string runId)
    {
        lock (ActiveRunLock)
            ActiveExecutingRunIds.Add(runId);

        var stop = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            while (!stop.Wait(TimeSpan.FromSeconds(QueueHeartbeatIntervalSeconds)))
                RefreshRunHeartbeat(runId);
        })
        {
            IsBackground = true,
            Name = $"SapRpaRunHeartbeat-{runId}"
        };
        thread.Start();
        RefreshRunHeartbeat(runId);
        return new RunHeartbeatScope(runId, stop, thread, CompleteRunHeartbeat);
    }

    static void CompleteRunHeartbeat(string runId)
    {
        lock (ActiveRunLock)
            ActiveExecutingRunIds.Remove(runId);
    }

    static bool IsRunActiveInCurrentProcess(string runId)
    {
        lock (ActiveRunLock)
            return ActiveExecutingRunIds.Contains(runId);
    }

    static void RefreshRunHeartbeat(string runId)
    {
        try
        {
            using var connection = OpenDatabaseConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
UPDATE runs
SET locked_at=$lockedAt,
    locked_by=$lockedBy
WHERE run_id=$runId
  AND status='running';
""";
            command.Parameters.AddWithValue("$runId", runId);
            command.Parameters.AddWithValue("$lockedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            command.Parameters.AddWithValue("$lockedBy", ExecutorId);
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Log($"refresh run heartbeat failed: runId={runId}, {ex.Message}");
        }
    }

    static void MarkBatchItemRunning(string childRunId)
    {
        try
        {
            using var connection = OpenDatabaseConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
UPDATE run_batch_items
SET status='running',
    started_at=CASE WHEN started_at='' THEN $startedAt ELSE started_at END,
    updated_at=$startedAt
WHERE child_run_id=$childRunId;
""";
            command.Parameters.AddWithValue("$childRunId", childRunId);
            command.Parameters.AddWithValue("$startedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Log($"mark batch item running failed: child={childRunId}, {ex.Message}");
        }
    }

    static NameValueCollection BuildQueryFromRunRequest(CreateRunRequest request, QueuedRunWorkItem item)
    {
        var query = new NameValueCollection
        {
            ["action"] = "run",
            ["tcode"] = item.TransactionCode,
            ["script"] = FirstNonEmpty(item.ScriptFile, DefaultScriptForTCode(item.TransactionCode)),
            ["runid"] = item.RunId,
            ["parentrunid"] = item.ParentRunId
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
        NotifyRunEvent(runId, "start", "执行器已开始处理任务");
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
        string parentRunId = UpdateBatchAfterChildCompletion(runId, status, result, now);
        if (!string.IsNullOrWhiteSpace(parentRunId))
        {
            AppendRunLog(runId, "INFO", $"child result recorded for parent {parentRunId}");
            return;
        }

        string notifyMessage = status == "success"
            ? "任务执行完成"
            : $"任务执行失败：{FirstNonEmpty(result.Message ?? "", result.SapStatusText ?? "", status)}";
        CleanupSapGuiSessionAfterRunId(runId);
        UpdateScheduleRunStatusForRun(runId, status, notifyMessage);
        bool vbsAlreadySentSapDingTalk = HasVbsSapDingTalkNotifyResult(result);
        NotifyRunEvent(runId, status == "success" ? "success" : "failure", notifyMessage, vbsAlreadySentSapDingTalk);
    }

    static TransactionScriptInfo LoadScriptInfo(string tcode)
    {
        using var connection = OpenDatabaseConnection();
        string normalizedTcode = tcode.ToUpperInvariant();
        string scriptFile = "";
        string scriptHash = "";
        string paramsJson = "";
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT script_file, script_hash, params_json FROM transactions WHERE tcode=$tcode";
            command.Parameters.AddWithValue("$tcode", normalizedTcode);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                scriptFile = reader.GetString(0);
                scriptHash = reader.GetString(1);
                paramsJson = reader.GetString(2);
            }
        }

        if (!string.IsNullOrWhiteSpace(scriptFile))
        {
            scriptHash = FirstNonEmpty(LoadCachedScriptHash(connection, normalizedTcode), scriptHash);
            return new TransactionScriptInfo
            {
                ScriptFile = scriptFile,
                ScriptHash = scriptHash,
                ParamKeys = SafeJsonArray(paramsJson)
            };
        }

        return new TransactionScriptInfo { ScriptFile = $"{normalizedTcode}.vbs" };
    }

    static string LoadCachedScriptHash(SqliteConnection connection, string tcode)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT script_hash FROM script_cache WHERE tcode=$tcode";
        command.Parameters.AddWithValue("$tcode", tcode.ToUpperInvariant());
        return command.ExecuteScalar() as string ?? "";
    }

    static void CleanupSapGuiSessionAfterRunId(string runId)
    {
        try
        {
            var run = LoadRun(runId, includeDetails: false);
            if (run == null)
                return;
            if (run.RunType.Equals("child", StringComparison.OrdinalIgnoreCase))
            {
                AppendRunLog(runId, "INFO", "SAP cleanup deferred to parent batch completion");
                return;
            }

            var request = JsonSerializer.Deserialize<CreateRunRequest>(run.RequestJson, new JsonSerializerOptions(JsonOptions)
            {
                PropertyNameCaseInsensitive = true
            }) ?? new CreateRunRequest { TransactionCode = run.TransactionCode };
            EnsureCreateRunRequestDefaults(request);
            var query = BuildQueryFromRunRequest(request, new QueuedRunWorkItem
            {
                RunId = run.RunId,
                TransactionCode = run.TransactionCode,
                RequestJson = run.RequestJson,
                ScriptFile = run.ScriptFile,
                RunType = run.RunType,
                ParentRunId = run.ParentRunId
            });
            var pars = BuildParams(query, PrimaryProtocolName);
            pars.RunId = runId;
            CleanupSapGuiSessionAfterRun(pars);
            AppendRunLog(runId, "INFO", "SAP cleanup requested after final transaction completion");
        }
        catch (Exception ex)
        {
            AppendRunLog(runId, "WARN", $"SAP cleanup after final completion failed: {ex.Message}");
            Log($"SAP cleanup after final completion failed: runId={runId}, {ex}");
        }
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
            DingTalkUserId = reader.GetString(5),
            Status = reader.GetString(6),
            RequestJson = reader.GetString(7),
            SapStatusType = reader.GetString(8),
            SapStatusText = reader.GetString(9),
            Message = reader.GetString(10),
            ScriptFile = reader.GetString(11),
            ScriptHash = reader.GetString(12),
            QueuedAt = reader.GetString(13),
            StartedAt = reader.GetString(14),
            FinishedAt = reader.GetString(15),
            DurationMs = reader.GetInt64(16),
            Source = reader.GetString(17),
            NotifyTarget = reader.GetString(18),
            Priority = reader.GetInt32(19),
            Attempt = reader.GetInt32(20),
            MaxAttempts = reader.GetInt32(21),
            LockedBy = reader.GetString(22),
            LockedAt = reader.GetString(23),
            RunType = reader.FieldCount > 24 ? reader.GetString(24) : "single",
            ParentRunId = reader.FieldCount > 25 ? reader.GetString(25) : "",
            BatchItemKey = reader.FieldCount > 26 ? reader.GetString(26) : "",
            BatchIndex = reader.FieldCount > 27 ? reader.GetInt32(27) : 0,
            BatchTotal = reader.FieldCount > 28 ? reader.GetInt32(28) : 0,
            AttemptNo = reader.FieldCount > 29 ? reader.GetInt32(29) : 1,
            SummaryJson = reader.FieldCount > 30 ? reader.GetString(30) : "",
            SourceParentRunId = reader.FieldCount > 31 ? reader.GetString(31) : "",
            RerunOfRunId = reader.FieldCount > 32 ? reader.GetString(32) : "",
            TransactionName = reader.FieldCount > 33 ? reader.GetString(33) : ""
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

    static void ReplaceRunFiles(string runId, IEnumerable<RunFile> files)
    {
        using var connection = OpenDatabaseConnection();
        using var tx = connection.BeginTransaction();
        using (var deleteFiles = connection.CreateCommand())
        {
            deleteFiles.Transaction = tx;
            deleteFiles.CommandText = "DELETE FROM run_files WHERE run_id=$runId";
            deleteFiles.Parameters.AddWithValue("$runId", runId);
            deleteFiles.ExecuteNonQuery();
        }

        foreach (var file in files)
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
    }

    static List<BatchItemStatus> LoadBatchItems(string parentRunId)
    {
        var items = new List<BatchItemStatus>();
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
SELECT parent_run_id, child_run_id, plant_code, batch_index, attempt_no, status,
       message, started_at, finished_at, duration_ms
FROM run_batch_items
WHERE parent_run_id=$parentRunId
ORDER BY batch_index, attempt_no, child_run_id;
""";
        command.Parameters.AddWithValue("$parentRunId", parentRunId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new BatchItemStatus
            {
                ParentRunId = reader.GetString(0),
                ChildRunId = reader.GetString(1),
                Plant = reader.GetString(2),
                BatchIndex = reader.GetInt32(3),
                AttemptNo = reader.GetInt32(4),
                Status = reader.GetString(5),
                Message = reader.GetString(6),
                StartedAt = reader.GetString(7),
                FinishedAt = reader.GetString(8),
                DurationMs = reader.GetInt64(9)
            });
        }

        return items;
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

    static void NotifyRunEvent(string runId, string eventName, string message, bool skipSapDingTalk = false)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return;

        if (IsChildRun(runId))
        {
            AppendRunLog(runId, "INFO", $"child notify suppressed {eventName}: {message}");
            return;
        }

        AppendRunLog(runId, "INFO", $"notify {eventName} queued: {message}");
        ThreadPool.QueueUserWorkItem(_ => DispatchRunNotification(runId, eventName, message, skipSapDingTalk));
    }

    static bool IsChildRun(string runId)
    {
        try
        {
            using var connection = OpenDatabaseConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT run_type FROM runs WHERE run_id=$runId";
            command.Parameters.AddWithValue("$runId", runId);
            return (command.ExecuteScalar() as string ?? "").Equals("child", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    static void DispatchRunNotification(string runId, string eventName, string message, bool skipSapDingTalk)
    {
        bool parentBatchStart = eventName.Equals("start", StringComparison.OrdinalIgnoreCase) && IsParentRun(runId);
        if (IsRunFinishedEvent(eventName) || parentBatchStart)
        {
            if (!RunRequestsSapDingTalkNotification(runId))
            {
                AppendRunLog(runId, "INFO", "sap dingtalk notify skipped: notifyTarget is not dingtalk");
            }
            else if (skipSapDingTalk)
            {
                AppendRunLog(runId, "INFO", "sap dingtalk notify skipped: legacy VBS notification result detected");
            }
            else if (!ShouldSendSapDingTalkForRunEvent(runId, eventName))
            {
                AppendRunLog(runId, "INFO", $"sap dingtalk notify skipped: schedule notify flag disabled for {eventName}");
            }
            else
            {
                try
                {
                    SendSapDingTalkNotification(runId, eventName, message);
                }
                catch (Exception ex)
                {
                    AppendRunLog(runId, "WARN", $"sap dingtalk notify failed: {ex.Message}");
                }
            }
        }

        var targets = LoadNotificationTargetsForRun(runId, eventName);
        string targetText = targets.Count == 0 ? "local" : string.Join(",", targets.Select(t => t.Label));
        AppendRunLog(runId, "INFO", $"notify {eventName} target={targetText}: {message}");
        foreach (var target in targets.Where(t => !string.IsNullOrWhiteSpace(t.Webhook)))
            SendNotification(runId, eventName, message, target);
    }

    static bool ShouldSendSapDingTalkForRunEvent(string runId, string eventName)
    {
        try
        {
            var run = LoadRun(runId, includeDetails: false);
            string source = run?.Source ?? "";
            const string schedulePrefix = "schedule:";
            if (!source.StartsWith(schedulePrefix, StringComparison.OrdinalIgnoreCase))
                return true;

            string scheduleId = source[schedulePrefix.Length..].Trim();
            if (string.IsNullOrWhiteSpace(scheduleId))
                return true;

            using var connection = OpenDatabaseConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
SELECT notify_enabled, notify_on_start, notify_on_success, notify_on_failure
FROM schedule_tasks
WHERE id=$id;
""";
            command.Parameters.AddWithValue("$id", scheduleId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return true;

            if (reader.GetInt32(0) != 1)
                return false;
            if (eventName.Equals("start", StringComparison.OrdinalIgnoreCase))
                return reader.GetInt32(1) == 1;
            if (eventName.Equals("success", StringComparison.OrdinalIgnoreCase))
                return reader.GetInt32(2) == 1;
            if (eventName.Equals("failure", StringComparison.OrdinalIgnoreCase) ||
                eventName.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
                eventName.Equals("finish", StringComparison.OrdinalIgnoreCase))
                return reader.GetInt32(3) == 1;
            return true;
        }
        catch (Exception ex)
        {
            Log($"read schedule notify flags failed: run={runId}, event={eventName}, {ex.Message}");
            return true;
        }
    }

    static bool RunRequestsSapDingTalkNotification(string runId)
    {
        try
        {
            using var connection = OpenDatabaseConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT notify_target FROM runs WHERE run_id=$runId";
            command.Parameters.AddWithValue("$runId", runId);
            string notifyTarget = command.ExecuteScalar() as string ?? "";
            return SplitNotifyTargets(notifyTarget).Any(IsSapDingTalkTarget);
        }
        catch (Exception ex)
        {
            Log($"read notify target failed: {runId}, {ex.Message}");
            return false;
        }
    }

    static IEnumerable<string> SplitNotifyTargets(string notifyTarget)
    {
        return (notifyTarget ?? "")
            .Split(new[] { ',', ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    static bool IsSapDingTalkTarget(string target)
    {
        string normalized = NormalizeNotifyTarget(target);
        return normalized.Equals("dingtalk", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("dingding", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("ding", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("sap-dingtalk", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("sap_dingtalk", StringComparison.OrdinalIgnoreCase);
    }

    static string NormalizeNotifyTarget(string target)
    {
        string value = (target ?? "").Trim();
        int delimiterIndex = value.IndexOf(':');
        if (delimiterIndex > 0)
            return value[..delimiterIndex].Trim();
        return value;
    }

    static string ResolveDingTalkUserIdFromNotifyTarget(string notifyTarget)
    {
        foreach (string target in SplitNotifyTargets(notifyTarget))
        {
            int delimiterIndex = target.IndexOf(':');
            if (delimiterIndex <= 0)
                continue;

            string kind = target[..delimiterIndex].Trim();
            string value = target[(delimiterIndex + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(value) && IsSapDingTalkTarget(kind))
                return value;
        }

        return "";
    }

    static bool IsParentRun(string runId)
    {
        try
        {
            using var connection = OpenDatabaseConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT run_type FROM runs WHERE run_id=$runId";
            command.Parameters.AddWithValue("$runId", runId);
            return (command.ExecuteScalar() as string ?? "").Equals("parent", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    static List<NotificationTarget> LoadNotificationTargetsForRun(string runId, string eventName)
    {
        var targets = new List<NotificationTarget>();
        try
        {
            using var connection = OpenDatabaseConnection();
            string tcode = "";
            string notifyTarget = "";
            using (var runCommand = connection.CreateCommand())
            {
                runCommand.CommandText = "SELECT transaction_code, notify_target FROM runs WHERE run_id=$runId";
                runCommand.Parameters.AddWithValue("$runId", runId);
                using var reader = runCommand.ExecuteReader();
                if (reader.Read())
                {
                    tcode = reader.GetString(0);
                    notifyTarget = reader.GetString(1);
                }
            }

            using var command = connection.CreateCommand();
            command.CommandText = """
SELECT DISTINCT r.id, r.name, r.robot_type, COALESCE(NULLIF(r.target_label, ''), r.name, r.id),
       r.webhook_protected, r.secret_protected
FROM notification_robots r
LEFT JOIN notification_robot_bindings b ON b.robot_id = r.id
WHERE r.enabled=1
  AND (
      b.robot_id IS NULL
      OR (
          b.enabled=1
          AND (
              b.event_name=''
              OR b.event_name=$eventName
              OR b.event_name='all'
              OR b.event_name=$eventAlias1
              OR b.event_name=$eventAlias2
              OR b.event_name=$eventAlias3
          )
          AND (b.tcode='' OR b.tcode=$tcode)
      )
  )
ORDER BY 1;
""";
            command.Parameters.AddWithValue("$eventName", eventName);
            string[] aliases = NotificationEventAliases(eventName);
            command.Parameters.AddWithValue("$eventAlias1", aliases.ElementAtOrDefault(0) ?? "");
            command.Parameters.AddWithValue("$eventAlias2", aliases.ElementAtOrDefault(1) ?? "");
            command.Parameters.AddWithValue("$eventAlias3", aliases.ElementAtOrDefault(2) ?? "");
            command.Parameters.AddWithValue("$tcode", tcode);
            using var robotReader = command.ExecuteReader();
            while (robotReader.Read())
            {
                string label = robotReader.GetString(3);
                if (string.IsNullOrWhiteSpace(label) ||
                    targets.Any(t => t.Label.Equals(label, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                targets.Add(new NotificationTarget
                {
                    Id = robotReader.GetString(0),
                    Name = robotReader.GetString(1),
                    RobotType = robotReader.GetString(2),
                    Label = label,
                    Webhook = UnprotectSecretIfPresent(robotReader.GetString(4)),
                    Secret = UnprotectSecretIfPresent(robotReader.GetString(5))
                });
            }
        }
        catch (Exception ex)
        {
            Log($"load notification targets failed: {runId}, {eventName}, {ex.Message}");
        }

        return targets;
    }

    static string[] NotificationEventAliases(string eventName)
    {
        return eventName.ToLowerInvariant() switch
        {
            "start" => new[] { "执行开始", "开始执行", "started" },
            "success" => new[] { "执行成功", "成功", "completed" },
            "failure" or "failed" => new[] { "执行失败", "失败", "finish" },
            "finish" => new[] { "执行结束", "执行完成", "failure" },
            _ => Array.Empty<string>()
        };
    }

    static void SendNotification(string runId, string eventName, string message, NotificationTarget target)
    {
        try
        {
            var run = LoadRun(runId, includeDetails: false);
            string title = eventName.Equals("success", StringComparison.OrdinalIgnoreCase) ? "SAP RPA 执行成功" :
                eventName.Equals("start", StringComparison.OrdinalIgnoreCase) ? "SAP RPA 开始执行" :
                "SAP RPA 执行结束";
            string text = $"{title}\n\n" +
                          $"- RunId: {runId}\n" +
                          $"- 事务码: {run?.TransactionCode ?? ""}\n" +
                          $"- 状态: {run?.Status ?? eventName}\n" +
                          $"- 操作人: {run?.OperatorName ?? ""}\n" +
                          $"- 时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                          $"- 摘要: {message}";

            string payload = BuildNotificationPayload(target, title, text);
            var response = PostNotificationJson(BuildNotificationUrl(target), payload);
            string responseText = response.Body;
            if (response.IsSuccessStatusCode)
                AppendRunLog(runId, "INFO", $"notify sent target={target.Label}");
            else
                AppendRunLog(runId, "WARN", $"notify failed target={target.Label}, transport={response.Transport}, status={response.StatusCode}, body={Truncate(responseText, 200)}");
        }
        catch (Exception ex)
        {
            AppendRunLog(runId, "WARN", $"notify failed target={target.Label}: {ex.Message}");
        }
    }

    static void SendSapDingTalkNotification(string runId, string eventName, string message)
    {
        string provider = ResolveSapDingTalkProvider();
        var run = LoadRun(runId, includeDetails: true);
        string dingTalkId = FirstNonEmpty(
            ResolveDingTalkUserIdFromNotifyTarget(run?.NotifyTarget ?? ""),
            run?.DingTalkUserId ?? "",
            Environment.GetEnvironmentVariable("SAP_RPA_DINGTALK_ID") ?? "",
            DefaultDingTalkId);
        string workNo = FirstNonEmpty(
            Environment.GetEnvironmentVariable("SAP_RPA_DINGTALK_WORKNO") ?? "",
            "");
        string notifyMessage = BuildSapDingTalkMessage(run, message);
        string notifyContent = BuildSapDingTalkContent(run, notifyMessage);

        var request = new SapDingTalkNotifyRequest
        {
            RunId = runId,
            EventName = eventName,
            Message = notifyMessage,
            Content = notifyContent,
            MarkdownContent = BuildSapDingTalkMarkdownContent(run, notifyMessage),
            TransactionCode = run?.TransactionCode ?? "",
            Status = run?.Status ?? eventName,
            WorkNo = workNo,
            DingTalkId = dingTalkId
        };

        switch (provider)
        {
            case "direct":
            case "openapi":
                SendSapDingTalkNotificationByDirectOpenApi(runId, request);
                return;

            case "http":
                string endpoint = FirstNonEmpty(
                    Environment.GetEnvironmentVariable("SAP_RPA_DINGTALK_NOTIFY_URL") ?? "",
                    Environment.GetEnvironmentVariable("SAP_RPA_DINGTALK_HTTP_URL") ?? "");
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    AppendRunLog(runId, "INFO", $"sap dingtalk notify skipped: provider=http but no endpoint configured, IV_DDID={request.DingTalkId}");
                    return;
                }

                SendSapDingTalkNotificationByHttp(runId, endpoint, request);
                return;

            case "rfc":
                SendSapDingTalkNotificationByRfc(runId, request);
                return;

            case "command":
                string command = Environment.GetEnvironmentVariable("SAP_RPA_DINGTALK_NOTIFY_COMMAND") ?? "";
                if (string.IsNullOrWhiteSpace(command))
                {
                    AppendRunLog(runId, "INFO", $"sap dingtalk notify skipped: provider=command but no command configured, IV_DDID={request.DingTalkId}");
                    return;
                }

                SendSapDingTalkNotificationByCommand(runId, command, request);
                return;

            default:
                AppendRunLog(runId, "INFO", $"sap dingtalk notify skipped: provider={provider}, IV_DDID={request.DingTalkId}");
                return;
        }
    }

    static string BuildSapDingTalkMessage(RunRecordView? run, string fallbackMessage)
    {
        if (run == null)
            return fallbackMessage;

        string prefix = FormatDingTalkTitleText(run.Status);
        string sapText = CleanDingTalkDisplayText(SelectDingTalkSapMessageSource(run, fallbackMessage));
        string transactionText = FormatTransactionDisplay(run);
        return string.IsNullOrWhiteSpace(sapText) || sapText.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            ? $"{prefix}: {transactionText}"
            : $"{prefix}: {transactionText}, {sapText}";
    }

    static string BuildSapDingTalkContent(RunRecordView? run, string message)
    {
        if (run == null)
            return message;

        string sapMessage = BuildFriendlySapMessage(run, message);
        string statusLabel = FormatRunStatusForDingTalk(run.Status);
        string sapStatusType = FormatSapStatusType(run.SapStatusType);
        string transactionText = FormatTransactionDisplay(run);
        string title = $"{FormatDingTalkStatusIcon(run.Status)} SAP {FormatDingTalkTitleText(run.Status)}";
        string durationText = FirstNonEmpty(FormatDuration(run.DurationMs), "\u672A\u8BB0\u5F55");
        var inputLines = BuildDingTalkPlainInputLines(run);
        var failedLines = BuildDingTalkPlainFailedItemLines(run);
        var lines = new List<string>
        {
            title,
            "",
            $"\u3010\u6458\u8981\u3011{transactionText} / {statusLabel} / {durationText}",
            $"\U0001F4CC \u4E8B\u52A1\uFF1A{transactionText}",
            $"\U0001F4CA \u6267\u884C\u7ED3\u679C\uFF1A{statusLabel}",
            $"\U0001F514 SAP\u6D88\u606F\uFF1A{sapMessage}",
            $"\U0001F3F7\uFE0F SAP\u72B6\u6001\uFF1A{sapStatusType}",
            $"\u23F1\uFE0F \u6267\u884C\u8017\u65F6\uFF1A{durationText}",
            $"\U0001F552 \u5F00\u59CB\u65F6\u95F4\uFF1A{FirstNonEmpty(run.StartedAt, "\u672A\u8BB0\u5F55")}",
            $"\U0001F3C1 \u5B8C\u6210\u65F6\u95F4\uFF1A{FirstNonEmpty(run.FinishedAt, "\u672A\u8BB0\u5F55")}",
            $"\U0001F194 \u4EFB\u52A1\u7F16\u53F7\uFF1A{run.RunId}"
        };
        if (inputLines.Count > 0)
        {
            lines.Add("");
            lines.Add("\U0001F9FE \u6267\u884C\u5165\u53C2");
            lines.AddRange(inputLines);
        }

        if (failedLines.Count > 0)
        {
            lines.Add("");
            lines.Add("\u26A0\uFE0F \u5931\u8D25\u9879");
            lines.AddRange(failedLines);
        }

        return string.Join("\n", lines);
    }

    static string BuildSapDingTalkMarkdownContent(RunRecordView? run, string message)
    {
        if (run == null)
            return message;

        string statusLabel = FormatRunStatusForDingTalk(run.Status);
        string statusIcon = FormatDingTalkStatusIcon(run.Status);
        string titleText = FormatDingTalkTitleText(run.Status);
        string sapMessage = BuildFriendlySapMessage(run, message);
        string durationText = FirstNonEmpty(FormatDuration(run.DurationMs), "\u672A\u8BB0\u5F55");
        string sapStatusType = FormatSapStatusType(run.SapStatusType);
        string transactionText = FormatTransactionDisplay(run);
        string startedAt = FirstNonEmpty(run.StartedAt, "\u672A\u8BB0\u5F55");
        string finishedAt = FirstNonEmpty(run.FinishedAt, "\u672A\u8BB0\u5F55");
        string batchSummary = BuildDingTalkBatchSummary(run);
        var inputLines = BuildDingTalkMarkdownInputLines(run);
        var failedLines = BuildDingTalkMarkdownFailedItemLines(run);

        var lines = new List<string>
        {
            $"**{statusIcon} {EscapeMarkdownForDingTalk(titleText)}**",
            "",
            $"> \u72B6\u6001\uFF1A**{EscapeMarkdownForDingTalk(statusLabel)}**  |  \u4E8B\u52A1\uFF1A**{EscapeMarkdownForDingTalk(transactionText)}**  |  \u8017\u65F6\uFF1A{EscapeMarkdownForDingTalk(durationText)}",
            "",
            $"**\U0001F514 SAP\u6D88\u606F**",
            $"> {EscapeMarkdownForDingTalk(sapMessage)}",
            "",
            $"**\U0001F4CC \u6267\u884C\u6982\u89C8**",
            $"- \u6267\u884C\u7ED3\u679C\uFF1A{statusIcon} **{EscapeMarkdownForDingTalk(statusLabel)}**",
            $"- \u6279\u6B21\u7ED3\u679C\uFF1A{EscapeMarkdownForDingTalk(batchSummary)}",
            $"- \u4E8B\u52A1\uFF1A{EscapeMarkdownForDingTalk(transactionText)}",
            $"- SAP\u72B6\u6001\uFF1A{EscapeMarkdownForDingTalk(sapStatusType)}",
            $"- \u6267\u884C\u8017\u65F6\uFF1A{EscapeMarkdownForDingTalk(durationText)}",
            "",
            $"**\U0001F9FE \u6267\u884C\u5165\u53C2**"
        };
        lines.AddRange(inputLines.Count > 0 ? inputLines : new[] { "- **\u5165\u53C2**\uFF1A\u672A\u8BB0\u5F55" });
        if (failedLines.Count > 0)
        {
            lines.Add("");
            lines.Add("**\u26A0\uFE0F \u5931\u8D25\u9879**");
            lines.AddRange(failedLines);
        }

        lines.AddRange(new[]
        {
            "",
            $"**\U0001F552 \u65F6\u95F4\u8F74**",
            $"- \u5F00\u59CB\uFF1A{EscapeMarkdownForDingTalk(startedAt)}",
            $"- \u5B8C\u6210\uFF1A{EscapeMarkdownForDingTalk(finishedAt)}",
            "",
            $"**\U0001F194 \u4EFB\u52A1\u53F7**",
            $"`{EscapeMarkdownForDingTalk(run.RunId)}`"
        });

        return string.Join("\n", lines);
    }

    static string BuildDingTalkBatchSummary(RunRecordView run)
    {
        if (run.BatchItems.Count == 0)
            return "\u5355\u4E2A\u4EFB\u52A1";

        var latestItems = LatestBatchItemsByPlant(run.BatchItems);
        int total = latestItems.Count;
        int success = latestItems.Count(i => i.Status.Equals("success", StringComparison.OrdinalIgnoreCase));
        int failed = latestItems.Count(i => IsTerminalRunStatus(i.Status) && !i.Status.Equals("success", StringComparison.OrdinalIgnoreCase));
        int pending = latestItems.Count(i => !IsTerminalRunStatus(i.Status));

        var parts = new List<string> { $"\u6210\u529F {success}/{total}" };
        if (failed > 0)
            parts.Add($"\u5931\u8D25 {failed}");
        if (pending > 0)
            parts.Add($"\u672A\u5B8C\u6210 {pending}");

        return string.Join("\uFF0C", parts);
    }

    static string BuildFriendlySapMessage(RunRecordView run, string fallbackMessage)
    {
        string raw = SelectDingTalkSapMessageSource(run, fallbackMessage);
        string cleaned = CleanDingTalkDisplayText(raw);
        if (!string.IsNullOrWhiteSpace(cleaned))
            return cleaned;

        return run.Status.Equals("success", StringComparison.OrdinalIgnoreCase)
            ? "\u81EA\u52A8\u5316\u5DF2\u8DD1\u5B8C"
            : "\u81EA\u52A8\u5316\u6267\u884C\u5B8C\u6210\uFF0C\u8BF7\u5728\u8FD0\u884C\u65E5\u5FD7\u67E5\u770B\u8BE6\u60C5";
    }

    static string SelectDingTalkSapMessageSource(RunRecordView run, string fallbackMessage)
    {
        foreach (string candidate in new[] { run.SapStatusText, run.Message, fallbackMessage })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && !IsTechnicalSapStatusText(candidate))
                return candidate;
        }

        return run.Status.Equals("success", StringComparison.OrdinalIgnoreCase)
            ? "\u81EA\u52A8\u5316\u5DF2\u8DD1\u5B8C"
            : FirstNonEmpty(fallbackMessage, "\u81EA\u52A8\u5316\u6267\u884C\u5B8C\u6210\uFF0C\u8BF7\u5728\u8FD0\u884C\u65E5\u5FD7\u67E5\u770B\u8BE6\u60C5");
    }

    static bool IsTechnicalSapStatusText(string value)
    {
        string text = FirstNonEmpty(value, "");
        return Regex.IsMatch(
            text,
            @"^\s*(OK|S)?\s*:?\s*GENERATED_MEMORY_IMPORT\b.*\bfinished\b",
            RegexOptions.IgnoreCase);
    }

    static string CleanDingTalkDisplayText(string value)
    {
        string text = FirstNonEmpty(value, "").Trim();
        if (string.IsNullOrWhiteSpace(text))
            return "";

        if (IsTechnicalSapStatusText(text))
            return "";

        if (text.Contains("ZFI057 auto workflow completed with no-data skips", StringComparison.OrdinalIgnoreCase))
            return "ZFI057 \u81EA\u52A8\u6D41\u7A0B\u5DF2\u5B8C\u6210\uFF0C\u90E8\u5206\u4E1A\u52A1\u8303\u56F4\u65E0\u6570\u636E\u5DF2\u8DF3\u8FC7";

        if (text.Contains("ZFI057 auto workflow completed", StringComparison.OrdinalIgnoreCase))
            return "ZFI057 \u81EA\u52A8\u6D41\u7A0B\u5DF2\u5B8C\u6210";

        text = Regex.Replace(text, @"\bS_KADKY\b", "\u6210\u672C\u6838\u7B97\u65E5\u671F", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\bS_KADAT\b", "\u6210\u672C\u6838\u7B97\u65E5\u671F\u8D77\u4E8E", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"[A-Za-z]:\\[^\r\n;]+", "\u672C\u673A\u4E34\u65F6\u811A\u672C");
        if (text.Contains("VBS", StringComparison.OrdinalIgnoreCase) &&
            (text.Contains("\u8D85\u8FC7", StringComparison.OrdinalIgnoreCase) || text.Contains("timeout", StringComparison.OrdinalIgnoreCase)))
        {
            return "VBS \u6267\u884C\u8D85\u65F6\uFF0C\u5DF2\u505C\u6B62\u5E76\u6E05\u7406 SAP \u4F1A\u8BDD";
        }

        if (text.Contains("SAP login did not produce a ready scripting session", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("target SAP session not found", StringComparison.OrdinalIgnoreCase))
        {
            return "SAP GUI \u5DF2\u6253\u5F00\uFF0C\u4F46\u811A\u672C\u4F1A\u8BDD\u672A\u5C31\u7EEA\uFF1B\u5DF2\u505C\u6B62\u672C\u6B21\u4EFB\u52A1";
        }

        return text;
    }

    static string EscapeMarkdownForDingTalk(string value)
    {
        return FirstNonEmpty(value, "")
            .Replace("`", "'")
            .Replace("\r\n", "\n")
            .Replace("\r", "\n");
    }

    static string FormatRunStatusForDingTalk(string status)
    {
        if (status.Equals("success", StringComparison.OrdinalIgnoreCase))
            return "\u6210\u529F";
        if (status.Equals("partial_failed", StringComparison.OrdinalIgnoreCase))
            return "\u90E8\u5206\u5931\u8D25";
        if (status.Equals("failure", StringComparison.OrdinalIgnoreCase) || status.Equals("failed", StringComparison.OrdinalIgnoreCase))
            return "\u5931\u8D25";
        if (status.Equals("running", StringComparison.OrdinalIgnoreCase))
            return "\u6267\u884C\u4E2D";
        if (status.Equals("queued", StringComparison.OrdinalIgnoreCase) || status.Equals("pending", StringComparison.OrdinalIgnoreCase))
            return "\u6392\u961F\u4E2D";
        if (status.Equals("cancelled", StringComparison.OrdinalIgnoreCase) || status.Equals("canceled", StringComparison.OrdinalIgnoreCase))
            return "\u5DF2\u53D6\u6D88";

        return FirstNonEmpty(status, "\u672A\u77E5");
    }

    static string FormatDingTalkStatusIcon(string status)
    {
        string value = FirstNonEmpty(status, "").Trim().ToLowerInvariant();
        return value switch
        {
            "success" => "\u2705",
            "running" or "queued" or "pending" => "\u23F3",
            "partial_failed" => "\u26A0\uFE0F",
            "canceled" or "cancelled" => "\u23F9\uFE0F",
            _ => "\u274C"
        };
    }

    static string FormatDingTalkTitleText(string status)
    {
        string value = FirstNonEmpty(status, "").Trim().ToLowerInvariant();
        return value switch
        {
            "success" => "\u81EA\u52A8\u5316\u5DF2\u8DD1\u5B8C",
            "running" or "queued" or "pending" => "\u81EA\u52A8\u5316\u5F00\u59CB\u6267\u884C",
            "partial_failed" => "\u81EA\u52A8\u5316\u90E8\u5206\u5931\u8D25",
            "canceled" or "cancelled" => "\u81EA\u52A8\u5316\u5DF2\u53D6\u6D88",
            _ => "\u81EA\u52A8\u5316\u6267\u884C\u5931\u8D25"
        };
    }

    static string FormatSapStatusType(string statusType)
    {
        string value = FirstNonEmpty(statusType, "").Trim().ToUpperInvariant();
        return value switch
        {
            "S" => "\u6210\u529F",
            "W" => "\u8B66\u544A",
            "E" => "\u9519\u8BEF",
            "A" => "\u4E2D\u6B62",
            "I" => "\u4FE1\u606F",
            "" => "\u672A\u8FD4\u56DE",
            _ => value
        };
    }

    static string FormatPlantsForDingTalk(string plants)
    {
        string value = FirstNonEmpty(plants, "").Trim();
        if (string.IsNullOrWhiteSpace(value))
            return "\u672A\u6307\u5B9A";

        string replaced = value.Replace(",", "\u3001");
        const int maxLength = 120;
        return replaced.Length <= maxLength ? replaced : replaced[..maxLength] + "\u2026";
    }

    static List<string> BuildDingTalkPlainInputLines(RunRecordView run)
    {
        var lines = new List<string>();
        foreach (var input in BuildDingTalkRunInputs(run))
        {
            if (IsZfi057BusinessAreaPlantMappingLine(input))
            {
                lines.Add($"\u2022 {input.Label}\uFF1A");
                lines.AddRange(input.Values.Select(value => $"  {value}"));
                continue;
            }

            lines.Add($"\u2022 {input.Label}\uFF1A{FormatDingTalkPlainValue(input.Values)}");
        }

        return lines;
    }

    static List<string> BuildDingTalkMarkdownInputLines(RunRecordView run)
    {
        var lines = new List<string>();
        foreach (var input in BuildDingTalkRunInputs(run))
        {
            if (IsZfi057BusinessAreaPlantMappingLine(input))
            {
                lines.Add($"- {EscapeMarkdownForDingTalk(input.Label)}\uFF1A");
                lines.AddRange(input.Values.Select(value => $"  - {EscapeMarkdownForDingTalk(value)}"));
                continue;
            }

            lines.Add($"- {EscapeMarkdownForDingTalk(input.Label)}\uFF1A{EscapeMarkdownForDingTalk(FormatDingTalkMarkdownValue(input.Values))}");
        }

        return lines;
    }

    static List<string> BuildDingTalkPlainFailedItemLines(RunRecordView run)
    {
        var failed = BuildDingTalkFailedItems(run);
        return failed.Count == 0
            ? new List<string>()
            : new List<string> { $"\u2022 {failed.Label}\uFF1A{FormatDingTalkPlainValue(failed.Values)}" };
    }

    static List<string> BuildDingTalkMarkdownFailedItemLines(RunRecordView run)
    {
        var failed = BuildDingTalkFailedItems(run);
        return failed.Count == 0
            ? new List<string>()
            : new List<string> { $"- **{EscapeMarkdownForDingTalk(failed.Label)}**\uFF1A{EscapeMarkdownForDingTalk(FormatDingTalkMarkdownValue(failed.Values, bold: true))}" };
    }

    static List<DingTalkInputLine> BuildDingTalkRunInputs(RunRecordView run)
    {
        var requestParams = ExtractRunParams(run.RequestJson);
        var result = new List<DingTalkInputLine>();
        string itemLabel = ResolveBatchItemLabelForRun(run);
        var allowedParamKeys = ResolveDingTalkAllowedParamKeys(run);

        foreach (var group in DingTalkParamGroups)
        {
            if (!ShouldShowDingTalkParamGroup(group, allowedParamKeys, itemLabel, run))
                continue;

            var values = CollectDingTalkParamValues(requestParams, group);
            if (group.Label.Equals(itemLabel, StringComparison.OrdinalIgnoreCase))
                AddDistinctValues(values, run.BatchItems.OrderBy(i => i.BatchIndex).Select(i => i.Plant));

            if (run.TransactionCode.Equals("ZFI057", StringComparison.OrdinalIgnoreCase) &&
                group.Label.Equals("\u4E1A\u52A1\u8303\u56F4", StringComparison.OrdinalIgnoreCase))
            {
                values = BuildZfi057DingTalkBusinessAreaMappingValues(run, requestParams, values);
            }

            if (values.Count > 0)
                result.Add(new DingTalkInputLine(group.Label, values));
        }

        if (run.TransactionCode.Equals("ZFI057", StringComparison.OrdinalIgnoreCase))
        {
            var dateWindowValues = BuildZfi057DingTalkDateWindowValues(run, requestParams);
            if (dateWindowValues.Count > 0)
                result.Add(new DingTalkInputLine("ZFI057日期入参", dateWindowValues));
        }

        if (run.TransactionCode.Equals("ZFI072N", StringComparison.OrdinalIgnoreCase) ||
            run.TransactionCode.Equals("ZFI080B", StringComparison.OrdinalIgnoreCase))
        {
            var budatValues = BuildBudatDingTalkDateWindowValues(run, requestParams);
            if (budatValues.Count > 0)
                result.Add(new DingTalkInputLine("过账日期", budatValues));
        }

        foreach (var pair in requestParams.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!IsAllowedDingTalkParam(pair.Key, allowedParamKeys) ||
                IsGroupedDingTalkParam(pair.Key) ||
                IsSensitiveDingTalkParam(pair.Key) ||
                string.IsNullOrWhiteSpace(pair.Value))
                continue;

            result.Add(new DingTalkInputLine(FormatDingTalkParamLabel(pair.Key), NormalizeDingTalkValues(pair.Value, split: ShouldSplitDingTalkParam(pair.Key))));
        }

        return result;
    }

    static bool IsZfi057BusinessAreaPlantMappingLine(DingTalkInputLine input)
    {
        return input.Label.Equals("\u4E1A\u52A1\u8303\u56F4", StringComparison.OrdinalIgnoreCase) &&
               input.Values.Any(value => value.Contains("->\u5DE5\u5382\uFF1A", StringComparison.OrdinalIgnoreCase));
    }

    static List<string> BuildZfi057DingTalkBusinessAreaMappingValues(RunRecordView run, Dictionary<string, string> requestParams, List<string> currentAreas)
    {
        var loggedValues = BuildZfi057DingTalkBusinessAreaMappingValuesFromLogs(run);
        if (loggedValues.Count > 0)
            return loggedValues;

        string businessAreas = string.Join(",", currentAreas.Where(v => !string.IsNullOrWhiteSpace(v)));
        if (string.IsNullOrWhiteSpace(businessAreas))
        {
            businessAreas = FirstNonEmpty(
                GetDictionaryValue(requestParams, "businessAreas", "businessareas", "businessArea", "businessarea", "businessAreaList", "businessareaslist", "gsberlist", "gsber"));
        }

        return NormalizeStringArray(businessAreas)
            .Where(area => !string.IsNullOrWhiteSpace(area))
            .Select(area => $"[{area}]->\u5DE5\u5382\uFF1A\u672A\u89E3\u6790\uFF08GET_GS03\u65E5\u5FD7\u7F3A\u5931\uFF09")
            .ToList();
    }

    static List<string> BuildZfi057DingTalkDateWindowValues(RunRecordView run, Dictionary<string, string> requestParams)
    {
        var loggedValues = BuildZfi057DingTalkDateWindowValuesFromLogs(run);
        if (loggedValues.Count > 0)
            return loggedValues;

        var dateParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string period = GetDictionaryValue(requestParams, "period", "startDate", "dateFrom", "fromDate", "beginDate", "dateBegin");
        string weekEnd = GetDictionaryValue(requestParams, "weekEnd", "week_end", "endDate", "dateTo", "toDate", "dateEnd");
        if (!string.IsNullOrWhiteSpace(period))
            dateParams["period"] = period;
        if (!string.IsNullOrWhiteSpace(weekEnd))
            dateParams["weekEnd"] = weekEnd;
        AddDefaultExecutionDateParams("ZFI057", dateParams);

        var windows = ResolveZfi057Step2DateWindows(GetParamValue(dateParams, "period"), GetParamValue(dateParams, "weekEnd"));
        return FormatZfi057DingTalkDateWindowValues(windows);
    }

    static List<string> BuildZfi057DingTalkDateWindowValuesFromLogs(RunRecordView run)
    {
        var windows = new List<Zfi057Step2DateWindow>();
        foreach (var line in run.Logs)
        {
            string message = line.Message ?? "";
            foreach (Match match in Regex.Matches(message, @"date input group #(\d+);\s*S_KADKY=\[([^~\]]+)~([^\]]+)\];\s*S_KADAT=\[([^~\]]+)~([^\]]+)\]", RegexOptions.IgnoreCase))
            {
                if (!int.TryParse(match.Groups[1].Value, out int index))
                    continue;
                AddZfi057DateWindow(windows, new Zfi057Step2DateWindow(
                    index,
                    match.Groups[2].Value.Trim(),
                    match.Groups[3].Value.Trim(),
                    match.Groups[4].Value.Trim(),
                    match.Groups[5].Value.Trim()));
            }

            foreach (Match match in Regex.Matches(message, @"window(\d+)\.S_KADKY-LOW=([^;]+).*?window\1\.S_KADKY-HIGH=([^;]+).*?window\1\.S_KADAT-LOW=([^;]+).*?window\1\.S_KADAT-HIGH=([^;]+)", RegexOptions.IgnoreCase))
            {
                if (!int.TryParse(match.Groups[1].Value, out int index))
                    continue;
                AddZfi057DateWindow(windows, new Zfi057Step2DateWindow(
                    index,
                    match.Groups[2].Value.Trim(),
                    match.Groups[3].Value.Trim(),
                    match.Groups[4].Value.Trim(),
                    match.Groups[5].Value.Trim()));
            }
        }

        return FormatZfi057DingTalkDateWindowValues(windows);
    }

    static void AddZfi057DateWindow(List<Zfi057Step2DateWindow> windows, Zfi057Step2DateWindow candidate)
    {
        if (windows.Any(window => window.Index == candidate.Index &&
                                  window.KadkyLow.Equals(candidate.KadkyLow, StringComparison.OrdinalIgnoreCase) &&
                                  window.KadkyHigh.Equals(candidate.KadkyHigh, StringComparison.OrdinalIgnoreCase) &&
                                  window.KadatLow.Equals(candidate.KadatLow, StringComparison.OrdinalIgnoreCase) &&
                                  window.KadatHigh.Equals(candidate.KadatHigh, StringComparison.OrdinalIgnoreCase)))
            return;

        windows.Add(candidate);
    }

    static List<string> FormatZfi057DingTalkDateWindowValues(IEnumerable<Zfi057Step2DateWindow> windows)
    {
        var ordered = windows
            .OrderBy(window => window.Index)
            .ToList();
        if (ordered.Count <= 1)
            return new List<string>();

        return ordered
            .Select(window => $"\u7B2C{window.Index}\u6B21\uFF1A\u6210\u672C\u6838\u7B97\u65E5\u671F=[{window.KadkyLow}~{window.KadkyHigh}]\uFF0C\u6210\u672C\u6838\u7B97\u65E5\u671F\u8D77\u4E8E=[{window.KadatLow}~{window.KadatHigh}]")
            .ToList();
    }

    static List<string> BuildBudatDingTalkDateWindowValues(RunRecordView run, Dictionary<string, string> requestParams)
    {
        var loggedValues = BuildBudatDingTalkDateWindowValuesFromLogs(run);
        if (loggedValues.Count > 0)
            return loggedValues;

        var dateParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string period = GetDictionaryValue(requestParams, "period", "startDate", "dateFrom", "fromDate", "beginDate", "dateBegin");
        string weekEnd = GetDictionaryValue(requestParams, "weekEnd", "week_end", "endDate", "dateTo", "toDate", "dateEnd");
        if (!string.IsNullOrWhiteSpace(period))
            dateParams["period"] = period;
        if (!string.IsNullOrWhiteSpace(weekEnd))
            dateParams["weekEnd"] = weekEnd;
        AddDefaultExecutionDateParams(run.TransactionCode, dateParams);

        if (run.TransactionCode.Equals("ZFI072N", StringComparison.OrdinalIgnoreCase))
            return FormatBudatDateWindowValues(ResolveZfi072nBudatDateWindows(GetParamValue(dateParams, "period"), GetParamValue(dateParams, "weekEnd")));

        string low = GetParamValue(dateParams, "period");
        string high = GetParamValue(dateParams, "weekEnd");
        return string.IsNullOrWhiteSpace(low) || string.IsNullOrWhiteSpace(high)
            ? new List<string>()
            : new List<string> { $"{low}~{high}" };
    }

    static List<string> BuildBudatDingTalkDateWindowValuesFromLogs(RunRecordView run)
    {
        var windows = new List<BudatDateWindow>();
        foreach (var line in run.Logs)
        {
            string message = line.Message ?? "";
            foreach (Match match in Regex.Matches(message, @"date input group #(\d+);\s*S_BUDAT=\[([^~\]]+)~([^\]]+)\]", RegexOptions.IgnoreCase))
            {
                if (!int.TryParse(match.Groups[1].Value, out int index))
                    continue;

                AddBudatDateWindow(windows, new BudatDateWindow(
                    index,
                    match.Groups[2].Value.Trim(),
                    match.Groups[3].Value.Trim()));
            }
        }

        return FormatBudatDateWindowValues(windows);
    }

    static List<BudatDateWindow> ResolveZfi072nBudatDateWindows(string period, string weekEnd)
    {
        DateTime defaultStart = StartOfWeek(DateTime.Today).AddDays(-7);
        DateTime defaultEnd = defaultStart.AddDays(6);
        DateTime start = ParseFlexibleDateOrDefault(period, defaultStart);
        DateTime end = ParseFlexibleDateOrDefault(weekEnd, defaultEnd);
        if (end < start)
            return new List<BudatDateWindow>();

        if (start.Year == end.Year && start.Month == end.Month)
        {
            return new List<BudatDateWindow>
            {
                new(1, FormatSapDate(new DateTime(end.Year, end.Month, 1)), FormatSapDate(end))
            };
        }

        return new List<BudatDateWindow>
        {
            new(1, FormatSapDate(new DateTime(start.Year, start.Month, 1)), FormatSapDate(new DateTime(start.Year, start.Month, DateTime.DaysInMonth(start.Year, start.Month)))),
            new(2, FormatSapDate(new DateTime(end.Year, end.Month, 1)), FormatSapDate(end))
        };
    }

    static void AddBudatDateWindow(List<BudatDateWindow> windows, BudatDateWindow candidate)
    {
        if (windows.Any(window => window.Index == candidate.Index &&
                                  window.Low.Equals(candidate.Low, StringComparison.OrdinalIgnoreCase) &&
                                  window.High.Equals(candidate.High, StringComparison.OrdinalIgnoreCase)))
            return;

        windows.Add(candidate);
    }

    static List<string> FormatBudatDateWindowValues(IEnumerable<BudatDateWindow> windows)
    {
        var ordered = windows
            .OrderBy(window => window.Index)
            .Where(window => !string.IsNullOrWhiteSpace(window.Low) && !string.IsNullOrWhiteSpace(window.High))
            .ToList();

        if (ordered.Count == 0)
            return new List<string>();

        if (ordered.Count == 1)
            return new List<string> { $"{ordered[0].Low}~{ordered[0].High}" };

        return ordered
            .Select(window => $"第{window.Index}次：{window.Low}~{window.High}")
            .ToList();
    }

    static List<string> BuildZfi057DingTalkBusinessAreaMappingValuesFromLogs(RunRecordView run)
    {
        var values = new List<string>();
        foreach (var line in run.Logs)
        {
            string message = line.Message ?? "";
            if (!message.Contains("businessArea=", StringComparison.OrdinalIgnoreCase) ||
                !message.Contains("plants=", StringComparison.OrdinalIgnoreCase))
                continue;

            var match = Regex.Match(message, @"\bbusinessArea=([^;]+);\s*plants=([^;\r\n]*)", RegexOptions.IgnoreCase);
            if (!match.Success)
                continue;

            string area = match.Groups[1].Value.Trim();
            string plants = NormalizeCsv(match.Groups[2].Value.Trim());
            if (string.IsNullOrWhiteSpace(area))
                continue;

            string value = $"[{area}]->\u5DE5\u5382\uFF1A{FirstNonEmpty(plants, "\u672A\u89E3\u6790")}";
            if (!values.Any(existing => existing.Equals(value, StringComparison.OrdinalIgnoreCase)))
                values.Add(value);
        }

        return values;
    }

    static string GetDictionaryValue(Dictionary<string, string> values, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        return "";
    }

    static HashSet<string> ResolveDingTalkAllowedParamKeys(RunRecordView run)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] strictKeys = ResolveStrictDingTalkParamKeys(run.TransactionCode);
        string[] configuredKeys = strictKeys.Length > 0 ? strictKeys : LoadTransactionParamKeys(run.TransactionCode);
        foreach (string key in configuredKeys)
            keys.Add(key);

        if (keys.Count == 0)
        {
            foreach (var pair in ExtractRunParams(run.RequestJson))
            {
                if (!IsSensitiveDingTalkParam(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                    keys.Add(pair.Key);
            }
        }

        string itemLabel = ResolveBatchItemLabelForRun(run);
        if (run.BatchItems.Count > 0)
        {
            if (itemLabel.Equals("业务范围", StringComparison.OrdinalIgnoreCase))
            {
                keys.Add("businessAreas");
                keys.Add("businessArea");
            }
            else
            {
                keys.Add("plants");
                keys.Add("plant");
            }
        }

        return keys;
    }

    static string[] ResolveStrictDingTalkParamKeys(string tcode)
    {
        string code = FirstNonEmpty(tcode, "").Trim().ToUpperInvariant();
        if (code is "ZFI057")
            return new[] { "plants", "businessAreas", "period", "weekEnd" };

        if (code is "ZCO020")
            return new[] { "businessAreas", "period", "weekEnd" };

        if (code is "ZFI072N" or "ZFI080B")
            return new[] { "plants" };

        if (code is "ZFI148")
            return new[] { "plants", "period", "weekEnd", "year", "week" };

        if (code is "ZFI072A" or "ZFI085" or "ZFI014D" or "ZFI057" or
            "ZCO020" or "ZPP063" or "ZPP063X" or "ZFI019NC")
        {
            return new[] { "year", "week", "plants" };
        }

        if (code is "ZFI019NI")
            return new[] { "year", "week", "plants", "businessAreas" };

        if (UsesDateRangeOnlyInputs(tcode))
            return new[] { "period", "weekEnd" };

        if (UsesPlantBatchItems(tcode))
            return new[] { "plants", "period", "weekEnd" };

        if (UsesBusinessAreaBatchItems(tcode))
            return new[] { "businessAreas", "period", "weekEnd" };

        return Array.Empty<string>();
    }

    static bool ShouldShowDingTalkParamGroup(DingTalkParamGroup group, HashSet<string> allowedParamKeys, string itemLabel, RunRecordView run)
    {
        if (group.Label.Equals(itemLabel, StringComparison.OrdinalIgnoreCase) && run.BatchItems.Count > 0)
            return true;

        return group.Keys.Any(k => allowedParamKeys.Contains(k));
    }

    static bool IsAllowedDingTalkParam(string key, HashSet<string> allowedParamKeys)
    {
        return allowedParamKeys.Count == 0 || allowedParamKeys.Contains(key);
    }

    static string[] LoadTransactionParamKeys(string tcode)
    {
        if (string.IsNullOrWhiteSpace(tcode))
            return Array.Empty<string>();

        try
        {
            InitializeDatabase(seedFromScripts: true);
            using var connection = OpenDatabaseConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT params_json FROM transactions WHERE tcode=$tcode";
            command.Parameters.AddWithValue("$tcode", tcode.ToUpperInvariant());
            string json = Convert.ToString(command.ExecuteScalar() ?? "") ?? "";
            return SafeJsonArray(json);
        }
        catch (Exception ex)
        {
            Log($"load transaction param keys failed: tcode={tcode}, {ex.Message}");
            return Array.Empty<string>();
        }
    }

    static DingTalkInputLine BuildDingTalkFailedItems(RunRecordView run)
    {
        string itemLabel = ResolveBatchItemLabelForRun(run);
        string failedLabel = itemLabel.Equals("业务范围", StringComparison.OrdinalIgnoreCase) ? "失败业务范围" : "失败工厂";
        var values = run.BatchItems
            .Where(i => IsTerminalRunStatus(i.Status) && !i.Status.Equals("success", StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.BatchIndex)
            .Select(i => i.Plant)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (values.Count == 0 &&
            (run.Status.Equals("failure", StringComparison.OrdinalIgnoreCase) || run.Status.Equals("failed", StringComparison.OrdinalIgnoreCase)))
        {
            var requestParams = ExtractRunParams(run.RequestJson);
            var group = itemLabel.Equals("业务范围", StringComparison.OrdinalIgnoreCase)
                ? DingTalkParamGroups.First(g => g.Label.Equals("业务范围", StringComparison.OrdinalIgnoreCase))
                : DingTalkParamGroups.First(g => g.Label.Equals("工厂", StringComparison.OrdinalIgnoreCase));
            values = CollectDingTalkParamValues(requestParams, group);
        }

        return new DingTalkInputLine(failedLabel, values);
    }

    static Dictionary<string, string> ExtractRunParams(string requestJson)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(requestJson))
            return values;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(requestJson);
            if (doc.RootElement.TryGetProperty("params", out JsonElement paramElement) &&
                paramElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in paramElement.EnumerateObject())
                {
                    string value = JsonValueToString(property.Value);
                    if (!string.IsNullOrWhiteSpace(value))
                        values[property.Name] = value;
                }
            }
        }
        catch
        {
        }

        return values;
    }

    static List<string> CollectDingTalkParamValues(Dictionary<string, string> requestParams, DingTalkParamGroup group)
    {
        var values = new List<string>();
        foreach (string key in group.Keys)
        {
            if (requestParams.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
                AddDistinctValues(values, NormalizeDingTalkValues(value, group.SplitValues));
        }

        return values;
    }

    static List<string> NormalizeDingTalkValues(string value, bool split)
    {
        if (!split)
        {
            string trimmed = CleanDingTalkDisplayText(value);
            return string.IsNullOrWhiteSpace(trimmed) ? new List<string>() : new List<string> { trimmed };
        }

        return NormalizeStringArray(value).Select(CleanDingTalkDisplayText).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
    }

    static void AddDistinctValues(List<string> target, IEnumerable<string> values)
    {
        foreach (string value in values)
        {
            string trimmed = CleanDingTalkDisplayText(value);
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;
            if (!target.Any(v => v.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
                target.Add(trimmed);
        }
    }

    static bool IsGroupedDingTalkParam(string key)
    {
        return DingTalkParamGroups.Any(g => g.Keys.Any(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)));
    }

    static bool IsSensitiveDingTalkParam(string key)
    {
        string value = FirstNonEmpty(key, "").Trim().ToLowerInvariant();
        return value.Contains("password") ||
               value.Equals("pw", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("secret") ||
               value.Contains("token") ||
               value.Contains("appkey") ||
               value.Contains("appsecret") ||
               value.Contains("agentid") ||
               value.Contains("webhook") ||
               value.Equals("system", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("client", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("user", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("sapuser", StringComparison.OrdinalIgnoreCase);
    }

    static bool ShouldSplitDingTalkParam(string key)
    {
        string value = FirstNonEmpty(key, "").Trim().ToLowerInvariant();
        return value.EndsWith("s", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("list", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("codes", StringComparison.OrdinalIgnoreCase);
    }

    static string FormatDingTalkParamLabel(string key)
    {
        return key switch
        {
            "tcode" or "transactionCode" => "事务码",
            _ => key
        };
    }

    static string ResolveBatchItemLabelForRun(RunRecordView run)
    {
        return UsesBusinessAreaBatchItems(run.TransactionCode) ? "业务范围" : "工厂";
    }

    static string FormatDingTalkPlainValue(IReadOnlyList<string> values)
    {
        string text = string.Join("\u3001", values.Where(v => !string.IsNullOrWhiteSpace(v)));
        return Truncate(FirstNonEmpty(text, "\u672A\u8BB0\u5F55"), 240);
    }

    static string FormatDingTalkMarkdownValue(IReadOnlyList<string> values, bool bold = false)
    {
        var normalized = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToArray();
        if (normalized.Length == 0)
            return "\u672A\u8BB0\u5F55";

        string text = string.Join(" ", normalized.Select(v => bold ? $"**[{v}]**" : $"[{v}]"));
        return Truncate(text, 240);
    }

    static string FormatPlantTagsForDingTalk(string plants)
    {
        string[] values = NormalizeStringArray(plants);
        if (values.Length == 0)
            return "\u672A\u6307\u5B9A";

        string text = string.Join(" ", values.Select(p => $"[{p}]"));
        const int maxLength = 120;
        return text.Length <= maxLength ? text : text[..maxLength] + "\u2026";
    }

    static string FormatFailedPlantTagsForDingTalk(RunRecordView run)
    {
        var failedPlants = run.BatchItems
            .Where(i => IsTerminalRunStatus(i.Status) && !i.Status.Equals("success", StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.BatchIndex)
            .Select(i => i.Plant)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (failedPlants.Length > 0)
            return "\u26A0\uFE0F " + string.Join(" ", failedPlants.Select(p => $"**[{p}]**"));

        if (run.Status.Equals("success", StringComparison.OrdinalIgnoreCase))
            return "\u65E0";

        string plants = ExtractRunParamValue(run.RequestJson, "plants");
        string plant = ExtractRunParamValue(run.RequestJson, "plant");
        string fallback = FirstNonEmpty(plants, plant);
        string[] values = NormalizeStringArray(fallback);
        if (values.Length > 0 && (run.Status.Equals("failure", StringComparison.OrdinalIgnoreCase) || run.Status.Equals("failed", StringComparison.OrdinalIgnoreCase)))
            return "\u26A0\uFE0F " + string.Join(" ", values.Select(p => $"**[{p}]**"));

        return "\u672A\u6807\u8BB0";
    }

    static string FormatTransactionDisplay(RunRecordView run)
    {
        string code = FirstNonEmpty(run.TransactionCode, "").Trim();
        string name = FirstNonEmpty(run.TransactionName, "").Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Equals(code, StringComparison.OrdinalIgnoreCase))
            return code;

        return $"{code} - {name}";
    }

    static string ResolveSapDingTalkProvider()
    {
        string provider = FirstNonEmpty(
            Environment.GetEnvironmentVariable("SAP_RPA_DINGTALK_PROVIDER") ?? "",
            Environment.GetEnvironmentVariable("SAP_RPA_DINGTALK_NOTIFY_PROVIDER") ?? "",
            "");

        if (string.IsNullOrWhiteSpace(provider))
            return "direct";

        provider = provider.Trim().ToLowerInvariant();
        return provider switch
        {
            "0" or "false" or "off" or "disabled" or "none" => "none",
            "1" or "true" or "on" or "http" => "http",
            "direct" or "openapi" or "openapi/direct" => "direct",
            "odata" or "o-data" => "direct",
            "rfc" => "rfc",
            "command" or "cmd" => "command",
            _ => provider
        };
    }

    static bool HasVbsSapDingTalkNotifyResult(RunResultRequest result)
    {
        return result.Logs.Any(line =>
            line.Message.StartsWith("NOTIFY_TYPE=", StringComparison.OrdinalIgnoreCase) ||
            line.Message.StartsWith("NOTIFY_MSG=", StringComparison.OrdinalIgnoreCase));
    }

    static string ExtractRunParamValue(string requestJson, string key)
    {
        if (string.IsNullOrWhiteSpace(requestJson))
            return "";

        try
        {
            using JsonDocument doc = JsonDocument.Parse(requestJson);
            if (doc.RootElement.TryGetProperty("params", out JsonElement paramElement) &&
                paramElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in paramElement.EnumerateObject())
                {
                    if (property.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                        return JsonValueToString(property.Value);
                }
            }
        }
        catch
        {
        }

        return "";
    }

    static string FormatDuration(long durationMs)
    {
        if (durationMs <= 0)
            return "";

        var span = TimeSpan.FromMilliseconds(durationMs);
        return span.TotalMinutes >= 1
            ? $"{(int)span.TotalMinutes}分{span.Seconds}秒"
            : $"{Math.Max(1, (int)Math.Round(span.TotalSeconds))}秒";
    }

    static bool IsRunFinishedEvent(string eventName)
    {
        return eventName.Equals("success", StringComparison.OrdinalIgnoreCase) ||
               eventName.Equals("failure", StringComparison.OrdinalIgnoreCase) ||
               eventName.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
               eventName.Equals("finish", StringComparison.OrdinalIgnoreCase);
    }

    static void SendSapDingTalkNotificationByDirectOpenApi(string runId, SapDingTalkNotifyRequest request)
    {
        var config = LoadDingTalkOpenApiConfig();
        if (!config.IsComplete)
        {
            AppendRunLog(runId, "WARN", $"sap dingtalk openapi skipped: missing {config.MissingFieldsSummary} config");
            return;
        }

        string token = FetchDingTalkOpenApiToken(config);
        if (string.IsNullOrWhiteSpace(token))
        {
            AppendRunLog(runId, "WARN", "sap dingtalk openapi failed: token response did not contain token");
            return;
        }

        string url = CombineUrl(config.BaseUrl, "dingtalk-oa/topapi/message/corpconversation/asyncsend_v2") +
                     "?token=" + Uri.EscapeDataString(token);
        var payload = new
        {
            agent_id = config.AgentId,
            userid_list = FirstNonEmpty(request.DingTalkId, DefaultDingTalkId),
            msg = new
            {
                msgtype = "markdown",
                markdown = new
                {
                    title = FirstNonEmpty(request.Message, "SAP\u81EA\u52A8\u5316\u901A\u77E5"),
                    text = FirstNonEmpty(request.MarkdownContent, request.Content)
                }
            }
        };

        var response = PostNotificationJson(url, JsonSerializer.Serialize(payload, JsonOptions));
        string responseText = response.Body;
        var result = ParseDingTalkOpenApiSendResponse(responseText);
        if (response.IsSuccessStatusCode && DingTalkOpenApiSuccess(result))
        {
            AppendRunLog(runId, "INFO", $"sap dingtalk openapi sent: userid={payload.userid_list}, task_id={result.TaskId}, transport={response.Transport}");
            return;
        }

        AppendRunLog(runId, "WARN", $"sap dingtalk openapi failed: transport={response.Transport}, status={response.StatusCode}, errcode={result.ErrCode}, errmsg={Truncate(FirstNonEmpty(result.ErrMsg, responseText), 200)}");
    }

    static DingTalkOpenApiConfig LoadDingTalkOpenApiConfig()
    {
        using JsonDocument? config = LoadLocalConfigDocument();
        JsonElement? dingTalk = TryGetObject(config?.RootElement, "dingTalkOpenApi");
        dingTalk ??= TryGetObject(config?.RootElement, "dingtalkOpenApi");
        dingTalk ??= TryGetObject(config?.RootElement, "dingTalk");
        dingTalk ??= TryGetObject(config?.RootElement, "dingtalk");

        string baseUrl = FirstNonEmpty(
            Environment.GetEnvironmentVariable("SAP_RPA_DINGTALK_OPENAPI_BASE_URL") ?? "",
            Environment.GetEnvironmentVariable("SAP_RPA_DINGTALK_OPENAPI_BASE") ?? "",
            Environment.GetEnvironmentVariable("SAP_RPA_DINGTALK_BASE_URL") ?? "",
            GetConfigString(dingTalk, "baseUrl"),
            GetConfigString(dingTalk, "openApiBaseUrl"),
            GetConfigString(dingTalk, "openapiBaseUrl"),
            GetConfigString(dingTalk, "base_url"));
        string appKey = FirstNonEmpty(
            Environment.GetEnvironmentVariable("SAP_RPA_DINGTALK_OPENAPI_APP_KEY") ?? "",
            Environment.GetEnvironmentVariable("SAP_RPA_DINGTALK_APP_KEY") ?? "",
            GetConfigString(dingTalk, "appKey"),
            GetConfigString(dingTalk, "app_key"));
        string appSecret = FirstNonEmpty(
            Environment.GetEnvironmentVariable("SAP_RPA_DINGTALK_OPENAPI_APP_SECRET") ?? "",
            Environment.GetEnvironmentVariable("SAP_RPA_DINGTALK_OPENAPI_SECRET") ?? "",
            Environment.GetEnvironmentVariable("SAP_RPA_DINGTALK_APP_SECRET") ?? "",
            GetConfigString(dingTalk, "appSecret"),
            GetConfigString(dingTalk, "app_secret"));
        string agentId = FirstNonEmpty(
            Environment.GetEnvironmentVariable("SAP_RPA_DINGTALK_OPENAPI_AGENT_ID") ?? "",
            Environment.GetEnvironmentVariable("SAP_RPA_DINGTALK_OPENAPI_AGENTID") ?? "",
            Environment.GetEnvironmentVariable("SAP_RPA_DINGTALK_AGENT_ID") ?? "",
            GetConfigString(dingTalk, "agentId"),
            GetConfigString(dingTalk, "agent_id"));

        return new DingTalkOpenApiConfig
        {
            BaseUrl = EnsureTrailingSlash(baseUrl),
            AppKey = appKey,
            AppSecret = appSecret,
            AgentId = agentId
        };
    }

    static string FetchDingTalkOpenApiToken(DingTalkOpenApiConfig config)
    {
        string url = CombineUrl(config.BaseUrl, "token");
        string payload = JsonSerializer.Serialize(new
        {
            appKey = config.AppKey,
            appSecret = config.AppSecret
        }, JsonOptions);

        var response = PostNotificationJson(url, payload);
        string responseText = response.Body;
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"token http status {response.StatusCode}, transport={response.Transport}, body={Truncate(responseText, 200)}");

        return ParseDingTalkOpenApiToken(responseText);
    }

    static string ParseDingTalkOpenApiToken(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
            return "";

        using JsonDocument doc = JsonDocument.Parse(responseText);
        JsonElement root = doc.RootElement;
        if (root.TryGetProperty("data", out JsonElement data))
        {
            string token = GetJsonString(data, "token");
            if (!string.IsNullOrWhiteSpace(token))
                return token;
        }

        return FirstNonEmpty(
            GetJsonString(root, "token"),
            GetJsonString(root, "access_token"),
            GetJsonString(root, "accessToken"));
    }

    static DingTalkOpenApiSendResult ParseDingTalkOpenApiSendResponse(string responseText)
    {
        var result = new DingTalkOpenApiSendResult();
        if (string.IsNullOrWhiteSpace(responseText))
            return result;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(responseText);
            JsonElement root = doc.RootElement;
            result.ErrCode = FirstNonEmpty(
                GetJsonString(root, "errcode"),
                GetJsonString(root, "errCode"),
                GetJsonString(root, "code"));
            result.ErrMsg = FirstNonEmpty(
                GetJsonString(root, "errmsg"),
                GetJsonString(root, "errMsg"),
                GetJsonString(root, "message"));
            result.TaskId = GetJsonString(root, "task_id");
            if (string.IsNullOrWhiteSpace(result.TaskId) &&
                root.TryGetProperty("data", out JsonElement data) &&
                data.ValueKind == JsonValueKind.Object)
            {
                result.TaskId = FirstNonEmpty(
                    GetJsonString(data, "task_id"),
                    GetJsonString(data, "taskId"));
            }

            if (string.IsNullOrWhiteSpace(result.ErrCode) &&
                root.TryGetProperty("state", out JsonElement state) &&
                state.ValueKind == JsonValueKind.True)
            {
                result.ErrCode = "0";
            }
        }
        catch
        {
            result.ErrMsg = responseText;
        }

        return result;
    }

    static bool DingTalkOpenApiSuccess(DingTalkOpenApiSendResult result)
    {
        return result.ErrCode.Equals("0", StringComparison.OrdinalIgnoreCase) ||
               result.ErrCode.Equals("OK", StringComparison.OrdinalIgnoreCase);
    }

    static NotificationHttpResult PostNotificationJson(string url, string payload)
    {
        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = NotificationHttpClient.PostAsync(url, content).GetAwaiter().GetResult();
            string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return new NotificationHttpResult((int)response.StatusCode, body, "httpclient");
        }
        catch (Exception ex) when (CanRetryNotificationWithCurl(url))
        {
            return PostNotificationJsonWithCurl(url, payload, ex);
        }
    }

    static bool CanRetryNotificationWithCurl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
               (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) &&
               !string.IsNullOrWhiteSpace(ResolveCurlExePath());
    }

    static string ResolveCurlExePath()
    {
        string systemCurl = Path.Combine(Environment.SystemDirectory, "curl.exe");
        if (File.Exists(systemCurl))
            return systemCurl;

        return "curl.exe";
    }

    static NotificationHttpResult PostNotificationJsonWithCurl(string url, string payload, Exception originalException)
    {
        string curlExe = ResolveCurlExePath();
        string tempFile = Path.Combine(Path.GetTempPath(), $"sap-rpa-http-{Guid.NewGuid():N}.json");
        File.WriteAllText(tempFile, payload, new UTF8Encoding(false));
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = curlExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                psi.ArgumentList.Add("--ssl-no-revoke");
            psi.ArgumentList.Add("--silent");
            psi.ArgumentList.Add("--show-error");
            psi.ArgumentList.Add("--location");
            psi.ArgumentList.Add("--max-time");
            psi.ArgumentList.Add(NotificationWorkerTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--header");
            psi.ArgumentList.Add("Content-Type: application/json");
            psi.ArgumentList.Add("--data-binary");
            psi.ArgumentList.Add("@" + tempFile);
            psi.ArgumentList.Add("--write-out");
            psi.ArgumentList.Add("\nSAP_RPA_HTTP_STATUS:%{http_code}");
            psi.ArgumentList.Add(url);

            using var proc = Process.Start(psi);
            if (proc == null)
                throw new InvalidOperationException($"curl process not started after {originalException.GetType().Name}: {originalException.Message}");

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(TimeSpan.FromSeconds(NotificationWorkerTimeoutSeconds + 3)))
            {
                proc.Kill(entireProcessTree: true);
                throw new TimeoutException($"curl timed out after {NotificationWorkerTimeoutSeconds + 3}s after {originalException.GetType().Name}: {originalException.Message}");
            }

            string stdout = stdoutTask.GetAwaiter().GetResult();
            string stderr = stderrTask.GetAwaiter().GetResult();
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"curl exit={proc.ExitCode}, stderr={Truncate(stderr, 200)} after {originalException.GetType().Name}: {originalException.Message}");

            Match statusMatch = Regex.Match(stdout, @"\r?\nSAP_RPA_HTTP_STATUS:(\d{3})\s*$");
            if (!statusMatch.Success)
                throw new InvalidOperationException($"curl did not return HTTP status, output={Truncate(stdout, 200)} after {originalException.GetType().Name}: {originalException.Message}");

            int status = int.Parse(statusMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            string body = stdout[..statusMatch.Index];
            return new NotificationHttpResult(status, body, "curl");
        }
        finally
        {
            try
            {
                File.Delete(tempFile);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    static JsonDocument? LoadLocalConfigDocument()
    {
        foreach (string path in new[] { RuntimeLocalConfigFilePath, ConfigFilePath })
        {
            try
            {
                if (!File.Exists(path))
                    continue;

                return JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8), new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });
            }
            catch (Exception ex)
            {
                Log($"read local config document failed: {path}, {ex.Message}");
            }
        }

        return null;
    }

    static JsonElement? TryGetObject(JsonElement? root, string property)
    {
        if (!root.HasValue ||
            root.Value.ValueKind != JsonValueKind.Object ||
            !root.Value.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return value;
    }

    static string GetConfigString(JsonElement? item, string property)
    {
        return item.HasValue ? GetJsonString(item.Value, property) : "";
    }

    static string EnsureTrailingSlash(string value)
    {
        value = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value))
            return "";

        return value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/";
    }

    static string CombineUrl(string baseUrl, string relativePath)
    {
        return EnsureTrailingSlash(baseUrl) + relativePath.TrimStart('/');
    }

    static void SendSapDingTalkNotificationByHttp(string runId, string endpoint, SapDingTalkNotifyRequest request)
    {
        string payload = BuildSapDingTalkRequestPayload(request);

        var response = PostNotificationJson(endpoint, payload);
        string responseText = response.Body;
        var sapResult = ParseSapDingTalkNotifyResponse(responseText);
        if (!string.IsNullOrWhiteSpace(sapResult.Message))
            AppendRunLog(runId, "INFO", $"sap dingtalk notify EV_TYPE={sapResult.Type}, EV_MSG={Truncate(sapResult.Message, 200)}");

        if (response.IsSuccessStatusCode && !IsSapErrorType(sapResult.Type))
        {
            AppendRunLog(runId, "INFO", $"sap dingtalk notify sent: IV_WORKNO={request.WorkNo}, IV_DDID={request.DingTalkId}, transport={response.Transport}");
            return;
        }

        AppendRunLog(runId, "WARN", $"sap dingtalk notify failed: transport={response.Transport}, status={response.StatusCode}, EV_TYPE={sapResult.Type}, body={Truncate(responseText, 200)}");
    }

    static void SendSapDingTalkNotificationByRfc(string runId, SapDingTalkNotifyRequest request)
    {
        string command = Environment.GetEnvironmentVariable("SAP_RPA_DINGTALK_RFC_COMMAND") ?? "";
        if (string.IsNullOrWhiteSpace(command))
        {
            AppendRunLog(runId, "INFO", $"sap dingtalk notify skipped: provider=rfc but no RFC command configured, function={request.SapFunction}, IV_DDID={request.DingTalkId}");
            return;
        }

        SendSapDingTalkNotificationByCommand(runId, command, request);
    }

    static string BuildSapDingTalkRequestPayload(SapDingTalkNotifyRequest request)
    {
        var payload = new Dictionary<string, string>
        {
            ["function"] = request.SapFunction,
            ["IV_WORKNO"] = request.WorkNo,
            ["IV_DDID"] = request.DingTalkId,
            ["IV_CONTENT"] = request.Content,
            ["ivWorkno"] = request.WorkNo,
            ["ivDdid"] = request.DingTalkId,
            ["ivContent"] = request.Content,
            ["runId"] = request.RunId,
            ["eventName"] = request.EventName,
            ["status"] = request.Status,
            ["transactionCode"] = request.TransactionCode,
            ["message"] = request.Message
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    static SapFunctionResult ParseSapDingTalkNotifyResponse(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
            return new SapFunctionResult();

        try
        {
            using JsonDocument doc = JsonDocument.Parse(responseText);
            JsonElement root = doc.RootElement;
            return new SapFunctionResult
            {
                Type = FirstNonEmpty(
                    GetJsonString(root, "evType"),
                    GetJsonString(root, "ev_type"),
                    GetJsonString(root, "EV_TYPE"),
                    GetJsonString(root, "type")),
                Message = FirstNonEmpty(
                    GetJsonString(root, "evMsg"),
                    GetJsonString(root, "ev_msg"),
                    GetJsonString(root, "EV_MSG"),
                    GetJsonString(root, "message"),
                    GetJsonString(root, "msg"))
            };
        }
        catch
        {
            string type = FirstNonEmpty(
                Regex.Match(responseText, @"<[^>]*:?Type[^>]*>\s*([^<]+)\s*</[^>]*:?Type>", RegexOptions.IgnoreCase).Groups[1].Value.Trim(),
                Regex.Match(responseText, @"EV_TYPE\s*[=:]\s*([A-Za-z])", RegexOptions.IgnoreCase).Groups[1].Value);
            string message = FirstNonEmpty(
                WebUtility.HtmlDecode(Regex.Match(responseText, @"<[^>]*:?Message[^>]*>\s*([^<]*)\s*</[^>]*:?Message>", RegexOptions.IgnoreCase).Groups[1].Value.Trim()),
                Regex.Match(responseText, @"EV_MSG\s*[=:]\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline).Groups[1].Value.Trim());
            return new SapFunctionResult { Type = type, Message = message };
        }
    }

    static bool IsSapErrorType(string type)
    {
        return type.Equals("E", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("A", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("X", StringComparison.OrdinalIgnoreCase);
    }

    static void SendSapDingTalkNotificationByCommand(string runId, string command, SapDingTalkNotifyRequest request)
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"sap-rpa-dingtalk-{runId}-{Guid.NewGuid():N}.json");
        File.WriteAllText(tempFile, BuildSapDingTalkRequestPayload(request), Encoding.UTF8);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = $"\"{tempFile}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                AppendRunLog(runId, "WARN", "sap dingtalk notify failed: command process not started");
                return;
            }

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(TimeSpan.FromSeconds(NotificationWorkerTimeoutSeconds)))
            {
                proc.Kill(entireProcessTree: true);
                AppendRunLog(runId, "WARN", $"sap dingtalk notify timed out after {NotificationWorkerTimeoutSeconds}s");
                return;
            }

            string stdout = stdoutTask.GetAwaiter().GetResult();
            string stderr = stderrTask.GetAwaiter().GetResult();
            var sapResult = ParseSapDingTalkNotifyResponse(stdout);
            if (!string.IsNullOrWhiteSpace(sapResult.Message))
                AppendRunLog(runId, "INFO", $"sap dingtalk notify EV_TYPE={sapResult.Type}, EV_MSG={Truncate(sapResult.Message, 200)}");

            if (proc.ExitCode == 0 && !IsSapErrorType(sapResult.Type))
                AppendRunLog(runId, "INFO", $"sap dingtalk notify sent: IV_WORKNO={request.WorkNo}, IV_DDID={request.DingTalkId}, IV_CONTENT={Truncate(request.Content, 120)}");
            else
                AppendRunLog(runId, "WARN", $"sap dingtalk notify failed: command exit={proc.ExitCode}, EV_TYPE={sapResult.Type}, stderr={Truncate(stderr, 200)}");
        }
        finally
        {
            try
            {
                File.Delete(tempFile);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    static string BuildNotificationPayload(NotificationTarget target, string title, string text)
    {
        if (target.RobotType.Equals("dingtalk", StringComparison.OrdinalIgnoreCase))
        {
            return JsonSerializer.Serialize(new
            {
                msgtype = "markdown",
                markdown = new { title, text }
            }, JsonOptions);
        }

        return JsonSerializer.Serialize(new { title, text }, JsonOptions);
    }

    static string BuildNotificationUrl(NotificationTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.Secret) ||
            !target.RobotType.Equals("dingtalk", StringComparison.OrdinalIgnoreCase))
        {
            return target.Webhook;
        }

        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string stringToSign = $"{timestamp}\n{target.Secret}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(target.Secret));
        string sign = WebUtility.UrlEncode(Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign))));
        string separator = target.Webhook.Contains('?') ? "&" : "?";
        return $"{target.Webhook}{separator}timestamp={timestamp}&sign={sign}";
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
            "queued" or "running" or "success" or "failed" or "partial_failed" or "canceled" => value,
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

    static string UnprotectSecretIfPresent(string protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
            return "";

        try
        {
            byte[] protectedBytes = Convert.FromBase64String(protectedValue);
            byte[] plainBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (Exception ex)
        {
            Log($"unprotect notification secret failed: {ex.Message}");
            return "";
        }
    }

    static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value ?? "";

        return value[..maxLength] + "...";
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

        var initialProbe = ProbeSapSession(p, StrictSapSessionMatching);
        if (initialProbe.Ready)
        {
            Log($"Detected ready SAP GUI session; skip sapshcut login. {initialProbe.Details}");
        }
        else
        {
            if (initialProbe.HasPendingLoginDialog)
            {
                if (ShouldTakeOverSapMultiLogon(p) && TryResolvePendingSapLoginDialog(p, out string takeOverDetail))
                {
                    ClearSapLoginFailure(p);
                    Log($"SAP login or multi-logon dialog handled before sapshcut login. {takeOverDetail}");
                    Thread.Sleep(1000);
                    initialProbe = ProbeSapSession(p, StrictSapSessionMatching);
                    if (initialProbe.Ready)
                    {
                        Log($"Detected ready SAP GUI session after dialog handling; skip sapshcut login. {initialProbe.Details}");
                    }
                    else
                    {
                        string message = "SAP GUI login dialog was handled but target session is still not ready. " +
                            $"Probe: {initialProbe.Details}; handler={takeOverDetail}";
                        Console.Error.WriteLine(message);
                        Log(message);
                        RememberSapLoginFailure(p, message);
                        return FailedRunResult(message, started);
                    }
                }
                else
                {
                    if (ShouldTakeOverSapMultiLogon(p))
                    {
                        Log($"SAP GUI has a pending login dialog before sapshcut login; cleanup stale local SAP windows and retry login. Probe: {initialProbe.Details}");
                        CleanupSapGuiSessionAfterRun(p);
                        Thread.Sleep(1000);
                        initialProbe = ProbeSapSession(p, StrictSapSessionMatching);
                    }

                    if (!initialProbe.Ready && initialProbe.HasPendingLoginDialog)
                    {
                        string message = "SAP GUI is waiting at a login or multi-logon dialog. " +
                            "Clear the dialog or ensure the scheduled SAP account is not already logged in elsewhere before submitting another queued run. " +
                            "Default policy is takeover; set SAP_RPA_MULTI_LOGON_POLICY=fail or config.local.json multiLogonPolicy=fail to stop instead of terminating other SAP logons. " +
                            $"Probe: {initialProbe.Details}";
                        Console.Error.WriteLine(message);
                        Log(message);
                        RememberSapLoginFailure(p, message);
                        return FailedRunResult(message, started);
                    }
                }
            }

            if (initialProbe.Ready)
            {
                Log($"Detected ready SAP GUI session; skip sapshcut login. {initialProbe.Details}");
            }
            else if (StrictSapSessionMatching && initialProbe.HasBlockingSapGui)
            {
                string message = "SAP GUI has open windows but target login session is not ready. " +
                    "Close SAP login or multi-logon dialogs before submitting another queued run. " +
                    $"Probe: {initialProbe.Details}";
                Console.Error.WriteLine(message);
                Log(message);
                return FailedRunResult(message, started);
            }

            string? sapshcut = FindSapshcut();
            if (string.IsNullOrEmpty(sapshcut))
            {
                Console.Error.WriteLine("未找到 sapshcut.exe，请安装 SAP GUI");
                Log("未找到 sapshcut.exe，请安装 SAP GUI");
                return FailedRunResult("未找到 sapshcut.exe，请安装 SAP GUI", started);
            }

            if (TryGetRecentSapLoginFailure(p, out var recentFailure))
            {
                string message = "SAP login recently failed; skip repeated sapshcut login for this queued item. " +
                    "Clear the SAP Logon/login dialog or fix the SAP connection, then submit again. " +
                    $"Last failure: {recentFailure.Details}";
                Console.Error.WriteLine(message);
                Log(message);
                return FailedRunResult(message, started);
            }

            var loginAttempts = BuildSapLoginAttempts(p);
            if (loginAttempts.Count == 0)
            {
                string message = "SAP login config did not produce any sapshcut launch attempt. " +
                    "Check system/client/user/password and SAP Logon entries.";
                Console.Error.WriteLine(message);
                Log(message);
                RememberSapLoginFailure(p, message);
                return FailedRunResult(message, started);
            }

            SapSessionProbeResult loginProbe = initialProbe;
            string loginDiagnostics = "";
            foreach (var attempt in loginAttempts)
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = sapshcut,
                    UseShellExecute = false,
                    CreateNoWindow = false
                };
                foreach (string arg in attempt.Args)
                    startInfo.ArgumentList.Add(arg);

                Log($"未检测到可用 SAP GUI 会话，启动 SAP GUI: mode={attempt.Name}, path={sapshcut}, args={MaskSapArgs(string.Join(" ", attempt.Args))}");
                try
                {
                    using var sapProcess = Process.Start(startInfo);
                    Log($"SAP GUI launch requested: mode={attempt.Name}, pid={(sapProcess?.Id.ToString(CultureInfo.InvariantCulture) ?? "unknown")}");
                }
                catch (Exception ex)
                {
                    string detail = $"{attempt.Name}: failed to start sapshcut - {ex.Message}";
                    loginDiagnostics = AppendDiagnostic(loginDiagnostics, detail);
                    Log(detail);
                    continue;
                }

                Log($"SAP GUI started; waiting for logged-in scripting session before running VBS. mode={attempt.Name}");
                loginProbe = WaitForReadySapSession(p, StrictSapSessionMatching, TimeSpan.FromSeconds(35), TimeSpan.FromSeconds(2));
                if (loginProbe.Ready)
                {
                    ClearSapLoginFailure(p);
                    Log($"SAP GUI login is ready. mode={attempt.Name}, {loginProbe.Details}");
                    break;
                }

                loginDiagnostics = AppendDiagnostic(loginDiagnostics, $"{attempt.Name}: {loginProbe.Details}; sapguiProcesses={CountProcessByName("sapgui")}, saplogonProcesses={CountProcessByName("saplogon")}");
                if (loginProbe.HasPendingLoginDialog)
                {
                    if (ShouldTakeOverSapMultiLogon(p) && TryResolvePendingSapLoginDialog(p, out string takeOverDetail))
                    {
                        loginDiagnostics = AppendDiagnostic(loginDiagnostics, $"{attempt.Name}: handled pending SAP login dialog: {takeOverDetail}");
                        Log($"SAP login or multi-logon dialog handled. mode={attempt.Name}, {takeOverDetail}");
                        loginProbe = WaitForReadySapSession(p, StrictSapSessionMatching, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(1));
                        if (loginProbe.Ready)
                        {
                            ClearSapLoginFailure(p);
                            Log($"SAP GUI login is ready after dialog handling. mode={attempt.Name}, {loginProbe.Details}");
                        }
                    }

                    if (!loginProbe.Ready)
                    {
                        string detail = "SAP login stopped at login or multi-logon dialog; skip remaining sapshcut fallback attempts.";
                        loginDiagnostics = AppendDiagnostic(loginDiagnostics, detail);
                        Log(detail);
                    }
                    break;
                }
            }

            if (!loginProbe.Ready)
            {
                string message = "SAP login did not produce a ready scripting session. " +
                    "Do not submit more runs until SAP login or multi-logon dialogs are cleared. " +
                    "Default policy is takeover; set SAP_RPA_MULTI_LOGON_POLICY=fail or config.local.json multiLogonPolicy=fail to stop instead of terminating other SAP logons. " +
                    $"Attempts: {loginDiagnostics}";
                Console.Error.WriteLine(message);
                Log(message);
                RememberSapLoginFailure(p, message);
                return FailedRunResult(message, started);
            }

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

    static SapSessionProbeResult WaitForReadySapSession(SapRunParams p, bool strictMatch, TimeSpan timeout, TimeSpan interval)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        SapSessionProbeResult last = ProbeSapSession(p, strictMatch);
        while (!last.Ready && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(interval);
            last = ProbeSapSession(p, strictMatch);
        }

        return last;
    }

    static List<SapLoginAttempt> BuildSapLoginAttempts(SapRunParams p)
    {
        return BuildSapLoginAttempts(p, GetSapLogonIniPaths());
    }

    static List<SapLoginAttempt> BuildSapLoginAttempts(SapRunParams p, IEnumerable<string> sapLogonIniPaths)
    {
        var attempts = new List<SapLoginAttempt>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string name, IEnumerable<string> args)
        {
            var list = args.Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
            string signature = string.Join("\n", list);
            if (list.Count > 0 && seen.Add(signature))
                attempts.Add(new SapLoginAttempt(name, list));
        }

        var common = BuildCommonSapShortcutArgs(p).ToList();
        string system = EscapeArg(p.System);
        if (!string.IsNullOrWhiteSpace(system))
            Add("saplogon-description", new[] { $"-sysname={system}" }.Concat(common));

        foreach (var entry in ReadSapLogonEntries(sapLogonIniPaths))
        {
            if (!SapLogonEntryMatches(entry, p.System))
                continue;

            string sid = FirstNonEmpty(entry.SystemId, entry.Description, p.System);
            string server = entry.Server;
            string sysNr = FirstNonEmpty(NormalizeSapSystemNumber(p.SysNr), entry.SystemNumber);
            if (!string.IsNullOrWhiteSpace(sid) &&
                !string.IsNullOrWhiteSpace(server) &&
                !string.IsNullOrWhiteSpace(sysNr))
            {
                Add("saplogon-direct-guiparm", new[]
                {
                    $"-system={EscapeArg(sid)}",
                    $"-guiparm={EscapeArg(server)} {EscapeArg(sysNr)}"
                }.Concat(common));
            }

            if (!string.IsNullOrWhiteSpace(sid) &&
                !sid.Equals(p.System, StringComparison.OrdinalIgnoreCase))
            {
                Add("saplogon-system-id", new[] { $"-system={EscapeArg(sid)}" }.Concat(common));
            }
        }

        string normalizedConfigSysNr = NormalizeSapSystemNumber(p.SysNr);
        if (!string.IsNullOrWhiteSpace(system) && !string.IsNullOrWhiteSpace(normalizedConfigSysNr))
        {
            Add("configured-system-sysnr", new[]
            {
                $"-system={system}",
                $"-sysnr={EscapeArg(normalizedConfigSysNr)}"
            }.Concat(common));
        }

        return attempts;
    }

    static List<string> BuildCommonSapShortcutArgs(SapRunParams p)
    {
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(p.Client))
            args.Add($"-client={EscapeArg(p.Client)}");
        if (!string.IsNullOrWhiteSpace(p.User))
            args.Add($"-user={EscapeArg(p.User)}");
        if (!string.IsNullOrWhiteSpace(p.Password))
            args.Add($"-pw={EscapeArg(p.Password)}");
        if (!string.IsNullOrWhiteSpace(p.Language))
            args.Add($"-language={EscapeArg(p.Language)}");
        args.Add("-maxgui");
        return args;
    }

    static List<SapLogonEntry> ReadSapLogonEntries()
    {
        return ReadSapLogonEntries(GetSapLogonIniPaths());
    }

    static List<SapLogonEntry> ReadSapLogonEntries(IEnumerable<string> paths)
    {
        var entries = new List<SapLogonEntry>();
        foreach (string path in paths)
        {
            try
            {
                if (!File.Exists(path))
                    continue;

                var values = ReadIniSections(path);
                var descriptions = values.TryGetValue("Description", out var d) ? d : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in descriptions)
                {
                    string key = pair.Key;
                    var entry = new SapLogonEntry
                    {
                        SourcePath = path,
                        ItemKey = key,
                        Description = pair.Value,
                        Server = GetIniValue(values, "Server", key),
                        SystemNumber = NormalizeSapSystemNumber(GetIniValue(values, "Database", key)),
                        SystemId = GetIniValue(values, "MSSysName", key),
                        Router = FirstNonEmpty(GetIniValue(values, "Router", key), GetIniValue(values, "Router2", key))
                    };
                    entries.Add(entry);
                }
            }
            catch (Exception ex)
            {
                Log($"read saplogon.ini failed: {path}, {ex.Message}");
            }
        }

        return entries;
    }

    static IEnumerable<string> GetSapLogonIniPaths()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        yield return Path.Combine(appData, "SAP", "Common", "saplogon.ini");
        yield return Path.Combine(commonAppData, "SAP", "Common", "saplogon.ini");
    }

    static Dictionary<string, Dictionary<string, string>> ReadIniSections(string path)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        string current = "";
        foreach (string rawLine in File.ReadLines(path, Encoding.Default))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal) || line.StartsWith("#", StringComparison.Ordinal))
                continue;

            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                current = line[1..^1].Trim();
                if (!sections.ContainsKey(current))
                    sections[current] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals <= 0 || string.IsNullOrWhiteSpace(current))
                continue;

            string key = line[..equals].Trim();
            string value = line[(equals + 1)..].Trim();
            sections[current][key] = value;
        }

        return sections;
    }

    static string GetIniValue(Dictionary<string, Dictionary<string, string>> sections, string section, string key)
    {
        if (sections.TryGetValue(section, out var values) && values.TryGetValue(key, out string? value))
            return value.Trim();
        return "";
    }

    static bool SapLogonEntryMatches(SapLogonEntry entry, string configuredSystem)
    {
        if (string.IsNullOrWhiteSpace(configuredSystem))
            return false;

        return configuredSystem.Equals(entry.Description, StringComparison.OrdinalIgnoreCase) ||
               configuredSystem.Equals(entry.SystemId, StringComparison.OrdinalIgnoreCase);
    }

    static string BuildSapTargetSystemMatcher(SapRunParams p)
    {
        var values = new List<string>();

        void Add(string value)
        {
            value = (value ?? "").Trim();
            if (value.Length > 0 && !values.Contains(value, StringComparer.OrdinalIgnoreCase))
                values.Add(value);
        }

        Add(p.System);
        foreach (var entry in ReadSapLogonEntries())
        {
            if (!SapLogonEntryMatches(entry, p.System))
                continue;
            Add(entry.SystemId);
            Add(entry.Description);
        }

        return string.Join("|", values);
    }

    static string NormalizeSapSystemNumber(string value)
    {
        value = (value ?? "").Trim();
        if (!Regex.IsMatch(value, @"^\d{1,2}$"))
            return "";

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int sysNr)
            ? sysNr.ToString("00", CultureInfo.InvariantCulture)
            : "";
    }

    static string AppendDiagnostic(string current, string next)
    {
        if (string.IsNullOrWhiteSpace(next))
            return current;
        if (string.IsNullOrWhiteSpace(current))
            return next;
        return current + " | " + next;
    }

    static int CountProcessByName(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName).Length;
        }
        catch
        {
            return -1;
        }
    }

    static bool DetectPendingSapLoginDialog(string details)
    {
        if (string.IsNullOrWhiteSpace(details))
            return false;

        bool loginProgram = details.Contains("program=SAPMSYST", StringComparison.OrdinalIgnoreCase);
        bool loginTransaction = details.Contains("transaction=S000", StringComparison.OrdinalIgnoreCase);
        bool loginScreen = details.Contains("screen=500", StringComparison.OrdinalIgnoreCase);
        bool emptyUser = Regex.IsMatch(details, @"(?i)\buser=([,;]|$)");
        bool targetNotFound = details.Contains("logged-in target SAP session not found", StringComparison.OrdinalIgnoreCase);

        return (loginProgram && loginTransaction) ||
               (loginProgram && loginScreen) ||
               (targetNotFound && emptyUser && (loginTransaction || loginScreen));
    }

    static bool TryResolvePendingSapLoginDialog(SapRunParams p, out string detail)
    {
        string takeoverFile = Path.Combine(Path.GetTempPath(), $"sap_rpa_multilogon_{Guid.NewGuid():N}.vbs");
        string targetSystems = BuildSapTargetSystemMatcher(p);
        string targetClient = p.Client;
        string targetUser = p.User;
        string takeoverScript = $"""
On Error Resume Next
Dim SapGuiAuto, application, connection, session, i, j, k, wnd, diag
Dim targetSystems, targetClient, targetUser, currentSystem, currentClient, currentUser
targetSystems = "{VbsEscape(targetSystems)}"
targetClient = "{VbsEscape(targetClient)}"
targetUser = "{VbsEscape(targetUser)}"
diag = ""

Sub AddDiag(ByVal value)
   If Len(Trim(CStr(value))) = 0 Then Exit Sub
   If Len(diag) > 0 Then diag = diag & " | "
   diag = diag & CStr(value)
End Sub

Sub DumpChildren(ByVal node, ByVal depth)
   On Error Resume Next
   If depth > 2 Then Exit Sub
   Dim idx, child, line
   For idx = 0 To node.Children.Count - 1
      Err.Clear
      Set child = node.Children.Item(CInt(idx))
      If Err.Number = 0 And IsObject(child) Then
         line = "child id=" & child.Id & ", type=" & child.Type
         Err.Clear
         line = line & ", text=" & child.Text
         Err.Clear
         AddDiag line
         DumpChildren child, depth + 1
      End If
   Next
End Sub

Function NodeContainsText(ByVal node, ByVal needle, ByVal depth)
   On Error Resume Next
   NodeContainsText = False
   If Len(Trim(CStr(needle))) = 0 Then
      NodeContainsText = True
      Exit Function
   End If
   If depth > 4 Then Exit Function
   Dim text, idx, child
   text = ""
   Err.Clear
   text = CStr(node.Text)
   If Err.Number = 0 Then
      If InStr(1, UCase(text), UCase(Trim(CStr(needle))), vbTextCompare) > 0 Then
         NodeContainsText = True
         Exit Function
      End If
   End If
   Err.Clear
   For idx = 0 To node.Children.Count - 1
      Err.Clear
      Set child = node.Children.Item(CInt(idx))
      If Err.Number = 0 And IsObject(child) Then
         If NodeContainsText(child, needle, depth + 1) Then
            NodeContainsText = True
            Exit Function
         End If
      End If
   Next
End Function

Function SystemMatches(ByVal value)
   On Error Resume Next
   SystemMatches = False
   If Trim(CStr(targetSystems)) = "" Then Exit Function
   Dim parts, idx, item
   parts = Split(CStr(targetSystems), "|")
   For idx = 0 To UBound(parts)
      item = Trim(CStr(parts(idx)))
      If item <> "" And UCase(Trim(CStr(value))) = UCase(item) Then
         SystemMatches = True
         Exit Function
      End If
   Next
End Function

Function MatchTarget(ByVal candidate)
   On Error Resume Next
   MatchTarget = True
   currentSystem = Trim(CStr(candidate.Info.SystemName))
   currentClient = Trim(CStr(candidate.Info.Client))
   currentUser = Trim(CStr(candidate.Info.User))
   If Not SystemMatches(currentSystem) Then MatchTarget = False
   If Trim(CStr(targetClient)) <> "" And currentClient <> Trim(CStr(targetClient)) Then MatchTarget = False
   If Trim(CStr(targetUser)) <> "" And currentUser <> "" And UCase(currentUser) <> UCase(Trim(CStr(targetUser))) Then MatchTarget = False
   Err.Clear
End Function

Function TryPressTakeover(ByVal candidate)
   On Error Resume Next
   TryPressTakeover = False
   Dim modal, radio, okButton, title, canPress, waited, modalStillOpen, candidateUser
   For k = 1 To 3
      Err.Clear
      Set modal = candidate.findById("wnd[" & k & "]")
      If Err.Number = 0 And IsObject(modal) Then
         title = ""
         Err.Clear
         title = CStr(modal.Text)
         Err.Clear
         AddDiag "modal wnd[" & k & "] title=" & title
         DumpChildren modal, 0

         Err.Clear
         Set radio = candidate.findById("wnd[" & k & "]/usr/radMULTI_LOGON_OPT1")
         If Err.Number = 0 And IsObject(radio) Then
            canPress = True
            If Trim(CStr(targetUser)) <> "" And Trim(CStr(currentUser)) = "" Then
               If Not NodeContainsText(modal, targetUser, 0) Then
                  AddDiag "skip takeover on wnd[" & k & "]: session user is empty and modal text does not contain target user"
                  Err.Clear
                  canPress = False
               End If
            End If
            If canPress Then
               radio.Select
               radio.SetFocus
               AddDiag "selected MULTI_LOGON_OPT1 on wnd[" & k & "]"
               Err.Clear
               Set okButton = candidate.findById("wnd[" & k & "]/tbar[0]/btn[0]")
               If Err.Number = 0 And IsObject(okButton) Then
                  okButton.Press
                  If Err.Number = 0 Then
                     AddDiag "pressed takeover OK button on wnd[" & k & "]"
                  Else
                     AddDiag "press takeover OK button failed on wnd[" & k & "]: " & Err.Description
                     Err.Clear
                  End If
               Else
                  Err.Clear
                  candidate.findById("wnd[" & k & "]").sendVKey 0
                  If Err.Number = 0 Then
                     AddDiag "sent takeover Enter on wnd[" & k & "]"
                  Else
                     AddDiag "send takeover Enter failed on wnd[" & k & "]: " & Err.Description
                     Err.Clear
                  End If
               End If
               WScript.Sleep 500
               Err.Clear
               candidate.findById("wnd[" & k & "]").sendVKey 0
               If Err.Number = 0 Then AddDiag "sent takeover Enter fallback on wnd[" & k & "]"
               Err.Clear

               waited = 0
               Do While waited <= 30000
                  WScript.Sleep 1000
                  waited = waited + 1000
                  Err.Clear
                  candidateUser = Trim(CStr(candidate.Info.User))
                  If Err.Number = 0 And candidateUser <> "" Then
                     AddDiag "takeover confirmed by session user=" & candidateUser & " afterMs=" & waited
                     TryPressTakeover = True
                     Exit Function
                  End If
                  Err.Clear
                  Set modal = candidate.findById("wnd[" & k & "]")
                  modalStillOpen = (Err.Number = 0 And IsObject(modal))
                  Err.Clear
                  If Not modalStillOpen Then
                     AddDiag "takeover dialog closed afterMs=" & waited
                     TryPressTakeover = True
                     Exit Function
                  End If
               Loop

               AddDiag "takeover dialog still open after confirm wait on wnd[" & k & "]"
               Exit Function
            End If
         Else
            AddDiag "MULTI_LOGON_OPT1 not found on wnd[" & k & "]"
            Err.Clear
         End If
      End If
   Next
End Function

Set SapGuiAuto = GetObject("SAPGUI")
If Err.Number <> 0 Or Not IsObject(SapGuiAuto) Then
   WScript.Echo "NO: SAPGUI object not found"
   WScript.Quit 1
End If
Err.Clear
Set application = SapGuiAuto.GetScriptingEngine
If Err.Number <> 0 Or Not IsObject(application) Or application.Children.Count = 0 Then
   WScript.Echo "NO: scripting engine or connection not ready"
   WScript.Quit 2
End If

For i = 0 To application.Children.Count - 1
   Err.Clear
   Set connection = application.Children.Item(CInt(i))
   If Err.Number = 0 And IsObject(connection) Then
      For j = 0 To connection.Children.Count - 1
         Err.Clear
         Set session = connection.Children.Item(CInt(j))
         If Err.Number = 0 And IsObject(session) Then
            AddDiag "session[" & i & "," & j & "].system=" & session.Info.SystemName & ",client=" & session.Info.Client & ",user=" & session.Info.User & ",transaction=" & session.Info.Transaction & ",program=" & session.Info.Program & ",screen=" & session.Info.ScreenNumber
            If MatchTarget(session) Then
               If TryPressTakeover(session) Then
                  WScript.Echo "OK: SAP multi-logon takeover selected; " & diag
                  WScript.Quit 0
               End If
            Else
               AddDiag "skip non-target session[" & i & "," & j & "]"
            End If
         End If
      Next
   End If
Next

WScript.Echo "NO: SAP multi-logon takeover option was not found; " & diag
WScript.Quit 4
""";

        try
        {
            File.WriteAllText(takeoverFile, takeoverScript, Encoding.Default);
            var psi = new ProcessStartInfo(ResolveCscriptPath(), $"//T:60 //nologo \"{takeoverFile}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                detail = "failed to start cscript.exe";
                return false;
            }

            if (!proc.WaitForExit(65_000))
            {
                proc.Kill(entireProcessTree: true);
                detail = "timeout while selecting SAP multi-logon takeover option";
                Log($"SAP multi-logon takeover: {detail}");
                return false;
            }

            string output = proc.StandardOutput.ReadToEnd().Trim();
            string error = proc.StandardError.ReadToEnd().Trim();
            detail = string.Join(" ", new[] { output, error }.Where(x => !string.IsNullOrWhiteSpace(x)));
            Log($"SAP multi-logon takeover: exit={proc.ExitCode}, {detail}");
            return proc.ExitCode == 0 && output.StartsWith("OK:", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            Log($"SAP multi-logon takeover failed: {ex}");
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(takeoverFile))
                    File.Delete(takeoverFile);
            }
            catch { }
        }
    }

    static string SapLoginFailureKey(SapRunParams p)
    {
        return $"{p.System}|{p.Client}|{p.User}".ToUpperInvariant();
    }

    static bool TryGetRecentSapLoginFailure(SapRunParams p, out SapLoginFailure failure)
    {
        lock (SapLoginStateLock)
        {
            if (LastSapLoginFailure is { } last &&
                last.Key.Equals(SapLoginFailureKey(p), StringComparison.OrdinalIgnoreCase) &&
                DateTime.UtcNow - last.AtUtc < SapLoginFailureCooldown)
            {
                failure = last;
                return true;
            }
        }

        failure = default!;
        return false;
    }

    static void RememberSapLoginFailure(SapRunParams p, string details)
    {
        lock (SapLoginStateLock)
        {
            LastSapLoginFailure = new SapLoginFailure(SapLoginFailureKey(p), DateTime.UtcNow, details);
        }
    }

    static void ClearSapLoginFailure(SapRunParams p)
    {
        lock (SapLoginStateLock)
        {
            if (LastSapLoginFailure is { } last &&
                last.Key.Equals(SapLoginFailureKey(p), StringComparison.OrdinalIgnoreCase))
            {
                LastSapLoginFailure = null;
            }
        }
    }

    static SapSessionProbeResult ProbeSapSession(SapRunParams p, bool strictMatch)
    {
        string probeFile = Path.Combine(Path.GetTempPath(), $"sap_rpa_probe_{Guid.NewGuid():N}.vbs");
        string targetSystem = strictMatch ? p.System : "";
        string targetClient = strictMatch ? p.Client : "";
        string targetUser = strictMatch ? p.User : "";
        string probeScript = $"""
On Error Resume Next
Dim SapGuiAuto, application, connection, session, i, j, detail, okcd, foundTarget
Dim targetSystem, targetClient, targetUser, currentSystem, currentClient, currentUser, currentTransaction
targetSystem = "{VbsEscape(targetSystem)}"
targetClient = "{VbsEscape(targetClient)}"
targetUser = "{VbsEscape(targetUser)}"
detail = ""
foundTarget = False
Set SapGuiAuto = GetObject("SAPGUI")
If Err.Number <> 0 Then
   WScript.Echo "NO: SAPGUI object not found; only SAP Logon or a non-scriptable/non-logged-in GUI may be open"
   WScript.Quit 1
End If
Set application = SapGuiAuto.GetScriptingEngine
If Err.Number <> 0 Or Not IsObject(application) Or application.Children.Count = 0 Then
   WScript.Echo "NO: scripting engine or connection not ready"
   WScript.Quit 2
End If
detail = "connections=" & application.Children.Count
Function SessionLooksReady(candidate)
   Dim candidateUser, candidateTransaction, candidateOkcd
   SessionLooksReady = False
   If Not IsObject(candidate) Then Exit Function
   Err.Clear
   candidateUser = Trim(CStr(candidate.Info.User))
   candidateTransaction = UCase(Trim(CStr(candidate.Info.Transaction)))
   If Err.Number <> 0 Then Err.Clear: Exit Function
   If candidateUser = "" Then Exit Function
   If candidateTransaction = "" Then Exit Function
   Err.Clear
   Set candidateOkcd = candidate.findById("wnd[0]/tbar[0]/okcd")
   If Err.Number = 0 And IsObject(candidateOkcd) Then SessionLooksReady = True
   Err.Clear
End Function

For i = 0 To application.Children.Count - 1
   Err.Clear
   Set connection = application.Children.Item(CInt(i))
   If Err.Number = 0 And IsObject(connection) Then
      detail = detail & "; conn[" & i & "].sessions=" & connection.Children.Count
      For j = 0 To connection.Children.Count - 1
         Err.Clear
         Set session = connection.Children.Item(CInt(j))
         If Err.Number = 0 And IsObject(session) Then
            currentSystem = Trim(CStr(session.Info.SystemName))
            currentClient = Trim(CStr(session.Info.Client))
            currentUser = Trim(CStr(session.Info.User))
            currentTransaction = Trim(CStr(session.Info.Transaction))
            detail = detail & "; session[" & i & "," & j & "].system=" & currentSystem & ",client=" & currentClient & ",user=" & currentUser & ",transaction=" & currentTransaction & ",program=" & session.Info.Program & ",screen=" & session.Info.ScreenNumber
            If (Trim(CStr(targetSystem)) = "" Or UCase(currentSystem) = UCase(Trim(CStr(targetSystem)))) And (Trim(CStr(targetClient)) = "" Or currentClient = Trim(CStr(targetClient))) And (Trim(CStr(targetUser)) = "" Or UCase(currentUser) = UCase(Trim(CStr(targetUser)))) Then
               If SessionLooksReady(session) Then
                  foundTarget = True
                  Exit For
               End If
            End If
         End If
      Next
       If IsObject(session) Then
          Err.Clear
          currentSystem = Trim(CStr(session.Info.SystemName))
          currentClient = Trim(CStr(session.Info.Client))
          currentUser = Trim(CStr(session.Info.User))
          If (Trim(CStr(targetSystem)) = "" Or UCase(currentSystem) = UCase(Trim(CStr(targetSystem)))) And (Trim(CStr(targetClient)) = "" Or currentClient = Trim(CStr(targetClient))) And (Trim(CStr(targetUser)) = "" Or UCase(currentUser) = UCase(Trim(CStr(targetUser)))) Then
             If SessionLooksReady(session) Then
                foundTarget = True
                Exit For
             End If
          End If
       End If
   End If
Next
If Err.Number <> 0 Or Not IsObject(session) Or Not CBool(foundTarget) Then
   WScript.Echo "NO: logged-in target SAP session not found; target=" & targetSystem & "/" & targetClient & "/" & targetUser & "; " & detail
   WScript.Quit 4
End If
Err.Clear
Set okcd = session.findById("wnd[0]/tbar[0]/okcd")
If Err.Number <> 0 Or Not IsObject(okcd) Then
   WScript.Echo "NO: command field not ready; " & detail
   WScript.Quit 5
End If
WScript.Echo "OK: system=" & session.Info.SystemName & ", client=" & session.Info.Client & ", user=" & session.Info.User & ", transaction=" & session.Info.Transaction & "; " & detail
WScript.Quit 0
""";

        try
        {
            File.WriteAllText(probeFile, probeScript, Encoding.Default);
            var psi = new ProcessStartInfo(ResolveCscriptPath(), $"//T:8 //nologo \"{probeFile}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
                return new SapSessionProbeResult(false, false, false, false, "failed to start cscript.exe");

            proc.WaitForExit(10_000);
            string output = proc.StandardOutput.ReadToEnd().Trim();
            string error = proc.StandardError.ReadToEnd().Trim();
            string merged = string.Join(" ", new[] { output, error }.Where(x => !string.IsNullOrWhiteSpace(x)));
            Log($"SAP session probe: exit={proc.ExitCode}, {merged}");
            bool hasSapGui = proc.ExitCode != 1 && proc.ExitCode != 2;
            bool hasPendingLoginDialog = DetectPendingSapLoginDialog(merged);
            return new SapSessionProbeResult(
                Ready: proc.ExitCode == 0 && output.StartsWith("OK:", StringComparison.OrdinalIgnoreCase),
                HasSapGui: hasSapGui,
                HasBlockingSapGui: hasSapGui && !merged.Contains("target SAP session not found", StringComparison.OrdinalIgnoreCase),
                HasPendingLoginDialog: hasPendingLoginDialog,
                Details: merged);
        }
        catch (Exception ex)
        {
            Log($"SAP session probe failed: {ex.Message}");
            return new SapSessionProbeResult(false, false, false, false, ex.Message);
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

    static bool HasReadySapSession()
    {
        string probeFile = Path.Combine(Path.GetTempPath(), $"sap_rpa_probe_{Guid.NewGuid():N}.vbs");
        string probeScript = """
On Error Resume Next
Dim SapGuiAuto, application, connection, session, i, j
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
For i = 0 To application.Children.Count - 1
   Err.Clear
   Set connection = application.Children(i)
   If Err.Number = 0 And IsObject(connection) Then
      For j = 0 To connection.Children.Count - 1
         Err.Clear
         Set session = connection.Children(j)
         If Err.Number = 0 And IsObject(session) Then
            If Trim(CStr(session.Info.User)) <> "" Then Exit For
         End If
      Next
      If IsObject(session) And Trim(CStr(session.Info.User)) <> "" Then Exit For
   End If
Next
If Err.Number <> 0 Or Not IsObject(session) Or Trim(CStr(session.Info.User)) = "" Then
   WScript.Echo "NO: logged-in SAP session not ready"
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
            var psi = new ProcessStartInfo(ResolveCscriptPath(), $"//T:8 //nologo \"{probeFile}\"")
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

    static string ResolveCscriptPath()
    {
        string sysWow64Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", "cscript.exe");
        if (File.Exists(sysWow64Path))
            return sysWow64Path;

        string systemPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cscript.exe");
        if (File.Exists(systemPath))
            return systemPath;

        string windowsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "cscript.exe");
        return File.Exists(windowsPath) ? windowsPath : "cscript.exe";
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

    static int ResolveVbsTimeoutSeconds(SapRunParams p, string effectivePlants)
    {
        int configured = p.TimeoutSeconds.GetValueOrDefault(0);
        int fallback = SupportsAlvExport(p.TCode) ? 3900 : 300;
        int timeoutSeconds = configured > 0 ? configured : fallback;

        if (SupportsAlvExport(p.TCode))
        {
            timeoutSeconds = Math.Max(timeoutSeconds, 3900);
            if (NormalizeStringArray(effectivePlants).Contains("9301", StringComparer.OrdinalIgnoreCase))
                timeoutSeconds = Math.Max(timeoutSeconds, 14_400);
        }

        return Math.Clamp(timeoutSeconds, 30, 14_400);
    }

    static RunResultRequest ExecuteViaGuiScripting(SapRunParams p)
    {
        var started = DateTime.UtcNow;
        string template = ReadTransactionScript(p, out string scriptDirectory);
        string effectivePlants = p.Plants;
        string scriptFixedPlants = ExtractScriptMetadataValue(template, "fixedPlants");
        if (!p.TCode.Equals("ZFI072A", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(effectivePlants) &&
            !string.IsNullOrWhiteSpace(scriptFixedPlants))
        {
            effectivePlants = NormalizeCsvPreserveOrder(scriptFixedPlants);
            Log($"script fixedPlants metadata used because request plants are empty: {effectivePlants}");
        }

        string materialPlaceholder = p.Materials;
        string? materialTempFile = null;
        if (p.TCode.Equals("ZFI057", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(p.Materials))
        {
            string[] materialItems = NormalizeStringArray(p.Materials);
            if (materialItems.Length > 0)
            {
                materialTempFile = Path.Combine(Path.GetTempPath(), $"sap_rpa_{p.TCode}_{Guid.NewGuid():N}_materials.txt");
                File.WriteAllText(materialTempFile, string.Join(Environment.NewLine, materialItems), Encoding.Unicode);
                materialPlaceholder = "@file:" + materialTempFile;
                Log($"ZFI057 material list externalized for VBS: file={materialTempFile}, count={materialItems.Length}");
            }
        }

        AlvExportTarget alvExportTarget = BuildAlvExportTarget(p, effectivePlants);
        if (SupportsAlvExport(p.TCode) && string.IsNullOrWhiteSpace(alvExportTarget.FullPath))
            return FailedRunResult($"ALV export path could not be prepared for {p.TCode}, runId={p.RunId}", started);

        string vbsScript = template
            .Replace("{OK_CODE}", VbsEscape(p.TCode))
            .Replace("{SAP_SYSTEM}", VbsEscape(StrictSapSessionMatching ? p.System : ""))
            .Replace("{SAP_CLIENT}", VbsEscape(StrictSapSessionMatching ? p.Client : ""))
            .Replace("{SAP_USER}", VbsEscape(StrictSapSessionMatching ? p.User : ""))
            .Replace("{SCRIPT_MODE}", VbsEscape(p.Script))
            .Replace("{FIELD1_NAME}", VbsEscape(p.Field1Name))
            .Replace("{FIELD1_VALUE}", VbsEscape(p.Field1Value))
            .Replace("{FIELD2_NAME}", VbsEscape(p.Field2Name))
            .Replace("{FIELD2_VALUE}", VbsEscape(p.Field2Value))
            .Replace("{PLANTS}", VbsEscape(effectivePlants))
            .Replace("{BUSINESS_AREAS}", VbsEscape(p.BusinessAreas))
            .Replace("{MATERIALS}", VbsEscape(materialPlaceholder))
            .Replace("{FACTORY_GROUP}", VbsEscape(p.FactoryGroup))
            .Replace("{RUN_STRATEGY}", VbsEscape(p.RunStrategy))
            .Replace("{PARENT_RUN_ID}", VbsEscape(p.ParentRunId))
            .Replace("{PERIOD}", VbsEscape(p.Period))
            .Replace("{YEAR}", VbsEscape(p.Year))
            .Replace("{WEEK}", VbsEscape(p.Week))
            .Replace("{WEEK_END}", VbsEscape(p.WeekEnd))
            .Replace("{ALV_EXPORT_DIR}", VbsEscape(alvExportTarget.Directory))
            .Replace("{ALV_EXPORT_FILENAME}", VbsEscape(alvExportTarget.FileName))
            .Replace("{ALV_EXPORT_PATH}", VbsEscape(alvExportTarget.FullPath))
            .Replace("{CARET_POS}", string.IsNullOrWhiteSpace(p.CaretPos) ? "0" : p.CaretPos)
            .Replace("{BUTTON_ID}", VbsEscape(p.ButtonId))
            .Replace("{SCRIPT_DIR}", VbsEscape(scriptDirectory));

        string tmpFile = Path.Combine(Path.GetTempPath(), $"sap_rpa_{p.TCode}_{Guid.NewGuid():N}.vbs");
        bool keepTempFile = false;
        try
        {
            File.WriteAllText(tmpFile, vbsScript, Encoding.Unicode);
            Log($"执行 VBS: {tmpFile}, tcode={p.TCode}, script={p.Script}, plants={effectivePlants}");

            int timeoutSeconds = ResolveVbsTimeoutSeconds(p, effectivePlants);
            var psi = new ProcessStartInfo(ResolveCscriptPath(), $"//T:{timeoutSeconds} //nologo \"{tmpFile}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null)
                return FailedRunResult("无法启动 cscript.exe 执行 VBS", started);

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(TimeSpan.FromSeconds(timeoutSeconds + 10)))
            {
                keepTempFile = true;
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch (Exception killEx)
                {
                    Log($"VBS 超时后终止失败: {killEx.Message}");
                }

                var timedOut = FailedRunResult($"VBS 执行超过 {timeoutSeconds} 秒，已终止；脚本已保留: {tmpFile}", started);
                timedOut.Logs.Add(new RunLogLine
                {
                    Level = "ERROR",
                    Message = $"VBS timeout after {timeoutSeconds} seconds; temp script retained: {tmpFile}"
                });
                return timedOut;
            }

            string stdOut = stdoutTask.GetAwaiter().GetResult();
            string stdErr = stderrTask.GetAwaiter().GetResult();
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

            var parsed = BuildRunResultFromVbs(stdOut, stdErr, proc?.ExitCode ?? 0, started);
            if (UsesDirectPlantAlvOutput(p.TCode) && parsed.Files.Count > 0)
            {
                string plantForLog = FirstNonEmpty(FirstCsvValue(effectivePlants), p.Plant, p.BusinessArea, p.FactoryGroup, "scope");
                parsed.Files = NormalizeDirectPlantAlvFiles(
                    p.RunId,
                    plantForLog,
                    p.RunId,
                    parsed.Files,
                    parsed.Logs,
                    updateStoredRunFiles: false);
            }
            else if (UsesBusinessAreaAlvOutput(p.TCode) && parsed.Files.Count > 0)
            {
                string businessAreaForLog = FirstNonEmpty(FirstCsvValue(p.BusinessAreas), p.BusinessArea, p.FactoryGroup, "scope");
                NormalizeBusinessAreaAlvFilesForRunResult(
                    parsed,
                    p.RunId,
                    businessAreaForLog,
                    p.RunId,
                    p.TCode);
            }

            if (SupportsAlvExport(p.TCode) &&
                !parsed.Status.Equals("success", StringComparison.OrdinalIgnoreCase))
            {
                keepTempFile = true;
                parsed.Logs.Add(new RunLogLine
                {
                    Level = "ERROR",
                    Message = $"{p.TCode} failed result; temp script retained for ALV export diagnostics: {tmpFile}"
                });
            }

            return parsed;
        }
        finally
        {
            try
            {
                if (!keepTempFile && File.Exists(tmpFile))
                    File.Delete(tmpFile);
                if (!keepTempFile && materialTempFile != null && File.Exists(materialTempFile))
                    File.Delete(materialTempFile);
            }
            catch { }
        }
    }

    static void CleanupSapGuiSessionAfterRun(SapRunParams p)
    {
        string cleanupFile = Path.Combine(Path.GetTempPath(), $"sap_rpa_cleanup_{Guid.NewGuid():N}.vbs");
        string cleanupTargetSystems = BuildSapTargetSystemMatcher(p);
        string cleanupTargetClient = p.Client;
        string cleanupTargetUser = p.User;
        string cleanupScript = $"""
On Error Resume Next
Dim SapGuiAuto, application, connection, session, i, j, k, okcd, wnd, closedCount, attemptedCount, skippedCount
Dim targetSystems, targetClient, targetUser, currentSystem, currentClient, currentUser
targetSystems = "{VbsEscape(cleanupTargetSystems)}"
targetClient = "{VbsEscape(cleanupTargetClient)}"
targetUser = "{VbsEscape(cleanupTargetUser)}"
closedCount = 0
attemptedCount = 0
skippedCount = 0
Set SapGuiAuto = GetObject("SAPGUI")
If Err.Number <> 0 Or Not IsObject(SapGuiAuto) Then
   WScript.Echo "CLEANUP: SAPGUI object not found"
   WScript.Quit 0
End If
Err.Clear
Set application = SapGuiAuto.GetScriptingEngine
If Err.Number <> 0 Or Not IsObject(application) Then
   WScript.Echo "CLEANUP: scripting engine not available"
   WScript.Quit 0
End If
Function SystemMatches(ByVal value)
   On Error Resume Next
   SystemMatches = False
   If Trim(CStr(targetSystems)) = "" Then Exit Function
   Dim parts, idx, item
   parts = Split(CStr(targetSystems), "|")
   For idx = 0 To UBound(parts)
      item = Trim(CStr(parts(idx)))
      If item <> "" And UCase(Trim(CStr(value))) = UCase(item) Then
         SystemMatches = True
         Exit Function
      End If
   Next
End Function
Function IsTargetSession(ByVal candidate)
   On Error Resume Next
   IsTargetSession = True
   currentSystem = Trim(CStr(candidate.Info.SystemName))
   currentClient = Trim(CStr(candidate.Info.Client))
   currentUser = Trim(CStr(candidate.Info.User))
   If Not SystemMatches(currentSystem) Then IsTargetSession = False
   If Trim(CStr(targetClient)) <> "" And currentClient <> Trim(CStr(targetClient)) Then IsTargetSession = False
   If Trim(CStr(targetUser)) <> "" And UCase(currentUser) <> UCase(Trim(CStr(targetUser))) Then IsTargetSession = False
   Err.Clear
End Function
For i = application.Children.Count - 1 To 0 Step -1
   Err.Clear
   Set connection = application.Children.Item(CInt(i))
   If Err.Number = 0 And IsObject(connection) Then
      For j = connection.Children.Count - 1 To 0 Step -1
          Err.Clear
          Set session = connection.Children.Item(CInt(j))
          If Err.Number = 0 And IsObject(session) Then
             currentSystem = Trim(CStr(session.Info.SystemName))
             currentClient = Trim(CStr(session.Info.Client))
             currentUser = Trim(CStr(session.Info.User))
             If Not IsTargetSession(session) Then
                skippedCount = skippedCount + 1
                WScript.Echo "CLEANUP: skip non-target session system=" & currentSystem & ", client=" & currentClient & ", user=" & currentUser & ", transaction=" & session.Info.Transaction
             Else
             attemptedCount = attemptedCount + 1
             Err.Clear
             Set okcd = session.findById("wnd[0]/tbar[0]/okcd")
             If Err.Number = 0 And IsObject(okcd) Then
                WScript.Echo "CLEANUP: closing session system=" & session.Info.SystemName & ", client=" & session.Info.Client & ", user=" & session.Info.User & ", transaction=" & session.Info.Transaction
                okcd.Text = "/nex"
                session.findById("wnd[0]").sendVKey 0
                WScript.Sleep 800
               If Err.Number = 0 Then
                  WScript.Echo "CLEANUP: sent /nex"
                  closedCount = closedCount + 1
               Else
                  WScript.Echo "CLEANUP: failed to send /nex - " & Err.Description
                End If
             Else
                Err.Clear
                For k = 3 To 0 Step -1
                   Err.Clear
                   Set wnd = session.findById("wnd[" & k & "]")
                   If Err.Number = 0 And IsObject(wnd) Then
                      WScript.Echo "CLEANUP: closing SAP window without okcd wnd[" & k & "] title=" & wnd.Text & ", system=" & currentSystem & ", client=" & currentClient & ", user=" & currentUser & ", transaction=" & session.Info.Transaction & ", program=" & session.Info.Program & ", screen=" & session.Info.ScreenNumber
                      wnd.Close
                      WScript.Sleep 500
                      If Err.Number = 0 Then
                         closedCount = closedCount + 1
                         Exit For
                      Else
                         WScript.Echo "CLEANUP: window close failed - " & Err.Description
                         Err.Clear
                      End If
                   End If
                Next
             End If
             End If
          End If
       Next
   End If
Next
WScript.Echo "CLEANUP: completed; attempted=" & attemptedCount & ", closed=" & closedCount & ", skipped=" & skippedCount
WScript.Quit 0
""";

        try
        {
            File.WriteAllText(cleanupFile, cleanupScript, Encoding.Default);
            var psi = new ProcessStartInfo(ResolveCscriptPath(), $"//T:12 //nologo \"{cleanupFile}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                Log($"SAP cleanup after {p.TCode}: failed to start cscript.exe");
                return;
            }

            if (!proc.WaitForExit(15_000))
            {
                proc.Kill(entireProcessTree: true);
                Log($"SAP cleanup after {p.TCode}: timeout");
                return;
            }

            string output = proc.StandardOutput.ReadToEnd().Trim();
            string error = proc.StandardError.ReadToEnd().Trim();
            string merged = string.Join(" ", new[] { output, error }.Where(x => !string.IsNullOrWhiteSpace(x)));
            Log($"SAP cleanup after {p.TCode}: exit={proc.ExitCode}, {merged}");
        }
        catch (Exception ex)
        {
            Log($"SAP cleanup after {p.TCode} failed: {ex.Message}");
        }
        finally
        {
            try
            {
                if (File.Exists(cleanupFile))
                    File.Delete(cleanupFile);
            }
            catch { }
        }
    }

    static string ReadTransactionScript(SapRunParams p)
    {
        return ReadTransactionScript(p, out _);
    }

    static string ReadTransactionScript(SapRunParams p, out string scriptDirectory)
    {
        scriptDirectory = "";
        string? externalScript = FindExternalScript(p.Script, p.TCode);
        if (!string.IsNullOrWhiteSpace(externalScript))
        {
            Log($"加载外部事务码脚本: {externalScript}");
            scriptDirectory = Path.GetDirectoryName(externalScript) ?? "";
            return ReadTextFileWithFallbackEncoding(externalScript);
        }

        if (!p.Script.Equals("openOnly", StringComparison.OrdinalIgnoreCase) &&
            !p.Script.Equals("zck", StringComparison.OrdinalIgnoreCase))
        {
            Log($"未找到外部脚本 {p.Script}，回退到通用模板打开事务码");
        }

        return ReadEmbeddedTemplate("transaction_template.vbs");
    }

    static string ReadTextFileWithFallbackEncoding(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        try
        {
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return strictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Default.GetString(bytes);
        }
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
            bool weekFolderOk = Regex.IsMatch(GetAlvWeekFolderName(new DateTime(2026, 8, 4)), @"^2026_WK\d{2}$", RegexOptions.CultureInvariant);
            Check("ALV output week folder name", weekFolderOk, GetAlvWeekFolderName(new DateTime(2026, 8, 4)));

            string factoryDirectory = GetAlvFactoryOutputDirectory(new DateTime(2026, 7, 28), "6700");
            bool factoryDirectoryOk = Path.GetFileName(factoryDirectory).Equals("2026_WK31_6700", StringComparison.OrdinalIgnoreCase);
            Check("ALV factory output directory name", factoryDirectoryOk, factoryDirectory);

            string plantFileName = BuildAlvDirectPlantFileName("ZFI072N", "\u7EF4\u62A4\u91C7\u8D2D\u4EF7", "6700", new DateTime(2026, 7, 28, 13, 45, 53));
            bool plantFileNameOk = plantFileName.Equals("ZFI072N_\u7EF4\u62A4\u91C7\u8D2D\u4EF7_\u5DE5\u53826700_20260728134553.xlsx", StringComparison.Ordinal);
            Check("ALV direct plant file name", plantFileNameOk, plantFileName);

            bool factoryHeaderAliasesOk =
                IsAlvFactoryHeader("WERKS") &&
                IsAlvFactoryHeader("Plant Code") &&
                IsAlvFactoryHeader("\u5DE5\u5382\u53F7") &&
                IsAlvFactoryHeader("\u5DE5\u5382\u4EE3\u7801") &&
                IsAlvFactoryHeader("\u5927BU-\u5DE5\u5382") &&
                IsAlvFactoryHeader("\u4E1A\u52A1\u8303\u56F4-\u5C0F\u5382") &&
                !IsAlvFactoryHeader("\u4E1A\u52A1\u8303\u56F4");
            Check("ALV factory header aliases", factoryHeaderAliasesOk, "WERKS/Plant Code/factory Chinese headers/business-area export headers");
        }

        {
            string tempRoot = Path.Combine(Path.GetTempPath(), $"sap_rpa_selftest_alv_parts_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempRoot);
            string finalPath = Path.Combine(tempRoot, "ZFI072N_buy_plant6700_20260728134553.xlsx");
            string part1 = Path.Combine(tempRoot, "ZFI072N_buy_plant6700_20260728134553_part1.xlsx");
            string part2 = Path.Combine(tempRoot, "ZFI072N_buy_plant6700_20260728134553_part2.xlsx");
            try
            {
                using (var wb = new XLWorkbook())
                {
                    var ws = wb.Worksheets.Add("ALV");
                    ws.Cell(1, 1).Value = "MATNR";
                    ws.Cell(1, 2).Value = "WERKS";
                    ws.Cell(2, 1).Value = "M1";
                    ws.Cell(2, 2).Value = "6700";
                    wb.SaveAs(part1);
                }

                using (var wb = new XLWorkbook())
                {
                    var ws = wb.Worksheets.Add("ALV");
                    ws.Cell(1, 1).Value = "MATNR";
                    ws.Cell(1, 2).Value = "WERKS";
                    ws.Cell(2, 1).Value = "M2";
                    ws.Cell(2, 2).Value = "6700";
                    wb.SaveAs(part2);
                }

                var logs = new List<RunLogLine>();
                var normalizedFiles = NormalizeDirectPlantAlvFiles(
                    "RUN-SELFTEST-ALV-PARTS",
                    "6700",
                    "RUN-SELFTEST-ALV-PARTS",
                    new List<RunFile>
                    {
                        BuildRunFile(part1),
                        BuildRunFile(part2)
                    },
                    logs,
                    updateStoredRunFiles: false);

                bool baseDetected = TryGetAlvWindowPartBasePath(part1, out string detectedBase) &&
                                    detectedBase.Equals(finalPath, StringComparison.OrdinalIgnoreCase) &&
                                    ExtractAlvWindowPartNumber(part2) == 2;

                using var merged = new XLWorkbook(finalPath);
                var rows = merged.Worksheets.First().RangeUsed()?.RowsUsed().ToList() ?? new List<IXLRangeRow>();
                bool mergedOk = baseDetected &&
                                normalizedFiles.Count == 1 &&
                                normalizedFiles[0].Path.Equals(finalPath, StringComparison.OrdinalIgnoreCase) &&
                                File.Exists(finalPath) &&
                                !File.Exists(part1) &&
                                !File.Exists(part2) &&
                                rows.Count == 3 &&
                                rows[0].Cell(1).GetString().Equals("MATNR", StringComparison.OrdinalIgnoreCase) &&
                                rows[1].Cell(1).GetString().Equals("M1", StringComparison.OrdinalIgnoreCase) &&
                                rows[2].Cell(1).GetString().Equals("M2", StringComparison.OrdinalIgnoreCase) &&
                                merged.Worksheets.Count == 1 &&
                                logs.Any(line => line.Message.Contains("ALV plant parts merged", StringComparison.OrdinalIgnoreCase));
                Check("ALV plant part merge keeps raw rows", mergedOk, $"rows={rows.Count}, files={normalizedFiles.Count}, file={finalPath}");
            }
            finally
            {
                try { Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        }

        {
            string tempRoot = Path.Combine(Path.GetTempPath(), $"sap_rpa_selftest_alv_single_part_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempRoot);
            string finalPath = Path.Combine(tempRoot, "ZFI072N_buy_plant6700_20260728134553.xlsx");
            string part1 = Path.Combine(tempRoot, "ZFI072N_buy_plant6700_20260728134553_part1.xlsx");
            try
            {
                using (var wb = new XLWorkbook())
                {
                    var ws = wb.Worksheets.Add("ALV");
                    ws.Cell(1, 1).Value = "MATNR";
                    ws.Cell(2, 1).Value = "M1";
                    wb.SaveAs(part1);
                }

                var normalizedFiles = NormalizeDirectPlantAlvFiles(
                    "RUN-SELFTEST-ALV-SINGLE-PART",
                    "6700",
                    "RUN-SELFTEST-ALV-SINGLE-PART",
                    new List<RunFile> { BuildRunFile(part1) },
                    new List<RunLogLine>(),
                    updateStoredRunFiles: false);

                bool singleOk = normalizedFiles.Count == 1 &&
                                normalizedFiles[0].Path.Equals(finalPath, StringComparison.OrdinalIgnoreCase) &&
                                File.Exists(finalPath) &&
                                !File.Exists(part1);
                Check("ALV single part normalized to final file", singleOk, $"files={normalizedFiles.Count}, final={finalPath}");
            }
            finally
            {
                try { Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        }

        {
            string tempRoot = Path.Combine(Path.GetTempPath(), $"sap_rpa_selftest_alv_factory_split_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempRoot);
            string rawPath = Path.Combine(tempRoot, "ZFI019NL_raw.xlsx");
            try
            {
                using (var wb = new XLWorkbook())
                {
                    var ws = wb.Worksheets.Add("ALV");
                    ws.Cell(1, 1).Value = "MATNR";
                    ws.Cell(1, 2).Value = "WERKS";
                    ws.Cell(1, 3).Value = "AMOUNT";
                    ws.Cell(2, 1).Value = "M1";
                    ws.Cell(2, 2).Value = "6700";
                    ws.Cell(2, 3).Value = 10;
                    ws.Cell(3, 1).Value = "M2";
                    ws.Cell(3, 2).Value = "6800";
                    ws.Cell(3, 3).Value = 20;
                    ws.Cell(4, 1).Value = "M3";
                    ws.Cell(4, 2).Value = "6700";
                    ws.Cell(4, 3).Value = 30;
                    wb.SaveAs(rawPath);
                }

                var logs = new List<RunLogLine>();
                var splitFiles = SplitAlvWorkbookByFactory(
                    rawPath,
                    "ZFI019NL",
                    "2800",
                    new DateTime(2026, 7, 28, 13, 45, 53),
                    tempRoot,
                    "RUN-SELFTEST-ALV-FACTORY-SPLIT",
                    logs);

                string? file6700 = splitFiles.FirstOrDefault(f => f.Path.Contains("2026_WK31_6700", StringComparison.OrdinalIgnoreCase))?.Path;
                string? file6800 = splitFiles.FirstOrDefault(f => f.Path.Contains("2026_WK31_6800", StringComparison.OrdinalIgnoreCase))?.Path;
                int rows6700 = 0;
                int rows6800 = 0;
                int columns6700 = 0;
                if (!string.IsNullOrWhiteSpace(file6700))
                {
                    using var wb6700 = new XLWorkbook(file6700);
                    var range6700 = wb6700.Worksheets.First().RangeUsed();
                    rows6700 = range6700?.RowsUsed().Count() ?? 0;
                    columns6700 = range6700?.ColumnCount() ?? 0;
                }
                if (!string.IsNullOrWhiteSpace(file6800))
                {
                    using var wb6800 = new XLWorkbook(file6800);
                    rows6800 = wb6800.Worksheets.First().RangeUsed()?.RowsUsed().Count() ?? 0;
                }

                bool ok = splitFiles.Count == 2 &&
                          File.Exists(file6700 ?? "") &&
                          File.Exists(file6800 ?? "") &&
                          rows6700 == 3 &&
                          rows6800 == 2 &&
                          columns6700 == 3 &&
                          splitFiles.All(f => Path.GetFileName(f.Path).StartsWith("ZFI019NL_", StringComparison.OrdinalIgnoreCase));
                Check("ALV business-area workbook split by factory", ok, $"files={splitFiles.Count}, rows6700={rows6700}, rows6800={rows6800}, root={tempRoot}");
            }
            finally
            {
                try { Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        }

        {
            string tempDir = Path.Combine(GetAlvBusinessAreaRawRoot(), $"SELFTEST_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            string rawPath = Path.Combine(tempDir, "ZFI019NL_header_only.xlsx");
            try
            {
                using (var wb = new XLWorkbook())
                {
                    var ws = wb.Worksheets.Add("ALV");
                    ws.Cell(1, 1).Value = "\u6CD5\u4EBA-\u516C\u53F8\u4EE3\u7801";
                    ws.Cell(1, 2).Value = "\u5927BU-\u5DE5\u5382";
                    ws.Cell(1, 3).Value = "\u4E1A\u52A1\u8303\u56F4-\u5C0F\u5382";
                    wb.SaveAs(rawPath);
                }

                var logs = new List<RunLogLine>();
                var normalizedFiles = NormalizeBusinessAreaAlvFiles(
                    "RUN-SELFTEST-ALV-BUSINESS-AREA-NO-DATA",
                    "9200",
                    "RUN-SELFTEST-ALV-BUSINESS-AREA-NO-DATA",
                    "ZFI019NL",
                    new List<RunFile> { BuildRunFile(rawPath) },
                    logs,
                    updateStoredRunFiles: false);

                bool ok = normalizedFiles.Count == 0 &&
                          !File.Exists(rawPath) &&
                          !Directory.Exists(tempDir) &&
                          !Directory.Exists(GetAlvBusinessAreaRawRoot()) &&
                          logs.Any(line => line.Level.Equals("WARN", StringComparison.OrdinalIgnoreCase) &&
                                           line.Message.Contains("no data rows", StringComparison.OrdinalIgnoreCase)) &&
                          logs.Any(line => line.Message.Contains("raw output removed", StringComparison.OrdinalIgnoreCase));
                Check("ALV business-area no-data raw cleanup", ok, $"files={normalizedFiles.Count}, rawExists={File.Exists(rawPath)}, dirExists={Directory.Exists(tempDir)}, rawRootExists={Directory.Exists(GetAlvBusinessAreaRawRoot())}");
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        {
            string tempDir = Path.Combine(GetAlvBusinessAreaRawRoot(), $"SELFTEST_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            string rawPath = Path.Combine(tempDir, "ZFI019NL_missing_factory_value.xlsx");
            try
            {
                using (var wb = new XLWorkbook())
                {
                    var ws = wb.Worksheets.Add("ALV");
                    ws.Cell(1, 1).Value = "MATNR";
                    ws.Cell(1, 2).Value = "\u5927BU-\u5DE5\u5382";
                    ws.Cell(1, 3).Value = "AMOUNT";
                    ws.Cell(2, 1).Value = "M1";
                    ws.Cell(2, 2).Value = "";
                    ws.Cell(2, 3).Value = 10;
                    wb.SaveAs(rawPath);
                }

                bool threw = false;
                try
                {
                    _ = NormalizeBusinessAreaAlvFiles(
                        "RUN-SELFTEST-ALV-BUSINESS-AREA-MISSING-FACTORY",
                        "9200",
                        "RUN-SELFTEST-ALV-BUSINESS-AREA-MISSING-FACTORY",
                        "ZFI019NL",
                        new List<RunFile> { BuildRunFile(rawPath) },
                        new List<RunLogLine>(),
                        updateStoredRunFiles: false);
                }
                catch (InvalidOperationException ex)
                {
                    threw = ex.Message.Contains("no factory values", StringComparison.OrdinalIgnoreCase);
                }

                bool ok = threw && File.Exists(rawPath) && Directory.Exists(tempDir);
                Check("ALV business-area data without factory value fails and keeps raw", ok, $"threw={threw}, rawExists={File.Exists(rawPath)}, dirExists={Directory.Exists(tempDir)}");
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        {
            string tempDir = Path.Combine(GetAlvBusinessAreaRawRoot(), $"SELFTEST_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            string rawPath = Path.Combine(tempDir, "ZFI019NL_missing_factory_value_result.xlsx");
            try
            {
                using (var wb = new XLWorkbook())
                {
                    var ws = wb.Worksheets.Add("ALV");
                    ws.Cell(1, 1).Value = "MATNR";
                    ws.Cell(1, 2).Value = "\u5927BU-\u5DE5\u5382";
                    ws.Cell(1, 3).Value = "AMOUNT";
                    ws.Cell(2, 1).Value = "M1";
                    ws.Cell(2, 2).Value = "";
                    ws.Cell(2, 3).Value = 10;
                    wb.SaveAs(rawPath);
                }

                var result = new RunResultRequest
                {
                    Status = "success",
                    Message = "script executed",
                    Files = { BuildRunFile(rawPath) }
                };

                NormalizeBusinessAreaAlvFilesForRunResult(
                    result,
                    "RUN-SELFTEST-ALV-BUSINESS-AREA-MISSING-FACTORY-RESULT",
                    "9200",
                    "RUN-SELFTEST-ALV-BUSINESS-AREA-MISSING-FACTORY-RESULT",
                    "ZFI019NL");

                bool ok = result.Status.Equals("failed", StringComparison.OrdinalIgnoreCase) &&
                          result.Files.Count == 1 &&
                          Path.GetFullPath(Environment.ExpandEnvironmentVariables(result.Files[0].Path ?? "")).Equals(Path.GetFullPath(rawPath), StringComparison.OrdinalIgnoreCase) &&
                          File.Exists(rawPath) &&
                          result.Logs.Any(line => line.Level.Equals("ERROR", StringComparison.OrdinalIgnoreCase) &&
                                                   line.Message.Contains("raw output kept", StringComparison.OrdinalIgnoreCase));
                Check("ALV business-area split failure keeps raw in run result", ok, $"status={result.Status}, files={result.Files.Count}, rawExists={File.Exists(rawPath)}");
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        {
            var request = new CreateRunRequest
            {
                TransactionCode = "ZFI019NL",
                Params = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["gsberlist"] = "3960, 2910, 3400"
                }
            };
            NormalizeCreateRunParams(request);
            string[] businessAreas = NormalizeStringArray(GetParamValue(request.Params, "businessAreas"));
            var plan = ResolveBatchPlan("ZFI019NL", Array.Empty<string>(), businessAreas);
            var childRequest = plan == null ? request : CloneRunRequestForBatchItem(request, plan, "2910");
            bool ok = plan != null &&
                      plan.ParamKey.Equals("businessAreas", StringComparison.OrdinalIgnoreCase) &&
                      plan.Items.Length == 3 &&
                      GetParamValue(childRequest.Params, "businessAreas").Equals("2910", StringComparison.OrdinalIgnoreCase) &&
                      GetParamValue(childRequest.Params, "businessArea").Equals("2910", StringComparison.OrdinalIgnoreCase);
            Check("ZFI019NL businessAreas batch split", ok, $"areas={GetParamValue(request.Params, "businessAreas")}, child={GetParamValue(childRequest.Params, "businessAreas")}");
        }

        {
            var p = new SapRunParams
            {
                TCode = "ZFI057",
                Plants = "103C",
                Period = "2026.06.29",
                WeekEnd = "2026.07.05"
            };
            var scopes = ResolveZfi057WorkflowScopes(p);
            bool ok = scopes.Count == 0;
            Check("ZFI057 workflow requires businessAreas before GET_GS03", ok, $"scopeCount={scopes.Count}, scopes={string.Join(";", scopes.Select(s => $"{s.BusinessArea}:{string.Join(",", s.Plants)}"))}");
        }

        {
            var request = BuildZfi019NlMemoryRequest(new SapRunParams
            {
                TCode = "ZFI057",
                BusinessAreas = "2800",
                Plants = "9999",
                Period = "2026.06.29",
                WeekEnd = "2026.07.05"
            }, "2800", Array.Empty<string>());
            bool ok = GetSelectionSummary(request, "S_GSBER").Equals("I:EQ:2800:", StringComparison.OrdinalIgnoreCase) &&
                      request.SplitWerks.Count == 0;
            Check("ZFI057 step1 ignores explicit plants and uses businessArea only", ok, $"S_GSBER={GetSelectionSummary(request, "S_GSBER")}, splitWerks={string.Join(",", request.SplitWerks)}");
        }

        {
            string json = "{\"EV_SUBRC\":0,\"RT_SET_VALUES\":[{\"FROM\":\"1011\",\"TITLE\":\"2800\"},{\"FROM\":\"1021\",\"TITLE\":\"2900\"},{\"FROM\":\"103C\",\"TITLE\":\"2800\"}]}";
            var plants = SapGs03PlantFetcher.ExtractPlantsForBusinessAreaForTest(json, "2800");
            bool ok = plants.SequenceEqual(new[] { "1011", "103C" }, StringComparer.OrdinalIgnoreCase);
            Check("ZFI057 GET_GS03 filters FROM by TITLE", ok, $"plants={string.Join(",", plants)}");
        }

        {
            var vbsResult = new RunResultRequest
            {
                Status = "success",
                Logs =
                {
                    new RunLogLine { Level = "INFO", Message = "MATERIAL=MAT001" },
                    new RunLogLine { Level = "INFO", Message = "MATERIALS_CSV=MAT002,MAT001" }
                }
            };
            string materials = ExtractMaterialsFromResult(vbsResult);
            bool ok = materials.Equals("MAT001,MAT002", StringComparison.OrdinalIgnoreCase);
            Check("ZFI057 workflow extracts upstream materials", ok, $"materials={materials}");
        }

        {
            var aggregate = new RunResultRequest();
            var p = new SapRunParams
            {
                RunId = "RUN-SELFTEST-ZFI057-AUDIT",
                Period = "2026.06.29",
                WeekEnd = "2026.07.05"
            };
            AddZfi057MaterialAuditFile(
                aggregate,
                p,
                1,
                "2800",
                new[] { "1011", "1022" },
                "ZFI019NL",
                Array.Empty<string>(),
                new[] { "MAT001", "MAT002" },
                new[] { "MAT001", "MAT002" },
                new[]
                {
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [Zfi019NlMemoryFetcher.FinalMaterialColumn] = "MAT_SPLIT",
                        [Zfi019NlMemoryFetcher.FinalSourceColumn] = "ZFI_SPLIT(BUKRS=2030,WERKS=1022)"
                    }
                });

            string path = aggregate.Files.FirstOrDefault()?.Path ?? "";
            bool exists = File.Exists(path);
            string text = exists ? File.ReadAllText(path, Encoding.UTF8) : "";
            bool hasHash = text.Contains("selectedHash", StringComparison.OrdinalIgnoreCase);
            bool hasSelected = text.Contains("\"selected\",1,\"MAT001\"", StringComparison.OrdinalIgnoreCase);
            bool hasUpstream = text.Contains("\"upstream\",2,\"MAT002\"", StringComparison.OrdinalIgnoreCase);
            bool hasCustomCount = text.Contains("summary,customTableCount,1", StringComparison.OrdinalIgnoreCase);
            bool hasCustomSource = text.Contains("\"MAT_SPLIT\",\"ZFI_SPLIT(BUKRS=2030,WERKS=1022)\"", StringComparison.OrdinalIgnoreCase);
            bool noReportSourceInMaterialSource = !text.Contains("\"MAT001\",\"ZFI019NL\"", StringComparison.OrdinalIgnoreCase);
            bool ok = exists && aggregate.Files.Count == 1 && hasHash && hasSelected && hasUpstream && hasCustomCount && hasCustomSource && noReportSourceInMaterialSource;
            Check("ZFI057 material audit file", ok, $"exists={exists}, files={aggregate.Files.Count}, hash={hasHash}, selected={hasSelected}, upstream={hasUpstream}, customCount={hasCustomCount}, customSource={hasCustomSource}, onlyCustomSource={noReportSourceInMaterialSource}, file={path}");
            try { if (exists) File.Delete(path); } catch { }
        }

        {
            var request = BuildZfi019NlMemoryRequest(new SapRunParams
            {
                Period = "2026.04.27",
                WeekEnd = "2026.05.03"
            }, "2800", Array.Empty<string>());
            bool ok = GetSelectionSummary(request, "S_BUDAT").Equals("I:BT:20260427:20260503", StringComparison.OrdinalIgnoreCase) &&
                      GetSelectionSummary(request, "S_GSBER").Equals("I:EQ:2800:", StringComparison.OrdinalIgnoreCase) &&
                      request.MemoryId.Equals("%ZFI019NA%", StringComparison.OrdinalIgnoreCase) &&
                      request.MemoryName.Equals("GT_ALV", StringComparison.OrdinalIgnoreCase) &&
                      request.SplitWerks.Count == 0;
            Check("ZFI057 step1 memory request", ok, $"S_BUDAT={GetSelectionSummary(request, "S_BUDAT")}, S_GSBER={GetSelectionSummary(request, "S_GSBER")}, splitWerks={string.Join(",", request.SplitWerks)}");
        }

        {
            var noData = new RunResultRequest
            {
                Status = "failed",
                SapStatusType = "E",
                SapStatusText = "没有符合条件数据",
                Message = "SAP status error after execute ZFI057 group #1 - 没有符合条件数据",
                Logs =
                {
                    new RunLogLine { Level = "ERROR", Message = "SAP status error after execute ZFI057 group #1 - 没有符合条件数据" }
                }
            };
            var realFailure = new RunResultRequest
            {
                Status = "failed",
                SapStatusType = "E",
                SapStatusText = "SAP GUI scripting error",
                Message = "open transaction failed"
            };
            bool ok = IsZfi057Step2NoDataResult(noData) && !IsZfi057Step2NoDataResult(realFailure);
            Check("ZFI057 step2 no-data skip detection", ok, $"noData={IsZfi057Step2NoDataResult(noData)}, realFailure={IsZfi057Step2NoDataResult(realFailure)}");

            var aggregate = new RunResultRequest();
            AddSkippedStepResult(aggregate, "step 2 ZFI057 skipped(no-data)", noData);
            bool noErrorLogs = aggregate.Logs.All(line => !line.Level.Equals("ERROR", StringComparison.OrdinalIgnoreCase)) &&
                               string.IsNullOrWhiteSpace(aggregate.SapStatusType) &&
                               string.IsNullOrWhiteSpace(aggregate.SapStatusText);
            Check("ZFI057 no-data skipped result is warning only", noErrorLogs, $"levels={string.Join(",", aggregate.Logs.Select(line => line.Level))}; sapStatusType={aggregate.SapStatusType}; sapStatusText={aggregate.SapStatusText}");
        }

        {
            string stdout = string.Join(Environment.NewLine, new[]
            {
                "INFO: pressed execute ZFI057 group #1",
                "INFO: pressed execute ZFI057 group #2",
                "WARN: execute ZFI057 group #2 returned no data; continuing remaining windows",
                "STATUS_TYPE=W",
                "STATUS_TEXT=ZFI057 completed with partial no-data windows: success=1, noData=1",
                "INFO: transaction script executed"
            });
            var partialNoData = BuildRunResultFromVbs(stdout, "", 0, DateTime.UtcNow);
            bool ok = IsSuccessResult(partialNoData) &&
                      partialNoData.SapStatusType.Equals("W", StringComparison.OrdinalIgnoreCase) &&
                      partialNoData.SapStatusText.Contains("partial no-data", StringComparison.OrdinalIgnoreCase);
            Check("ZFI057 partial window no-data still counts as step2 success", ok, $"status={partialNoData.Status}; sapStatusType={partialNoData.SapStatusType}; sapStatusText={partialNoData.SapStatusText}");
        }

        {
            var completed = new Zfi057TbtcoJobCheckResult(
                true,
                SapJobStatusFetcher.IsTbtcoTerminalStatus("F"),
                SapJobStatusFetcher.IsTbtcoFailureStatus("F"),
                SapJobStatusFetcher.DescribeTbtcoStatus("F"),
                SapJobStatusFetcher.NormalizeTbtcoStatusCategory("F"),
                "",
                new RunResultRequest { Status = "success" });
            var failedCheck = new Zfi057TbtcoJobCheckResult(
                true,
                SapJobStatusFetcher.IsTbtcoTerminalStatus("A"),
                SapJobStatusFetcher.IsTbtcoFailureStatus("A"),
                SapJobStatusFetcher.DescribeTbtcoStatus("A"),
                SapJobStatusFetcher.NormalizeTbtcoStatusCategory("A"),
                "",
                new RunResultRequest { Status = "success" });
            var running = new Zfi057TbtcoJobCheckResult(
                true,
                SapJobStatusFetcher.IsTbtcoTerminalStatus("R"),
                SapJobStatusFetcher.IsTbtcoFailureStatus("R"),
                SapJobStatusFetcher.DescribeTbtcoStatus("R"),
                SapJobStatusFetcher.NormalizeTbtcoStatusCategory("R"),
                "",
                new RunResultRequest { Status = "success" });
            bool ok = completed.Category.Equals("terminal_success", StringComparison.OrdinalIgnoreCase) &&
                      failedCheck.Category.Equals("terminal_failed", StringComparison.OrdinalIgnoreCase) &&
                      running.Category.Equals("running", StringComparison.OrdinalIgnoreCase) &&
                      ShouldRepeatZfi057Step3AfterTbtcoCheck(completed) &&
                      ShouldRepeatZfi057Step3AfterTbtcoCheck(failedCheck) &&
                      !ShouldRepeatZfi057Step3AfterTbtcoCheck(running);
            Check("ZFI057 TBTCO status drives step3 repeat", ok, $"completed={completed.Category}, failed={failedCheck.Category}, running={running.Category}");
        }

        {
            var query = new SapJobStatusQuery
            {
                JobName = "ZFI057",
                JobUser = "IT049",
                LowerUtc = new DateTime(2026, 7, 15, 10, 28, 39, DateTimeKind.Local).ToUniversalTime(),
                UpperUtc = new DateTime(2026, 7, 15, 10, 29, 39, DateTimeKind.Local).ToUniversalTime()
            };
            string sql = SapJobStatusFetcher.BuildTbtcoSqlSummary(query);
            var options = SapJobStatusFetcher.BuildTbtcoWhereOptions(query);
            bool ok = sql.Contains("FROM TBTCO", StringComparison.OrdinalIgnoreCase) &&
                      sql.Contains("JOBNAME = 'ZFI057'", StringComparison.OrdinalIgnoreCase) &&
                      sql.Contains("SDLUNAME = 'IT049'", StringComparison.OrdinalIgnoreCase) &&
                      options.Any(x => x.Contains("SDLSTRTTM >=", StringComparison.OrdinalIgnoreCase)) &&
                      options.Any(x => x.Contains("SDLSTRTTM <=", StringComparison.OrdinalIgnoreCase)) &&
                      options.All(x => x.Length <= 72) &&
                      sql.Contains("SDLSTRTTM <= '102939'", StringComparison.OrdinalIgnoreCase);
            Check("ZFI057 TBTCO query constrains job user and time window", ok, $"{sql}; options={string.Join("|", options)}");
        }

        {
            string guiUser = ResolveZfi057TbtcoJobUser(
                new SapRunParams { User = "GUI_USER" },
                new SapNcoConnectionConfig { User = "NCO_USER" });
            string fallbackUser = ResolveZfi057TbtcoJobUser(
                new SapRunParams(),
                new SapNcoConnectionConfig { User = "NCO_USER" });
            bool ok = guiUser.Equals("GUI_USER", StringComparison.OrdinalIgnoreCase) &&
                      fallbackUser.Equals("NCO_USER", StringComparison.OrdinalIgnoreCase);
            Check("ZFI057 TBTCO job owner prefers SAP GUI user", ok, $"guiUser={guiUser}; fallbackUser={fallbackUser}");
        }

        {
            var step2 = new SapRunParams
            {
                TCode = "ZFI057",
                Script = "ZFI057.vbs",
                Period = "2026.04.27",
                WeekEnd = "2026.05.03",
                Materials = "MAT001,MAT002",
                TimeoutSeconds = 1800
            };
            string summary = BuildZfi057Step2InputSummary(step2, "2800", "1011,1022", 1, 1, 536);
            bool ok = summary.Contains("plantCount=2", StringComparison.OrdinalIgnoreCase) &&
                      summary.Contains("plants=1011,1022", StringComparison.OrdinalIgnoreCase) &&
                      summary.Contains("S_WERKS.mode=LOW seed + multiSelection", StringComparison.OrdinalIgnoreCase) &&
                      summary.Contains("S_WERKS-LOW.seed=1011", StringComparison.OrdinalIgnoreCase) &&
                      summary.Contains("S_WERKS.items=1011,1022", StringComparison.OrdinalIgnoreCase) &&
                      summary.Contains("S_KADKY-LOW=2026.03.01", StringComparison.OrdinalIgnoreCase) &&
                      summary.Contains("S_KADKY-HIGH=2026.04.30", StringComparison.OrdinalIgnoreCase) &&
                      summary.Contains("S_KADAT-LOW=2026.03.02", StringComparison.OrdinalIgnoreCase) &&
                      summary.Contains("S_KADAT-HIGH=2026.04.30", StringComparison.OrdinalIgnoreCase) &&
                      summary.Contains("window2.S_KADKY-LOW=2026.05.01", StringComparison.OrdinalIgnoreCase) &&
                      summary.Contains("window2.S_KADAT-LOW=2026.05.02", StringComparison.OrdinalIgnoreCase) &&
                      summary.Contains("materialCount=536", StringComparison.OrdinalIgnoreCase) &&
                      summary.Contains("materials=omitted", StringComparison.OrdinalIgnoreCase) &&
                      !summary.Contains("MAT001", StringComparison.OrdinalIgnoreCase) &&
                      !summary.Contains("MAT002", StringComparison.OrdinalIgnoreCase);
            Check("ZFI057 step2 input summary uses multi-select and omits materials", ok, summary);
        }

        {
            var sameMonthWindows = ResolveZfi057Step2DateWindows("2026.05.04", "2026.05.10");
            bool ok = sameMonthWindows.Count == 1 &&
                      sameMonthWindows[0].KadkyLow.Equals("2026.05.01", StringComparison.OrdinalIgnoreCase) &&
                      sameMonthWindows[0].KadatLow.Equals("2026.05.02", StringComparison.OrdinalIgnoreCase);
            Check("ZFI057 S_KADAT low starts from day 2", ok, string.Join(" | ", sameMonthWindows.Select(w => $"{w.Index}:{w.KadkyLow}~{w.KadkyHigh}/{w.KadatLow}~{w.KadatHigh}")));
        }

        {
            var logs = SanitizeZfi057DiagnosticLogs(new[]
            {
                new RunLogLine { Level = "INFO", Message = "ZFI019NL memory fetch materials: count=2; sample=MAT001,MAT002; hash=ABCDEF123456" },
                new RunLogLine { Level = "INFO", Message = "ZFI019NL memory fetch counts: rawLines=1; finalRows=2" }
            }).ToArray();
            string joined = string.Join(Environment.NewLine, logs.Select(line => line.Message));
            bool ok = joined.Contains("sample=omitted", StringComparison.OrdinalIgnoreCase) &&
                      joined.Contains("hash=omitted", StringComparison.OrdinalIgnoreCase) &&
                      !joined.Contains("MAT001", StringComparison.OrdinalIgnoreCase) &&
                      !joined.Contains("ABCDEF123456", StringComparison.OrdinalIgnoreCase);
            Check("ZFI057 diagnostic log omits material samples", ok, joined);
        }

        {
            string ini = Path.Combine(Path.GetTempPath(), $"sap_rpa_selftest_saplogon_nco_{Guid.NewGuid():N}.ini");
            File.WriteAllText(ini, """
[Server]
Item1=10.0.40.212
[Database]
Item1=10
[MSSysName]
Item1=TD1
[Description]
Item1=test888
""", Encoding.ASCII);
            try
            {
                var entries = ReadSapLogonEntries(new[] { ini });
                var p = new SapRunParams
                {
                    System = "test888",
                    Client = "888",
                    User = "IT049",
                    Password = "SECRET",
                    Language = "ZH",
                    SysNr = "10000000000000000000000"
                };
                var matched = entries.FirstOrDefault(e => SapLogonEntryMatches(e, p.System));
                string ipAddress = FirstNonEmpty(matched?.Server ?? "");
                string systemNumber = NormalizeSapSystemNumber(FirstNonEmpty(matched?.SystemNumber ?? "", p.SysNr));
                string systemId = FirstNonEmpty(matched?.SystemId ?? "", p.System);
                bool ok = ipAddress.Equals("10.0.40.212", StringComparison.OrdinalIgnoreCase) &&
                          systemNumber.Equals("10", StringComparison.OrdinalIgnoreCase) &&
                          systemId.Equals("TD1", StringComparison.OrdinalIgnoreCase);
                Check("SAP Logon supplies NCo target", ok, $"ipAddress={ipAddress}, systemNumber={systemNumber}, systemId={systemId}");
            }
            finally
            {
                try { File.Delete(ini); } catch { }
            }
        }

        {
            var request = new CreateRunRequest
            {
                TransactionCode = "ZFI080",
                Params = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["plants"] = "1022,1024,1032"
                }
            };
            NormalizeCreateRunParams(request);
            string[] plants = NormalizeStringArray(GetParamValue(request.Params, "plants"));
            var plan = ResolveBatchPlan("ZFI080", plants, Array.Empty<string>());
            var childRequest = plan == null ? request : CloneRunRequestForBatchItem(request, plan, "1024");
            bool ok = plan != null &&
                      plan.ParamKey.Equals("plants", StringComparison.OrdinalIgnoreCase) &&
                      plan.Items.Length == 3 &&
                      GetParamValue(childRequest.Params, "plants").Equals("1024", StringComparison.OrdinalIgnoreCase) &&
                      GetParamValue(childRequest.Params, "plant").Equals("1024", StringComparison.OrdinalIgnoreCase);
            Check("ZFI080 plants batch split", ok, $"plants={GetParamValue(request.Params, "plants")}, child={GetParamValue(childRequest.Params, "plants")}");
        }

        {
            var request = new CreateRunRequest
            {
                TransactionCode = "ZFI072N",
                Params = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["plants"] = "1022,1032",
                    ["period"] = "2026.02.23",
                    ["weekEnd"] = "2026.03.01"
                }
            };
            NormalizeCreateRunParams(request);
            string[] plants = NormalizeStringArray(GetParamValue(request.Params, "plants"));
            var plan = ResolveBatchPlan("ZFI072N", plants, Array.Empty<string>());
            var childRequest = plan == null ? request : CloneRunRequestForBatchItem(request, plan, "1032");
            var windows = ResolveZfi072nBudatDateWindows(GetParamValue(request.Params, "period"), GetParamValue(request.Params, "weekEnd"));
            var invalidWindows = ResolveZfi072nBudatDateWindows("2026.03.01", "2026.02.23");
            bool ok = plan != null &&
                      plan.ParamKey.Equals("plants", StringComparison.OrdinalIgnoreCase) &&
                      plan.Items.SequenceEqual(new[] { "1022", "1032" }, StringComparer.OrdinalIgnoreCase) &&
                      GetParamValue(childRequest.Params, "plants").Equals("1032", StringComparison.OrdinalIgnoreCase) &&
                      GetParamValue(childRequest.Params, "period").Equals("2026.02.23", StringComparison.OrdinalIgnoreCase) &&
                      windows.Count == 2 &&
                      windows[0].Low.Equals("2026.02.01", StringComparison.OrdinalIgnoreCase) &&
                      windows[0].High.Equals("2026.02.28", StringComparison.OrdinalIgnoreCase) &&
                      windows[1].Low.Equals("2026.03.01", StringComparison.OrdinalIgnoreCase) &&
                      windows[1].High.Equals("2026.03.01", StringComparison.OrdinalIgnoreCase) &&
                      invalidWindows.Count == 0;
            Check("ZFI072N plants batch split and cross-month BUDAT windows", ok, $"plants={GetParamValue(request.Params, "plants")}, child={GetParamValue(childRequest.Params, "plants")}, windows={string.Join("|", windows.Select(w => $"{w.Index}:{w.Low}~{w.High}"))}");
        }

        {
            var request = new CreateRunRequest
            {
                TransactionCode = "ZFI080B",
                Params = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["plants"] = "1022,1032"
                }
            };
            NormalizeCreateRunParams(request);
            DateTime defaultStart = StartOfWeek(DateTime.Today).AddDays(-7);
            DateTime defaultEnd = defaultStart.AddDays(6);
            string[] plants = NormalizeStringArray(GetParamValue(request.Params, "plants"));
            var plan = ResolveBatchPlan("ZFI080B", plants, Array.Empty<string>());
            bool ok = plan != null &&
                      plan.Items.SequenceEqual(new[] { "1022", "1032" }, StringComparer.OrdinalIgnoreCase) &&
                      GetParamValue(request.Params, "period").Equals(FormatSapDate(defaultStart), StringComparison.OrdinalIgnoreCase) &&
                      GetParamValue(request.Params, "weekEnd").Equals(FormatSapDate(defaultEnd), StringComparison.OrdinalIgnoreCase);
            Check("ZFI080B plants batch split and weekly BUDAT default", ok, $"period={GetParamValue(request.Params, "period")}, weekEnd={GetParamValue(request.Params, "weekEnd")}, plants={GetParamValue(request.Params, "plants")}");
        }

        {
            var request = new CreateRunRequest
            {
                TransactionCode = "ZFI148",
                Params = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["plants"] = "1022,1032"
                }
            };
            NormalizeCreateRunParams(request);
            DateTime defaultStart = StartOfWeek(DateTime.Today).AddDays(-7);
            DateTime defaultEnd = defaultStart.AddDays(6);
            string[] plants = NormalizeStringArray(GetParamValue(request.Params, "plants"));
            var plan = ResolveBatchPlan("ZFI148", plants, Array.Empty<string>());
            var childRequest = plan == null ? request : CloneRunRequestForBatchItem(request, plan, "1032");
            bool ok = plan != null &&
                      plan.Items.SequenceEqual(new[] { "1022", "1032" }, StringComparer.OrdinalIgnoreCase) &&
                      GetParamValue(childRequest.Params, "plants").Equals("1032", StringComparison.OrdinalIgnoreCase) &&
                      GetParamValue(request.Params, "period").Equals(FormatSapDate(defaultStart), StringComparison.OrdinalIgnoreCase) &&
                      GetParamValue(request.Params, "weekEnd").Equals(FormatSapDate(defaultEnd), StringComparison.OrdinalIgnoreCase) &&
                      GetParamValue(request.Params, "year").Equals(defaultStart.Year.ToString(), StringComparison.OrdinalIgnoreCase) &&
                      GetParamValue(request.Params, "week").Equals(ISOWeek.GetWeekOfYear(defaultStart).ToString(), StringComparison.OrdinalIgnoreCase);
            Check("ZFI148 plants batch split and weekly year/week default", ok, $"period={GetParamValue(request.Params, "period")}, weekEnd={GetParamValue(request.Params, "weekEnd")}, year={GetParamValue(request.Params, "year")}, week={GetParamValue(request.Params, "week")}, child={GetParamValue(childRequest.Params, "plants")}");
        }

        {
            var request = new CreateRunRequest
            {
                TransactionCode = "ZFIR034",
                Params = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["plants"] = "1022,1024",
                    ["businessAreas"] = "2900,9200"
                }
            };
            NormalizeCreateRunParams(request);
            DateTime defaultStart = StartOfWeek(DateTime.Today).AddDays(-7);
            DateTime defaultEnd = defaultStart.AddDays(6);
            string[] plants = NormalizeStringArray(GetParamValue(request.Params, "plants"));
            string[] businessAreas = NormalizeStringArray(GetParamValue(request.Params, "businessAreas"));
            var plan = ResolveBatchPlan("ZFIR034", plants, businessAreas);
            bool ok = plan == null &&
                      string.IsNullOrWhiteSpace(GetParamValue(request.Params, "plants")) &&
                      string.IsNullOrWhiteSpace(GetParamValue(request.Params, "businessAreas")) &&
                      GetParamValue(request.Params, "period").Equals(FormatSapDate(defaultStart), StringComparison.OrdinalIgnoreCase) &&
                      GetParamValue(request.Params, "weekEnd").Equals(FormatSapDate(defaultEnd), StringComparison.OrdinalIgnoreCase);
            Check("ZFIR034 date range defaults without scope", ok, $"period={GetParamValue(request.Params, "period")}, weekEnd={GetParamValue(request.Params, "weekEnd")}, plants={GetParamValue(request.Params, "plants")}, businessAreas={GetParamValue(request.Params, "businessAreas")}");
        }

        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["period"] = "2025.12.29",
                ["weekEnd"] = "2026.01.04"
            };
            AddDefaultExecutionDateParams("ZFI072A", values);
            bool ok = GetParamValue(values, "year").Equals("2025", StringComparison.OrdinalIgnoreCase);
            Check("weekly year uses range low", ok, $"period={GetParamValue(values, "period")}, weekEnd={GetParamValue(values, "weekEnd")}, year={GetParamValue(values, "year")}, week={GetParamValue(values, "week")}");
        }

        {
            var run = new RunRecordView
            {
                RunId = "RUN-SELFTEST-ZFI019NL",
                TransactionCode = "ZFI019NL",
                TransactionName = "业务范围测试",
                Status = "partial_failed",
                RequestJson = "{\"transactionCode\":\"ZFI019NL\",\"params\":{\"businessAreas\":\"3960,2910,3400\",\"businessArea\":\"3960\",\"gsberlist\":\"3960,2910,3400\",\"plants\":\"1024,1032\",\"period\":\"2026.06.15\",\"weekEnd\":\"2026.06.21\",\"factoryGroup\":\"平湖九厂\",\"appSecret\":\"SHOULD_NOT_APPEAR\",\"password\":\"SHOULD_NOT_APPEAR\"}}",
                SapStatusType = "S",
                SapStatusText = "自动化已跑完",
                StartedAt = "2026-06-26 10:00:00",
                FinishedAt = "2026-06-26 10:02:00",
                DurationMs = 120000
            };
            run.BatchItems.Add(new BatchItemStatus { Plant = "3960", BatchIndex = 1, Status = "success" });
            run.BatchItems.Add(new BatchItemStatus { Plant = "2910", BatchIndex = 2, Status = "failed" });
            run.BatchItems.Add(new BatchItemStatus { Plant = "3400", BatchIndex = 3, Status = "success" });
            string markdown = BuildSapDingTalkMarkdownContent(run, "自动化已跑完");
            string plain = BuildSapDingTalkContent(run, "自动化已跑完");
            var inputs = BuildDingTalkRunInputs(run);
            var checks = new Dictionary<string, bool>
            {
                ["has businessAreas"] = markdown.Contains("业务范围", StringComparison.OrdinalIgnoreCase),
                ["has 3960"] = markdown.Contains("[3960]", StringComparison.OrdinalIgnoreCase),
                ["has 2910"] = markdown.Contains("[2910]", StringComparison.OrdinalIgnoreCase),
                ["has 3400"] = markdown.Contains("[3400]", StringComparison.OrdinalIgnoreCase),
                ["has failed label"] = markdown.Contains("失败业务范围", StringComparison.OrdinalIgnoreCase),
                ["has failed 2910"] = markdown.Contains("**[2910]**", StringComparison.OrdinalIgnoreCase),
                ["has period"] = markdown.Contains("2026.06.15", StringComparison.OrdinalIgnoreCase),
                ["has weekEnd"] = markdown.Contains("2026.06.21", StringComparison.OrdinalIgnoreCase),
                ["unused plants hidden"] = !markdown.Contains("工厂", StringComparison.OrdinalIgnoreCase) &&
                                           !markdown.Contains("[1024]", StringComparison.OrdinalIgnoreCase) &&
                                           !markdown.Contains("[1032]", StringComparison.OrdinalIgnoreCase),
                ["no old plant label"] = !markdown.Contains("本次工厂", StringComparison.OrdinalIgnoreCase),
                ["no unspecified markdown"] = !markdown.Contains("未指定", StringComparison.OrdinalIgnoreCase),
                ["no unspecified plain"] = !plain.Contains("未指定", StringComparison.OrdinalIgnoreCase),
                ["unused year hidden"] = !inputs.Any(i => i.Label.Equals("年度", StringComparison.OrdinalIgnoreCase)),
                ["markdown hides secret"] = !markdown.Contains("SHOULD_NOT_APPEAR", StringComparison.OrdinalIgnoreCase),
                ["plain hides secret"] = !plain.Contains("SHOULD_NOT_APPEAR", StringComparison.OrdinalIgnoreCase)
            };
            bool ok = checks.Values.All(v => v);
            string detail = ok
                ? Truncate(markdown.Replace("\n", " | "), 240)
                : string.Join(", ", checks.Where(c => !c.Value).Select(c => c.Key));
            Check("DingTalk inputs for businessAreas", ok, detail);
        }

        {
            var run = new RunRecordView
            {
                RunId = "RUN-SELFTEST-ZFI072A",
                TransactionCode = "ZFI072A",
                TransactionName = "采购价月表",
                Status = "success",
                RequestJson = "{\"transactionCode\":\"ZFI072A\",\"params\":{\"plants\":\"5021,9301\",\"plant\":\"5021\",\"businessAreas\":\"2900,9200\",\"year\":\"2026\",\"week\":\"25\",\"period\":\"2026.06.15\",\"weekEnd\":\"2026.06.21\",\"token\":\"SHOULD_NOT_APPEAR\"}}",
                SapStatusType = "S",
                SapStatusText = "自动化已跑完",
                StartedAt = "2026-06-26 10:00:00",
                FinishedAt = "2026-06-26 10:02:00",
                DurationMs = 120000
            };
            run.BatchItems.Add(new BatchItemStatus { Plant = "5021", BatchIndex = 1, Status = "success" });
            run.BatchItems.Add(new BatchItemStatus { Plant = "9301", BatchIndex = 2, Status = "success" });
            string markdown = BuildSapDingTalkMarkdownContent(run, "自动化已跑完");
            bool ok = markdown.Contains("工厂", StringComparison.OrdinalIgnoreCase) &&
                      markdown.Contains("[5021]", StringComparison.OrdinalIgnoreCase) &&
                      markdown.Contains("[9301]", StringComparison.OrdinalIgnoreCase) &&
                      markdown.Contains("2026", StringComparison.OrdinalIgnoreCase) &&
                      markdown.Contains("25", StringComparison.OrdinalIgnoreCase) &&
                      !markdown.Contains("业务范围", StringComparison.OrdinalIgnoreCase) &&
                      !markdown.Contains("[2900]", StringComparison.OrdinalIgnoreCase) &&
                      !markdown.Contains("[9200]", StringComparison.OrdinalIgnoreCase) &&
                      !markdown.Contains("2026.06.15", StringComparison.OrdinalIgnoreCase) &&
                      !markdown.Contains("2026.06.21", StringComparison.OrdinalIgnoreCase) &&
                      !markdown.Contains("失败工厂", StringComparison.OrdinalIgnoreCase) &&
                      !markdown.Contains("SHOULD_NOT_APPEAR", StringComparison.OrdinalIgnoreCase);
            Check("DingTalk inputs for plants", ok, Truncate(markdown.Replace("\n", " | "), 240));
        }

        {
            var run = new RunRecordView
            {
                RunId = "RUN-SELFTEST-ZFI072N",
                TransactionCode = "ZFI072N",
                TransactionName = "维护采购价",
                Status = "success",
                RequestJson = "{\"transactionCode\":\"ZFI072N\",\"params\":{\"plants\":\"1022,1032\",\"plant\":\"1022\",\"year\":\"2026\",\"week\":\"9\",\"period\":\"2026.02.23\",\"weekEnd\":\"2026.03.01\",\"businessAreas\":\"2900\",\"token\":\"SHOULD_NOT_APPEAR\"}}",
                SapStatusType = "S",
                SapStatusText = "自动化已跑完",
                StartedAt = "2026-03-02 10:00:00",
                FinishedAt = "2026-03-02 10:02:00",
                DurationMs = 120000
            };
            run.BatchItems.Add(new BatchItemStatus { Plant = "1022", BatchIndex = 1, Status = "success" });
            run.BatchItems.Add(new BatchItemStatus { Plant = "1032", BatchIndex = 2, Status = "success" });
            run.Logs.Add(new RunLogLine { Level = "INFO", Message = "[ZFI072N] INFO: date input group #1; S_BUDAT=[2026.02.01~2026.02.28]" });
            run.Logs.Add(new RunLogLine { Level = "INFO", Message = "[ZFI072N] INFO: date input group #2; S_BUDAT=[2026.03.01~2026.03.01]" });
            string markdown = BuildSapDingTalkMarkdownContent(run, "自动化已跑完");
            bool ok = markdown.Contains("过账日期", StringComparison.OrdinalIgnoreCase) &&
                      markdown.Contains("第1次：2026.02.01~2026.02.28", StringComparison.OrdinalIgnoreCase) &&
                      markdown.Contains("第2次：2026.03.01~2026.03.01", StringComparison.OrdinalIgnoreCase) &&
                      markdown.Contains("[1022]", StringComparison.OrdinalIgnoreCase) &&
                      markdown.Contains("[1032]", StringComparison.OrdinalIgnoreCase) &&
                      !markdown.Contains("年度", StringComparison.OrdinalIgnoreCase) &&
                      !markdown.Contains("周次", StringComparison.OrdinalIgnoreCase) &&
                      !markdown.Contains("业务范围", StringComparison.OrdinalIgnoreCase) &&
                      !markdown.Contains("[2900]", StringComparison.OrdinalIgnoreCase) &&
                      !markdown.Contains("SHOULD_NOT_APPEAR", StringComparison.OrdinalIgnoreCase);
            Check("DingTalk inputs for ZFI072N BUDAT windows", ok, Truncate(markdown.Replace("\n", " | "), 260));
        }

        {
            var run = new RunRecordView
            {
                RunId = "RUN-SELFTEST-ZFI080B",
                TransactionCode = "ZFI080B",
                TransactionName = "工费率保存",
                Status = "success",
                RequestJson = "{\"transactionCode\":\"ZFI080B\",\"params\":{\"plants\":\"1022\",\"plant\":\"1022\",\"period\":\"2026.06.22\",\"weekEnd\":\"2026.06.28\",\"year\":\"2026\",\"week\":\"26\",\"businessAreas\":\"2900\"}}",
                SapStatusType = "S",
                SapStatusText = "自动化已跑完",
                StartedAt = "2026-07-02 10:00:00",
                FinishedAt = "2026-07-02 10:02:00",
                DurationMs = 120000
            };
            string markdown = BuildSapDingTalkMarkdownContent(run, "自动化已跑完");
            bool ok = markdown.Contains("过账日期", StringComparison.OrdinalIgnoreCase) &&
                      markdown.Contains("2026.06.22~2026.06.28", StringComparison.OrdinalIgnoreCase) &&
                      markdown.Contains("[1022]", StringComparison.OrdinalIgnoreCase) &&
                      !markdown.Contains("年度", StringComparison.OrdinalIgnoreCase) &&
                      !markdown.Contains("周次", StringComparison.OrdinalIgnoreCase) &&
                      !markdown.Contains("业务范围", StringComparison.OrdinalIgnoreCase) &&
                      !markdown.Contains("[2900]", StringComparison.OrdinalIgnoreCase);
            Check("DingTalk inputs for ZFI080B BUDAT range", ok, Truncate(markdown.Replace("\n", " | "), 260));
        }

        {
            var run = new RunRecordView
            {
                RunId = "RUN-SELFTEST-ZCO019",
                TransactionCode = "ZCO019",
                TransactionName = "标准材料成本",
                Status = "success",
                RequestJson = "{\"transactionCode\":\"ZCO019\",\"params\":{\"plants\":\"1024,1032,6041\",\"businessAreas\":\"2900,9200,2800\",\"period\":\"2026.06.15\",\"weekEnd\":\"2026.06.21\"}}",
                SapStatusType = "S",
                SapStatusText = "自动化已跑完",
                StartedAt = "2026-06-26 10:00:00",
                FinishedAt = "2026-06-26 10:02:00",
                DurationMs = 120000
            };
            run.BatchItems.Add(new BatchItemStatus { Plant = "1024", BatchIndex = 1, Status = "success" });
            run.BatchItems.Add(new BatchItemStatus { Plant = "1032", BatchIndex = 2, Status = "success" });
            run.BatchItems.Add(new BatchItemStatus { Plant = "6041", BatchIndex = 3, Status = "success" });
            string markdown = BuildSapDingTalkMarkdownContent(run, "自动化已跑完");
            bool ok = markdown.Contains("工厂", StringComparison.OrdinalIgnoreCase) &&
                      markdown.Contains("[1024]", StringComparison.OrdinalIgnoreCase) &&
                      markdown.Contains("[1032]", StringComparison.OrdinalIgnoreCase) &&
                      markdown.Contains("[6041]", StringComparison.OrdinalIgnoreCase) &&
                      markdown.Contains("2026.06.15", StringComparison.OrdinalIgnoreCase) &&
                      markdown.Contains("2026.06.21", StringComparison.OrdinalIgnoreCase) &&
                      !markdown.Contains("业务范围", StringComparison.OrdinalIgnoreCase) &&
                      !markdown.Contains("[2900]", StringComparison.OrdinalIgnoreCase) &&
                      !markdown.Contains("[9200]", StringComparison.OrdinalIgnoreCase) &&
                      !markdown.Contains("[2800]", StringComparison.OrdinalIgnoreCase);
            Check("DingTalk filters unused businessAreas", ok, Truncate(markdown.Replace("\n", " | "), 240));
        }

        {
            var run = new RunRecordView
            {
                RunId = "RUN-SELFTEST-ZFIR034",
                TransactionCode = "ZFIR034",
                TransactionName = "日期范围报表",
                Status = "success",
                RequestJson = "{\"transactionCode\":\"ZFIR034\",\"params\":{\"plants\":\"1022,1032\",\"businessAreas\":\"2900,9200\",\"period\":\"2026.06.22\",\"weekEnd\":\"2026.06.28\",\"token\":\"SHOULD_NOT_APPEAR\"}}",
                SapStatusType = "S",
                SapStatusText = "自动化已跑完",
                StartedAt = "2026-07-02 10:00:00",
                FinishedAt = "2026-07-02 10:02:00",
                DurationMs = 120000
            };
            string markdown = BuildSapDingTalkMarkdownContent(run, "自动化已跑完");
            bool ok = markdown.Contains("2026.06.22", StringComparison.OrdinalIgnoreCase) &&
                      markdown.Contains("2026.06.28", StringComparison.OrdinalIgnoreCase) &&
                      !markdown.Contains("工厂", StringComparison.OrdinalIgnoreCase) &&
                      !markdown.Contains("业务范围", StringComparison.OrdinalIgnoreCase) &&
                      !markdown.Contains("[1022]", StringComparison.OrdinalIgnoreCase) &&
                      !markdown.Contains("[2900]", StringComparison.OrdinalIgnoreCase) &&
                      !markdown.Contains("SHOULD_NOT_APPEAR", StringComparison.OrdinalIgnoreCase);
            Check("DingTalk inputs for ZFIR034 date range", ok, Truncate(markdown.Replace("\n", " | "), 240));
        }

        {
            var run = new RunRecordView
            {
                RunId = "RUN-SELFTEST-ZFI057-DINGTALK",
                TransactionCode = "ZFI057",
                TransactionName = "\u4EA7\u503C\u62C6\u5206",
                Status = "success",
                RequestJson = "{\"transactionCode\":\"ZFI057\",\"params\":{\"businessAreas\":\"2800\",\"period\":\"2026.04.27\",\"weekEnd\":\"2026.05.03\"}}",
                SapStatusType = "S",
                SapStatusText = "OK: GENERATED_MEMORY_IMPORT finished, rows=1523 S_GSBER=2800;\u7279\u6B8A\u8303\u56F4\u8FC7\u6EE4=\u65E0",
                Message = "ZFI057 auto workflow completed with no-data skips: step2Success=1, step2Skipped=1",
                StartedAt = "2026-07-16 10:18:49",
                FinishedAt = "2026-07-16 10:20:52",
                DurationMs = 122903
            };
            string expected = "ZFI057 \u81EA\u52A8\u6D41\u7A0B\u5DF2\u5B8C\u6210\uFF0C\u90E8\u5206\u4E1A\u52A1\u8303\u56F4\u65E0\u6570\u636E\u5DF2\u8DF3\u8FC7";
            string summary = BuildSapDingTalkMessage(run, run.Message);
            string markdown = BuildSapDingTalkMarkdownContent(run, summary);
            string plain = BuildSapDingTalkContent(run, summary);
            bool ok = summary.Contains(expected, StringComparison.OrdinalIgnoreCase) &&
                      markdown.Contains(expected, StringComparison.OrdinalIgnoreCase) &&
                      plain.Contains(expected, StringComparison.OrdinalIgnoreCase) &&
                      !summary.Contains("GENERATED_MEMORY_IMPORT", StringComparison.OrdinalIgnoreCase) &&
                      !markdown.Contains("GENERATED_MEMORY_IMPORT", StringComparison.OrdinalIgnoreCase) &&
                      !plain.Contains("GENERATED_MEMORY_IMPORT", StringComparison.OrdinalIgnoreCase) &&
                      !markdown.Contains("S_GSBER", StringComparison.OrdinalIgnoreCase) &&
                      !plain.Contains("S_GSBER", StringComparison.OrdinalIgnoreCase);
            Check("DingTalk filters technical ZFI057 memory status", ok, Truncate(markdown.Replace("\n", " | "), 240));
        }

        {
            var run = new RunRecordView
            {
                RunId = "RUN-SELFTEST-ZFI057-DINGTALK-SCOPE-SUMMARY",
                TransactionCode = "ZFI057",
                TransactionName = "\u4EA7\u503C\u62C6\u5206",
                Status = "partial_failed",
                RequestJson = "{\"transactionCode\":\"ZFI057\",\"params\":{\"businessAreas\":\"2800,2900\",\"period\":\"2026.04.27\",\"weekEnd\":\"2026.05.03\"}}",
                SapStatusType = "W",
                SapStatusText = "ZFI057\u6D41\u7A0B\u90E8\u5206\u5931\u8D25\uFF0C\u4E1A\u52A1\u8303\u56F4\uFF1A2800\u65E0\u6570\u636E\uFF0C2900\u8FD0\u884C\u5931\u8D25\uFF08step3 failed\uFF09",
                Message = "fallback should not replace scope summary",
                StartedAt = "2026-07-16 10:18:49",
                FinishedAt = "2026-07-16 10:20:52",
                DurationMs = 122903
            };
            run.Logs.Add(new RunLogLine { Level = "INFO", Message = "[scope] #1 businessArea=2800; plants=1011,1022,1031" });
            run.Logs.Add(new RunLogLine { Level = "INFO", Message = "[scope] #2 businessArea=2900; plants=1021,1023" });
            string summary = BuildSapDingTalkMessage(run, run.Message);
            string markdown = BuildSapDingTalkMarkdownContent(run, summary);
            string plain = BuildSapDingTalkContent(run, summary);
            string expected2800Mapping = "[2800]->\u5DE5\u5382\uFF1A1011,1022,1031";
            bool ok = summary.Contains("2800\u65E0\u6570\u636E", StringComparison.OrdinalIgnoreCase) &&
                      summary.Contains("2900\u8FD0\u884C\u5931\u8D25", StringComparison.OrdinalIgnoreCase) &&
                      markdown.Contains(expected2800Mapping, StringComparison.OrdinalIgnoreCase) &&
                      markdown.Contains("[2900]->\u5DE5\u5382\uFF1A1021,1023", StringComparison.OrdinalIgnoreCase) &&
                      markdown.Contains("2800\u65E0\u6570\u636E", StringComparison.OrdinalIgnoreCase) &&
                      markdown.Contains("2900\u8FD0\u884C\u5931\u8D25", StringComparison.OrdinalIgnoreCase) &&
                      markdown.Contains("ZFI057\u65E5\u671F\u5165\u53C2", StringComparison.OrdinalIgnoreCase) &&
                      markdown.Contains("\u7B2C1\u6B21\uFF1A\u6210\u672C\u6838\u7B97\u65E5\u671F=[2026.03.01~2026.04.30]", StringComparison.OrdinalIgnoreCase) &&
                      markdown.Contains("\u7B2C2\u6B21\uFF1A\u6210\u672C\u6838\u7B97\u65E5\u671F=[2026.05.01~2026.05.03]", StringComparison.OrdinalIgnoreCase) &&
                      plain.Contains("\u2022 \u4E1A\u52A1\u8303\u56F4\uFF1A", StringComparison.OrdinalIgnoreCase) &&
                      plain.Contains(expected2800Mapping, StringComparison.OrdinalIgnoreCase) &&
                      plain.Contains("[2900]->\u5DE5\u5382\uFF1A1021,1023", StringComparison.OrdinalIgnoreCase) &&
                      plain.Contains("ZFI057\u65E5\u671F\u5165\u53C2", StringComparison.OrdinalIgnoreCase);
            Check("DingTalk shows ZFI057 scope mapping and results", ok, Truncate(markdown.Replace("\n", " | "), 260));
        }

        {
            var run = new RunRecordView
            {
                RunId = "RUN-SELFTEST-ZFI057-DINGTALK-DATE-LOG",
                TransactionCode = "ZFI057",
                TransactionName = "\u4EA7\u503C\u62C6\u5206",
                Status = "success",
                RequestJson = "{\"transactionCode\":\"ZFI057\",\"params\":{\"businessAreas\":\"2800\"}}",
                SapStatusType = "S",
                SapStatusText = "ZFI057\u6D41\u7A0B\u5B8C\u6210\uFF0C\u4E1A\u52A1\u8303\u56F4\uFF1A2800\u8FD0\u884C\u6210\u529F",
                StartedAt = "2026-07-16 10:18:49",
                FinishedAt = "2026-07-16 10:20:52",
                DurationMs = 122903
            };
            run.Logs.Add(new RunLogLine { Level = "INFO", Message = "[scope] #1 businessArea=2800; plants=1011,1022" });
            run.Logs.Add(new RunLogLine { Level = "INFO", Message = "[step 2 ZFI057] INFO: date input group #1; S_KADKY=[2026.03.01~2026.04.30]; S_KADAT=[2026.03.02~2026.04.30]" });
            run.Logs.Add(new RunLogLine { Level = "INFO", Message = "[step 2 ZFI057] INFO: date input group #2; S_KADKY=[2026.05.01~2026.05.03]; S_KADAT=[2026.05.02~2026.05.03]" });
            string markdown = BuildSapDingTalkMarkdownContent(run, run.SapStatusText);
            string plain = BuildSapDingTalkContent(run, run.SapStatusText);
            bool ok = markdown.Contains("ZFI057\u65E5\u671F\u5165\u53C2", StringComparison.OrdinalIgnoreCase) &&
                      markdown.Contains("\u7B2C1\u6B21\uFF1A\u6210\u672C\u6838\u7B97\u65E5\u671F=[2026.03.01~2026.04.30]", StringComparison.OrdinalIgnoreCase) &&
                      markdown.Contains("\u6210\u672C\u6838\u7B97\u65E5\u671F\u8D77\u4E8E=[2026.03.02~2026.04.30]", StringComparison.OrdinalIgnoreCase) &&
                      markdown.Contains("\u7B2C2\u6B21\uFF1A\u6210\u672C\u6838\u7B97\u65E5\u671F=[2026.05.01~2026.05.03]", StringComparison.OrdinalIgnoreCase) &&
                      markdown.Contains("\u6210\u672C\u6838\u7B97\u65E5\u671F\u8D77\u4E8E=[2026.05.02~2026.05.03]", StringComparison.OrdinalIgnoreCase) &&
                      plain.Contains("ZFI057\u65E5\u671F\u5165\u53C2", StringComparison.OrdinalIgnoreCase) &&
                      plain.Contains("\u7B2C1\u6B21\uFF1A\u6210\u672C\u6838\u7B97\u65E5\u671F=[2026.03.01~2026.04.30]", StringComparison.OrdinalIgnoreCase) &&
                      plain.Contains("\u6210\u672C\u6838\u7B97\u65E5\u671F\u8D77\u4E8E=[2026.05.02~2026.05.03]", StringComparison.OrdinalIgnoreCase) &&
                      !markdown.Contains("S_KADKY=", StringComparison.OrdinalIgnoreCase) &&
                      !markdown.Contains("S_KADAT=", StringComparison.OrdinalIgnoreCase) &&
                      !plain.Contains("S_KADKY=", StringComparison.OrdinalIgnoreCase) &&
                      !plain.Contains("S_KADAT=", StringComparison.OrdinalIgnoreCase);
            Check("DingTalk shows ZFI057 date windows from execution logs", ok, Truncate(markdown.Replace("\n", " | "), 260));
        }

        {
            string cleaned = CleanDingTalkDisplayText("ZFI057 failed; S_KADKY=[2026.05.01~2026.05.03]; S_KADAT=[2026.05.02~2026.05.03]");
            bool ok = cleaned.Contains("\u6210\u672C\u6838\u7B97\u65E5\u671F=[2026.05.01~2026.05.03]", StringComparison.OrdinalIgnoreCase) &&
                      cleaned.Contains("\u6210\u672C\u6838\u7B97\u65E5\u671F\u8D77\u4E8E=[2026.05.02~2026.05.03]", StringComparison.OrdinalIgnoreCase) &&
                      !cleaned.Contains("S_KADKY", StringComparison.OrdinalIgnoreCase) &&
                      !cleaned.Contains("S_KADAT", StringComparison.OrdinalIgnoreCase);
            Check("DingTalk cleans ZFI057 technical date fields", ok, cleaned);
        }

        {
            var run = new RunRecordView
            {
                RunId = "RUN-SELFTEST-ZFI057-DINGTALK-LOG-MAPPING",
                TransactionCode = "ZFI057",
                TransactionName = "\u4EA7\u503C\u62C6\u5206",
                Status = "success",
                RequestJson = "{\"transactionCode\":\"ZFI057\",\"params\":{\"businessAreas\":\"2800\",\"period\":\"2026.04.27\",\"weekEnd\":\"2026.05.03\"}}",
                SapStatusType = "S",
                SapStatusText = "ZFI057\u6D41\u7A0B\u5B8C\u6210\uFF0C\u4E1A\u52A1\u8303\u56F4\uFF1A2800\u8FD0\u884C\u6210\u529F",
                StartedAt = "2026-07-16 10:18:49",
                FinishedAt = "2026-07-16 10:20:52",
                DurationMs = 122903
            };
            run.Logs.Add(new RunLogLine { Level = "INFO", Message = "[scope] #1 businessArea=2800; plants=9001,9002" });
            string markdown = BuildSapDingTalkMarkdownContent(run, run.SapStatusText);
            string plain = BuildSapDingTalkContent(run, run.SapStatusText);
            bool ok = markdown.Contains("[2800]->\u5DE5\u5382\uFF1A9001,9002", StringComparison.OrdinalIgnoreCase) &&
                      plain.Contains("[2800]->\u5DE5\u5382\uFF1A9001,9002", StringComparison.OrdinalIgnoreCase) &&
                      !markdown.Contains("\u672A\u89E3\u6790", StringComparison.OrdinalIgnoreCase) &&
                      !plain.Contains("\u672A\u89E3\u6790", StringComparison.OrdinalIgnoreCase);
            Check("DingTalk prefers logged ZFI057 scope mapping", ok, Truncate(markdown.Replace("\n", " | "), 260));
        }

        {
            var run = new RunRecordView
            {
                RunId = "RUN-SELFTEST-ZFI057-DINGTALK-FAILED",
                TransactionCode = "ZFI057",
                TransactionName = "\u4EA7\u503C\u62C6\u5206",
                Status = "failed",
                RequestJson = "{\"transactionCode\":\"ZFI057\",\"params\":{\"businessAreas\":\"2800\",\"period\":\"2026.04.27\",\"weekEnd\":\"2026.05.03\"}}",
                SapStatusType = "S",
                SapStatusText = "OK: GENERATED_MEMORY_IMPORT finished, rows=1523 S_GSBER=2800",
                Message = "ZFI057 workflow stopped: step2 failed after memory fetch",
                StartedAt = "2026-07-16 10:18:49",
                FinishedAt = "2026-07-16 10:20:52",
                DurationMs = 122903
            };
            string summary = BuildSapDingTalkMessage(run, run.Message);
            string markdown = BuildSapDingTalkMarkdownContent(run, summary);
            string plain = BuildSapDingTalkContent(run, summary);
            bool ok = summary.Contains("ZFI057 workflow stopped", StringComparison.OrdinalIgnoreCase) &&
                      markdown.Contains("ZFI057 workflow stopped", StringComparison.OrdinalIgnoreCase) &&
                      plain.Contains("ZFI057 workflow stopped", StringComparison.OrdinalIgnoreCase) &&
                      !summary.Contains("GENERATED_MEMORY_IMPORT", StringComparison.OrdinalIgnoreCase) &&
                      !markdown.Contains("GENERATED_MEMORY_IMPORT", StringComparison.OrdinalIgnoreCase) &&
                      !plain.Contains("GENERATED_MEMORY_IMPORT", StringComparison.OrdinalIgnoreCase);
            Check("DingTalk keeps failure message after technical SAP status", ok, Truncate(markdown.Replace("\n", " | "), 240));
        }

        {
            var run = new RunRecordView
            {
                RunId = "RUN-SELFTEST-ZFI057-DINGTALK-NODATA",
                TransactionCode = "ZFI057",
                TransactionName = "\u4EA7\u503C\u62C6\u5206",
                Status = "failed",
                RequestJson = "{\"transactionCode\":\"ZFI057\",\"params\":{\"businessAreas\":\"2800\",\"period\":\"2026.04.27\",\"weekEnd\":\"2026.05.03\"}}",
                SapStatusType = "E",
                SapStatusText = "\u6CA1\u6709\u7B26\u5408\u6761\u4EF6\u6570\u636E",
                Message = "fallback should not replace SAP status",
                StartedAt = "2026-07-16 10:18:49",
                FinishedAt = "2026-07-16 10:20:52",
                DurationMs = 122903
            };
            string summary = BuildSapDingTalkMessage(run, run.Message);
            string markdown = BuildSapDingTalkMarkdownContent(run, summary);
            string plain = BuildSapDingTalkContent(run, summary);
            bool ok = summary.Contains("\u6CA1\u6709\u7B26\u5408\u6761\u4EF6\u6570\u636E", StringComparison.OrdinalIgnoreCase) &&
                      markdown.Contains("\u6CA1\u6709\u7B26\u5408\u6761\u4EF6\u6570\u636E", StringComparison.OrdinalIgnoreCase) &&
                      plain.Contains("\u6CA1\u6709\u7B26\u5408\u6761\u4EF6\u6570\u636E", StringComparison.OrdinalIgnoreCase);
            Check("DingTalk keeps normal SAP status text", ok, Truncate(markdown.Replace("\n", " | "), 240));
        }

        {
            var run = new RunRecordView
            {
                RunId = "RUN-SELFTEST-ZFI057-DINGTALK-SUCCESS",
                TransactionCode = "ZFI057",
                TransactionName = "\u4EA7\u503C\u62C6\u5206",
                Status = "success",
                RequestJson = "{\"transactionCode\":\"ZFI057\",\"params\":{\"businessAreas\":\"2800\",\"period\":\"2026.04.27\",\"weekEnd\":\"2026.05.03\"}}",
                SapStatusType = "S",
                SapStatusText = "OK: GENERATED_MEMORY_IMPORT finished, rows=1523 S_GSBER=2800",
                Message = "ZFI057 auto workflow completed: ZFI019NL -> ZFI057 -> ZCO020",
                StartedAt = "2026-07-16 10:18:49",
                FinishedAt = "2026-07-16 10:20:52",
                DurationMs = 122903
            };
            string expected = "ZFI057 \u81EA\u52A8\u6D41\u7A0B\u5DF2\u5B8C\u6210";
            string summary = BuildSapDingTalkMessage(run, run.Message);
            string markdown = BuildSapDingTalkMarkdownContent(run, summary);
            string plain = BuildSapDingTalkContent(run, summary);
            bool ok = summary.Contains(expected, StringComparison.OrdinalIgnoreCase) &&
                      markdown.Contains(expected, StringComparison.OrdinalIgnoreCase) &&
                      plain.Contains(expected, StringComparison.OrdinalIgnoreCase) &&
                      !summary.Contains("GENERATED_MEMORY_IMPORT", StringComparison.OrdinalIgnoreCase) &&
                      !markdown.Contains("GENERATED_MEMORY_IMPORT", StringComparison.OrdinalIgnoreCase) &&
                      !plain.Contains("GENERATED_MEMORY_IMPORT", StringComparison.OrdinalIgnoreCase);
            Check("DingTalk formats ZFI057 workflow success message", ok, Truncate(markdown.Replace("\n", " | "), 240));
        }

        {
            bool ok = IsSapDingTalkTarget("dingtalk") &&
                      IsSapDingTalkTarget("sap-dingtalk") &&
                      SplitNotifyTargets("dingtalk,local|robot").SequenceEqual(new[] { "dingtalk", "local", "robot" }) &&
                      new DingTalkOpenApiConfig().MissingFieldsSummary.Equals("baseUrl/appKey/appSecret/agentId", StringComparison.OrdinalIgnoreCase);
            Check("DingTalk notify diagnostics", ok, "notifyTarget=dingtalk is a SAP DingTalk request, not a sent robot target");
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
            string ini = Path.Combine(Path.GetTempPath(), $"sap_rpa_selftest_saplogon_{Guid.NewGuid():N}.ini");
            File.WriteAllText(ini, """
[Server]
Item1=10.0.40.212
[Database]
Item1=10
[MSSysName]
Item1=TD1
[Description]
Item1=test888
""", Encoding.ASCII);
            try
            {
                var p = new SapRunParams
                {
                    System = "test888",
                    Client = "888",
                    User = "IT049",
                    Password = "SECRET",
                    Language = "ZH",
                    SysNr = "10000000000000000000000"
                };
                var attempts = BuildSapLoginAttempts(p, new[] { ini });
                bool ok = attempts.Any(a =>
                    a.Name.Equals("saplogon-direct-guiparm", StringComparison.OrdinalIgnoreCase) &&
                    a.Args.Any(x => x.Equals("-system=TD1", StringComparison.OrdinalIgnoreCase)) &&
                    a.Args.Any(x => x.Equals("-guiparm=10.0.40.212 10", StringComparison.OrdinalIgnoreCase)));
                Check("sapshcut guiparm fallback", ok, string.Join(" | ", attempts.Select(a => $"{a.Name}:{MaskSapArgs(string.Join(" ", a.Args))}")));
            }
            finally
            {
                try { File.Delete(ini); } catch { }
            }
        }

        {
            var local = new SapLocalConfig
            {
                System = "TEST_SYSTEM",
                Client = "888",
                User = "IT049",
                Password = "SECRET",
                MultiLogonPolicy = "takeover"
            };
            var q = new NameValueCollection
            {
                ["tcode"] = "ZFI019NL",
                ["multilogonpolicy"] = "takeover"
            };
            string? old = Environment.GetEnvironmentVariable("SAP_RPA_MULTI_LOGON_POLICY");
            try
            {
                Environment.SetEnvironmentVariable("SAP_RPA_MULTI_LOGON_POLICY", "fail");
                var p = BuildParams(q, PrimaryProtocolName, local);
                Check("multi-logon env fail overrides request", p.MultiLogonPolicy.Equals("fail", StringComparison.OrdinalIgnoreCase), $"policy={p.MultiLogonPolicy}");
            }
            finally
            {
                Environment.SetEnvironmentVariable("SAP_RPA_MULTI_LOGON_POLICY", old);
            }
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
                if (!string.IsNullOrWhiteSpace(outputFile))
                {
                    var file = BuildRunFile(outputFile);
                    result.Files.Add(file);
                    if (file.Size <= 0)
                    {
                        result.Status = "failed";
                        string expanded = Environment.ExpandEnvironmentVariables(outputFile);
                        string message = File.Exists(expanded)
                            ? $"OUTPUT_FILE is empty: {outputFile}"
                            : $"OUTPUT_FILE was reported but does not exist: {outputFile}";
                        result.Logs.Add(new RunLogLine { Level = "ERROR", Message = message });
                    }
                    else
                    {
                        ScheduleDelayedExcelWorkbookClose(outputFile, result.Logs);
                    }
                }
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

    static void ScheduleDelayedExcelWorkbookClose(string outputFile, List<RunLogLine> logs)
    {
        try
        {
            string expanded = Environment.ExpandEnvironmentVariables(outputFile ?? "");
            if (string.IsNullOrWhiteSpace(expanded) || !File.Exists(expanded))
                return;

            if (!IsExcelWorkbookPath(expanded))
                return;

            if (ScheduleExcelCloseHelper(expanded))
                logs.Add(new RunLogLine { Level = "INFO", Message = $"scheduled delayed Excel close for output workbook: {Path.GetFileName(expanded)}" });
            else
                logs.Add(new RunLogLine { Level = "INFO", Message = $"skipped delayed Excel close because no Excel process is open: {Path.GetFileName(expanded)}" });
        }
        catch (Exception ex)
        {
            logs.Add(new RunLogLine { Level = "WARN", Message = $"failed to schedule delayed Excel close: {ex.Message}" });
        }
    }

    static void ScheduleDelayedExcelWorkbookClose(string outputFile, string runId)
    {
        try
        {
            string expanded = Environment.ExpandEnvironmentVariables(outputFile ?? "");
            if (string.IsNullOrWhiteSpace(expanded) || !File.Exists(expanded) || !IsExcelWorkbookPath(expanded))
                return;

            if (ScheduleExcelCloseHelper(expanded))
                AppendRunLog(runId, "INFO", $"scheduled safe Excel close for exported workbook: {Path.GetFileName(expanded)}");
            else
                AppendRunLog(runId, "INFO", $"skipped safe Excel close because no Excel process is open: {Path.GetFileName(expanded)}");
        }
        catch (Exception ex)
        {
            AppendRunLog(runId, "WARN", $"failed to schedule safe Excel close for exported workbook: {ex.Message}");
        }
    }

    static bool IsExcelWorkbookPath(string path)
    {
        string extension = Path.GetExtension(path ?? "");
        return extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".xls", StringComparison.OrdinalIgnoreCase);
    }

    static bool ScheduleExcelCloseHelper(string fullPath)
    {
        string workbookName = Path.GetFileName(fullPath);
        if (string.IsNullOrWhiteSpace(workbookName))
            return false;

        if (Process.GetProcessesByName("EXCEL").Length == 0)
            return false;

        string helperDir = Path.Combine(LogDirectory, "excel-close");
        Directory.CreateDirectory(helperDir);
        string safeName = Regex.Replace(workbookName, @"[^A-Za-z0-9_.-]+", "_");
        if (safeName.Length > 80)
            safeName = safeName[^80..];
        string helperPath = Path.Combine(helperDir, $"close_{DateTime.Now:yyyyMMddHHmmssfff}_{safeName}.vbs");
        File.WriteAllLines(helperPath, BuildExcelCloseHelperScript(), Encoding.ASCII);

        var psi = new ProcessStartInfo(ResolveCscriptPath())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        psi.ArgumentList.Add("//B");
        psi.ArgumentList.Add("//nologo");
        psi.ArgumentList.Add(helperPath);
        psi.ArgumentList.Add(Path.GetFullPath(fullPath));

        Process.Start(psi);
        return true;
    }

    static string[] BuildExcelCloseHelperScript()
    {
        return new[]
        {
            "On Error Resume Next",
            "target = LCase(Replace(CStr(WScript.Arguments.Item(0)), \"/\", \"\\\"))",
            "waited = 0",
            "Do While waited <= 60000",
            "  Err.Clear",
            "  Set app = GetObject(, \"Excel.Application\")",
            "  If Err.Number = 0 And IsObject(app) Then",
            "    app.DisplayAlerts = False",
            "    closed = 0",
            "    For i = app.Workbooks.Count To 1 Step -1",
            "      Err.Clear",
            "      Set wb = app.Workbooks.Item(CInt(i))",
            "      fullName = \"\"",
            "      If Err.Number = 0 Then fullName = LCase(Replace(CStr(wb.FullName), \"/\", \"\\\"))",
            "      If Err.Number = 0 And fullName = target Then",
            "        wb.Close False",
            "        closed = closed + 1",
            "      End If",
            "      Err.Clear",
            "    Next",
            "    If closed > 0 Then",
            "      WScript.Sleep 500",
            "      If app.Workbooks.Count = 0 Then app.Quit",
            "      CreateObject(\"Scripting.FileSystemObject\").DeleteFile WScript.ScriptFullName, True",
            "      WScript.Quit 0",
            "    End If",
            "  End If",
            "  Err.Clear",
            "  Set wmi = GetObject(\"winmgmts:\\\\.\\root\\cimv2\")",
            "  If Err.Number = 0 And IsObject(wmi) Then",
            "    Set procs = wmi.ExecQuery(\"SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='EXCEL.EXE'\")",
            "    If Err.Number = 0 Then",
            "      For Each proc In procs",
            "        cmd = \"\"",
            "        If Not IsNull(proc.CommandLine) Then cmd = LCase(Replace(CStr(proc.CommandLine), \"/\", \"\\\"))",
            "        If InStr(cmd, target) > 0 Then",
            "          proc.Terminate()",
            "          CreateObject(\"Scripting.FileSystemObject\").DeleteFile WScript.ScriptFullName, True",
            "          WScript.Quit 0",
            "        End If",
            "        Err.Clear",
            "      Next",
            "    End If",
            "  End If",
            "  Err.Clear",
            "  WScript.Sleep 1000",
            "  waited = waited + 1000",
            "Loop",
            "CreateObject(\"Scripting.FileSystemObject\").DeleteFile WScript.ScriptFullName, True",
            "WScript.Quit 0"
        };
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
        return $"tcode={p.TCode}, script={p.Script}, system={p.System}, client={p.Client}, user={p.User}, pw={MaskValue("pw", p.Password)}, lang={p.Language}, sysnr={p.SysNr}, year={p.Year}, week={p.Week}, plant={p.Plant}, plants={p.Plants}, period={p.Period}, businessArea={p.BusinessArea}, businessAreas={p.BusinessAreas}, weekEnd={p.WeekEnd}, materialsCount={NormalizeStringArray(p.Materials).Length}, factoryGroup={p.FactoryGroup}, runStrategy={p.RunStrategy}";
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
    public string MultiLogonPolicy { get; set; } = "takeover";
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
    public string Materials { get; set; } = "";
    public string FactoryGroup { get; set; } = "";
    public string RunStrategy { get; set; } = "";
    public string Field1Name { get; set; } = "";
    public string Field1Value { get; set; } = "";
    public string Field2Name { get; set; } = "";
    public string Field2Value { get; set; } = "";
    public string CaretPos { get; set; } = "0";
    public string ButtonId { get; set; } = "";
    public string RunId { get; set; } = "";
    public string ParentRunId { get; set; } = "";
    public int? TimeoutSeconds { get; set; }
}

record BatchRunPlan(string TCode, string ParamKey, string SingleParamKey, string ListParamKey, string ItemLabel, string[] Items);

record AlvExportTarget(string Directory, string FileName, string FullPath)
{
    public static readonly AlvExportTarget Empty = new("", "", "");
}

record Zfi057WorkflowScope(string BusinessArea, string[] Plants);

record Zfi057WorkflowScopeResult(string BusinessArea, string[] Plants, string Status, string Message);

record Zfi057Step1MaterialFetch(RunResultRequest Result, string[] Materials, Zfi019NlFetchResult FetchResult);

record Zfi057Step2DateWindow(int Index, string KadkyLow, string KadkyHigh, string KadatLow, string KadatHigh);

record BudatDateWindow(int Index, string Low, string High);

record Zfi057Step3ScopeResult(bool Success, string Message, string FirstJobStatus, string FinalJobStatus, bool Repeated);

record Zfi057TbtcoJobCheckResult(bool Success, bool IsTerminal, bool IsFailure, string Status, string Category, string Message, RunResultRequest RawResult);

record DingTalkParamGroup(string Label, string[] Keys, bool SplitValues);

record DingTalkInputLine(string Label, List<string> Values)
{
    public int Count => Values.Count;
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
    public string? MultiLogonPolicy { get; set; }
}

class SapNcoLocalConfig
{
    public string ConnectionName { get; set; } = "";
    public string Name { get; set; } = "";
    public string SystemId { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string AppServerHost { get; set; } = "";
    public string Ashost { get; set; } = "";
    public string SystemNumber { get; set; } = "";
    public string SysNr { get; set; } = "";
    public string Client { get; set; } = "";
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string Language { get; set; } = "";
    public string Lang { get; set; } = "";
    public string Router { get; set; } = "";
    public string SapRouter { get; set; } = "";
}

class Zfi019NlMemoryLocalConfig
{
    public string Report { get; set; } = "";
    public string Variant { get; set; } = "";
    public string MemoryId { get; set; } = "";
    public string MemoryName { get; set; } = "";
    public string SpoolDevice { get; set; } = "";
    public int WaitSeconds { get; set; }
    public string SplitTable { get; set; } = "";
    public string SplitBukrs { get; set; } = "";
    public string[] DongtaiBusinessAreas { get; set; } = Array.Empty<string>();
}

readonly record struct SapSessionProbeResult(bool Ready, bool HasSapGui, bool HasBlockingSapGui, bool HasPendingLoginDialog, string Details);

readonly record struct SapLoginAttempt(string Name, List<string> Args);

readonly record struct SapLoginFailure(string Key, DateTime AtUtc, string Details);

class SapLogonEntry
{
    public string SourcePath { get; set; } = "";
    public string ItemKey { get; set; } = "";
    public string Description { get; set; } = "";
    public string Server { get; set; } = "";
    public string SystemNumber { get; set; } = "";
    public string SystemId { get; set; } = "";
    public string Router { get; set; } = "";
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

interface IRawJsonRequest
{
    void CaptureRawJson(string json);
}

class ScheduleTaskRequest : IRawJsonRequest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string TCode { get; set; } = "";
    public string Code { get; set; } = "";
    public string TransactionCode { get; set; } = "";
    public JsonElement Plants { get; set; }
    public string PlantsCsv { get; set; } = "";
    public JsonElement PlantCodes { get; set; }
    public JsonElement FactoryCodes { get; set; }
    public string DefaultBusinessScope { get; set; } = "";
    public string FactoryGroup { get; set; } = "";
    public string PlantGroupId { get; set; } = "";
    public string GroupId { get; set; } = "";
    public string DefaultPlantGroup { get; set; } = "";
    public string Cron { get; set; } = "";
    public string Frequency { get; set; } = "";
    public string ScheduleType { get; set; } = "";
    public string FrequencyCode { get; set; } = "";
    public string Time { get; set; } = "";
    public string RunTime { get; set; } = "";
    public string ExecTime { get; set; } = "";
    public string RunAt { get; set; } = "";
    public string StartTime { get; set; } = "";
    public JsonElement BusinessAreas { get; set; }
    public string BusinessAreasCsv { get; set; } = "";
    public bool? Enabled { get; set; }
    public bool? NotifyEnabled { get; set; }
    public bool? Notify { get; set; }
    public bool? NotifyStart { get; set; }
    public bool? NotifyOnSuccess { get; set; }
    public bool? NotifySuccess { get; set; }
    public bool? NotifyOnFailure { get; set; }
    public bool? NotifyFail { get; set; }
    public string NotifyTarget { get; set; } = "";
    public JsonElement Params { get; set; }
    public string CreatedBy { get; set; } = "";
    public string UpdatedBy { get; set; } = "";

    [JsonIgnore]
    public bool HasExplicitPlantSelection { get; private set; }

    public void CaptureRawJson(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return;

            HasExplicitPlantSelection =
                HasJsonProperty(doc.RootElement, "plants") ||
                HasJsonProperty(doc.RootElement, "plantsCsv") ||
                HasJsonProperty(doc.RootElement, "plantCodes") ||
                HasJsonProperty(doc.RootElement, "factoryCodes");
        }
        catch
        {
            HasExplicitPlantSelection = false;
        }
    }

    static bool HasJsonProperty(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.EnumerateObject().Any(prop => prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }
}

class ScheduleTaskDue
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string TCode { get; set; } = "";
    public string Plants { get; set; } = "";
    public string DefaultBusinessScope { get; set; } = "";
    public string Cron { get; set; } = "";
    public string Frequency { get; set; } = "";
    public string RunTime { get; set; } = "";
    public bool NotifyEnabled { get; set; }
    public bool NotifyOnStart { get; set; }
    public bool NotifyOnSuccess { get; set; }
    public bool NotifyOnFailure { get; set; }
    public string NotifyTarget { get; set; } = "";
    public string ParamsJson { get; set; } = "";
    public string ScheduledAt { get; set; } = "";
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
    public string DingTalkUserId { get; set; } = "";
    public string Ddid { get; set; } = "";
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
    public string TransactionName { get; set; } = "";
    public string OperatorId { get; set; } = "";
    public string OperatorName { get; set; } = "";
    public string OperatorDept { get; set; } = "";
    public string DingTalkUserId { get; set; } = "";
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
    public string RunType { get; set; } = "single";
    public string ParentRunId { get; set; } = "";
    public string BatchItemKey { get; set; } = "";
    public int BatchIndex { get; set; }
    public int BatchTotal { get; set; }
    public int AttemptNo { get; set; } = 1;
    public string SummaryJson { get; set; } = "";
    public string SourceParentRunId { get; set; } = "";
    public string RerunOfRunId { get; set; } = "";
    public List<string> ChildRunIds { get; set; } = new();
    public List<BatchItemStatus> BatchItems { get; set; } = new();
    public string LockedBy { get; set; } = "";
    public string LockedAt { get; set; } = "";
    public int QueuePosition { get; set; }
    public int RunsAhead { get; set; }
    public int WorkItemsAhead { get; set; }
    public string RunningRunId { get; set; } = "";
    public long QueuedCount { get; set; }
    public List<RunLogLine> Logs { get; set; } = new();
    public List<RunFile> Files { get; set; } = new();
}

class QueuePositionInfo
{
    public int QueuePosition { get; set; }
    public int RunsAhead { get; set; }
    public int WorkItemsAhead { get; set; }
    public string RunningRunId { get; set; } = "";
    public long QueuedCount { get; set; }
}

class QueueRunSummary
{
    public string RunId { get; set; } = "";
    public string TransactionCode { get; set; } = "";
    public string OperatorId { get; set; } = "";
    public string OperatorName { get; set; } = "";
    public string OperatorDept { get; set; } = "";
    public string Status { get; set; } = "";
    public string QueuedAt { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public int Priority { get; set; }
    public int Attempt { get; set; }
    public int MaxAttempts { get; set; }
    public string LockedBy { get; set; } = "";
    public string LockedAt { get; set; } = "";
    public int QueuePosition { get; set; }
    public int RunsAhead { get; set; }
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

class BatchItemStatus
{
    public string ParentRunId { get; set; } = "";
    public string ChildRunId { get; set; } = "";
    public string Plant { get; set; } = "";
    public int BatchIndex { get; set; }
    public int AttemptNo { get; set; } = 1;
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public string FinishedAt { get; set; } = "";
    public long DurationMs { get; set; }
}

class NotificationTarget
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string RobotType { get; set; } = "";
    public string Label { get; set; } = "";
    public string Webhook { get; set; } = "";
    public string Secret { get; set; } = "";
}

class SapDingTalkNotifyRequest
{
    public string RunId { get; set; } = "";
    public string EventName { get; set; } = "";
    public string Message { get; set; } = "";
    public string Content { get; set; } = "";
    public string MarkdownContent { get; set; } = "";
    public string TransactionCode { get; set; } = "";
    public string Status { get; set; } = "";
    public string WorkNo { get; set; } = "";
    public string DingTalkId { get; set; } = "";
    public string SapFunction { get; set; } = "";
    public string IV_WORKNO => WorkNo;
    public string IV_DDID => DingTalkId;
    public string IV_CONTENT => Content;
}

class DingTalkOpenApiConfig
{
    public string BaseUrl { get; set; } = "";
    public string AppKey { get; set; } = "";
    public string AppSecret { get; set; } = "";
    public string AgentId { get; set; } = "";
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(BaseUrl) &&
        !string.IsNullOrWhiteSpace(AppKey) &&
        !string.IsNullOrWhiteSpace(AppSecret) &&
        !string.IsNullOrWhiteSpace(AgentId);

    public string MissingFieldsSummary
    {
        get
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(BaseUrl))
                missing.Add("baseUrl");
            if (string.IsNullOrWhiteSpace(AppKey))
                missing.Add("appKey");
            if (string.IsNullOrWhiteSpace(AppSecret))
                missing.Add("appSecret");
            if (string.IsNullOrWhiteSpace(AgentId))
                missing.Add("agentId");
            return missing.Count == 0 ? "none" : string.Join("/", missing);
        }
    }
}

class DingTalkOpenApiSendResult
{
    public string ErrCode { get; set; } = "";
    public string ErrMsg { get; set; } = "";
    public string TaskId { get; set; } = "";
}

class NotificationHttpResult
{
    public NotificationHttpResult(int statusCode, string body, string transport)
    {
        StatusCode = statusCode;
        Body = body;
        Transport = transport;
    }

    public int StatusCode { get; }
    public string Body { get; }
    public string Transport { get; }
    public bool IsSuccessStatusCode => StatusCode >= 200 && StatusCode <= 299;
}

class SapFunctionResult
{
    public string Type { get; set; } = "";
    public string Message { get; set; } = "";
}

class TransactionScriptInfo
{
    public string ScriptFile { get; set; } = "";
    public string ScriptHash { get; set; } = "";
    public string[] ParamKeys { get; set; } = Array.Empty<string>();
}

class QueuedRunWorkItem
{
    public string RunId { get; set; } = "";
    public string TransactionCode { get; set; } = "";
    public string RequestJson { get; set; } = "";
    public string ScriptFile { get; set; } = "";
    public string RunType { get; set; } = "single";
    public string ParentRunId { get; set; } = "";
}

class RunHeartbeatScope : IDisposable
{
    private readonly string runId;
    private readonly ManualResetEventSlim stop;
    private readonly Thread thread;
    private readonly Action<string> onDispose;
    private bool disposed;

    public RunHeartbeatScope(string runId, ManualResetEventSlim stop, Thread thread, Action<string> onDispose)
    {
        this.runId = runId;
        this.stop = stop;
        this.thread = thread;
        this.onDispose = onDispose;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        stop.Set();
        if (thread.IsAlive)
            thread.Join(TimeSpan.FromSeconds(2));
        stop.Dispose();
        onDispose(runId);
    }
}

class PlantReportAccumulator
{
    public long TotalRuns { get; set; }
    public long SuccessRuns { get; set; }
    public long FailedRuns { get; set; }
    public long DurationTotalMs { get; set; }
    public long DurationCount { get; set; }
}
