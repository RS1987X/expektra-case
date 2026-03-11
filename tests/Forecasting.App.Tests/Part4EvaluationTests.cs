using System.Globalization;
using Forecasting.App;

namespace Forecasting.App.Tests;

public class Part4EvaluationTests
{
    [Fact]
    public void RunEvaluation_ComputesExpectedMicroAveragedMetrics()
    {
        var anchor = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var part2Path = CreatePart2Csv([BuildPart2Row(anchor, "Validation", BuildActualTargets(10d))]);
        var predictionsPath = CreatePredictionsCsv([BuildPredictionRow(anchor, "Validation", "FastTreeRecursive", BuildPredictions(10d, step1: 12d, step2: 8d))]);

        try
        {
            var result = Part4Evaluation.RunEvaluation(part2Path, predictionsPath);
            var metric = Assert.Single(result.Metrics);

            Assert.Equal("FastTreeRecursive", metric.ModelName);
            Assert.Equal(192, metric.EvaluatedPoints);
            Assert.Equal(192, metric.MapeEvaluatedPoints);
            Assert.Equal(0, metric.ZeroActualExcludedPoints);

            var expectedMae = 4d / 192d;
            var expectedRmse = Math.Sqrt(8d / 192d);
            var expectedMape = 40d / 192d;

            Assert.Equal(expectedMae, metric.Mae, 10);
            Assert.Equal(expectedRmse, metric.Rmse, 10);
            Assert.Equal(expectedMape, metric.Mape, 10);

            Assert.True(result.SamplePoints.Count > 0);
            Assert.All(result.SamplePoints, point => Assert.Equal(anchor, point.AnchorUtcTime));
        }
        finally
        {
            File.Delete(part2Path);
            File.Delete(predictionsPath);
        }
    }

    [Fact]
    public void RunEvaluation_ExcludesZeroActualsFromMapeAndTracksCounts()
    {
        var anchor = new DateTime(2024, 2, 2, 0, 0, 0, DateTimeKind.Utc);
        var actualTargets = BuildActualTargets(0d);
        actualTargets[0] = 10d;

        var predictedTargets = BuildPredictions(3d);
        predictedTargets[0] = 12d;

        var part2Path = CreatePart2Csv([BuildPart2Row(anchor, "Validation", actualTargets)]);
        var predictionsPath = CreatePredictionsCsv([BuildPredictionRow(anchor, "Validation", "FastTreeRecursive", predictedTargets)]);

        try
        {
            var result = Part4Evaluation.RunEvaluation(part2Path, predictionsPath);
            var metric = Assert.Single(result.Metrics);

            Assert.Equal(192, metric.EvaluatedPoints);
            Assert.Equal(1, metric.MapeEvaluatedPoints);
            Assert.Equal(191, metric.ZeroActualExcludedPoints);
            Assert.Equal(20d, metric.Mape, 10);
        }
        finally
        {
            File.Delete(part2Path);
            File.Delete(predictionsPath);
        }
    }

