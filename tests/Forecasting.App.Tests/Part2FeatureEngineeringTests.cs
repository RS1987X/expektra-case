using Forecasting.App;

namespace Forecasting.App.Tests;

public class Part2FeatureEngineeringTests
{
    [Fact]
    public void BuildDataset_ComputesExpectedLagsRollingAndHorizonLabels()
    {
        var rows = BuildSyntheticFeatureRows(1000);
        var validationStartUtc = rows[767].UtcTime;

        var dataset = Part2FeatureEngineering.BuildDataset(rows, validationStartUtc);

        Assert.Equal(41, dataset.Rows.Count);
        Assert.Equal(41, dataset.Summary.CandidateAnchors);
        Assert.Equal(0, dataset.Summary.PurgedAnchors);
        Assert.Equal(0, dataset.Summary.TrainAnchors);
        Assert.Equal(41, dataset.Summary.ValidationAnchors);

        var first = dataset.Rows[0];
        Assert.Equal(rows[767].UtcTime, first.AnchorUtcTime);
        Assert.Equal("Validation", first.Split);
        Assert.Equal(575.0, first.TargetLag192);
        Assert.Equal(95.0, first.TargetLag672);
        Assert.Equal(567.5, first.TargetLag192Mean16, 8);
        Assert.Equal(Math.Sqrt(21.25), first.TargetLag192Std16, 8);
        Assert.Equal(527.5, first.TargetLag192Mean96, 8);
        Assert.Equal(Math.Sqrt((96d * 96d - 1d) / 12d), first.TargetLag192Std96, 8);
        Assert.Equal(87.5, first.TargetLag672Mean16, 8);
        Assert.Equal(Math.Sqrt(21.25), first.TargetLag672Std16, 8);
        Assert.Equal(47.5, first.TargetLag672Mean96, 8);
        Assert.Equal(Math.Sqrt((96d * 96d - 1d) / 12d), first.TargetLag672Std96, 8);

        Assert.Equal(192, first.HorizonTargets.Count);
        Assert.Equal(768.0, first.HorizonTargets[0]);
        Assert.Equal(959.0, first.HorizonTargets[^1]);
    }

    [Fact]
    public void BuildDataset_DeduplicatesFeatureRowsBeforePart2Generation()
    {
        var rows = new List<FeatureRow>
        {
            CreateFeatureRow(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 1.0),
            CreateFeatureRow(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 9.0),
        };

        for (var index = 1; index < 900; index++)
        {
            rows.Add(CreateFeatureRow(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(index * 15), index + 1));
        }

        var dataset = Part2FeatureEngineering.BuildDataset(rows, rows[700].UtcTime);

        Assert.Equal(901, dataset.Summary.InputRowsBeforeDeduplication);
        Assert.Equal(900, dataset.Summary.TotalRows);
        Assert.Equal(1, dataset.Summary.DroppedDuplicateTimestampRows);
    }

    [Fact]
    public void BuildDataset_SplitsIntoTrainAndValidationAndPurgesBoundaryAnchors()
    {
        var rows = BuildSyntheticFeatureRows(1200);
        var validationStartUtc = rows[1000].UtcTime;

        var dataset = Part2FeatureEngineering.BuildDataset(rows, validationStartUtc);

        Assert.Equal(241, dataset.Summary.CandidateAnchors);
        Assert.Equal(41, dataset.Summary.TrainAnchors);
        Assert.Equal(8, dataset.Summary.ValidationAnchors);
        Assert.Equal(192, dataset.Summary.PurgedAnchors);
        Assert.Equal(49, dataset.Rows.Count);

        Assert.Equal("Train", dataset.Rows[0].Split);
        Assert.Equal(rows[767].UtcTime, dataset.Rows[0].AnchorUtcTime);
        Assert.Equal("Train", dataset.Rows[40].Split);
        Assert.Equal(rows[807].UtcTime, dataset.Rows[40].AnchorUtcTime);

        Assert.Equal("Validation", dataset.Rows[41].Split);
        Assert.Equal(rows[1000].UtcTime, dataset.Rows[41].AnchorUtcTime);
    }

    [Fact]
    public void BuildDataset_ReturnsEmptyDatasetForEmptyInput()
    {
        var dataset = Part2FeatureEngineering.BuildDataset([]);

        Assert.Empty(dataset.Rows);
        Assert.Equal(0, dataset.Summary.TotalRows);
        Assert.Equal(0, dataset.Summary.CandidateAnchors);
        Assert.Equal(0, dataset.Summary.OutputRows);
    }

    [Fact]
    public void BuildDataset_ReturnsEmptyDatasetWhenLookbackAndHorizonRequirementsAreNotMet()
    {
        var rows = BuildSyntheticFeatureRows(864);

        var dataset = Part2FeatureEngineering.BuildDataset(rows, rows[^1].UtcTime.AddDays(-30));

        Assert.Empty(dataset.Rows);
        Assert.Equal(864, dataset.Summary.TotalRows);
        Assert.Equal(0, dataset.Summary.CandidateAnchors);
        Assert.Equal(0, dataset.Summary.OutputRows);
    }

