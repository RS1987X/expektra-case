// CSV/JSON I/O for Part3: reading Part2 input datasets and writing Part3 forecast outputs.
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Forecasting.App;

public static partial class Part3Modeling
{
    private static readonly string[] RequiredPart2BaseColumns =
    [
        "anchorUtcTime",
        nameof(Part2SupervisedRow.TargetAtT),
        nameof(Part2SupervisedRow.Temperature),
        nameof(Part2SupervisedRow.Windspeed),
        nameof(Part2SupervisedRow.SolarIrradiation),
        nameof(Part2SupervisedRow.HourOfDay),
        nameof(Part2SupervisedRow.MinuteOfHour),
        nameof(Part2SupervisedRow.DayOfWeek),
        nameof(Part2SupervisedRow.IsHoliday),
        nameof(Part2SupervisedRow.HourSin),
        nameof(Part2SupervisedRow.HourCos),
        nameof(Part2SupervisedRow.WeekdaySin),
        nameof(Part2SupervisedRow.WeekdayCos),
        nameof(Part2SupervisedRow.TargetLag192),
        nameof(Part2SupervisedRow.TargetLag672),
        nameof(Part2SupervisedRow.TargetLag192Mean16),
        nameof(Part2SupervisedRow.TargetLag192Std16),
        nameof(Part2SupervisedRow.TargetLag192Mean96),
        nameof(Part2SupervisedRow.TargetLag192Std96),
        nameof(Part2SupervisedRow.TargetLag672Mean16),
        nameof(Part2SupervisedRow.TargetLag672Std16),
        nameof(Part2SupervisedRow.TargetLag672Mean96),
        nameof(Part2SupervisedRow.TargetLag672Std96),
        "Split"
    ];

    public static IReadOnlyList<Part2SupervisedRow> ReadPart2DatasetCsv(string part2DatasetCsvPath)
    {
        var rows = new List<Part2SupervisedRow>();
        using var reader = new StreamReader(part2DatasetCsvPath);

        var header = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(header))
        {
            return rows;
        }

        var columns = header.Split(';');
        var requiredPart2Indexes = RequiredPart2BaseColumns.ToDictionary(
            name => name,
            name => CsvParsing.FindRequiredColumnIndex(columns, name, $"part2 supervised dataset '{part2DatasetCsvPath}'"),
            StringComparer.Ordinal);
        var horizonIndexes = Enumerable.Range(1, PipelineConstants.HorizonSteps)
            .Select(step => CsvParsing.FindRequiredColumnIndex(columns, $"Target_tPlus{step}", $"part2 supervised dataset '{part2DatasetCsvPath}'"))
            .ToArray();
        var maxRequiredIndex = Math.Max(requiredPart2Indexes.Values.Max(), horizonIndexes.Length == 0 ? -1 : horizonIndexes.Max());

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
            if (parts.Length <= maxRequiredIndex)
            {
                throw new FormatException($"Invalid row at line {lineNumber}: expected at least {maxRequiredIndex + 1} columns.");
            }

            var anchorUtcTime = CsvParsing.ParseRequiredUtcDateTime(parts[requiredPart2Indexes["anchorUtcTime"]], lineNumber, "anchorUtcTime");

            var horizonTargets = new double[PipelineConstants.HorizonSteps];
            for (var step = 0; step < PipelineConstants.HorizonSteps; step++)
            {
                horizonTargets[step] = CsvParsing.ParseRequiredDouble(parts[horizonIndexes[step]], lineNumber, $"Target_tPlus{step + 1}");
            }

