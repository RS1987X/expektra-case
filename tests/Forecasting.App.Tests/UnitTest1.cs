using Forecasting.App;

namespace Forecasting.App.Tests;

public class Part1PreprocessingTests
{
    [Fact]
    public void BuildFeatureMatrix_ParsesAndBuildsExpectedFeatures()
    {
        var dataPath = CreateTempFile(
            """
            utcTime;Target;Temperature;Windspeed;SolarIrradiation
            2024-01-01 00:00;10,0;1,0;2,0;0
            2024-01-01 00:15;;;2,1;
            2024-01-01 00:30;12,0;1,2;;5
            """);

        var holidaysPath = CreateTempFile(
            """
            Id;Country;StartDate;EndDate;Type;RegionalScope;Name
            malformed-row
            123;SE;2024-01-01;;Public;National;SV Nyårsdagen
            """);

        try
        {
            var features = Part1Preprocessing.BuildFeatureMatrix(dataPath, holidaysPath);

            Assert.Equal(3, features.Count);
            Assert.Equal(10.0, features[1].Target);
            Assert.Equal(1.0, features[1].Temperature);
            Assert.Equal(2.1, features[2].Windspeed);
            Assert.Equal(0.0, features[1].SolarIrradiation);
            Assert.True(features[0].IsHoliday);
            Assert.Equal(0, features[0].HourOfDay);
            Assert.Equal(15, features[1].MinuteOfHour);
            Assert.InRange(features[0].HourSin, -1.0 - 1e-9, 1.0 + 1e-9);
            Assert.InRange(features[0].HourCos, -1.0 - 1e-9, 1.0 + 1e-9);
            Assert.InRange(features[0].WeekdaySin, -1.0 - 1e-9, 1.0 + 1e-9);
            Assert.InRange(features[0].WeekdayCos, -1.0 - 1e-9, 1.0 + 1e-9);
        }
        finally
        {
            File.Delete(dataPath);
            File.Delete(holidaysPath);
        }
    }

    [Fact]
    public void ForwardFillTargets_SeedsLeadingNullsFromFirstKnownValue()
    {
        var filled = Part1Preprocessing.ForwardFillTargets([null, null, 3.0, null, 5.0]);

        Assert.Equal([3.0, 3.0, 3.0, 3.0, 5.0], filled);
    }

    [Fact]
    public void WriteFeatureMatrixCsv_CreatesCsvInArtifactsShape()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), "artifacts", $"features-{Guid.NewGuid():N}.csv");
        var features = new[]
        {
            new FeatureRow(
                new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                10.0,
                1.0,
                2.0,
                0.0,
                0,
                0,
                1,
                true,
                0.0,
                1.0,
                0.5,
                0.5)
        };

        try
        {
            Part1Preprocessing.WriteFeatureMatrixCsv(features, outputPath);

            Assert.True(File.Exists(outputPath));
            var lines = File.ReadAllLines(outputPath);
            Assert.Equal(2, lines.Length);
            Assert.Contains("utcTime;Target;Temperature;Windspeed;SolarIrradiation", lines[0]);
            Assert.Contains("2024-01-01 00:00:00;10;1;2;0;0;0;1;True", lines[1]);
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            var parent = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
            {
                Directory.Delete(parent, false);
            }
        }
    }

    private static string CreateTempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"forecasting-test-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, content.Trim() + Environment.NewLine);
        return path;
    }
}