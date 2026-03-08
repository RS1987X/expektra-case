namespace Forecasting.App;

public sealed record ForecastPredictionPoint(
    string ModelName,
    DateTime AnchorUtcTime,
    int HorizonStep,
    double Predicted,
    string Split);

public static class PredictionPoints
{
    public static List<ForecastPredictionPoint> ReadFromPredictionsCsv(string predictionsCsvPath)
    {
        var points = new List<ForecastPredictionPoint>();
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

                points.Add(new ForecastPredictionPoint(modelName, anchorUtcTime, step, predicted, split));
            }
        }

        return points;
    }

    public static List<ForecastPredictionPoint> BuildFromForecastRows(IReadOnlyList<Part3ForecastRow> forecastRows)
    {
        var points = new List<ForecastPredictionPoint>();
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

                points.Add(new ForecastPredictionPoint(row.ModelName, row.AnchorUtcTime, step, predicted, row.Split));
            }
        }

        return points;
    }
}
