using System.Globalization;
using System.Text;

namespace Forecasting.App;

public sealed record DiagnosticsPreModelSummary(
    string Split,
    int AnchorCount,
    double MeanTarget,
    double StdTarget,
    double MinTarget,
    double P05Target,
    double P50Target,
    double P95Target,
    double MaxTarget,
    double TrendSlopePerStep);

public sealed record DiagnosticsCadenceSummary(
    string Split,
    int AnchorCount,
    int IrregularIntervalCount,
    int MissingStepCount,
    int NonPositiveIntervalCount,
    double MinIntervalMinutes,
    double MeanIntervalMinutes,
    double MaxIntervalMinutes);

public sealed record DiagnosticsResidualSummary(
    string ModelName,
    int EvaluatedPoints,
    double MeanError,
    double Mae,
    double Rmse,
    double ResidualP05,
    double ResidualP50,
    double ResidualP95,
    double UnderPredictionRate,
    double OverPredictionRate);

public sealed record DiagnosticsHorizonBucketSummary(
    string ModelName,
    int HorizonStart,
    int HorizonEnd,
    int EvaluatedPoints,
    double MeanError,
    double Mae,
    double Rmse,
    double UnderPredictionRate,
    double OverPredictionRate);

public sealed record DiagnosticsSamplePoint(
    string ModelName,
    DateTime AnchorUtcTime,
    DateTime ForecastUtcTime,
    int HorizonStep,
    double Predicted,
    double Actual,
    double Residual);

public sealed record DiagnosticsRunResult(
    DateTime GeneratedAtUtc,
    IReadOnlyList<DiagnosticsPreModelSummary> PreModelSummaries,
    IReadOnlyList<DiagnosticsCadenceSummary> CadenceSummaries,
    IReadOnlyList<DiagnosticsResidualSummary> ResidualSummaries,
    IReadOnlyList<DiagnosticsHorizonBucketSummary> HorizonBucketSummaries,
    IReadOnlyList<DiagnosticsSamplePoint> SamplePoints);

public static class PartDiagnostics
{
    private const int HorizonSteps = 192;
    private const int MinutesPerStep = 15;
    private const int HorizonBucketSize = 24;
    private const int SampleAnchors = 2;
    private static readonly CultureInfo InvariantCulture = CultureInfo.InvariantCulture;

    private sealed record PredictionPoint(string ModelName, DateTime AnchorUtcTime, int HorizonStep, double Predicted, string Split);

    private sealed record ActualPoint(DateTime AnchorUtcTime, int HorizonStep, double Actual);

    private sealed class RunningStats
    {
        public int Count { get; set; }
        public double Sum { get; set; }
        public double SumAbs { get; set; }
        public double SumSquares { get; set; }
        public int UnderCount { get; set; }
        public int OverCount { get; set; }
        public List<double> Residuals { get; } = [];

        public void Add(double residual)
        {
            Count++;
            Sum += residual;
            SumAbs += Math.Abs(residual);
            SumSquares += residual * residual;
            if (residual < 0d)
            {
                UnderCount++;
            }
            else if (residual > 0d)
            {
                OverCount++;
            }

            Residuals.Add(residual);
        }
    }

