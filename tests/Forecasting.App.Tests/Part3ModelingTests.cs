using Forecasting.App;

namespace Forecasting.App.Tests;

public class Part3ModelingTests
{
    [Fact]
    public void ReadPart2DatasetCsv_EmptyFileOrHeader_ReturnsEmpty()
    {
        var path = CreateTempFile("\n");
        try
        {
            var rows = Part3Modeling.ReadPart2DatasetCsv(path);
            Assert.Empty(rows);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadPart2DatasetCsv_ValidRowAndBlankLines_Parses()
    {
        var path = CreatePart3CsvWithData(BuildPart3CsvRow(), includeBlankLine: true);
        try
        {
            var rows = Part3Modeling.ReadPart2DatasetCsv(path);
            var row = Assert.Single(rows);
            Assert.Equal("Train", row.Split);
            Assert.Equal(192, row.HorizonTargets.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadPart2DatasetCsv_InvalidColumnCount_ThrowsFormatException()
    {
        var path = CreatePart3CsvWithData("2024-01-01 00:00:00;1;2;3");
        try
        {
            var ex = Assert.Throws<FormatException>(() => Part3Modeling.ReadPart2DatasetCsv(path));
            Assert.Contains("expected at least", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadPart2DatasetCsv_InvalidAnchorTime_ThrowsFormatException()
    {
        var cells = BuildPart3CsvCells();
        cells[0] = "not-a-date";
        var path = CreatePart3CsvWithData(string.Join(';', cells));
        try
        {
            var ex = Assert.Throws<FormatException>(() => Part3Modeling.ReadPart2DatasetCsv(path));
            Assert.Contains("anchorUtcTime", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadPart2DatasetCsv_InvalidNumericAndBool_ThrowFormatException()
    {
        var numericCells = BuildPart3CsvCells();
        numericCells[5] = "NaNHour";
        var numericPath = CreatePart3CsvWithData(string.Join(';', numericCells));

        var boolCells = BuildPart3CsvCells();
        boolCells[8] = "not_bool";
        var boolPath = CreatePart3CsvWithData(string.Join(';', boolCells));

        try
        {
            Assert.Throws<FormatException>(() => Part3Modeling.ReadPart2DatasetCsv(numericPath));
            Assert.Throws<FormatException>(() => Part3Modeling.ReadPart2DatasetCsv(boolPath));
        }
        finally
        {
            File.Delete(numericPath);
            File.Delete(boolPath);
        }
    }

    [Fact]
    public void RunModels_WithoutTrainRows_ThrowsInvalidOperationException()
    {
        var rows = BuildSyntheticPart3Rows(trainCount: 0, validationCount: 3);

        var ex = Assert.Throws<InvalidOperationException>(() => Part3Modeling.RunModels(rows));

        Assert.Contains("training row", ex.Message);
    }

    [Fact]
    public void RunModels_WithoutValidationRows_ThrowsInvalidOperationException()
    {
        var rows = BuildSyntheticPart3Rows(trainCount: 10, validationCount: 0);

        var ex = Assert.Throws<InvalidOperationException>(() => Part3Modeling.RunModels(rows));

        Assert.Contains("validation row", ex.Message);
    }

    [Fact]
    public void RunModels_BaselineCanUseGlobalMeanFallback_WhenSeasonalKeyMissing()
    {
        var trainRows = BuildSyntheticPart3Rows(trainCount: 4, validationCount: 0)
            .Select((row, index) => row with
            {
                AnchorUtcTime = new DateTime(2024, 1, 1, 0, index * 15, 0, DateTimeKind.Utc),
                Split = "Train"
            })
            .ToList();

        var validationAnchorTime = new DateTime(2024, 1, 2, 6, 0, 0, DateTimeKind.Utc);
        var validationRow = trainRows[0] with
        {
            AnchorUtcTime = validationAnchorTime,
            Split = "Validation",
            IsHoliday = true
        };

        var rows = trainRows.Concat([validationRow]).ToList();

        var result = Part3Modeling.RunModels(rows);
        var baseline = Assert.Single(result.Forecasts.Where(f =>
            f.ModelName == "BaselineSeasonal" &&
            f.AnchorUtcTime == validationAnchorTime));
        Assert.True(baseline.ExogenousFallbackSteps > 0);
    }

    [Fact]
    public void RunModels_BaselineProducesFullHorizonAndSeasonalStepPrediction()
    {
        var rows = BuildSyntheticPart3Rows(trainCount: 320, validationCount: 6);
        var trainRows = rows.Where(row => row.Split == "Train").ToList();
        var firstValidation = rows.First(row => row.Split == "Validation");

        var result = Part3Modeling.RunModels(rows);

        var baseline = Assert.Single(result.Forecasts.Where(f =>
            f.ModelName == "BaselineSeasonal" &&
            f.AnchorUtcTime == firstValidation.AnchorUtcTime));

        Assert.Equal(192, baseline.PredictedTargets.Count);

        var firstStepTimestamp = firstValidation.AnchorUtcTime.AddMinutes(15);
        var expectedFirstStep = trainRows
            .Where(row => row.DayOfWeek == (int)firstStepTimestamp.DayOfWeek)
            .Where(row => row.HourOfDay == firstStepTimestamp.Hour)
            .Where(row => row.MinuteOfHour == firstStepTimestamp.Minute)
            .Select(row => row.TargetAtT)
            .DefaultIfEmpty(trainRows.Average(row => row.TargetAtT))
            .Average();

        Assert.Equal(expectedFirstStep, baseline.PredictedTargets[0], 8);
    }

    [Fact]
    public void RunModels_FastTreeProducesFiniteFullHorizonPredictions()
    {
        var rows = BuildSyntheticPart3Rows(trainCount: 320, validationCount: 4);

        var result = Part3Modeling.RunModels(rows);

        var fastTreeForecasts = result.Forecasts.Where(f => f.ModelName == "FastTreeRecursive").ToList();
        Assert.Equal(rows.Count, fastTreeForecasts.Count);
        Assert.All(fastTreeForecasts, forecast =>
        {
            Assert.Equal(192, forecast.PredictedTargets.Count);
            Assert.All(forecast.PredictedTargets, value => Assert.True(double.IsFinite(value)));
        });

        var summary = Assert.Single(result.Summary.Models.Where(model => model.ModelName == "FastTreeRecursive"));
        Assert.Equal(rows.Count, summary.AnchorsForecasted);
        Assert.Equal(192, summary.HorizonSteps);

        var splitCounts = result.Forecasts
            .Where(forecast => forecast.ModelName == "FastTreeRecursive")
            .GroupBy(forecast => forecast.Split, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(320, splitCounts["Train"]);
        Assert.Equal(4, splitCounts["Validation"]);
    }

    [Fact]
    public void WriteForecastsCsvAndSummaryJson_WritesExpectedArtifacts()
    {
        var rows = BuildSyntheticPart3Rows(trainCount: 300, validationCount: 2);
        var result = Part3Modeling.RunModels(rows);

        var outputDir = Path.Combine(Path.GetTempPath(), $"part3-tests-{Guid.NewGuid():N}");
        var predictionsPath = Path.Combine(outputDir, "predictions.csv");
        var summaryPath = Path.Combine(outputDir, "predictions.summary.json");

        try
        {
            Part3Modeling.WriteForecastsCsv(result.Forecasts, predictionsPath);
            Part3Modeling.WriteSummaryJson(result.Summary, summaryPath);

            Assert.True(File.Exists(predictionsPath));
            Assert.True(File.Exists(summaryPath));

            var lines = File.ReadAllLines(predictionsPath);
            Assert.True(lines.Length > 1);
            Assert.Contains("Pred_tPlus192", lines[0]);
            Assert.Contains("Actual_tPlus192", lines[0]);

            var trainRows = lines.Count(line => line.Contains(";Train;", StringComparison.Ordinal));
            var validationRows = lines.Count(line => line.Contains(";Validation;", StringComparison.Ordinal));
            Assert.True(trainRows > 0);
            Assert.True(validationRows > 0);

            var summaryJson = File.ReadAllText(summaryPath);
            Assert.Contains("BaselineSeasonal", summaryJson);
            Assert.Contains("FastTreeRecursive", summaryJson);
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, true);
            }
        }
    }

    private static List<Part3InputRow> BuildSyntheticPart3Rows(int trainCount, int validationCount)
    {
        var rows = new List<Part3InputRow>(trainCount + validationCount);
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var index = 0; index < trainCount + validationCount; index++)
        {
            var utcTime = start.AddMinutes(index * 15);
            var target = 100.0 + Math.Sin(index / 8.0) * 5.0 + index * 0.03;
            var temperature = 10.0 + Math.Sin(index / 32.0) * 7.0;
            var windspeed = 4.0 + Math.Cos(index / 40.0) * 1.5;
            var solar = Math.Max(0.0, Math.Sin((utcTime.Hour / 24.0) * Math.PI) * 500.0);
            var split = index < trainCount ? "Train" : "Validation";

            var horizon = new double[192];
            for (var step = 0; step < horizon.Length; step++)
            {
                horizon[step] = target + (step + 1) * 0.05;
            }

            var hour = utcTime.Hour;
            var minute = utcTime.Minute;
            var dayOfWeek = (int)utcTime.DayOfWeek;
            var hourAngle = 2d * Math.PI * (hour / 24d);
            var weekdayAngle = 2d * Math.PI * (dayOfWeek / 7d);

            rows.Add(new Part3InputRow(
                utcTime,
                target,
                temperature,
                windspeed,
                solar,
                hour,
                minute,
                dayOfWeek,
                false,
                Math.Sin(hourAngle),
                Math.Cos(hourAngle),
                Math.Sin(weekdayAngle),
                Math.Cos(weekdayAngle),
                target - 1.0,
                target - 2.0,
                target - 0.5,
                0.2,
                target - 1.0,
                0.4,
                split,
                horizon));
        }

        return rows;
    }

    private static string CreatePart3CsvWithData(string row, bool includeBlankLine = false)
    {
        var lines = new List<string> { BuildPart3CsvHeader() };
        if (includeBlankLine)
        {
            lines.Add(string.Empty);
        }

        lines.Add(row);
        return CreateTempFile(string.Join(Environment.NewLine, lines));
    }

    private static string CreateTempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"part3-read-tests-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, content);
        return path;
    }

    private static string BuildPart3CsvHeader()
    {
        var firstColumns = new[]
        {
            "anchorUtcTime",
            "TargetAtT",
            "Temperature",
            "Windspeed",
            "SolarIrradiation",
            "HourOfDay",
            "MinuteOfHour",
            "DayOfWeek",
            "IsHoliday",
            "HourSin",
            "HourCos",
            "WeekdaySin",
            "WeekdayCos",
            "TargetLag192",
            "TargetLag672",
            "TargetMean16",
            "TargetStd16",
            "TargetMean96",
            "TargetStd96",
            "Split"
        };

        var horizonColumns = Enumerable.Range(1, 192).Select(index => $"Target_tPlus{index}");
        return string.Join(';', firstColumns.Concat(horizonColumns));
    }

    private static string BuildPart3CsvRow()
    {
        return string.Join(';', BuildPart3CsvCells());
    }

    private static string[] BuildPart3CsvCells()
    {
        var cells = new List<string>
        {
            "2024-01-01 00:00:00",
            "100",
            "10",
            "5",
            "0",
            "0",
            "0",
            "1",
            "false",
            "0",
            "1",
            "0",
            "1",
            "99",
            "98",
            "100",
            "0.1",
            "100",
            "0.2",
            "Train"
        };

        cells.AddRange(Enumerable.Range(1, 192).Select(index => (100 + index * 0.01).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return cells.ToArray();
    }
}