using System.Diagnostics;
using System.Text.Json;

namespace Forecasting.App;

public enum PipelineMode
{
    All,
    Part1,
    Part2,
    Part3,
    Part4,
    Diagnostics
}

public sealed record PipelineRunRequest(
    PipelineMode Mode,
    string[] RawArgs,
    int? ValidationWindowDays,
    bool EnablePfi,
    int PfiHorizonStep,
    int? MaxPart3Rows,
    IReadOnlyDictionary<string, string> InputPaths,
    IReadOnlyDictionary<string, string> OutputPaths);

public sealed record PipelineRunResult(
    bool Succeeded,
    IReadOnlyList<string> OutputLines);

public static class PipelineRunner
{
    public static PipelineRunResult Run(PipelineRunRequest request, Action<string>? output = null)
    {
        return request.Mode switch
        {
            PipelineMode.All => RunAll(request, output),
            PipelineMode.Part1 => RunPart1(request, output),
            PipelineMode.Part2 => RunPart2(request, output),
            PipelineMode.Part3 => RunPart3(request, output),
            PipelineMode.Part4 => RunPart4(request, output),
            PipelineMode.Diagnostics => RunDiagnostics(request, output),
            _ => Fail(output, "Unknown mode. Use one of: all, part1, part2, part3, part4, diagnostics.")
        };
    }

    private static PipelineRunResult RunAll(PipelineRunRequest request, Action<string>? output)
    {
        var dataPath = GetRequired(request.InputPaths, "data");
        var holidaysPath = GetRequired(request.InputPaths, "holidays");
        var part1OutputPath = GetRequired(request.OutputPaths, "part1FeatureMatrix");
        var part1AuditOutputPath = GetRequired(request.OutputPaths, "part1AuditCsv");
        var part1AuditSummaryOutputPath = GetRequired(request.OutputPaths, "part1AuditSummaryJson");
        var part2OutputPath = GetRequired(request.OutputPaths, "part2DatasetCsv");
        var part2SummaryPath = GetRequired(request.OutputPaths, "part2SummaryJson");
        var part3OutputPath = GetRequired(request.OutputPaths, "part3PredictionsCsv");
        var part3SummaryPath = GetRequired(request.OutputPaths, "part3SummaryJson");
        var part3PfiOutputPath = GetRequired(request.OutputPaths, "part3FeatureImportanceCsv");
        var part4MetricsOutputPath = GetRequired(request.OutputPaths, "part4MetricsCsv");
        var part4SampleOutputPath = GetRequired(request.OutputPaths, "part4SampleCsv");
        var diagnosticsOutputDirectory = GetRequired(request.OutputPaths, "diagnosticsDirectory");
        var validationWindowDays = request.ValidationWindowDays ?? PipelineConstants.DefaultValidationWindowDays;

        if (!File.Exists(dataPath) || !File.Exists(holidaysPath))
        {
            return Fail(
                output,
                "Input files not found.",
                $"Data path: {dataPath}",
                $"Holidays path: {holidaysPath}");
        }

        var lines = new List<string>();
        AddOutputLine(lines, "Running full pipeline (Part 1 -> Part 4 + diagnostics)...", output);

        var preprocessed = Part1Preprocessing.BuildPreprocessedDatasetForEvaluation(dataPath, holidaysPath, validationWindowDays);
        Part1Preprocessing.WriteFeatureMatrixCsv(preprocessed.PersistedFeatures, part1OutputPath);
        Part1Preprocessing.WritePreprocessingAuditCsv(preprocessed.AuditEvents, part1AuditOutputPath);
        Part1Preprocessing.WritePreprocessingSummaryJson(preprocessed.AuditSummary, part1AuditSummaryOutputPath);
        AddOutputLine(lines, $"Part 1 complete: {preprocessed.PersistedFeatures.Count} rows -> {part1OutputPath} (validation window: {validationWindowDays} days)", output);

        var allValidationStartUtc = preprocessed.PersistedFeatures.Max(row => row.UtcTime).AddDays(-validationWindowDays);
        var part2Dataset = Part2FeatureEngineering.BuildDataset(preprocessed.PersistedFeatures, allValidationStartUtc);
        Part2FeatureEngineering.WriteDatasetCsv(part2Dataset.Rows, part2OutputPath);
        Part2FeatureEngineering.WriteSummaryJson(part2Dataset.Summary, part2SummaryPath);
        AddOutputLine(lines, $"Part 2 complete: {part2Dataset.Summary.OutputRows} rows -> {part2OutputPath} (validation window: {validationWindowDays} days)", output);

        var part3Rows = ApplyPart3RowLimit(part2Dataset.Rows, request.MaxPart3Rows, lines, output);
        var part3Result = Part3Modeling.RunModels(part3Rows, enablePfi: request.EnablePfi, pfiHorizonStep: request.PfiHorizonStep);
        Part3Modeling.WriteForecastsCsv(part3Result.Forecasts, part3OutputPath);
        Part3Modeling.WriteSummaryJson(part3Result.Summary, part3SummaryPath);
        if (part3Result.FeatureImportance is not null)
        {
            Part3Modeling.WriteFeatureImportanceCsv(part3Result.FeatureImportance, part3PfiOutputPath);
            AddOutputLine(lines, $"Part 3 PFI complete: {part3Result.FeatureImportance.Features.Count} features for horizon t+{part3Result.FeatureImportance.HorizonStep} -> {part3PfiOutputPath}", output);
        }

        AddOutputLine(lines, $"Part 3 complete: {part3Result.Forecasts.Count} forecasts -> {part3OutputPath}", output);

        var part4Result = Part4Evaluation.RunEvaluation(part3Rows, part3Result.Forecasts);
        Part4Evaluation.WriteMetricsCsv(part4Result, part4MetricsOutputPath);
        Part4Evaluation.WriteSampleCsv(part4Result, part4SampleOutputPath);
        AddOutputLine(lines, $"Part 4 complete: metrics -> {part4MetricsOutputPath}", output);

        var diagnosticsResult = PartDiagnostics.RunDiagnostics(part3Rows, part3Result.Forecasts, part3Result.FeatureImportance?.Features);
        PartDiagnostics.WriteArtifacts(diagnosticsResult, diagnosticsOutputDirectory);
        AddOutputLine(lines, $"Diagnostics complete: {diagnosticsOutputDirectory}", output);

        WriteRunManifest(
            outputDirectory: "artifacts",
            mode: "all",
            rawArgs: request.RawArgs,
            validationWindowDays: validationWindowDays,
            enablePfi: request.EnablePfi,
            pfiHorizonStep: request.PfiHorizonStep,
            maxPart3Rows: request.MaxPart3Rows,
            inputPaths: request.InputPaths,
            outputPaths: request.OutputPaths);

        return Success(lines);
    }