    [Fact]
    public void RunEvaluation_DuplicatePredictionKeys_ThrowsInvalidOperationException()
    {
        var anchor = new DateTime(2024, 2, 3, 0, 0, 0, DateTimeKind.Utc);
        var part2Path = CreatePart2Csv([BuildPart2Row(anchor, "Validation", BuildActualTargets(10d))]);
        var duplicateRow = BuildPredictionRow(anchor, "Validation", "FastTreeRecursive", BuildPredictions(10d));
        var predictionsPath = CreatePredictionsCsv([duplicateRow, duplicateRow]);

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Part4Evaluation.RunEvaluation(part2Path, predictionsPath));
            Assert.Contains("Duplicate prediction key", ex.Message);
        }
        finally
        {
            File.Delete(part2Path);
            File.Delete(predictionsPath);
        }
    }

    [Fact]
    public void RunEvaluation_ModelWithNoEvaluablePoints_ThrowsInvalidOperationException()
    {
        var actualAnchor = new DateTime(2024, 2, 5, 0, 0, 0, DateTimeKind.Utc);
        var missingAnchor = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var part2Path = CreatePart2Csv([BuildPart2Row(actualAnchor, "Validation", BuildActualTargets(10d))]);
        var predictionsPath = CreatePredictionsCsv([BuildPredictionRow(missingAnchor, "Validation", "FastTreeRecursive", BuildPredictions(10d))]);

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Part4Evaluation.RunEvaluation(part2Path, predictionsPath));
            Assert.Contains("has no evaluable prediction points", ex.Message);
        }
        finally
        {
            File.Delete(part2Path);
            File.Delete(predictionsPath);
        }
    }

    [Fact]
    public void RunEvaluation_NonFinitePredictionValue_ThrowsFormatException()
    {
        var anchor = new DateTime(2024, 2, 6, 0, 0, 0, DateTimeKind.Utc);
        var part2Path = CreatePart2Csv([BuildPart2Row(anchor, "Validation", BuildActualTargets(10d))]);

        var predictions = BuildPredictions(10d);
        predictions[0] = double.NaN;
        var predictionsPath = CreatePredictionsCsv([BuildPredictionRow(anchor, "Validation", "FastTreeRecursive", predictions)]);

        try
        {
            var ex = Assert.Throws<FormatException>(() => Part4Evaluation.RunEvaluation(part2Path, predictionsPath));
            Assert.Contains("non-finite", ex.Message);
        }
        finally
        {
            File.Delete(part2Path);
            File.Delete(predictionsPath);
        }
    }

    [Fact]
    public void RunEvaluation_SampleUsesEarliestAnchorWithMatchedActuals()
    {
        var matchedAnchor = new DateTime(2024, 2, 7, 0, 0, 0, DateTimeKind.Utc);
        var unmatchedEarlierAnchor = matchedAnchor.AddMinutes(-15);

        var part2Path = CreatePart2Csv([BuildPart2Row(matchedAnchor, "Validation", BuildActualTargets(10d))]);
        var predictionsPath = CreatePredictionsCsv(
        [
            BuildPredictionRow(unmatchedEarlierAnchor, "Validation", "FastTreeRecursive", BuildPredictions(9d)),
            BuildPredictionRow(matchedAnchor, "Validation", "FastTreeRecursive", BuildPredictions(10d))
        ]);

        try
        {
            var result = Part4Evaluation.RunEvaluation(part2Path, predictionsPath);
            Assert.NotEmpty(result.SamplePoints);
            Assert.All(result.SamplePoints, sample => Assert.Equal(matchedAnchor, sample.AnchorUtcTime));
        }
        finally
        {
            File.Delete(part2Path);
            File.Delete(predictionsPath);
        }
    }

    [Fact]
    public void WriteMetricsAndSampleCsv_WritesExpectedHeaders()
    {
        var anchor = new DateTime(2024, 2, 4, 0, 0, 0, DateTimeKind.Utc);
        var part2Path = CreatePart2Csv([BuildPart2Row(anchor, "Validation", BuildActualTargets(10d))]);
        var predictionsPath = CreatePredictionsCsv([BuildPredictionRow(anchor, "Validation", "FastTreeRecursive", BuildPredictions(10d))]);

        var outputDir = Path.Combine(Path.GetTempPath(), $"part4-tests-{Guid.NewGuid():N}");
        var metricsPath = Path.Combine(outputDir, "part4_metrics.csv");
        var samplePath = Path.Combine(outputDir, "part4_sample.csv");

        try
        {
            var result = Part4Evaluation.RunEvaluation(part2Path, predictionsPath);
            Part4Evaluation.WriteMetricsCsv(result, metricsPath);
            Part4Evaluation.WriteSampleCsv(result, samplePath);

            Assert.True(File.Exists(metricsPath));
            Assert.True(File.Exists(samplePath));

            var metricsHeader = File.ReadLines(metricsPath).First();
            Assert.Contains("ModelName", metricsHeader);
            Assert.Contains("MAE", metricsHeader);

            var sampleHeader = File.ReadLines(samplePath).First();
            Assert.Contains("ForecastUtcTime", sampleHeader);
            Assert.Contains("HorizonStep", sampleHeader);
        }
        finally
        {
            File.Delete(part2Path);
            File.Delete(predictionsPath);
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, true);
            }
        }
    }

    [Fact]
    public void WriteValidationPlotArtifactsByModel_WritesCsvAndSvgForBothModels()
    {
        var baseAnchor = new DateTime(2024, 2, 8, 0, 0, 0, DateTimeKind.Utc);
        var anchors = Enumerable.Range(0, 8)
            .Select(index => baseAnchor.AddMinutes(index * PipelineConstants.MinutesPerStep))
            .ToArray();

        var part2Rows = anchors
            .Select(anchor => BuildPart2Row(anchor, "Validation", BuildActualTargets(100d)))
            .ToArray();

        var forecastRows = anchors
            .Select(anchor => BuildPredictionRow(anchor, "Validation", "FastTreeRecursive", BuildPredictions(105d)))
            .Concat(anchors.Select(anchor => BuildPredictionRow(anchor, "Validation", "BaselineSeasonal", BuildPredictions(98d))))
            .ToArray();

        var part2Path = CreatePart2Csv(part2Rows);
        var predictionsPath = CreatePredictionsCsv(forecastRows);

        var outputDir = Path.Combine(Path.GetTempPath(), $"part4-plot-tests-{Guid.NewGuid():N}");

        try
        {
            var result = Part4Evaluation.RunEvaluation(part2Path, predictionsPath);
            var written = Part4Evaluation.WriteValidationPlotArtifactsByModel(result, outputDir);

            Assert.Equal(2, written.Count);

            var fastTreePaths = Assert.Single(written.Where(path => path.ModelName == "FastTreeRecursive"));
            var baselinePaths = Assert.Single(written.Where(path => path.ModelName == "BaselineSeasonal"));

            Assert.True(File.Exists(fastTreePaths.CsvPath));
            Assert.True(File.Exists(fastTreePaths.SvgPath));
            Assert.True(File.Exists(baselinePaths.CsvPath));
            Assert.True(File.Exists(baselinePaths.SvgPath));

            // Each CSV should have 1 header + 8 data rows (one per anchor at fixed horizon step 92).
            var fastTreeCsvLines = File.ReadAllLines(fastTreePaths.CsvPath);
            Assert.Equal(9, fastTreeCsvLines.Length);
            Assert.Contains("HorizonStep", fastTreeCsvLines[0]);
            Assert.All(fastTreeCsvLines.Skip(1), line => Assert.Contains(";92;", line));

            var baselineCsvLines = File.ReadAllLines(baselinePaths.CsvPath);
            Assert.Equal(9, baselineCsvLines.Length);
            Assert.Contains("HorizonStep", baselineCsvLines[0]);
            Assert.All(baselineCsvLines.Skip(1), line => Assert.Contains(";92;", line));

            var fastTreeSvgText = File.ReadAllText(fastTreePaths.SvgPath);
            Assert.Contains("FastTreeRecursive Predicted vs Actual at fixed horizon t+92", fastTreeSvgText);
            Assert.Contains("48h window", fastTreeSvgText);

            var baselineSvgText = File.ReadAllText(baselinePaths.SvgPath);
            Assert.Contains("BaselineSeasonal Predicted vs Actual at fixed horizon t+92", baselineSvgText);
            Assert.Contains("48h window", baselineSvgText);
        }
        finally
        {
            File.Delete(part2Path);
            File.Delete(predictionsPath);
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, true);
            }
        }
    }

    private static string CreatePart2Csv(IReadOnlyList<string> rows)
    {
        var path = Path.Combine(Path.GetTempPath(), $"part4-part2-{Guid.NewGuid():N}.csv");
        var header = BuildPart2Header();
        File.WriteAllText(path, string.Join(Environment.NewLine, new[] { header }.Concat(rows)));
        return path;
    }

    private static string CreatePredictionsCsv(IReadOnlyList<string> rows)
    {
        var path = Path.Combine(Path.GetTempPath(), $"part4-pred-{Guid.NewGuid():N}.csv");
        var header = BuildPredictionsHeader();
        File.WriteAllText(path, string.Join(Environment.NewLine, new[] { header }.Concat(rows)));
        return path;
    }

    private static string BuildPart2Header()
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

        var horizon = Enumerable.Range(1, 192).Select(step => $"Target_tPlus{step}");
        return string.Join(';', firstColumns.Concat(horizon));
    }

    private static string BuildPredictionsHeader()
    {
        var predictedColumns = Enumerable.Range(1, 192).Select(step => $"Pred_tPlus{step}");
        var actualColumns = Enumerable.Range(1, 192).Select(step => $"Actual_tPlus{step}");
        return string.Join(';', new[] { "anchorUtcTime", "Split", "Model", "FallbackOrRecursiveSteps" }
            .Concat(predictedColumns)
            .Concat(actualColumns));
    }

    private static string BuildPart2Row(DateTime anchorUtc, string split, IReadOnlyList<double> actualTargets)
    {
        var baseCells = new[]
        {
            anchorUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            "10",
            "5",
            "1",
            "0",
            "0",
            "0",
            "1",
            "false",
            "0",
            "1",
            "0",
            "1",
            "9",
            "8",
            "10",
            "0.1",
            "10",
            "0.2",
            "8",
            "0.3",
            "7",
            "0.4",
            split
        };

        var horizonCells = actualTargets.Select(value => value.ToString(CultureInfo.InvariantCulture));
        return string.Join(';', baseCells.Concat(horizonCells));
    }

    private static string BuildPredictionRow(DateTime anchorUtc, string split, string modelName, IReadOnlyList<double> predictedTargets)
    {
        var prefix = new[]
        {
            anchorUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            split,
            modelName,
            "0"
        };

        var predictedCells = predictedTargets.Select(value => value.ToString("R", CultureInfo.InvariantCulture));
        var actualCells = Enumerable.Repeat("0", 192);
        return string.Join(';', prefix.Concat(predictedCells).Concat(actualCells));
    }

    private static double[] BuildActualTargets(double value)
    {
        return Enumerable.Repeat(value, 192).ToArray();
    }

    private static double[] BuildPredictions(double value, double? step1 = null, double? step2 = null)
    {
        var predictions = Enumerable.Repeat(value, 192).ToArray();
        if (step1.HasValue)
        {
            predictions[0] = step1.Value;
        }

        if (step2.HasValue)
        {
            predictions[1] = step2.Value;
        }

        return predictions;
    }
}