    public static DiagnosticsRunResult RunDiagnostics(string part2InputCsvPath, string part3PredictionsCsvPath)
    {
        var rows = Part3Modeling.ReadPart2DatasetCsv(part2InputCsvPath);
        var preModel = BuildPreModelSummaries(rows);
        var cadence = BuildCadenceSummaries(rows);

        var actualLookup = BuildActualLookup(rows);
        var predictions = ReadPredictionPoints(part3PredictionsCsvPath)
            .Where(point => string.Equals(point.Split, "Validation", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var residualSummaries = BuildResidualSummaries(predictions, actualLookup);
        var bucketSummaries = BuildHorizonBucketSummaries(predictions, actualLookup);
        var samplePoints = BuildSamplePoints(predictions, actualLookup);

        return new DiagnosticsRunResult(
            DateTime.UtcNow,
            preModel,
            cadence,
            residualSummaries,
            bucketSummaries,
            samplePoints);
    }

    public static void WriteArtifacts(DiagnosticsRunResult result, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        WritePreModelSummaryCsv(result, Path.Combine(outputDirectory, "premodel_summary.csv"));
        WriteCadenceSummaryCsv(result, Path.Combine(outputDirectory, "premodel_cadence.csv"));
        WriteResidualSummaryCsv(result, Path.Combine(outputDirectory, "postmodel_residual_summary.csv"));
        WriteHorizonBucketCsv(result, Path.Combine(outputDirectory, "postmodel_bias_by_horizon_bucket.csv"));
        WriteSampleCsv(result, Path.Combine(outputDirectory, "postmodel_sample_points.csv"));
        WriteHtmlReport(result, Path.Combine(outputDirectory, "diagnostics_report.html"));
    }

    public static void WritePreModelSummaryCsv(DiagnosticsRunResult result, string outputCsvPath)
    {
        EnsureOutputDirectory(outputCsvPath);
        using var writer = new StreamWriter(outputCsvPath, false);
        writer.WriteLine("Split;AnchorCount;MeanTarget;StdTarget;MinTarget;P05Target;P50Target;P95Target;MaxTarget;TrendSlopePerStep");

        foreach (var summary in result.PreModelSummaries.OrderBy(summary => summary.Split, StringComparer.Ordinal))
        {
            writer.WriteLine(string.Join(';',
                summary.Split,
                summary.AnchorCount.ToString(InvariantCulture),
                summary.MeanTarget.ToString("F6", InvariantCulture),
                summary.StdTarget.ToString("F6", InvariantCulture),
                summary.MinTarget.ToString("F6", InvariantCulture),
                summary.P05Target.ToString("F6", InvariantCulture),
                summary.P50Target.ToString("F6", InvariantCulture),
                summary.P95Target.ToString("F6", InvariantCulture),
                summary.MaxTarget.ToString("F6", InvariantCulture),
                summary.TrendSlopePerStep.ToString("F6", InvariantCulture)));
        }
    }

    public static void WriteCadenceSummaryCsv(DiagnosticsRunResult result, string outputCsvPath)
    {
        EnsureOutputDirectory(outputCsvPath);
        using var writer = new StreamWriter(outputCsvPath, false);
        writer.WriteLine("Split;AnchorCount;IrregularIntervalCount;MissingStepCount;NonPositiveIntervalCount;MinIntervalMinutes;MeanIntervalMinutes;MaxIntervalMinutes");

        foreach (var summary in result.CadenceSummaries.OrderBy(summary => summary.Split, StringComparer.Ordinal))
        {
            writer.WriteLine(string.Join(';',
                summary.Split,
                summary.AnchorCount.ToString(InvariantCulture),
                summary.IrregularIntervalCount.ToString(InvariantCulture),
                summary.MissingStepCount.ToString(InvariantCulture),
                summary.NonPositiveIntervalCount.ToString(InvariantCulture),
                summary.MinIntervalMinutes.ToString("F6", InvariantCulture),
                summary.MeanIntervalMinutes.ToString("F6", InvariantCulture),
                summary.MaxIntervalMinutes.ToString("F6", InvariantCulture)));
        }
    }

    public static void WriteResidualSummaryCsv(DiagnosticsRunResult result, string outputCsvPath)
    {
        EnsureOutputDirectory(outputCsvPath);
        using var writer = new StreamWriter(outputCsvPath, false);
        writer.WriteLine("ModelName;EvaluatedPoints;MeanError;MAE;RMSE;ResidualP05;ResidualP50;ResidualP95;UnderPredictionRate;OverPredictionRate");

        foreach (var summary in result.ResidualSummaries.OrderBy(summary => summary.ModelName, StringComparer.Ordinal))
        {
            writer.WriteLine(string.Join(';',
                summary.ModelName,
                summary.EvaluatedPoints.ToString(InvariantCulture),
                summary.MeanError.ToString("F6", InvariantCulture),
                summary.Mae.ToString("F6", InvariantCulture),
                summary.Rmse.ToString("F6", InvariantCulture),
                summary.ResidualP05.ToString("F6", InvariantCulture),
                summary.ResidualP50.ToString("F6", InvariantCulture),
                summary.ResidualP95.ToString("F6", InvariantCulture),
                summary.UnderPredictionRate.ToString("F6", InvariantCulture),
                summary.OverPredictionRate.ToString("F6", InvariantCulture)));
        }
    }

    public static void WriteHorizonBucketCsv(DiagnosticsRunResult result, string outputCsvPath)
    {
        EnsureOutputDirectory(outputCsvPath);
        using var writer = new StreamWriter(outputCsvPath, false);
        writer.WriteLine("ModelName;HorizonStart;HorizonEnd;EvaluatedPoints;MeanError;MAE;RMSE;UnderPredictionRate;OverPredictionRate");

        foreach (var summary in result.HorizonBucketSummaries
                     .OrderBy(summary => summary.ModelName, StringComparer.Ordinal)
                     .ThenBy(summary => summary.HorizonStart))
        {
            writer.WriteLine(string.Join(';',
                summary.ModelName,
                summary.HorizonStart.ToString(InvariantCulture),
                summary.HorizonEnd.ToString(InvariantCulture),
                summary.EvaluatedPoints.ToString(InvariantCulture),
                summary.MeanError.ToString("F6", InvariantCulture),
                summary.Mae.ToString("F6", InvariantCulture),
                summary.Rmse.ToString("F6", InvariantCulture),
                summary.UnderPredictionRate.ToString("F6", InvariantCulture),
                summary.OverPredictionRate.ToString("F6", InvariantCulture)));
        }
    }

    public static void WriteSampleCsv(DiagnosticsRunResult result, string outputCsvPath)
    {
        EnsureOutputDirectory(outputCsvPath);
        using var writer = new StreamWriter(outputCsvPath, false);
        writer.WriteLine("ModelName;AnchorUtcTime;ForecastUtcTime;HorizonStep;Predicted;Actual;Residual");

        foreach (var point in result.SamplePoints
                     .OrderBy(point => point.AnchorUtcTime)
                     .ThenBy(point => point.ModelName, StringComparer.Ordinal)
                     .ThenBy(point => point.HorizonStep))
        {
            writer.WriteLine(string.Join(';',
                point.ModelName,
                point.AnchorUtcTime.ToString("yyyy-MM-dd HH:mm:ss", InvariantCulture),
                point.ForecastUtcTime.ToString("yyyy-MM-dd HH:mm:ss", InvariantCulture),
                point.HorizonStep.ToString(InvariantCulture),
                point.Predicted.ToString("F6", InvariantCulture),
                point.Actual.ToString("F6", InvariantCulture),
                point.Residual.ToString("F6", InvariantCulture)));
        }
    }

    public static void WriteHtmlReport(DiagnosticsRunResult result, string outputHtmlPath)
    {
        EnsureOutputDirectory(outputHtmlPath);

        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html>");
        sb.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\" />");
        sb.AppendLine("<title>Forecast diagnostics</title>");
        sb.AppendLine("<style>body{font-family:Arial,sans-serif;margin:16px}table{border-collapse:collapse;margin-bottom:16px}th,td{border:1px solid #ccc;padding:4px 8px;text-align:right}th:first-child,td:first-child{text-align:left}h2{margin-top:28px}svg{border:1px solid #ccc;background:#fff}</style>");
        sb.AppendLine("</head><body>");
        sb.AppendLine($"<h1>Forecast diagnostics</h1><p>Generated UTC: {result.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss}</p>");

        AppendPreModelTable(sb, result.PreModelSummaries);
        AppendCadenceTable(sb, result.CadenceSummaries);
        AppendResidualTable(sb, result.ResidualSummaries);
        AppendHorizonBucketTable(sb, result.HorizonBucketSummaries);
        AppendSamplePlots(sb, result.SamplePoints);

        sb.AppendLine("</body></html>");
        File.WriteAllText(outputHtmlPath, sb.ToString());
    }

    private static List<DiagnosticsPreModelSummary> BuildPreModelSummaries(IReadOnlyList<Part3InputRow> rows)
    {
        var summaries = new List<DiagnosticsPreModelSummary>();
        foreach (var splitRows in rows.GroupBy(row => NormalizeSplit(row.Split), StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var ordered = splitRows.OrderBy(row => row.AnchorUtcTime).ToList();
            var values = ordered.Select(row => row.TargetAtT).ToList();
            var quantiles = ComputeQuantiles(values);

            summaries.Add(new DiagnosticsPreModelSummary(
                splitRows.Key,
                values.Count,
                values.Count == 0 ? 0d : values.Average(),
                ComputePopulationStd(values),
                values.Count == 0 ? 0d : values.Min(),
                quantiles.P05,
                quantiles.P50,
                quantiles.P95,
                values.Count == 0 ? 0d : values.Max(),
                ComputeTrendSlope(ordered.Select(row => row.TargetAtT).ToList())));
        }

        return summaries;
    }

    private static List<DiagnosticsCadenceSummary> BuildCadenceSummaries(IReadOnlyList<Part3InputRow> rows)
    {
        var summaries = new List<DiagnosticsCadenceSummary>();
        foreach (var splitRows in rows.GroupBy(row => NormalizeSplit(row.Split), StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var ordered = splitRows.OrderBy(row => row.AnchorUtcTime).Select(row => row.AnchorUtcTime).ToList();
            var intervals = new List<double>();
            var irregular = 0;
            var missingSteps = 0;
            var nonPositive = 0;

            for (var index = 1; index < ordered.Count; index++)
            {
                var minutes = (ordered[index] - ordered[index - 1]).TotalMinutes;
                intervals.Add(minutes);

                if (minutes <= 0d)
                {
                    nonPositive++;
                    continue;
                }

                var roundedSteps = (int)Math.Round(minutes / MinutesPerStep);
                var expectedMinutes = roundedSteps * MinutesPerStep;
                if (Math.Abs(minutes - expectedMinutes) > 1e-9 || roundedSteps != 1)
                {
                    irregular++;
                }

                if (roundedSteps > 1)
                {
                    missingSteps += roundedSteps - 1;
                }
            }

            summaries.Add(new DiagnosticsCadenceSummary(
                splitRows.Key,
                ordered.Count,
                irregular,
                missingSteps,
                nonPositive,
                intervals.Count == 0 ? 0d : intervals.Min(),
                intervals.Count == 0 ? 0d : intervals.Average(),
                intervals.Count == 0 ? 0d : intervals.Max()));
        }

        return summaries;
    }

    private static Dictionary<(DateTime AnchorUtcTime, int HorizonStep), double> BuildActualLookup(IReadOnlyList<Part3InputRow> rows)
    {
        var lookup = new Dictionary<(DateTime AnchorUtcTime, int HorizonStep), double>();
        foreach (var row in rows)
        {
            if (!string.Equals(row.Split, "Validation", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            for (var step = 1; step <= HorizonSteps; step++)
            {
                var key = (row.AnchorUtcTime, step);
                if (!lookup.TryAdd(key, row.HorizonTargets[step - 1]))
                {
                    throw new InvalidOperationException(
                        $"Duplicate ground-truth key for anchor '{row.AnchorUtcTime:yyyy-MM-dd HH:mm:ss}' and horizon '{step}'.");
                }
            }
        }

        return lookup;
    }

    private static List<DiagnosticsResidualSummary> BuildResidualSummaries(
        IReadOnlyList<PredictionPoint> predictions,
        IReadOnlyDictionary<(DateTime AnchorUtcTime, int HorizonStep), double> actualLookup)
    {
        var stats = new Dictionary<string, RunningStats>(StringComparer.Ordinal);
        foreach (var prediction in predictions)
        {
            if (!actualLookup.TryGetValue((prediction.AnchorUtcTime, prediction.HorizonStep), out var actual))
            {
                continue;
            }

            if (!stats.TryGetValue(prediction.ModelName, out var running))
            {
                running = new RunningStats();
                stats[prediction.ModelName] = running;
            }

            running.Add(prediction.Predicted - actual);
        }

        return stats
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => BuildResidualSummary(pair.Key, pair.Value))
            .ToList();
    }

    private static List<DiagnosticsHorizonBucketSummary> BuildHorizonBucketSummaries(
        IReadOnlyList<PredictionPoint> predictions,
        IReadOnlyDictionary<(DateTime AnchorUtcTime, int HorizonStep), double> actualLookup)
    {
        var stats = new Dictionary<(string ModelName, int BucketStart), RunningStats>(new ModelBucketComparer());
        foreach (var prediction in predictions)
        {
            if (!actualLookup.TryGetValue((prediction.AnchorUtcTime, prediction.HorizonStep), out var actual))
            {
                continue;
            }

            var bucketStart = ((prediction.HorizonStep - 1) / HorizonBucketSize) * HorizonBucketSize + 1;
            var key = (prediction.ModelName, bucketStart);
            if (!stats.TryGetValue(key, out var running))
            {
                running = new RunningStats();
                stats[key] = running;
            }

            running.Add(prediction.Predicted - actual);
        }

        return stats
            .OrderBy(pair => pair.Key.ModelName, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.BucketStart)
            .Select(pair =>
            {
                var summary = BuildResidualSummary(pair.Key.ModelName, pair.Value);
                var bucketEnd = Math.Min(HorizonSteps, pair.Key.BucketStart + HorizonBucketSize - 1);
                return new DiagnosticsHorizonBucketSummary(
                    summary.ModelName,
                    pair.Key.BucketStart,
                    bucketEnd,
                    summary.EvaluatedPoints,
                    summary.MeanError,
                    summary.Mae,
                    summary.Rmse,
                    summary.UnderPredictionRate,
                    summary.OverPredictionRate);
            })
            .ToList();
    }

    private static List<DiagnosticsSamplePoint> BuildSamplePoints(
        IReadOnlyList<PredictionPoint> predictions,
        IReadOnlyDictionary<(DateTime AnchorUtcTime, int HorizonStep), double> actualLookup)
    {
        var selectedAnchors = predictions
            .Select(point => point.AnchorUtcTime)
            .Distinct()
            .OrderBy(anchor => anchor)
            .Where(anchor => predictions.Any(point => point.AnchorUtcTime == anchor && actualLookup.ContainsKey((anchor, point.HorizonStep))))
            .Take(SampleAnchors)
            .ToHashSet();

        var sample = new List<DiagnosticsSamplePoint>();
        foreach (var prediction in predictions
                     .Where(point => selectedAnchors.Contains(point.AnchorUtcTime))
                     .OrderBy(point => point.AnchorUtcTime)
                     .ThenBy(point => point.ModelName, StringComparer.Ordinal)
                     .ThenBy(point => point.HorizonStep))
        {
            if (!actualLookup.TryGetValue((prediction.AnchorUtcTime, prediction.HorizonStep), out var actual))
            {
                continue;
            }

            sample.Add(new DiagnosticsSamplePoint(
                prediction.ModelName,
                prediction.AnchorUtcTime,
                prediction.AnchorUtcTime.AddMinutes(prediction.HorizonStep * MinutesPerStep),
                prediction.HorizonStep,
                prediction.Predicted,
                actual,
                prediction.Predicted - actual));
        }

        return sample;
    }

    private static DiagnosticsResidualSummary BuildResidualSummary(string modelName, RunningStats running)
    {
        if (running.Count == 0)
        {
            return new DiagnosticsResidualSummary(modelName, 0, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d);
        }

        var quantiles = ComputeQuantiles(running.Residuals);
        return new DiagnosticsResidualSummary(
            modelName,
            running.Count,
            running.Sum / running.Count,
            running.SumAbs / running.Count,
            Math.Sqrt(running.SumSquares / running.Count),
            quantiles.P05,
            quantiles.P50,
            quantiles.P95,
            (double)running.UnderCount * 100d / running.Count,
            (double)running.OverCount * 100d / running.Count);
    }

    private static (double P05, double P50, double P95) ComputeQuantiles(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return (0d, 0d, 0d);
        }

        var sorted = values.OrderBy(value => value).ToArray();
        return (Percentile(sorted, 0.05), Percentile(sorted, 0.50), Percentile(sorted, 0.95));
    }

    private static double Percentile(IReadOnlyList<double> sortedValues, double probability)
    {
        if (sortedValues.Count == 0)
        {
            return 0d;
        }

        if (sortedValues.Count == 1)
        {
            return sortedValues[0];
        }

        var position = probability * (sortedValues.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        var fraction = position - lower;
        return sortedValues[lower] + (sortedValues[upper] - sortedValues[lower]) * fraction;
    }

    private static double ComputePopulationStd(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return 0d;
        }

        var mean = values.Average();
        var variance = values.Sum(value => (value - mean) * (value - mean)) / values.Count;
        return Math.Sqrt(Math.Max(0d, variance));
    }

    private static double ComputeTrendSlope(IReadOnlyList<double> values)
    {
        if (values.Count < 2)
        {
            return 0d;
        }

        var n = values.Count;
        var meanX = (n - 1) / 2d;
        var meanY = values.Average();
        var numerator = 0d;
        var denominator = 0d;

        for (var index = 0; index < n; index++)
        {
            var dx = index - meanX;
            numerator += dx * (values[index] - meanY);
            denominator += dx * dx;
        }

        if (denominator <= 0d)
        {
            return 0d;
        }

        return numerator / denominator;
    }

    private static List<PredictionPoint> ReadPredictionPoints(string predictionsCsvPath)
    {
        var points = new List<PredictionPoint>();
        using var reader = new StreamReader(predictionsCsvPath);

        var header = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(header))
        {
            return points;
        }

        var columns = header.Split(';');
        var anchorIndex = FindRequiredIndex(columns, "anchorUtcTime");
        var splitIndex = FindRequiredIndex(columns, "Split");
        var modelIndex = FindRequiredIndex(columns, "Model");
        var predictedIndexes = Enumerable.Range(1, HorizonSteps)
            .Select(step => FindRequiredIndex(columns, $"Pred_tPlus{step}"))
            .ToArray();

        var seenKeys = new HashSet<(string ModelName, DateTime AnchorUtcTime, int HorizonStep)>();
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
                throw new FormatException($"Invalid prediction row at line {lineNumber}: expected {columns.Length} columns.");
            }

            if (!DateTime.TryParseExact(
                    parts[anchorIndex],
                    "yyyy-MM-dd HH:mm:ss",
                    InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var anchorUtcTime))
            {
                throw new FormatException($"Invalid anchorUtcTime at line {lineNumber}: '{parts[anchorIndex]}'.");
            }

            var split = parts[splitIndex];
            var modelName = parts[modelIndex];

            for (var step = 1; step <= HorizonSteps; step++)
            {
                var predicted = ParseRequiredDouble(parts[predictedIndexes[step - 1]], lineNumber, $"Pred_tPlus{step}");
                var key = (modelName, anchorUtcTime, step);
                if (!seenKeys.Add(key))
                {
                    throw new InvalidOperationException(
                        $"Duplicate prediction key for model '{modelName}', anchor '{anchorUtcTime:yyyy-MM-dd HH:mm:ss}', horizon '{step}'.");
                }

                points.Add(new PredictionPoint(modelName, anchorUtcTime, step, predicted, split));
            }
        }

        return points;
    }

