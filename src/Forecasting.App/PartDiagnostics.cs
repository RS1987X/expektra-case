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
    string Split,
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
    string Split,
    int HorizonStart,
    int HorizonEnd,
    int EvaluatedPoints,
    double MeanError,
    double Mae,
    double Rmse,
    double UnderPredictionRate,
    double OverPredictionRate);

public sealed record DiagnosticsHorizonStepSummary(
    string ModelName,
    int HorizonStep,
    int EvaluatedPoints,
    double MeanError,
    double Mae,
    double Rmse,
    double UnderPredictionRate,
    double OverPredictionRate);

public sealed record DiagnosticsSamplePoint(
    string ModelName,
    string Split,
    DateTime AnchorUtcTime,
    DateTime ForecastUtcTime,
    int HorizonStep,
    double Predicted,
    double Actual,
    double Residual);

public sealed record DiagnosticsTargetPoint(
    DateTime AnchorUtcTime,
    string Split,
    double TargetAtT);

public sealed record DiagnosticsOverlayPoint(
    string Split,
    string ModelName,
    int HorizonStep,
    DateTime ForecastUtcTime,
    double Predicted,
    double Actual);

public sealed record DiagnosticsRunResult(
    DateTime GeneratedAtUtc,
    IReadOnlyList<DiagnosticsPreModelSummary> PreModelSummaries,
    IReadOnlyList<DiagnosticsCadenceSummary> CadenceSummaries,
    IReadOnlyList<DiagnosticsResidualSummary> ResidualSummaries,
    IReadOnlyList<DiagnosticsHorizonBucketSummary> HorizonBucketSummaries,
    IReadOnlyList<DiagnosticsHorizonStepSummary> ValidationHorizonSummaries,
    IReadOnlyList<DiagnosticsSamplePoint> SamplePoints,
    IReadOnlyList<DiagnosticsTargetPoint> TargetSeries,
    IReadOnlyList<DiagnosticsOverlayPoint> OverlayPoints,
    IReadOnlyList<Part3PfiFeatureResult>? FeatureImportance = null);

