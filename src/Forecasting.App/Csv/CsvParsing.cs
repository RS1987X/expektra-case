using System.Globalization;

namespace Forecasting.App;

public static class CsvParsing
{
    private static readonly CultureInfo InvariantCulture = CultureInfo.InvariantCulture;

    public static int FindRequiredColumnIndex(IReadOnlyList<string> columns, string name, string csvContext)
    {
        for (var index = 0; index < columns.Count; index++)
        {
            if (string.Equals(columns[index], name, StringComparison.Ordinal))
            {
                return index;
            }
        }

        throw new FormatException($"Missing required column '{name}' in {csvContext}.");
    }

    public static double ParseRequiredDouble(string value, int lineNumber, string columnName, bool rejectNonFinite = false)
    {
        if (!double.TryParse(value, NumberStyles.Float, InvariantCulture, out var parsed))
        {
            throw new FormatException($"Invalid {columnName} at line {lineNumber}: '{value}'.");
        }

        if (rejectNonFinite && !double.IsFinite(parsed))
        {
            throw new FormatException($"Invalid {columnName} at line {lineNumber}: non-finite value '{value}'.");
        }

        return parsed;
    }

    public static int ParseRequiredInt(string value, int lineNumber, string columnName)
    {
        if (!int.TryParse(value, NumberStyles.Integer, InvariantCulture, out var parsed))
        {
            throw new FormatException($"Invalid {columnName} at line {lineNumber}: '{value}'.");
        }

        return parsed;
    }

    public static bool ParseRequiredBool(string value, int lineNumber, string columnName)
    {
        if (!bool.TryParse(value, out var parsed))
        {
            throw new FormatException($"Invalid {columnName} at line {lineNumber}: '{value}'.");
        }

        return parsed;
    }

    public static DateTime ParseRequiredUtcDateTime(
        string value,
        int lineNumber,
        string columnName,
        string format = "yyyy-MM-dd HH:mm:ss")
    {
        if (!DateTime.TryParseExact(
                value,
                format,
                InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            throw new FormatException($"Invalid {columnName} at line {lineNumber}: '{value}'.");
        }

        return parsed;
    }
}
