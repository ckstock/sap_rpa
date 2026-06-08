using Microsoft.Win32;
using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace SapWebLauncher;

static class Program
{
    private const string PrimaryProtocolName = "sap-rpa";
    private const string LegacyProtocolName = "sap-zck";
    private const string MutexId = "SapWebLauncher-SingleInstance-Mutex";
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SapWebLauncher");
    private static readonly string LogFilePath = Path.Combine(LogDirectory, "launcher.log");

    static void Main(string[] args)
    {
        Log($"启动参数: {MaskRawArg(args.FirstOrDefault())}");

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
            RunSelfTest();
            return;
        }

        if (raw.Equals("--register", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("/register", StringComparison.OrdinalIgnoreCase))
        {
            RegisterProtocols();
            Console.WriteLine($"{PrimaryProtocolName}:// 和 {LegacyProtocolName}:// 协议已注册");
            return;
        }

        Console.WriteLine($"用法: {Process.GetCurrentProcess().ProcessName}.exe [--register]");
        Console.WriteLine($"  或从浏览器跳转 {PrimaryProtocolName}://run?action=run&tcode=ZFI019NL&system=Fiori&...");
    }

    static bool IsSupportedUri(string raw)
    {
        return raw.StartsWith(PrimaryProtocolName + "://", StringComparison.OrdinalIgnoreCase) ||
               raw.StartsWith(LegacyProtocolName + "://", StringComparison.OrdinalIgnoreCase);
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
        RegisterProtocol(LegacyProtocolName, exePath);
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
        LaunchSapGuiAndExecute(new SapRunParams
        {
            System = "Fiori",
            Client = "400",
            User = "UI5035",
            Password = "fiori666",
            Language = "ZH",
            SysNr = "04",
            TCode = "ZFI019NL",
            Script = "openOnly"
        });
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

    static SapRunParams BuildParams(NameValueCollection query, string protocolName)
    {
        string fallbackTCode = protocolName.Equals(LegacyProtocolName, StringComparison.OrdinalIgnoreCase) ? "zck" : "ZFI019NL";
        string tcode = First(query, "tcode", "t-code", "transaction", "transactioncode") ?? fallbackTCode;
        string script = First(query, "script", "scriptmode", "mode") ?? (tcode.Equals("zck", StringComparison.OrdinalIgnoreCase) ? "zck" : "openOnly");

        var p = new SapRunParams
        {
            System = First(query, "system", "sys") ?? "Fiori",
            Client = First(query, "client", "cli") ?? "400",
            User = First(query, "user", "usr") ?? "UI5035",
            Password = First(query, "pw", "password") ?? "fiori666",
            Language = First(query, "lang", "language") ?? "ZH",
            SysNr = First(query, "sysnr") ?? "04",
            TCode = SanitizeTCode(tcode),
            Script = script,
            Plant = First(query, "plant", "werks") ?? "",
            Plants = First(query, "plants", "werkslist", "plantlist") ?? "",
            Period = First(query, "period", "week") ?? "",
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

    static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
    }

    static void LaunchSapGuiAndExecute(SapRunParams p)
    {
        string? sapshcut = FindSapshcut();
        if (string.IsNullOrEmpty(sapshcut))
        {
            Console.Error.WriteLine("未找到 sapshcut.exe，请安装 SAP GUI");
            Log("未找到 sapshcut.exe，请安装 SAP GUI");
            return;
        }

        string args = $"-sysname=\"{EscapeArg(p.System)}\" -client={EscapeArg(p.Client)} " +
                      $"-user={EscapeArg(p.User)} -pw={EscapeArg(p.Password)} -language={EscapeArg(p.Language)} " +
                      $"-type=Transaction -command=\"{EscapeArg(p.TCode)}\"";

        if (!string.IsNullOrWhiteSpace(p.SysNr))
            args += $" -sysnr={EscapeArg(p.SysNr)}";

        Log($"启动 SAP GUI: path={sapshcut}, args={MaskSapArgs(args)}");
        Process.Start(sapshcut, args);

        Log("SAP GUI 已启动，0.1 秒后开始执行 VBS 自动化");
        Thread.Sleep(100);

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
        string template = ReadEmbeddedTemplate("transaction_template.vbs");
        string vbsScript = template
            .Replace("{OK_CODE}", VbsEscape(p.TCode))
            .Replace("{SCRIPT_MODE}", VbsEscape(p.Script))
            .Replace("{FIELD1_NAME}", VbsEscape(p.Field1Name))
            .Replace("{FIELD1_VALUE}", VbsEscape(p.Field1Value))
            .Replace("{FIELD2_NAME}", VbsEscape(p.Field2Name))
            .Replace("{FIELD2_VALUE}", VbsEscape(p.Field2Value))
            .Replace("{PLANTS}", VbsEscape(p.Plants))
            .Replace("{BUSINESS_AREAS}", VbsEscape(p.BusinessAreas))
            .Replace("{FACTORY_GROUP}", VbsEscape(p.FactoryGroup))
            .Replace("{RUN_STRATEGY}", VbsEscape(p.RunStrategy))
            .Replace("{PERIOD}", VbsEscape(p.Period))
            .Replace("{WEEK_END}", VbsEscape(p.WeekEnd))
            .Replace("{CARET_POS}", string.IsNullOrWhiteSpace(p.CaretPos) ? "0" : p.CaretPos)
            .Replace("{BUTTON_ID}", VbsEscape(p.ButtonId));

        string tmpFile = Path.Combine(Path.GetTempPath(), $"sap_rpa_{p.TCode}_{Guid.NewGuid():N}.vbs");
        bool keepTempFile = false;
        try
        {
            File.WriteAllText(tmpFile, vbsScript, Encoding.Default);
            Log($"执行 VBS: {tmpFile}");

            var psi = new ProcessStartInfo("cscript.exe", $"//T:35 //nologo \"{tmpFile}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(38_000);
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

    static void RunSelfTest()
    {
        Console.WriteLine("=== SapWebLauncher 自测试 ===\n");
        int passed = 0, failed = 0;

        void Check(string name, bool ok, string detail)
        {
            Console.WriteLine($"[{name}] {(ok ? "PASS" : "FAIL")} - {detail}");
            if (ok) passed++; else failed++;
        }

        {
            string uri = "sap-rpa://run?action=run&tcode=ZFI019NL&system=Y4Q&client=630&user=MYUSER&pw=MYPASS&lang=ZH&sysnr=00";
            var q = ParseUri(uri);
            Check("新协议URI", q["action"] == "run" && q["tcode"] == "ZFI019NL" && q["system"] == "Y4Q" && q["client"] == "630", uri);
        }

        {
            string uri = "sap-zck://action=run&system=Fiori&client=400&user=UI5035&pw=fiori666";
            var q = ParseUri(uri);
            var p = BuildParams(q, LegacyProtocolName);
            Check("旧协议兼容", p.TCode.Equals("zck", StringComparison.OrdinalIgnoreCase) && p.Script == "zck", $"{p.TCode}/{p.Script}");
        }

        {
            string uri = "sap-rpa://run?payload=%7B%22tCode%22%3A%22ZFI019NL%22%2C%22plants%22%3A%5B%221022%22%2C%221024%22%5D%2C%22businessAreas%22%3A%5B%222900%22%2C%223960%22%5D%7D";
            var q = ParseUri(uri);
            MergePayload(q);
            var p = BuildParams(q, PrimaryProtocolName);
            Check("payload兼容", p.TCode == "ZFI019NL" && p.Plants == "1022,1024" && p.BusinessAreas == "2900,3960", $"tcode={p.TCode}, plants={p.Plants}, businessAreas={p.BusinessAreas}");
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
                .Replace("{PERIOD}", "2026-W23")
                .Replace("{WEEK_END}", "2026-06-07")
                .Replace("{CARET_POS}", "0")
                .Replace("{BUTTON_ID}", "");
            bool ok = result.Contains("ZFI019NL") && !result.Contains("{OK_CODE}") && !result.Contains("{PLANTS}") && result.Contains("transaction script executed");
            Check("VBS替换", ok, $"模板 {template.Length} 字节 -> {result.Length} 字节");
        }

        Console.WriteLine($"\n=== 总计: {passed} PASS, {failed} FAIL, {(failed == 0 ? "全部通过" : "有失败项")} ===");
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
        return $"tcode={p.TCode}, script={p.Script}, system={p.System}, client={p.Client}, user={p.User}, pw={MaskValue("pw", p.Password)}, lang={p.Language}, sysnr={p.SysNr}, plant={p.Plant}, plants={p.Plants}, period={p.Period}, businessArea={p.BusinessArea}, businessAreas={p.BusinessAreas}, weekEnd={p.WeekEnd}, factoryGroup={p.FactoryGroup}, runStrategy={p.RunStrategy}";
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
    public string System { get; set; } = "Fiori";
    public string Client { get; set; } = "400";
    public string User { get; set; } = "UI5035";
    public string Password { get; set; } = "fiori666";
    public string Language { get; set; } = "ZH";
    public string SysNr { get; set; } = "04";
    public string TCode { get; set; } = "ZFI019NL";
    public string Script { get; set; } = "openOnly";
    public string Plant { get; set; } = "";
    public string Plants { get; set; } = "";
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
