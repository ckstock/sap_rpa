using SAP.Middleware.Connector;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SapWebLauncher;

internal sealed class SapNcoConnectionConfig
{
    public string ConnectionName { get; set; } = "";
    public string ConnectionMode { get; set; } = "direct";
    public string SystemId { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string MessageServerHost { get; set; } = "";
    public string MessageServerService { get; set; } = "";
    public string LogonGroup { get; set; } = "";
    public string Client { get; set; } = "";
    public string Language { get; set; } = "ZH";
    public string SystemNumber { get; set; } = "";
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string Router { get; set; } = "";

    public bool IsComplete(out string message)
    {
        var missing = new List<string>();
        bool messageServer = string.Equals(ConnectionMode, "messageServer", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(ConnectionMode, "group", StringComparison.OrdinalIgnoreCase);
        if (messageServer)
        {
            if (string.IsNullOrWhiteSpace(MessageServerHost)) missing.Add("sapNco.messageServerHost");
            if (string.IsNullOrWhiteSpace(SystemId)) missing.Add("sapNco.systemId");
            if (string.IsNullOrWhiteSpace(LogonGroup)) missing.Add("sapNco.logonGroup");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(IpAddress)) missing.Add("sapNco.ipAddress/appServerHost");
            if (string.IsNullOrWhiteSpace(SystemNumber)) missing.Add("sapNco.systemNumber/sysNr");
        }
        if (string.IsNullOrWhiteSpace(Client)) missing.Add("client");
        if (string.IsNullOrWhiteSpace(User)) missing.Add("user");
        if (string.IsNullOrWhiteSpace(Password)) missing.Add("password/passwordProtected");

        message = missing.Count == 0 ? "" : "NCo connection config is incomplete: missing " + string.Join(", ", missing);
        return missing.Count == 0;
    }

    public string SafeSummary()
    {
        bool messageServer = string.Equals(ConnectionMode, "messageServer", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(ConnectionMode, "group", StringComparison.OrdinalIgnoreCase);
        string target = messageServer
            ? $"messageServerHost={MessageServerHost}; messageServerService={MessageServerService}; logonGroup={LogonGroup}"
            : $"ipAddress={IpAddress}; systemNumber={SystemNumber}";
        return $"name={ConnectionName}; mode={(messageServer ? "messageServer" : "direct")}; systemId={SystemId}; {target}; client={Client}; user={Mask(User)}; language={Language}; router={(string.IsNullOrWhiteSpace(Router) ? "-" : "configured")}";
    }

    private static string Mask(string value)
    {
        value = (value ?? "").Trim();
        if (value.Length <= 2) return value.Length == 0 ? "-" : "***";
        return value[..1] + "***" + value[^1..];
    }
}

internal sealed class Zfi019NlFetchRequest
{
    public string Report { get; init; } = "ZFI019NL";
    public string Variant { get; init; } = "";
    public string MemoryId { get; init; } = "%ZFI019NA%";
    public string MemoryName { get; init; } = "GT_ALV";
    public string SpoolDevice { get; init; } = "LP01";
    public int WaitSeconds { get; init; } = 60;
    public string SplitTable { get; init; } = "ZFI_SPLIT";
    public string SplitBukrs { get; init; } = "2030";
    public List<Zfi019NlSelection> Conditions { get; init; } = new();
    public List<string> BusinessAreas { get; init; } = new();
    public List<string> SplitWerks { get; init; } = new();
}

internal sealed class Zfi019NlSelection
{
    public string Selname { get; init; } = "";
    public string Kind { get; init; } = "S";
    public string Sign { get; init; } = "I";
    public string Option { get; init; } = "EQ";
    public string Low { get; init; } = "";
    public string High { get; init; } = "";
}

internal sealed class Zfi019NlFetchResult
{
    public bool Success { get; init; }
    public int Subrc { get; init; }
    public string Message { get; init; } = "";
    public string ActualMethod { get; init; } = "";
    public string Options { get; init; } = "";
    public List<string> RawLines { get; init; } = new();
    public List<string> Headers { get; init; } = new();
    public List<Dictionary<string, string>> AlvRows { get; init; } = new();
    public List<Dictionary<string, string>> FinalRows { get; init; } = new();
    public List<Dictionary<string, string>> SplitRows { get; init; } = new();
    public int SplitMaterialCount { get; init; }
    public bool DongtaiOnly800 { get; init; }
}

internal sealed class SapJobStatusQuery
{
    public string JobName { get; init; } = "";
    public string JobUser { get; init; } = "";
    public DateTime LowerUtc { get; init; }
    public DateTime UpperUtc { get; init; }
}

internal sealed class SapJobStatusResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public string SqlSummary { get; init; } = "";
    public List<string> Options { get; init; } = new();
    public int RawRowCount { get; init; }
    public List<SapJobStatusRow> Rows { get; init; } = new();
    public SapJobStatusRow? Latest { get; init; }
}

internal sealed class SapJobStatusRow
{
    public string JobName { get; init; } = "";
    public string JobCount { get; init; } = "";
    public string User { get; init; } = "";
    public string Status { get; init; } = "";
    public string ScheduledDate { get; init; } = "";
    public string ScheduledTime { get; init; } = "";
    public string StartDate { get; init; } = "";
    public string StartTime { get; init; } = "";
    public string EndDate { get; init; } = "";
    public string EndTime { get; init; } = "";
    public DateTime? EffectiveStartLocal { get; init; }
    public DateTime? EndLocal { get; init; }
}

internal sealed class SapJobStatusFetcher
{
    private static readonly string[] TbtcoFields =
    {
        "JOBNAME", "JOBCOUNT", "SDLUNAME", "STATUS", "SDLSTRTDT", "SDLSTRTTM", "STRTDATE", "STRTTIME", "ENDDATE", "ENDTIME"
    };

