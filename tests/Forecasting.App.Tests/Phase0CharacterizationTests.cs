using Forecasting.App;

namespace Forecasting.App.Tests;

public class Phase0CharacterizationTests
{
    [Fact]
    public void Part2_WriteDatasetCsv_HeaderContract_IsStable()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"phase0-part2-{Guid.NewGuid():N}");
        var csvPath = Path.Combine(outputDir, "part2.csv");

        try
        {
            var row = new Part2SupervisedRow(
                AnchorUtcTime: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                TargetAtT: 100,
                Temperature: 10,
                Windspeed: 5,
                SolarIrradiation: 0,
                HourOfDay: 0,
                MinuteOfHour: 0,
                DayOfWeek: 1,
                IsHoliday: false,
                HourSin: 0,
                HourCos: 1,
                WeekdaySin: 0,
                WeekdayCos: 1,
                TargetLag192: 99,
                TargetLag672: 95,
                TargetLag192Mean16: 98,
                TargetLag192Std16: 0.5,
                TargetLag192Mean96: 97,
                TargetLag192Std96: 0.8,
                TargetLag672Mean16: 94,
                TargetLag672Std16: 0.4,
                TargetLag672Mean96: 93,
                TargetLag672Std96: 0.7,
                Split: "Train",
                HorizonTargets: Enumerable.Repeat(100d, PipelineConstants.HorizonSteps).ToArray());

            Part2FeatureEngineering.WriteDatasetCsv([row], csvPath);

            var header = File.ReadLines(csvPath).First();
            var expected = string.Join(';',
                new[]
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
                }.Concat(Enumerable.Range(1, PipelineConstants.HorizonSteps).Select(step => $"Target_tPlus{step}")));

            Assert.Equal(expected, header);
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
    public void Part3_WriteForecastsCsv_HeaderContract_IsStable()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"phase0-part3-{Guid.NewGuid():N}");
        var csvPath = Path.Combine(outputDir, "part3_predictions.csv");

        try
        {
            var forecast = new Part3ForecastRow(
                AnchorUtcTime: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Split: "Validation",
                ModelName: "FastTreeRecursive",
                ExogenousFallbackSteps: 0,
                PredictedTargets: Enumerable.Repeat(101d, PipelineConstants.HorizonSteps).ToArray(),
                ActualTargets: Enumerable.Repeat(100d, PipelineConstants.HorizonSteps).ToArray());

            Part3Modeling.WriteForecastsCsv([forecast], csvPath);

            var header = File.ReadLines(csvPath).First();
            var expected = string.Join(';',
                new[]
                {
                    "anchorUtcTime",
                    "Split",
                    "Model",
                    "ExogenousFallbackSteps"
                }
                .Concat(Enumerable.Range(1, PipelineConstants.HorizonSteps).Select(step => $"Pred_tPlus{step}"))
                .Concat(Enumerable.Range(1, PipelineConstants.HorizonSteps).Select(step => $"Actual_tPlus{step}")));

            Assert.Equal(expected, header);
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
    public void Part4_WriteMetricsCsv_HeaderContract_IsStable()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"phase0-part4-{Guid.NewGuid():N}");
        var csvPath = Path.Combine(outputDir, "part4_metrics.csv");

        try
        {
            var result = new Part4RunResult(
                GeneratedAtUtc: DateTime.UtcNow,
                ValidationActualPoints: 1,
                ValidationPredictionPoints: 1,
                Metrics:
                [
                    new Part4ModelMetrics("BaselineSeasonal", 1, 1, 0, 1.0, 1.0, 1.0)
                ],
                SamplePoints: []);

            Part4Evaluation.WriteMetricsCsv(result, csvPath);

            var header = File.ReadLines(csvPath).First();
            Assert.Equal("ModelName;EvaluatedPoints;MapeEvaluatedPoints;ZeroActualExcludedPoints;MAE;RMSE;MAPE", header);
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
    public void Diagnostics_WriteResidualSummaryCsv_HeaderContract_IsStable()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"phase0-diagnostics-{Guid.NewGuid():N}");
        var csvPath = Path.Combine(outputDir, "postmodel_residual_summary.csv");

        try
        {
            var result = new DiagnosticsRunResult(
                GeneratedAtUtc: DateTime.UtcNow,
                PreModelSummaries: [],
                CadenceSummaries: [],
                ResidualSummaries:
                [
                    new DiagnosticsResidualSummary("FastTreeRecursive", "Validation", 1, 0.1, 0.1, 0.1, -0.1, 0.0, 0.1, 0.4, 0.6)
                ],
                HorizonBucketSummaries: [],
                ValidationHorizonSummaries: [],
                SamplePoints: [],
                TargetSeries: [],
                OverlayPoints: []);

            PartDiagnostics.WriteResidualSummaryCsv(result, csvPath);

            var header = File.ReadLines(csvPath).First();
            Assert.Equal("ModelName;Split;EvaluatedPoints;MeanError;MAE;RMSE;ResidualP05;ResidualP50;ResidualP95;UnderPredictionRate;OverPredictionRate", header);
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
    public void Part3_RunModels_OutputContract_UsesExpectedModelSetAndHorizonShape()
    {
        var rows = BuildSyntheticPart3Rows(trainCount: 160, validationCount: 8);

        var result = Part3Modeling.RunModels(rows, enablePfi: false);

        var modelNames = result.Forecasts
            .Select(forecast => forecast.ModelName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["BaselineSeasonal", "FastTreeRecursive"], modelNames);
        Assert.Equal(rows.Count * 2, result.Forecasts.Count);
        Assert.All(result.Forecasts, forecast =>
        {
            Assert.Equal(PipelineConstants.HorizonSteps, forecast.PredictedTargets.Count);
            Assert.Equal(PipelineConstants.HorizonSteps, forecast.ActualTargets.Count);
        });

        var summaryNames = result.Summary.Models
            .Select(model => model.ModelName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["BaselineSeasonal", "FastTreeRecursive"], summaryNames);
        Assert.All(result.Summary.Models, model => Assert.Equal(PipelineConstants.HorizonSteps, model.HorizonSteps));
    }

    private static List<Part2SupervisedRow> BuildSyntheticPart3Rows(int trainCount, int validationCount)
    {
        var rows = new List<Part2SupervisedRow>(trainCount + validationCount);
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var index = 0; index < trainCount + validationCount; index++)
        {
            var utcTime = start.AddMinutes(index * PipelineConstants.MinutesPerStep);
            var target = 100.0 + Math.Sin(index / 8.0) * 5.0 + index * 0.03;
            var temperature = 10.0 + Math.Sin(index / 32.0) * 7.0;
            var windspeed = 4.0 + Math.Cos(index / 40.0) * 1.5;
            var solar = Math.Max(0.0, Math.Sin((utcTime.Hour / 24.0) * Math.PI) * 500.0);
            var split = index < trainCount ? "Train" : "Validation";

            var horizon = new double[PipelineConstants.HorizonSteps];
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
}
