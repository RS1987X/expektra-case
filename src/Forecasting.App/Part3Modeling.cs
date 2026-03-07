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
    double TargetLag192Mean16,
    double TargetLag192Std16,
    double TargetLag192Mean96,
    double TargetLag192Std96,
    double TargetLag672Mean16,
    double TargetLag672Std16,
    double TargetLag672Mean96,
    double TargetLag672Std96,
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
    Part3RunSummary Summary,
    Part3PfiResult? FeatureImportance);

public sealed record Part3PfiFeatureResult(
    int Rank,
    string FeatureName,
    double MaeDelta,
    double MaeDeltaStdDev,
    double RmseDelta,
    double RmseDeltaStdDev,
    double R2Delta,
    double R2DeltaStdDev);

public sealed record Part3PfiResult(
    IReadOnlyList<Part3PfiFeatureResult> Features,
    int PermutationCount,
    int EvaluationRowCount);

public static class Part3Modeling
{
    private const int PfiPermutationCount = 10;
    private static readonly CultureInfo InvariantCulture = CultureInfo.InvariantCulture;

    private sealed record FeatureDefinition(string Name, Func<FeatureSnapshot, float> Selector);

    private sealed record FeatureSnapshot(
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
        double TargetLag192Mean16,
        double TargetLag192Std16,
        double TargetLag192Mean96,
        double TargetLag192Std96,
        double TargetLag672Mean16,
        double TargetLag672Std16,
        double TargetLag672Mean96,
        double TargetLag672Std96);

    private static readonly FeatureDefinition[] FeatureDefinitions =
    [
        new("Temperature", snapshot => (float)snapshot.Temperature),
        new("Windspeed", snapshot => (float)snapshot.Windspeed),
        new("SolarIrradiation", snapshot => (float)snapshot.SolarIrradiation),
        new("HourOfDay", snapshot => snapshot.HourOfDay),
        new("MinuteOfHour", snapshot => snapshot.MinuteOfHour),
        new("DayOfWeek", snapshot => snapshot.DayOfWeek),
        new("IsHoliday", snapshot => snapshot.IsHoliday ? 1f : 0f),
        new("HourSin", snapshot => (float)snapshot.HourSin),
        new("HourCos", snapshot => (float)snapshot.HourCos),
        new("WeekdaySin", snapshot => (float)snapshot.WeekdaySin),
        new("WeekdayCos", snapshot => (float)snapshot.WeekdayCos),
        new("TargetLag192", snapshot => (float)snapshot.TargetLag192),
        new("TargetLag672", snapshot => (float)snapshot.TargetLag672),
        new("TargetLag192Mean16", snapshot => (float)snapshot.TargetLag192Mean16),
        new("TargetLag192Std16", snapshot => (float)snapshot.TargetLag192Std16),
        new("TargetLag192Mean96", snapshot => (float)snapshot.TargetLag192Mean96),
        new("TargetLag192Std96", snapshot => (float)snapshot.TargetLag192Std96),
        new("TargetLag672Mean16", snapshot => (float)snapshot.TargetLag672Mean16),
        new("TargetLag672Std16", snapshot => (float)snapshot.TargetLag672Std16),
        new("TargetLag672Mean96", snapshot => (float)snapshot.TargetLag672Mean96),
        new("TargetLag672Std96", snapshot => (float)snapshot.TargetLag672Std96)
    ];

    /// <summary>Feature names in the same index order as <see cref="ToFeatureVector"/>.</summary>
    public static readonly string[] FeatureNames = FeatureDefinitions.Select(feature => feature.Name).ToArray();

    private const int DaysPerWeek = 7;
    private const int HoursPerDay = 24;
    private const int MinutesPerHour = 60;
    private static readonly int SlotsPerHour = GetSlotsPerHour();
    private static readonly int SlotsPerDay = HoursPerDay * SlotsPerHour;