    public SapJobStatusResult Fetch(SapNcoConnectionConfig connectionConfig, SapJobStatusQuery query)
    {
        ArgumentNullException.ThrowIfNull(connectionConfig);
        ArgumentNullException.ThrowIfNull(query);

        if (!connectionConfig.IsComplete(out string configError))
            return new SapJobStatusResult { Success = false, Message = configError, SqlSummary = BuildTbtcoSqlSummary(query), Options = BuildTbtcoWhereOptions(query) };

        try
        {
            var destination = SapRpaNcoDestinationProvider.GetDestination(connectionConfig);
            var function = destination.Repository.CreateFunction("RFC_READ_TABLE");
            function.SetValue("QUERY_TABLE", "TBTCO");
            function.SetValue("DELIMITER", "|");
            function.SetValue("ROWCOUNT", 0);

            var optionsText = BuildTbtcoWhereOptions(query);
            var options = function.GetTable("OPTIONS");
            foreach (string option in optionsText)
                AppendRfcReadOption(options, option);

            var fields = function.GetTable("FIELDS");
            foreach (string field in TbtcoFields)
            {
                fields.Append();
                fields.SetValue("FIELDNAME", field);
            }

            function.Invoke(destination);

            var data = function.GetTable("DATA");
            var layout = ReadRfcReadTableLayout(fields);
            var rows = new List<SapJobStatusRow>();
            for (var i = 0; i < data.RowCount; i++)
            {
                data.CurrentIndex = i;
                rows.Add(ParseTbtcoRow(SplitRfcReadTableRow(data.GetString("WA"), layout)));
            }

            DateTime lowerLocal = query.LowerUtc.ToLocalTime();
            DateTime upperLocal = query.UpperUtc.ToLocalTime();
            string user = Normalize(query.JobUser);
            string job = Normalize(query.JobName);
            var matchedRows = rows
                .Where(row => Normalize(row.JobName).Equals(job, StringComparison.OrdinalIgnoreCase))
                .Where(row => string.IsNullOrWhiteSpace(user) || Normalize(row.User).Equals(user, StringComparison.OrdinalIgnoreCase))
                .Where(row => row.EffectiveStartLocal.HasValue &&
                              row.EffectiveStartLocal.Value >= lowerLocal &&
                              row.EffectiveStartLocal.Value <= upperLocal)
                .OrderByDescending(row => row.EffectiveStartLocal ?? DateTime.MinValue)
                .ThenByDescending(row => row.JobCount, StringComparer.OrdinalIgnoreCase)
                .ToList();
            SapJobStatusRow? latest = matchedRows.FirstOrDefault();

            string message = latest == null
                ? $"No TBTCO job matched window; rawRows={rows.Count}; lower={FormatSapLocal(lowerLocal)}; upper={FormatSapLocal(upperLocal)}"
                : $"TBTCO matched {matchedRows.Count} job(s); latest {latest.JobName}/{latest.JobCount} status={latest.Status}; category={NormalizeTbtcoStatusCategory(latest.Status)}; start={FormatNullableLocal(latest.EffectiveStartLocal)}; end={FormatNullableLocal(latest.EndLocal)}";

            return new SapJobStatusResult
            {
                Success = true,
                Message = message,
                SqlSummary = BuildTbtcoSqlSummary(query),
                Options = optionsText,
                RawRowCount = rows.Count,
                Rows = matchedRows,
                Latest = latest
            };
        }
        catch (Exception ex)
        {
            return new SapJobStatusResult
            {
                Success = false,
                Message = $"TBTCO RFC_READ_TABLE failed: {ExceptionChain(ex)}",
                SqlSummary = BuildTbtcoSqlSummary(query),
                Options = BuildTbtcoWhereOptions(query)
            };
        }
    }

    // Kept for callers outside the workflow that only need the latest matched job.
    public SapJobStatusResult FetchLatest(SapNcoConnectionConfig connectionConfig, SapJobStatusQuery query)
        => Fetch(connectionConfig, query);

    public static List<string> BuildTbtcoWhereOptions(SapJobStatusQuery query)
    {
        var lowerLocal = query.LowerUtc.ToLocalTime();
        var upperLocal = query.UpperUtc.ToLocalTime();
        string lowerDate = lowerLocal.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        string lowerTime = lowerLocal.ToString("HHmmss", CultureInfo.InvariantCulture);
        string upperDate = upperLocal.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        string upperTime = upperLocal.ToString("HHmmss", CultureInfo.InvariantCulture);
        var options = new List<string>
        {
            $"JOBNAME = '{EscapeSqlLiteral(FirstNonEmpty(query.JobName, "ZFI057").Trim().ToUpperInvariant())}'"
        };
        if (!string.IsNullOrWhiteSpace(query.JobUser))
            options.Add($"AND SDLUNAME = '{EscapeSqlLiteral(query.JobUser.Trim().ToUpperInvariant())}'");
        options.Add($"AND ( SDLSTRTDT > '{lowerDate}'");
        options.Add($"OR ( SDLSTRTDT = '{lowerDate}' AND SDLSTRTTM >= '{lowerTime}' ) )");
        options.Add($"AND ( SDLSTRTDT < '{upperDate}'");
        options.Add($"OR ( SDLSTRTDT = '{upperDate}' AND SDLSTRTTM <= '{upperTime}' ) )");
        return options;
    }

    public static string BuildTbtcoSqlSummary(SapJobStatusQuery query)
    {
        var lowerLocal = query.LowerUtc.ToLocalTime();
        var upperLocal = query.UpperUtc.ToLocalTime();
        string lowerDate = lowerLocal.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        string lowerTime = lowerLocal.ToString("HHmmss", CultureInfo.InvariantCulture);
        string upperDate = upperLocal.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        string upperTime = upperLocal.ToString("HHmmss", CultureInfo.InvariantCulture);
        string userPredicate = string.IsNullOrWhiteSpace(query.JobUser)
            ? ""
            : $" AND SDLUNAME = '{EscapeSqlLiteral(query.JobUser.Trim().ToUpperInvariant())}'";
        return "SELECT JOBNAME, JOBCOUNT, SDLUNAME, STATUS, SDLSTRTDT, SDLSTRTTM, STRTDATE, STRTTIME, ENDDATE, ENDTIME " +
               "FROM TBTCO " +
               $"WHERE JOBNAME = '{EscapeSqlLiteral(FirstNonEmpty(query.JobName, "ZFI057").Trim().ToUpperInvariant())}'{userPredicate} " +
               $"AND (SDLSTRTDT > '{lowerDate}' OR (SDLSTRTDT = '{lowerDate}' AND SDLSTRTTM >= '{lowerTime}')) " +
               $"AND (SDLSTRTDT < '{upperDate}' OR (SDLSTRTDT = '{upperDate}' AND SDLSTRTTM <= '{upperTime}')) " +
                $"-- post-filter effective start between {FormatSapLocal(lowerLocal)} and {FormatSapLocal(upperLocal)}; all matched rows returned";
    }

