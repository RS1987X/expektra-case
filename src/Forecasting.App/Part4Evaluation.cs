using System.Globalization;
using System.Text;

namespace Forecasting.App;

public sealed record Part4ModelMetrics(
    string ModelName,
    int EvaluatedPoints,
    int MapeEvaluatedPoints,
    int ZeroActualExcludedPoints,
    double Mae,
    double Rmse,
    double Mape);

public sealed record Part4SamplePoint(
    string ModelName,
    DateTime AnchorUtcTime,
    DateTime ForecastUtcTime,
    int HorizonStep,
    double Predicted,
    double Actual);

public sealed record Part4RunResult(
    DateTime GeneratedAtUtc,
    int ValidationActualPoints,
    int ValidationPredictionPoints,
    IReadOnlyList<Part4ModelMetrics> Metrics,
    IReadOnlyList<Part4SamplePoint> SamplePoints,
    IReadOnlyList<Part4OverlayPoint> OverlayPoints);

public sealed record Part4OverlayPoint(
    string ModelName,
    int HorizonStep,
    DateTime ForecastUtcTime,
    double Predicted,
    double Actual);

public static class Part4Evaluation
{
    private static readonly CultureInfo InvariantCulture = CultureInfo.InvariantCulture;
    private const int FocusHorizonStep = 92;
    private const int FocusWindowHours = 48;

    public static Part4RunResult RunEvaluation(string part2InputCsvPath, string part3PredictionsCsvPath)
    {
        var part2Rows = Part3Modeling.ReadPart2DatasetCsv(part2InputCsvPath);
        var forecasts = Part3Modeling.ReadForecastsCsv(part3PredictionsCsvPath);
        return RunEvaluation(part2Rows, forecasts);
    }