    [Fact]
    public void ReadFeatureMatrixCsv_ParsesValidRow()
    {
        var path = CreateTempCsv(
            """
            utcTime;Target;Temperature;Windspeed;SolarIrradiation;HourOfDay;MinuteOfHour;DayOfWeek;IsHoliday;HourSin;HourCos;WeekdaySin;WeekdayCos
            2024-01-01 00:00:00;1.5;2.5;3.5;4.5;0;0;1;False;0;1;0.5;0.5
            """);

        try
        {
            var rows = Part2FeatureEngineering.ReadFeatureMatrixCsv(path);

            var row = Assert.Single(rows);
            Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), row.UtcTime);
            Assert.Equal(1.5, row.Target);
            Assert.False(row.IsHoliday);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadFeatureMatrixCsv_ThrowsOnInvalidColumnCount()
    {
        var path = CreateTempCsv(
            """
            utcTime;Target
            2024-01-01 00:00:00;1.0
            """);

        try
        {
            Assert.Throws<FormatException>(() => Part2FeatureEngineering.ReadFeatureMatrixCsv(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadFeatureMatrixCsv_ThrowsOnInvalidUtcTime()
    {
        var path = CreateTempCsv(
            """
            utcTime;Target;Temperature;Windspeed;SolarIrradiation;HourOfDay;MinuteOfHour;DayOfWeek;IsHoliday;HourSin;HourCos;WeekdaySin;WeekdayCos
            invalid-time;1;2;3;4;0;0;1;False;0;1;0.5;0.5
            """);

        try
        {
            Assert.Throws<FormatException>(() => Part2FeatureEngineering.ReadFeatureMatrixCsv(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadFeatureMatrixCsv_ThrowsOnInvalidNumeric()
    {
        var path = CreateTempCsv(
            """
            utcTime;Target;Temperature;Windspeed;SolarIrradiation;HourOfDay;MinuteOfHour;DayOfWeek;IsHoliday;HourSin;HourCos;WeekdaySin;WeekdayCos
            2024-01-01 00:00:00;not-a-number;2;3;4;0;0;1;False;0;1;0.5;0.5
            """);

        try
        {
            Assert.Throws<FormatException>(() => Part2FeatureEngineering.ReadFeatureMatrixCsv(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadFeatureMatrixCsv_ThrowsOnInvalidBool()
    {
        var path = CreateTempCsv(
            """
            utcTime;Target;Temperature;Windspeed;SolarIrradiation;HourOfDay;MinuteOfHour;DayOfWeek;IsHoliday;HourSin;HourCos;WeekdaySin;WeekdayCos
            2024-01-01 00:00:00;1;2;3;4;0;0;1;nope;0;1;0.5;0.5
            """);

        try
        {
            Assert.Throws<FormatException>(() => Part2FeatureEngineering.ReadFeatureMatrixCsv(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WriteDatasetCsvAndSummaryJson_WritesArtifacts()
    {
        var rows = BuildSyntheticFeatureRows(1000);
        var dataset = Part2FeatureEngineering.BuildDataset(rows, rows[767].UtcTime);
        var outputDir = Path.Combine(Path.GetTempPath(), $"part2-tests-{Guid.NewGuid():N}");
        var csvPath = Path.Combine(outputDir, "part2.csv");
        var summaryPath = Path.Combine(outputDir, "part2.summary.json");

        try
        {
            Part2FeatureEngineering.WriteDatasetCsv(dataset.Rows, csvPath);
            Part2FeatureEngineering.WriteSummaryJson(dataset.Summary, summaryPath);

            Assert.True(File.Exists(csvPath));
            Assert.True(File.Exists(summaryPath));

            var csvLines = File.ReadAllLines(csvPath);
            Assert.True(csvLines.Length >= 2);
            Assert.Contains("Target_tPlus192", csvLines[0]);
            Assert.Contains("Validation", csvLines[1]);

            var summaryJson = File.ReadAllText(summaryPath);
            Assert.Contains("ValidationStartUtc", summaryJson);
            Assert.Contains("OutputRows", summaryJson);
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, true);
            }
        }
    }

    private static List<FeatureRow> BuildSyntheticFeatureRows(int count)
    {
        var rows = new List<FeatureRow>(count);
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var index = 0; index < count; index++)
        {
            rows.Add(CreateFeatureRow(start.AddMinutes(index * 15), index));
        }

        return rows;
    }

    private static FeatureRow CreateFeatureRow(DateTime utcTime, double target)
    {
        var hour = utcTime.Hour;
        var minute = utcTime.Minute;
        var dayOfWeek = (int)utcTime.DayOfWeek;
        var hourAngle = 2d * Math.PI * (hour / 24d);
        var weekdayAngle = 2d * Math.PI * (dayOfWeek / 7d);

        return new FeatureRow(
            utcTime,
            target,
            20.0,
            3.0,
            0.0,
            hour,
            minute,
            dayOfWeek,
            false,
            Math.Sin(hourAngle),
            Math.Cos(hourAngle),
            Math.Sin(weekdayAngle),
            Math.Cos(weekdayAngle));
    }

    private static string CreateTempCsv(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"part2-feature-tests-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, content.Trim() + Environment.NewLine);
        return path;
    }
}