    private static int FindRequiredIndex(IReadOnlyList<string> columns, string name)
    {
        for (var index = 0; index < columns.Count; index++)
        {
            if (string.Equals(columns[index], name, StringComparison.Ordinal))
            {
                return index;
            }
        }

        throw new FormatException($"Missing required column '{name}' in predictions CSV.");
    }

    private static double ParseRequiredDouble(string value, int lineNumber, string columnName)
    {
        if (!double.TryParse(value, NumberStyles.Float, InvariantCulture, out var parsed))
        {
            throw new FormatException($"Invalid {columnName} at line {lineNumber}: '{value}'.");
        }

        if (!double.IsFinite(parsed))
        {
            throw new FormatException($"Invalid {columnName} at line {lineNumber}: non-finite value '{value}'.");
        }

        return parsed;
    }

    private static string NormalizeSplit(string split)
    {
        if (string.IsNullOrWhiteSpace(split))
        {
            return "Unknown";
        }

        return split.Trim();
    }

    private static void EnsureOutputDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static void AppendPreModelTable(StringBuilder sb, IReadOnlyList<DiagnosticsPreModelSummary> summaries)
    {
        sb.AppendLine("<h2>Pre-model target shift</h2>");
        sb.AppendLine("<table><thead><tr><th>Split</th><th>Count</th><th>Mean</th><th>Std</th><th>P05</th><th>P50</th><th>P95</th><th>Slope/step</th></tr></thead><tbody>");
        foreach (var summary in summaries.OrderBy(summary => summary.Split, StringComparer.Ordinal))
        {
            sb.AppendLine($"<tr><td>{Escape(summary.Split)}</td><td>{summary.AnchorCount}</td><td>{summary.MeanTarget:F3}</td><td>{summary.StdTarget:F3}</td><td>{summary.P05Target:F3}</td><td>{summary.P50Target:F3}</td><td>{summary.P95Target:F3}</td><td>{summary.TrendSlopePerStep:F6}</td></tr>");
        }
        sb.AppendLine("</tbody></table>");
    }