    public static Part4RunResult RunEvaluation(
        IReadOnlyList<Part2SupervisedRow> part2Rows,
        IReadOnlyList<Part3ForecastRow> forecastRows)
    {
        ValidateForecastRows(forecastRows);

        var actualLookup = BuildValidationActualLookup(part2Rows);

        var validationForecasts = forecastRows
            .Where(row => string.Equals(row.Split, "Validation", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var metrics = ComputeMetrics(validationForecasts, actualLookup);
        var sample = BuildDeterministicSample(validationForecasts, actualLookup);
        var overlayPoints = BuildOverlayPoints(validationForecasts, actualLookup);

        return new Part4RunResult(
            DateTime.UtcNow,
            actualLookup.Count * PipelineConstants.HorizonSteps,
            validationForecasts.Count * PipelineConstants.HorizonSteps,
            metrics,
            sample,
            overlayPoints);
    }

    public static void WriteMetricsCsv(Part4RunResult result, string outputCsvPath)
    {
        FileOutput.EnsureParentDirectory(outputCsvPath);

        using var writer = new StreamWriter(outputCsvPath, false);
        writer.WriteLine("ModelName;EvaluatedPoints;MapeEvaluatedPoints;ZeroActualExcludedPoints;MAE;RMSE;MAPE");

        foreach (var metric in result.Metrics.OrderBy(metric => metric.ModelName, StringComparer.Ordinal))
        {
            writer.WriteLine(string.Join(';',
                metric.ModelName,
                metric.EvaluatedPoints.ToString(InvariantCulture),
                metric.MapeEvaluatedPoints.ToString(InvariantCulture),
                metric.ZeroActualExcludedPoints.ToString(InvariantCulture),
                metric.Mae.ToString("F6", InvariantCulture),
                metric.Rmse.ToString("F6", InvariantCulture),
                metric.Mape.ToString("F6", InvariantCulture)));
        }
    }

    public static void WriteSampleCsv(Part4RunResult result, string outputCsvPath)
    {
        FileOutput.EnsureParentDirectory(outputCsvPath);

        using var writer = new StreamWriter(outputCsvPath, false);
        writer.WriteLine("ModelName;AnchorUtcTime;ForecastUtcTime;HorizonStep;Predicted;Actual");

        foreach (var samplePoint in result.SamplePoints
                     .OrderBy(point => point.ModelName, StringComparer.Ordinal)
                     .ThenBy(point => point.HorizonStep))
        {
            writer.WriteLine(string.Join(';',
                samplePoint.ModelName,
                samplePoint.AnchorUtcTime.ToString("yyyy-MM-dd HH:mm:ss", InvariantCulture),
                samplePoint.ForecastUtcTime.ToString("yyyy-MM-dd HH:mm:ss", InvariantCulture),
                samplePoint.HorizonStep.ToString(InvariantCulture),
                samplePoint.Predicted.ToString("F6", InvariantCulture),
                samplePoint.Actual.ToString("F6", InvariantCulture)));
        }
    }

    public static IReadOnlyList<(string ModelName, string CsvPath, string SvgPath)> WriteValidationPlotArtifactsByModel(
        Part4RunResult result,
        string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        var written = new List<(string ModelName, string CsvPath, string SvgPath)>();
        var modelNames = result.OverlayPoints
            .Select(point => point.ModelName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var windowSize = FocusWindowHours * (60 / PipelineConstants.MinutesPerStep); // 192 anchors

        foreach (var modelName in modelNames)
        {
            // Select predictions at the fixed horizon step across all validation anchors.
            var fixedHorizonPoints = result.OverlayPoints
                .Where(point => string.Equals(point.ModelName, modelName, StringComparison.Ordinal)
                                && point.HorizonStep == FocusHorizonStep)
                .OrderBy(point => point.ForecastUtcTime)
                .ToList();

            if (fixedHorizonPoints.Count == 0)
            {
                continue;
            }

            // Take the last <windowSize> consecutive anchor points (most recent 48h window).
            var windowedPoints = fixedHorizonPoints
                .Skip(Math.Max(0, fixedHorizonPoints.Count - windowSize))
                .ToList();

            var modelToken = GetModelArtifactToken(modelName);
            var csvPath = Path.Combine(outputDirectory, $"part4_{modelToken}_validation_tplus{FocusHorizonStep}_{FocusWindowHours}h.csv");
            var svgPath = Path.Combine(outputDirectory, $"part4_{modelToken}_validation_tplus{FocusHorizonStep}_{FocusWindowHours}h.svg");

            FileOutput.EnsureParentDirectory(csvPath);
            using (var writer = new StreamWriter(csvPath, false))
            {
                writer.WriteLine("ModelName;AnchorUtcTime;HorizonStep;ForecastUtcTime;Actual;Predicted;Residual");
                foreach (var point in windowedPoints)
                {
                    var anchorUtcTime = point.ForecastUtcTime.AddMinutes(-point.HorizonStep * PipelineConstants.MinutesPerStep);
                    var residual = point.Predicted - point.Actual;
                    writer.WriteLine(string.Join(';',
                        point.ModelName,
                        anchorUtcTime.ToString("yyyy-MM-dd HH:mm:ss", InvariantCulture),
                        point.HorizonStep.ToString(InvariantCulture),
                        point.ForecastUtcTime.ToString("yyyy-MM-dd HH:mm:ss", InvariantCulture),
                        point.Actual.ToString("F6", InvariantCulture),
                        point.Predicted.ToString("F6", InvariantCulture),
                        residual.ToString("F6", InvariantCulture)));
                }
            }

            FileOutput.EnsureParentDirectory(svgPath);
            File.WriteAllText(svgPath, BuildFixedHorizonSvg(windowedPoints, modelName));
            written.Add((modelName, csvPath, svgPath));
        }

        return written;
    }

    private static List<Part4ModelMetrics> ComputeMetrics(
        IReadOnlyList<Part3ForecastRow> forecasts,
        IReadOnlyDictionary<DateTime, double[]> actualLookup)
    {
        var accumulators = new Dictionary<string, MetricsAccumulator>(StringComparer.Ordinal);

        foreach (var modelName in forecasts.Select(row => row.ModelName).Distinct(StringComparer.Ordinal))
        {
            accumulators[modelName] = new MetricsAccumulator();
        }

        foreach (var forecast in forecasts)
        {
            if (!actualLookup.TryGetValue(forecast.AnchorUtcTime, out var actuals))
            {
                continue;
            }

            var accumulator = accumulators[forecast.ModelName];

            for (var step = 1; step <= PipelineConstants.HorizonSteps; step++)
            {
                var actual = actuals[step - 1];
                var error = forecast.PredictedTargets[step - 1] - actual;
                var absError = Math.Abs(error);
                accumulator.EvaluatedPoints++;
                accumulator.SumAbs += absError;
                accumulator.SumSquared += error * error;

                if (actual == 0d)
                {
                    accumulator.ZeroActualExcludedPoints++;
                    continue;
                }

                accumulator.MapeEvaluatedPoints++;
                accumulator.SumApePercent += Math.Abs(error / actual) * 100d;
            }
        }

        var metrics = new List<Part4ModelMetrics>();
        foreach (var (modelName, accumulator) in accumulators.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (accumulator.EvaluatedPoints == 0)
            {
                throw new InvalidOperationException($"Model '{modelName}' has no evaluable prediction points in validation.");
            }

            var mae = accumulator.SumAbs / accumulator.EvaluatedPoints;
            var rmse = Math.Sqrt(accumulator.SumSquared / accumulator.EvaluatedPoints);
            var mape = accumulator.MapeEvaluatedPoints == 0
                ? 0d
                : accumulator.SumApePercent / accumulator.MapeEvaluatedPoints;

            metrics.Add(new Part4ModelMetrics(
                modelName,
                accumulator.EvaluatedPoints,
                accumulator.MapeEvaluatedPoints,
                accumulator.ZeroActualExcludedPoints,
                mae,
                rmse,
                mape));
        }

        if (metrics.Count == 0)
        {
            throw new InvalidOperationException("No evaluable prediction points were found for validation.");
        }

        return metrics;
    }

    private static List<Part4SamplePoint> BuildDeterministicSample(
        IReadOnlyList<Part3ForecastRow> forecasts,
        IReadOnlyDictionary<DateTime, double[]> actualLookup)
    {
        var firstAnchor = forecasts
            .Select(row => row.AnchorUtcTime)
            .Distinct()
            .OrderBy(timestamp => timestamp)
            .FirstOrDefault(anchor => actualLookup.ContainsKey(anchor));

        if (firstAnchor == default)
        {
            return [];
        }

        var actuals = actualLookup[firstAnchor];
        var sample = new List<Part4SamplePoint>();

        foreach (var forecast in forecasts
                     .Where(row => row.AnchorUtcTime == firstAnchor)
                     .OrderBy(row => row.ModelName, StringComparer.Ordinal))
        {
            for (var step = 1; step <= PipelineConstants.HorizonSteps; step++)
            {
                sample.Add(new Part4SamplePoint(
                    forecast.ModelName,
                    forecast.AnchorUtcTime,
                    forecast.AnchorUtcTime.AddMinutes(step * PipelineConstants.MinutesPerStep),
                    step,
                    forecast.PredictedTargets[step - 1],
                    actuals[step - 1]));
            }
        }

        return sample;
    }

    private static List<Part4OverlayPoint> BuildOverlayPoints(
        IReadOnlyList<Part3ForecastRow> forecasts,
        IReadOnlyDictionary<DateTime, double[]> actualLookup)
    {
        var overlayPoints = new List<Part4OverlayPoint>();

        foreach (var forecast in forecasts)
        {
            if (!actualLookup.TryGetValue(forecast.AnchorUtcTime, out var actuals))
            {
                continue;
            }

            for (var step = 1; step <= PipelineConstants.HorizonSteps; step++)
            {
                overlayPoints.Add(new Part4OverlayPoint(
                    forecast.ModelName,
                    step,
                    forecast.AnchorUtcTime.AddMinutes(step * PipelineConstants.MinutesPerStep),
                    forecast.PredictedTargets[step - 1],
                    actuals[step - 1]));
            }
        }

        return overlayPoints;
    }

    private static string BuildFixedHorizonSvg(IReadOnlyList<Part4OverlayPoint> points, string modelName)
    {
        const int width = 1200;
        const int height = 420;
        const int left = 70;
        const int right = 20;
        const int top = 25;
        const int bottom = 55;

        var minValue = points.Min(point => Math.Min(point.Actual, point.Predicted));
        var maxValue = points.Max(point => Math.Max(point.Actual, point.Predicted));
        if (Math.Abs(maxValue - minValue) < 1e-9)
        {
            maxValue = minValue + 1d;
        }

        var plotWidth = width - left - right;
        var plotHeight = height - top - bottom;

        double X(int index)
        {
            if (points.Count == 1)
            {
                return left + plotWidth / 2d;
            }

            return left + (plotWidth * index / (double)(points.Count - 1));
        }

        double Y(double value)
        {
            var normalized = (value - minValue) / (maxValue - minValue);
            return top + plotHeight - (normalized * plotHeight);
        }

        var actualSeries = string.Join(' ', points.Select((point, index) => $"{X(index):F2},{Y(point.Actual):F2}"));
        var predictedSeries = string.Join(' ', points.Select((point, index) => $"{X(index):F2},{Y(point.Predicted):F2}"));
        var firstTs = points[0].ForecastUtcTime.ToString("yyyy-MM-dd HH:mm", InvariantCulture);
        var lastTs = points[^1].ForecastUtcTime.ToString("yyyy-MM-dd HH:mm", InvariantCulture);
        var horizonStep = points[0].HorizonStep;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\">");
        sb.AppendLine("<style>text { font-family: Arial, sans-serif; fill: #1f2937; } .axis { stroke: #6b7280; stroke-width: 1; } .grid { stroke: #e5e7eb; stroke-width: 1; } .actual { fill: none; stroke: #1d4ed8; stroke-width: 2; } .pred { fill: none; stroke: #dc2626; stroke-width: 2; } .title { font-size: 14px; font-weight: 600; } .label { font-size: 11px; } .legend { font-size: 12px; }</style>");
        sb.AppendLine($"<text class=\"title\" x=\"{left}\" y=\"16\">{EscapeXml(modelName)} Predicted vs Actual at fixed horizon t+{horizonStep} ({FocusWindowHours}h window)</text>");
        sb.AppendLine($"<line class=\"axis\" x1=\"{left}\" y1=\"{top + plotHeight}\" x2=\"{left + plotWidth}\" y2=\"{top + plotHeight}\" />");
        sb.AppendLine($"<line class=\"axis\" x1=\"{left}\" y1=\"{top}\" x2=\"{left}\" y2=\"{top + plotHeight}\" />");
        sb.AppendLine($"<polyline class=\"actual\" points=\"{actualSeries}\" />");
        sb.AppendLine($"<polyline class=\"pred\" points=\"{predictedSeries}\" />");
        sb.AppendLine($"<text class=\"label\" x=\"{left}\" y=\"{height - 12}\">{firstTs}</text>");
        sb.AppendLine($"<text class=\"label\" x=\"{left + plotWidth - 130}\" y=\"{height - 12}\">{lastTs}</text>");
        sb.AppendLine($"<text class=\"label\" x=\"8\" y=\"{top + 10}\">{maxValue:F0}</text>");
        sb.AppendLine($"<text class=\"label\" x=\"8\" y=\"{top + plotHeight}\">{minValue:F0}</text>");
        sb.AppendLine($"<line x1=\"{left + plotWidth - 260}\" y1=\"30\" x2=\"{left + plotWidth - 230}\" y2=\"30\" class=\"actual\" /><text class=\"legend\" x=\"{left + plotWidth - 225}\" y=\"34\">Actual</text>");
        sb.AppendLine($"<line x1=\"{left + plotWidth - 160}\" y1=\"30\" x2=\"{left + plotWidth - 130}\" y2=\"30\" class=\"pred\" /><text class=\"legend\" x=\"{left + plotWidth - 125}\" y=\"34\">Predicted</text>");
        sb.AppendLine("</svg>");

        return sb.ToString();
    }



    private static string SanitizeFileNameSegment(string value)
    {
        var chars = value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray();
        return new string(chars);
    }

    private static string GetModelArtifactToken(string modelName)
    {
        return modelName switch
        {
            "BaselineSeasonal" => "baselineseasonal",
            "FastTreeRecursive" => "fasttree",
            _ => SanitizeFileNameSegment(modelName).ToLowerInvariant()
        };
    }

    private static string EscapeXml(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
    }

    private static Dictionary<DateTime, double[]> BuildValidationActualLookup(IReadOnlyList<Part2SupervisedRow> rows)
    {
        var lookup = new Dictionary<DateTime, double[]>();
        foreach (var row in rows)
        {
            if (!string.Equals(row.Split, "Validation", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!lookup.TryAdd(row.AnchorUtcTime, row.HorizonTargets.ToArray()))
            {
                throw new InvalidOperationException(
                    $"Duplicate ground-truth key for anchor '{row.AnchorUtcTime:yyyy-MM-dd HH:mm:ss}'.");
            }
        }

        return lookup;
    }

    private sealed class MetricsAccumulator
    {
        public int EvaluatedPoints { get; set; }
        public int MapeEvaluatedPoints { get; set; }
        public int ZeroActualExcludedPoints { get; set; }
        public double SumAbs { get; set; }
        public double SumSquared { get; set; }
        public double SumApePercent { get; set; }
    }

    private static void ValidateForecastRows(IReadOnlyList<Part3ForecastRow> forecastRows)
    {
        var seenKeys = new HashSet<(string ModelName, DateTime AnchorUtcTime)>();

        foreach (var row in forecastRows)
        {
            if (!seenKeys.Add((row.ModelName, row.AnchorUtcTime)))
            {
                throw new InvalidOperationException(
                    $"Duplicate prediction key for model '{row.ModelName}', anchor '{row.AnchorUtcTime:yyyy-MM-dd HH:mm:ss}'.");
            }

            for (var step = 0; step < row.PredictedTargets.Count; step++)
            {
                if (!double.IsFinite(row.PredictedTargets[step]))
                {
                    throw new FormatException(
                        $"Forecast for model '{row.ModelName}', anchor '{row.AnchorUtcTime:yyyy-MM-dd HH:mm:ss}', step {step + 1} contains a non-finite value.");
                }
            }
        }
    }
}