    private static PipelineRunResult RunPart1(PipelineRunRequest request, Action<string>? output)
    {
        var dataPath = GetRequired(request.InputPaths, "data");
        var holidaysPath = GetRequired(request.InputPaths, "holidays");
        var outputPath = GetRequired(request.OutputPaths, "part1FeatureMatrix");
        var auditOutputPath = GetRequired(request.OutputPaths, "part1AuditCsv");
        var auditSummaryOutputPath = GetRequired(request.OutputPaths, "part1AuditSummaryJson");
        var validationWindowDays = request.ValidationWindowDays ?? PipelineConstants.DefaultValidationWindowDays;

        if (!File.Exists(dataPath) || !File.Exists(holidaysPath))
        {
            return Fail(
                output,
                "Input files not found.",
                $"Data path: {dataPath}",
                $"Holidays path: {holidaysPath}");
        }

        var preprocessed = Part1Preprocessing.BuildPreprocessedDatasetForEvaluation(dataPath, holidaysPath, validationWindowDays);
        Part1Preprocessing.WriteFeatureMatrixCsv(preprocessed.PersistedFeatures, outputPath);
        Part1Preprocessing.WritePreprocessingAuditCsv(preprocessed.AuditEvents, auditOutputPath);
        Part1Preprocessing.WritePreprocessingSummaryJson(preprocessed.AuditSummary, auditSummaryOutputPath);

        return Success(
            output,
            $"Validation split starts at: {preprocessed.AuditSummary.ValidationStartUtc:yyyy-MM-dd HH:mm:ss} UTC",
            $"Generated {preprocessed.PersistedFeatures.Count} persisted feature rows.",
            $"Saved feature matrix to: {outputPath}",
            $"Saved preprocessing audit events to: {auditOutputPath}",
            $"Saved preprocessing summary to: {auditSummaryOutputPath}");
    }