    private static void AppendCadenceTable(StringBuilder sb, IReadOnlyList<DiagnosticsCadenceSummary> summaries)
    {
        sb.AppendLine("<h2>Cadence & missingness</h2>");
        sb.AppendLine("<table><thead><tr><th>Split</th><th>Anchors</th><th>IrregularIntervals</th><th>MissingSteps</th><th>NonPositiveIntervals</th><th>MeanIntervalMin</th></tr></thead><tbody>");
        foreach (var summary in summaries.OrderBy(summary => summary.Split, StringComparer.Ordinal))
        {
            sb.AppendLine($"<tr><td>{Escape(summary.Split)}</td><td>{summary.AnchorCount}</td><td>{summary.IrregularIntervalCount}</td><td>{summary.MissingStepCount}</td><td>{summary.NonPositiveIntervalCount}</td><td>{summary.MeanIntervalMinutes:F3}</td></tr>");
        }
        sb.AppendLine("</tbody></table>");
    }

    private static void AppendResidualTable(StringBuilder sb, IReadOnlyList<DiagnosticsResidualSummary> summaries)
    {
        sb.AppendLine("<h2>Residual summary</h2>");
        sb.AppendLine("<table><thead><tr><th>Model</th><th>Points</th><th>MeanError</th><th>MAE</th><th>RMSE</th><th>ResidualP05</th><th>ResidualP50</th><th>ResidualP95</th><th>Under%</th><th>Over%</th></tr></thead><tbody>");
        foreach (var summary in summaries.OrderBy(summary => summary.ModelName, StringComparer.Ordinal))
        {
            sb.AppendLine($"<tr><td>{Escape(summary.ModelName)}</td><td>{summary.EvaluatedPoints}</td><td>{summary.MeanError:F3}</td><td>{summary.Mae:F3}</td><td>{summary.Rmse:F3}</td><td>{summary.ResidualP05:F3}</td><td>{summary.ResidualP50:F3}</td><td>{summary.ResidualP95:F3}</td><td>{summary.UnderPredictionRate:F2}</td><td>{summary.OverPredictionRate:F2}</td></tr>");
        }
        sb.AppendLine("</tbody></table>");
    }

