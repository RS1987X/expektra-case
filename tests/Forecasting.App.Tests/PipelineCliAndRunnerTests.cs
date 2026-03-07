using Forecasting.App;

namespace Forecasting.App.Tests;

public class PipelineCliAndRunnerTests
{
    [Fact]
    public void BuildRequest_NoArgs_UsesAllModeDefaults()
    {
        var request = PipelineCliRequestParser.BuildRequest([]);

        Assert.NotNull(request);
        Assert.Equal(PipelineMode.All, request!.Mode);
        Assert.Equal(PipelineConstants.DefaultValidationWindowDays, request.ValidationWindowDays);
        Assert.False(request.EnablePfi);
        Assert.Equal(1, request.PfiHorizonStep);
        Assert.Null(request.MaxPart3Rows);
        Assert.Equal(Path.Combine("data", "testdata.csv"), request.InputPaths["data"]);
        Assert.Equal(Path.Combine("artifacts", "part3_predictions.csv"), request.OutputPaths["part3PredictionsCsv"]);
    }

    [Fact]
    public void BuildRequest_Part3WithPfiHorizon_ParsesOptionAndPositionalArgs()
    {
        var request = PipelineCliRequestParser.BuildRequest([
            "part3",
            "in.csv",
            "out.csv",
            "summary.json",
            "--pfi-horizon",
            "4"
        ]);

        Assert.NotNull(request);
        Assert.Equal(PipelineMode.Part3, request!.Mode);
        Assert.True(request.EnablePfi);
        Assert.Equal(4, request.PfiHorizonStep);
        Assert.Equal("in.csv", request.InputPaths["part2DatasetCsv"]);
        Assert.Equal("out.csv", request.OutputPaths["part3PredictionsCsv"]);
        Assert.Equal("summary.json", request.OutputPaths["part3SummaryJson"]);
    }

    [Fact]
    public void BuildRequest_AllWithMaxRows_ParsesSmokeLimit()
    {
        var request = PipelineCliRequestParser.BuildRequest([
            "all",
            "30",
            "--max-rows",
            "120"
        ]);

        Assert.NotNull(request);
        Assert.Equal(PipelineMode.All, request!.Mode);
        Assert.Equal(120, request.MaxPart3Rows);
    }

    [Fact]
    public void BuildRequest_UnknownMode_ReturnsNull()
    {
        var request = PipelineCliRequestParser.BuildRequest(["not-a-mode"]);

        Assert.Null(request);
    }

    [Fact]
    public void BuildRequest_InvalidPfiHorizon_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            PipelineCliRequestParser.BuildRequest(["part3", "--pfi-horizon", "999"]));

        Assert.Contains("Invalid --pfi-horizon value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRequest_InvalidMaxRows_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            PipelineCliRequestParser.BuildRequest(["part3", "--max-rows", "1"]));

        Assert.Contains("Invalid --max-rows value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_Part4MissingInput_StreamsFailureLines()
    {
        var request = new PipelineRunRequest(
            Mode: PipelineMode.Part4,
            RawArgs: ["part4"],
            ValidationWindowDays: null,
            EnablePfi: false,
            PfiHorizonStep: 1,
            MaxPart3Rows: null,
            InputPaths: new Dictionary<string, string>
            {
                ["part2DatasetCsv"] = Path.Combine(Path.GetTempPath(), $"missing-part2-{Guid.NewGuid():N}.csv"),
                ["part3PredictionsCsv"] = Path.Combine(Path.GetTempPath(), $"missing-part3-{Guid.NewGuid():N}.csv")
            },
            OutputPaths: new Dictionary<string, string>
            {
                ["part4MetricsCsv"] = "unused.csv",
                ["part4SampleCsv"] = "unused2.csv"
            });

        var streamed = new List<string>();
        var result = PipelineRunner.Run(request, streamed.Add);

        Assert.False(result.Succeeded);
        Assert.Equal(result.OutputLines, streamed);
        Assert.Equal("Part 4 input file not found.", streamed[0]);
    }

    [Fact]
    public void Run_Part1Success_StreamsSameLinesAsResult()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"pipeline-part1-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);

        var dataPath = Path.Combine(outputDir, "testdata.csv");
        File.WriteAllText(dataPath,
            "utcTime;Target;Temperature;Windspeed;SolarIrradiation\n" +
            "2024-01-01 00:00;10,0;1,0;2,0;0\n" +
            "2024-01-01 00:15;11,0;1,1;2,1;1\n" +
            "2024-01-03 00:00;12,0;1,2;2,2;2\n" +
            "2024-01-03 00:15;13,0;1,3;2,3;3\n");

        var holidaysPath = Path.Combine(outputDir, "holidays.public.csv");
        File.WriteAllText(holidaysPath,
            "Id;Country;StartDate;EndDate;Type;RegionalScope;Name\n" +
            "123;SE;2024-01-01;;Public;National;SV Nyårsdagen\n");

        var featurePath = Path.Combine(outputDir, "part1_feature_matrix.csv");
        var auditPath = Path.Combine(outputDir, "part1_feature_matrix.audit.csv");
        var auditSummaryPath = Path.Combine(outputDir, "part1_feature_matrix.audit.summary.json");

        try
        {
            var request = new PipelineRunRequest(
                Mode: PipelineMode.Part1,
                RawArgs: ["part1"],
                ValidationWindowDays: 1,
                EnablePfi: false,
                PfiHorizonStep: 1,
                MaxPart3Rows: null,
                InputPaths: new Dictionary<string, string>
                {
                    ["data"] = dataPath,
                    ["holidays"] = holidaysPath
                },
                OutputPaths: new Dictionary<string, string>
                {
                    ["part1FeatureMatrix"] = featurePath,
                    ["part1AuditCsv"] = auditPath,
                    ["part1AuditSummaryJson"] = auditSummaryPath
                });

            var streamed = new List<string>();
            var result = PipelineRunner.Run(request, streamed.Add);

            Assert.True(result.Succeeded);
            Assert.Equal(result.OutputLines, streamed);
            Assert.True(File.Exists(featurePath));
            Assert.True(File.Exists(auditPath));
            Assert.True(File.Exists(auditSummaryPath));
            Assert.Contains(streamed, line => line.StartsWith("Generated ", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, true);
            }
        }
    }
}
