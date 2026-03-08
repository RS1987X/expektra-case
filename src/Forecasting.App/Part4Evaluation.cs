using System.Globalization;

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
    IReadOnlyList<Part4SamplePoint> SamplePoints);

public static class Part4Evaluation
{
    private static readonly CultureInfo InvariantCulture = CultureInfo.InvariantCulture;

    public static Part4RunResult RunEvaluation(string part2InputCsvPath, string part3PredictionsCsvPath)
    {
        var part2Rows = Part3Modeling.ReadPart2DatasetCsv(part2InputCsvPath);
        var forecasts = Part3Modeling.ReadForecastsCsv(part3PredictionsCsvPath);
        return RunEvaluation(part2Rows, forecasts);
    }

    public static Part4RunResult RunEvaluation(
        IReadOnlyList<Part3InputRow> part2Rows,
        IReadOnlyList<Part3ForecastRow> forecastRows)
    {
        ValidateForecastRows(forecastRows);

        var actualLookup = BuildValidationActualLookup(part2Rows);

        var validationForecasts = forecastRows
            .Where(row => string.Equals(row.Split, "Validation", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var metrics = ComputeMetrics(validationForecasts, actualLookup);
        var sample = BuildDeterministicSample(validationForecasts, actualLookup);

        return new Part4RunResult(
            DateTime.UtcNow,
            actualLookup.Count * PipelineConstants.HorizonSteps,
            validationForecasts.Count * PipelineConstants.HorizonSteps,
            metrics,
            sample);
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

    private static Dictionary<DateTime, double[]> BuildValidationActualLookup(IReadOnlyList<Part3InputRow> rows)
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