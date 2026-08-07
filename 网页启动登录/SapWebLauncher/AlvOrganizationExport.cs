using ClosedXML.Excel;
using SAP.Middleware.Connector;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SapWebLauncher;

internal enum AlvOrganizationMappingKind
{
    Plant,
    BusinessArea
}

internal sealed record AlvOrganizationTarget(string Zbu, string Zsbu)
{
    public string Key => Zbu + "\u001f" + Zsbu;
}

internal sealed class AlvOrganizationMappingResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public IReadOnlyList<AlvOrganizationTarget> Targets { get; init; } = Array.Empty<AlvOrganizationTarget>();
}

// All SAP reads here are read-only RFC_READ_TABLE calls. No mapping data is written back to SAP.
internal sealed class AlvOrganizationMappingFetcher
{
    public AlvOrganizationMappingResult Fetch(SapNcoConnectionConfig connectionConfig, AlvOrganizationMappingKind kind, string sourceCode)
    {
        if (connectionConfig == null) throw new ArgumentNullException(nameof(connectionConfig));

        string sourceField = kind == AlvOrganizationMappingKind.Plant ? "WERKS" : "GSBER";
        string tableName = kind == AlvOrganizationMappingKind.Plant ? "ZTFI48B" : "ZTFI48A";
        string code = NormalizeSourceCode(sourceCode);
        if (code.Length == 0)
        {
            return new AlvOrganizationMappingResult
            {
                Success = true,
                Message = $"{tableName} lookup skipped because {sourceField} is empty."
            };
        }

        if (!connectionConfig.IsComplete(out string configError))
        {
            return new AlvOrganizationMappingResult
            {
                Success = false,
                Message = $"{tableName} lookup cannot start: {configError}"
            };
        }

        try
        {
            var destination = SapRpaNcoDestinationProvider.GetDestination(connectionConfig);
            var function = destination.Repository.CreateFunction("RFC_READ_TABLE");
            function.SetValue("QUERY_TABLE", tableName);
            function.SetValue("DELIMITER", "|");
            function.SetValue("ROWCOUNT", 0);

            var options = function.GetTable("OPTIONS");
            options.Append();
            options.SetValue("TEXT", $"{sourceField} = '{EscapeSqlLiteral(code)}'");

            var fields = function.GetTable("FIELDS");
            foreach (string fieldName in new[] { sourceField, "ZBU", "ZSBU" })
            {
                fields.Append();
                fields.SetValue("FIELDNAME", fieldName);
            }

            function.Invoke(destination);

            var targets = new List<AlvOrganizationTarget>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var data = function.GetTable("DATA");
            for (int row = 0; row < data.RowCount; row++)
            {
                data.CurrentIndex = row;
                string[] values = data.GetString("WA").Split('|');
                string zbu = values.Length > 1 ? values[1].Trim() : "";
                string zsbu = values.Length > 2 ? values[2].Trim() : "";
                if (zbu.Length == 0 || zsbu.Length == 0)
                    continue;

                var target = new AlvOrganizationTarget(zbu, zsbu);
                if (seen.Add(target.Key))
                    targets.Add(target);
            }

            return new AlvOrganizationMappingResult
            {
                Success = true,
                Message = targets.Count == 0
                    ? $"{tableName} has no ZBU/ZSBU mapping for {sourceField}={code}."
                    : $"{tableName} mapped {sourceField}={code} to {targets.Count} destination(s).",
                Targets = targets
            };
        }
        catch (Exception ex)
        {
            return new AlvOrganizationMappingResult
            {
                Success = false,
                Message = $"{tableName} RFC_READ_TABLE failed for {sourceField}={code}: {ex.Message}"
            };
        }
    }

    private static string NormalizeSourceCode(string value)
    {
        return Regex.Replace((value ?? "").Trim(), @"\s+", "");
    }

    private static string EscapeSqlLiteral(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }
}