    /// <summary>Pre-computed sin/cos cyclic encodings for each model cadence slot in a week.</summary>
    private static readonly (float HourSin, float HourCos, float WeekdaySin, float WeekdayCos)[] CyclicLookup = BuildCyclicLookup();

    private static int GetSlotsPerHour()
    {
        if (MinutesPerHour % PipelineConstants.MinutesPerStep != 0)
        {
            throw new InvalidOperationException(
                $"MinutesPerStep ({PipelineConstants.MinutesPerStep}) must evenly divide {MinutesPerHour} to build cyclic lookup.");
        }

        return MinutesPerHour / PipelineConstants.MinutesPerStep;
    }

    private static (float, float, float, float)[] BuildCyclicLookup()
    {
        var table = new (float, float, float, float)[DaysPerWeek * SlotsPerDay];
        for (var day = 0; day < DaysPerWeek; day++)
        {
            var weekdayAngle = 2d * Math.PI * (day / (double)DaysPerWeek);
            var wSin = (float)Math.Sin(weekdayAngle);
            var wCos = (float)Math.Cos(weekdayAngle);
            for (var hour = 0; hour < HoursPerDay; hour++)
            {
                var hourAngle = 2d * Math.PI * (hour / (double)HoursPerDay);
                var hSin = (float)Math.Sin(hourAngle);
                var hCos = (float)Math.Cos(hourAngle);
                for (var slot = 0; slot < SlotsPerHour; slot++)
                {
                    table[day * SlotsPerDay + hour * SlotsPerHour + slot] = (hSin, hCos, wSin, wCos);
                }
            }
        }

        return table;
    }

    private static int GetCyclicLookupIndex(DateTime timestamp)
    {
        if (timestamp.Minute % PipelineConstants.MinutesPerStep != 0)
        {
            throw new InvalidOperationException(
                $"Timestamp minute ({timestamp.Minute}) is not aligned to {PipelineConstants.MinutesPerStep}-minute cadence.");
        }

        var dayOfWeek = (int)timestamp.DayOfWeek;
        var slotWithinHour = timestamp.Minute / PipelineConstants.MinutesPerStep;
        return dayOfWeek * SlotsPerDay + timestamp.Hour * SlotsPerHour + slotWithinHour;
    }

    private sealed record SeasonalKey(int DayOfWeek, int HourOfDay, int MinuteOfHour);

    private sealed record SeasonalBaselineModel(
        IReadOnlyDictionary<SeasonalKey, double> Means,
        double GlobalMean);

    private sealed class OneStepModelInput
    {
        public float[] Features { get; set; } = [];

        public float Label { get; set; }
    }

    private sealed class OneStepPrediction
    {
        public float Score { get; set; }
    }