    private static void AppendHorizonBucketTable(StringBuilder sb, IReadOnlyList<DiagnosticsHorizonBucketSummary> summaries)
    {
        sb.AppendLine("<h2>Signed bias by horizon bucket</h2>");
        sb.AppendLine("<table><thead><tr><th>Model</th><th>Bucket</th><th>Points</th><th>MeanError</th><th>MAE</th><th>RMSE</th><th>Under%</th><th>Over%</th></tr></thead><tbody>");
        foreach (var summary in summaries.OrderBy(summary => summary.ModelName, StringComparer.Ordinal).ThenBy(summary => summary.HorizonStart))
        {
            sb.AppendLine($"<tr><td>{Escape(summary.ModelName)}</td><td>{summary.HorizonStart}-{summary.HorizonEnd}</td><td>{summary.EvaluatedPoints}</td><td>{summary.MeanError:F3}</td><td>{summary.Mae:F3}</td><td>{summary.Rmse:F3}</td><td>{summary.UnderPredictionRate:F2}</td><td>{summary.OverPredictionRate:F2}</td></tr>");
        }
        sb.AppendLine("</tbody></table>");
    }

    private static void AppendSamplePlots(StringBuilder sb, IReadOnlyList<DiagnosticsSamplePoint> samplePoints)
    {
        sb.AppendLine("<h2>Predicted vs actual sampled anchors</h2>");

        var byAnchor = samplePoints
            .GroupBy(point => point.AnchorUtcTime)
            .OrderBy(group => group.Key)
            .ToList();

        if (byAnchor.Count == 0)
        {
            sb.AppendLine("<p>No matched sample points available.</p>");
            return;
        }

        foreach (var anchorGroup in byAnchor)
        {
            var anchorPoints = anchorGroup.ToList();
            var actualSeries = anchorPoints
                .GroupBy(point => point.HorizonStep)
                .Select(group => group.First())
                .OrderBy(point => point.HorizonStep)
                .ToList();

            var modelGroups = anchorPoints
                .GroupBy(point => point.ModelName)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToList();

            var minY = Math.Min(
                actualSeries.Count == 0 ? 0d : actualSeries.Min(point => point.Actual),
                modelGroups.SelectMany(group => group).DefaultIfEmpty().Min(point => point?.Predicted ?? 0d));
            var maxY = Math.Max(
                actualSeries.Count == 0 ? 0d : actualSeries.Max(point => point.Actual),
                modelGroups.SelectMany(group => group).DefaultIfEmpty().Max(point => point?.Predicted ?? 0d));

            if (Math.Abs(maxY - minY) < 1e-9)
            {
                maxY = minY + 1d;
            }

            sb.AppendLine($"<h3>Anchor {anchorGroup.Key:yyyy-MM-dd HH:mm:ss} UTC</h3>");
            sb.AppendLine("<svg viewBox=\"0 0 960 260\" width=\"960\" height=\"260\" role=\"img\" aria-label=\"Prediction plot\">");
            sb.AppendLine("<line x1=\"40\" y1=\"220\" x2=\"920\" y2=\"220\" stroke=\"#444\" />");
            sb.AppendLine("<line x1=\"40\" y1=\"20\" x2=\"40\" y2=\"220\" stroke=\"#444\" />");

            var actualPolyline = BuildPolyline(actualSeries.Select(point => (point.HorizonStep, point.Actual)).ToList(), minY, maxY);
            sb.AppendLine($"<polyline points=\"{actualPolyline}\" fill=\"none\" stroke=\"#000\" stroke-width=\"2\" />");

            var dashStyles = new[] { "6 3", "2 2", "10 4", "1 4" };
            var modelIndex = 0;
            foreach (var modelGroup in modelGroups)
            {
                var polyline = BuildPolyline(modelGroup.OrderBy(point => point.HorizonStep).Select(point => (point.HorizonStep, point.Predicted)).ToList(), minY, maxY);
                var dash = dashStyles[modelIndex % dashStyles.Length];
                sb.AppendLine($"<polyline points=\"{polyline}\" fill=\"none\" stroke=\"hsl({(modelIndex * 67) % 360} 70% 40%)\" stroke-width=\"1.8\" stroke-dasharray=\"{dash}\" />");
                modelIndex++;
            }

            sb.AppendLine("</svg>");
            sb.AppendLine("<p><strong>Legend:</strong> solid black = actual, dashed lines = models.</p>");
        }
    }