            rows.Add(new Part2SupervisedRow(
                anchorUtcTime,
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.TargetAtT)]], lineNumber, nameof(Part2SupervisedRow.TargetAtT)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.Temperature)]], lineNumber, nameof(Part2SupervisedRow.Temperature)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.Windspeed)]], lineNumber, nameof(Part2SupervisedRow.Windspeed)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.SolarIrradiation)]], lineNumber, nameof(Part2SupervisedRow.SolarIrradiation)),
                CsvParsing.ParseRequiredInt(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.HourOfDay)]], lineNumber, nameof(Part2SupervisedRow.HourOfDay)),
                CsvParsing.ParseRequiredInt(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.MinuteOfHour)]], lineNumber, nameof(Part2SupervisedRow.MinuteOfHour)),
                CsvParsing.ParseRequiredInt(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.DayOfWeek)]], lineNumber, nameof(Part2SupervisedRow.DayOfWeek)),
                CsvParsing.ParseRequiredBool(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.IsHoliday)]], lineNumber, nameof(Part2SupervisedRow.IsHoliday)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.HourSin)]], lineNumber, nameof(Part2SupervisedRow.HourSin)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.HourCos)]], lineNumber, nameof(Part2SupervisedRow.HourCos)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.WeekdaySin)]], lineNumber, nameof(Part2SupervisedRow.WeekdaySin)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.WeekdayCos)]], lineNumber, nameof(Part2SupervisedRow.WeekdayCos)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.TargetLag192)]], lineNumber, nameof(Part2SupervisedRow.TargetLag192)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.TargetLag672)]], lineNumber, nameof(Part2SupervisedRow.TargetLag672)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.TargetLag192Mean16)]], lineNumber, nameof(Part2SupervisedRow.TargetLag192Mean16)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.TargetLag192Std16)]], lineNumber, nameof(Part2SupervisedRow.TargetLag192Std16)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.TargetLag192Mean96)]], lineNumber, nameof(Part2SupervisedRow.TargetLag192Mean96)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.TargetLag192Std96)]], lineNumber, nameof(Part2SupervisedRow.TargetLag192Std96)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.TargetLag672Mean16)]], lineNumber, nameof(Part2SupervisedRow.TargetLag672Mean16)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.TargetLag672Std16)]], lineNumber, nameof(Part2SupervisedRow.TargetLag672Std16)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.TargetLag672Mean96)]], lineNumber, nameof(Part2SupervisedRow.TargetLag672Mean96)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.TargetLag672Std96)]], lineNumber, nameof(Part2SupervisedRow.TargetLag672Std96)),
                parts[requiredPart2Indexes["Split"]],
                horizonTargets));
        }

        return rows;
    }

    public static void WriteForecastsCsv(IReadOnlyList<Part3ForecastRow> forecasts, string outputCsvPath)
    {
        FileOutput.EnsureParentDirectory(outputCsvPath);

        using var writer = new StreamWriter(outputCsvPath, false);
        var headerBuilder = new StringBuilder(capacity: 64 + PipelineConstants.HorizonSteps * 24);
        headerBuilder.Append("anchorUtcTime;Split;Model;ExogenousFallbackSteps");
        for (var step = 1; step <= PipelineConstants.HorizonSteps; step++)
        {
            headerBuilder.Append(';').Append("Pred_tPlus").Append(step);
        }

        for (var step = 1; step <= PipelineConstants.HorizonSteps; step++)
        {
            headerBuilder.Append(';').Append("Actual_tPlus").Append(step);
        }

        writer.WriteLine(headerBuilder.ToString());

        var rowBuilder = new StringBuilder(capacity: 128 + PipelineConstants.HorizonSteps * 28);

        foreach (var forecast in forecasts)
        {
            rowBuilder.Clear();
            rowBuilder.Append(forecast.AnchorUtcTime.ToString("yyyy-MM-dd HH:mm:ss", InvariantCulture));
            rowBuilder.Append(';').Append(forecast.Split);
            rowBuilder.Append(';').Append(forecast.ModelName);
            rowBuilder.Append(';').Append(forecast.ExogenousFallbackSteps.ToString(InvariantCulture));

            for (var step = 0; step < forecast.PredictedTargets.Count; step++)
            {
                rowBuilder.Append(';').Append(forecast.PredictedTargets[step].ToString(InvariantCulture));
            }

            for (var step = 0; step < forecast.ActualTargets.Count; step++)
            {
                rowBuilder.Append(';').Append(forecast.ActualTargets[step].ToString(InvariantCulture));
            }

            writer.WriteLine(rowBuilder.ToString());
        }
    }

    public static IReadOnlyList<Part3ForecastRow> ReadForecastsCsv(string forecastsCsvPath)
    {
        var rows = new List<Part3ForecastRow>();
        using var reader = new StreamReader(forecastsCsvPath);

        var header = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(header))
        {
            return rows;
        }

        var columns = header.Split(';');
        var anchorIndex = CsvParsing.FindRequiredColumnIndex(columns, "anchorUtcTime", "forecasts CSV");
        var splitIndex = CsvParsing.FindRequiredColumnIndex(columns, "Split", "forecasts CSV");
        var modelIndex = CsvParsing.FindRequiredColumnIndex(columns, "Model", "forecasts CSV");
        var fallbackIndex = CsvParsing.FindRequiredColumnIndex(columns, "ExogenousFallbackSteps", "forecasts CSV");
        var predictedIndexes = Enumerable.Range(1, PipelineConstants.HorizonSteps)
            .Select(step => CsvParsing.FindRequiredColumnIndex(columns, $"Pred_tPlus{step}", "forecasts CSV"))
            .ToArray();
        var actualIndexes = Enumerable.Range(1, PipelineConstants.HorizonSteps)
            .Select(step => CsvParsing.FindRequiredColumnIndex(columns, $"Actual_tPlus{step}", "forecasts CSV"))
            .ToArray();

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
            if (parts.Length < columns.Length)
            {
                throw new FormatException($"Invalid forecast row at line {lineNumber}: expected {columns.Length} columns.");
            }

            var anchorUtcTime = CsvParsing.ParseRequiredUtcDateTime(parts[anchorIndex], lineNumber, "anchorUtcTime");

            var predicted = new double[PipelineConstants.HorizonSteps];
            var actual = new double[PipelineConstants.HorizonSteps];
            for (var step = 0; step < PipelineConstants.HorizonSteps; step++)
            {
                predicted[step] = CsvParsing.ParseRequiredDouble(parts[predictedIndexes[step]], lineNumber, $"Pred_tPlus{step + 1}");
                actual[step] = CsvParsing.ParseRequiredDouble(parts[actualIndexes[step]], lineNumber, $"Actual_tPlus{step + 1}");
            }

            rows.Add(new Part3ForecastRow(
                anchorUtcTime,
                parts[splitIndex],
                parts[modelIndex],
                CsvParsing.ParseRequiredInt(parts[fallbackIndex], lineNumber, "ExogenousFallbackSteps"),
                predicted,
                actual));
        }

        return rows;
    }

    public static void WriteSummaryJson(Part3RunSummary summary, string outputJsonPath)
    {
        FileOutput.EnsureParentDirectory(outputJsonPath);

        var json = JsonSerializer.Serialize(summary, FileOutput.IndentedJsonOptions);
        File.WriteAllText(outputJsonPath, json);
    }

    public static void WriteFeatureImportanceCsv(Part3PfiResult pfiResult, string outputCsvPath)
    {
        FileOutput.EnsureParentDirectory(outputCsvPath);

        using var writer = new StreamWriter(outputCsvPath, false);
        writer.WriteLine("Rank;FeatureName;MaeDelta;MaeDeltaStdDev;RmseDelta;RmseDeltaStdDev;R2Delta;R2DeltaStdDev");

        foreach (var feature in pfiResult.Features)
        {
            writer.WriteLine(string.Join(';',
                feature.Rank.ToString(InvariantCulture),
                feature.FeatureName,
                feature.MaeDelta.ToString(InvariantCulture),
                feature.MaeDeltaStdDev.ToString(InvariantCulture),
                feature.RmseDelta.ToString(InvariantCulture),
                feature.RmseDeltaStdDev.ToString(InvariantCulture),
                feature.R2Delta.ToString(InvariantCulture),
                feature.R2DeltaStdDev.ToString(InvariantCulture)));
        }
    }

    public static void WriteFeatureImportancePerSeedCsv(Part3PfiResult pfiResult, string outputCsvPath)
    {
        if (pfiResult.PerSeedDetails is null || pfiResult.PerSeedDetails.Count == 0)
            return;

        FileOutput.EnsureParentDirectory(outputCsvPath);

        // Pivot: rows = features, columns = per-seed rank + average.
        var seeds = pfiResult.PerSeedDetails.Select(d => d.Seed).Distinct().OrderBy(s => s).ToList();
        var featureNames = pfiResult.Features.Select(f => f.FeatureName).ToList();
        var lookup = pfiResult.PerSeedDetails.ToDictionary(d => (d.Seed, d.FeatureName), d => d.Rank);

        using var writer = new StreamWriter(outputCsvPath, false);
        // Header: FeatureName;Seed42_Rank;Seed43_Rank;...;AvgRank
        var header = "FeatureName;" + string.Join(';', seeds.Select(s => $"Seed{s}_Rank")) + ";AvgRank";
        writer.WriteLine(header);

        foreach (var feature in featureNames)
        {
            var ranks = seeds.Select(s => lookup.TryGetValue((s, feature), out var r) ? r : 0).ToList();
            var avgRank = ranks.Average();
            var row = feature + ";" + string.Join(';', ranks.Select(r => r.ToString(InvariantCulture)))
                + ";" + avgRank.ToString("F1", InvariantCulture);
            writer.WriteLine(row);
        }
    }
}
