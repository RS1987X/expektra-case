using System.Globalization;
using System.Text.Json;

namespace Forecasting.App;

public sealed record RawDataRow(
    DateTime UtcTime,
    double? Target,
    double? Temperature,
    double? Windspeed,
    double? SolarIrradiation);

public sealed record FeatureRow(
    DateTime UtcTime,
    double Target,
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
    double WeekdayCos);

public sealed record PreprocessingAuditEvent(
    DateTime UtcTime,
    string EventType,
    string Reason,
    bool IsValidation,
    bool IsTargetImputed,
    string ImputationSource);

public sealed record PreprocessingAuditSummary(
    DateTime ValidationStartUtc,
    int InputRowsBeforeDeduplication,
    int TotalRows,
    int TrainingRows,
    int ValidationRows,
    int PersistedRows,
    int DroppedValidationRowsFromTrainingImputation,
    int DroppedDuplicateTimestampRows);

public sealed record PreprocessedDataset(
    IReadOnlyList<FeatureRow> PersistedFeatures,
    IReadOnlyList<PreprocessingAuditEvent> AuditEvents,
    PreprocessingAuditSummary AuditSummary);

public static class Part1Preprocessing
{
    private static readonly CultureInfo SwedishCulture = CultureInfo.GetCultureInfo("sv-SE");
    private static readonly CultureInfo InvariantCulture = CultureInfo.InvariantCulture;
    private static readonly string[] AcceptedDateTimeFormats = ["yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss"];

    private sealed record FilledValue(double Value, bool IsImputed, bool IsSourcedFromPriorSegment);

    public static IReadOnlyList<FeatureRow> BuildFeatureMatrix(string dataCsvPath, string holidaysCsvPath)
    {
        var rawRows = ReadRawDataRows(dataCsvPath).OrderBy(row => row.UtcTime).ToList();
        rawRows = DeduplicateByUtcTimeKeepLast(rawRows, out _);
        var holidays = ReadSwedishPublicHolidays(holidaysCsvPath);
        var targets = ForwardFillTargets(rawRows.Select(row => row.Target).ToList());
        var temperatures = ForwardFillRequiredSeries(rawRows.Select(row => row.Temperature).ToList(), "Temperature");
        var windspeeds = ForwardFillRequiredSeries(rawRows.Select(row => row.Windspeed).ToList(), "Windspeed");
        var solarIrradiations = ForwardFillRequiredSeries(rawRows.Select(row => row.SolarIrradiation).ToList(), "SolarIrradiation");

        var result = new List<FeatureRow>(rawRows.Count);
        for (var index = 0; index < rawRows.Count; index++)
        {
            var row = rawRows[index];
            var target = targets[index];

            var hour = row.UtcTime.Hour;
            var minute = row.UtcTime.Minute;
            var dayOfWeek = (int)row.UtcTime.DayOfWeek;

            // Encode hour/day-of-week as sin/cos cycles so boundary neighbors stay close (23↔0, Sunday↔Monday).
            var hourAngle = 2d * Math.PI * (hour / 24d);
            var weekdayAngle = 2d * Math.PI * (dayOfWeek / 7d);

            result.Add(new FeatureRow(
                row.UtcTime,
                target,
                temperatures[index],
                windspeeds[index],
                solarIrradiations[index],
                hour,
                minute,
                dayOfWeek,
                holidays.Contains(DateOnly.FromDateTime(row.UtcTime)),
                Math.Sin(hourAngle),
                Math.Cos(hourAngle),
                Math.Sin(weekdayAngle),
                Math.Cos(weekdayAngle)));
        }

        return result;
    }

    public static PreprocessedDataset BuildPreprocessedDatasetForEvaluation(
        string dataCsvPath,
        string holidaysCsvPath,
        int validationWindowDays = PipelineConstants.DefaultValidationWindowDays)
    {
        if (validationWindowDays <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(validationWindowDays), "Validation window days must be greater than zero.");
        }

        var rawRows = ReadRawDataRows(dataCsvPath).OrderBy(row => row.UtcTime).ToList();
        var inputRowsBeforeDeduplication = rawRows.Count;
        rawRows = DeduplicateByUtcTimeKeepLast(rawRows, out var droppedDuplicateTimestampRows);
        if (rawRows.Count == 0)
        {
            var emptySummary = new PreprocessingAuditSummary(DateTime.MinValue, 0, 0, 0, 0, 0, 0, 0);
            return new PreprocessedDataset([], [], emptySummary);
        }