    private sealed record FastTreeRecursiveModel(
        MLContext MlContext,
        ITransformer Transformer,
        PredictionEngine<OneStepModelInput, OneStepPrediction> PredictionEngine,
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

    /// <summary>
    /// Maintains a view of history truncated at the anchor time, plus predicted values appended
    /// during recursive inference. Base arrays are shared and access is constrained by
    /// <c>_baseLength</c> so reads cannot cross the anchor boundary.
    /// </summary>
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

        /// <summary>
        /// Fills a rolling window using sequential index walking instead of repeated binary searches.
        /// Finds the base index for <paramref name="endTimestamp"/> once, then steps backwards
        /// through the sorted base values for each window slot.
        /// </summary>
        public RollingWindowStats InitializeRollingWindow(DateTime endTimestamp, int windowSteps, double fallback)
        {
            var window = new RollingWindowStats(windowSteps);

            // Find the base index for the end timestamp (rightmost position ≤ endTimestamp).
            var endIndex = UpperBound(_baseTimestamps, _baseLength, endTimestamp) - 1;

            // Walk backwards from endIndex to fill the window oldest-first.
            // Offset 0 = oldest slot, offset (windowSteps-1) = newest slot (at endTimestamp).
            for (var slot = 0; slot < windowSteps; slot++)
            {
                var targetOffset = windowSteps - 1 - slot; // distance from endTimestamp
                var idx = endIndex - targetOffset;
                var value = idx >= 0 ? _baseValues[idx] : fallback;
                window.AddInitial(value);
            }

            return window;
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
            if (parts.Length < 24 + PipelineConstants.HorizonSteps)
            {
                throw new FormatException($"Invalid row at line {lineNumber}: expected at least {24 + PipelineConstants.HorizonSteps} columns.");
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
                horizonTargets[step] = ParseRequiredDouble(parts[24 + step], lineNumber, $"Target_tPlus{step + 1}");
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
                ParseRequiredDouble(parts[15], lineNumber, nameof(Part3InputRow.TargetLag192Mean16)),
                ParseRequiredDouble(parts[16], lineNumber, nameof(Part3InputRow.TargetLag192Std16)),
                ParseRequiredDouble(parts[17], lineNumber, nameof(Part3InputRow.TargetLag192Mean96)),
                ParseRequiredDouble(parts[18], lineNumber, nameof(Part3InputRow.TargetLag192Std96)),
                ParseRequiredDouble(parts[19], lineNumber, nameof(Part3InputRow.TargetLag672Mean16)),
                ParseRequiredDouble(parts[20], lineNumber, nameof(Part3InputRow.TargetLag672Std16)),
                ParseRequiredDouble(parts[21], lineNumber, nameof(Part3InputRow.TargetLag672Mean96)),
                ParseRequiredDouble(parts[22], lineNumber, nameof(Part3InputRow.TargetLag672Std96)),
                parts[23],
                horizonTargets));
        }

