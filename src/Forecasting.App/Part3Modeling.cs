using System.Globalization;
using System.Text.Json;
using Microsoft.ML;
using Microsoft.ML.Data;

namespace Forecasting.App;

public sealed record Part3InputRow(
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
    double TargetMean16,
    double TargetStd16,
    double TargetMean96,
    double TargetStd96,
    string Split,
    IReadOnlyList<double> HorizonTargets);

public sealed record Part3ForecastRow(
    DateTime AnchorUtcTime,
    string Split,
    string ModelName,
    int ExogenousFallbackSteps,
    IReadOnlyList<double> PredictedTargets,
    IReadOnlyList<double> ActualTargets);

public sealed record Part3ModelSummary(
    string ModelName,
    int AnchorsForecasted,
    int HorizonSteps,
    int ExogenousFallbackSteps);

public sealed record Part3RunSummary(
    DateTime GeneratedAtUtc,
    int InputRows,
    int TrainRows,
    int ValidationRows,
    IReadOnlyList<Part3ModelSummary> Models);

public sealed record Part3RunResult(
    IReadOnlyList<Part3ForecastRow> Forecasts,
    Part3RunSummary Summary);

public static class Part3Modeling
{
    private static readonly CultureInfo InvariantCulture = CultureInfo.InvariantCulture;

    private sealed record SeasonalKey(int DayOfWeek, int HourOfDay, int MinuteOfHour);

    private sealed record SeasonalBaselineModel(
        IReadOnlyDictionary<SeasonalKey, double> Means,
        double GlobalMean);

    private sealed class OneStepTrainingRow
    {
        [VectorType(18)]
        public float[] Features { get; set; } = [];

        public float Label { get; set; }
    }

    private sealed class OneStepPrediction
    {
        public float Score { get; set; }
    }

    private sealed record FastTreeRecursiveModel(
        PredictionEngine<OneStepTrainingRow, OneStepPrediction> PredictionEngine,
        IReadOnlyDictionary<DateTime, Part3InputRow> RowByTimestamp,
        DateTime[] HistoryTimestamps,
        double[] HistoryValues);

    private sealed class RollingWindowStats
    {
        private readonly Queue<double> _values;
        private readonly int _windowSize;
        private double _sum;
        private double _sumSquares;

        public RollingWindowStats(int windowSize)
        {
            _windowSize = windowSize;
            _values = new Queue<double>(windowSize);
        }

        public void AddInitial(double value)
        {
            _values.Enqueue(value);
            _sum += value;
            _sumSquares += value * value;
        }

        public void Push(double value)
        {
            var removed = _values.Dequeue();
            _sum -= removed;
            _sumSquares -= removed * removed;

            _values.Enqueue(value);
            _sum += value;
            _sumSquares += value * value;
        }

        public double Mean => _sum / _windowSize;

        public double Std
        {
            get
            {
                var mean = Mean;
                var variance = (_sumSquares / _windowSize) - (mean * mean);
                return Math.Sqrt(Math.Max(0d, variance));
            }
        }
    }

    private sealed class RecursiveHistoryState
    {
        private readonly DateTime[] _baseTimestamps;
        private readonly double[] _baseValues;
        private readonly int _baseLength;
        private readonly List<DateTime> _predictedTimestamps = new(PipelineConstants.HorizonSteps);
        private readonly List<double> _predictedValues = new(PipelineConstants.HorizonSteps);

        public RecursiveHistoryState(DateTime[] baseTimestamps, double[] baseValues, int baseEndIndex)
        {
            _baseTimestamps = baseTimestamps;
            _baseValues = baseValues;
            _baseLength = Math.Max(0, baseEndIndex + 1);
        }

        public double GetValueAtOrBefore(DateTime timestamp, double fallback)
        {
            var predictedIndex = UpperBound(_predictedTimestamps, timestamp) - 1;
            if (predictedIndex >= 0)
            {
                return _predictedValues[predictedIndex];
            }

            var baseIndex = UpperBound(_baseTimestamps, _baseLength, timestamp) - 1;
            return baseIndex >= 0 ? _baseValues[baseIndex] : fallback;
        }

        public void AppendPredicted(DateTime timestamp, double value)
        {
            _predictedTimestamps.Add(timestamp);
            _predictedValues.Add(value);
        }
    }

