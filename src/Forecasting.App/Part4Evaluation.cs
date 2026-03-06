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
    private const int HorizonSteps = 192;
    private const int MinutesPerStep = 15;
    private static readonly CultureInfo InvariantCulture = CultureInfo.InvariantCulture;

    private sealed record PredictionPoint(string ModelName, DateTime AnchorUtcTime, int HorizonStep, double Predicted, string Split);

    private sealed record ActualPoint(DateTime AnchorUtcTime, int HorizonStep, double Actual);

    public static Part4RunResult RunEvaluation(string part2InputCsvPath, string part3PredictionsCsvPath)
    {
        var part2Rows = Part3Modeling.ReadPart2DatasetCsv(part2InputCsvPath);
        var actualPoints = BuildValidationActualPoints(part2Rows);
        var actualLookup = BuildActualLookup(actualPoints);

        var predictionPoints = ReadPredictionPoints(part3PredictionsCsvPath)
            .Where(point => string.Equals(point.Split, "Validation", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var metrics = ComputeMetrics(predictionPoints, actualLookup);
        var sample = BuildDeterministicSample(predictionPoints, actualLookup);

        return new Part4RunResult(
            DateTime.UtcNow,
            actualPoints.Count,
            predictionPoints.Count,
            metrics,
            sample);
    }

    public static void WriteMetricsCsv(Part4RunResult result, string outputCsvPath)
    {
        EnsureOutputDirectory(outputCsvPath);

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
        EnsureOutputDirectory(outputCsvPath);

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
        IReadOnlyList<PredictionPoint> predictionPoints,
        IReadOnlyDictionary<(DateTime AnchorUtcTime, int HorizonStep), double> actualLookup)
    {
        var accumulators = new Dictionary<string, MetricsAccumulator>(StringComparer.Ordinal);

        foreach (var prediction in predictionPoints)
        {
            if (!actualLookup.TryGetValue((prediction.AnchorUtcTime, prediction.HorizonStep), out var actual))
            {
                continue;
            }

            if (!accumulators.TryGetValue(prediction.ModelName, out var accumulator))
            {
                accumulator = new MetricsAccumulator();
                accumulators[prediction.ModelName] = accumulator;
            }

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
        IReadOnlyList<PredictionPoint> predictionPoints,
        IReadOnlyDictionary<(DateTime AnchorUtcTime, int HorizonStep), double> actualLookup)
    {
        var firstAnchor = predictionPoints
            .Select(point => point.AnchorUtcTime)
            .OrderBy(timestamp => timestamp)
            .FirstOrDefault();

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
                prediction.AnchorUtcTime.AddMinutes(prediction.HorizonStep * MinutesPerStep),
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

            for (var step = 1; step <= HorizonSteps; step++)
            {
                points.Add(new ActualPoint(row.AnchorUtcTime, step, row.HorizonTargets[step - 1]));
            }
        }

        return points;
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

        return parsed;
    }

    private static void EnsureOutputDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
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