        return rows;
    }

    public static Part3RunResult RunModels(IReadOnlyList<Part3InputRow> rows, FastTreeOptions? fastTreeOptions = null, bool enablePfi = false)
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

        // Compute PFI on validation data only when explicitly requested (it is expensive).
        var pfiResult = enablePfi ? ComputePermutationImportance(fastTreeModel, validationRows) : null;

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

        return new Part3RunResult(forecasts, summary, pfiResult);
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

    public static void WriteFeatureImportanceCsv(Part3PfiResult pfiResult, string outputCsvPath)
    {
        var directory = Path.GetDirectoryName(outputCsvPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

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

    private static Part3PfiResult? ComputePermutationImportance(
        FastTreeRecursiveModel model,
        IReadOnlyList<Part3InputRow> validationRows)
    {
        if (validationRows.Count == 0)
        {
            return null;
        }

        var validationData = validationRows
            .Select(row => new OneStepModelInput
            {
                Features = ToFeatureVector(row),
                Label = (float)row.HorizonTargets[0]
            })
            .ToList();

        var schema = BuildTrainingRowSchemaDefinition();
        var dataView = model.MlContext.Data.LoadFromEnumerable(validationData, schema);

        var pfi = model.MlContext.Regression.PermutationFeatureImportance(
            model.Transformer,
            dataView,
            labelColumnName: nameof(OneStepModelInput.Label),
            permutationCount: PfiPermutationCount,
            useFeatureWeightFilter: false);

        var features = pfi
            .Select((kvp, index) => new
            {
                Index = index,
                Name = index < FeatureNames.Length ? FeatureNames[index] : $"Feature_{index}",
                MaeDelta = kvp.Value.MeanAbsoluteError.Mean,
                MaeDeltaStdDev = kvp.Value.MeanAbsoluteError.StandardDeviation,
                RmseDelta = kvp.Value.RootMeanSquaredError.Mean,
                RmseDeltaStdDev = kvp.Value.RootMeanSquaredError.StandardDeviation,
                R2Delta = kvp.Value.RSquared.Mean,
                R2DeltaStdDev = kvp.Value.RSquared.StandardDeviation
            })
            .OrderByDescending(f => Math.Abs(f.MaeDelta))
            .Select((f, rank) => new Part3PfiFeatureResult(
                rank + 1,
                f.Name,
                f.MaeDelta,
                f.MaeDeltaStdDev,
                f.RmseDelta,
                f.RmseDeltaStdDev,
                f.R2Delta,
                f.R2DeltaStdDev))
            .ToList();

        return new Part3PfiResult(features, PfiPermutationCount, validationRows.Count);
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

    /// <summary>
    /// Builds a <see cref="SchemaDefinition"/> for <see cref="OneStepModelInput"/>
    /// that annotates the Features vector column with slot names from <see cref="FeatureNames"/>.
    /// This enables ML.NET's <c>PermutationFeatureImportance</c> to resolve per-feature slots.
    /// </summary>
    private static SchemaDefinition BuildTrainingRowSchemaDefinition()
    {
        var schema = SchemaDefinition.Create(typeof(OneStepModelInput));
        var featuresColumn = schema[nameof(OneStepModelInput.Features)];
        featuresColumn.ColumnType = new VectorDataViewType(NumberDataViewType.Single, FeatureDefinitions.Length);
        var slotNames = new VBuffer<ReadOnlyMemory<char>>(
            FeatureNames.Length,
            FeatureNames.Select(n => n.AsMemory()).ToArray());
        featuresColumn.AddAnnotation(
            "SlotNames",
            slotNames,
            new VectorDataViewType(TextDataViewType.Instance, FeatureNames.Length));
        return schema;
    }

    private static FastTreeRecursiveModel BuildFastTreeRecursiveModel(
        IReadOnlyList<Part3InputRow> trainRows,
        IReadOnlyList<Part3InputRow> allRows,
        FastTreeOptions options)
    {
        var mlContext = new MLContext(seed: options.Seed);

        var trainingData = trainRows
            .Select(row => new OneStepModelInput
            {
                Features = ToFeatureVector(row),
                Label = (float)row.HorizonTargets[0]
            })
            .ToList();

        var schema = BuildTrainingRowSchemaDefinition();
        var dataView = mlContext.Data.LoadFromEnumerable(trainingData, schema);
        var trainer = mlContext.Regression.Trainers.FastTree(new Microsoft.ML.Trainers.FastTree.FastTreeRegressionTrainer.Options
        {
            FeatureColumnName = nameof(OneStepModelInput.Features),
            LabelColumnName = nameof(OneStepModelInput.Label),
            NumberOfTrees = options.NumberOfTrees,
            NumberOfLeaves = options.NumberOfLeaves,
            MinimumExampleCountPerLeaf = options.MinimumExampleCountPerLeaf,
            LearningRate = options.LearningRate
        });

        var model = trainer.Fit(dataView);

        var predictionEngine = mlContext.Model.CreatePredictionEngine<OneStepModelInput, OneStepPrediction>(
            model, inputSchemaDefinition: schema);

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

        return new FastTreeRecursiveModel(mlContext, model, predictionEngine, rowByTimestamp, historyTimestamps, historyValues);
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

        var targetLag192Rolling16 = history.InitializeRollingWindow(
            anchor.AnchorUtcTime.AddMinutes(-FeatureConfig.TargetLag192 * PipelineConstants.MinutesPerStep),
            FeatureConfig.RollingWindow16,
            anchor.TargetLag192Mean16);
        var targetLag192Rolling96 = history.InitializeRollingWindow(
            anchor.AnchorUtcTime.AddMinutes(-FeatureConfig.TargetLag192 * PipelineConstants.MinutesPerStep),
            FeatureConfig.RollingWindow96,
            anchor.TargetLag192Mean96);
        var targetLag672Rolling16 = history.InitializeRollingWindow(
            anchor.AnchorUtcTime.AddMinutes(-FeatureConfig.TargetLag672 * PipelineConstants.MinutesPerStep),
            FeatureConfig.RollingWindow16,
            anchor.TargetLag672Mean16);
        var targetLag672Rolling96 = history.InitializeRollingWindow(
            anchor.AnchorUtcTime.AddMinutes(-FeatureConfig.TargetLag672 * PipelineConstants.MinutesPerStep),
            FeatureConfig.RollingWindow96,
            anchor.TargetLag672Mean96);

        var lastTemperature = anchor.Temperature;
        var lastWindspeed = anchor.Windspeed;
        var lastSolar = anchor.SolarIrradiation;

        // Reuse a single training row and feature array across all 192 steps to avoid ~14M allocations.
        var features = new float[FeatureDefinitions.Length];
        var reusableRow = new OneStepModelInput { Features = features };

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

            var targetLag192 = history.GetValueAtOrBefore(currentTime.AddMinutes(-FeatureConfig.TargetLag192 * PipelineConstants.MinutesPerStep), anchor.TargetLag192);
            var targetLag672 = history.GetValueAtOrBefore(currentTime.AddMinutes(-FeatureConfig.TargetLag672 * PipelineConstants.MinutesPerStep), anchor.TargetLag672);
            if (step > 1)
            {
                // Keep rolling windows aligned with their lagged timelines (t-192 and t-672).
                targetLag192Rolling16.Push(targetLag192);
                targetLag192Rolling96.Push(targetLag192);
                targetLag672Rolling16.Push(targetLag672);
                targetLag672Rolling96.Push(targetLag672);
            }

            var hour = currentTime.Hour;
            var minute = currentTime.Minute;
            var dayOfWeek = (int)currentTime.DayOfWeek;
            var cyclic = CyclicLookup[GetCyclicLookupIndex(currentTime)];

            // Fill feature vector directly (must stay in sync with FeatureDefinitions order).
            features[0] = (float)temperature;
            features[1] = (float)windspeed;
            features[2] = (float)solar;
            features[3] = hour;
            features[4] = minute;
            features[5] = dayOfWeek;
            features[6] = hasContext && contextRow!.IsHoliday ? 1f : 0f;
            features[7] = cyclic.HourSin;
            features[8] = cyclic.HourCos;
            features[9] = cyclic.WeekdaySin;
            features[10] = cyclic.WeekdayCos;
            features[11] = (float)targetLag192;
            features[12] = (float)targetLag672;
            features[13] = (float)targetLag192Rolling16.Mean;
            features[14] = (float)targetLag192Rolling16.Std;
            features[15] = (float)targetLag192Rolling96.Mean;
            features[16] = (float)targetLag192Rolling96.Std;
            features[17] = (float)targetLag672Rolling16.Mean;
            features[18] = (float)targetLag672Rolling16.Std;
            features[19] = (float)targetLag672Rolling96.Mean;
            features[20] = (float)targetLag672Rolling96.Std;

            var score = model.PredictionEngine.Predict(reusableRow).Score;
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
        return ToFeatureVector(new FeatureSnapshot(
            row.TargetAtT,
            row.Temperature,
            row.Windspeed,
            row.SolarIrradiation,
            row.HourOfDay,
            row.MinuteOfHour,
            row.DayOfWeek,
            row.IsHoliday,
            row.HourSin,
            row.HourCos,
            row.WeekdaySin,
            row.WeekdayCos,
            row.TargetLag192,
            row.TargetLag672,
            row.TargetLag192Mean16,
            row.TargetLag192Std16,
            row.TargetLag192Mean96,
            row.TargetLag192Std96,
            row.TargetLag672Mean16,
            row.TargetLag672Std16,
            row.TargetLag672Mean96,
            row.TargetLag672Std96));
    }

    private static float[] ToFeatureVector(FeatureSnapshot snapshot)
    {
        var values = new float[FeatureDefinitions.Length];
        for (var index = 0; index < FeatureDefinitions.Length; index++)
        {
            values[index] = FeatureDefinitions[index].Selector(snapshot);
        }

        return values;
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