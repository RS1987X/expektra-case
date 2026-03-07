using Forecasting.App;

namespace Forecasting.App.Tests;

public class Phase1bHybridHandoffTests
{
    private const double MetricTolerance = 1e-9;

    [Fact]
    public void Part4_InMemoryAndFilePathEvaluations_AreEquivalent()
    {
        using var artifacts = BuildArtifacts();
        var part2Path = artifacts.Part2Path;
        var part3Path = artifacts.Part3Path;

        var filePathResult = Part4Evaluation.RunEvaluation(part2Path, part3Path);

        var part2Rows = Part3Modeling.ReadPart2DatasetCsv(part2Path);
        var forecastRows = Part3Modeling.ReadForecastsCsv(part3Path);
        var inMemoryResult = Part4Evaluation.RunEvaluation(part2Rows, forecastRows);

        Assert.Equal(filePathResult.ValidationActualPoints, inMemoryResult.ValidationActualPoints);
        Assert.Equal(filePathResult.ValidationPredictionPoints, inMemoryResult.ValidationPredictionPoints);
        Assert.Equal(filePathResult.Metrics.Count, inMemoryResult.Metrics.Count);
        Assert.Equal(filePathResult.SamplePoints.Count, inMemoryResult.SamplePoints.Count);

        for (var index = 0; index < filePathResult.Metrics.Count; index++)
        {
            var expected = filePathResult.Metrics[index];
            var actual = inMemoryResult.Metrics[index];

            Assert.Equal(expected.ModelName, actual.ModelName);
            Assert.Equal(expected.EvaluatedPoints, actual.EvaluatedPoints);
            Assert.Equal(expected.MapeEvaluatedPoints, actual.MapeEvaluatedPoints);
            Assert.Equal(expected.ZeroActualExcludedPoints, actual.ZeroActualExcludedPoints);
            Assert.InRange(Math.Abs(expected.Mae - actual.Mae), 0d, MetricTolerance);
            Assert.InRange(Math.Abs(expected.Rmse - actual.Rmse), 0d, MetricTolerance);
            Assert.InRange(Math.Abs(expected.Mape - actual.Mape), 0d, MetricTolerance);
        }
    }

    [Fact]
    public void Diagnostics_InMemoryAndFilePathRuns_AreEquivalent()
    {
        using var artifacts = BuildArtifacts();
        var part2Path = artifacts.Part2Path;
        var part3Path = artifacts.Part3Path;

        var filePathResult = PartDiagnostics.RunDiagnostics(part2Path, part3Path);

        var part2Rows = Part3Modeling.ReadPart2DatasetCsv(part2Path);
        var forecastRows = Part3Modeling.ReadForecastsCsv(part3Path);
        var inMemoryResult = PartDiagnostics.RunDiagnostics(part2Rows, forecastRows);

        Assert.Equal(filePathResult.PreModelSummaries.Count, inMemoryResult.PreModelSummaries.Count);
        Assert.Equal(filePathResult.CadenceSummaries.Count, inMemoryResult.CadenceSummaries.Count);
        Assert.Equal(filePathResult.ResidualSummaries.Count, inMemoryResult.ResidualSummaries.Count);
        Assert.Equal(filePathResult.HorizonBucketSummaries.Count, inMemoryResult.HorizonBucketSummaries.Count);
        Assert.Equal(filePathResult.ValidationHorizonSummaries.Count, inMemoryResult.ValidationHorizonSummaries.Count);
        Assert.Equal(filePathResult.SamplePoints.Count, inMemoryResult.SamplePoints.Count);
        Assert.Equal(filePathResult.TargetSeries.Count, inMemoryResult.TargetSeries.Count);
        Assert.Equal(filePathResult.OverlayPoints.Count, inMemoryResult.OverlayPoints.Count);

        for (var index = 0; index < filePathResult.ResidualSummaries.Count; index++)
        {
            var expected = filePathResult.ResidualSummaries[index];
            var actual = inMemoryResult.ResidualSummaries[index];

            Assert.Equal(expected.ModelName, actual.ModelName);
            Assert.Equal(expected.Split, actual.Split);
            Assert.Equal(expected.EvaluatedPoints, actual.EvaluatedPoints);
            Assert.InRange(Math.Abs(expected.Mae - actual.Mae), 0d, MetricTolerance);
            Assert.InRange(Math.Abs(expected.Rmse - actual.Rmse), 0d, MetricTolerance);
            Assert.InRange(Math.Abs(expected.MeanError - actual.MeanError), 0d, MetricTolerance);
        }
    }