    private static string BuildPolyline(IReadOnlyList<(int XStep, double YValue)> points, double minY, double maxY)
    {
        if (points.Count == 0)
        {
            return string.Empty;
        }

        const double xMin = 40d;
        const double xMax = 920d;
        const double yMin = 20d;
        const double yMax = 220d;
        const int maxStep = HorizonSteps;

        return string.Join(' ', points.Select(point =>
        {
            var x = xMin + (point.XStep - 1d) / (maxStep - 1d) * (xMax - xMin);
            var normalized = (point.YValue - minY) / (maxY - minY);
            var y = yMax - normalized * (yMax - yMin);
            return $"{x.ToString("F2", InvariantCulture)},{y.ToString("F2", InvariantCulture)}";
        }));
    }

    private static string Escape(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&#39;", StringComparison.Ordinal);
    }

    private sealed class ModelBucketComparer : IEqualityComparer<(string ModelName, int BucketStart)>
    {
        public bool Equals((string ModelName, int BucketStart) x, (string ModelName, int BucketStart) y)
        {
            return string.Equals(x.ModelName, y.ModelName, StringComparison.Ordinal) && x.BucketStart == y.BucketStart;
        }

        public int GetHashCode((string ModelName, int BucketStart) obj)
        {
            return HashCode.Combine(obj.ModelName, obj.BucketStart);
        }
    }
}