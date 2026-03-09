using System.Globalization;
using Microsoft.ML;
using Microsoft.ML.Data;

namespace Forecasting.App;

public static partial class Part3Modeling
{

    private static readonly CultureInfo InvariantCulture = CultureInfo.InvariantCulture;

    private sealed record FeatureSchemaEntry(string Name, Func<FeatureSnapshot, float> Selector);

    internal sealed record FeatureSnapshot(
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

    private static readonly FeatureSchemaEntry[] FeatureSchema =
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
        new("TargetLag192Std96", snapshot => (float)snapshot.TargetLag192Std96)
    ];

    /// <summary>Feature names in the same index order as <see cref="ToFeatureVector"/>.</summary>
    public static readonly string[] FeatureNames = FeatureSchema.Select(feature => feature.Name).ToArray();

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
        IReadOnlyDictionary<DateTime, Part2SupervisedRow> RowByTimestamp,
        DateTime[] HistoryTimestamps,
        double[] HistoryValues);

    private interface IForecastingModel
    {
        // Stable seam for adding/removing Part3 models without touching run orchestration.
        string ModelName { get; }

        void Train(IReadOnlyList<Part2SupervisedRow> trainRows, IReadOnlyList<Part2SupervisedRow> allRows);

        (double[] Predictions, int RecursiveStepsBeyondAnchor) Predict(Part2SupervisedRow anchor);
    }

    private interface IPermutationImportanceModel
    {
        // Optional capability seam: only models that support PFI implement this.
        Part3PfiResult? ComputePermutationImportance(IReadOnlyList<Part2SupervisedRow> validationRows, int pfiHorizonStep);
    }

    private sealed class BaselineSeasonalForecastingModel : IForecastingModel
    {
        private SeasonalBaselineModel? _model;

        public string ModelName => "BaselineSeasonal";

        public void Train(IReadOnlyList<Part2SupervisedRow> trainRows, IReadOnlyList<Part2SupervisedRow> allRows)
        {
            _model = BuildSeasonalBaseline(trainRows);
        }

        public (double[] Predictions, int RecursiveStepsBeyondAnchor) Predict(Part2SupervisedRow anchor)
        {
            return PredictWithBaseline(anchor.AnchorUtcTime, RequireModel());
        }

        private SeasonalBaselineModel RequireModel()
        {
            return _model ?? throw new InvalidOperationException($"Model '{ModelName}' must be trained before prediction.");
        }
    }

    private sealed class FastTreeRecursiveForecastingModel : IForecastingModel, IPermutationImportanceModel
    {
        private readonly FastTreeOptions _options;
        private FastTreeRecursiveModel? _model;

        public FastTreeRecursiveForecastingModel(FastTreeOptions options)
        {
            _options = options;
        }

        public string ModelName => "FastTreeRecursive";

        public void Train(IReadOnlyList<Part2SupervisedRow> trainRows, IReadOnlyList<Part2SupervisedRow> allRows)
        {
            _model = BuildFastTreeRecursiveModel(trainRows, allRows, _options);
        }

        public (double[] Predictions, int RecursiveStepsBeyondAnchor) Predict(Part2SupervisedRow anchor)
        {
            return PredictWithFastTreeRecursive(anchor, RequireModel());
        }

        public Part3PfiResult? ComputePermutationImportance(IReadOnlyList<Part2SupervisedRow> validationRows, int pfiHorizonStep)
        {
            return Part3Modeling.ComputeMultiSeedPermutationImportance(
                RequireModel(), validationRows, pfiHorizonStep, _options);
        }

        private FastTreeRecursiveModel RequireModel()
        {
            return _model ?? throw new InvalidOperationException($"Model '{ModelName}' must be trained before prediction.");
        }
    }

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

        public double GetTargetValueAtOrBefore(DateTime timestamp, double fallback)
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

    public static Part3RunResult RunModels(
        IReadOnlyList<Part2SupervisedRow> rows,
        FastTreeOptions? fastTreeOptions = null,
        bool enablePfi = false,
        int pfiHorizonStep = 1)
    {
        if (pfiHorizonStep < 1 || pfiHorizonStep > PipelineConstants.HorizonSteps)
        {
            throw new ArgumentOutOfRangeException(nameof(pfiHorizonStep),
                $"PFI horizon step must be between 1 and {PipelineConstants.HorizonSteps}.");
        }

        // Single pass to bucket rows by split and collect forecast anchors.
        var sorted = rows.OrderBy(row => row.AnchorUtcTime).ToList();
        var trainRows = new List<Part2SupervisedRow>(sorted.Count);
        var validationRows = new List<Part2SupervisedRow>(sorted.Count);
        var forecastAnchors = new List<Part2SupervisedRow>(sorted.Count);

        // Single pass over sorted rows to avoid repeated filter scans and allocations.
        foreach (var row in sorted)
        {
            if (string.Equals(row.Split, "Train", StringComparison.OrdinalIgnoreCase))
            {
                trainRows.Add(row);
                forecastAnchors.Add(row);
                continue;
            }

            if (string.Equals(row.Split, "Validation", StringComparison.OrdinalIgnoreCase))
            {
                validationRows.Add(row);
                forecastAnchors.Add(row);
            }
        }

        if (trainRows.Count == 0)
        {
            throw new InvalidOperationException("Part 3 requires at least one training row (Split=Train).");
        }

        if (validationRows.Count == 0)
        {
            throw new InvalidOperationException("Part 3 requires at least one validation row (Split=Validation).");
        }

        var modelRegistry = CreateModelRegistry(fastTreeOptions ?? new FastTreeOptions());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var model in modelRegistry)
        {
            var trainSw = System.Diagnostics.Stopwatch.StartNew();
            model.Train(trainRows, sorted);
            trainSw.Stop();
            Console.Error.WriteLine($"[TIMING] {model.ModelName} Train: {trainSw.Elapsed.TotalSeconds:F2}s");
        }

        // Compute PFI on validation data only when explicitly requested (it is expensive).
        var pfiModel = modelRegistry.OfType<IPermutationImportanceModel>().FirstOrDefault();
        Part3PfiResult? pfiResult = null;
        if (enablePfi && pfiModel is not null)
        {
            var pfiSw = System.Diagnostics.Stopwatch.StartNew();
            pfiResult = pfiModel.ComputePermutationImportance(validationRows, pfiHorizonStep);
            pfiSw.Stop();
            Console.Error.WriteLine($"[TIMING] PFI (horizon t+{pfiHorizonStep}): {pfiSw.Elapsed.TotalSeconds:F2}s");
        }

        var forecasts = new List<Part3ForecastRow>(forecastAnchors.Count * modelRegistry.Count);
        var recursiveStepsByModel = modelRegistry.ToDictionary(model => model.ModelName, _ => 0, StringComparer.Ordinal);
        var inferenceTimers = modelRegistry.ToDictionary(model => model.ModelName, _ => new System.Diagnostics.Stopwatch(), StringComparer.Ordinal);

        foreach (var anchor in forecastAnchors)
        {
            foreach (var model in modelRegistry)
            {
                inferenceTimers[model.ModelName].Start();
                var prediction = model.Predict(anchor);
                inferenceTimers[model.ModelName].Stop();
                recursiveStepsByModel[model.ModelName] += prediction.RecursiveStepsBeyondAnchor;

                forecasts.Add(new Part3ForecastRow(
                    anchor.AnchorUtcTime,
                    anchor.Split,
                    model.ModelName,
                    prediction.RecursiveStepsBeyondAnchor,
                    prediction.Predictions,
                    anchor.HorizonTargets));
            }
        }

        foreach (var model in modelRegistry)
        {
            Console.Error.WriteLine($"[TIMING] {model.ModelName} Inference: {inferenceTimers[model.ModelName].Elapsed.TotalSeconds:F2}s ({forecastAnchors.Count} anchors)");
        }
        sw.Stop();
        Console.Error.WriteLine($"[TIMING] Part3 total (train+pfi+infer): {sw.Elapsed.TotalSeconds:F2}s");

        var modelSummaries = modelRegistry
            .Select(model => new Part3ModelSummary(
                model.ModelName,
                forecastAnchors.Count,
                PipelineConstants.HorizonSteps,
                recursiveStepsByModel[model.ModelName]))
            .ToList();

        var summary = new Part3RunSummary(
            DateTime.UtcNow,
            sorted.Count,
            trainRows.Count,
            validationRows.Count,
            modelSummaries);

        return new Part3RunResult(forecasts, summary, pfiResult);
    }

    private static List<IForecastingModel> CreateModelRegistry(FastTreeOptions fastTreeOptions)
    {
        // Single registration point for model lineup keeps RunModels flow closed to branching.
        return
        [
            new BaselineSeasonalForecastingModel(),
            new FastTreeRecursiveForecastingModel(fastTreeOptions)
        ];
    }

    private static Part3PfiResult? ComputePermutationImportance(
        FastTreeRecursiveModel model,
        IReadOnlyList<Part2SupervisedRow> validationRows,
        int pfiHorizonStep,
        int permutationCount)
    {
        if (validationRows.Count == 0)
        {
            return null;
        }

        var validationData = validationRows
            .Select(row => new OneStepModelInput
            {
                Features = ToFeatureVector(row),
                Label = (float)row.HorizonTargets[pfiHorizonStep - 1]
            })
            .ToList();

        var schema = BuildTrainingRowSchemaDefinition();
        var dataView = model.MlContext.Data.LoadFromEnumerable(validationData, schema);

        var pfi = model.MlContext.Regression.PermutationFeatureImportance(
            model.Transformer,
            dataView,
            labelColumnName: nameof(OneStepModelInput.Label),
            permutationCount: permutationCount,
            useFeatureWeightFilter: false);

        var features = pfi
            .Select((kvp, index) => new
            {
                Index = index,
                Name = ResolvePfiFeatureName(kvp.Key, index),
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

        return new Part3PfiResult(features, permutationCount, validationRows.Count, pfiHorizonStep);
    }

    internal static string ResolvePfiFeatureName(object? featureKey, int fallbackIndex)
    {
        if (featureKey is string keyAsString && !string.IsNullOrWhiteSpace(keyAsString))
        {
            return keyAsString;
        }

        if (featureKey is ReadOnlyMemory<char> keyAsMemory && !keyAsMemory.IsEmpty)
        {
            return keyAsMemory.ToString();
        }

        if (TryResolveFeatureIndex(featureKey, out var keyAsIndex))
        {
            return FeatureNames[keyAsIndex];
        }

        if (fallbackIndex >= 0 && fallbackIndex < FeatureNames.Length)
        {
            return FeatureNames[fallbackIndex];
        }

        return $"Feature_{fallbackIndex}";
    }

    private static bool TryResolveFeatureIndex(object? featureKey, out int index)
    {
        // ML.NET can surface vector slot keys as various integral runtime types.
        // Accept any integral key to keep slot-name mapping deterministic across processes.
        switch (featureKey)
        {
            case int i when i >= 0 && i < FeatureNames.Length:
                index = i;
                return true;
            case long l when l >= 0 && l < FeatureNames.Length:
                index = (int)l;
                return true;
            case uint ui when ui < FeatureNames.Length:
                index = (int)ui;
                return true;
            case ulong ul when ul < (ulong)FeatureNames.Length:
                index = (int)ul;
                return true;
            case short s when s >= 0 && s < FeatureNames.Length:
                index = s;
                return true;
            case ushort us when us < FeatureNames.Length:
                index = us;
                return true;
            case byte b when b < FeatureNames.Length:
                index = b;
                return true;
            case sbyte sb when sb >= 0 && sb < FeatureNames.Length:
                index = sb;
                return true;
            default:
                index = -1;
                return false;
        }
    }

    private static Part3PfiResult? ComputeMultiSeedPermutationImportance(
        FastTreeRecursiveModel primaryModel,
        IReadOnlyList<Part2SupervisedRow> validationRows,
        int pfiHorizonStep,
        FastTreeOptions options)
    {
        if (validationRows.Count == 0)
            return null;

        if (options.PfiModelSeeds <= 1)
            return ComputePermutationImportance(primaryModel, validationRows, pfiHorizonStep, options.PfiPermutationCount);

        // Run PFI on the primary model + (PfiModelSeeds - 1) models with different seeds.
        var allRuns = new List<Part3PfiResult>();
        var primary = ComputePermutationImportance(primaryModel, validationRows, pfiHorizonStep, options.PfiPermutationCount);
        if (primary != null)
            allRuns.Add(primary);

        // Train additional models with varied seeds and run PFI on each.
        // We need trainRows and allRows — extract from primaryModel's history.
        for (int i = 1; i < options.PfiModelSeeds; i++)
        {
            var seedOptions = options with { Seed = options.Seed + i };
            var mlContext = new MLContext(seed: seedOptions.Seed);

            var validationData = validationRows
                .Select(row => new OneStepModelInput
                {
                    Features = ToFeatureVector(row),
                    Label = (float)row.HorizonTargets[pfiHorizonStep - 1]
                })
                .ToList();

            var schema = BuildTrainingRowSchemaDefinition();

            // Build a lightweight model for PFI only (reuse primary model's training data view).
            // We build from the same trainRows by filtering allRows to Split=="Train".
            var trainRows = primaryModel.RowByTimestamp.Values
                .Where(r => r.Split == "Train")
                .ToList();
            var allRows = primaryModel.RowByTimestamp.Values.ToList();
            var altModel = BuildFastTreeRecursiveModel(trainRows, allRows, seedOptions);

            var result = ComputePermutationImportance(altModel, validationRows, pfiHorizonStep, options.PfiPermutationCount);
            if (result != null)
                allRuns.Add(result);
        }

        // Aggregate: average MaeDelta per feature across all runs, re-rank.
        var featureNames = allRuns[0].Features.Select(f => f.FeatureName).ToList();
        var aggregated = featureNames.Select(name =>
        {
            var perRun = allRuns
                .SelectMany(r => r.Features)
                .Where(f => f.FeatureName == name)
                .ToList();

            return new
            {
                Name = name,
                MaeDelta = perRun.Average(f => f.MaeDelta),
                MaeDeltaStdDev = Math.Sqrt(perRun.Sum(f => f.MaeDeltaStdDev * f.MaeDeltaStdDev) / perRun.Count),
                RmseDelta = perRun.Average(f => f.RmseDelta),
                RmseDeltaStdDev = Math.Sqrt(perRun.Sum(f => f.RmseDeltaStdDev * f.RmseDeltaStdDev) / perRun.Count),
                R2Delta = perRun.Average(f => f.R2Delta),
                R2DeltaStdDev = Math.Sqrt(perRun.Sum(f => f.R2DeltaStdDev * f.R2DeltaStdDev) / perRun.Count),
            };
        })
        .OrderByDescending(f => Math.Abs(f.MaeDelta))
        .Select((f, rank) => new Part3PfiFeatureResult(
            rank + 1, f.Name,
            f.MaeDelta, f.MaeDeltaStdDev,
            f.RmseDelta, f.RmseDeltaStdDev,
            f.R2Delta, f.R2DeltaStdDev))
        .ToList();

        // Build per-seed detail table so rank stability can be inspected.
        var perSeedDetails = new List<Part3PfiSeedDetail>();
        for (int runIdx = 0; runIdx < allRuns.Count; runIdx++)
        {
            var seed = options.Seed + runIdx;
            foreach (var f in allRuns[runIdx].Features)
            {
                perSeedDetails.Add(new Part3PfiSeedDetail(seed, f.FeatureName, f.Rank, f.MaeDelta));
            }
        }

        return new Part3PfiResult(aggregated, options.PfiPermutationCount, validationRows.Count, pfiHorizonStep, perSeedDetails);
    }

    private static SeasonalBaselineModel BuildSeasonalBaseline(IReadOnlyList<Part2SupervisedRow> trainRows)
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

    private static (double[] Predictions, int RecursiveStepsBeyondAnchor) PredictWithBaseline(DateTime anchorUtcTime, SeasonalBaselineModel model)
    {
        var predictions = new double[PipelineConstants.HorizonSteps];
        var recursiveStepsBeyondAnchor = 0;

        for (var step = 1; step <= PipelineConstants.HorizonSteps; step++)
        {
            var ts = anchorUtcTime.AddMinutes(step * PipelineConstants.MinutesPerStep);
            var key = new SeasonalKey((int)ts.DayOfWeek, ts.Hour, ts.Minute);
            if (model.Means.TryGetValue(key, out var mean))
            {
                predictions[step - 1] = mean;
                continue;
            }

            recursiveStepsBeyondAnchor++;
            predictions[step - 1] = model.GlobalMean;
        }

        return (predictions, recursiveStepsBeyondAnchor);
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
        featuresColumn.ColumnType = new VectorDataViewType(NumberDataViewType.Single, FeatureSchema.Length);
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
        IReadOnlyList<Part2SupervisedRow> trainRows,
        IReadOnlyList<Part2SupervisedRow> allRows,
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

        var trainerOptions = new Microsoft.ML.Trainers.FastTree.FastTreeRegressionTrainer.Options
        {
            FeatureColumnName = nameof(OneStepModelInput.Features),
            LabelColumnName = nameof(OneStepModelInput.Label),
            NumberOfTrees = options.NumberOfTrees,
            NumberOfLeaves = options.NumberOfLeaves,
            MinimumExampleCountPerLeaf = options.MinimumExampleCountPerLeaf,
            LearningRate = options.LearningRate
        };

        ITransformer model;
        if (options.EnableEarlyStopping)
        {
            trainerOptions.EarlyStoppingRule = new Microsoft.ML.Trainers.FastTree.TolerantEarlyStoppingRule();
            trainerOptions.EarlyStoppingMetric = Microsoft.ML.Trainers.FastTree.EarlyStoppingMetric.L1Norm;
            // Split off 10% for early stopping validation (chronological tail to avoid leakage).
            var splitIndex = (int)(trainingData.Count * 0.9);
            var earlyStopTrain = mlContext.Data.LoadFromEnumerable(trainingData.Take(splitIndex).ToList(), schema);
            var earlyStopValid = mlContext.Data.LoadFromEnumerable(trainingData.Skip(splitIndex).ToList(), schema);
            var trainer = mlContext.Regression.Trainers.FastTree(trainerOptions);
            model = trainer.Fit(earlyStopTrain, earlyStopValid);
        }
        else
        {
            var trainer = mlContext.Regression.Trainers.FastTree(trainerOptions);
            model = trainer.Fit(dataView);
        }

        var predictionEngine = mlContext.Model.CreatePredictionEngine<OneStepModelInput, OneStepPrediction>(
            model, inputSchemaDefinition: schema);

        var history = BuildRecursiveHistory(allRows);

        return new FastTreeRecursiveModel(mlContext, model, predictionEngine, history.RowByTimestamp, history.HistoryTimestamps, history.HistoryValues);
    }

    private static (double[] Predictions, int RecursiveStepsBeyondAnchor) PredictWithFastTreeRecursive(
        Part2SupervisedRow anchor,
        FastTreeRecursiveModel model)
    {
        var features = new float[FeatureSchema.Length];
        // Reuse input wrapper + feature buffer across recursive steps to avoid per-step allocations.
        var reusableRow = new OneStepModelInput { Features = features };

        // Keep production FastTree scoring on the same recursive rollout used by test scorers,
        // so the state-transition logic can be verified independently of ML.NET internals.
        return PredictRecursively(
            anchor,
            model.RowByTimestamp,
            model.HistoryTimestamps,
            model.HistoryValues,
            snapshot =>
            {
                FillFeatureVector(snapshot, features);
                var score = model.PredictionEngine.Predict(reusableRow).Score;
                // Avoid propagating invalid model output through the recursive rollout.
                return double.IsFinite(score) ? score : snapshot.TargetAtT;
            });
    }

    // Test seam: reuse the same recursive rollout with a deterministic scorer so tests can
    // assert exact step-by-step outputs without depending on ML.NET tree internals.
    internal static (double[] Predictions, int RecursiveStepsBeyondAnchor) PredictWithRecursiveOracle(
        Part2SupervisedRow anchor,
        IReadOnlyList<Part2SupervisedRow> allRows,
        Func<FeatureSnapshot, double> scorer)
    {
        var history = BuildRecursiveHistory(allRows);
        return PredictRecursively(anchor, history.RowByTimestamp, history.HistoryTimestamps, history.HistoryValues, scorer);
    }

    private static (IReadOnlyDictionary<DateTime, Part2SupervisedRow> RowByTimestamp, DateTime[] HistoryTimestamps, double[] HistoryValues) BuildRecursiveHistory(
        IReadOnlyList<Part2SupervisedRow> allRows)
    {
        // Build both lookup and deduplicated history in one ordered pass.
        var rowByTimestamp = new Dictionary<DateTime, Part2SupervisedRow>();
        var historyRows = new List<Part2SupervisedRow>();
        var orderedRows = allRows.OrderBy(row => row.AnchorUtcTime);

        foreach (var row in orderedRows)
        {
            rowByTimestamp[row.AnchorUtcTime] = row;

            if (historyRows.Count > 0 && historyRows[^1].AnchorUtcTime == row.AnchorUtcTime)
            {
                historyRows[^1] = row;
                continue;
            }

            historyRows.Add(row);
        }

        return (
            rowByTimestamp,
            historyRows.Select(row => row.AnchorUtcTime).ToArray(),
            historyRows.Select(row => row.TargetAtT).ToArray());
    }

    /// <summary>
    /// Executes the recursive rollout for one anchor.
    /// The caller supplies the scoring function; this method owns the prediction mechanics:
    /// step timing, context lookup, history reads, lag/rolling updates, and feeding each
    /// predicted value back into history for subsequent steps.
    /// </summary>
    private static (double[] Predictions, int RecursiveStepsBeyondAnchor) PredictRecursively(
        Part2SupervisedRow anchor,
        IReadOnlyDictionary<DateTime, Part2SupervisedRow> rowByTimestamp,
        DateTime[] historyTimestamps,
        double[] historyValues,
        Func<FeatureSnapshot, double> scorer)
    {
        var predictions = new double[PipelineConstants.HorizonSteps];
        var recursiveStepsBeyondAnchor = 0;

        var baseEndIndex = UpperBound(historyTimestamps, historyTimestamps.Length, anchor.AnchorUtcTime) - 1;
        // Working timeline for this anchor: start with known past, then append each new prediction so later steps can use it as history.
        var history = new RecursiveHistoryState(historyTimestamps, historyValues, baseEndIndex);

        var targetLag192Rolling16 = history.InitializeRollingWindow(
            anchor.AnchorUtcTime.AddMinutes(-FeatureConfig.TargetLag192 * PipelineConstants.MinutesPerStep),
            FeatureConfig.RollingWindow16,
            anchor.TargetLag192Mean16);
        var targetLag192Rolling96 = history.InitializeRollingWindow(
            anchor.AnchorUtcTime.AddMinutes(-FeatureConfig.TargetLag192 * PipelineConstants.MinutesPerStep),
            FeatureConfig.RollingWindow96,
            anchor.TargetLag192Mean96);

        for (var step = 1; step <= PipelineConstants.HorizonSteps; step++)
        {
            // currentTime represents the feature timestamp t used to predict the next point t+1.
            var currentTime = anchor.AnchorUtcTime.AddMinutes((step - 1) * PipelineConstants.MinutesPerStep);

            // Only use exogenous variables known at the anchor. Calendar-derived context such as
            // holiday status may still advance with time because it is known in advance.
            var hasCalendarContext = rowByTimestamp.TryGetValue(currentTime, out var contextRow);
            var temperature = anchor.Temperature;
            var windspeed = anchor.Windspeed;
            var solar = anchor.SolarIrradiation;

            if (currentTime > anchor.AnchorUtcTime)
            {
                recursiveStepsBeyondAnchor++;
            }

            // Read autoregressive inputs from the truncated history view. Once predictions start,
            // later steps see earlier predicted values instead of future actual targets.
            var stepTargetAtT = history.GetTargetValueAtOrBefore(currentTime, anchor.TargetAtT);
            var stepTargetLag192 = history.GetTargetValueAtOrBefore(currentTime.AddMinutes(-FeatureConfig.TargetLag192 * PipelineConstants.MinutesPerStep), anchor.TargetLag192);
            var stepTargetLag672 = history.GetTargetValueAtOrBefore(currentTime.AddMinutes(-FeatureConfig.TargetLag672 * PipelineConstants.MinutesPerStep), anchor.TargetLag672);
            
            if (step > 1)
            {
                // Keep rolling windows aligned with their lagged timeline (t-192).
                targetLag192Rolling16.Push(stepTargetLag192);
                targetLag192Rolling96.Push(stepTargetLag192);
            }

            var hour = currentTime.Hour;
            var minute = currentTime.Minute;
            var dayOfWeek = (int)currentTime.DayOfWeek;
            var cyclic = CyclicLookup[GetCyclicLookupIndex(currentTime)];

            // Canonical feature projection for recursive inference state.
            var snapshot = new FeatureSnapshot(
                stepTargetAtT,
                temperature,
                windspeed,
                solar,
                hour,
                minute,
                dayOfWeek,
                hasCalendarContext && contextRow!.IsHoliday,
                cyclic.HourSin,
                cyclic.HourCos,
                cyclic.WeekdaySin,
                cyclic.WeekdayCos,
                stepTargetLag192,
                stepTargetLag672,
                targetLag192Rolling16.Mean,
                targetLag192Rolling16.Std,
                targetLag192Rolling96.Mean,
                targetLag192Rolling96.Std,
                anchor.TargetLag672Mean16,
                anchor.TargetLag672Std16,
                anchor.TargetLag672Mean96,
                anchor.TargetLag672Std96);

            // Score the current snapshot, either with ML.NET in production or a deterministic
            // oracle scorer in tests.
            var stepPrediction = scorer(snapshot);
            predictions[step - 1] = stepPrediction;

            var nextTimestamp = currentTime.AddMinutes(PipelineConstants.MinutesPerStep);
            if (nextTimestamp > anchor.AnchorUtcTime)
            {
                // Feed the new prediction back into history so subsequent steps recurse on it.
                history.AppendPredicted(nextTimestamp, stepPrediction);
            }
        }

        return (predictions, recursiveStepsBeyondAnchor);
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

    private static float[] ToFeatureVector(Part2SupervisedRow row)
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
        var values = new float[FeatureSchema.Length];
        FillFeatureVector(snapshot, values);

        return values;
    }

    private static void FillFeatureVector(FeatureSnapshot snapshot, Span<float> destination)
    {
        if (destination.Length < FeatureSchema.Length)
        {
            throw new ArgumentException($"Destination span must be at least {FeatureSchema.Length} elements.", nameof(destination));
        }

        for (var index = 0; index < FeatureSchema.Length; index++)
        {
            destination[index] = FeatureSchema[index].Selector(snapshot);
        }
    }

}