    private static PipelineRunResult RunPart2(PipelineRunRequest request, Action<string>? output)
    {
        var part2InputPath = GetRequired(request.InputPaths, "part1FeatureMatrix");
        var part2OutputPath = GetRequired(request.OutputPaths, "part2DatasetCsv");
        var part2SummaryPath = GetRequired(request.OutputPaths, "part2SummaryJson");
        var validationWindowDays = request.ValidationWindowDays ?? PipelineConstants.DefaultValidationWindowDays;

        if (!File.Exists(part2InputPath))
        {
            return Fail(
                output,
                "Part 2 input file not found.",
                $"Input path: {part2InputPath}");
        }

        var part1Rows = Part2FeatureEngineering.ReadFeatureMatrixCsv(part2InputPath);
        var part2ValidationStartUtc = part1Rows.Max(row => row.UtcTime).AddDays(-validationWindowDays);
        var part2Dataset = Part2FeatureEngineering.BuildDataset(part1Rows, part2ValidationStartUtc);
        Part2FeatureEngineering.WriteDatasetCsv(part2Dataset.Rows, part2OutputPath);
        Part2FeatureEngineering.WriteSummaryJson(part2Dataset.Summary, part2SummaryPath);

        return Success(
            output,
            $"Part 2 validation split starts at: {part2Dataset.Summary.ValidationStartUtc:yyyy-MM-dd HH:mm:ss} UTC",
            $"Part 2 validation window: {validationWindowDays} days",
            $"Generated {part2Dataset.Summary.OutputRows} Part 2 supervised rows.",
            $"Saved Part 2 dataset to: {part2OutputPath}",
            $"Saved Part 2 summary to: {part2SummaryPath}");
    }

    private static PipelineRunResult RunPart3(PipelineRunRequest request, Action<string>? output)
    {
        var part3InputPath = GetRequired(request.InputPaths, "part2DatasetCsv");
        var part3OutputPath = GetRequired(request.OutputPaths, "part3PredictionsCsv");
        var part3SummaryPath = GetRequired(request.OutputPaths, "part3SummaryJson");
        var part3PfiPath = GetRequired(request.OutputPaths, "part3FeatureImportanceCsv");

        if (!File.Exists(part3InputPath))
        {
            return Fail(
                output,
                "Part 3 input file not found.",
                $"Input path: {part3InputPath}");
        }

        var lines = new List<string>();
        var part3Rows = Part3Modeling.ReadPart2DatasetCsv(part3InputPath);
        part3Rows = ApplyPart3RowLimit(part3Rows, request.MaxPart3Rows, lines, output);
        var part3Result = Part3Modeling.RunModels(part3Rows, enablePfi: request.EnablePfi, pfiHorizonStep: request.PfiHorizonStep);
        Part3Modeling.WriteForecastsCsv(part3Result.Forecasts, part3OutputPath);
        Part3Modeling.WriteSummaryJson(part3Result.Summary, part3SummaryPath);

        if (part3Result.FeatureImportance is not null)
        {
            Part3Modeling.WriteFeatureImportanceCsv(part3Result.FeatureImportance, part3PfiPath);
            AddOutputLine(lines, $"Saved Part 3 PFI (horizon t+{part3Result.FeatureImportance.HorizonStep}) to: {part3PfiPath}", output);
        }

        AddOutputLine(lines, $"Part 3 models: {string.Join(", ", part3Result.Summary.Models.Select(model => model.ModelName))}", output);
        AddOutputLine(lines, $"Generated {part3Result.Forecasts.Count} forecast rows.", output);
        AddOutputLine(lines, $"Saved Part 3 predictions to: {part3OutputPath}", output);
        AddOutputLine(lines, $"Saved Part 3 summary to: {part3SummaryPath}", output);

        WriteRunManifest(
            outputDirectory: Path.GetDirectoryName(part3OutputPath) ?? "artifacts",
            mode: "part3",
            rawArgs: request.RawArgs,
            validationWindowDays: null,
            enablePfi: request.EnablePfi,
            pfiHorizonStep: request.PfiHorizonStep,
            maxPart3Rows: request.MaxPart3Rows,
            inputPaths: request.InputPaths,
            outputPaths: request.OutputPaths);

        return Success(lines);
    }

