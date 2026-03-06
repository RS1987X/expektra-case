using System.Globalization;
using Forecasting.App;

namespace Forecasting.App.Tests;

public class PartDiagnosticsTests
{
    [Fact]
    public void RunDiagnostics_ComputesResidualAndBucketSummaries()
    {
        var trainAnchor = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var validationAnchor = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        var part2Path = CreatePart2Csv(
        [
            BuildPart2Row(trainAnchor, "Train", BuildActualTargets(8d)),
            BuildPart2Row(validationAnchor, "Validation", BuildActualTargets(10d))
        ]);

        var modelA = BuildPredictions(10d, step1: 12d, step2: 8d);
        var modelB = BuildPredictions(9d);
        var predictionsPath = CreatePredictionsCsv(
        [
            BuildPredictionRow(validationAnchor, "Validation", "FastTreeRecursive", modelA),
            BuildPredictionRow(validationAnchor, "Validation", "BaselineSeasonal", modelB)
        ]);

        try
        {
            var result = PartDiagnostics.RunDiagnostics(part2Path, predictionsPath);

            Assert.Equal(2, result.PreModelSummaries.Count);
            var residuals = result.ResidualSummaries.ToDictionary(summary => summary.ModelName, StringComparer.Ordinal);
            Assert.Equal(2, residuals.Count);

            Assert.True(residuals.TryGetValue("FastTreeRecursive", out var fastTree));
            Assert.Equal(192, fastTree!.EvaluatedPoints);
            Assert.Equal(0d, fastTree.MeanError, 10);

            Assert.True(residuals.TryGetValue("BaselineSeasonal", out var baseline));
            Assert.Equal(100d, baseline!.UnderPredictionRate, 10);

            var baselineBucket1 = Assert.Single(result.HorizonBucketSummaries.Where(summary =>
                summary.ModelName == "BaselineSeasonal" && summary.HorizonStart == 1));
            Assert.Equal(24, baselineBucket1.EvaluatedPoints);
            Assert.Equal(-1d, baselineBucket1.MeanError, 10);

            Assert.NotEmpty(result.SamplePoints);
        }
        finally
        {
            File.Delete(part2Path);
            File.Delete(predictionsPath);
        }
    }

    [Fact]
    public void RunDiagnostics_ReportsCadenceGapsInSplitSummary()
    {
        var trainAnchor1 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var trainAnchor2 = trainAnchor1.AddMinutes(45);
        var validationAnchor = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        var part2Path = CreatePart2Csv(
        [
            BuildPart2Row(trainAnchor1, "Train", BuildActualTargets(8d)),
            BuildPart2Row(trainAnchor2, "Train", BuildActualTargets(8d)),
            BuildPart2Row(validationAnchor, "Validation", BuildActualTargets(10d))
        ]);

        var predictionsPath = CreatePredictionsCsv([BuildPredictionRow(validationAnchor, "Validation", "FastTreeRecursive", BuildPredictions(10d))]);

        try
        {
            var result = PartDiagnostics.RunDiagnostics(part2Path, predictionsPath);
            var trainCadence = Assert.Single(result.CadenceSummaries.Where(summary => summary.Split == "Train"));

            Assert.Equal(2, trainCadence.AnchorCount);
            Assert.Equal(1, trainCadence.IrregularIntervalCount);
            Assert.Equal(2, trainCadence.MissingStepCount);
        }
        finally
        {
            File.Delete(part2Path);
            File.Delete(predictionsPath);
        }
    }

    [Fact]
    public void WriteArtifacts_CreatesExpectedDiagnosticsFiles()
    {
        var trainAnchor = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var validationAnchor = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        var part2Path = CreatePart2Csv(
        [
            BuildPart2Row(trainAnchor, "Train", BuildActualTargets(8d)),
            BuildPart2Row(validationAnchor, "Validation", BuildActualTargets(10d))
        ]);

        var predictionsPath = CreatePredictionsCsv([BuildPredictionRow(validationAnchor, "Validation", "FastTreeRecursive", BuildPredictions(10d))]);
        var outputDir = Path.Combine(Path.GetTempPath(), $"diagnostics-tests-{Guid.NewGuid():N}");

        try
        {
            var result = PartDiagnostics.RunDiagnostics(part2Path, predictionsPath);
            PartDiagnostics.WriteArtifacts(result, outputDir);

            var expectedFiles = new[]
            {
                "premodel_summary.csv",
                "premodel_cadence.csv",
                "postmodel_residual_summary.csv",
                "postmodel_bias_by_horizon_bucket.csv",
                "postmodel_sample_points.csv",
                "target_over_time.svg",
                "diagnostics_report.html"
            };

            foreach (var file in expectedFiles)
            {
                Assert.True(File.Exists(Path.Combine(outputDir, file)), $"Expected artifact '{file}' was not created.");
            }

            var html = File.ReadAllText(Path.Combine(outputDir, "diagnostics_report.html"));
            Assert.Contains("<svg", html);
            Assert.Contains("Target over time (TargetAtT)", html);
            Assert.Contains("Time (UTC)", html);
            Assert.Contains("Target value", html);
            Assert.Contains("Predicted vs actual sampled anchors", html);

            var targetSvg = File.ReadAllText(Path.Combine(outputDir, "target_over_time.svg"));
            Assert.Contains("Time (UTC)", targetSvg);
            Assert.Contains("Target value", targetSvg);
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
        var path = Path.Combine(Path.GetTempPath(), $"diag-part2-{Guid.NewGuid():N}.csv");
        var header = BuildPart2Header();
        File.WriteAllText(path, string.Join(Environment.NewLine, new[] { header }.Concat(rows)));
        return path;
    }

    private static string CreatePredictionsCsv(IReadOnlyList<string> rows)
    {
        var path = Path.Combine(Path.GetTempPath(), $"diag-pred-{Guid.NewGuid():N}.csv");
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
            "TargetMean16",
            "TargetStd16",
            "TargetMean96",
            "TargetStd96",
            "Split"
        };

        var horizon = Enumerable.Range(1, 192).Select(step => $"Target_tPlus{step}");
        return string.Join(';', firstColumns.Concat(horizon));
    }

    private static string BuildPredictionsHeader()
    {
        var predictedColumns = Enumerable.Range(1, 192).Select(step => $"Pred_tPlus{step}");
        var actualColumns = Enumerable.Range(1, 192).Select(step => $"Actual_tPlus{step}");
        return string.Join(';', new[] { "anchorUtcTime", "Split", "Model", "ExogenousFallbackSteps" }
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