    public static string NormalizeTbtcoStatusCategory(string status)
    {
        string value = Normalize(status);
        return value switch
        {
            "F" => "terminal_success",
            "A" or "C" => "terminal_failed",
            "R" or "Y" or "S" or "P" => "running",
            "" => "unknown",
            _ => "unknown"
        };
    }

    public static bool IsTbtcoTerminalStatus(string status)
    {
        string category = NormalizeTbtcoStatusCategory(status);
        return category.Equals("terminal_success", StringComparison.OrdinalIgnoreCase) ||
               category.Equals("terminal_failed", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTbtcoFailureStatus(string status)
        => NormalizeTbtcoStatusCategory(status).Equals("terminal_failed", StringComparison.OrdinalIgnoreCase);

    public static string DescribeTbtcoStatus(string status)
    {
        string value = Normalize(status);
        return value switch
        {
            "F" => "F(finished)",
            "A" => "A(cancelled)",
            "C" => "C(cancelled/completed)",
            "R" => "R(active)",
            "Y" => "Y(ready)",
            "S" => "S(released)",
            "P" => "P(scheduled)",
            "" => "unknown",
            _ => value
        };
    }

    private static SapJobStatusRow ParseTbtcoRow(Dictionary<string, string> values)
    {
        values.TryGetValue("JOBNAME", out string? jobName);
        values.TryGetValue("JOBCOUNT", out string? jobCount);
        values.TryGetValue("SDLUNAME", out string? user);
        values.TryGetValue("STATUS", out string? status);
        values.TryGetValue("SDLSTRTDT", out string? scheduledDate);
        values.TryGetValue("SDLSTRTTM", out string? scheduledTime);
        values.TryGetValue("STRTDATE", out string? startDate);
        values.TryGetValue("STRTTIME", out string? startTime);
        values.TryGetValue("ENDDATE", out string? endDate);
        values.TryGetValue("ENDTIME", out string? endTime);
        DateTime? actualStart = TryParseSapLocalDateTime(startDate, startTime);
        DateTime? scheduledStart = TryParseSapLocalDateTime(scheduledDate, scheduledTime);
        return new SapJobStatusRow
        {
            JobName = jobName?.Trim() ?? "",
            JobCount = jobCount?.Trim() ?? "",
            User = user?.Trim() ?? "",
            Status = status?.Trim() ?? "",
            ScheduledDate = scheduledDate?.Trim() ?? "",
            ScheduledTime = scheduledTime?.Trim() ?? "",
            StartDate = startDate?.Trim() ?? "",
            StartTime = startTime?.Trim() ?? "",
            EndDate = endDate?.Trim() ?? "",
            EndTime = endTime?.Trim() ?? "",
            EffectiveStartLocal = actualStart ?? scheduledStart,
            EndLocal = TryParseSapLocalDateTime(endDate, endTime)
        };
    }

    private static DateTime? TryParseSapLocalDateTime(string? dateValue, string? timeValue)
    {
        string date = DigitsOnly(dateValue);
        if (date.Length != 8 || date == "00000000") return null;
        string time = DigitsOnly(timeValue);
        if (time.Length == 0) time = "000000";
        if (time.Length > 6) time = time[^6..];
        time = time.PadLeft(6, '0');
        return DateTime.TryParseExact(date + time, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)
            ? result
            : null;
    }

    private static string DigitsOnly(string? value) => new((value ?? "").Where(char.IsDigit).ToArray());

    private static void AppendRfcReadOption(IRfcTable options, string text)
    {
        if (text.Length > 72) throw new ArgumentException($"RFC_READ_TABLE option is too long: {text}");
        options.Append();
        options.SetValue("TEXT", text);
    }

    private static List<(string Name, int Offset, int Length)> ReadRfcReadTableLayout(IRfcTable fields)
    {
        var result = new List<(string Name, int Offset, int Length)>();
        for (var i = 0; i < fields.RowCount; i++)
        {
            fields.CurrentIndex = i;
            result.Add((fields.GetString("FIELDNAME").Trim().ToUpperInvariant(), fields.GetInt("OFFSET"), fields.GetInt("LENGTH")));
        }
        return result;
    }

    private static Dictionary<string, string> SplitRfcReadTableRow(string row, IReadOnlyList<(string Name, int Offset, int Length)> layout)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in layout)
        {
            if (field.Offset >= row.Length)
            {
                result[field.Name] = "";
                continue;
            }

            var length = Math.Min(field.Length, row.Length - field.Offset);
            result[field.Name] = row.Substring(field.Offset, length).Trim();
        }
        return result;
    }

    private static string Normalize(string? value) => (value ?? "").Trim().ToUpperInvariant();

    private static string EscapeSqlLiteral(string value) => value.Replace("'", "''");

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        return "";
    }

    private static string FormatSapLocal(DateTime value) => value.ToString("yyyyMMdd HHmmss", CultureInfo.InvariantCulture);

    private static string FormatNullableLocal(DateTime? value) => value.HasValue ? FormatSapLocal(value.Value) : "-";

    private static string ExceptionChain(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? current = ex; current != null; current = current.InnerException)
            parts.Add($"{current.GetType().Name}: {current.Message}");
        return string.Join(" -> ", parts);
    }
}

internal sealed class Zfi019NlMemoryFetcher
{
    public const string FinalMaterialColumn = "入库料号";
    public const string FinalSourceColumn = "来源";

    private const string SapApiGatewayFunctionName = "ZFI_SAP_API_GATEWAY";
    private const string ReportSource = "ZFI019NL";

    private static readonly string[] BusinessAreaNames = { "GSBER", "S_GSBER", "业务范围" };
    private static readonly string[] PostingDateNames = { "S_BUDAT", "BUDAT", "过账日期" };
    private static readonly string[] ProductMaterialNames = { "SMATNR", "所属成品" };
    private static readonly string[] InboundMaterialNames = { "MATNR", FinalMaterialColumn };
    private static readonly string[] SplitFields = { "BUKRS", "WERKS", "MATNR", "BEGDA", "ENDDA", "MTART" };