    private static PipelineRunResult RunPart4(PipelineRunRequest request, Action<string>? output)
    {
        var part4InputPath = GetRequired(request.InputPaths, "part2DatasetCsv");
        var part4PredictionsPath = GetRequired(request.InputPaths, "part3PredictionsCsv");
        var part4MetricsOutputPath = GetRequired(request.OutputPaths, "part4MetricsCsv");
        var part4SampleOutputPath = GetRequired(request.OutputPaths, "part4SampleCsv");

        if (!File.Exists(part4InputPath))
        {
            return Fail(
                output,
                "Part 4 input file not found.",
                $"Input path: {part4InputPath}");
        }

        if (!File.Exists(part4PredictionsPath))
        {
            return Fail(
                output,
                "Part 4 predictions file not found.",
                $"Predictions path: {part4PredictionsPath}");
        }

        var part4Result = Part4Evaluation.RunEvaluation(part4InputPath, part4PredictionsPath);
        Part4Evaluation.WriteMetricsCsv(part4Result, part4MetricsOutputPath);
        Part4Evaluation.WriteSampleCsv(part4Result, part4SampleOutputPath);

        var lines = new List<string>();
        AddOutputLine(lines, "Part 4 model comparison:", output);
        AddOutputLine(lines, "Model\tMAE\tRMSE\tMAPE\tPoints\tMAPEPoints", output);

        foreach (var metric in part4Result.Metrics.OrderBy(metric => metric.ModelName, StringComparer.Ordinal))
        {
            AddOutputLine(lines, $"{metric.ModelName}\t{metric.Mae:F6}\t{metric.Rmse:F6}\t{metric.Mape:F6}\t{metric.EvaluatedPoints}\t{metric.MapeEvaluatedPoints}", output);
        }

        AddOutputLine(lines, $"Saved Part 4 metrics to: {part4MetricsOutputPath}", output);
        AddOutputLine(lines, $"Saved Part 4 sample to: {part4SampleOutputPath}", output);
        return Success(lines);
    }

    private static PipelineRunResult RunDiagnostics(PipelineRunRequest request, Action<string>? output)
    {
        var diagnosticsInputPath = GetRequired(request.InputPaths, "part2DatasetCsv");
        var diagnosticsPredictionsPath = GetRequired(request.InputPaths, "part3PredictionsCsv");
        var diagnosticsPfiPath = GetOptional(request.InputPaths, "part3FeatureImportanceCsv");
        var diagnosticsOutputDirectory = GetRequired(request.OutputPaths, "diagnosticsDirectory");

        if (!File.Exists(diagnosticsInputPath))
        {
            return Fail(
                output,
                "Diagnostics input file not found.",
                $"Input path: {diagnosticsInputPath}");
        }

        if (!File.Exists(diagnosticsPredictionsPath))
        {
            return Fail(
                output,
                "Diagnostics predictions file not found.",
                $"Predictions path: {diagnosticsPredictionsPath}");
        }

        var diagnosticsResult = PartDiagnostics.RunDiagnostics(diagnosticsInputPath, diagnosticsPredictionsPath, diagnosticsPfiPath);
        PartDiagnostics.WriteArtifacts(diagnosticsResult, diagnosticsOutputDirectory);

        var lines = new List<string>();
        AddOutputLine(lines, "Diagnostics residual summary:", output);
        AddOutputLine(lines, "Model\tSplit\tMeanError\tMAE\tRMSE\tUnder%\tPoints", output);

        foreach (var summary in diagnosticsResult.ResidualSummaries
                     .OrderBy(summary => summary.ModelName, StringComparer.Ordinal)
                     .ThenBy(summary => summary.Split, StringComparer.Ordinal))
        {
            AddOutputLine(lines, $"{summary.ModelName}\t{summary.Split}\t{summary.MeanError:F6}\t{summary.Mae:F6}\t{summary.Rmse:F6}\t{summary.UnderPredictionRate:F6}\t{summary.EvaluatedPoints}", output);
        }

        AddOutputLine(lines, $"Saved diagnostics artifacts to: {diagnosticsOutputDirectory}", output);
        return Success(lines);
    }

    private static void AddOutputLine(ICollection<string> lines, string line, Action<string>? output)
    {
        lines.Add(line);
        output?.Invoke(line);
    }

    private static PipelineRunResult Success(IReadOnlyList<string> lines)
    {
        return new PipelineRunResult(true, lines);
    }

    private static PipelineRunResult Success(Action<string>? output, params string[] lines)
    {
        foreach (var line in lines)
        {
            output?.Invoke(line);
        }

        return Success(lines);
    }

    private static PipelineRunResult Success(params string[] lines)
    {
        return new PipelineRunResult(true, lines);
    }

    private static PipelineRunResult Fail(Action<string>? output, params string[] lines)
    {
        foreach (var line in lines)
        {
            output?.Invoke(line);
        }

        return Fail(lines);
    }

    private static PipelineRunResult Fail(params string[] lines)
    {
        return new PipelineRunResult(false, lines);
    }

    private static string GetRequired(IReadOnlyDictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Missing required path '{key}' in pipeline request.");
        }