    private static TestArtifacts BuildArtifacts()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"phase1b-hybrid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);

        var part1Path = Path.Combine(outputDir, "part1_feature_matrix.csv");
        var part2Path = Path.Combine(outputDir, "part2_supervised_matrix.csv");
        var part3Path = Path.Combine(outputDir, "part3_predictions.csv");
        var (part2Rows, forecastRows) = BuildSyntheticData();

        // Retain the expected artifact chain in a lightweight fixture.
        Part1Preprocessing.WriteFeatureMatrixCsv([], part1Path);
        Part2FeatureEngineering.WriteDatasetCsv(part2Rows, part2Path);
        Part3Modeling.WriteForecastsCsv(forecastRows, part3Path);

        return new TestArtifacts(outputDir, part2Path, part3Path);
    }

    private static (IReadOnlyList<Part2SupervisedRow> Part2Rows, IReadOnlyList<Part3ForecastRow> ForecastRows) BuildSyntheticData()
    {
        var anchors = new[]
        {
            (new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), "Train", 100d),
            (new DateTime(2024, 1, 1, 0, 15, 0, DateTimeKind.Utc), "Validation", 110d),
            (new DateTime(2024, 1, 1, 0, 30, 0, DateTimeKind.Utc), "Validation", 120d)
        };

        var part2Rows = new List<Part2SupervisedRow>();
        var forecastRows = new List<Part3ForecastRow>();

        foreach (var (anchorUtcTime, split, targetAtT) in anchors)
        {
            var actualTargets = Enumerable.Range(1, PipelineConstants.HorizonSteps)
                .Select(step => targetAtT + step)
                .ToArray();

            part2Rows.Add(new Part2SupervisedRow(
                anchorUtcTime,
                targetAtT,
                Temperature: 10d,
                Windspeed: 2d,
                SolarIrradiation: 1d,
                HourOfDay: anchorUtcTime.Hour,
                MinuteOfHour: anchorUtcTime.Minute,
                DayOfWeek: (int)anchorUtcTime.DayOfWeek,
                IsHoliday: false,
                HourSin: 0d,
                HourCos: 1d,
                WeekdaySin: 0d,
                WeekdayCos: 1d,
                TargetLag192: targetAtT - 1d,
                TargetLag672: targetAtT - 2d,
                TargetLag192Mean16: targetAtT - 1d,
                TargetLag192Std16: 0.1d,
                TargetLag192Mean96: targetAtT - 1d,
                TargetLag192Std96: 0.2d,
                TargetLag672Mean16: targetAtT - 2d,
                TargetLag672Std16: 0.3d,
                TargetLag672Mean96: targetAtT - 2d,
                TargetLag672Std96: 0.4d,
                Split: split,
                HorizonTargets: actualTargets));

            forecastRows.Add(new Part3ForecastRow(
                anchorUtcTime,
                split,
                "BaselineSeasonal",
                ExogenousFallbackSteps: 0,
                PredictedTargets: actualTargets.Select(v => v + 0.5d).ToArray(),
                ActualTargets: actualTargets));

            forecastRows.Add(new Part3ForecastRow(
                anchorUtcTime,
                split,
                "FastTreeRecursive",
                ExogenousFallbackSteps: 0,
                PredictedTargets: actualTargets.Select(v => v - 0.25d).ToArray(),
                ActualTargets: actualTargets));
        }

        return (part2Rows, forecastRows);
    }

    private sealed class TestArtifacts : IDisposable
    {
        public TestArtifacts(string outputDirectory, string part2Path, string part3Path)
        {
            OutputDirectory = outputDirectory;
            Part2Path = part2Path;
            Part3Path = part3Path;
        }

        public string OutputDirectory { get; }

        public string Part2Path { get; }

        public string Part3Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(OutputDirectory))
            {
                Directory.Delete(OutputDirectory, true);
            }
        }
    }
}