internal static class AlvOrganizationExport
{
    private const string SourceKeyColumnName = "__SAP_RPA_SOURCE_KEY";
    private const string UnmappedOrganizationDirectoryName = "\u96C6\u91C7\u5DE5\u5382";

    public static IReadOnlyList<RunFile> RoutePlantWorkbook(
        string sourcePath,
        string outputRoot,
        string transactionCode,
        string transactionName,
        string plant,
        DateTime archiveDate,
        Func<string, AlvOrganizationMappingResult> lookup)
    {
        if (lookup == null) throw new ArgumentNullException(nameof(lookup));
        using var source = new XLWorkbook(sourcePath);
        var sheet = source.Worksheets.FirstOrDefault();
        var range = sheet?.RangeUsed();
        if (sheet == null || range == null)
            throw new InvalidOperationException($"ALV workbook is empty: {sourcePath}");

        var rows = range.RowsUsed().ToList();
        if (rows.Count <= 1)
        {
            DeleteSourceAfterSuccessfulRoute(sourcePath, outputRoot);
            return Array.Empty<RunFile>();
        }

        int columnCount = range.ColumnCount();
        var plantColumn = FindPlantColumn(rows, columnCount);
        if (plantColumn.RowIndex < 0 || plantColumn.ColumnIndex <= 0)
        {
            // Older scripts can export a single known plant without displaying WERKS in the ALV.
            // Retain that compatible path, but never use it when the workbook exposes plant values.
            string requestPlant = NormalizeSourceCode(plant);
            if (requestPlant.Length == 0)
                throw new InvalidOperationException($"ALV plant column was not found and request plant is empty: {sourcePath}");

            return RouteWorkbook(
                sourcePath,
                outputRoot,
                transactionCode,
                transactionName,
                archiveDate,
                sourceKey: $"{NormalizeTransactionCode(transactionCode)}|plant|{requestPlant}",
                sourceLabel: "\u5DE5\u5382",
                sourceCode: requestPlant,
                lookup);
        }

        var groupedRows = new Dictionary<string, List<IXLRangeRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Skip(plantColumn.RowIndex + 1))
        {
            string plantCode = NormalizeSourceCode(row.Cell(plantColumn.ColumnIndex).GetString());
            if (plantCode.Length == 0)
                throw new InvalidOperationException($"ALV row has no plant value: {sourcePath}");

            if (!groupedRows.TryGetValue(plantCode, out var group))
            {
                group = new List<IXLRangeRow>();
                groupedRows[plantCode] = group;
            }
            group.Add(row);
        }

        if (groupedRows.Count == 0)
            throw new InvalidOperationException($"ALV workbook has data rows but no plant values: {sourcePath}");

        var results = new Dictionary<string, RunFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groupedRows.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var mapping = lookup(group.Key);
            if (!mapping.Success)
                throw new InvalidOperationException(mapping.Message);

            string sourceKey = $"{NormalizeTransactionCode(transactionCode)}|plant|{group.Key}";
            string[] targetPaths = BuildTargetPaths(
                outputRoot,
                transactionCode,
                transactionName,
                archiveDate,
                "\u5DE5\u5382",
                group.Key,
                mapping.Targets).ToArray();
            RemoveSourceRowsFromPriorTargets(outputRoot, transactionCode, transactionName, archiveDate, sourceKey, targetPaths);