    public Zfi019NlFetchResult Fetch(SapNcoConnectionConfig connectionConfig, Zfi019NlFetchRequest request)
    {
        ArgumentNullException.ThrowIfNull(connectionConfig);
        ArgumentNullException.ThrowIfNull(request);

        if (!connectionConfig.IsComplete(out string configError))
            return Error(8, configError, "CONFIG", "");

        var report = string.IsNullOrWhiteSpace(request.Report) ? "ZFI019NL" : request.Report.Trim().ToUpperInvariant();
        var memoryId = string.IsNullOrWhiteSpace(request.MemoryId) ? "%ZFI019NA%" : request.MemoryId.Trim();
        var memoryName = string.IsNullOrWhiteSpace(request.MemoryName) ? "GT_ALV" : request.MemoryName.Trim().ToUpperInvariant();
        var options = "";

        try
        {
            options = BuildOptions(request, memoryId, memoryName);
            var input = JsonSerializer.Serialize(new
            {
                method = "MEMORY_EXPORT",
                report,
                variant = request.Variant?.Trim() ?? "",
                spool_device = string.IsNullOrWhiteSpace(request.SpoolDevice) ? "LP01" : request.SpoolDevice.Trim().ToUpperInvariant(),
                wait_seconds = request.WaitSeconds <= 0 ? 60 : request.WaitSeconds,
                adapter_class = "",
                options
            });

            var destination = SapRpaNcoDestinationProvider.GetDestination(connectionConfig);
            var function = destination.Repository.CreateFunction(SapApiGatewayFunctionName);
            function.SetValue("IV_ACTION", "REPORT_SUBMIT");
            function.SetValue("IV_JSON_IN", input);
            function.Invoke(destination);

            var outerSubrc = function.GetInt("EV_SUBRC");
            var outerMsg = function.GetString("EV_MSG") ?? "";
            var jsonOut = function.GetString("EV_JSON_OUT") ?? "";
            if (outerSubrc != 0)
                return Error(outerSubrc, $"ZFI_SAP_API_GATEWAY REPORT_SUBMIT failed: {outerMsg}", "REPORT_SUBMIT", options);

            if (string.IsNullOrWhiteSpace(jsonOut))
                return Error(8, "ZFI_SAP_API_GATEWAY returned empty EV_JSON_OUT.", "REPORT_SUBMIT", options);

            using var document = JsonDocument.Parse(jsonOut);
            var root = document.RootElement;
            var innerSubrc = ReadInt(root, "EV_SUBRC", "SUBRC", "subrc");
            var innerMsg = ReadString(root, "EV_MSG", "MSG", "MESSAGE") ?? outerMsg;
            var lines = ReadLines(root, out var hasLines, out var invalidLine);
            var actualMethod = FindMethod(lines) ?? ReadString(root, "ACTUAL_METHOD", "METHOD") ?? "MEMORY_EXPORT";
            if (innerSubrc != 0 || !hasLines || invalidLine)
            {
                var message = innerSubrc != 0
                    ? innerMsg
                    : !hasLines ? "REPORT_SUBMIT response is missing ET_LINES." : "REPORT_SUBMIT response contains an invalid ET_LINES item.";
                return new Zfi019NlFetchResult
                {
                    Subrc = innerSubrc == 0 ? 8 : innerSubrc,
                    Message = message,
                    ActualMethod = actualMethod,
                    Options = options,
                    RawLines = lines
                };
            }

            var table = ParseTable(lines);
            if (table.Headers.Count == 0 || table.Warnings.Count > 0)
            {
                return new Zfi019NlFetchResult
                {
                    Subrc = 4,
                    Message = "ALV 内表解析失败：" + string.Join(" ", table.Warnings),
                    ActualMethod = actualMethod,
                    Options = options,
                    RawLines = lines,
                    Headers = table.Headers
                };
            }

            var dongtai = ResolveDongtaiBusinessAreas(connectionConfig, request, table);
            if (!dongtai.Success)
                return Error(4, dongtai.Message, "RFC_READ_TABLE", options);

            var processed = FilterAndProject(table, request, dongtai.BusinessAreas);
            if (!processed.Reliable)
            {
                return new Zfi019NlFetchResult
                {
                    Subrc = 4,
                    Message = JoinMessages(dongtai.Message, processed.Message),
                    ActualMethod = actualMethod,
                    Options = options,
                    RawLines = lines,
                    Headers = table.Headers,
                    AlvRows = processed.AlvRows,
                    DongtaiOnly800 = dongtai.HasDongtai
                };
            }

            var finalRows = processed.FinalRows;
            var splitRows = new List<Dictionary<string, string>>();
            var split = AppendDongtaiSplitMaterials(destination, request, dongtai.HasDongtai, finalRows, splitRows);
            if (!split.Success)
            {
                return new Zfi019NlFetchResult
                {
                    Subrc = 4,
                    Message = split.Message,
                    ActualMethod = actualMethod,
                    Options = options,
                    RawLines = lines,
                    Headers = table.Headers,
                    AlvRows = processed.AlvRows,
                    FinalRows = finalRows,
                    SplitRows = splitRows,
                    SplitMaterialCount = splitRows.Count,
                    DongtaiOnly800 = dongtai.HasDongtai
                };
            }

            DeduplicateFinalRows(finalRows);
            return new Zfi019NlFetchResult
            {
                Success = finalRows.Count > 0,
                Subrc = finalRows.Count > 0 ? 0 : 4,
                Message = finalRows.Count > 0 ? JoinMessages(innerMsg, dongtai.Message, processed.Message, split.Message) : "没有可用的 MATNR/入库料号结果。",
                ActualMethod = actualMethod,
                Options = options,
                RawLines = lines,
                Headers = table.Headers,
                AlvRows = processed.AlvRows,
                FinalRows = finalRows,
                SplitRows = splitRows,
                SplitMaterialCount = splitRows.Count,
                DongtaiOnly800 = dongtai.HasDongtai
            };
        }
        catch (Exception ex)
        {
            return Error(8, $"SAP RFC 调用失败：{ExceptionChain(ex)}", "MEMORY_EXPORT", options);
        }
    }

    private static string BuildOptions(Zfi019NlFetchRequest request, string memoryId, string memoryName)
    {
        var tokens = new List<string> { $"MEMORY_ID={Check(memoryId)}", $"MEMORY_NAME={Check(memoryName)}" };
        var conditions = request.Conditions.ToList();
        if (!conditions.Any(x => IsBusinessArea(x.Selname)))
        {
            conditions.AddRange(request.BusinessAreas.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => new Zfi019NlSelection
            {
                Selname = "S_GSBER",
                Low = x.Trim(),
                Kind = "S",
                Sign = "I",
                Option = "EQ"
            }));
        }

