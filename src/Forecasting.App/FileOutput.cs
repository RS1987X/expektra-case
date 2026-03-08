using System.Text.Json;

namespace Forecasting.App;

public static class FileOutput
{
    public static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    public static void EnsureParentDirectory(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
