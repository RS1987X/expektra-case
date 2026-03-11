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
    public void ReadPart2DatasetCsv_MissingRequiredFeatureColumn_ThrowsDeterministicErrorWithPath()
    {
        var columns = BuildPart3CsvHeader().Split(';').Where(column => !string.Equals(column, "Temperature", StringComparison.Ordinal)).ToArray();
        var path = CreateTempFile(string.Join(Environment.NewLine,
            string.Join(';', columns),
            BuildPart3CsvRow()));

        try
        {
            var ex = Assert.Throws<FormatException>(() => Part3Modeling.ReadPart2DatasetCsv(path));
            Assert.Equal($"Missing required column 'Temperature' in part2 supervised dataset '{path}'.", ex.Message);
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
        Assert.True(baseline.FallbackOrRecursiveSteps > 0);
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
            .Select(row => row.TargetAtT)
            .DefaultIfEmpty(trainRows.Average(row => row.TargetAtT))
            .Average();

        Assert.Equal(expectedFirstStep, baseline.PredictedTargets[0], 8);
    }

    [Fact]
    public void RunModels_BaselineAllMissingKeys_UsesGlobalMeanForEntireHorizon()
    {
        var trainRows = new List<Part2SupervisedRow>
        {
            // Saturday keys only; validation horizon below spans Mon->Wed, so all keys miss.
            CreateOracleRow(new DateTime(2024, 1, 6, 0, 0, 0, DateTimeKind.Utc), targetAtT: 10.0, temperature: 1.0, split: "Train"),
            CreateOracleRow(new DateTime(2024, 1, 6, 12, 0, 0, DateTimeKind.Utc), targetAtT: 20.0, temperature: 1.0, split: "Train")
        };

        var validationAnchor = CreateOracleRow(
            new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            targetAtT: 30.0,
            temperature: 1.0,
            split: "Validation");

        var rows = trainRows.Concat([validationAnchor]).ToList();
        var result = Part3Modeling.RunModels(rows);

        var baseline = Assert.Single(result.Forecasts.Where(f =>
            f.ModelName == "BaselineSeasonal" &&
            f.AnchorUtcTime == validationAnchor.AnchorUtcTime &&
            f.Split == "Validation"));

        var expectedGlobalMean = trainRows.Average(row => row.TargetAtT);
        Assert.Equal(PipelineConstants.HorizonSteps, baseline.FallbackOrRecursiveSteps);
        Assert.All(baseline.PredictedTargets, value => Assert.Equal(expectedGlobalMean, value, 8));
    }

    [Fact]
    public void RunModels_BaselineUsesSameWeekdayHourMean_WhenKeyExists()
    {
        var trainRows = new List<Part2SupervisedRow>
        {
            // Step 1 from anchor below is Tuesday 00:15; this key mean should be used.
            CreateOracleRow(new DateTime(2024, 1, 2, 0, 15, 0, DateTimeKind.Utc), targetAtT: 10.0, temperature: 1.0, split: "Train"),
            CreateOracleRow(new DateTime(2024, 1, 9, 0, 15, 0, DateTimeKind.Utc), targetAtT: 14.0, temperature: 1.0, split: "Train")
        };

        var validationAnchor = CreateOracleRow(
            new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            targetAtT: 50.0,
            temperature: 1.0,
            split: "Validation");

        var rows = trainRows.Concat([validationAnchor]).ToList();
        var result = Part3Modeling.RunModels(rows);

        var baseline = Assert.Single(result.Forecasts.Where(f =>
            f.ModelName == "BaselineSeasonal" &&
            f.AnchorUtcTime == validationAnchor.AnchorUtcTime &&
            f.Split == "Validation"));

        Assert.Equal(12.0, baseline.PredictedTargets[0], 8);
    }

    [Fact]
    public void RunModels_BaselineIgnoresMinuteWithinSameWeekdayHourBucket()
    {
        var trainRows = new List<Part2SupervisedRow>
        {
            // Tuesday 00:* values should be averaged together for step 1.
            CreateOracleRow(new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), targetAtT: 10.0, temperature: 1.0, split: "Train"),
            CreateOracleRow(new DateTime(2024, 1, 9, 0, 15, 0, DateTimeKind.Utc), targetAtT: 14.0, temperature: 1.0, split: "Train")
        };

        var validationAnchor = CreateOracleRow(
            new DateTime(2024, 1, 1, 23, 45, 0, DateTimeKind.Utc),
            targetAtT: 50.0,
            temperature: 1.0,
            split: "Validation");

        var rows = trainRows.Concat([validationAnchor]).ToList();
        var result = Part3Modeling.RunModels(rows);

        var baseline = Assert.Single(result.Forecasts.Where(f =>
            f.ModelName == "BaselineSeasonal" &&
            f.AnchorUtcTime == validationAnchor.AnchorUtcTime &&
            f.Split == "Validation"));

        Assert.Equal(12.0, baseline.PredictedTargets[0], 8); // mean(10, 14) across Tuesday 00:xx hour bucket
    }

    [Fact]
    public void RunModels_BaselineLookbackWeeks_UsesRecentWindowForSeasonalMean()
    {
        var keyTimestamp = new DateTime(2024, 1, 2, 0, 15, 0, DateTimeKind.Utc);
        var oldTrain = CreateOracleRow(keyTimestamp, targetAtT: 10.0, temperature: 1.0, split: "Train");
        var recentTrain = CreateOracleRow(keyTimestamp.AddDays(14), targetAtT: 30.0, temperature: 1.0, split: "Train");
        var validationAnchor = CreateOracleRow(
            new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            targetAtT: 50.0,
            temperature: 1.0,
            split: "Validation");

        var rows = new List<Part2SupervisedRow> { oldTrain, recentTrain, validationAnchor };

        var defaultResult = Part3Modeling.RunModels(rows);
        var defaultBaseline = Assert.Single(defaultResult.Forecasts.Where(f =>
            f.ModelName == "BaselineSeasonal" &&
            f.AnchorUtcTime == validationAnchor.AnchorUtcTime));

        var lookbackResult = Part3Modeling.RunModels(
            rows,
            baselineOptions: new BaselineSeasonalOptions(LookbackWeeks: 1));
        var lookbackBaseline = Assert.Single(lookbackResult.Forecasts.Where(f =>
            f.ModelName == "BaselineSeasonal" &&
            f.AnchorUtcTime == validationAnchor.AnchorUtcTime));

        Assert.Equal(20.0, defaultBaseline.PredictedTargets[0], 8); // mean(10, 30)
        Assert.Equal(30.0, lookbackBaseline.PredictedTargets[0], 8); // recent window only
    }

    [Fact]
    public void RunModels_BaselineLookbackWeeks_NonPositive_Throws()
    {
        var rows = BuildSyntheticPart3Rows(trainCount: 320, validationCount: 4);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            Part3Modeling.RunModels(rows, baselineOptions: new BaselineSeasonalOptions(LookbackWeeks: 0)));

        Assert.Contains("lookback", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RunModels_BaselineStepOneUsesNextTimestampKey_AcrossMidnightBoundary()
    {
        var trainRows = new List<Part2SupervisedRow>
        {
            // Tuesday 00:00 key mean is 42 and should be used for step 1.
            CreateOracleRow(new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), targetAtT: 40.0, temperature: 1.0, split: "Train"),
            CreateOracleRow(new DateTime(2024, 1, 9, 0, 0, 0, DateTimeKind.Utc), targetAtT: 44.0, temperature: 1.0, split: "Train"),
            // Distractor at anchor clock time (Monday 23:45) should not be used for step 1.
            CreateOracleRow(new DateTime(2024, 1, 1, 23, 45, 0, DateTimeKind.Utc), targetAtT: 999.0, temperature: 1.0, split: "Train")
        };

        var validationAnchor = CreateOracleRow(
            new DateTime(2024, 1, 1, 23, 45, 0, DateTimeKind.Utc),
            targetAtT: 50.0,
            temperature: 1.0,
            split: "Validation");

        var rows = trainRows.Concat([validationAnchor]).ToList();
        var result = Part3Modeling.RunModels(rows);

        var baseline = Assert.Single(result.Forecasts.Where(f =>
            f.ModelName == "BaselineSeasonal" &&
            f.AnchorUtcTime == validationAnchor.AnchorUtcTime &&
            f.Split == "Validation"));

        Assert.Equal(42.0, baseline.PredictedTargets[0], 8);
        Assert.NotEqual(999.0, baseline.PredictedTargets[0]);
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
    public void PredictWithRecursiveOracle_FeedsPriorPredictionsBackIntoLaterSteps()
    {
        var anchorTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var rows = new List<Part2SupervisedRow>
        {
            CreateOracleRow(anchorTime, targetAtT: 10.0, temperature: 1.0),
            CreateOracleRow(anchorTime.AddMinutes(15), targetAtT: 999.0, temperature: 2.0),
            CreateOracleRow(anchorTime.AddMinutes(30), targetAtT: 5000.0, temperature: 4.0),
            CreateOracleRow(anchorTime.AddMinutes(45), targetAtT: -1234.0, temperature: 8.0)
        };

        var result = Part3Modeling.PredictWithRecursiveOracle(
            rows[0],
            rows,
            snapshot => snapshot.TargetAtT + snapshot.Temperature);

        Assert.Equal(PipelineConstants.HorizonSteps, result.Predictions.Length);
        Assert.Equal(191, result.FallbackOrRecursiveSteps);

        // Future realized temperatures must not leak into the recursive loop.
        AssertPredictionPrefix(result.Predictions, 11.0, 12.0, 13.0, 14.0, 15.0, 16.0);
    }

    [Fact]
    public void PredictWithRecursiveOracle_FreezesExogenousValuesAtAnchorTime()
    {
        var anchorTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var rows = new List<Part2SupervisedRow>
        {
            CreateOracleRow(anchorTime, targetAtT: 10.0, temperature: 1.0),
            CreateOracleRow(anchorTime.AddMinutes(15), targetAtT: 20.0, temperature: 2.0),
            CreateOracleRow(anchorTime.AddMinutes(30), targetAtT: 30.0, temperature: 4.0),
            CreateOracleRow(anchorTime.AddMinutes(45), targetAtT: 40.0, temperature: 8.0)
        };

        var result = Part3Modeling.PredictWithRecursiveOracle(
            rows[0],
            rows,
            snapshot => snapshot.Temperature);

        Assert.Equal(PipelineConstants.HorizonSteps, result.Predictions.Length);
        Assert.Equal(191, result.FallbackOrRecursiveSteps);

        AssertPredictionPrefix(result.Predictions, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0);
    }

    [Fact]
    public void PredictWithRecursiveOracle_AllowsHolidayCalendarContextWithoutUsingFutureRealizedExogenousValues()
    {
        var anchorTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var rows = new List<Part2SupervisedRow>
        {
            CreateOracleRow(anchorTime, targetAtT: 10.0, temperature: 1.0, split: "Validation"),
            CreateOracleRow(anchorTime.AddMinutes(15), targetAtT: 20.0, temperature: 99.0, split: "Validation") with { IsHoliday = true },
            CreateOracleRow(anchorTime.AddMinutes(30), targetAtT: 30.0, temperature: 77.0, split: "Validation"),
            CreateOracleRow(anchorTime.AddMinutes(45), targetAtT: 40.0, temperature: 55.0, split: "Validation")
        };

        var result = Part3Modeling.PredictWithRecursiveOracle(
            rows[0],
            rows,
            snapshot => snapshot.IsHoliday ? 100.0 + snapshot.Temperature : snapshot.Temperature);

        AssertPredictionPrefix(result.Predictions, 1.0, 101.0, 1.0, 1.0);
    }

    [Fact]
    public void PredictWithRecursiveOracle_RollsCalendarTimeFeaturesForwardAcrossSteps()
    {
        var anchorTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var rows = new List<Part2SupervisedRow>
        {
            CreateOracleRow(anchorTime, targetAtT: 10.0, temperature: 1.0, split: "Validation"),
            CreateOracleRow(anchorTime.AddMinutes(15), targetAtT: 20.0, temperature: 99.0, split: "Validation"),
            CreateOracleRow(anchorTime.AddMinutes(30), targetAtT: 30.0, temperature: 77.0, split: "Validation"),
            CreateOracleRow(anchorTime.AddMinutes(45), targetAtT: 40.0, temperature: 55.0, split: "Validation")
        };

        var result = Part3Modeling.PredictWithRecursiveOracle(
            rows[0],
            rows,
            snapshot => (snapshot.DayOfWeek * 10000.0) + (snapshot.HourOfDay * 100.0) + snapshot.MinuteOfHour);

        AssertPredictionPrefix(result.Predictions, 10000.0, 10015.0, 10030.0, 10045.0);
    }

    [Fact]
    public void PredictWithRecursiveOracle_SortsHistoryAndUsesLastDuplicateAtSameTimestamp()
    {
        var anchorTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var anchor = CreateOracleRow(anchorTime, targetAtT: 10.0, temperature: 1.0, split: "Validation");

        var rows = new List<Part2SupervisedRow>
        {
            // Intentionally unsorted: history is expected to be sorted before recursive reads.
            CreateOracleRow(anchorTime.AddMinutes(15), targetAtT: 999.0, temperature: 2.0, split: "Validation"),
            anchor,
            CreateOracleRow(anchorTime.AddMinutes(-15), targetAtT: 5.0, temperature: 3.0, split: "Validation"),
            // Duplicate timestamp for anchor; last duplicate should win in row/history dictionaries.
            CreateOracleRow(anchorTime, targetAtT: 20.0, temperature: 4.0, split: "Validation") with { IsHoliday = true }
        };

        var result = Part3Modeling.PredictWithRecursiveOracle(
            anchor,
            rows,
            snapshot => snapshot.TargetAtT + (snapshot.IsHoliday ? 1000.0 : 0.0));

        // Step 1 reads the deduplicated anchor-time value (20) and holiday context from the last duplicate.
        // Step 2 then recurses on step 1 prediction fed back at t+1.
        AssertPredictionPrefix(result.Predictions, 1020.0, 1020.0, 1020.0);
    }

    [Fact]
    public void PredictWithRecursiveOracle_TargetLag192AdvancesAcrossHistoryTimeline()
    {
        var anchorTime = new DateTime(2024, 1, 3, 0, 0, 0, DateTimeKind.Utc);
        var start = anchorTime.AddMinutes(-FeatureConfig.TargetLag192 * PipelineConstants.MinutesPerStep);
        var rows = new List<Part2SupervisedRow>(FeatureConfig.TargetLag192 + 1);

        for (var i = 0; i <= FeatureConfig.TargetLag192; i++)
        {
            var ts = start.AddMinutes(i * PipelineConstants.MinutesPerStep);
            rows.Add(CreateOracleRow(ts, targetAtT: 1000.0 + i, temperature: 1.0, split: "Validation"));
        }

        var anchor = rows[^1];
        var result = Part3Modeling.PredictWithRecursiveOracle(
            anchor,
            rows,
            snapshot => snapshot.TargetLag192);

        // At step k, lag-192 should walk forward one slot in historical timeline.
        AssertPredictionPrefix(result.Predictions, 1000.0, 1001.0, 1002.0, 1003.0, 1004.0);
    }

    [Fact]
    public void PredictWithRecursiveOracle_FiniteGuardPatternPreventsNaNPropagation()
    {
        var anchorTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var rows = new List<Part2SupervisedRow>
        {
            CreateOracleRow(anchorTime, targetAtT: 10.0, temperature: 1.0),
            CreateOracleRow(anchorTime.AddMinutes(15), targetAtT: 20.0, temperature: 2.0),
            CreateOracleRow(anchorTime.AddMinutes(30), targetAtT: 30.0, temperature: 3.0)
        };

        var result = Part3Modeling.PredictWithRecursiveOracle(
            rows[0],
            rows,
            snapshot =>
            {
                var nonFiniteScore = double.NaN;
                // Mirror production scorer guard: fallback to current recursive target when score is invalid.
                return double.IsFinite(nonFiniteScore) ? nonFiniteScore : snapshot.TargetAtT;
            });

        AssertPredictionPrefix(result.Predictions, 10.0, 10.0, 10.0, 10.0);
    }

    [Fact]
    public void RunModels_SummaryModelsMatchForecastModelSet()
    {
        var rows = BuildSyntheticPart3Rows(trainCount: 120, validationCount: 8);

        var result = Part3Modeling.RunModels(rows);

        var modelNamesFromSummary = result.Summary.Models
            .Select(model => model.ModelName)
            .ToHashSet(StringComparer.Ordinal);
        var modelNamesFromForecasts = result.Forecasts
            .Select(forecast => forecast.ModelName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(modelNamesFromSummary, modelNamesFromForecasts);

        foreach (var model in result.Summary.Models)
        {
            var perModelForecastCount = result.Forecasts.Count(forecast =>
                string.Equals(forecast.ModelName, model.ModelName, StringComparison.Ordinal));
            Assert.Equal(model.AnchorsForecasted, perModelForecastCount);
        }
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

    [Fact]
    public void RunModels_PfiIsNullByDefault()
    {
        var rows = BuildSyntheticPart3Rows(trainCount: 320, validationCount: 4);

        var result = Part3Modeling.RunModels(rows);

        Assert.Null(result.FeatureImportance);
    }

    [Fact]
    public void Pfi_ResultHas17FeaturesWithFiniteMetrics()
    {
        var rows = BuildSyntheticPart3Rows(trainCount: 320, validationCount: 6);

        var result = Part3Modeling.RunModels(rows, enablePfi: true);

        Assert.NotNull(result.FeatureImportance);
        Assert.Equal(17, result.FeatureImportance.Features.Count);
        Assert.Equal(10, result.FeatureImportance.PermutationCount);
        Assert.Equal(6, result.FeatureImportance.EvaluationRowCount);
        Assert.Equal(1, result.FeatureImportance.HorizonStep);

        Assert.All(result.FeatureImportance.Features, feature =>
        {
            Assert.False(string.IsNullOrEmpty(feature.FeatureName));
            Assert.True(double.IsFinite(feature.MaeDelta));
            Assert.True(double.IsFinite(feature.MaeDeltaStdDev));
            Assert.True(double.IsFinite(feature.RmseDelta));
            Assert.True(double.IsFinite(feature.RmseDeltaStdDev));
            Assert.True(double.IsFinite(feature.R2Delta));
            Assert.True(double.IsFinite(feature.R2DeltaStdDev));
        });
    }

    [Fact]
    public void Pfi_FeaturesArRankedByAbsoluteMAEDeltaDescending()
    {
        var rows = BuildSyntheticPart3Rows(trainCount: 320, validationCount: 6);

        var result = Part3Modeling.RunModels(rows, enablePfi: true);

        Assert.NotNull(result.FeatureImportance);
        var features = result.FeatureImportance.Features;

        for (var i = 0; i < features.Count; i++)
        {
            Assert.Equal(i + 1, features[i].Rank);
        }

        for (var i = 1; i < features.Count; i++)
        {
            Assert.True(
                Math.Abs(features[i - 1].MaeDelta) >= Math.Abs(features[i].MaeDelta),
                $"Feature at rank {features[i - 1].Rank} (|MAE|={Math.Abs(features[i - 1].MaeDelta)}) should have >= |MAE| than rank {features[i].Rank} (|MAE|={Math.Abs(features[i].MaeDelta)})");
        }
    }

    [Fact]
    public void Pfi_UsesRequestedHorizonStep()
    {
        var rows = BuildSyntheticPart3Rows(trainCount: 320, validationCount: 6);

        var result = Part3Modeling.RunModels(rows, enablePfi: true, pfiHorizonStep: 96);

        Assert.NotNull(result.FeatureImportance);
        Assert.Equal(96, result.FeatureImportance.HorizonStep);
        Assert.Equal(17, result.FeatureImportance.Features.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(193)]
    public void RunModels_InvalidPfiHorizon_ThrowsArgumentOutOfRangeException(int pfiHorizonStep)
    {
        var rows = BuildSyntheticPart3Rows(trainCount: 320, validationCount: 4);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Part3Modeling.RunModels(rows, enablePfi: true, pfiHorizonStep: pfiHorizonStep));
    }

    [Fact]
    public void Pfi_IsDeterministicAcrossRuns()
    {
        var rows = BuildSyntheticPart3Rows(trainCount: 320, validationCount: 4);

        var result1 = Part3Modeling.RunModels(rows, enablePfi: true);
        var result2 = Part3Modeling.RunModels(rows, enablePfi: true);

        Assert.NotNull(result1.FeatureImportance);
        Assert.NotNull(result2.FeatureImportance);

        for (var i = 0; i < result1.FeatureImportance.Features.Count; i++)
        {
            var f1 = result1.FeatureImportance.Features[i];
            var f2 = result2.FeatureImportance.Features[i];
            Assert.Equal(f1.FeatureName, f2.FeatureName);
            Assert.Equal(f1.Rank, f2.Rank);
            Assert.Equal(f1.MaeDelta, f2.MaeDelta, 12);
            Assert.Equal(f1.RmseDelta, f2.RmseDelta, 12);
            Assert.Equal(f1.R2Delta, f2.R2Delta, 12);
        }
    }

    [Fact]
    public void Pfi_FeatureNamesMatchExpectedListAndVectorTypeLength()
    {
        var expectedNames = new[]
        {
            "Temperature", "Windspeed", "SolarIrradiation",
            "HourOfDay", "MinuteOfHour", "DayOfWeek", "IsHoliday",
            "HourSin", "HourCos", "WeekdaySin", "WeekdayCos",
            "TargetLag192", "TargetLag672", "TargetLag192Mean16", "TargetLag192Std16",
            "TargetLag192Mean96", "TargetLag192Std96"
        };

        Assert.Equal(expectedNames.Length, Part3Modeling.FeatureNames.Length);
        Assert.Equal(expectedNames, Part3Modeling.FeatureNames);

        // Verify expected schema width stays in sync with the centralized feature definitions.
        Assert.Equal(expectedNames.Length, Part3Modeling.FeatureNames.Length);
    }

    [Fact]
    public void ResolvePfiFeatureName_PrefersPfiStringKeyOverFallbackIndex()
    {
        var resolved = Part3Modeling.ResolvePfiFeatureName("TargetLag192Std96", fallbackIndex: 0);

        Assert.Equal("TargetLag192Std96", resolved);
    }

    [Fact]
    public void ResolvePfiFeatureName_UsesNumericKeyAsFeatureIndex()
    {
        var resolved = Part3Modeling.ResolvePfiFeatureName(2, fallbackIndex: 0);

        Assert.Equal(Part3Modeling.FeatureNames[2], resolved);
    }

    [Fact]
    public void ResolvePfiFeatureName_UsesUIntKeyAsFeatureIndex()
    {
        var resolved = Part3Modeling.ResolvePfiFeatureName((uint)3, fallbackIndex: 0);

        Assert.Equal(Part3Modeling.FeatureNames[3], resolved);
    }

    [Fact]
    public void ResolvePfiFeatureName_UsesLongKeyAsFeatureIndex()
    {
        var resolved = Part3Modeling.ResolvePfiFeatureName((long)4, fallbackIndex: 0);

        Assert.Equal(Part3Modeling.FeatureNames[4], resolved);
    }

    [Fact]
    public void Pfi_WriteFeatureImportanceCsv_WritesExpectedFormat()
    {
        var rows = BuildSyntheticPart3Rows(trainCount: 320, validationCount: 4);
        var result = Part3Modeling.RunModels(rows, enablePfi: true);

        Assert.NotNull(result.FeatureImportance);

        var outputDir = Path.Combine(Path.GetTempPath(), $"pfi-csv-test-{Guid.NewGuid():N}");
        var csvPath = Path.Combine(outputDir, "feature_importance.csv");

        try
        {
            Part3Modeling.WriteFeatureImportanceCsv(result.FeatureImportance, csvPath);

            Assert.True(File.Exists(csvPath));
            var lines = File.ReadAllLines(csvPath);
            Assert.Equal(18, lines.Length); // header + 17 features

            Assert.Equal("Rank;FeatureName;MaeDelta;MaeDeltaStdDev;RmseDelta;RmseDeltaStdDev;R2Delta;R2DeltaStdDev", lines[0]);

            // Verify each data row has 8 columns
            for (var i = 1; i < lines.Length; i++)
            {
                var parts = lines[i].Split(';');
                Assert.Equal(8, parts.Length);
                Assert.True(int.TryParse(parts[0], out _), $"Rank column should be integer at line {i}");
                Assert.False(string.IsNullOrEmpty(parts[1]), $"FeatureName should not be empty at line {i}");
            }
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, true);
            }
        }
    }

    private static List<Part2SupervisedRow> BuildSyntheticPart3Rows(int trainCount, int validationCount)
    {
        var rows = new List<Part2SupervisedRow>(trainCount + validationCount);
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

            rows.Add(new Part2SupervisedRow(
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
                target - 2.5,
                0.3,
                target - 3.0,
                0.5,
                split,
                horizon));
        }

        return rows;
    }

    private static Part2SupervisedRow CreateOracleRow(
        DateTime anchorUtcTime,
        double targetAtT,
        double temperature,
        double windspeed = 0.0,
        double solarIrradiation = 0.0,
        string split = "Train")
    {
        var hour = anchorUtcTime.Hour;
        var minute = anchorUtcTime.Minute;
        var dayOfWeek = (int)anchorUtcTime.DayOfWeek;
        var hourAngle = 2d * Math.PI * (hour / 24d);
        var weekdayAngle = 2d * Math.PI * (dayOfWeek / 7d);

        return new Part2SupervisedRow(
            anchorUtcTime,
            targetAtT,
            temperature,
            windspeed,
            solarIrradiation,
            hour,
            minute,
            dayOfWeek,
            false,
            Math.Sin(hourAngle),
            Math.Cos(hourAngle),
            Math.Sin(weekdayAngle),
            Math.Cos(weekdayAngle),
            targetAtT - 1.0,
            targetAtT - 2.0,
            targetAtT - 0.5,
            0.2,
            targetAtT - 1.0,
            0.4,
            targetAtT - 2.5,
            0.3,
            targetAtT - 3.0,
            0.5,
            split,
            Enumerable.Repeat(targetAtT, PipelineConstants.HorizonSteps).ToArray());
    }

    private static void AssertPredictionPrefix(IReadOnlyList<double> predictions, params double[] expectedPrefix)
    {
        Assert.True(predictions.Count >= expectedPrefix.Length);

        for (var index = 0; index < expectedPrefix.Length; index++)
        {
            Assert.Equal(expectedPrefix[index], predictions[index], 8);
        }
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
            "TargetLag192Mean16",
            "TargetLag192Std16",
            "TargetLag192Mean96",
            "TargetLag192Std96",
            "TargetLag672Mean16",
            "TargetLag672Std16",
            "TargetLag672Mean96",
            "TargetLag672Std96",
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
            "98",
            "0.3",
            "97",
            "0.4",
            "Train"
        };

        cells.AddRange(Enumerable.Range(1, 192).Select(index => (100 + index * 0.01).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return cells.ToArray();
    }
}