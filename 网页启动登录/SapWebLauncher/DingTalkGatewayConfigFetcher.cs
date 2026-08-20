using SAP.Middleware.Connector;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SapWebLauncher;

internal sealed class DingTalkGatewayConfigFetchResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public string BaseUrl { get; init; } = "";
}

// The DingTalk gateway URL is SAP configuration. This read-only RFC never changes SAP data.
internal sealed class DingTalkGatewayConfigFetcher
{
    internal const string TableName = "ZTPLM_CONFIG";
    internal const string ProgramField = "CPROG";
    internal const string UrlField = "ZURL";
    internal const string ProgramValue = "ZPP154";

    public DingTalkGatewayConfigFetchResult Fetch(SapNcoConnectionConfig connectionConfig)
    {
        ArgumentNullException.ThrowIfNull(connectionConfig);
        if (!connectionConfig.IsComplete(out string configError))
            return new DingTalkGatewayConfigFetchResult { Message = $"{TableName} lookup cannot start: {configError}" };

        try
        {
            var destination = SapRpaNcoDestinationProvider.GetDestination(connectionConfig);
            var function = destination.Repository.CreateFunction("RFC_READ_TABLE");
            function.SetValue("QUERY_TABLE", TableName);
            function.SetValue("DELIMITER", "|");
            function.SetValue("ROWCOUNT", 0);

            var options = function.GetTable("OPTIONS");
            options.Append();
            options.SetValue("TEXT", $"{ProgramField} = '{ProgramValue}'");

            var fields = function.GetTable("FIELDS");
            foreach (string fieldName in new[] { ProgramField, UrlField })
            {
                fields.Append();
                fields.SetValue("FIELDNAME", fieldName);
            }

            function.Invoke(destination);
            var rows = new List<string>();
            var data = function.GetTable("DATA");
            for (int row = 0; row < data.RowCount; row++)
            {
                data.CurrentIndex = row;
                rows.Add(data.GetString("WA") ?? "");
            }

            string[] urls = ExtractBaseUrlsForTest(rows);
            if (urls.Length == 1)
            {
                return new DingTalkGatewayConfigFetchResult
                {
                    Success = true,
                    BaseUrl = urls[0],
                    Message = $"{TableName} resolved {ProgramField}={ProgramValue}."
                };
            }

            return new DingTalkGatewayConfigFetchResult
            {
                Message = urls.Length == 0
                    ? $"{TableName} has no valid {UrlField} for {ProgramField}={ProgramValue}."
                    : $"{TableName} has {urls.Length} different {UrlField} values for {ProgramField}={ProgramValue}; a single gateway URL is required."
            };
        }
        catch (Exception ex)
        {
            return new DingTalkGatewayConfigFetchResult
            {
                Message = $"{TableName} RFC_READ_TABLE failed for {ProgramField}={ProgramValue}: {ex.Message}"
            };
        }
    }

    internal static string[] ExtractBaseUrlsForTest(IEnumerable<string> rows)
    {
        return rows
            .Select(row => (row ?? "").Split('|'))
            .Where(values => values.Length >= 2 && values[0].Trim().Equals(ProgramValue, StringComparison.OrdinalIgnoreCase))
            .Select(values => NormalizeBaseUrl(values[1]))
            .Where(url => url.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeBaseUrl(string value)
    {
        string url = (value ?? "").Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            !(uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
              uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            return "";

        return uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? uri.AbsoluteUri : uri.AbsoluteUri + "/";
    }
}
