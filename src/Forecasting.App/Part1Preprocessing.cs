using System.Globalization;

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

public static class Part1Preprocessing
{
    private static readonly CultureInfo SwedishCulture = CultureInfo.GetCultureInfo("sv-SE");
    private static readonly string[] AcceptedDateTimeFormats = ["yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss"];

    public static IReadOnlyList<FeatureRow> BuildFeatureMatrix(string dataCsvPath, string holidaysCsvPath)
    {
        var rawRows = ReadRawDataRows(dataCsvPath).OrderBy(row => row.UtcTime).ToList();
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
        var firstKnownIndex = -1;
        for (var index = 0; index < targets.Count; index++)
        {
            if (targets[index].HasValue)
            {
                firstKnownIndex = index;
                break;
            }
        }

        if (firstKnownIndex < 0)
        {
            throw new InvalidOperationException("Target column has no valid values to forward-fill.");
        }

        // Non-obvious but intentional: we seed leading nulls with the first valid future value,
        // then do a normal forward-fill for all subsequent nulls.
        var filled = new List<double>(targets.Count);
        var previous = targets[firstKnownIndex]!.Value;

        for (var index = 0; index < targets.Count; index++)
        {
            if (targets[index].HasValue)
            {
                previous = targets[index]!.Value;
            }

            filled.Add(previous);
        }

        return filled;
    }

    private static IReadOnlyList<double> ForwardFillRequiredSeries(IReadOnlyList<double?> values, string columnName)
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
            throw new InvalidOperationException($"{columnName} column has no valid values to forward-fill.");
        }

        // Non-obvious but intentional: for sparse operational telemetry, we seed leading nulls with
        // the first known future value, then continue with regular forward-fill.
        var filled = new List<double>(values.Count);
        var previous = values[firstKnownIndex]!.Value;

        for (var index = 0; index < values.Count; index++)
        {
            if (values[index].HasValue)
            {
                previous = values[index]!.Value;
            }

            filled.Add(previous);
        }

        return filled;
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
