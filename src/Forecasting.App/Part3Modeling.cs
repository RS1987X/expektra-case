using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.ML;
using Microsoft.ML.Data;

namespace Forecasting.App;

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
    int EvaluationRowCount,
    int HorizonStep);

public static class Part3Modeling
{
    private const int PfiPermutationCount = 10;
    private static readonly CultureInfo InvariantCulture = CultureInfo.InvariantCulture;
    private static readonly string[] RequiredPart2BaseColumns =
    [
        "anchorUtcTime",
        nameof(Part2SupervisedRow.TargetAtT),
        nameof(Part2SupervisedRow.Temperature),
        nameof(Part2SupervisedRow.Windspeed),
        nameof(Part2SupervisedRow.SolarIrradiation),
        nameof(Part2SupervisedRow.HourOfDay),
        nameof(Part2SupervisedRow.MinuteOfHour),
        nameof(Part2SupervisedRow.DayOfWeek),
        nameof(Part2SupervisedRow.IsHoliday),
        nameof(Part2SupervisedRow.HourSin),
        nameof(Part2SupervisedRow.HourCos),
        nameof(Part2SupervisedRow.WeekdaySin),
        nameof(Part2SupervisedRow.WeekdayCos),
        nameof(Part2SupervisedRow.TargetLag192),
        nameof(Part2SupervisedRow.TargetLag672),
        nameof(Part2SupervisedRow.TargetLag192Mean16),
        nameof(Part2SupervisedRow.TargetLag192Std16),
        nameof(Part2SupervisedRow.TargetLag192Mean96),
        nameof(Part2SupervisedRow.TargetLag192Std96),
        nameof(Part2SupervisedRow.TargetLag672Mean16),
        nameof(Part2SupervisedRow.TargetLag672Std16),
        nameof(Part2SupervisedRow.TargetLag672Mean96),
        nameof(Part2SupervisedRow.TargetLag672Std96),
        "Split"
    ];

    private sealed record FeatureSchemaEntry(string Name, Func<FeatureSnapshot, float> Selector);

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

        (double[] Predictions, int FallbackSteps) Predict(Part2SupervisedRow anchor);
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

        public (double[] Predictions, int FallbackSteps) Predict(Part2SupervisedRow anchor)
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

        public (double[] Predictions, int FallbackSteps) Predict(Part2SupervisedRow anchor)
        {
            return PredictWithFastTreeRecursive(anchor, RequireModel());
        }