        var holidays = ReadSwedishPublicHolidays(holidaysCsvPath);
        var validationStartUtc = rawRows[^1].UtcTime.AddDays(-validationWindowDays);

        EnsureObservedBeforeValidation(rawRows, validationStartUtc, row => row.Target, "Target");
        EnsureObservedBeforeValidation(rawRows, validationStartUtc, row => row.Temperature, "Temperature");
        EnsureObservedBeforeValidation(rawRows, validationStartUtc, row => row.Windspeed, "Windspeed");
        EnsureObservedBeforeValidation(rawRows, validationStartUtc, row => row.SolarIrradiation, "SolarIrradiation");


        // Non-obvious but intentional: we split the dataset into training and validation segments, then apply forward-fill imputation separately,
        // seeding the validation segment with the last observed training target to avoid leakage from training to validation.
        var trainRows = rawRows.Where(row => row.UtcTime < validationStartUtc).ToList();
        var validationRows = rawRows.Where(row => row.UtcTime >= validationStartUtc).ToList();

        var trainBuilt = BuildRowsWithTargetFlags(trainRows, holidays);

        var lastObservedTrainingTarget = trainRows
            .Where(row => row.Target.HasValue)
            .Select(row => row.Target!.Value)
            .Last();

        var validationBuilt = BuildRowsWithTargetFlags(
            validationRows,
            holidays,
            seedTargetFromPriorSegment: true,
            priorSegmentTargetValue: lastObservedTrainingTarget);

        var persistedFeatures = new List<FeatureRow>(trainBuilt.Features.Count + validationBuilt.Features.Count);
        persistedFeatures.AddRange(trainBuilt.Features);

        var auditEvents = new List<PreprocessingAuditEvent>();
        var droppedValidationRows = 0;

        for (var index = 0; index < validationBuilt.Features.Count; index++)
        {
            var row = validationBuilt.Features[index];
            var isTargetImputed = validationBuilt.TargetIsImputed[index];
            var isSourcedFromTraining = validationBuilt.TargetIsSourcedFromPriorSegment[index];
            // Non-obvious but intentional: drop only validation targets imputed from training context
            // (cross-split leakage), while keeping validation-to-validation imputations.
            var includeInPersistedDataset = !(isTargetImputed && isSourcedFromTraining);

            if (includeInPersistedDataset)
            {
                persistedFeatures.Add(row);
            }
            else
            {
                droppedValidationRows++;
                auditEvents.Add(new PreprocessingAuditEvent(
                    row.UtcTime,
                    "ValidationRowDropped",
                    "Target was imputed from training context.",
                    true,
                    true,
                    "Training"));
            }
        }

        var summary = new PreprocessingAuditSummary(
            validationStartUtc,
            inputRowsBeforeDeduplication,
            rawRows.Count,
            trainRows.Count,
            validationRows.Count,
            persistedFeatures.Count,
            droppedValidationRows,
            droppedDuplicateTimestampRows);

