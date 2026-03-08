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

    private sealed record PredictionPoint(string ModelName, DateTime AnchorUtcTime, int HorizonStep, double Predicted, string Split);

    private sealed record ActualPoint(DateTime AnchorUtcTime, int HorizonStep, double Actual);

    public static Part4RunResult RunEvaluation(string part2InputCsvPath, string part3PredictionsCsvPath)
    {
        var part2Rows = Part3Modeling.ReadPart2DatasetCsv(part2InputCsvPath);
        var predictionPoints = ReadPredictionPoints(part3PredictionsCsvPath);
        return RunEvaluation(part2Rows, predictionPoints);
    }

    public static Part4RunResult RunEvaluation(
        IReadOnlyList<Part3InputRow> part2Rows,
        IReadOnlyList<Part3ForecastRow> forecastRows)
    {
        var predictionPoints = BuildPredictionPoints(forecastRows);
        return RunEvaluation(part2Rows, predictionPoints);
    }

    private static Part4RunResult RunEvaluation(
        IReadOnlyList<Part3InputRow> part2Rows,
        IReadOnlyList<PredictionPoint> predictionPoints)
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
        IReadOnlyList<PredictionPoint> predictionPoints,
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
        IReadOnlyList<PredictionPoint> predictionPoints,
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
        var anchorIndex = CsvParsing.FindRequiredColumnIndex(columns, "anchorUtcTime", "predictions CSV");
        var splitIndex = CsvParsing.FindRequiredColumnIndex(columns, "Split", "predictions CSV");
        var modelIndex = CsvParsing.FindRequiredColumnIndex(columns, "Model", "predictions CSV");
        var predictedIndexes = Enumerable.Range(1, PipelineConstants.HorizonSteps)
            .Select(step => CsvParsing.FindRequiredColumnIndex(columns, $"Pred_tPlus{step}", "predictions CSV"))
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

            var anchorUtcTime = CsvParsing.ParseRequiredUtcDateTime(parts[anchorIndex], lineNumber, "anchorUtcTime");

            var split = parts[splitIndex];
            var modelName = parts[modelIndex];

            for (var step = 1; step <= PipelineConstants.HorizonSteps; step++)
            {
                var predicted = CsvParsing.ParseRequiredDouble(parts[predictedIndexes[step - 1]], lineNumber, $"Pred_tPlus{step}", rejectNonFinite: true);
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