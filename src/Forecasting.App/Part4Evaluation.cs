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

    private sealed record ActualPoint(DateTime AnchorUtcTime, int HorizonStep, double Actual);

    public static Part4RunResult RunEvaluation(string part2InputCsvPath, string part3PredictionsCsvPath)
    {
        var part2Rows = Part3Modeling.ReadPart2DatasetCsv(part2InputCsvPath);
        var predictionPoints = PredictionPoints.ReadFromPredictionsCsv(part3PredictionsCsvPath);
        return RunEvaluation(part2Rows, predictionPoints);
    }

    public static Part4RunResult RunEvaluation(
        IReadOnlyList<Part3InputRow> part2Rows,
        IReadOnlyList<Part3ForecastRow> forecastRows)
    {
        var predictionPoints = PredictionPoints.BuildFromForecastRows(forecastRows);
        return RunEvaluation(part2Rows, predictionPoints);
    }

    private static Part4RunResult RunEvaluation(
        IReadOnlyList<Part3InputRow> part2Rows,
        IReadOnlyList<ForecastPredictionPoint> predictionPoints)
    {
        var actualPoints = BuildValidationActualPoints(part2Rows);
        var actualLookup = BuildActualLookup(actualPoints);

        var validationPredictionPoints = predictionPoints
            .Where(point => string.Equals(point.Split, "Validation", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var metrics = ComputeMetrics(validationPredictionPoints, actualLookup);
        var sample = BuildDeterministicSample(validationPredictionPoints, actualLookup);

        return new Part4RunResult(
            DateTime.UtcNow,
            actualPoints.Count,
            validationPredictionPoints.Count,
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
        IReadOnlyList<ForecastPredictionPoint> predictionPoints,
        IReadOnlyDictionary<(DateTime AnchorUtcTime, int HorizonStep), double> actualLookup)
    {
        var accumulators = new Dictionary<string, MetricsAccumulator>(StringComparer.Ordinal);

        foreach (var modelName in predictionPoints.Select(point => point.ModelName).Distinct(StringComparer.Ordinal))
        {
            accumulators[modelName] = new MetricsAccumulator();
        }

        foreach (var prediction in predictionPoints)
        {
            if (!actualLookup.TryGetValue((prediction.AnchorUtcTime, prediction.HorizonStep), out var actual))
            {
                continue;
            }

            var accumulator = accumulators[prediction.ModelName];

            var error = prediction.Predicted - actual;
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
        IReadOnlyList<ForecastPredictionPoint> predictionPoints,
        IReadOnlyDictionary<(DateTime AnchorUtcTime, int HorizonStep), double> actualLookup)
    {
        var firstAnchor = predictionPoints
            .Select(point => point.AnchorUtcTime)
            .Distinct()
            .OrderBy(timestamp => timestamp)
            .FirstOrDefault(anchor => predictionPoints.Any(point =>
                point.AnchorUtcTime == anchor && actualLookup.ContainsKey((point.AnchorUtcTime, point.HorizonStep))));

        if (firstAnchor == default)
        {
            return [];
        }

        var sample = new List<Part4SamplePoint>();
        var pointsByModelAndStep = predictionPoints
            .Where(point => point.AnchorUtcTime == firstAnchor)
            .ToDictionary(
                point => (point.ModelName, point.HorizonStep),
                point => point,
                new ModelStepComparer());

        foreach (var key in pointsByModelAndStep.Keys.OrderBy(key => key.ModelName, StringComparer.Ordinal).ThenBy(key => key.HorizonStep))
        {
            var prediction = pointsByModelAndStep[key];
            if (!actualLookup.TryGetValue((prediction.AnchorUtcTime, prediction.HorizonStep), out var actual))
            {
                continue;
            }

            sample.Add(new Part4SamplePoint(
                prediction.ModelName,
                prediction.AnchorUtcTime,
                prediction.AnchorUtcTime.AddMinutes(prediction.HorizonStep * PipelineConstants.MinutesPerStep),
                prediction.HorizonStep,
                prediction.Predicted,
                actual));
        }

        return sample;
    }

    private static Dictionary<(DateTime AnchorUtcTime, int HorizonStep), double> BuildActualLookup(IReadOnlyList<ActualPoint> points)
    {
        var lookup = new Dictionary<(DateTime AnchorUtcTime, int HorizonStep), double>();
        foreach (var point in points)
        {
            var key = (point.AnchorUtcTime, point.HorizonStep);
            if (!lookup.TryAdd(key, point.Actual))
            {
                throw new InvalidOperationException(
                    $"Duplicate ground-truth key for anchor '{point.AnchorUtcTime:yyyy-MM-dd HH:mm:ss}' and horizon '{point.HorizonStep}'.");
            }
        }

        return lookup;
    }

    private static List<ActualPoint> BuildValidationActualPoints(IReadOnlyList<Part3InputRow> rows)
    {
        var points = new List<ActualPoint>();
        foreach (var row in rows)
        {
            if (!string.Equals(row.Split, "Validation", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            for (var step = 1; step <= PipelineConstants.HorizonSteps; step++)
            {
                points.Add(new ActualPoint(row.AnchorUtcTime, step, row.HorizonTargets[step - 1]));
            }
        }

        return points;
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

    private sealed class ModelStepComparer : IEqualityComparer<(string ModelName, int HorizonStep)>
    {
        public bool Equals((string ModelName, int HorizonStep) x, (string ModelName, int HorizonStep) y)
        {
            return string.Equals(x.ModelName, y.ModelName, StringComparison.Ordinal) && x.HorizonStep == y.HorizonStep;
        }

        public int GetHashCode((string ModelName, int HorizonStep) obj)
        {
            return HashCode.Combine(obj.ModelName, obj.HorizonStep);
        }
    }
}