        return new PreprocessedDataset(persistedFeatures, auditEvents, summary);
    }

    public static IReadOnlyCollection<DateOnly> ReadSwedishPublicHolidays(string holidaysCsvPath)
    {
        var dates = new HashSet<DateOnly>();
        using var reader = new StreamReader(holidaysCsvPath);

        _ = reader.ReadLine();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split(';');
            if (parts.Length < 7)
            {
                continue;
            }

            if (!string.Equals(parts[1], "SE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!DateOnly.TryParseExact(parts[2], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                continue;
            }

            dates.Add(date);
        }

        return dates;
    }

    public static void WriteFeatureMatrixCsv(IReadOnlyList<FeatureRow> features, string outputCsvPath)
    {
        var directory = Path.GetDirectoryName(outputCsvPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var writer = new StreamWriter(outputCsvPath, false);
        writer.WriteLine("utcTime;Target;Temperature;Windspeed;SolarIrradiation;HourOfDay;MinuteOfHour;DayOfWeek;IsHoliday;HourSin;HourCos;WeekdaySin;WeekdayCos");

        foreach (var row in features)
        {
            writer.WriteLine(string.Join(';',
                row.UtcTime.ToString("yyyy-MM-dd HH:mm:ss", InvariantCulture),
                row.Target.ToString(InvariantCulture),
                row.Temperature.ToString(InvariantCulture),
                row.Windspeed.ToString(InvariantCulture),
                row.SolarIrradiation.ToString(InvariantCulture),
                row.HourOfDay.ToString(InvariantCulture),
                row.MinuteOfHour.ToString(InvariantCulture),
                row.DayOfWeek.ToString(InvariantCulture),
                row.IsHoliday.ToString(InvariantCulture),
                row.HourSin.ToString(InvariantCulture),
                row.HourCos.ToString(InvariantCulture),
                row.WeekdaySin.ToString(InvariantCulture),
                row.WeekdayCos.ToString(InvariantCulture)));
        }
    }

    public static void WritePreprocessingAuditCsv(IReadOnlyList<PreprocessingAuditEvent> auditEvents, string outputCsvPath)
    {
        var directory = Path.GetDirectoryName(outputCsvPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var writer = new StreamWriter(outputCsvPath, false);
        writer.WriteLine("utcTime;EventType;Reason;IsValidation;IsTargetImputed;ImputationSource");

        foreach (var row in auditEvents)
        {
            writer.WriteLine(string.Join(';',
                row.UtcTime.ToString("yyyy-MM-dd HH:mm:ss", InvariantCulture),
                row.EventType,
                row.Reason,
                row.IsValidation.ToString(InvariantCulture),
                row.IsTargetImputed.ToString(InvariantCulture),
                row.ImputationSource));
        }
    }

    public static void WritePreprocessingSummaryJson(PreprocessingAuditSummary summary, string outputJsonPath)
    {
        var directory = Path.GetDirectoryName(outputJsonPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(outputJsonPath, json);
    }

    public static IReadOnlyList<RawDataRow> ReadRawDataRows(string dataCsvPath)
    {
        var rows = new List<RawDataRow>();
        using var reader = new StreamReader(dataCsvPath);

        _ = reader.ReadLine();
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
            if (parts.Length < 5)
            {
                throw new FormatException($"Invalid row at line {lineNumber}: expected at least 5 columns.");
            }

            if (!DateTime.TryParseExact(
                    parts[0],
                    AcceptedDateTimeFormats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var utcTime))
            {
                throw new FormatException($"Invalid utcTime at line {lineNumber}: '{parts[0]}'.");
            }

            var target = ParseNullableDouble(parts[1], lineNumber, "Target");
            var temperature = ParseNullableDouble(parts[2], lineNumber, "Temperature");
            var windspeed = ParseNullableDouble(parts[3], lineNumber, "Windspeed");
            var solarIrradiation = ParseNullableDouble(parts[4], lineNumber, "SolarIrradiation");

            rows.Add(new RawDataRow(utcTime, target, temperature, windspeed, solarIrradiation));
        }

        return rows;
    }

    public static IReadOnlyList<double> ForwardFillTargets(IReadOnlyList<double?> targets)
    {
        return ForwardFillWithImputationFlags(targets, "Target")
            .Select(value => value.Value)
            .ToList();
    }

    private static IReadOnlyList<double> ForwardFillRequiredSeries(IReadOnlyList<double?> values, string columnName)
    {
        return ForwardFillWithImputationFlags(values, columnName)
            .Select(value => value.Value)
            .ToList();
    }

    private static (
        IReadOnlyList<FeatureRow> Features,
        IReadOnlyList<bool> TargetIsImputed,
        IReadOnlyList<bool> TargetIsSourcedFromPriorSegment) BuildRowsWithTargetFlags(
        IReadOnlyList<RawDataRow> rows,
        IReadOnlyCollection<DateOnly> holidays,
        bool seedTargetFromPriorSegment = false,
        double priorSegmentTargetValue = 0d)
    {
        if (rows.Count == 0)
        {
            return ([], [], []);
        }

        var targetFilled = ForwardFillWithImputationFlags(
            rows.Select(row => row.Target).ToList(),
            "Target",
            seedFromPriorSegment: seedTargetFromPriorSegment,
            priorSegmentValue: priorSegmentTargetValue);
        var temperatures = ForwardFillRequiredSeries(rows.Select(row => row.Temperature).ToList(), "Temperature");
        var windspeeds = ForwardFillRequiredSeries(rows.Select(row => row.Windspeed).ToList(), "Windspeed");
        var solarIrradiations = ForwardFillRequiredSeries(rows.Select(row => row.SolarIrradiation).ToList(), "SolarIrradiation");

        var result = new List<FeatureRow>(rows.Count);
        var targetIsImputed = new List<bool>(rows.Count);
        var targetIsSourcedFromPriorSegment = new List<bool>(rows.Count);

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var target = targetFilled[index].Value;

            var hour = row.UtcTime.Hour;
            var minute = row.UtcTime.Minute;
            var dayOfWeek = (int)row.UtcTime.DayOfWeek;

            // Encode hour/day-of-week as sin/cos cycles so boundary neighbors stay close (23↔0, Sunday↔Monday).
            var hourAngle = 2d * Math.PI * (hour / 24d);
            var weekdayAngle = 2d * Math.PI * (dayOfWeek / 7d);

            result.Add(new FeatureRow(
                row.UtcTime,
                target,
                temperatures[index],
                windspeeds[index],
                solarIrradiations[index],
                hour,
                minute,
                dayOfWeek,
                holidays.Contains(DateOnly.FromDateTime(row.UtcTime)),
                Math.Sin(hourAngle),
                Math.Cos(hourAngle),
                Math.Sin(weekdayAngle),
                Math.Cos(weekdayAngle)));

            targetIsImputed.Add(targetFilled[index].IsImputed);
            targetIsSourcedFromPriorSegment.Add(targetFilled[index].IsSourcedFromPriorSegment);
        }

        return (result, targetIsImputed, targetIsSourcedFromPriorSegment);
    }

    private static IReadOnlyList<FilledValue> ForwardFillWithImputationFlags(
        IReadOnlyList<double?> values,
        string columnName,
        bool seedFromPriorSegment = false,
        double priorSegmentValue = 0d)
    {
        var firstKnownIndex = -1;
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index].HasValue)
            {
                firstKnownIndex = index;
                break;
            }
        }

        if (firstKnownIndex < 0)
        {
            if (!seedFromPriorSegment)
            {
                throw new InvalidOperationException($"{columnName} column has no valid values to forward-fill.");
            }

            firstKnownIndex = values.Count;
        }

        // Non-obvious but intentional: we seed leading nulls with the first known future value,
        // then continue with regular forward-fill.
        var filled = new List<FilledValue>(values.Count);
        var previous = seedFromPriorSegment
            ? priorSegmentValue
            : values[firstKnownIndex]!.Value;
        var sourceFromPriorSegment = seedFromPriorSegment;

        for (var index = 0; index < values.Count; index++)
        {
            var isImputed = !values[index].HasValue;
            if (values[index].HasValue)
            {
                previous = values[index]!.Value;
                sourceFromPriorSegment = false;
            }
            else if (!seedFromPriorSegment && index >= firstKnownIndex)
            {
                sourceFromPriorSegment = false;
            }

            filled.Add(new FilledValue(previous, isImputed, isImputed && sourceFromPriorSegment));
        }

        return filled;
    }

    private static void EnsureObservedBeforeValidation(
        IReadOnlyList<RawDataRow> rows,
        DateTime validationStartUtc,
        Func<RawDataRow, double?> selector,
        string columnName)
    {
        var hasObservedValueBeforeValidation = rows.Any(row => row.UtcTime < validationStartUtc && selector(row).HasValue);
        if (!hasObservedValueBeforeValidation)
        {
            throw new InvalidOperationException(
                $"{columnName} has no observed values before validation start ({validationStartUtc:yyyy-MM-dd HH:mm:ss} UTC).");
        }
    }

    private static List<RawDataRow> DeduplicateByUtcTimeKeepLast(IReadOnlyList<RawDataRow> rows, out int droppedRows)
    {
        if (rows.Count == 0)
        {
            droppedRows = 0;
            return [];
        }

        var deduplicated = rows
            .GroupBy(row => row.UtcTime)
            .Select(group => group.Last())
            .OrderBy(row => row.UtcTime)
            .ToList();

        droppedRows = rows.Count - deduplicated.Count;
        return deduplicated;
    }

    private static double? ParseNullableDouble(string value, int lineNumber, string columnName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!double.TryParse(value, NumberStyles.Float, SwedishCulture, out var parsed))
        {
            throw new FormatException($"Invalid {columnName} at line {lineNumber}: '{value}'.");
        }

        return parsed;
    }
}
