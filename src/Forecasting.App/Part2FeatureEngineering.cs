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
    double TargetLag192Mean16,
    double TargetLag192Std16,
    double TargetLag192Mean96,
    double TargetLag192Std96,
    double TargetLag672Mean16,
    double TargetLag672Std16,
    double TargetLag672Mean96,
    double TargetLag672Std96,
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

            var utcTime = CsvParsing.ParseRequiredUtcDateTime(parts[0], lineNumber, "utcTime");

            rows.Add(new FeatureRow(
                utcTime,
                CsvParsing.ParseRequiredDouble(parts[1], lineNumber, "Target"),
                CsvParsing.ParseRequiredDouble(parts[2], lineNumber, "Temperature"),
                CsvParsing.ParseRequiredDouble(parts[3], lineNumber, "Windspeed"),
                CsvParsing.ParseRequiredDouble(parts[4], lineNumber, "SolarIrradiation"),
                CsvParsing.ParseRequiredInt(parts[5], lineNumber, "HourOfDay"),
                CsvParsing.ParseRequiredInt(parts[6], lineNumber, "MinuteOfHour"),
                CsvParsing.ParseRequiredInt(parts[7], lineNumber, "DayOfWeek"),
                CsvParsing.ParseRequiredBool(parts[8], lineNumber, "IsHoliday"),
                CsvParsing.ParseRequiredDouble(parts[9], lineNumber, "HourSin"),
                CsvParsing.ParseRequiredDouble(parts[10], lineNumber, "HourCos"),
                CsvParsing.ParseRequiredDouble(parts[11], lineNumber, "WeekdaySin"),
                CsvParsing.ParseRequiredDouble(parts[12], lineNumber, "WeekdayCos")));
        }

        return rows;
    }

    public static Part2Dataset BuildDataset(IReadOnlyList<FeatureRow> inputRows, DateTime? validationStartUtc = null)
    {
        var sorted = inputRows.OrderBy(row => row.UtcTime).ToList();
        var inputRowsBeforeDeduplication = sorted.Count;
        sorted = CollectionHelpers.DeduplicateByKeyKeepLast(sorted, row => row.UtcTime, out var droppedDuplicateTimestampRows);

        if (sorted.Count == 0)
        {
            return new Part2Dataset([], new Part2DatasetSummary(DateTime.MinValue, PipelineConstants.HorizonSteps, 0, 0, 0, 0, 0, 0, 0, 0));
        }

        var effectiveValidationStart = validationStartUtc ?? sorted[^1].UtcTime.AddDays(-PipelineConstants.DefaultValidationWindowDays);

        const int rollingMinLag192 = FeatureConfig.TargetLag192;
        const int rollingMinLag672 = FeatureConfig.TargetLag672;
        // This gate only ensures enough history exists to compute lag/rolling features.
        // Split safety/leakage control is handled later by horizon-based train/validation eligibility and purge.
        var maxLookback = new[]
        {
            FeatureConfig.TargetLag192,
            FeatureConfig.TargetLag672,
            rollingMinLag192 + (FeatureConfig.RollingWindow16 - 1),
            rollingMinLag192 + (FeatureConfig.RollingWindow96 - 1),
            rollingMinLag672 + (FeatureConfig.RollingWindow16 - 1),
            rollingMinLag672 + (FeatureConfig.RollingWindow96 - 1)
        }.Max();

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
            // Assignment requirement: rolling stats must be computed on lagged history.
            var rollingEndIndex192 = anchorIndex - rollingMinLag192;
            var mean16 = CalculateMean(sorted, rollingEndIndex192 - (FeatureConfig.RollingWindow16 - 1), rollingEndIndex192);
            var std16 = CalculatePopulationStd(sorted, rollingEndIndex192 - (FeatureConfig.RollingWindow16 - 1), rollingEndIndex192, mean16);
            var mean96 = CalculateMean(sorted, rollingEndIndex192 - (FeatureConfig.RollingWindow96 - 1), rollingEndIndex192);
            var std96 = CalculatePopulationStd(sorted, rollingEndIndex192 - (FeatureConfig.RollingWindow96 - 1), rollingEndIndex192, mean96);

            var rollingEndIndex672 = anchorIndex - rollingMinLag672;
            var mean16Lag672 = CalculateMean(sorted, rollingEndIndex672 - (FeatureConfig.RollingWindow16 - 1), rollingEndIndex672);
            var std16Lag672 = CalculatePopulationStd(sorted, rollingEndIndex672 - (FeatureConfig.RollingWindow16 - 1), rollingEndIndex672, mean16Lag672);
            var mean96Lag672 = CalculateMean(sorted, rollingEndIndex672 - (FeatureConfig.RollingWindow96 - 1), rollingEndIndex672);
            var std96Lag672 = CalculatePopulationStd(sorted, rollingEndIndex672 - (FeatureConfig.RollingWindow96 - 1), rollingEndIndex672, mean96Lag672);

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
                mean16Lag672,
                std16Lag672,
                mean96Lag672,
                std96Lag672,
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
        FileOutput.EnsureParentDirectory(outputCsvPath);

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
                "TargetLag192Mean16",
                "TargetLag192Std16",
                "TargetLag192Mean96",
                "TargetLag192Std96",
                "TargetLag672Mean16",
                "TargetLag672Std16",
                "TargetLag672Mean96",
                "TargetLag672Std96",
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
                row.TargetLag192Mean16.ToString(InvariantCulture),
                row.TargetLag192Std16.ToString(InvariantCulture),
                row.TargetLag192Mean96.ToString(InvariantCulture),
                row.TargetLag192Std96.ToString(InvariantCulture),
                row.TargetLag672Mean16.ToString(InvariantCulture),
                row.TargetLag672Std16.ToString(InvariantCulture),
                row.TargetLag672Mean96.ToString(InvariantCulture),
                row.TargetLag672Std96.ToString(InvariantCulture),
                row.Split
            };

            var horizonValues = row.HorizonTargets.Select(value => value.ToString(InvariantCulture));
            writer.WriteLine(string.Join(';', prefixValues.Concat(horizonValues)));
        }
    }

    public static void WriteSummaryJson(Part2DatasetSummary summary, string outputJsonPath)
    {
        FileOutput.EnsureParentDirectory(outputJsonPath);

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

}
