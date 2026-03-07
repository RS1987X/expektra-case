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
            BuildPredictionRow(trainAnchor, "Train", "FastTreeRecursive", BuildPredictions(7d)),
            BuildPredictionRow(trainAnchor, "Train", "BaselineSeasonal", BuildPredictions(9d)),
            BuildPredictionRow(validationAnchor, "Validation", "FastTreeRecursive", modelA),
            BuildPredictionRow(validationAnchor, "Validation", "BaselineSeasonal", modelB)
        ]);

        try
        {
            var result = PartDiagnostics.RunDiagnostics(part2Path, predictionsPath);

            Assert.Equal(2, result.PreModelSummaries.Count);
            Assert.Equal(4, result.ResidualSummaries.Count);

            var fastTreeValidation = Assert.Single(result.ResidualSummaries.Where(summary =>
                summary.ModelName == "FastTreeRecursive" && summary.Split == "Validation"));
            Assert.Equal(192, fastTreeValidation.EvaluatedPoints);
            Assert.Equal(0d, fastTreeValidation.MeanError, 10);

            var baselineValidation = Assert.Single(result.ResidualSummaries.Where(summary =>
                summary.ModelName == "BaselineSeasonal" && summary.Split == "Validation"));
            Assert.Equal(100d, baselineValidation.UnderPredictionRate, 10);

            var fastTreeTrain = Assert.Single(result.ResidualSummaries.Where(summary =>
                summary.ModelName == "FastTreeRecursive" && summary.Split == "Train"));
            Assert.Equal(-1d, fastTreeTrain.MeanError, 10);

            var baselineTrain = Assert.Single(result.ResidualSummaries.Where(summary =>
                summary.ModelName == "BaselineSeasonal" && summary.Split == "Train"));
            Assert.Equal(1d, baselineTrain.MeanError, 10);
            Assert.Equal(100d, baselineTrain.OverPredictionRate, 10);

            var baselineBucket1 = Assert.Single(result.HorizonBucketSummaries.Where(summary =>
                summary.ModelName == "BaselineSeasonal" && summary.Split == "Validation" && summary.HorizonStart == 1));
            Assert.Equal(24, baselineBucket1.EvaluatedPoints);
            Assert.Equal(-1d, baselineBucket1.MeanError, 10);

            Assert.NotEmpty(result.SamplePoints);
            Assert.Contains(result.SamplePoints, point => point.Split == "Train");
            Assert.Contains(result.SamplePoints, point => point.Split == "Validation");
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
                "postmodel_validation_error_by_horizon.csv",
                "postmodel_validation_error_by_horizon.svg",
                "postmodel_sample_points.csv",
                "target_over_time.svg",
                "diagnostics_report.html"
            };

            foreach (var file in expectedFiles)
            {
                Assert.True(File.Exists(Path.Combine(outputDir, file)), $"Expected artifact '{file}' was not created.");
            }

            var overlayFiles = Directory
                .GetFiles(outputDir, "target_vs_predicted_*.svg")
                .Select(Path.GetFileName)
                .ToList();
            Assert.Contains("target_vs_predicted_validation_tplus96.svg", overlayFiles);
            Assert.Contains("target_vs_predicted_validation_tplus192.svg", overlayFiles);

            var overlayCsvFiles = Directory
                .GetFiles(outputDir, "target_vs_predicted_*.csv")
                .Select(Path.GetFileName)
                .ToList();
            Assert.Contains("target_vs_predicted_validation_tplus96.csv", overlayCsvFiles);
            Assert.Contains("target_vs_predicted_validation_tplus192.csv", overlayCsvFiles);

            var html = File.ReadAllText(Path.Combine(outputDir, "diagnostics_report.html"));
            Assert.Contains("<svg", html);
            Assert.Contains("Target over time (TargetAtT)", html);
            Assert.Contains("Validation error by prediction horizon (t+1..t+192)", html);
            Assert.Contains("FastTreeRecursive", html);
            Assert.Contains("Target vs FastTreeRecursive over time by split (t+96, t+192)", html);
            Assert.Contains("Time (UTC)", html);
            Assert.Contains("Target value", html);
            Assert.Contains("Predicted vs actual sampled anchors", html);
            Assert.Contains("Split", html);

            var residualCsv = File.ReadAllLines(Path.Combine(outputDir, "postmodel_residual_summary.csv"));
            Assert.StartsWith("ModelName;Split;EvaluatedPoints;MeanError", residualCsv[0], StringComparison.Ordinal);

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

    [Fact]
    public void WriteArtifacts_WithPfiCsv_CreatesFeatureImportanceSvgAndHtmlSection()
    {
        var trainAnchor = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var validationAnchor = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        var part2Path = CreatePart2Csv(
        [
            BuildPart2Row(trainAnchor, "Train", BuildActualTargets(8d)),
            BuildPart2Row(validationAnchor, "Validation", BuildActualTargets(10d))
        ]);

        var predictionsPath = CreatePredictionsCsv([BuildPredictionRow(validationAnchor, "Validation", "FastTreeRecursive", BuildPredictions(10d))]);
        var pfiCsvPath = CreatePfiCsv();
        var outputDir = Path.Combine(Path.GetTempPath(), $"diagnostics-pfi-tests-{Guid.NewGuid():N}");

        try
        {
            var result = PartDiagnostics.RunDiagnostics(part2Path, predictionsPath, pfiCsvPath);
            PartDiagnostics.WriteArtifacts(result, outputDir);

            // Verify PFI data was loaded
            Assert.NotNull(result.FeatureImportance);
            Assert.Equal(3, result.FeatureImportance.Count);

            // Verify SVG was created
            Assert.True(File.Exists(Path.Combine(outputDir, "feature_importance.svg")));
            var svg = File.ReadAllText(Path.Combine(outputDir, "feature_importance.svg"));
            Assert.Contains("<rect", svg);
            Assert.Contains("<svg", svg);
            Assert.Contains("TargetAtT", svg);

            // Verify HTML includes PFI section
            var html = File.ReadAllText(Path.Combine(outputDir, "diagnostics_report.html"));
            Assert.Contains("Feature importance (PFI)", html);
            Assert.Contains("MAE delta", html);
            Assert.Contains("TargetAtT", html);
        }
        finally
        {
            File.Delete(part2Path);
            File.Delete(predictionsPath);
            File.Delete(pfiCsvPath);
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, true);
            }
        }
    }

    [Fact]
    public void WriteArtifacts_WithoutPfiCsv_OmitsPfiSectionGracefully()
    {
        var trainAnchor = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var validationAnchor = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        var part2Path = CreatePart2Csv(
        [
            BuildPart2Row(trainAnchor, "Train", BuildActualTargets(8d)),
            BuildPart2Row(validationAnchor, "Validation", BuildActualTargets(10d))
        ]);

        var predictionsPath = CreatePredictionsCsv([BuildPredictionRow(validationAnchor, "Validation", "FastTreeRecursive", BuildPredictions(10d))]);
        var outputDir = Path.Combine(Path.GetTempPath(), $"diagnostics-nopfi-tests-{Guid.NewGuid():N}");

        try
        {
            // Run without PFI CSV path
            var result = PartDiagnostics.RunDiagnostics(part2Path, predictionsPath);
            PartDiagnostics.WriteArtifacts(result, outputDir);

            // Verify no PFI artifacts
            Assert.Null(result.FeatureImportance);
            Assert.False(File.Exists(Path.Combine(outputDir, "feature_importance.svg")));

            // Verify HTML does NOT contain PFI section
            var html = File.ReadAllText(Path.Combine(outputDir, "diagnostics_report.html"));
            Assert.DoesNotContain("Feature importance (PFI)", html);
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
    public void ReadFeatureImportanceCsv_ParsesExpectedFormat()
    {
        var csvPath = CreatePfiCsv();

        try
        {
            var features = PartDiagnostics.ReadFeatureImportanceCsv(csvPath);

            Assert.Equal(3, features.Count);
            Assert.Equal("TargetAtT", features[0].FeatureName);
            Assert.Equal(1, features[0].Rank);
            Assert.Equal(0.5, features[0].MaeDelta, 6);
            Assert.Equal("Temperature", features[1].FeatureName);
            Assert.Equal("Windspeed", features[2].FeatureName);
        }
        finally
        {
            File.Delete(csvPath);
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

    private static string CreatePfiCsv()
    {
        var path = Path.Combine(Path.GetTempPath(), $"diag-pfi-{Guid.NewGuid():N}.csv");
        var lines = new[]
        {
            "Rank;FeatureName;MaeDelta;MaeDeltaStdDev;RmseDelta;RmseDeltaStdDev;R2Delta;R2DeltaStdDev",
            "1;TargetAtT;0.5;0.01;0.6;0.02;-0.1;0.005",
            "2;Temperature;0.3;0.008;0.4;0.015;-0.05;0.003",
            "3;Windspeed;0.1;0.005;0.15;0.01;-0.02;0.002"
        };
        File.WriteAllText(path, string.Join(Environment.NewLine, lines));
        return path;
    }
}