        public Part3PfiResult? ComputePermutationImportance(IReadOnlyList<Part2SupervisedRow> validationRows, int pfiHorizonStep)
        {
            return Part3Modeling.ComputePermutationImportance(RequireModel(), validationRows, pfiHorizonStep);
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

    public static IReadOnlyList<Part2SupervisedRow> ReadPart2DatasetCsv(string part2DatasetCsvPath)
    {
        var rows = new List<Part2SupervisedRow>();
        using var reader = new StreamReader(part2DatasetCsvPath);

        var header = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(header))
        {
            return rows;
        }

        var columns = header.Split(';');
        var requiredPart2Indexes = RequiredPart2BaseColumns.ToDictionary(
            name => name,
            name => CsvParsing.FindRequiredColumnIndex(columns, name, $"part2 supervised dataset '{part2DatasetCsvPath}'"),
            StringComparer.Ordinal);
        var horizonIndexes = Enumerable.Range(1, PipelineConstants.HorizonSteps)
            .Select(step => CsvParsing.FindRequiredColumnIndex(columns, $"Target_tPlus{step}", $"part2 supervised dataset '{part2DatasetCsvPath}'"))
            .ToArray();
        var maxRequiredIndex = Math.Max(requiredPart2Indexes.Values.Max(), horizonIndexes.Length == 0 ? -1 : horizonIndexes.Max());

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
            if (parts.Length <= maxRequiredIndex)
            {
                throw new FormatException($"Invalid row at line {lineNumber}: expected at least {maxRequiredIndex + 1} columns.");
            }

            var anchorUtcTime = CsvParsing.ParseRequiredUtcDateTime(parts[requiredPart2Indexes["anchorUtcTime"]], lineNumber, "anchorUtcTime");

            var horizonTargets = new double[PipelineConstants.HorizonSteps];
            for (var step = 0; step < PipelineConstants.HorizonSteps; step++)
            {
                horizonTargets[step] = CsvParsing.ParseRequiredDouble(parts[horizonIndexes[step]], lineNumber, $"Target_tPlus{step + 1}");
            }

            rows.Add(new Part2SupervisedRow(
                anchorUtcTime,
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.TargetAtT)]], lineNumber, nameof(Part2SupervisedRow.TargetAtT)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.Temperature)]], lineNumber, nameof(Part2SupervisedRow.Temperature)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.Windspeed)]], lineNumber, nameof(Part2SupervisedRow.Windspeed)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.SolarIrradiation)]], lineNumber, nameof(Part2SupervisedRow.SolarIrradiation)),
                CsvParsing.ParseRequiredInt(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.HourOfDay)]], lineNumber, nameof(Part2SupervisedRow.HourOfDay)),
                CsvParsing.ParseRequiredInt(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.MinuteOfHour)]], lineNumber, nameof(Part2SupervisedRow.MinuteOfHour)),
                CsvParsing.ParseRequiredInt(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.DayOfWeek)]], lineNumber, nameof(Part2SupervisedRow.DayOfWeek)),
                CsvParsing.ParseRequiredBool(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.IsHoliday)]], lineNumber, nameof(Part2SupervisedRow.IsHoliday)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.HourSin)]], lineNumber, nameof(Part2SupervisedRow.HourSin)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.HourCos)]], lineNumber, nameof(Part2SupervisedRow.HourCos)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.WeekdaySin)]], lineNumber, nameof(Part2SupervisedRow.WeekdaySin)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.WeekdayCos)]], lineNumber, nameof(Part2SupervisedRow.WeekdayCos)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.TargetLag192)]], lineNumber, nameof(Part2SupervisedRow.TargetLag192)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.TargetLag672)]], lineNumber, nameof(Part2SupervisedRow.TargetLag672)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.TargetLag192Mean16)]], lineNumber, nameof(Part2SupervisedRow.TargetLag192Mean16)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.TargetLag192Std16)]], lineNumber, nameof(Part2SupervisedRow.TargetLag192Std16)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.TargetLag192Mean96)]], lineNumber, nameof(Part2SupervisedRow.TargetLag192Mean96)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.TargetLag192Std96)]], lineNumber, nameof(Part2SupervisedRow.TargetLag192Std96)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.TargetLag672Mean16)]], lineNumber, nameof(Part2SupervisedRow.TargetLag672Mean16)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.TargetLag672Std16)]], lineNumber, nameof(Part2SupervisedRow.TargetLag672Std16)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.TargetLag672Mean96)]], lineNumber, nameof(Part2SupervisedRow.TargetLag672Mean96)),
                CsvParsing.ParseRequiredDouble(parts[requiredPart2Indexes[nameof(Part2SupervisedRow.TargetLag672Std96)]], lineNumber, nameof(Part2SupervisedRow.TargetLag672Std96)),
                parts[requiredPart2Indexes["Split"]],
                horizonTargets));
        }

        return rows;
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
        foreach (var model in modelRegistry)
        {
            model.Train(trainRows, sorted);
        }

        // Compute PFI on validation data only when explicitly requested (it is expensive).
        var pfiModel = modelRegistry.OfType<IPermutationImportanceModel>().FirstOrDefault();
        var pfiResult = enablePfi && pfiModel is not null
            ? pfiModel.ComputePermutationImportance(validationRows, pfiHorizonStep)
            : null;

        var forecasts = new List<Part3ForecastRow>(forecastAnchors.Count * modelRegistry.Count);
        var fallbackStepsByModel = modelRegistry.ToDictionary(model => model.ModelName, _ => 0, StringComparer.Ordinal);

        foreach (var anchor in forecastAnchors)
        {
            foreach (var model in modelRegistry)
            {
                var prediction = model.Predict(anchor);
                fallbackStepsByModel[model.ModelName] += prediction.FallbackSteps;

                forecasts.Add(new Part3ForecastRow(
                    anchor.AnchorUtcTime,
                    anchor.Split,
                    model.ModelName,
                    prediction.FallbackSteps,
                    prediction.Predictions,
                    anchor.HorizonTargets));
            }
        }

        var modelSummaries = modelRegistry
            .Select(model => new Part3ModelSummary(
                model.ModelName,
                forecastAnchors.Count,
                PipelineConstants.HorizonSteps,
                fallbackStepsByModel[model.ModelName]))
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

    public static void WriteForecastsCsv(IReadOnlyList<Part3ForecastRow> forecasts, string outputCsvPath)
    {
        FileOutput.EnsureParentDirectory(outputCsvPath);

        using var writer = new StreamWriter(outputCsvPath, false);
        // Build header once and reuse a row buffer for lower per-row allocation pressure.
        var headerBuilder = new StringBuilder(capacity: 64 + PipelineConstants.HorizonSteps * 24);
        headerBuilder.Append("anchorUtcTime;Split;Model;ExogenousFallbackSteps");
        for (var step = 1; step <= PipelineConstants.HorizonSteps; step++)
        {
            headerBuilder.Append(';').Append("Pred_tPlus").Append(step);
        }

        for (var step = 1; step <= PipelineConstants.HorizonSteps; step++)
        {
            headerBuilder.Append(';').Append("Actual_tPlus").Append(step);
        }

        writer.WriteLine(headerBuilder.ToString());

        var rowBuilder = new StringBuilder(capacity: 128 + PipelineConstants.HorizonSteps * 28);

        foreach (var forecast in forecasts)
        {
            rowBuilder.Clear();
            rowBuilder.Append(forecast.AnchorUtcTime.ToString("yyyy-MM-dd HH:mm:ss", InvariantCulture));
            rowBuilder.Append(';').Append(forecast.Split);
            rowBuilder.Append(';').Append(forecast.ModelName);
            rowBuilder.Append(';').Append(forecast.ExogenousFallbackSteps.ToString(InvariantCulture));

            for (var step = 0; step < forecast.PredictedTargets.Count; step++)
            {
                rowBuilder.Append(';').Append(forecast.PredictedTargets[step].ToString(InvariantCulture));
            }

            for (var step = 0; step < forecast.ActualTargets.Count; step++)
            {
                rowBuilder.Append(';').Append(forecast.ActualTargets[step].ToString(InvariantCulture));
            }

            writer.WriteLine(rowBuilder.ToString());
        }
    }

    public static IReadOnlyList<Part3ForecastRow> ReadForecastsCsv(string forecastsCsvPath)
    {
        var rows = new List<Part3ForecastRow>();
        using var reader = new StreamReader(forecastsCsvPath);

        var header = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(header))
        {
            return rows;
        }

        var columns = header.Split(';');
        var anchorIndex = CsvParsing.FindRequiredColumnIndex(columns, "anchorUtcTime", "forecasts CSV");
        var splitIndex = CsvParsing.FindRequiredColumnIndex(columns, "Split", "forecasts CSV");
        var modelIndex = CsvParsing.FindRequiredColumnIndex(columns, "Model", "forecasts CSV");
        var fallbackIndex = CsvParsing.FindRequiredColumnIndex(columns, "ExogenousFallbackSteps", "forecasts CSV");
        var predictedIndexes = Enumerable.Range(1, PipelineConstants.HorizonSteps)
            .Select(step => CsvParsing.FindRequiredColumnIndex(columns, $"Pred_tPlus{step}", "forecasts CSV"))
            .ToArray();
        var actualIndexes = Enumerable.Range(1, PipelineConstants.HorizonSteps)
            .Select(step => CsvParsing.FindRequiredColumnIndex(columns, $"Actual_tPlus{step}", "forecasts CSV"))
            .ToArray();

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
                throw new FormatException($"Invalid forecast row at line {lineNumber}: expected {columns.Length} columns.");
            }

            var anchorUtcTime = CsvParsing.ParseRequiredUtcDateTime(parts[anchorIndex], lineNumber, "anchorUtcTime");

            var predicted = new double[PipelineConstants.HorizonSteps];
            var actual = new double[PipelineConstants.HorizonSteps];
            for (var step = 0; step < PipelineConstants.HorizonSteps; step++)
            {
                predicted[step] = CsvParsing.ParseRequiredDouble(parts[predictedIndexes[step]], lineNumber, $"Pred_tPlus{step + 1}");
                actual[step] = CsvParsing.ParseRequiredDouble(parts[actualIndexes[step]], lineNumber, $"Actual_tPlus{step + 1}");
            }

            rows.Add(new Part3ForecastRow(
                anchorUtcTime,
                parts[splitIndex],
                parts[modelIndex],
                CsvParsing.ParseRequiredInt(parts[fallbackIndex], lineNumber, "ExogenousFallbackSteps"),
                predicted,
                actual));
        }

        return rows;
    }

    public static void WriteSummaryJson(Part3RunSummary summary, string outputJsonPath)
    {
        FileOutput.EnsureParentDirectory(outputJsonPath);

        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(outputJsonPath, json);
    }

    public static void WriteFeatureImportanceCsv(Part3PfiResult pfiResult, string outputCsvPath)
    {
        FileOutput.EnsureParentDirectory(outputCsvPath);

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
        IReadOnlyList<Part2SupervisedRow> validationRows,
        int pfiHorizonStep)
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

        return new Part3PfiResult(features, PfiPermutationCount, validationRows.Count, pfiHorizonStep);
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

        var historyTimestamps = historyRows.Select(row => row.AnchorUtcTime).ToArray();
        var historyValues = historyRows.Select(row => row.TargetAtT).ToArray();

        return new FastTreeRecursiveModel(mlContext, model, predictionEngine, rowByTimestamp, historyTimestamps, historyValues);
    }

    private static (double[] Predictions, int FallbackSteps) PredictWithFastTreeRecursive(
        Part2SupervisedRow anchor,
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

        var lastTemperature = anchor.Temperature;
        var lastWindspeed = anchor.Windspeed;
        var lastSolar = anchor.SolarIrradiation;

        // Reuse a single training row and feature array across all 192 steps to avoid ~14M allocations.
        var features = new float[FeatureSchema.Length];
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
                // Keep rolling windows aligned with their lagged timeline (t-192).
                targetLag192Rolling16.Push(targetLag192);
                targetLag192Rolling96.Push(targetLag192);
            }

            var hour = currentTime.Hour;
            var minute = currentTime.Minute;
            var dayOfWeek = (int)currentTime.DayOfWeek;
            var cyclic = CyclicLookup[GetCyclicLookupIndex(currentTime)];

            // Canonical feature projection for recursive inference state.
            FillFeatureVector(new FeatureSnapshot(
                targetAtT,
                temperature,
                windspeed,
                solar,
                hour,
                minute,
                dayOfWeek,
                hasContext && contextRow!.IsHoliday,
                cyclic.HourSin,
                cyclic.HourCos,
                cyclic.WeekdaySin,
                cyclic.WeekdayCos,
                targetLag192,
                targetLag672,
                targetLag192Rolling16.Mean,
                targetLag192Rolling16.Std,
                targetLag192Rolling96.Mean,
                targetLag192Rolling96.Std,
                anchor.TargetLag672Mean16,
                anchor.TargetLag672Std16,
                anchor.TargetLag672Mean96,
                anchor.TargetLag672Std96),
                features);

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
