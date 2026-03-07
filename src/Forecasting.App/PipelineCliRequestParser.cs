namespace Forecasting.App;

public static class PipelineCliRequestParser
{
    public static PipelineRunRequest? BuildRequest(string[] args)
    {
        var pfiHorizonStep = ParseOptionalPfiHorizonStep(args);
        var maxPart3Rows = ParseOptionalMaxPart3Rows(args);
        var enablePfi = args.Any(a => string.Equals(a, "--pfi", StringComparison.OrdinalIgnoreCase)) || pfiHorizonStep.HasValue;
        var effectivePfiHorizonStep = pfiHorizonStep ?? 1;
        var positionalArgs = ExtractPositionalArgs(args);

        return BuildRequest(positionalArgs, args, enablePfi, effectivePfiHorizonStep, maxPart3Rows);
    }

    private static PipelineRunRequest? BuildRequest(string[] positionalArgs, string[] rawArgs, bool enablePfi, int pfiHorizonStep, int? maxPart3Rows)
    {
        if (positionalArgs.Length == 0 || string.Equals(positionalArgs[0], "all", StringComparison.OrdinalIgnoreCase))
        {
            var validationWindowDays = positionalArgs.Length > 1
                ? ParseValidationWindowDays(positionalArgs[1])
                : PipelineConstants.DefaultValidationWindowDays;

            return new PipelineRunRequest(
                Mode: PipelineMode.All,
                RawArgs: rawArgs,
                ValidationWindowDays: validationWindowDays,
                EnablePfi: enablePfi,
                PfiHorizonStep: pfiHorizonStep,
                MaxPart3Rows: maxPart3Rows,
                InputPaths: new Dictionary<string, string>
                {
                    ["data"] = Path.Combine("data", "testdata.csv"),
                    ["holidays"] = Path.Combine("data", "holidays.public.csv")
                },
                OutputPaths: new Dictionary<string, string>
                {
                    ["part1FeatureMatrix"] = Path.Combine("artifacts", "part1_feature_matrix.csv"),
                    ["part1AuditCsv"] = Path.Combine("artifacts", "part1_feature_matrix.audit.csv"),
                    ["part1AuditSummaryJson"] = Path.Combine("artifacts", "part1_feature_matrix.audit.summary.json"),
                    ["part2DatasetCsv"] = Path.Combine("artifacts", "part2_supervised_matrix.csv"),
                    ["part2SummaryJson"] = Path.Combine("artifacts", "part2_supervised_matrix.summary.json"),
                    ["part3PredictionsCsv"] = Path.Combine("artifacts", "part3_predictions.csv"),
                    ["part3SummaryJson"] = Path.Combine("artifacts", "part3_predictions.summary.json"),
                    ["part3FeatureImportanceCsv"] = Path.Combine("artifacts", "part3_feature_importance.csv"),
                    ["part4MetricsCsv"] = Path.Combine("artifacts", "part4_metrics.csv"),
                    ["part4SampleCsv"] = Path.Combine("artifacts", "part4_pred_vs_actual_sample.csv"),
                    ["diagnosticsDirectory"] = Path.Combine("artifacts", "diagnostics")
                });
        }

        if (string.Equals(positionalArgs[0], "diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            var diagnosticsInputPath = positionalArgs.Length > 1 ? positionalArgs[1] : Path.Combine("artifacts", "part2_supervised_matrix.csv");
            var diagnosticsPredictionsPath = positionalArgs.Length > 2 ? positionalArgs[2] : Path.Combine("artifacts", "part3_predictions.csv");
            var diagnosticsOutputDirectory = positionalArgs.Length > 3 ? positionalArgs[3] : Path.Combine("artifacts", "diagnostics");
            var diagnosticsPfiPath = positionalArgs.Length > 4 ? positionalArgs[4] : Path.Combine("artifacts", "part3_feature_importance.csv");

            return new PipelineRunRequest(
                Mode: PipelineMode.Diagnostics,
                RawArgs: rawArgs,
                ValidationWindowDays: null,
                EnablePfi: enablePfi,
                PfiHorizonStep: pfiHorizonStep,
                MaxPart3Rows: maxPart3Rows,
                InputPaths: new Dictionary<string, string>
                {
                    ["part2DatasetCsv"] = diagnosticsInputPath,
                    ["part3PredictionsCsv"] = diagnosticsPredictionsPath,
                    ["part3FeatureImportanceCsv"] = diagnosticsPfiPath
                },
                OutputPaths: new Dictionary<string, string>
                {
                    ["diagnosticsDirectory"] = diagnosticsOutputDirectory
                });
        }

        if (string.Equals(positionalArgs[0], "part4", StringComparison.OrdinalIgnoreCase))
        {
            var part4InputPath = positionalArgs.Length > 1 ? positionalArgs[1] : Path.Combine("artifacts", "part2_supervised_matrix.csv");
            var part4PredictionsPath = positionalArgs.Length > 2 ? positionalArgs[2] : Path.Combine("artifacts", "part3_predictions.csv");
            var part4MetricsOutputPath = positionalArgs.Length > 3 ? positionalArgs[3] : Path.Combine("artifacts", "part4_metrics.csv");
            var part4SampleOutputPath = positionalArgs.Length > 4 ? positionalArgs[4] : Path.Combine("artifacts", "part4_pred_vs_actual_sample.csv");

            return new PipelineRunRequest(
                Mode: PipelineMode.Part4,
                RawArgs: rawArgs,
                ValidationWindowDays: null,
                EnablePfi: enablePfi,
                PfiHorizonStep: pfiHorizonStep,
                MaxPart3Rows: maxPart3Rows,
                InputPaths: new Dictionary<string, string>
                {
                    ["part2DatasetCsv"] = part4InputPath,
                    ["part3PredictionsCsv"] = part4PredictionsPath
                },
                OutputPaths: new Dictionary<string, string>
                {
                    ["part4MetricsCsv"] = part4MetricsOutputPath,
                    ["part4SampleCsv"] = part4SampleOutputPath
                });
        }

        if (string.Equals(positionalArgs[0], "part3", StringComparison.OrdinalIgnoreCase))
        {
            var part3InputPath = positionalArgs.Length > 1 ? positionalArgs[1] : Path.Combine("artifacts", "part2_supervised_matrix.csv");
            var part3OutputPath = positionalArgs.Length > 2 ? positionalArgs[2] : Path.Combine("artifacts", "part3_predictions.csv");
            var part3SummaryPath = positionalArgs.Length > 3
                ? positionalArgs[3]
                : Path.Combine(
                    Path.GetDirectoryName(part3OutputPath) ?? "artifacts",
                    $"{Path.GetFileNameWithoutExtension(part3OutputPath)}.summary.json");

            return new PipelineRunRequest(
                Mode: PipelineMode.Part3,
                RawArgs: rawArgs,
                ValidationWindowDays: null,
                EnablePfi: enablePfi,
                PfiHorizonStep: pfiHorizonStep,
                MaxPart3Rows: maxPart3Rows,
                InputPaths: new Dictionary<string, string>
                {
                    ["part2DatasetCsv"] = part3InputPath
                },
                OutputPaths: new Dictionary<string, string>
                {
                    ["part3PredictionsCsv"] = part3OutputPath,
                    ["part3SummaryJson"] = part3SummaryPath,
                    ["part3FeatureImportanceCsv"] = Path.Combine(Path.GetDirectoryName(part3OutputPath) ?? "artifacts", "part3_feature_importance.csv")
                });
        }

        if (string.Equals(positionalArgs[0], "part2", StringComparison.OrdinalIgnoreCase))
        {
            var part2InputPath = positionalArgs.Length > 1 ? positionalArgs[1] : Path.Combine("artifacts", "part1_feature_matrix.csv");
            var part2OutputPath = positionalArgs.Length > 2 ? positionalArgs[2] : Path.Combine("artifacts", "part2_supervised_matrix.csv");
            var part2SummaryPath = positionalArgs.Length > 3
                ? positionalArgs[3]
                : Path.Combine(
                    Path.GetDirectoryName(part2OutputPath) ?? "artifacts",
                    $"{Path.GetFileNameWithoutExtension(part2OutputPath)}.summary.json");
            var part2ValidationWindowDays = positionalArgs.Length > 4
                ? ParseValidationWindowDays(positionalArgs[4])
                : PipelineConstants.DefaultValidationWindowDays;

            return new PipelineRunRequest(
                Mode: PipelineMode.Part2,
                RawArgs: rawArgs,
                ValidationWindowDays: part2ValidationWindowDays,
                EnablePfi: enablePfi,
                PfiHorizonStep: pfiHorizonStep,
                MaxPart3Rows: maxPart3Rows,
                InputPaths: new Dictionary<string, string>
                {
                    ["part1FeatureMatrix"] = part2InputPath
                },
                OutputPaths: new Dictionary<string, string>
                {
                    ["part2DatasetCsv"] = part2OutputPath,
                    ["part2SummaryJson"] = part2SummaryPath
                });
        }

        if (string.Equals(positionalArgs[0], "part1", StringComparison.OrdinalIgnoreCase))
        {
            var dataPath = positionalArgs.Length > 1 ? positionalArgs[1] : Path.Combine("data", "testdata.csv");
            var holidaysPath = positionalArgs.Length > 2 ? positionalArgs[2] : Path.Combine("data", "holidays.public.csv");
            var outputPath = positionalArgs.Length > 3 ? positionalArgs[3] : Path.Combine("artifacts", "part1_feature_matrix.csv");
            var auditOutputPath = positionalArgs.Length > 4
                ? positionalArgs[4]
                : Path.Combine(
                    Path.GetDirectoryName(outputPath) ?? "artifacts",
                    $"{Path.GetFileNameWithoutExtension(outputPath)}.audit.csv");
            var auditSummaryOutputPath = positionalArgs.Length > 5
                ? positionalArgs[5]
                : Path.Combine(
                    Path.GetDirectoryName(outputPath) ?? "artifacts",
                    $"{Path.GetFileNameWithoutExtension(outputPath)}.audit.summary.json");
            var validationWindowDays = positionalArgs.Length > 6
                ? ParseValidationWindowDays(positionalArgs[6])
                : PipelineConstants.DefaultValidationWindowDays;

            return new PipelineRunRequest(
                Mode: PipelineMode.Part1,
                RawArgs: rawArgs,
                ValidationWindowDays: validationWindowDays,
                EnablePfi: enablePfi,
                PfiHorizonStep: pfiHorizonStep,
                MaxPart3Rows: maxPart3Rows,
                InputPaths: new Dictionary<string, string>
                {
                    ["data"] = dataPath,
                    ["holidays"] = holidaysPath
                },
                OutputPaths: new Dictionary<string, string>
                {
                    ["part1FeatureMatrix"] = outputPath,
                    ["part1AuditCsv"] = auditOutputPath,
                    ["part1AuditSummaryJson"] = auditSummaryOutputPath
                });
        }

        return null;
    }

    private static int ParseValidationWindowDays(string value)
    {
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
        {
            throw new ArgumentException($"Invalid validation window days value '{value}'. Expected a positive integer.");
        }

        return parsed;
    }

    private static string[] ExtractPositionalArgs(string[] args)
    {
        var optionsWithValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--pfi-horizon",
            "--max-rows"
        };

        var positional = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            var token = args[index];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(token);
                continue;
            }

