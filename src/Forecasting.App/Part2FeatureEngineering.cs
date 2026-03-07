using System.Globalization;
using System.Text.Json;

namespace Forecasting.App;

public sealed record Part2SupervisedRow(
    DateTime AnchorUtcTime,
    double TargetAtT,
    double Temperature,
    double Windspeed,
    double SolarIrradiation,
    int HourOfDay,
    int MinuteOfHour,
    int DayOfWeek,
    bool IsHoliday,
    double HourSin,
    double HourCos,
    double WeekdaySin,
    double WeekdayCos,
    double TargetLag192,
    double TargetLag672,
    double TargetMean16,
    double TargetStd16,
    double TargetMean96,
    double TargetStd96,
    string Split,
    IReadOnlyList<double> HorizonTargets);

public sealed record Part2DatasetSummary(
    DateTime ValidationStartUtc,
    int HorizonSteps,
    int InputRowsBeforeDeduplication,
    int TotalRows,
    int DroppedDuplicateTimestampRows,
    int CandidateAnchors,
    int PurgedAnchors,
    int TrainAnchors,
    int ValidationAnchors,
    int OutputRows);

public sealed record Part2Dataset(
    IReadOnlyList<Part2SupervisedRow> Rows,
    Part2DatasetSummary Summary);

public static class Part2FeatureEngineering
{
    private static readonly CultureInfo InvariantCulture = CultureInfo.InvariantCulture;

    public static IReadOnlyList<FeatureRow> ReadFeatureMatrixCsv(string featureMatrixCsvPath)
    {
        var rows = new List<FeatureRow>();
        using var reader = new StreamReader(featureMatrixCsvPath);

        _ = reader.ReadLine();
        string? line;
        var lineNumber = 1;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split(';');
            if (parts.Length < 13)
            {
                throw new FormatException($"Invalid row at line {lineNumber}: expected at least 13 columns.");
            }

            if (!DateTime.TryParseExact(
                    parts[0],
                    "yyyy-MM-dd HH:mm:ss",
                    InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var utcTime))
            {
                throw new FormatException($"Invalid utcTime at line {lineNumber}: '{parts[0]}'.");
            }

            rows.Add(new FeatureRow(
                utcTime,
                ParseRequiredDouble(parts[1], lineNumber, "Target"),
                ParseRequiredDouble(parts[2], lineNumber, "Temperature"),
                ParseRequiredDouble(parts[3], lineNumber, "Windspeed"),
                ParseRequiredDouble(parts[4], lineNumber, "SolarIrradiation"),
                ParseRequiredInt(parts[5], lineNumber, "HourOfDay"),
                ParseRequiredInt(parts[6], lineNumber, "MinuteOfHour"),
                ParseRequiredInt(parts[7], lineNumber, "DayOfWeek"),
                ParseRequiredBool(parts[8], lineNumber, "IsHoliday"),
                ParseRequiredDouble(parts[9], lineNumber, "HourSin"),
                ParseRequiredDouble(parts[10], lineNumber, "HourCos"),
                ParseRequiredDouble(parts[11], lineNumber, "WeekdaySin"),
                ParseRequiredDouble(parts[12], lineNumber, "WeekdayCos")));
        }