        foreach (var condition in conditions)
        {
            var name = condition.Selname.Trim().ToUpperInvariant();
            var low = Check(condition.Low);
            var high = Check(condition.High);
            if (name.Length == 0 || low.Length == 0) continue;

            if (name.StartsWith("P_", StringComparison.OrdinalIgnoreCase) ||
                condition.Kind.Equals("P", StringComparison.OrdinalIgnoreCase))
            {
                tokens.Add($"{name}={low}");
                continue;
            }

            var sign = string.IsNullOrWhiteSpace(condition.Sign) ? "I" : condition.Sign.Trim().ToUpperInvariant();
            var option = string.IsNullOrWhiteSpace(condition.Option)
                ? high.Length == 0 ? "EQ" : "BT"
                : condition.Option.Trim().ToUpperInvariant();
            if (sign is not ("I" or "E")) throw new ArgumentException($"{name}.SIGN must be I or E.");
            if (option is not ("EQ" or "NE" or "BT" or "NB" or "CP" or "NP" or "GE" or "LE" or "GT" or "LT"))
                throw new ArgumentException($"Unsupported SAP option: {option}.");
            tokens.Add($"{name}={sign}:{option}:{low}:{high}");
        }

        return string.Join(';', tokens);
    }

    private static (List<string> Headers, List<string[]> Rows, List<string> Warnings) ParseTable(IEnumerable<string> rawLines)
    {
        var logicalLines = RebuildLongLines(rawLines, out var warnings);
        var headers = new List<string>();
        var rows = new List<string[]>();

        foreach (var line in logicalLines)
        {
            if (line.StartsWith("HEADER=", StringComparison.OrdinalIgnoreCase))
            {
                headers = line["HEADER=".Length..].Split('|').ToList();
            }
            else if (line.StartsWith("ROW=", StringComparison.OrdinalIgnoreCase))
            {
                var body = line["ROW=".Length..];
                var separator = body.IndexOf('|');
                if (separator < 0)
                {
                    warnings.Add("ROW 缺少分隔符。");
                    continue;
                }
                rows.Add(body[(separator + 1)..].Split('|'));
            }
        }

        if (headers.Count == 0) warnings.Add("没有 HEADER。");
        foreach (var row in rows)
        {
            if (row.Length != headers.Count) warnings.Add($"列数不一致 HEADER={headers.Count}, ROW={row.Length}。");
        }

        return (headers, rows, warnings);
    }

    private static List<string> RebuildLongLines(IEnumerable<string> rawLines, out List<string> warnings)
    {
        warnings = new List<string>();
        var result = new List<string>();
        StringBuilder? buffer = null;

        foreach (var raw in rawLines)
        {
            var line = raw ?? "";
            var marker = line.Trim();
            if (marker.Equals("LONG_BEGIN", StringComparison.OrdinalIgnoreCase))
            {
                buffer = new StringBuilder();
                continue;
            }
            if (marker.StartsWith("LONG_PART=", StringComparison.OrdinalIgnoreCase))
            {
                if (buffer == null)
                {
                    warnings.Add("LONG_PART 没有 LONG_BEGIN。");
                    buffer = new StringBuilder();
                }
                buffer.Append(line.TrimStart()["LONG_PART=".Length..]);
                continue;
            }
            if (marker.Equals("LONG_END", StringComparison.OrdinalIgnoreCase))
            {
                if (buffer == null) warnings.Add("LONG_END 没有 LONG_BEGIN。");
                else result.Add(buffer.ToString());
                buffer = null;
                continue;
            }
            if (buffer != null)
            {
                warnings.Add("LONG 块中出现非 LONG_PART 行。");
                buffer.Append(line);
            }
            else
            {
                result.Add(line);
            }
        }

        if (buffer != null) warnings.Add("LONG_BEGIN 未闭合。");
        return result;
    }

    private static (bool Reliable, string Message, List<Dictionary<string, string>> AlvRows, List<Dictionary<string, string>> FinalRows)
        FilterAndProject(
            (List<string> Headers, List<string[]> Rows, List<string> Warnings) table,
            Zfi019NlFetchRequest request,
            IReadOnlySet<string> dongtaiBusinessAreas)
    {
        var areaIndex = FindColumn(table.Headers, BusinessAreaNames);
        var inboundIndex = FindInboundMaterialColumn(table.Headers);
        var productIndex = FindProductMaterialColumn(table.Headers, inboundIndex);
        var requestedAreas = request.Conditions.Where(x => IsBusinessArea(x.Selname)).Select(x => Normalize(x.Low)).Where(x => x.Length > 0).ToList();
        if (requestedAreas.Count == 0) requestedAreas.AddRange(request.BusinessAreas.Select(Normalize).Where(x => x.Length > 0));
        var dongtaiRequested = requestedAreas.Any(dongtaiBusinessAreas.Contains);
        var normalRequested = requestedAreas.Any(x => !dongtaiBusinessAreas.Contains(x));
        var uncertainScope = request.Conditions.Where(x => IsBusinessArea(x.Selname)).Any(x => !IsExactIncluded(x));

        if (inboundIndex < 0) return (false, "没有 MATNR/入库料号字段。", new(), new());
        if (productIndex < 0 && (dongtaiRequested || (areaIndex >= 0 && table.Rows.Any(x => areaIndex < x.Length && dongtaiBusinessAreas.Contains(Normalize(x[areaIndex]))))))
            return (false, "东台业务范围缺少 SMATNR/所属成品字段。", new(), new());
        if (areaIndex < 0 && requestedAreas.Count == 0)
            return (false, "ALV 没有 GSBER，且没有显式 S_GSBER，无法确认变式中的业务范围。", new(), new());
        if (areaIndex < 0 && (uncertainScope || (dongtaiRequested && normalRequested)))
            return (false, "ALV 没有 GSBER，S_GSBER 范围不明确，拒绝猜测过滤结果。", new(), new());

        var alvRows = new List<Dictionary<string, string>>();
        var finalRows = new List<Dictionary<string, string>>();
        foreach (var values in table.Rows)
        {
            var keep = true;
            var area = areaIndex >= 0 ? Normalize(values[areaIndex]) : "";
            // The exported material list is built from MATNR (inboundIndex). The
            // ALV's SMATNR/product value may be 800* while MATNR is a 63* child
            // material, which must not enter the ZFI019NL_MEMORY source set.
            if (areaIndex >= 0 && dongtaiBusinessAreas.Contains(area)) keep = Normalize(values[inboundIndex]).StartsWith("800", StringComparison.OrdinalIgnoreCase);
            else if (areaIndex < 0 && dongtaiRequested) keep = Normalize(values[inboundIndex]).StartsWith("800", StringComparison.OrdinalIgnoreCase);
            if (!keep) continue;

            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < table.Headers.Count; i++)
            {
                var key = table.Headers[i];
                if (row.ContainsKey(key)) key = $"{key}_{i + 1}";
                row[key] = values[i];
            }
            alvRows.Add(row);
            AddFinalMaterial(finalRows, values[inboundIndex], ReportSource);
        }

        return (true, $"S_GSBER={string.Join(",", requestedAreas)};东台范围过滤={(dongtaiRequested ? "MATNR 800*（最终物料）" : "无")}", alvRows, finalRows);
    }

    private static (bool Success, string Message) AppendDongtaiSplitMaterials(
        RfcDestination destination,
        Zfi019NlFetchRequest request,
        bool isDongtai,
        List<Dictionary<string, string>> finalRows,
        List<Dictionary<string, string>> splitRows)
    {
        if (!isDongtai) return (true, "");

        var dateRanges = GetPostingDateRanges(request);
        if (!dateRanges.Success)
            return (false, dateRanges.Message);

        var tableName = string.IsNullOrWhiteSpace(request.SplitTable) ? "ZFI_SPLIT" : request.SplitTable.Trim().ToUpperInvariant();
        var bukrs = string.IsNullOrWhiteSpace(request.SplitBukrs) ? "2030" : request.SplitBukrs.Trim().ToUpperInvariant();
        var werksList = request.SplitWerks
            .Select(Normalize)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        try
        {
            var added = 0;
            foreach (var dateRange in dateRanges.Ranges)
            {
                foreach (string werks in werksList.Length == 0 ? new[] { "" } : werksList)
                {
                    var function = destination.Repository.CreateFunction("RFC_READ_TABLE");
                    function.SetValue("QUERY_TABLE", tableName);
                    function.SetValue("DELIMITER", "|");
                    function.SetValue("ROWCOUNT", 0);

                    var options = function.GetTable("OPTIONS");
                    string first = $"BUKRS = '{EscapeSqlLiteral(bukrs)}'";
                    if (!string.IsNullOrWhiteSpace(werks))
                        first += $" AND WERKS = '{EscapeSqlLiteral(werks)}'";
                    AppendRfcReadOption(options, first);
                    AppendRfcReadOption(options, $"AND BEGDA <= '{dateRange.High}'");
                    AppendRfcReadOption(options, $"AND ENDDA >= '{dateRange.Low}'");

                    var fields = function.GetTable("FIELDS");
                    foreach (var field in SplitFields)
                    {
                        fields.Append();
                        fields.SetValue("FIELDNAME", field);
                    }

                    function.Invoke(destination);

                    var data = function.GetTable("DATA");
                    var fieldLayout = ReadRfcReadTableLayout(fields);
                    for (var i = 0; i < data.RowCount; i++)
                    {
                        data.CurrentIndex = i;
                        var values = SplitRfcReadTableRow(data.GetString("WA"), fieldLayout);

                        var matnr = values.TryGetValue("MATNR", out var value) ? value : "";
                        var rowWerks = values.TryGetValue("WERKS", out var rowWerkValue) ? rowWerkValue : werks;
                        var source = $"{tableName}(BUKRS={bukrs},WERKS={rowWerks})";
                        if (AddFinalMaterial(splitRows, matnr, source))
                        {
                            AddFinalMaterial(finalRows, matnr, source);
                            added++;
                        }
                    }
                }
            }

            return (true, added > 0 ? $"{tableName} 追加物料 {added} 条。" : $"{tableName} 未找到匹配物料。");
        }
        catch (Exception ex)
        {
            return (false, $"{tableName} 读取失败：{ex.Message}");
        }
    }

    private sealed class DongtaiBusinessAreaResolution
    {
        public bool Success { get; init; }
        public string Message { get; init; } = "";
        public HashSet<string> BusinessAreas { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public bool HasDongtai => BusinessAreas.Count > 0;
    }

    private static DongtaiBusinessAreaResolution ResolveDongtaiBusinessAreas(
        SapNcoConnectionConfig connectionConfig,
        Zfi019NlFetchRequest request,
        (List<string> Headers, List<string[]> Rows, List<string> Warnings) table)
    {
        var businessAreas = new HashSet<string>(GetRequestedBusinessAreas(request), StringComparer.OrdinalIgnoreCase);
        var areaIndex = FindColumn(table.Headers, BusinessAreaNames);
        if (areaIndex >= 0)
        {
            foreach (var row in table.Rows)
            {
                if (areaIndex < row.Length)
                {
                    string area = Normalize(row[areaIndex]);
                    if (area.Length > 0) businessAreas.Add(area);
                }
            }
        }

        if (businessAreas.Count == 0)
        {
            return new DongtaiBusinessAreaResolution
            {
                Message = "无法从 S_GSBER 或 ZFI019NL ALV 识别业务范围，不能判定是否为东台范围。"
            };
        }

        var dongtaiAreas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var summaries = new List<string>();
        var mappingFetcher = new AlvOrganizationMappingFetcher();
        foreach (string businessArea in businessAreas.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            AlvOrganizationMappingResult mapping = mappingFetcher.Fetch(
                connectionConfig,
                AlvOrganizationMappingKind.BusinessArea,
                businessArea);
            if (!mapping.Success)
            {
                return new DongtaiBusinessAreaResolution
                {
                    Message = $"东台范围判定失败：{mapping.Message}"
                };
            }

            bool isDongtai = mapping.ZsbuValues.Any(IsDongtaiZsbuDescription);
            if (isDongtai) dongtaiAreas.Add(businessArea);
            summaries.Add($"{businessArea}={(isDongtai ? "东台" : "非东台")}");
        }

        return new DongtaiBusinessAreaResolution
        {
            Success = true,
            Message = $"ZTFI48A 东台判定：{string.Join(",", summaries)}",
            BusinessAreas = dongtaiAreas
        };
    }

    internal static bool IsDongtaiZsbuDescription(string? value)
        => (value ?? "").Contains("东台", StringComparison.Ordinal);

    private static List<string> GetRequestedBusinessAreas(Zfi019NlFetchRequest request)
    {
        var businessAreaConditions = request.Conditions.Where(x => IsBusinessArea(x.Selname)).ToList();
        var result = businessAreaConditions
            .Where(IsExactIncluded)
            .Select(x => Normalize(x.Low))
            .Where(x => x.Length > 0)
            .ToList();
        if (businessAreaConditions.Count == 0)
            result.AddRange(request.BusinessAreas.Select(Normalize).Where(x => x.Length > 0));
        return result;
    }

    private static (bool Success, string Message, List<(string Low, string High)> Ranges) GetPostingDateRanges(Zfi019NlFetchRequest request)
    {
        var ranges = new List<(DateTime Low, DateTime High)>();
        foreach (var condition in request.Conditions.Where(x => PostingDateNames.Any(name => Normalize(name).Equals(Normalize(x.Selname), StringComparison.OrdinalIgnoreCase))))
        {
            if (!condition.Sign.Equals("I", StringComparison.OrdinalIgnoreCase))
                return (false, "东台 ZFI_SPLIT 读取遇到 S_BUDAT/BUDAT 排除条件，请先拆成明确 include 日期范围。", new());
            if (!TryNormalizeSapDate(condition.Low, out var lowDate)) continue;

            var option = string.IsNullOrWhiteSpace(condition.Option)
                ? string.IsNullOrWhiteSpace(condition.High) ? "EQ" : "BT"
                : condition.Option.Trim().ToUpperInvariant();
            DateTime low;
            DateTime high;
            switch (option)
            {
                case "EQ":
                    low = high = lowDate;
                    break;
                case "BT":
                    if (!TryNormalizeSapDate(condition.High, out high)) continue;
                    low = lowDate <= high ? lowDate : high;
                    high = lowDate <= high ? high : lowDate;
                    break;
                case "GE":
                    low = lowDate;
                    high = new DateTime(9999, 12, 31);
                    break;
                case "GT":
                    low = lowDate.AddDays(1);
                    high = new DateTime(9999, 12, 31);
                    break;
                case "LE":
                    low = new DateTime(1, 1, 1);
                    high = lowDate;
                    break;
                case "LT":
                    low = new DateTime(1, 1, 1);
                    high = lowDate.AddDays(-1);
                    break;
                default:
                    continue;
            }

            ranges.Add((low, high));
        }

        if (ranges.Count == 0)
            return (false, "东台业务范围需要 S_BUDAT/BUDAT 日期范围，才能按相同查询范围读取 ZFI_SPLIT。", new());

        var sapRanges = ranges
            .Select(x => (x.Low.ToString("yyyyMMdd", CultureInfo.InvariantCulture), x.High.ToString("yyyyMMdd", CultureInfo.InvariantCulture)))
            .Distinct()
            .ToList();
        return (true, "", sapRanges);
    }

    private static bool TryNormalizeSapDate(string? value, out DateTime date)
    {
        var digits = new string((value ?? "").Where(char.IsDigit).ToArray());
        return DateTime.TryParseExact(digits, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static bool AddFinalMaterial(List<Dictionary<string, string>> rows, string? material, string source)
    {
        var value = material?.Trim() ?? "";
        if (value.Length == 0) return false;
        rows.Add(new Dictionary<string, string>
        {
            [FinalMaterialColumn] = value,
            [FinalSourceColumn] = source
        });
        return true;
    }

    private static void DeduplicateFinalRows(List<Dictionary<string, string>> rows)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var writeIndex = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            string material = rows[i].TryGetValue(FinalMaterialColumn, out string? value) ? value.Trim() : "";
            if (material.Length == 0 || !seen.Add(material)) continue;
            rows[writeIndex++] = rows[i];
        }
        if (writeIndex < rows.Count)
            rows.RemoveRange(writeIndex, rows.Count - writeIndex);
    }

    private static void AppendRfcReadOption(IRfcTable options, string text)
    {
        if (text.Length > 72) throw new ArgumentException($"RFC_READ_TABLE option is too long: {text}");
        options.Append();
        options.SetValue("TEXT", text);
    }

    private static List<(string Name, int Offset, int Length)> ReadRfcReadTableLayout(IRfcTable fields)
    {
        var result = new List<(string Name, int Offset, int Length)>();
        for (var i = 0; i < fields.RowCount; i++)
        {
            fields.CurrentIndex = i;
            result.Add((
                fields.GetString("FIELDNAME").Trim().ToUpperInvariant(),
                fields.GetInt("OFFSET"),
                fields.GetInt("LENGTH")));
        }
        return result;
    }

    private static Dictionary<string, string> SplitRfcReadTableRow(string row, IReadOnlyList<(string Name, int Offset, int Length)> layout)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in layout)
        {
            if (field.Offset >= row.Length)
            {
                result[field.Name] = "";
                continue;
            }

            var length = Math.Min(field.Length, row.Length - field.Offset);
            result[field.Name] = row.Substring(field.Offset, length).Trim();
        }
        return result;
    }

    private static string EscapeSqlLiteral(string value) => value.Replace("'", "''");

    private static string JoinMessages(params string?[] messages)
        => string.Join(" ", messages.Select(x => x?.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)));

    private static Zfi019NlFetchResult Error(int subrc, string message, string method, string options)
        => new() { Subrc = subrc, Message = message, ActualMethod = method, Options = options };

    private static string ExceptionChain(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? current = ex; current != null; current = current.InnerException)
            parts.Add($"{current.GetType().Name}: {current.Message}");
        return string.Join(" -> ", parts);
    }

    private static int FindColumn(IReadOnlyList<string> headers, IEnumerable<string> aliases)
    {
        foreach (var alias in aliases)
            for (var i = 0; i < headers.Count; i++)
                if (Normalize(headers[i]).Equals(Normalize(alias), StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static int FindInboundMaterialColumn(IReadOnlyList<string> headers)
    {
        int index = FindColumn(headers, InboundMaterialNames);
        return index >= 0 ? index : FindNthColumn(headers, "物料", 0);
    }

    private static int FindProductMaterialColumn(IReadOnlyList<string> headers, int inboundIndex)
    {
        int index = FindColumn(headers, ProductMaterialNames);
        if (index >= 0) return index;

        int secondMaterial = FindNthColumn(headers, "物料", 1);
        if (secondMaterial >= 0) return secondMaterial;

        return inboundIndex;
    }

    private static int FindNthColumn(IReadOnlyList<string> headers, string alias, int occurrence)
    {
        int seen = 0;
        for (int i = 0; i < headers.Count; i++)
        {
            if (!Normalize(headers[i]).Equals(Normalize(alias), StringComparison.OrdinalIgnoreCase))
                continue;

            if (seen == occurrence)
                return i;
            seen++;
        }
        return -1;
    }

    private static bool IsBusinessArea(string name) => BusinessAreaNames.Any(x => Normalize(x).Equals(Normalize(name), StringComparison.OrdinalIgnoreCase));

    private static bool IsExactIncluded(Zfi019NlSelection x)
        => x.Sign.Equals("I", StringComparison.OrdinalIgnoreCase) &&
           x.Option.Equals("EQ", StringComparison.OrdinalIgnoreCase) &&
           string.IsNullOrWhiteSpace(x.High);

    private static string Normalize(string? value) => (value ?? "").Trim().Replace(" ", "").ToUpperInvariant();

    private static string Check(string? value)
    {
        var result = value?.Trim() ?? "";
        if (result.Contains(';') || result.Contains('=') || result.Contains('\r') || result.Contains('\n'))
            throw new ArgumentException("SAP options value contains a reserved delimiter.");
        return result;
    }

    private static int ReadInt(JsonElement root, params string[] names)
    {
        if (!TryGet(root, out var token, names)) return 8;
        if (token.TryGetInt32(out var value)) return value;
        return int.TryParse(token.ToString(), out value) ? value : 8;
    }

    private static string? ReadString(JsonElement root, params string[] names)
        => TryGet(root, out var token, names) ? token.ToString() : null;

    private static bool TryGet(JsonElement root, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
            if (root.TryGetProperty(name, out value)) return true;
        foreach (var property in root.EnumerateObject())
            if (names.Any(x => property.Name.Equals(x, StringComparison.OrdinalIgnoreCase)))
            {
                value = property.Value;
                return true;
            }
        value = default;
        return false;
    }

    private static List<string> ReadLines(JsonElement root, out bool hasLines, out bool invalidItem)
    {
        hasLines = TryGet(root, out var token, "ET_LINES", "LINES");
        invalidItem = false;
        if (!hasLines || token.ValueKind != JsonValueKind.Array) return new();
        var lines = new List<string>();
        foreach (var item in token.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String) lines.Add(item.GetString() ?? "");
            else if (item.ValueKind == JsonValueKind.Object && TryGet(item, out var line, "LINE")) lines.Add(line.ToString());
            else invalidItem = true;
        }
        return lines;
    }

    private static string? FindMethod(IEnumerable<string> lines)
    {
        string? method = null;
        foreach (var line in lines)
        {
            if (!line.StartsWith("METHOD=", StringComparison.OrdinalIgnoreCase)) continue;
            var value = line["METHOD=".Length..].Trim().ToUpperInvariant();
            if (value is "MEMORY_EXPORT" or "ADAPTER" or "BACKGROUND_SPOOL" or "ALV_RUNTIME" or "LIST_MEMORY") method = value;
        }
        return method;
    }
}

internal sealed class SapRpaNcoDestinationConfiguration : IDestinationConfiguration
{
    private readonly Dictionary<string, RfcConfigParameters> _destinations = new(StringComparer.OrdinalIgnoreCase);

    public void AddOrUpdateDestination(string name, RfcConfigParameters parameters)
    {
        lock (_destinations)
            _destinations[name] = parameters;
    }

    public RfcConfigParameters GetParameters(string destinationName)
    {
        lock (_destinations)
            return _destinations.TryGetValue(destinationName, out var parameters) ? parameters : null!;
    }

    public bool ChangeEventsSupported() => false;

#pragma warning disable CS0067
    public event RfcDestinationManager.ConfigurationChangeHandler? ConfigurationChanged;
#pragma warning restore CS0067
}

internal static class SapRpaNcoDestinationProvider
{
    private static readonly object SyncRoot = new();
    private static readonly SapRpaNcoDestinationConfiguration Configuration = new();
    private static bool Registered;

    public static RfcDestination GetDestination(SapNcoConnectionConfig config)
    {
        string name = string.IsNullOrWhiteSpace(config.ConnectionName)
            ? $"SAP_RPA_{config.SystemId}_{config.Client}_{config.User}"
            : config.ConnectionName.Trim();

        var parameters = new RfcConfigParameters
        {
            { RfcConfigParameters.Name, name }
        };

        bool messageServer = string.Equals(config.ConnectionMode, "messageServer", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(config.ConnectionMode, "group", StringComparison.OrdinalIgnoreCase);
        if (messageServer)
        {
            if (!string.IsNullOrWhiteSpace(config.MessageServerHost))
                parameters.Add(RfcConfigParameters.MessageServerHost, config.MessageServerHost.Trim());
            if (!string.IsNullOrWhiteSpace(config.MessageServerService))
                parameters.Add(RfcConfigParameters.MessageServerService, config.MessageServerService.Trim());
            if (!string.IsNullOrWhiteSpace(config.SystemId))
                parameters.Add(RfcConfigParameters.SystemID, config.SystemId.Trim());
            if (!string.IsNullOrWhiteSpace(config.LogonGroup))
                parameters.Add(RfcConfigParameters.LogonGroup, config.LogonGroup.Trim());
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(config.IpAddress))
                parameters.Add(RfcConfigParameters.AppServerHost, config.IpAddress.Trim());
            if (!string.IsNullOrWhiteSpace(config.SystemNumber))
                parameters.Add(RfcConfigParameters.SystemNumber, config.SystemNumber.Trim());
            if (!string.IsNullOrWhiteSpace(config.SystemId))
                parameters.Add(RfcConfigParameters.SystemID, config.SystemId.Trim());
        }
        if (!string.IsNullOrWhiteSpace(config.User)) parameters.Add(RfcConfigParameters.User, config.User.Trim());
        if (!string.IsNullOrWhiteSpace(config.Password)) parameters.Add(RfcConfigParameters.Password, config.Password);
        if (!string.IsNullOrWhiteSpace(config.Client)) parameters.Add(RfcConfigParameters.Client, config.Client.Trim());
        if (!string.IsNullOrWhiteSpace(config.Language)) parameters.Add(RfcConfigParameters.Language, config.Language.Trim());
        if (!string.IsNullOrWhiteSpace(config.Router)) parameters.Add(RfcConfigParameters.SAPRouter, config.Router.Trim());

        lock (SyncRoot)
        {
            if (!Registered)
            {
                RfcDestinationManager.RegisterDestinationConfiguration(Configuration);
                Registered = true;
            }
            Configuration.AddOrUpdateDestination(name, parameters);
        }

        return RfcDestinationManager.GetDestination(name);
    }
}
