using SAP.Middleware.Connector;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SapWebLauncher;

internal sealed class Zfi057BusinessAreaPlantFetchResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public string BusinessArea { get; init; } = "";
    public IReadOnlyList<string> Plants { get; init; } = Array.Empty<string>();
}

// ZFI057 reads this SAP mapping only. The query is RFC_READ_TABLE and never writes to SAP.
internal sealed class Zfi057BusinessAreaPlantFetcher
{
    internal const string TableName = "ZFIT_RPA_BUKRS";
    internal const string BusinessAreaField = "GSBER";
    internal const string PlantField = "WERKS";

    public Zfi057BusinessAreaPlantFetchResult Fetch(SapNcoConnectionConfig connectionConfig, string businessArea)
    {
        ArgumentNullException.ThrowIfNull(connectionConfig);

        string area = (businessArea ?? "").Trim();
        if (area.Length == 0)
        {
            return new Zfi057BusinessAreaPlantFetchResult
            {
                Success = false,
                Message = $"{TableName} lookup requires a GSBER business area."
            };
        }

        if (!connectionConfig.IsComplete(out string configError))
        {
            return new Zfi057BusinessAreaPlantFetchResult
            {
                Success = false,
                Message = $"{TableName} lookup cannot start: {configError}",
                BusinessArea = area
            };
        }

        try
        {
            var destination = SapRpaNcoDestinationProvider.GetDestination(connectionConfig);
            var function = destination.Repository.CreateFunction("RFC_READ_TABLE");
            function.SetValue("QUERY_TABLE", TableName);
            function.SetValue("DELIMITER", "|");
            function.SetValue("ROWCOUNT", 0);

            var options = function.GetTable("OPTIONS");
            options.Append();
            options.SetValue("TEXT", $"{BusinessAreaField} = '{EscapeSqlLiteral(area)}'");

            var fields = function.GetTable("FIELDS");
            foreach (string fieldName in new[] { BusinessAreaField, PlantField })
            {
                fields.Append();
                fields.SetValue("FIELDNAME", fieldName);
            }

            function.Invoke(destination);

            var data = function.GetTable("DATA");
            var rows = new List<string>();
            for (int row = 0; row < data.RowCount; row++)
            {
                data.CurrentIndex = row;
                rows.Add(data.GetString("WA") ?? "");
            }

            string[] plants = ExtractPlantsForBusinessAreaForTest(rows, area);
            return new Zfi057BusinessAreaPlantFetchResult
            {
                Success = true,
                BusinessArea = area,
                Plants = plants,
                Message = plants.Length > 0
                    ? $"{TableName} mapped {BusinessAreaField}={area} to {plants.Length} plant(s)."
                    : $"{TableName} has no executable {PlantField} mapping for {BusinessAreaField}={area}."
            };
        }
        catch (Exception ex)
        {
            return new Zfi057BusinessAreaPlantFetchResult
            {
                Success = false,
                BusinessArea = area,
                Message = $"{TableName} RFC_READ_TABLE failed for {BusinessAreaField}={area}: {ex.Message}"
            };
        }
    }

    internal static string[] ExtractPlantsForBusinessAreaForTest(IEnumerable<string> rows, string businessArea)
    {
        string area = (businessArea ?? "").Trim();
        return rows
            .Select(row => (row ?? "").Split('|'))
            .Where(values => values.Length >= 2 && values[0].Trim().Equals(area, StringComparison.OrdinalIgnoreCase))
            .Select(values => values[1].Trim())
            .Where(plant => plant.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string EscapeSqlLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