        return rows;
    }

    public static Part2Dataset BuildDataset(IReadOnlyList<FeatureRow> inputRows, DateTime? validationStartUtc = null)
    {
        var sorted = inputRows.OrderBy(row => row.UtcTime).ToList();
        var inputRowsBeforeDeduplication = sorted.Count;
        sorted = DeduplicateByUtcTimeKeepLast(sorted, out var droppedDuplicateTimestampRows);

        if (sorted.Count == 0)
        {
            return new Part2Dataset([], new Part2DatasetSummary(DateTime.MinValue, PipelineConstants.HorizonSteps, 0, 0, 0, 0, 0, 0, 0, 0));
        }

        var effectiveValidationStart = validationStartUtc ?? sorted[^1].UtcTime.AddDays(-PipelineConstants.DefaultValidationWindowDays);

        // This gate only ensures enough history exists to compute lag/rolling features.
        // Split safety/leakage control is handled later by horizon-based train/validation eligibility and purge.
        var maxLookback = new[] { FeatureConfig.TargetLag192, FeatureConfig.TargetLag672, FeatureConfig.RollingWindow16, FeatureConfig.RollingWindow96 }.Max();

        var lastAnchorIndex = sorted.Count - 1 - PipelineConstants.HorizonSteps;
        if (lastAnchorIndex < maxLookback)
        {
            return new Part2Dataset([], new Part2DatasetSummary(
                effectiveValidationStart,
                PipelineConstants.HorizonSteps,
                inputRowsBeforeDeduplication,
                sorted.Count,
                droppedDuplicateTimestampRows,
                0,
                0,
                0,
                0,
                0));
        }

        var result = new List<Part2SupervisedRow>();
        var candidateAnchors = 0;
        var purgedAnchors = 0;
        var trainAnchors = 0;
        var validationAnchors = 0;

        for (var anchorIndex = maxLookback; anchorIndex <= lastAnchorIndex; anchorIndex++)
        {
            candidateAnchors++;

            var anchor = sorted[anchorIndex];
            var horizonEndUtc = sorted[anchorIndex + PipelineConstants.HorizonSteps].UtcTime;
            // Non-obvious but intentional: each anchor at time t maps to one full label vector
            // y(t+1..t+H), so split eligibility must consider horizon end, not anchor time alone.
            var isTrain = horizonEndUtc < effectiveValidationStart;
            var isValidation = anchor.UtcTime >= effectiveValidationStart;

            if (!isTrain && !isValidation)
            {
                // Non-obvious but intentional: purge boundary anchors whose future labels would overlap
                // validation, to prevent cross-split leakage in multi-step supervision.
                purgedAnchors++;
                continue;
            }

            var lagValue192 = sorted[anchorIndex - FeatureConfig.TargetLag192].Target;
            var lagValue672 = sorted[anchorIndex - FeatureConfig.TargetLag672].Target;
            var mean16 = CalculateMean(sorted, anchorIndex - (FeatureConfig.RollingWindow16 - 1), anchorIndex);
            var std16 = CalculatePopulationStd(sorted, anchorIndex - (FeatureConfig.RollingWindow16 - 1), anchorIndex, mean16);
            var mean96 = CalculateMean(sorted, anchorIndex - (FeatureConfig.RollingWindow96 - 1), anchorIndex);
            var std96 = CalculatePopulationStd(sorted, anchorIndex - (FeatureConfig.RollingWindow96 - 1), anchorIndex, mean96);

            var horizonTargets = new double[PipelineConstants.HorizonSteps];
            for (var step = 1; step <= PipelineConstants.HorizonSteps; step++)
            {
                horizonTargets[step - 1] = sorted[anchorIndex + step].Target;
            }

            var split = isTrain ? "Train" : "Validation";
            if (isTrain)
            {
                trainAnchors++;
            }
            else
            {
                validationAnchors++;
            }

            result.Add(new Part2SupervisedRow(
                anchor.UtcTime,
                anchor.Target,
                anchor.Temperature,
                anchor.Windspeed,
                anchor.SolarIrradiation,
                anchor.HourOfDay,
                anchor.MinuteOfHour,
                anchor.DayOfWeek,
                anchor.IsHoliday,
                anchor.HourSin,
                anchor.HourCos,
                anchor.WeekdaySin,
                anchor.WeekdayCos,
                lagValue192,
                lagValue672,
                mean16,
                std16,
                mean96,
                std96,
                split,
                horizonTargets));
        }

        var summary = new Part2DatasetSummary(
            effectiveValidationStart,
            PipelineConstants.HorizonSteps,
            inputRowsBeforeDeduplication,
            sorted.Count,
            droppedDuplicateTimestampRows,
            candidateAnchors,
            purgedAnchors,
            trainAnchors,
            validationAnchors,
            result.Count);

        return new Part2Dataset(result, summary);
    }

    public static void WriteDatasetCsv(IReadOnlyList<Part2SupervisedRow> rows, string outputCsvPath)
    {
        var directory = Path.GetDirectoryName(outputCsvPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var writer = new StreamWriter(outputCsvPath, false);
        var horizonColumns = Enumerable.Range(1, PipelineConstants.HorizonSteps).Select(step => $"Target_tPlus{step}");
        writer.WriteLine(string.Join(';',
            new[]
            {
                "anchorUtcTime",
                "TargetAtT",
                "Temperature",
                "Windspeed",
                "SolarIrradiation",
                "HourOfDay",
                "MinuteOfHour",
                "DayOfWeek",
                "IsHoliday",
                "HourSin",
                "HourCos",
                "WeekdaySin",
                "WeekdayCos",
                "TargetLag192",
                "TargetLag672",
                "TargetMean16",
                "TargetStd16",
                "TargetMean96",
                "TargetStd96",
                "Split"
            }.Concat(horizonColumns)));

        foreach (var row in rows)
        {
            var prefixValues = new[]
            {
                row.AnchorUtcTime.ToString("yyyy-MM-dd HH:mm:ss", InvariantCulture),
                row.TargetAtT.ToString(InvariantCulture),
                row.Temperature.ToString(InvariantCulture),
                row.Windspeed.ToString(InvariantCulture),
                row.SolarIrradiation.ToString(InvariantCulture),
                row.HourOfDay.ToString(InvariantCulture),
                row.MinuteOfHour.ToString(InvariantCulture),
                row.DayOfWeek.ToString(InvariantCulture),
                row.IsHoliday.ToString(InvariantCulture),
                row.HourSin.ToString(InvariantCulture),
                row.HourCos.ToString(InvariantCulture),
                row.WeekdaySin.ToString(InvariantCulture),
                row.WeekdayCos.ToString(InvariantCulture),
                row.TargetLag192.ToString(InvariantCulture),
                row.TargetLag672.ToString(InvariantCulture),
                row.TargetMean16.ToString(InvariantCulture),
                row.TargetStd16.ToString(InvariantCulture),
                row.TargetMean96.ToString(InvariantCulture),
                row.TargetStd96.ToString(InvariantCulture),
                row.Split
            };

            var horizonValues = row.HorizonTargets.Select(value => value.ToString(InvariantCulture));
            writer.WriteLine(string.Join(';', prefixValues.Concat(horizonValues)));
        }
    }

    public static void WriteSummaryJson(Part2DatasetSummary summary, string outputJsonPath)
    {
        var directory = Path.GetDirectoryName(outputJsonPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(outputJsonPath, json);
    }

    private static double CalculateMean(IReadOnlyList<FeatureRow> rows, int startInclusive, int endInclusive)
    {
        var sum = 0d;
        var count = 0;
        for (var index = startInclusive; index <= endInclusive; index++)
        {
            sum += rows[index].Target;
            count++;
        }

        return sum / count;
    }

    private static double CalculatePopulationStd(IReadOnlyList<FeatureRow> rows, int startInclusive, int endInclusive, double mean)
    {
        var sumSquaredDiff = 0d;
        var count = 0;
        for (var index = startInclusive; index <= endInclusive; index++)
        {
            var diff = rows[index].Target - mean;
            sumSquaredDiff += diff * diff;
            count++;
        }

        return Math.Sqrt(sumSquaredDiff / count);
    }

    private static List<FeatureRow> DeduplicateByUtcTimeKeepLast(IReadOnlyList<FeatureRow> rows, out int droppedRows)
    {
        if (rows.Count == 0)
        {
            droppedRows = 0;
            return [];
        }

        var deduplicated = rows
            .GroupBy(row => row.UtcTime)
            .Select(group => group.Last())
            .OrderBy(row => row.UtcTime)
            .ToList();

        droppedRows = rows.Count - deduplicated.Count;
        return deduplicated;
    }

    private static double ParseRequiredDouble(string value, int lineNumber, string columnName)
    {
        if (!double.TryParse(value, NumberStyles.Float, InvariantCulture, out var parsed))
        {
            throw new FormatException($"Invalid {columnName} at line {lineNumber}: '{value}'.");
        }

        return parsed;
    }

    private static int ParseRequiredInt(string value, int lineNumber, string columnName)
    {
        if (!int.TryParse(value, NumberStyles.Integer, InvariantCulture, out var parsed))
        {
            throw new FormatException($"Invalid {columnName} at line {lineNumber}: '{value}'.");
        }

        return parsed;
    }

    private static bool ParseRequiredBool(string value, int lineNumber, string columnName)
    {
        if (!bool.TryParse(value, out var parsed))
        {
            throw new FormatException($"Invalid {columnName} at line {lineNumber}: '{value}'.");
        }

        return parsed;
    }
}