        return value;
    }

    private static string? GetOptional(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
    }

    private static List<Part2SupervisedRow> ApplyPart3RowLimit(
        IReadOnlyList<Part2SupervisedRow> rows,
        int? maxPart3Rows,
        ICollection<string>? lines,
        Action<string>? output)
    {
        if (!maxPart3Rows.HasValue)
        {
            return rows as List<Part2SupervisedRow> ?? rows.ToList();
        }

        if (rows.Count <= maxPart3Rows.Value)
        {
            return rows.OrderBy(row => row.AnchorUtcTime).ToList();
        }

        var trainRows = rows
            .Where(row => string.Equals(row.Split, "Train", StringComparison.OrdinalIgnoreCase))
            .OrderBy(row => row.AnchorUtcTime)
            .ToList();
        var validationRows = rows
            .Where(row => string.Equals(row.Split, "Validation", StringComparison.OrdinalIgnoreCase))
            .OrderBy(row => row.AnchorUtcTime)
            .ToList();

        if (trainRows.Count == 0 || validationRows.Count == 0)
        {
            return rows.OrderBy(row => row.AnchorUtcTime).ToList();
        }

        var targetTrainRows = Math.Max(1, maxPart3Rows.Value / 2);
        var targetValidationRows = Math.Max(1, maxPart3Rows.Value - targetTrainRows);

        targetTrainRows = Math.Min(targetTrainRows, trainRows.Count);
        targetValidationRows = Math.Min(targetValidationRows, validationRows.Count);

        var remainingBudget = maxPart3Rows.Value - (targetTrainRows + targetValidationRows);
        if (remainingBudget > 0)
        {
            var availableTrain = trainRows.Count - targetTrainRows;
            var addTrain = Math.Min(availableTrain, remainingBudget);
            targetTrainRows += addTrain;
            remainingBudget -= addTrain;

            if (remainingBudget > 0)
            {
                var availableValidation = validationRows.Count - targetValidationRows;
                var addValidation = Math.Min(availableValidation, remainingBudget);
                targetValidationRows += addValidation;
            }
        }

        var selected = trainRows
            .TakeLast(targetTrainRows)
            .Concat(validationRows.Take(targetValidationRows))
            .OrderBy(row => row.AnchorUtcTime)
            .ToList();

        if (lines is not null)
        {
            AddOutputLine(
                lines,
                $"Part 3 row limit applied for smoke run: {selected.Count}/{rows.Count} rows (Train={targetTrainRows}, Validation={targetValidationRows}).",
                output);
        }
        else
        {
            output?.Invoke($"Part 3 row limit applied for smoke run: {selected.Count}/{rows.Count} rows (Train={targetTrainRows}, Validation={targetValidationRows}).");
        }

        return selected;
    }

    private static void WriteRunManifest(
        string outputDirectory,
        string mode,
        string[] rawArgs,
        int? validationWindowDays,
        bool enablePfi,
        int pfiHorizonStep,
        int? maxPart3Rows,
        IReadOnlyDictionary<string, string> inputPaths,
        IReadOnlyDictionary<string, string> outputPaths)
    {
        Directory.CreateDirectory(outputDirectory);

        var fastTreeOptions = new FastTreeOptions();
        var manifest = new
        {
            GeneratedAtUtc = DateTime.UtcNow,
            Mode = mode,
            RawArgs = rawArgs,
            ValidationWindowDays = validationWindowDays,
            Pfi = new
            {
                Enabled = enablePfi,
                HorizonStep = enablePfi ? (int?)pfiHorizonStep : null
            },
            MaxPart3Rows = maxPart3Rows,
            Pipeline = new
            {
                PipelineConstants.HorizonSteps,
                PipelineConstants.MinutesPerStep,
                PipelineConstants.DefaultValidationWindowDays
            },
            FastTreeOptions = fastTreeOptions,
            InputPaths = inputPaths,
            OutputPaths = outputPaths,
            Git = new
            {
                Branch = TryRunGitCommand("rev-parse --abbrev-ref HEAD"),
                Commit = TryRunGitCommand("rev-parse --short HEAD")
            }
        };

        var outputPath = Path.Combine(outputDirectory, "run_manifest.json");
        var json = JsonSerializer.Serialize(manifest, FileOutput.IndentedJsonOptions);
        File.WriteAllText(outputPath, json);
    }

    private static string? TryRunGitCommand(string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                return null;
            }

            var trimmed = output.Trim();
            return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
        }
        catch
        {
            return null;
        }
    }
}