            var equalsIndex = token.IndexOf('=');
            var optionName = equalsIndex >= 0 ? token[..equalsIndex] : token;
            if (optionsWithValues.Contains(optionName) && equalsIndex < 0 && index + 1 < args.Length)
            {
                index++;
            }
        }

        return positional.ToArray();
    }

    private static int? ParseOptionalPfiHorizonStep(string[] args)
    {
        const string flag = "--pfi-horizon";
        int? parsedValue = null;

        for (var index = 0; index < args.Length; index++)
        {
            var token = args[index];
            if (token.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
            {
                if (parsedValue.HasValue)
                {
                    throw new ArgumentException($"Duplicate '{flag}' option.");
                }

                parsedValue = ParsePfiHorizonValue(token[(flag.Length + 1)..]);
                continue;
            }

            if (!string.Equals(token, flag, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (parsedValue.HasValue)
            {
                throw new ArgumentException($"Duplicate '{flag}' option.");
            }

            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for '{flag}'. Expected an integer from 1 to {PipelineConstants.HorizonSteps}.");
            }

            parsedValue = ParsePfiHorizonValue(args[++index]);
        }

        return parsedValue;
    }

    private static int ParsePfiHorizonValue(string value)
    {
        if (!int.TryParse(value, out var parsed) || parsed < 1 || parsed > PipelineConstants.HorizonSteps)
        {
            throw new ArgumentException($"Invalid --pfi-horizon value '{value}'. Expected an integer from 1 to {PipelineConstants.HorizonSteps}.");
        }

        return parsed;
    }

    private static int? ParseOptionalMaxPart3Rows(string[] args)
    {
        const string flag = "--max-rows";
        int? parsedValue = null;

        for (var index = 0; index < args.Length; index++)
        {
            var token = args[index];
            if (token.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
            {
                if (parsedValue.HasValue)
                {
                    throw new ArgumentException($"Duplicate '{flag}' option.");
                }

                parsedValue = ParseMaxPart3RowsValue(token[(flag.Length + 1)..]);
                continue;
            }

            if (!string.Equals(token, flag, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (parsedValue.HasValue)
            {
                throw new ArgumentException($"Duplicate '{flag}' option.");
            }

            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for '{flag}'. Expected an integer >= 2.");
            }

            parsedValue = ParseMaxPart3RowsValue(args[++index]);
        }

        return parsedValue;
    }

    private static int ParseMaxPart3RowsValue(string value)
    {
        if (!int.TryParse(value, out var parsed) || parsed < 2)
        {
            throw new ArgumentException($"Invalid --max-rows value '{value}'. Expected an integer >= 2.");
        }

        return parsed;
    }
}