            foreach (string targetPath in targetPaths)
            {
                UpsertRows(targetPath, rows[plantColumn.RowIndex], group.Value, columnCount, sourceKey);
                results[targetPath] = BuildRunFile(targetPath);
            }
        }

        DeleteSourceAfterSuccessfulRoute(sourcePath, outputRoot);
        return results.Values.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IReadOnlyList<RunFile> RouteBusinessAreaWorkbook(
        string sourcePath,
        string outputRoot,
        string transactionCode,
        string transactionName,
        DateTime archiveDate,
        string sourcePlant,
        Func<string, AlvOrganizationMappingResult> lookup)
    {
        if (lookup == null) throw new ArgumentNullException(nameof(lookup));
        using var source = new XLWorkbook(sourcePath);
        var sheet = source.Worksheets.FirstOrDefault();
        var range = sheet?.RangeUsed();
        if (sheet == null || range == null)
            throw new InvalidOperationException($"ALV workbook is empty: {sourcePath}");

        var rows = range.RowsUsed().ToList();
        if (rows.Count <= 1)
        {
            DeleteSourceAfterSuccessfulRoute(sourcePath, outputRoot);
            return Array.Empty<RunFile>();
        }

        int columnCount = range.ColumnCount();
        var businessAreaColumn = FindBusinessAreaColumn(rows, columnCount);
        if (businessAreaColumn.RowIndex < 0 || businessAreaColumn.ColumnIndex <= 0)
            throw new InvalidOperationException($"ALV business-area column was not found: {sourcePath}");
        var plantColumn = FindPlantColumn(rows, columnCount);
        string requestPlant = NormalizeSourceCode(sourcePlant);

        var groupedRows = new Dictionary<string, List<IXLRangeRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Skip(businessAreaColumn.RowIndex + 1))
        {
            string businessArea = NormalizeSourceCode(row.Cell(businessAreaColumn.ColumnIndex).GetString());
            if (businessArea.Length == 0)
                throw new InvalidOperationException($"ALV row has no business-area value: {sourcePath}");

            if (!groupedRows.TryGetValue(businessArea, out var group))
            {
                group = new List<IXLRangeRow>();
                groupedRows[businessArea] = group;
            }
            group.Add(row);
        }

        if (groupedRows.Count == 0)
            throw new InvalidOperationException($"ALV workbook has data rows but no business-area values: {sourcePath}");

        var results = new Dictionary<string, RunFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groupedRows.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var mapping = lookup(group.Key);
            if (!mapping.Success)
                throw new InvalidOperationException(mapping.Message);

            var sourceGroups = new Dictionary<string, List<IXLRangeRow>>(StringComparer.OrdinalIgnoreCase);
            bool hasPlantColumn = plantColumn.RowIndex >= 0 && plantColumn.ColumnIndex > 0;
            foreach (var row in group.Value)
            {
                string sourceIdentity = hasPlantColumn
                    ? NormalizeSourceCode(row.Cell(plantColumn.ColumnIndex).GetString())
                    : requestPlant;
                if (sourceIdentity.Length == 0)
                    sourceIdentity = group.Key;

                if (!sourceGroups.TryGetValue(sourceIdentity, out var sourceRows))
                {
                    sourceRows = new List<IXLRangeRow>();
                    sourceGroups[sourceIdentity] = sourceRows;
                }
                sourceRows.Add(row);
            }

            foreach (var sourceGroup in sourceGroups.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                bool sourceIsPlant = hasPlantColumn || requestPlant.Length > 0;
                string sourceLabel = sourceIsPlant ? "\u5DE5\u5382" : "\u4E1A\u52A1\u8303\u56F4";
                string sourceKey = sourceIsPlant
                    ? $"{NormalizeTransactionCode(transactionCode)}|businessArea|{group.Key}|plant|{sourceGroup.Key}"
                    : $"{NormalizeTransactionCode(transactionCode)}|businessArea|{group.Key}";
                string legacySourceKey = $"{NormalizeTransactionCode(transactionCode)}|businessArea|{group.Key}";
                string[] targetPaths = BuildTargetPaths(
                    outputRoot,
                    transactionCode,
                    transactionName,
                    archiveDate,
                    sourceLabel,
                    sourceGroup.Key,
                    mapping.Targets).ToArray();
                RemoveSourceRowsFromPriorTargets(outputRoot, transactionCode, transactionName, archiveDate, sourceKey, targetPaths);
                RemoveLegacyBusinessAreaRowsFromCurrentWeek(
                    outputRoot,
                    transactionCode,
                    transactionName,
                    archiveDate,
                    legacySourceKey,
                    sourceGroup.Key);

                foreach (string targetPath in targetPaths)
                {
                    UpsertRows(targetPath, rows[businessAreaColumn.RowIndex], sourceGroup.Value, columnCount, sourceKey);
                    results[targetPath] = BuildRunFile(targetPath);
                }
            }
        }

        DeleteSourceAfterSuccessfulRoute(sourcePath, outputRoot);
        return results.Values.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string GetWeekFolderName(DateTime date)
    {
        int week = ISOWeek.GetWeekOfYear(date);
        int year = ISOWeek.GetYear(date);
        return $"{year.ToString(CultureInfo.InvariantCulture)}_WK{week.ToString("00", CultureInfo.InvariantCulture)}";
    }

    public static string GetAggregateFileName(string transactionCode, string transactionName, DateTime archiveDate)
    {
        string transactionPart = SafePathPart(string.Join("_", new[]
        {
            NormalizeTransactionCode(transactionCode),
            transactionName?.Trim() ?? ""
        }.Where(value => value.Length > 0)));
        int week = ISOWeek.GetWeekOfYear(archiveDate);
        return $"{transactionPart}_WK{week.ToString("00", CultureInfo.InvariantCulture)}.xlsx";
    }

    private static IReadOnlyList<RunFile> RouteWorkbook(
        string sourcePath,
        string outputRoot,
        string transactionCode,
        string transactionName,
        DateTime archiveDate,
        string sourceKey,
        string sourceLabel,
        string sourceCode,
        Func<string, AlvOrganizationMappingResult> lookup)
    {
        if (lookup == null) throw new ArgumentNullException(nameof(lookup));
        var mapping = lookup(sourceCode);
        if (!mapping.Success)
            throw new InvalidOperationException(mapping.Message);

        string[] targetPaths = BuildTargetPaths(
            outputRoot,
            transactionCode,
            transactionName,
            archiveDate,
            sourceLabel,
            sourceCode,
            mapping.Targets).ToArray();
        RemoveSourceRowsFromPriorTargets(outputRoot, transactionCode, transactionName, archiveDate, sourceKey, targetPaths);

        var result = new List<RunFile>();
        foreach (string targetPath in targetPaths)
        {
            UpsertWholeWorkbook(targetPath, sourcePath, sourceKey);
            result.Add(BuildRunFile(targetPath));
        }

        DeleteSourceAfterSuccessfulRoute(sourcePath, outputRoot);
        return result;
    }

    private static IEnumerable<string> BuildTargetPaths(
        string outputRoot,
        string transactionCode,
        string transactionName,
        DateTime archiveDate,
        string sourceLabel,
        string sourceCode,
        IReadOnlyList<AlvOrganizationTarget> targets)
    {
        string root = Path.GetFullPath(outputRoot);
        string aggregateFileName = GetAggregateFileName(transactionCode, transactionName, archiveDate);
        if (targets.Count == 0)
        {
            string fallbackFileName = Path.GetFileNameWithoutExtension(aggregateFileName) +
                "_" + SafePathPart(sourceLabel) + SafePathPart(sourceCode) + ".xlsx";
            yield return Path.Combine(root, UnmappedOrganizationDirectoryName, GetWeekFolderName(archiveDate), fallbackFileName);
            yield break;
        }

        foreach (var target in targets.OrderBy(item => item.Zbu, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Zsbu, StringComparer.OrdinalIgnoreCase))
        {
            string directory = Path.Combine(root, SafePathPart(target.Zbu), SafePathPart(target.Zsbu), GetWeekFolderName(archiveDate));
            yield return Path.Combine(directory, aggregateFileName);
        }
    }

    private static void RemoveSourceRowsFromPriorTargets(
        string outputRoot,
        string transactionCode,
        string transactionName,
        DateTime archiveDate,
        string sourceKey,
        IReadOnlyCollection<string> activeTargetPaths,
        params string[] additionalSourceKeys)
    {
        string root = Path.GetFullPath(outputRoot);
        if (!Directory.Exists(root))
            return;

        string aggregateFileName = GetAggregateFileName(transactionCode, transactionName, archiveDate);
        string aggregateFileStem = Path.GetFileNameWithoutExtension(aggregateFileName);
        string weekFolder = GetWeekFolderName(archiveDate);
        var activePaths = new HashSet<string>(
            activeTargetPaths.Select(Path.GetFullPath),
            StringComparer.OrdinalIgnoreCase);
        var sourceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sourceKey };
        foreach (string key in additionalSourceKeys)
        {
            if (!string.IsNullOrWhiteSpace(key))
                sourceKeys.Add(key);
        }

        foreach (string candidatePath in Directory.EnumerateFiles(root, "*.xlsx", SearchOption.AllDirectories))
        {
            string fullPath = Path.GetFullPath(candidatePath);
            if (activePaths.Contains(fullPath) ||
                !IsCurrentWeekAggregateCandidate(root, fullPath, weekFolder, aggregateFileName, aggregateFileStem))
                continue;

            RemoveSourceRowsFromWorkbook(fullPath, root, sourceKeys);
        }
    }

    private static bool IsCurrentWeekAggregateCandidate(
        string root,
        string candidatePath,
        string weekFolder,
        string aggregateFileName,
        string aggregateFileStem)
    {
        string fileName = Path.GetFileName(candidatePath);
        if (!fileName.Equals(aggregateFileName, StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileNameWithoutExtension(fileName).StartsWith(aggregateFileStem + "_", StringComparison.OrdinalIgnoreCase))
            return false;

        string relative = Path.GetRelativePath(root, candidatePath);
        return relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.Equals(weekFolder, StringComparison.OrdinalIgnoreCase));
    }

    private static void RemoveSourceRowsFromWorkbook(string targetPath, string outputRoot, ISet<string> sourceKeys)
    {
        using var existing = new XLWorkbook(targetPath);
        var sheet = existing.Worksheets.FirstOrDefault();
        var range = sheet?.RangeUsed();
        if (sheet == null || range == null)
            return;

        var rows = range.RowsUsed().ToList();
        if (rows.Count == 0)
            return;

        int sourceKeyColumn = FindSourceKeyColumn(rows[0], range.ColumnCount());
        if (sourceKeyColumn <= 0)
            return;

        int dataColumnCount = sourceKeyColumn - 1;
        var retainedRows = new List<(List<XLCellValue> Values, string SourceKey)>();
        bool removed = false;
        foreach (var row in rows.Skip(1))
        {
            string existingSourceKey = row.Cell(sourceKeyColumn).GetString().Trim();
            if (sourceKeys.Contains(existingSourceKey))
            {
                removed = true;
                continue;
            }

            retainedRows.Add((ReadRow(row, dataColumnCount), existingSourceKey));
        }

        if (!removed)
            return;

        if (retainedRows.Count == 0)
        {
            File.Delete(targetPath);
            DeleteEmptyOutputDirectories(Path.GetDirectoryName(targetPath), outputRoot);
            return;
        }

        WriteAggregateWorkbook(targetPath, ReadRow(rows[0], dataColumnCount), retainedRows);
    }

    private static void RemoveLegacyBusinessAreaRowsFromCurrentWeek(
        string outputRoot,
        string transactionCode,
        string transactionName,
        DateTime archiveDate,
        string legacySourceKey,
        string sourcePlant)
    {
        string root = Path.GetFullPath(outputRoot);
        if (!Directory.Exists(root) || string.IsNullOrWhiteSpace(legacySourceKey))
            return;

        string aggregateFileName = GetAggregateFileName(transactionCode, transactionName, archiveDate);
        string aggregateFileStem = Path.GetFileNameWithoutExtension(aggregateFileName);
        string weekFolder = GetWeekFolderName(archiveDate);
        foreach (string candidatePath in Directory.EnumerateFiles(root, "*.xlsx", SearchOption.AllDirectories))
        {
            string fullPath = Path.GetFullPath(candidatePath);
            if (IsCurrentWeekAggregateCandidate(root, fullPath, weekFolder, aggregateFileName, aggregateFileStem))
                RemoveLegacyBusinessAreaRowsFromWorkbook(fullPath, root, legacySourceKey, sourcePlant);
        }
    }

    private static void RemoveLegacyBusinessAreaRowsFromWorkbook(
        string targetPath,
        string outputRoot,
        string legacySourceKey,
        string sourcePlant)
    {
        using var existing = new XLWorkbook(targetPath);
        var sheet = existing.Worksheets.FirstOrDefault();
        var range = sheet?.RangeUsed();
        if (sheet == null || range == null)
            return;

        var rows = range.RowsUsed().ToList();
        if (rows.Count == 0)
            return;

        int sourceKeyColumn = FindSourceKeyColumn(rows[0], range.ColumnCount());
        if (sourceKeyColumn <= 0)
            return;

        int dataColumnCount = sourceKeyColumn - 1;
        int plantColumn = FindPlantColumn(rows, dataColumnCount).ColumnIndex;
        string normalizedPlant = NormalizeSourceCode(sourcePlant);
        var retainedRows = new List<(List<XLCellValue> Values, string SourceKey)>();
        bool removed = false;
        foreach (var row in rows.Skip(1))
        {
            string existingSourceKey = row.Cell(sourceKeyColumn).GetString().Trim();
            bool isLegacySource = existingSourceKey.Equals(legacySourceKey, StringComparison.OrdinalIgnoreCase);
            bool isCurrentPlant = plantColumn <= 0 ||
                                  NormalizeSourceCode(row.Cell(plantColumn).GetString()).Equals(normalizedPlant, StringComparison.OrdinalIgnoreCase);
            if (isLegacySource && isCurrentPlant)
            {
                removed = true;
                continue;
            }

            retainedRows.Add((ReadRow(row, dataColumnCount), existingSourceKey));
        }

        if (!removed)
            return;

        if (retainedRows.Count == 0)
        {
            File.Delete(targetPath);
            DeleteEmptyOutputDirectories(Path.GetDirectoryName(targetPath), outputRoot);
            return;
        }

        WriteAggregateWorkbook(targetPath, ReadRow(rows[0], dataColumnCount), retainedRows);
    }

    private static void DeleteEmptyOutputDirectories(string? directoryPath, string outputRoot)
    {
        string root = Path.GetFullPath(outputRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? current = directoryPath;
        while (!string.IsNullOrWhiteSpace(current))
        {
            string fullCurrent = Path.GetFullPath(current).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (fullCurrent.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                !fullCurrent.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(fullCurrent) ||
                Directory.EnumerateFileSystemEntries(fullCurrent).Any())
                return;

            Directory.Delete(fullCurrent);
            current = Path.GetDirectoryName(fullCurrent);
        }
    }

    private static void UpsertWholeWorkbook(string targetPath, string sourcePath, string sourceKey)
    {
        using var source = new XLWorkbook(sourcePath);
        var sheet = source.Worksheets.FirstOrDefault();
        var range = sheet?.RangeUsed();
        if (sheet == null || range == null)
            throw new InvalidOperationException($"ALV workbook is empty: {sourcePath}");

        var rows = range.RowsUsed().ToList();
        if (rows.Count == 0)
            throw new InvalidOperationException($"ALV workbook has no rows: {sourcePath}");

        UpsertRows(targetPath, rows[0], rows.Skip(1).ToList(), range.ColumnCount(), sourceKey);
    }

    private static void UpsertRows(string targetPath, IXLRangeRow sourceHeader, IReadOnlyList<IXLRangeRow> incomingRows, int sourceColumnCount, string sourceKey)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? throw new InvalidOperationException("ALV target directory is missing."));
        var header = ReadRow(sourceHeader, sourceColumnCount);
        var retainedRows = new List<(List<XLCellValue> Values, string SourceKey)>();

        if (File.Exists(targetPath))
        {
            using var existing = new XLWorkbook(targetPath);
            var existingSheet = existing.Worksheets.FirstOrDefault();
            var existingRange = existingSheet?.RangeUsed();
            if (existingSheet != null && existingRange != null)
            {
                var existingRows = existingRange.RowsUsed().ToList();
                if (existingRows.Count > 0)
                {
                    int existingColumnCount = existingRange.ColumnCount();
                    int sourceKeyColumn = FindSourceKeyColumn(existingRows[0], existingColumnCount);
                    int comparableColumnCount = sourceKeyColumn > 0 ? sourceKeyColumn - 1 : existingColumnCount;
                    if (!HeadersMatch(header, ReadRow(existingRows[0], comparableColumnCount)))
                        throw new InvalidOperationException($"ALV headers differ from existing aggregate workbook: {targetPath}");

                    foreach (var row in existingRows.Skip(1))
                    {
                        string existingSourceKey = sourceKeyColumn > 0 ? row.Cell(sourceKeyColumn).GetString().Trim() : "legacy";
                        if (!existingSourceKey.Equals(sourceKey, StringComparison.OrdinalIgnoreCase))
                            retainedRows.Add((ReadRow(row, sourceColumnCount), existingSourceKey));
                    }
                }
            }
        }

        foreach (var row in incomingRows)
            retainedRows.Add((ReadRow(row, sourceColumnCount), sourceKey));

        WriteAggregateWorkbook(targetPath, header, retainedRows);
    }

    private static void WriteAggregateWorkbook(
        string targetPath,
        IReadOnlyList<XLCellValue> header,
        IReadOnlyList<(List<XLCellValue> Values, string SourceKey)> rows)
    {
        if (header.Count == 0)
            throw new InvalidOperationException($"ALV aggregate header is empty: {targetPath}");

        string targetDirectory = Path.GetDirectoryName(targetPath) ?? throw new InvalidOperationException("ALV target directory is missing.");
        Directory.CreateDirectory(targetDirectory);
        string tempPath = Path.Combine(
            targetDirectory,
            Path.GetFileNameWithoutExtension(targetPath) + ".write-" + Guid.NewGuid().ToString("N") + Path.GetExtension(targetPath));
        try
        {
            using var output = new XLWorkbook();
            var worksheet = output.Worksheets.Add("ALV");
            WriteRow(worksheet, 1, header);
            worksheet.Cell(1, header.Count + 1).Value = SourceKeyColumnName;
            worksheet.Column(header.Count + 1).Hide();

            int outputRow = 2;
            foreach (var row in rows)
            {
                WriteRow(worksheet, outputRow, row.Values);
                worksheet.Cell(outputRow, header.Count + 1).Value = row.SourceKey;
                outputRow++;
            }

            worksheet.SheetView.FreezeRows(1);
            output.Properties.Title = Path.GetFileNameWithoutExtension(targetPath);
            output.Properties.Subject = "SAP RPA ALV organizational export";
            output.SaveAs(tempPath);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static List<XLCellValue> ReadRow(IXLRangeRow row, int columnCount)
    {
        var values = new List<XLCellValue>(columnCount);
        for (int column = 1; column <= columnCount; column++)
            values.Add(row.Cell(column).Value);
        return values;
    }

    private static void WriteRow(IXLWorksheet target, int row, IReadOnlyList<XLCellValue> values)
    {
        for (int column = 0; column < values.Count; column++)
            target.Cell(row, column + 1).Value = values[column];
    }

    private static bool HeadersMatch(IReadOnlyList<XLCellValue> expected, IReadOnlyList<XLCellValue> actual)
    {
        if (expected.Count != actual.Count)
            return false;
        for (int index = 0; index < expected.Count; index++)
        {
            if (!expected[index].ToString().Trim().Equals(actual[index].ToString().Trim(), StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static int FindSourceKeyColumn(IXLRangeRow header, int columnCount)
    {
        for (int column = 1; column <= columnCount; column++)
        {
            if (header.Cell(column).GetString().Trim().Equals(SourceKeyColumnName, StringComparison.OrdinalIgnoreCase))
                return column;
        }
        return 0;
    }

    private static (int RowIndex, int ColumnIndex) FindBusinessAreaColumn(IReadOnlyList<IXLRangeRow> rows, int columnCount)
    {
        for (int row = 0; row < Math.Min(rows.Count, 10); row++)
        {
            for (int column = 1; column <= columnCount; column++)
            {
                string header = Regex.Replace(rows[row].Cell(column).GetString(), "[\\s\\u3000:\\uFF1A_-]+", "").Trim().ToUpperInvariant();
                if (header is "GSBER" or "BUSINESSAREA" or "BUSINESSAREACODE" or "\u4E1A\u52A1\u8303\u56F4" or "\u4E1A\u52A1\u8303\u56F4\u7F16\u7801")
                    return (row, column);
            }
        }
        return (-1, 0);
    }

    private static (int RowIndex, int ColumnIndex) FindPlantColumn(IReadOnlyList<IXLRangeRow> rows, int columnCount)
    {
        for (int row = 0; row < Math.Min(rows.Count, 10); row++)
        {
            for (int column = 1; column <= columnCount; column++)
            {
                string header = Regex.Replace(rows[row].Cell(column).GetString(), "[\\s\\u3000:\\uFF1A_-]+", "").Trim().ToUpperInvariant();
                if (header is "WERKS" or "WERK" or "PLANT" or "PLANTCODE" or "PLANTNO" or "PLANTNUMBER" or
                    "FACTORY" or "FACTORYCODE" or "BIGBUFACTORY" or "\u5DE5\u5382" or "\u5DE5\u5382\u53F7" or
                    "\u5DE5\u5382\u4EE3\u7801" or "\u5DE5\u5382\u7F16\u7801" or "\u5C0F\u5382" or "\u5927BU\u5DE5\u5382" or
                    "\u4E1A\u52A1\u8303\u56F4\u5C0F\u5382" or "\u751F\u4EA7\u5DE5\u5382")
                    return (row, column);
            }
        }
        return (-1, 0);
    }

    private static RunFile BuildRunFile(string path)
    {
        var info = new FileInfo(path);
        return new RunFile
        {
            Type = "output",
            Name = info.Name,
            Path = info.FullName,
            Size = info.Exists ? info.Length : 0,
            CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        };
    }

    private static void DeleteSourceAfterSuccessfulRoute(string sourcePath, string outputRoot)
    {
        if (File.Exists(sourcePath))
            File.Delete(sourcePath);

        string root = Path.GetFullPath(outputRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? current = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
        while (!string.IsNullOrWhiteSpace(current) &&
               current.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.EnumerateFileSystemEntries(current).Any())
                break;

            Directory.Delete(current);
            current = Path.GetDirectoryName(current);
        }
    }

    private static string NormalizeTransactionCode(string value)
    {
        return Regex.Replace((value ?? "").Trim().ToUpperInvariant(), "[^A-Z0-9_-]+", "");
    }

    private static string NormalizeSourceCode(string value)
    {
        return Regex.Replace((value ?? "").Trim(), @"\s+", "");
    }

    private static string SafePathPart(string value)
    {
        string text = (value ?? "").Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
            text = text.Replace(invalid, '_');
        text = Regex.Replace(text, @"\s+", "_").Trim('_', '.');
        return text.Length == 0 ? "na" : text;
    }
}