public static class PartDiagnostics
{
    private const int HorizonBucketSize = 24;
    private const int SampleAnchors = 2;
    private static readonly int[] OverlayHorizonSteps = [96, 192];
    private const int OverlayMaxRenderPoints = 1500;
    private const string OverlayModelName = "FastTreeRecursive";
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
        return RunDiagnostics(part2InputCsvPath, part3PredictionsCsvPath, pfiCsvPath: null);
    }

    public static DiagnosticsRunResult RunDiagnostics(string part2InputCsvPath, string part3PredictionsCsvPath, string? pfiCsvPath)
    {
        var rows = Part3Modeling.ReadPart2DatasetCsv(part2InputCsvPath);
        var predictions = ReadPredictionPoints(part3PredictionsCsvPath);

        IReadOnlyList<Part3PfiFeatureResult>? featureImportance = null;
        if (!string.IsNullOrWhiteSpace(pfiCsvPath) && File.Exists(pfiCsvPath))
        {
            featureImportance = ReadFeatureImportanceCsv(pfiCsvPath);
        }

        return RunDiagnostics(rows, predictions, featureImportance);
    }

    public static DiagnosticsRunResult RunDiagnostics(
        IReadOnlyList<Part3InputRow> rows,
        IReadOnlyList<Part3ForecastRow> forecastRows,
        IReadOnlyList<Part3PfiFeatureResult>? featureImportance = null)
    {
        var predictions = BuildPredictionPoints(forecastRows);
        return RunDiagnostics(rows, predictions, featureImportance);
    }

    private static DiagnosticsRunResult RunDiagnostics(
        IReadOnlyList<Part3InputRow> rows,
        IReadOnlyList<PredictionPoint> predictions,
        IReadOnlyList<Part3PfiFeatureResult>? featureImportance)
    {
        var preModel = BuildPreModelSummaries(rows);
        var cadence = BuildCadenceSummaries(rows);

        var actualLookup = BuildActualLookup(rows);

        var residualSummaries = BuildResidualSummaries(predictions, actualLookup);
        var bucketSummaries = BuildHorizonBucketSummaries(predictions, actualLookup);
        var validationHorizonSummaries = BuildValidationHorizonSummaries(predictions, actualLookup);
        var samplePoints = BuildSamplePoints(predictions, actualLookup);
        var overlayPoints = BuildOverlayPoints(predictions, actualLookup);
        var targetSeries = rows
            .OrderBy(row => row.AnchorUtcTime)
            .Select(row => new DiagnosticsTargetPoint(row.AnchorUtcTime, NormalizeSplit(row.Split), row.TargetAtT))
            .ToList();

        return new DiagnosticsRunResult(
            DateTime.UtcNow,
            preModel,
            cadence,
            residualSummaries,
            bucketSummaries,
            validationHorizonSummaries,
            samplePoints,
            targetSeries,
            overlayPoints,
            featureImportance);
    }

    public static void WriteArtifacts(DiagnosticsRunResult result, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        WritePreModelSummaryCsv(result, Path.Combine(outputDirectory, "premodel_summary.csv"));
        WriteCadenceSummaryCsv(result, Path.Combine(outputDirectory, "premodel_cadence.csv"));
        WriteResidualSummaryCsv(result, Path.Combine(outputDirectory, "postmodel_residual_summary.csv"));
        WriteHorizonBucketCsv(result, Path.Combine(outputDirectory, "postmodel_bias_by_horizon_bucket.csv"));
        WriteValidationHorizonCsv(result, Path.Combine(outputDirectory, "postmodel_validation_error_by_horizon.csv"));
        WriteSampleCsv(result, Path.Combine(outputDirectory, "postmodel_sample_points.csv"));
        WriteTargetSeriesSvg(result, Path.Combine(outputDirectory, "target_over_time.svg"));
        WriteSplitOverlaySvgs(result, outputDirectory);
        WriteValidationHorizonSvg(result, Path.Combine(outputDirectory, "postmodel_validation_error_by_horizon.svg"));

        if (result.FeatureImportance is { Count: > 0 })
        {
            WriteFeatureImportanceSvg(result.FeatureImportance, Path.Combine(outputDirectory, "feature_importance.svg"));
        }

        WriteHtmlReport(result, Path.Combine(outputDirectory, "diagnostics_report.html"));
    }

    public static void WriteValidationHorizonCsv(DiagnosticsRunResult result, string outputCsvPath)
    {
        EnsureOutputDirectory(outputCsvPath);
        using var writer = new StreamWriter(outputCsvPath, false);
        writer.WriteLine("ModelName;HorizonStep;EvaluatedPoints;MeanError;MAE;RMSE;UnderPredictionRate;OverPredictionRate");

        foreach (var summary in result.ValidationHorizonSummaries
                     .OrderBy(summary => summary.ModelName, StringComparer.Ordinal)
                     .ThenBy(summary => summary.HorizonStep))
        {
            writer.WriteLine(string.Join(';',
                summary.ModelName,
                summary.HorizonStep.ToString(InvariantCulture),
                summary.EvaluatedPoints.ToString(InvariantCulture),
                summary.MeanError.ToString("F6", InvariantCulture),
                summary.Mae.ToString("F6", InvariantCulture),
                summary.Rmse.ToString("F6", InvariantCulture),
                summary.UnderPredictionRate.ToString("F6", InvariantCulture),
                summary.OverPredictionRate.ToString("F6", InvariantCulture)));
        }
    }

    public static void WriteValidationHorizonSvg(DiagnosticsRunResult result, string outputSvgPath)
    {
        EnsureOutputDirectory(outputSvgPath);
        File.WriteAllText(outputSvgPath, BuildValidationHorizonSvg(result.ValidationHorizonSummaries));
    }

    public static void WriteSplitOverlaySvgs(DiagnosticsRunResult result, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        var bySplitAndHorizon = result.OverlayPoints
            .GroupBy(point => (point.Split, point.HorizonStep))
            .OrderBy(group => group.Key.Split, StringComparer.Ordinal)
            .ThenBy(group => group.Key.HorizonStep);

        foreach (var group in bySplitAndHorizon)
        {
            var safeSplit = SanitizeFileNameSegment(group.Key.Split);
            var suffix = $"tplus{group.Key.HorizonStep}";
            var outputPath = Path.Combine(outputDirectory, $"target_vs_predicted_{safeSplit}_{suffix}.svg");
            File.WriteAllText(outputPath, BuildSplitOverlaySvg(group.ToList()));

            var csvPath = Path.Combine(outputDirectory, $"target_vs_predicted_{safeSplit}_{suffix}.csv");
            WriteSplitOverlayCsv(group.ToList(), csvPath);
        }
    }

    private static void WriteSplitOverlayCsv(IReadOnlyList<DiagnosticsOverlayPoint> points, string outputCsvPath)
    {
        EnsureOutputDirectory(outputCsvPath);
        using var writer = new StreamWriter(outputCsvPath, false);
        writer.WriteLine("Split;ModelName;HorizonStep;ForecastUtcTime;Actual;Predicted;Residual");

        foreach (var point in points.OrderBy(point => point.ForecastUtcTime))
        {
            var residual = point.Predicted - point.Actual;
            writer.WriteLine(string.Join(';',
                point.Split,
                point.ModelName,
                point.HorizonStep.ToString(InvariantCulture),
                point.ForecastUtcTime.ToString("yyyy-MM-dd HH:mm:ss", InvariantCulture),
                point.Actual.ToString("F6", InvariantCulture),
                point.Predicted.ToString("F6", InvariantCulture),
                residual.ToString("F6", InvariantCulture)));
        }
    }

    public static void WriteTargetSeriesSvg(DiagnosticsRunResult result, string outputSvgPath)
    {
        EnsureOutputDirectory(outputSvgPath);
        var svg = BuildTargetSeriesSvg(result.TargetSeries);
        File.WriteAllText(outputSvgPath, svg);
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
        writer.WriteLine("ModelName;Split;EvaluatedPoints;MeanError;MAE;RMSE;ResidualP05;ResidualP50;ResidualP95;UnderPredictionRate;OverPredictionRate");

        foreach (var summary in result.ResidualSummaries
                     .OrderBy(summary => summary.ModelName, StringComparer.Ordinal)
                     .ThenBy(summary => summary.Split, StringComparer.Ordinal))
        {
            writer.WriteLine(string.Join(';',
                summary.ModelName,
                summary.Split,
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
        writer.WriteLine("ModelName;Split;HorizonStart;HorizonEnd;EvaluatedPoints;MeanError;MAE;RMSE;UnderPredictionRate;OverPredictionRate");

        foreach (var summary in result.HorizonBucketSummaries
                     .OrderBy(summary => summary.ModelName, StringComparer.Ordinal)
                     .ThenBy(summary => summary.Split, StringComparer.Ordinal)
                     .ThenBy(summary => summary.HorizonStart))
        {
            writer.WriteLine(string.Join(';',
                summary.ModelName,
                summary.Split,
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
        writer.WriteLine("ModelName;Split;AnchorUtcTime;ForecastUtcTime;HorizonStep;Predicted;Actual;Residual");

        foreach (var point in result.SamplePoints
                     .OrderBy(point => point.Split, StringComparer.Ordinal)
                     .ThenBy(point => point.AnchorUtcTime)
                     .ThenBy(point => point.ModelName, StringComparer.Ordinal)
                     .ThenBy(point => point.HorizonStep))
        {
            writer.WriteLine(string.Join(';',
                point.ModelName,
                point.Split,
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

        AppendTargetSeriesPlot(sb, result.TargetSeries);
        AppendValidationHorizonPlot(sb, result.ValidationHorizonSummaries);
        AppendSplitOverlayPlots(sb, result.OverlayPoints);
        AppendFeatureImportanceSection(sb, result.FeatureImportance);
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

                var roundedSteps = (int)Math.Round(minutes / PipelineConstants.MinutesPerStep);
                var expectedMinutes = roundedSteps * PipelineConstants.MinutesPerStep;
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

    private static Dictionary<(string Split, DateTime AnchorUtcTime, int HorizonStep), double> BuildActualLookup(IReadOnlyList<Part3InputRow> rows)
    {
        var lookup = new Dictionary<(string Split, DateTime AnchorUtcTime, int HorizonStep), double>();
        foreach (var row in rows)
        {
            var split = NormalizeSplit(row.Split);

            for (var step = 1; step <= PipelineConstants.HorizonSteps; step++)
            {
                var key = (split, row.AnchorUtcTime, step);
                if (!lookup.TryAdd(key, row.HorizonTargets[step - 1]))
                {
                    throw new InvalidOperationException(
                        $"Duplicate ground-truth key for split '{split}', anchor '{row.AnchorUtcTime:yyyy-MM-dd HH:mm:ss}', and horizon '{step}'.");
                }
            }
        }

        return lookup;
    }

    private static List<DiagnosticsResidualSummary> BuildResidualSummaries(
        IReadOnlyList<PredictionPoint> predictions,
        IReadOnlyDictionary<(string Split, DateTime AnchorUtcTime, int HorizonStep), double> actualLookup)
    {
        var stats = new Dictionary<(string ModelName, string Split), RunningStats>(new ModelSplitComparer());
        foreach (var prediction in predictions)
        {
            var split = NormalizeSplit(prediction.Split);
            if (!actualLookup.TryGetValue((split, prediction.AnchorUtcTime, prediction.HorizonStep), out var actual))
            {
                continue;
            }

            var key = (prediction.ModelName, split);
            if (!stats.TryGetValue(key, out var running))
            {
                running = new RunningStats();
                stats[key] = running;
            }

            running.Add(prediction.Predicted - actual);
        }

        return stats
            .OrderBy(pair => pair.Key.ModelName, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Split, StringComparer.Ordinal)
            .Select(pair => BuildResidualSummary(pair.Key.ModelName, pair.Key.Split, pair.Value))
            .ToList();
    }

    private static List<DiagnosticsHorizonBucketSummary> BuildHorizonBucketSummaries(
        IReadOnlyList<PredictionPoint> predictions,
        IReadOnlyDictionary<(string Split, DateTime AnchorUtcTime, int HorizonStep), double> actualLookup)
    {
        var stats = new Dictionary<(string ModelName, string Split, int BucketStart), RunningStats>(new ModelSplitBucketComparer());
        foreach (var prediction in predictions)
        {
            var split = NormalizeSplit(prediction.Split);
            if (!actualLookup.TryGetValue((split, prediction.AnchorUtcTime, prediction.HorizonStep), out var actual))
            {
                continue;
            }

            var bucketStart = ((prediction.HorizonStep - 1) / HorizonBucketSize) * HorizonBucketSize + 1;
            var key = (prediction.ModelName, split, bucketStart);
            if (!stats.TryGetValue(key, out var running))
            {
                running = new RunningStats();
                stats[key] = running;
            }

            running.Add(prediction.Predicted - actual);
        }

        return stats
            .OrderBy(pair => pair.Key.ModelName, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Split, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.BucketStart)
            .Select(pair =>
            {
                var summary = BuildResidualSummary(pair.Key.ModelName, pair.Key.Split, pair.Value);
                var bucketEnd = Math.Min(PipelineConstants.HorizonSteps, pair.Key.BucketStart + HorizonBucketSize - 1);
                return new DiagnosticsHorizonBucketSummary(
                    summary.ModelName,
                    summary.Split,
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

    private static List<DiagnosticsHorizonStepSummary> BuildValidationHorizonSummaries(
        IReadOnlyList<PredictionPoint> predictions,
        IReadOnlyDictionary<(string Split, DateTime AnchorUtcTime, int HorizonStep), double> actualLookup)
    {
        var stats = new Dictionary<(string ModelName, int HorizonStep), RunningStats>();

        foreach (var prediction in predictions)
        {
            if (!string.Equals(prediction.ModelName, OverlayModelName, StringComparison.Ordinal))
            {
                continue;
            }

            var split = NormalizeSplit(prediction.Split);
            if (!string.Equals(split, "Validation", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!actualLookup.TryGetValue((split, prediction.AnchorUtcTime, prediction.HorizonStep), out var actual))
            {
                continue;
            }

            var key = (prediction.ModelName, prediction.HorizonStep);
            if (!stats.TryGetValue(key, out var running))
            {
                running = new RunningStats();
                stats[key] = running;
            }

            running.Add(prediction.Predicted - actual);
        }

        return stats
            .OrderBy(pair => pair.Key.ModelName, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.HorizonStep)
            .Select(pair =>
            {
                var summary = BuildResidualSummary(pair.Key.ModelName, "Validation", pair.Value);
                return new DiagnosticsHorizonStepSummary(
                    summary.ModelName,
                    pair.Key.HorizonStep,
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
        IReadOnlyDictionary<(string Split, DateTime AnchorUtcTime, int HorizonStep), double> actualLookup)
    {
        var selectedAnchors = predictions
            .GroupBy(point => NormalizeSplit(point.Split), StringComparer.Ordinal)
            .SelectMany(group =>
            {
                var split = group.Key;
                return group
                    .Select(point => point.AnchorUtcTime)
                    .Distinct()
                    .OrderBy(anchor => anchor)
                    .Where(anchor => group.Any(point =>
                        point.AnchorUtcTime == anchor && actualLookup.ContainsKey((split, anchor, point.HorizonStep))))
                    .Take(SampleAnchors)
                    .Select(anchor => (Split: split, AnchorUtcTime: anchor));
            })
            .ToHashSet();

        var sample = new List<DiagnosticsSamplePoint>();
        foreach (var prediction in predictions
                     .Where(point => selectedAnchors.Contains((NormalizeSplit(point.Split), point.AnchorUtcTime)))
                     .OrderBy(point => NormalizeSplit(point.Split), StringComparer.Ordinal)
                     .ThenBy(point => point.AnchorUtcTime)
                     .ThenBy(point => point.ModelName, StringComparer.Ordinal)
                     .ThenBy(point => point.HorizonStep))
        {
            var split = NormalizeSplit(prediction.Split);
            if (!actualLookup.TryGetValue((split, prediction.AnchorUtcTime, prediction.HorizonStep), out var actual))
            {
                continue;
            }

            sample.Add(new DiagnosticsSamplePoint(
                prediction.ModelName,
                split,
                prediction.AnchorUtcTime,
                prediction.AnchorUtcTime.AddMinutes(prediction.HorizonStep * PipelineConstants.MinutesPerStep),
                prediction.HorizonStep,
                prediction.Predicted,
                actual,
                prediction.Predicted - actual));
        }

        return sample;
    }

    private static List<DiagnosticsOverlayPoint> BuildOverlayPoints(
        IReadOnlyList<PredictionPoint> predictions,
        IReadOnlyDictionary<(string Split, DateTime AnchorUtcTime, int HorizonStep), double> actualLookup)
    {
        var overlays = new List<DiagnosticsOverlayPoint>();

        foreach (var point in predictions.Where(point =>
                     OverlayHorizonSteps.Contains(point.HorizonStep)
                     && string.Equals(point.ModelName, OverlayModelName, StringComparison.Ordinal)))
        {
            var split = NormalizeSplit(point.Split);
            if (!actualLookup.TryGetValue((split, point.AnchorUtcTime, point.HorizonStep), out var actual))
            {
                continue;
            }

            overlays.Add(new DiagnosticsOverlayPoint(
                split,
                point.ModelName,
                point.HorizonStep,
                point.AnchorUtcTime.AddMinutes(point.HorizonStep * PipelineConstants.MinutesPerStep),
                point.Predicted,
                actual));
        }

        return overlays;
    }

    private static DiagnosticsResidualSummary BuildResidualSummary(string modelName, string split, RunningStats running)
    {
        if (running.Count == 0)
        {
            return new DiagnosticsResidualSummary(modelName, split, 0, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d);
        }

        var quantiles = ComputeQuantiles(running.Residuals);
        return new DiagnosticsResidualSummary(
            modelName,
            split,
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
        var predictedIndexes = Enumerable.Range(1, PipelineConstants.HorizonSteps)
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

            for (var step = 1; step <= PipelineConstants.HorizonSteps; step++)
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

    private static List<PredictionPoint> BuildPredictionPoints(IReadOnlyList<Part3ForecastRow> forecastRows)
    {
        var points = new List<PredictionPoint>();
        var seenKeys = new HashSet<(string ModelName, DateTime AnchorUtcTime, int HorizonStep)>();

        foreach (var row in forecastRows)
        {
            if (row.PredictedTargets.Count < PipelineConstants.HorizonSteps)
            {
                throw new FormatException(
                    $"Invalid in-memory forecast row for model '{row.ModelName}' at anchor '{row.AnchorUtcTime:yyyy-MM-dd HH:mm:ss}': expected {PipelineConstants.HorizonSteps} predicted targets.");
            }

            for (var step = 1; step <= PipelineConstants.HorizonSteps; step++)
            {
                var predicted = row.PredictedTargets[step - 1];
                var key = (row.ModelName, row.AnchorUtcTime, step);
                if (!seenKeys.Add(key))
                {
                    throw new InvalidOperationException(
                        $"Duplicate prediction key for model '{row.ModelName}', anchor '{row.AnchorUtcTime:yyyy-MM-dd HH:mm:ss}', horizon '{step}'.");
                }

                points.Add(new PredictionPoint(row.ModelName, row.AnchorUtcTime, step, predicted, row.Split));
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

    private static void AppendTargetSeriesPlot(StringBuilder sb, IReadOnlyList<DiagnosticsTargetPoint> targetSeries)
    {
        sb.AppendLine("<h2>Target over time (TargetAtT)</h2>");

        if (targetSeries.Count == 0)
        {
            sb.AppendLine("<p>No target points available.</p>");
            return;
        }

        sb.AppendLine(BuildTargetSeriesSvg(targetSeries));

        var splitGroups = targetSeries
            .GroupBy(point => point.Split)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToList();
        sb.AppendLine($"<p><strong>Splits:</strong> {string.Join(", ", splitGroups.Select(group => Escape(group.Key)))}.</p>");
    }

    private static string BuildTargetSeriesSvg(IReadOnlyList<DiagnosticsTargetPoint> targetSeries)
    {
        const double xMin = 70d;
        const double xMax = 920d;
        const double yMin = 20d;
        const double yMax = 220d;
        static string F2(double value) => value.ToString("F2", InvariantCulture);

        var ordered = targetSeries.OrderBy(point => point.AnchorUtcTime).ToList();
        var minY = ordered.Count == 0 ? 0d : ordered.Min(point => point.TargetAtT);
        var maxY = ordered.Count == 0 ? 1d : ordered.Max(point => point.TargetAtT);
        if (Math.Abs(maxY - minY) < 1e-9)
        {
            maxY = minY + 1d;
        }

        var svg = new StringBuilder();
        svg.AppendLine("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 960 260\" width=\"960\" height=\"260\" role=\"img\" aria-label=\"Target over time\">");
    svg.AppendLine($"<line x1=\"{F2(xMin)}\" y1=\"{F2(yMax)}\" x2=\"{F2(xMax)}\" y2=\"{F2(yMax)}\" stroke=\"#444\" />");
    svg.AppendLine($"<line x1=\"{F2(xMin)}\" y1=\"{F2(yMin)}\" x2=\"{F2(xMin)}\" y2=\"{F2(yMax)}\" stroke=\"#444\" />");

        var yTickCount = 5;
        for (var tick = 0; tick <= yTickCount; tick++)
        {
            var fraction = tick / (double)yTickCount;
            var yValue = maxY - fraction * (maxY - minY);
            var y = yMin + fraction * (yMax - yMin);
            var yText = yValue.ToString("F0", InvariantCulture);
            svg.AppendLine($"<line x1=\"{F2(xMin - 5d)}\" y1=\"{F2(y)}\" x2=\"{F2(xMin)}\" y2=\"{F2(y)}\" stroke=\"#666\" />");
            svg.AppendLine($"<text x=\"{F2(xMin - 8d)}\" y=\"{F2(y + 4d)}\" text-anchor=\"end\" font-size=\"10\" fill=\"#666\">{yText}</text>");
        }

        if (ordered.Count > 0)
        {
            var xTickCount = Math.Min(6, ordered.Count);
            var seenLabels = new HashSet<string>(StringComparer.Ordinal);
            for (var tick = 0; tick < xTickCount; tick++)
            {
                var index = xTickCount == 1
                    ? 0
                    : (int)Math.Round(tick * (ordered.Count - 1d) / (xTickCount - 1d));

                var timestamp = ordered[index].AnchorUtcTime.ToString("yyyy-MM", InvariantCulture);
                if (!seenLabels.Add(timestamp) && tick < xTickCount - 1)
                {
                    continue;
                }

                var x = xMin + (ordered.Count == 1
                    ? 0d
                    : index / (ordered.Count - 1d) * (xMax - xMin));
                svg.AppendLine($"<line x1=\"{F2(x)}\" y1=\"{F2(yMax)}\" x2=\"{F2(x)}\" y2=\"{F2(yMax + 5d)}\" stroke=\"#666\" />");
                svg.AppendLine($"<text x=\"{F2(x)}\" y=\"{F2(yMax + 18d)}\" text-anchor=\"middle\" font-size=\"10\" fill=\"#666\">{timestamp}</text>");
            }
        }

        svg.AppendLine("<text x=\"495\" y=\"252\" text-anchor=\"middle\" font-size=\"11\" fill=\"#444\">Time (UTC)</text>");
        svg.AppendLine("<text x=\"18\" y=\"120\" transform=\"rotate(-90,18,120)\" text-anchor=\"middle\" font-size=\"11\" fill=\"#444\">Target value</text>");

        var indexedSeries = ordered
            .Select((point, index) => new { Point = point, Index = index + 1 })
            .ToList();

        var splitGroups = indexedSeries
            .GroupBy(item => item.Point.Split)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToList();

        var splitIndex = 0;
        foreach (var splitGroup in splitGroups)
        {
            var points = splitGroup
                .Select(item => (item.Index, item.Point.TargetAtT))
                .ToList();

            var polyline = BuildIndexedPolyline(points, minY, maxY, Math.Max(ordered.Count, 1));
            svg.AppendLine($"<polyline points=\"{polyline}\" fill=\"none\" stroke=\"hsl({(splitIndex * 127) % 360} 70% 40%)\" stroke-width=\"1.8\" />");
            splitIndex++;
        }

        svg.AppendLine("</svg>");
        return svg.ToString();
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

    private static void AppendSplitOverlayPlots(StringBuilder sb, IReadOnlyList<DiagnosticsOverlayPoint> overlayPoints)
    {
        sb.AppendLine($"<h2>Target vs {Escape(OverlayModelName)} over time by split ({string.Join(", ", OverlayHorizonSteps.Select(step => $"t+{step}"))})</h2>");

        var bySplitAndHorizon = overlayPoints
            .GroupBy(point => (point.Split, point.HorizonStep))
            .OrderBy(group => group.Key.Split, StringComparer.Ordinal)
            .ThenBy(group => group.Key.HorizonStep)
            .ToList();

        if (bySplitAndHorizon.Count == 0)
        {
            sb.AppendLine("<p>No overlay points available.</p>");
            return;
        }

        foreach (var group in bySplitAndHorizon)
        {
            sb.AppendLine($"<h3>Split: {Escape(group.Key.Split)} | Horizon: t+{group.Key.HorizonStep}</h3>");
            sb.AppendLine(BuildSplitOverlaySvg(group.ToList()));
        }
    }

    private static void AppendValidationHorizonPlot(StringBuilder sb, IReadOnlyList<DiagnosticsHorizonStepSummary> summaries)
    {
        sb.AppendLine("<h2>Validation error by prediction horizon (t+1..t+192)</h2>");

        if (summaries.Count == 0)
        {
            sb.AppendLine("<p>No validation horizon error points available.</p>");
            return;
        }

        sb.AppendLine(BuildValidationHorizonSvg(summaries));
    }

    private static void AppendResidualTable(StringBuilder sb, IReadOnlyList<DiagnosticsResidualSummary> summaries)
    {
        sb.AppendLine("<h2>Residual summary</h2>");
        sb.AppendLine("<table><thead><tr><th>Model</th><th>Split</th><th>Points</th><th>MeanError</th><th>MAE</th><th>RMSE</th><th>ResidualP05</th><th>ResidualP50</th><th>ResidualP95</th><th>Under%</th><th>Over%</th></tr></thead><tbody>");
        foreach (var summary in summaries.OrderBy(summary => summary.ModelName, StringComparer.Ordinal).ThenBy(summary => summary.Split, StringComparer.Ordinal))
        {
            sb.AppendLine($"<tr><td>{Escape(summary.ModelName)}</td><td>{Escape(summary.Split)}</td><td>{summary.EvaluatedPoints}</td><td>{summary.MeanError:F3}</td><td>{summary.Mae:F3}</td><td>{summary.Rmse:F3}</td><td>{summary.ResidualP05:F3}</td><td>{summary.ResidualP50:F3}</td><td>{summary.ResidualP95:F3}</td><td>{summary.UnderPredictionRate:F2}</td><td>{summary.OverPredictionRate:F2}</td></tr>");
        }
        sb.AppendLine("</tbody></table>");
    }

    private static void AppendHorizonBucketTable(StringBuilder sb, IReadOnlyList<DiagnosticsHorizonBucketSummary> summaries)
    {
        sb.AppendLine("<h2>Signed bias by horizon bucket</h2>");
        sb.AppendLine("<table><thead><tr><th>Model</th><th>Split</th><th>Bucket</th><th>Points</th><th>MeanError</th><th>MAE</th><th>RMSE</th><th>Under%</th><th>Over%</th></tr></thead><tbody>");
        foreach (var summary in summaries.OrderBy(summary => summary.ModelName, StringComparer.Ordinal).ThenBy(summary => summary.Split, StringComparer.Ordinal).ThenBy(summary => summary.HorizonStart))
        {
            sb.AppendLine($"<tr><td>{Escape(summary.ModelName)}</td><td>{Escape(summary.Split)}</td><td>{summary.HorizonStart}-{summary.HorizonEnd}</td><td>{summary.EvaluatedPoints}</td><td>{summary.MeanError:F3}</td><td>{summary.Mae:F3}</td><td>{summary.Rmse:F3}</td><td>{summary.UnderPredictionRate:F2}</td><td>{summary.OverPredictionRate:F2}</td></tr>");
        }
        sb.AppendLine("</tbody></table>");
    }

    private static void AppendSamplePlots(StringBuilder sb, IReadOnlyList<DiagnosticsSamplePoint> samplePoints)
    {
        sb.AppendLine("<h2>Predicted vs actual sampled anchors</h2>");

        var bySplit = samplePoints
            .GroupBy(point => point.Split, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToList();

        if (bySplit.Count == 0)
        {
            sb.AppendLine("<p>No matched sample points available.</p>");
            return;
        }

        foreach (var splitGroup in bySplit)
        {
            sb.AppendLine($"<h3>Split: {Escape(splitGroup.Key)}</h3>");
            var byAnchor = splitGroup
                .GroupBy(point => point.AnchorUtcTime)
                .OrderBy(group => group.Key)
                .ToList();

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

                sb.AppendLine($"<h4>Anchor {anchorGroup.Key:yyyy-MM-dd HH:mm:ss} UTC</h4>");
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
    }

    private static string BuildSplitOverlaySvg(IReadOnlyList<DiagnosticsOverlayPoint> splitPoints)
    {
        const double xMin = 70d;
        const double xMax = 920d;
        const double yMin = 20d;
        const double yMax = 220d;
        static string F2(double value) => value.ToString("F2", InvariantCulture);

        var horizonStep = splitPoints[0].HorizonStep;

        var actualSeries = splitPoints
            .GroupBy(point => point.ForecastUtcTime)
            .Select(group => group.First())
            .OrderBy(point => point.ForecastUtcTime)
            .Select(point => (point.ForecastUtcTime, point.Actual))
            .ToList();

        if (actualSeries.Count > OverlayMaxRenderPoints)
        {
            actualSeries = DownsampleSeries(actualSeries, OverlayMaxRenderPoints);
        }

        if (actualSeries.Count == 0)
        {
            return "<p>No aligned target/prediction points for this split.</p>";
        }

        var timestampIndex = actualSeries
            .Select((point, index) => new { point.ForecastUtcTime, Index = index + 1 })
            .ToDictionary(item => item.ForecastUtcTime, item => item.Index);

        var modelSeries = splitPoints
            .GroupBy(point => point.ModelName)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var points = group
                    .GroupBy(point => point.ForecastUtcTime)
                    .Select(item => item.First())
                    .Where(point => timestampIndex.ContainsKey(point.ForecastUtcTime))
                    .OrderBy(point => point.ForecastUtcTime)
                    .ToList();

                return (ModelName: group.Key, Points: points);
            })
            .ToList();

        var minY = Math.Min(
            actualSeries.Min(point => point.Item2),
            modelSeries.SelectMany(series => series.Points).DefaultIfEmpty().Min(point => point?.Predicted ?? 0d));
        var maxY = Math.Max(
            actualSeries.Max(point => point.Item2),
            modelSeries.SelectMany(series => series.Points).DefaultIfEmpty().Max(point => point?.Predicted ?? 0d));

        if (Math.Abs(maxY - minY) < 1e-9)
        {
            maxY = minY + 1d;
        }

        var svg = new StringBuilder();
        svg.AppendLine("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 960 260\" width=\"960\" height=\"260\" role=\"img\" aria-label=\"Target versus predicted over time\">");
        svg.AppendLine($"<line x1=\"{F2(xMin)}\" y1=\"{F2(yMax)}\" x2=\"{F2(xMax)}\" y2=\"{F2(yMax)}\" stroke=\"#444\" />");
        svg.AppendLine($"<line x1=\"{F2(xMin)}\" y1=\"{F2(yMin)}\" x2=\"{F2(xMin)}\" y2=\"{F2(yMax)}\" stroke=\"#444\" />");

        var yTickCount = 5;
        for (var tick = 0; tick <= yTickCount; tick++)
        {
            var fraction = tick / (double)yTickCount;
            var yValue = maxY - fraction * (maxY - minY);
            var y = yMin + fraction * (yMax - yMin);
            var yText = yValue.ToString("F0", InvariantCulture);
            svg.AppendLine($"<line x1=\"{F2(xMin - 5d)}\" y1=\"{F2(y)}\" x2=\"{F2(xMin)}\" y2=\"{F2(y)}\" stroke=\"#666\" />");
            svg.AppendLine($"<text x=\"{F2(xMin - 8d)}\" y=\"{F2(y + 4d)}\" text-anchor=\"end\" font-size=\"10\" fill=\"#666\">{yText}</text>");
        }

        var xTickCount = Math.Min(6, actualSeries.Count);
        for (var tick = 0; tick < xTickCount; tick++)
        {
            var index = xTickCount == 1
                ? 0
                : (int)Math.Round(tick * (actualSeries.Count - 1d) / (xTickCount - 1d));
            var label = actualSeries[index].ForecastUtcTime.ToString("yyyy-MM-dd", InvariantCulture);
            var x = xMin + (actualSeries.Count == 1
                ? 0d
                : index / (actualSeries.Count - 1d) * (xMax - xMin));
            svg.AppendLine($"<line x1=\"{F2(x)}\" y1=\"{F2(yMax)}\" x2=\"{F2(x)}\" y2=\"{F2(yMax + 5d)}\" stroke=\"#666\" />");
            svg.AppendLine($"<text x=\"{F2(x)}\" y=\"{F2(yMax + 18d)}\" text-anchor=\"middle\" font-size=\"10\" fill=\"#666\">{label}</text>");
        }

        svg.AppendLine($"<text x=\"495\" y=\"252\" text-anchor=\"middle\" font-size=\"11\" fill=\"#444\">Forecast time (UTC) | Horizon t+{horizonStep}</text>");
        svg.AppendLine("<text x=\"18\" y=\"120\" transform=\"rotate(-90,18,120)\" text-anchor=\"middle\" font-size=\"11\" fill=\"#444\">Target / prediction</text>");

        var actualPolyline = BuildIndexedPolyline(
            actualSeries
                .Select(point => (timestampIndex[point.ForecastUtcTime], point.Item2))
                .ToList(),
            minY,
            maxY,
            actualSeries.Count);
        svg.AppendLine($"<polyline points=\"{actualPolyline}\" fill=\"none\" stroke=\"#d32f2f\" stroke-width=\"2.4\" />");

        var dashStyles = new[] { "6 3", "2 2", "10 4", "1 4" };
        for (var modelIndex = 0; modelIndex < modelSeries.Count; modelIndex++)
        {
            var series = modelSeries[modelIndex];
            var points = series.Points
                .Select(point => (timestampIndex[point.ForecastUtcTime], point.Predicted))
                .ToList();
            var polyline = BuildIndexedPolyline(points, minY, maxY, actualSeries.Count);
            var dash = dashStyles[modelIndex % dashStyles.Length];
            svg.AppendLine($"<polyline points=\"{polyline}\" fill=\"none\" stroke=\"#1565c0\" stroke-width=\"1.4\" stroke-opacity=\"0.85\" stroke-dasharray=\"{dash}\" />");
        }

        svg.AppendLine("<text x=\"75\" y=\"15\" font-size=\"11\" fill=\"#d32f2f\">Actual (Target)</text>");
        for (var modelIndex = 0; modelIndex < modelSeries.Count; modelIndex++)
        {
            var y = 15 + (modelIndex + 1) * 14;
            svg.AppendLine($"<text x=\"75\" y=\"{y}\" font-size=\"11\" fill=\"#1565c0\">{Escape(modelSeries[modelIndex].ModelName)} (Predicted)</text>");
        }

        svg.AppendLine("</svg>");
        return svg.ToString();
    }

    public static IReadOnlyList<Part3PfiFeatureResult> ReadFeatureImportanceCsv(string csvPath)
    {
        var results = new List<Part3PfiFeatureResult>();
        using var reader = new StreamReader(csvPath);
        var header = reader.ReadLine(); // skip header
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split(';');
            if (parts.Length < 8)
            {
                continue;
            }

            results.Add(new Part3PfiFeatureResult(
                int.Parse(parts[0], InvariantCulture),
                parts[1],
                double.Parse(parts[2], InvariantCulture),
                double.Parse(parts[3], InvariantCulture),
                double.Parse(parts[4], InvariantCulture),
                double.Parse(parts[5], InvariantCulture),
                double.Parse(parts[6], InvariantCulture),
                double.Parse(parts[7], InvariantCulture)));
        }

        return results;
    }

    public static void WriteFeatureImportanceSvg(IReadOnlyList<Part3PfiFeatureResult> features, string outputSvgPath)
    {
        EnsureOutputDirectory(outputSvgPath);
        File.WriteAllText(outputSvgPath, BuildFeatureImportanceSvg(features));
    }

    private static void AppendFeatureImportanceSection(StringBuilder sb, IReadOnlyList<Part3PfiFeatureResult>? features)
    {
        if (features is not { Count: > 0 })
        {
            return;
        }

        sb.AppendLine("<h2>Feature importance (PFI)</h2>");
        sb.AppendLine(BuildFeatureImportanceSvg(features));

        sb.AppendLine("<table><tr><th>Rank</th><th>Feature</th><th>MAE delta</th><th>MAE &sigma;</th><th>RMSE delta</th><th>RMSE &sigma;</th><th>R&sup2; delta</th><th>R&sup2; &sigma;</th></tr>");
        foreach (var feature in features.OrderBy(f => f.Rank))
        {
            sb.AppendLine($"<tr><td>{feature.Rank}</td><td style=\"text-align:left\">{Escape(feature.FeatureName)}</td>" +
                $"<td>{feature.MaeDelta.ToString("F6", InvariantCulture)}</td><td>{feature.MaeDeltaStdDev.ToString("F6", InvariantCulture)}</td>" +
                $"<td>{feature.RmseDelta.ToString("F6", InvariantCulture)}</td><td>{feature.RmseDeltaStdDev.ToString("F6", InvariantCulture)}</td>" +
                $"<td>{feature.R2Delta.ToString("F6", InvariantCulture)}</td><td>{feature.R2DeltaStdDev.ToString("F6", InvariantCulture)}</td></tr>");
        }

        sb.AppendLine("</table>");
    }

    private static string BuildFeatureImportanceSvg(IReadOnlyList<Part3PfiFeatureResult> features)
    {
        static string F2(double value) => value.ToString("F2", InvariantCulture);

        var ordered = features.OrderBy(f => f.Rank).ToList();
        var barCount = ordered.Count;
        if (barCount == 0)
        {
            return "<p>No feature importance data available.</p>";
        }

        const double leftMargin = 160d;
        const double rightMargin = 20d;
        const double topMargin = 20d;
        const double barHeight = 18d;
        const double barGap = 4d;
        var svgWidth = 960d;
        var svgHeight = topMargin + barCount * (barHeight + barGap) + 30d;

        var maxAbsMae = ordered.Max(f => Math.Abs(f.MaeDelta));
        if (maxAbsMae < 1e-12)
        {
            maxAbsMae = 1d;
        }

        var chartWidth = svgWidth - leftMargin - rightMargin;

        var svg = new StringBuilder();
        svg.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {F2(svgWidth)} {F2(svgHeight)}\" width=\"{(int)svgWidth}\" height=\"{(int)svgHeight}\" role=\"img\" aria-label=\"Feature importance (PFI)\">");

        for (var index = 0; index < ordered.Count; index++)
        {
            var feature = ordered[index];
            var y = topMargin + index * (barHeight + barGap);
            var barWidth = Math.Abs(feature.MaeDelta) / maxAbsMae * chartWidth;
            var color = feature.MaeDelta >= 0 ? "#1565c0" : "#c62828";

            svg.AppendLine($"<text x=\"{F2(leftMargin - 6d)}\" y=\"{F2(y + barHeight / 2d + 4d)}\" text-anchor=\"end\" font-size=\"11\" fill=\"#333\">{Escape(feature.FeatureName)}</text>");
            svg.AppendLine($"<rect x=\"{F2(leftMargin)}\" y=\"{F2(y)}\" width=\"{F2(barWidth)}\" height=\"{F2(barHeight)}\" fill=\"{color}\" opacity=\"0.8\" />");
            svg.AppendLine($"<text x=\"{F2(leftMargin + barWidth + 4d)}\" y=\"{F2(y + barHeight / 2d + 4d)}\" font-size=\"10\" fill=\"#666\">{feature.MaeDelta.ToString("F4", InvariantCulture)}</text>");
        }

        svg.AppendLine($"<text x=\"{F2(leftMargin + chartWidth / 2d)}\" y=\"{F2(svgHeight - 4d)}\" text-anchor=\"middle\" font-size=\"11\" fill=\"#444\">|MAE delta| (higher = more important)</text>");
        svg.AppendLine("</svg>");
        return svg.ToString();
    }

    private static string BuildValidationHorizonSvg(IReadOnlyList<DiagnosticsHorizonStepSummary> summaries)
    {
        const double xMin = 70d;
        const double xMax = 920d;
        const double yMin = 20d;
        const double yMax = 220d;
        static string F2(double value) => value.ToString("F2", InvariantCulture);

        var ordered = summaries
            .OrderBy(summary => summary.ModelName, StringComparer.Ordinal)
            .ThenBy(summary => summary.HorizonStep)
            .ToList();

        if (ordered.Count == 0)
        {
            return "<p>No validation horizon error points available.</p>";
        }

        var minY = ordered.Min(summary => summary.MeanError);
        var maxY = ordered.Max(summary => summary.MeanError);
        if (Math.Abs(maxY - minY) < 1e-9)
        {
            maxY = minY + 1d;
        }

        var svg = new StringBuilder();
        svg.AppendLine("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 960 260\" width=\"960\" height=\"260\" role=\"img\" aria-label=\"Validation mean error by horizon\">");
        svg.AppendLine($"<line x1=\"{F2(xMin)}\" y1=\"{F2(yMax)}\" x2=\"{F2(xMax)}\" y2=\"{F2(yMax)}\" stroke=\"#444\" />");
        svg.AppendLine($"<line x1=\"{F2(xMin)}\" y1=\"{F2(yMin)}\" x2=\"{F2(xMin)}\" y2=\"{F2(yMax)}\" stroke=\"#444\" />");

        var yTickCount = 5;
        for (var tick = 0; tick <= yTickCount; tick++)
        {
            var fraction = tick / (double)yTickCount;
            var yValue = maxY - fraction * (maxY - minY);
            var y = yMin + fraction * (yMax - yMin);
            svg.AppendLine($"<line x1=\"{F2(xMin - 5d)}\" y1=\"{F2(y)}\" x2=\"{F2(xMin)}\" y2=\"{F2(y)}\" stroke=\"#666\" />");
            svg.AppendLine($"<text x=\"{F2(xMin - 8d)}\" y=\"{F2(y + 4d)}\" text-anchor=\"end\" font-size=\"10\" fill=\"#666\">{yValue.ToString("F0", InvariantCulture)}</text>");
        }

        var xTicks = new[] { 1, 24, 48, 72, 96, 120, 144, 168, 192 };
        foreach (var tick in xTicks)
        {
            var x = xMin + (tick - 1d) / (PipelineConstants.HorizonSteps - 1d) * (xMax - xMin);
            svg.AppendLine($"<line x1=\"{F2(x)}\" y1=\"{F2(yMax)}\" x2=\"{F2(x)}\" y2=\"{F2(yMax + 5d)}\" stroke=\"#666\" />");
            svg.AppendLine($"<text x=\"{F2(x)}\" y=\"{F2(yMax + 18d)}\" text-anchor=\"middle\" font-size=\"10\" fill=\"#666\">t+{tick}</text>");
        }

        svg.AppendLine("<text x=\"495\" y=\"252\" text-anchor=\"middle\" font-size=\"11\" fill=\"#444\">Prediction horizon</text>");
        svg.AppendLine("<text x=\"18\" y=\"120\" transform=\"rotate(-90,18,120)\" text-anchor=\"middle\" font-size=\"11\" fill=\"#444\">Mean error (pred - actual)</text>");

        var modelGroups = ordered
            .GroupBy(summary => summary.ModelName)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToList();

        var zeroNormalized = (0d - minY) / (maxY - minY);
        var zeroY = yMax - zeroNormalized * (yMax - yMin);
        if (zeroY >= yMin && zeroY <= yMax)
        {
            svg.AppendLine($"<line x1=\"{F2(xMin)}\" y1=\"{F2(zeroY)}\" x2=\"{F2(xMax)}\" y2=\"{F2(zeroY)}\" stroke=\"#999\" stroke-dasharray=\"4 4\" />");
        }

        for (var modelIndex = 0; modelIndex < modelGroups.Count; modelIndex++)
        {
            var group = modelGroups[modelIndex];
            var polyline = BuildPolyline(
                group.Select(summary => (summary.HorizonStep, summary.MeanError)).ToList(),
                minY,
                maxY);
            var color = $"hsl({(modelIndex * 97) % 360} 70% 40%)";
            svg.AppendLine($"<polyline points=\"{polyline}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"2\" />");
            svg.AppendLine($"<text x=\"75\" y=\"{15 + modelIndex * 14}\" font-size=\"11\" fill=\"{color}\">{Escape(group.Key)}</text>");
        }

        svg.AppendLine("</svg>");
        return svg.ToString();
    }

    private static List<(DateTime ForecastUtcTime, double Value)> DownsampleSeries(
        IReadOnlyList<(DateTime ForecastUtcTime, double Value)> points,
        int maxPoints)
    {
        if (points.Count <= maxPoints)
        {
            return points.ToList();
        }

        var sampled = new List<(DateTime ForecastUtcTime, double Value)>(maxPoints);
        for (var index = 0; index < maxPoints; index++)
        {
            var sourceIndex = (int)Math.Round(index * (points.Count - 1d) / (maxPoints - 1d));
            sampled.Add(points[sourceIndex]);
        }

        return sampled;
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
        const int maxStep = PipelineConstants.HorizonSteps;

        return string.Join(' ', points.Select(point =>
        {
            var x = xMin + (point.XStep - 1d) / (maxStep - 1d) * (xMax - xMin);
            var normalized = (point.YValue - minY) / (maxY - minY);
            var y = yMax - normalized * (yMax - yMin);
            return $"{x.ToString("F2", InvariantCulture)},{y.ToString("F2", InvariantCulture)}";
        }));
    }

    private static string BuildIndexedPolyline(IReadOnlyList<(int XIndex, double YValue)> points, double minY, double maxY, int maxIndex)
    {
        if (points.Count == 0)
        {
            return string.Empty;
        }

        const double xMin = 40d;
        const double xMax = 920d;
        const double yMin = 20d;
        const double yMax = 220d;

        var denominator = Math.Max(1d, maxIndex - 1d);
        return string.Join(' ', points.Select(point =>
        {
            var x = xMin + (point.XIndex - 1d) / denominator * (xMax - xMin);
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

    private static string SanitizeFileNameSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var chars = value
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray();

        return new string(chars);
    }

    private sealed class ModelSplitComparer : IEqualityComparer<(string ModelName, string Split)>
    {
        public bool Equals((string ModelName, string Split) x, (string ModelName, string Split) y)
        {
            return string.Equals(x.ModelName, y.ModelName, StringComparison.Ordinal)
                   && string.Equals(x.Split, y.Split, StringComparison.Ordinal);
        }

        public int GetHashCode((string ModelName, string Split) obj)
        {
            return HashCode.Combine(obj.ModelName, obj.Split);
        }
    }

    private sealed class ModelSplitBucketComparer : IEqualityComparer<(string ModelName, string Split, int BucketStart)>
    {
        public bool Equals((string ModelName, string Split, int BucketStart) x, (string ModelName, string Split, int BucketStart) y)
        {
            return string.Equals(x.ModelName, y.ModelName, StringComparison.Ordinal)
                   && string.Equals(x.Split, y.Split, StringComparison.Ordinal)
                   && x.BucketStart == y.BucketStart;
        }

        public int GetHashCode((string ModelName, string Split, int BucketStart) obj)
        {
            return HashCode.Combine(obj.ModelName, obj.Split, obj.BucketStart);
        }
    }
}