    public static IReadOnlyList<Part3InputRow> ReadPart2DatasetCsv(string part2DatasetCsvPath)
    {
        var rows = new List<Part3InputRow>();
        using var reader = new StreamReader(part2DatasetCsvPath);

        var header = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(header))
        {
            return rows;
        }

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
            if (parts.Length < 20 + PipelineConstants.HorizonSteps)
            {
                throw new FormatException($"Invalid row at line {lineNumber}: expected at least {20 + PipelineConstants.HorizonSteps} columns.");
            }

            if (!DateTime.TryParseExact(
                    parts[0],
                    "yyyy-MM-dd HH:mm:ss",
                    InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var anchorUtcTime))
            {
                throw new FormatException($"Invalid anchorUtcTime at line {lineNumber}: '{parts[0]}'.");
            }

            var horizonTargets = new double[PipelineConstants.HorizonSteps];
            for (var step = 0; step < PipelineConstants.HorizonSteps; step++)
            {
                horizonTargets[step] = ParseRequiredDouble(parts[20 + step], lineNumber, $"Target_tPlus{step + 1}");
            }

            rows.Add(new Part3InputRow(
                anchorUtcTime,
                ParseRequiredDouble(parts[1], lineNumber, nameof(Part3InputRow.TargetAtT)),
                ParseRequiredDouble(parts[2], lineNumber, nameof(Part3InputRow.Temperature)),
                ParseRequiredDouble(parts[3], lineNumber, nameof(Part3InputRow.Windspeed)),
                ParseRequiredDouble(parts[4], lineNumber, nameof(Part3InputRow.SolarIrradiation)),
                ParseRequiredInt(parts[5], lineNumber, nameof(Part3InputRow.HourOfDay)),
                ParseRequiredInt(parts[6], lineNumber, nameof(Part3InputRow.MinuteOfHour)),
                ParseRequiredInt(parts[7], lineNumber, nameof(Part3InputRow.DayOfWeek)),
                ParseRequiredBool(parts[8], lineNumber, nameof(Part3InputRow.IsHoliday)),
                ParseRequiredDouble(parts[9], lineNumber, nameof(Part3InputRow.HourSin)),
                ParseRequiredDouble(parts[10], lineNumber, nameof(Part3InputRow.HourCos)),
                ParseRequiredDouble(parts[11], lineNumber, nameof(Part3InputRow.WeekdaySin)),
                ParseRequiredDouble(parts[12], lineNumber, nameof(Part3InputRow.WeekdayCos)),
                ParseRequiredDouble(parts[13], lineNumber, nameof(Part3InputRow.TargetLag192)),
                ParseRequiredDouble(parts[14], lineNumber, nameof(Part3InputRow.TargetLag672)),
                ParseRequiredDouble(parts[15], lineNumber, nameof(Part3InputRow.TargetMean16)),
                ParseRequiredDouble(parts[16], lineNumber, nameof(Part3InputRow.TargetStd16)),
                ParseRequiredDouble(parts[17], lineNumber, nameof(Part3InputRow.TargetMean96)),
                ParseRequiredDouble(parts[18], lineNumber, nameof(Part3InputRow.TargetStd96)),
                parts[19],
                horizonTargets));
        }

        return rows;
    }

    public static Part3RunResult RunModels(IReadOnlyList<Part3InputRow> rows, FastTreeOptions? fastTreeOptions = null)
    {
        var sorted = rows.OrderBy(row => row.AnchorUtcTime).ToList();
        var trainRows = sorted.Where(row => string.Equals(row.Split, "Train", StringComparison.OrdinalIgnoreCase)).ToList();
        var validationRows = sorted.Where(row => string.Equals(row.Split, "Validation", StringComparison.OrdinalIgnoreCase)).ToList();

        if (trainRows.Count == 0)
        {
            throw new InvalidOperationException("Part 3 requires at least one training row (Split=Train).");
        }

        if (validationRows.Count == 0)
        {
            throw new InvalidOperationException("Part 3 requires at least one validation row (Split=Validation).");
        }

        var baselineModel = BuildSeasonalBaseline(trainRows);
        var fastTreeModel = BuildFastTreeRecursiveModel(trainRows, sorted, fastTreeOptions ?? new FastTreeOptions());
        var forecastAnchors = sorted
            .Where(row => string.Equals(row.Split, "Train", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(row.Split, "Validation", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var forecasts = new List<Part3ForecastRow>(forecastAnchors.Count * 2);
        var baselineFallbackSteps = 0;
        var fastTreeFallbackSteps = 0;

        foreach (var anchor in forecastAnchors)
        {
            var baseline = PredictWithBaseline(anchor.AnchorUtcTime, baselineModel);
            baselineFallbackSteps += baseline.FallbackSteps;
            forecasts.Add(new Part3ForecastRow(
                anchor.AnchorUtcTime,
                anchor.Split,
                "BaselineSeasonal",
                baseline.FallbackSteps,
                baseline.Predictions,
                anchor.HorizonTargets));

            var fastTree = PredictWithFastTreeRecursive(anchor, fastTreeModel);
            fastTreeFallbackSteps += fastTree.FallbackSteps;
            forecasts.Add(new Part3ForecastRow(
                anchor.AnchorUtcTime,
                anchor.Split,
                "FastTreeRecursive",
                fastTree.FallbackSteps,
                fastTree.Predictions,
                anchor.HorizonTargets));
        }

        var summary = new Part3RunSummary(
            DateTime.UtcNow,
            sorted.Count,
            trainRows.Count,
            validationRows.Count,
            [
                new Part3ModelSummary("BaselineSeasonal", forecastAnchors.Count, PipelineConstants.HorizonSteps, baselineFallbackSteps),
                new Part3ModelSummary("FastTreeRecursive", forecastAnchors.Count, PipelineConstants.HorizonSteps, fastTreeFallbackSteps)
            ]);

        return new Part3RunResult(forecasts, summary);
    }

    public static void WriteForecastsCsv(IReadOnlyList<Part3ForecastRow> forecasts, string outputCsvPath)
    {
        var directory = Path.GetDirectoryName(outputCsvPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var writer = new StreamWriter(outputCsvPath, false);
        var predictedColumns = Enumerable.Range(1, PipelineConstants.HorizonSteps).Select(step => $"Pred_tPlus{step}");
        var actualColumns = Enumerable.Range(1, PipelineConstants.HorizonSteps).Select(step => $"Actual_tPlus{step}");
        writer.WriteLine(string.Join(';',
            new[]
            {
                "anchorUtcTime",
                "Split",
                "Model",
                "ExogenousFallbackSteps"
            }.Concat(predictedColumns).Concat(actualColumns)));

        foreach (var forecast in forecasts)
        {
            var prefix = new[]
            {
                forecast.AnchorUtcTime.ToString("yyyy-MM-dd HH:mm:ss", InvariantCulture),
                forecast.Split,
                forecast.ModelName,
                forecast.ExogenousFallbackSteps.ToString(InvariantCulture)
            };

            var predicted = forecast.PredictedTargets.Select(value => value.ToString(InvariantCulture));
            var actual = forecast.ActualTargets.Select(value => value.ToString(InvariantCulture));
            writer.WriteLine(string.Join(';', prefix.Concat(predicted).Concat(actual)));
        }
    }

    public static void WriteSummaryJson(Part3RunSummary summary, string outputJsonPath)
    {
        var directory = Path.GetDirectoryName(outputJsonPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(outputJsonPath, json);
    }

    private static SeasonalBaselineModel BuildSeasonalBaseline(IReadOnlyList<Part3InputRow> trainRows)
    {
        if (trainRows.Count == 0)
        {
            throw new InvalidOperationException("Cannot build seasonal baseline with an empty training set.");
        }

        var grouped = trainRows
            .GroupBy(row => new SeasonalKey(row.DayOfWeek, row.HourOfDay, row.MinuteOfHour))
            .ToDictionary(group => group.Key, group => group.Average(row => row.TargetAtT));

        return new SeasonalBaselineModel(grouped, trainRows.Average(row => row.TargetAtT));
    }

    private static (double[] Predictions, int FallbackSteps) PredictWithBaseline(DateTime anchorUtcTime, SeasonalBaselineModel model)
    {
        var predictions = new double[PipelineConstants.HorizonSteps];
        var fallbackSteps = 0;

        for (var step = 1; step <= PipelineConstants.HorizonSteps; step++)
        {
            var ts = anchorUtcTime.AddMinutes(step * PipelineConstants.MinutesPerStep);
            var key = new SeasonalKey((int)ts.DayOfWeek, ts.Hour, ts.Minute);
            if (model.Means.TryGetValue(key, out var mean))
            {
                predictions[step - 1] = mean;
                continue;
            }

            fallbackSteps++;
            predictions[step - 1] = model.GlobalMean;
        }

        return (predictions, fallbackSteps);
    }

    private static FastTreeRecursiveModel BuildFastTreeRecursiveModel(
        IReadOnlyList<Part3InputRow> trainRows,
        IReadOnlyList<Part3InputRow> allRows,
        FastTreeOptions options)
    {
        var mlContext = new MLContext(seed: options.Seed);

        var trainingData = trainRows
            .Select(row => new OneStepTrainingRow
            {
                Features = ToFeatureVector(row),
                Label = (float)row.HorizonTargets[0]
            })
            .ToList();

        var dataView = mlContext.Data.LoadFromEnumerable(trainingData);
        var trainer = mlContext.Regression.Trainers.FastTree(new Microsoft.ML.Trainers.FastTree.FastTreeRegressionTrainer.Options
        {
            FeatureColumnName = nameof(OneStepTrainingRow.Features),
            LabelColumnName = nameof(OneStepTrainingRow.Label),
            NumberOfTrees = options.NumberOfTrees,
            NumberOfLeaves = options.NumberOfLeaves,
            MinimumExampleCountPerLeaf = options.MinimumExampleCountPerLeaf,
            LearningRate = options.LearningRate
        });

        var model = trainer.Fit(dataView);

        var predictionEngine = mlContext.Model.CreatePredictionEngine<OneStepTrainingRow, OneStepPrediction>(model);

        var rowByTimestamp = allRows
            .GroupBy(row => row.AnchorUtcTime)
            .ToDictionary(group => group.Key, group => group.Last());

        var historyRows = allRows
            .GroupBy(row => row.AnchorUtcTime)
            .Select(group => group.Last())
            .OrderBy(row => row.AnchorUtcTime)
            .ToList();

        var historyTimestamps = historyRows.Select(row => row.AnchorUtcTime).ToArray();
        var historyValues = historyRows.Select(row => row.TargetAtT).ToArray();

        return new FastTreeRecursiveModel(predictionEngine, rowByTimestamp, historyTimestamps, historyValues);
    }

    private static (double[] Predictions, int FallbackSteps) PredictWithFastTreeRecursive(
        Part3InputRow anchor,
        FastTreeRecursiveModel model)
    {
        var predictions = new double[PipelineConstants.HorizonSteps];
        var fallbackSteps = 0;

        var baseEndIndex = UpperBound(model.HistoryTimestamps, model.HistoryTimestamps.Length, anchor.AnchorUtcTime) - 1;
        // Working timeline for this anchor: start with known past, then append each new prediction so later steps can use it as history.
        var history = new RecursiveHistoryState(model.HistoryTimestamps, model.HistoryValues, baseEndIndex);

        var rolling16 = InitializeRollingWindow(history, anchor.AnchorUtcTime, FeatureConfig.RollingWindowShort, anchor.TargetMean16);
        var rolling96 = InitializeRollingWindow(history, anchor.AnchorUtcTime, FeatureConfig.RollingWindowLong, anchor.TargetMean96);

        var lastTemperature = anchor.Temperature;
        var lastWindspeed = anchor.Windspeed;
        var lastSolar = anchor.SolarIrradiation;

        for (var step = 1; step <= PipelineConstants.HorizonSteps; step++)
        {
            var currentTime = anchor.AnchorUtcTime.AddMinutes((step - 1) * PipelineConstants.MinutesPerStep);

            var hasContext = model.RowByTimestamp.TryGetValue(currentTime, out var contextRow);
            var temperature = hasContext ? contextRow!.Temperature : lastTemperature;
            var windspeed = hasContext ? contextRow!.Windspeed : lastWindspeed;
            var solar = hasContext ? contextRow!.SolarIrradiation : lastSolar;

            if (!hasContext)
            {
                fallbackSteps++;
            }

            lastTemperature = temperature;
            lastWindspeed = windspeed;
            lastSolar = solar;

            var targetAtT = history.GetValueAtOrBefore(currentTime, anchor.TargetAtT);

            if (step > 1)
            {
                // Incremental O(1) update: advance rolling windows with the latest target instead of rebuilding 16/96-step histories each step.
                rolling16.Push(targetAtT);
                rolling96.Push(targetAtT);
            }

            var lag192 = history.GetValueAtOrBefore(currentTime.AddMinutes(-FeatureConfig.TargetLagShort * PipelineConstants.MinutesPerStep), anchor.TargetLag192);
            var lag672 = history.GetValueAtOrBefore(currentTime.AddMinutes(-FeatureConfig.TargetLagLong * PipelineConstants.MinutesPerStep), anchor.TargetLag672);
            var mean16 = rolling16.Mean;
            var std16 = rolling16.Std;
            var mean96 = rolling96.Mean;
            var std96 = rolling96.Std;

            var hour = currentTime.Hour;
            var minute = currentTime.Minute;
            var dayOfWeek = (int)currentTime.DayOfWeek;
            // Encode hour/day-of-week as sin/cos cycles so boundary neighbors stay close (23↔0, Sunday↔Monday).
            var hourAngle = 2d * Math.PI * (hour / 24d);
            var weekdayAngle = 2d * Math.PI * (dayOfWeek / 7d);
            var hourSin = Math.Sin(hourAngle);
            var hourCos = Math.Cos(hourAngle);
            var weekdaySin = Math.Sin(weekdayAngle);
            var weekdayCos = Math.Cos(weekdayAngle);
            var isHoliday = hasContext && contextRow!.IsHoliday;

            var features = new float[]
            {
                (float)targetAtT,
                (float)temperature,
                (float)windspeed,
                (float)solar,
                hour,
                minute,
                dayOfWeek,
                isHoliday ? 1f : 0f,
                (float)hourSin,
                (float)hourCos,
                (float)weekdaySin,
                (float)weekdayCos,
                (float)lag192,
                (float)lag672,
                (float)mean16,
                (float)std16,
                (float)mean96,
                (float)std96
            };

            var score = model.PredictionEngine.Predict(new OneStepTrainingRow { Features = features }).Score;
            var predicted = double.IsFinite(score) ? score : targetAtT;
            predictions[step - 1] = predicted;

            var nextTimestamp = currentTime.AddMinutes(PipelineConstants.MinutesPerStep);
            if (nextTimestamp > anchor.AnchorUtcTime)
            {
                history.AppendPredicted(nextTimestamp, predicted);
            }
        }

        return (predictions, fallbackSteps);
    }

    private static RollingWindowStats InitializeRollingWindow(
        RecursiveHistoryState history,
        DateTime endTimestamp,
        int windowSteps,
        double fallback)
    {
        var window = new RollingWindowStats(windowSteps);
        for (var offset = windowSteps - 1; offset >= 0; offset--)
        {
            var timestamp = endTimestamp.AddMinutes(-offset * PipelineConstants.MinutesPerStep);
            var value = history.GetValueAtOrBefore(timestamp, fallback);
            window.AddInitial(value);
        }

        return window;
    }

    private static int UpperBound(DateTime[] values, int length, DateTime target)
    {
        var low = 0;
        var high = length;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (values[mid] <= target)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private static int UpperBound(List<DateTime> values, DateTime target)
    {
        var low = 0;
        var high = values.Count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (values[mid] <= target)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private static float[] ToFeatureVector(Part3InputRow row)
    {
        return
        [
            (float)row.TargetAtT,
            (float)row.Temperature,
            (float)row.Windspeed,
            (float)row.SolarIrradiation,
            row.HourOfDay,
            row.MinuteOfHour,
            row.DayOfWeek,
            row.IsHoliday ? 1f : 0f,
            (float)row.HourSin,
            (float)row.HourCos,
            (float)row.WeekdaySin,
            (float)row.WeekdayCos,
            (float)row.TargetLag192,
            (float)row.TargetLag672,
            (float)row.TargetMean16,
            (float)row.TargetStd16,
            (float)row.TargetMean96,
            (float)row.TargetStd96
        ];
    }

    private static double ParseRequiredDouble(string value, int lineNumber, string columnName)
    {
        if (!double.TryParse(value, NumberStyles.Float, InvariantCulture, out var parsed))
        {
            throw new FormatException($"Invalid {columnName} at line {lineNumber}: '{value}'.");
        }

        return parsed;
    }

    private static int ParseRequiredInt(string value, int lineNumber, string columnName)
    {
        if (!int.TryParse(value, NumberStyles.Integer, InvariantCulture, out var parsed))
        {
            throw new FormatException($"Invalid {columnName} at line {lineNumber}: '{value}'.");
        }

        return parsed;
    }

    private static bool ParseRequiredBool(string value, int lineNumber, string columnName)
    {
        if (!bool.TryParse(value, out var parsed))
        {
            throw new FormatException($"Invalid {columnName} at line {lineNumber}: '{value}'.");
        }

        return parsed;
    }
}