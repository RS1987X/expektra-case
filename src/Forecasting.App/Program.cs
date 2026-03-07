using Forecasting.App;

if (args.Length == 0 || (args.Length > 0 && string.Equals(args[0], "all", StringComparison.OrdinalIgnoreCase)))
{
	var allValidationWindowDays = args.Length > 1
		? ParseValidationWindowDays(args[1])
		: PipelineConstants.DefaultValidationWindowDays;

	var dataPath = Path.Combine("data", "testdata.csv");
	var holidaysPath = Path.Combine("data", "holidays.public.csv");
	var part1OutputPath = Path.Combine("artifacts", "part1_feature_matrix.csv");
	var part1AuditOutputPath = Path.Combine("artifacts", "part1_feature_matrix.audit.csv");
	var part1AuditSummaryOutputPath = Path.Combine("artifacts", "part1_feature_matrix.audit.summary.json");
	var part2OutputPath = Path.Combine("artifacts", "part2_supervised_matrix.csv");
	var part2SummaryPath = Path.Combine("artifacts", "part2_supervised_matrix.summary.json");
	var part3OutputPath = Path.Combine("artifacts", "part3_predictions.csv");
	var part3SummaryPath = Path.Combine("artifacts", "part3_predictions.summary.json");
	var part3PfiOutputPath = Path.Combine("artifacts", "part3_feature_importance.csv");
	var part4MetricsOutputPath = Path.Combine("artifacts", "part4_metrics.csv");
	var part4SampleOutputPath = Path.Combine("artifacts", "part4_pred_vs_actual_sample.csv");
	var diagnosticsOutputDirectory = Path.Combine("artifacts", "diagnostics");

	if (!File.Exists(dataPath) || !File.Exists(holidaysPath))
	{
		Console.WriteLine("Input files not found.");
		Console.WriteLine($"Data path: {dataPath}");
		Console.WriteLine($"Holidays path: {holidaysPath}");
		return;
	}

	Console.WriteLine("Running full pipeline (Part 1 -> Part 4 + diagnostics)...");

	var preprocessed = Part1Preprocessing.BuildPreprocessedDatasetForEvaluation(dataPath, holidaysPath, allValidationWindowDays);
	Part1Preprocessing.WriteFeatureMatrixCsv(preprocessed.PersistedFeatures, part1OutputPath);
	Part1Preprocessing.WritePreprocessingAuditCsv(preprocessed.AuditEvents, part1AuditOutputPath);
	Part1Preprocessing.WritePreprocessingSummaryJson(preprocessed.AuditSummary, part1AuditSummaryOutputPath);
	Console.WriteLine($"Part 1 complete: {preprocessed.PersistedFeatures.Count} rows -> {part1OutputPath} (validation window: {allValidationWindowDays} days)");

	var part1Rows = Part2FeatureEngineering.ReadFeatureMatrixCsv(part1OutputPath);
	var allValidationStartUtc = part1Rows.Max(row => row.UtcTime).AddDays(-allValidationWindowDays);
	var part2Dataset = Part2FeatureEngineering.BuildDataset(part1Rows, allValidationStartUtc);
	Part2FeatureEngineering.WriteDatasetCsv(part2Dataset.Rows, part2OutputPath);
	Part2FeatureEngineering.WriteSummaryJson(part2Dataset.Summary, part2SummaryPath);
	Console.WriteLine($"Part 2 complete: {part2Dataset.Summary.OutputRows} rows -> {part2OutputPath} (validation window: {allValidationWindowDays} days)");

	var part3Rows = Part3Modeling.ReadPart2DatasetCsv(part2OutputPath);
	var part3Result = Part3Modeling.RunModels(part3Rows);
	Part3Modeling.WriteForecastsCsv(part3Result.Forecasts, part3OutputPath);
	Part3Modeling.WriteSummaryJson(part3Result.Summary, part3SummaryPath);
	if (part3Result.FeatureImportance is not null)
	{
		Part3Modeling.WriteFeatureImportanceCsv(part3Result.FeatureImportance, part3PfiOutputPath);
		Console.WriteLine($"Part 3 PFI complete: {part3Result.FeatureImportance.Features.Count} features -> {part3PfiOutputPath}");
	}
	Console.WriteLine($"Part 3 complete: {part3Result.Forecasts.Count} forecasts -> {part3OutputPath}");

	var part4Result = Part4Evaluation.RunEvaluation(part2OutputPath, part3OutputPath);
	Part4Evaluation.WriteMetricsCsv(part4Result, part4MetricsOutputPath);
	Part4Evaluation.WriteSampleCsv(part4Result, part4SampleOutputPath);
	Console.WriteLine($"Part 4 complete: metrics -> {part4MetricsOutputPath}");

	var diagnosticsResult = PartDiagnostics.RunDiagnostics(part2OutputPath, part3OutputPath, part3PfiOutputPath);
	PartDiagnostics.WriteArtifacts(diagnosticsResult, diagnosticsOutputDirectory);
	Console.WriteLine($"Diagnostics complete: {diagnosticsOutputDirectory}");
	return;
}

if (args.Length > 0 && string.Equals(args[0], "diagnostics", StringComparison.OrdinalIgnoreCase))
{
	var diagnosticsInputPath = args.Length > 1 ? args[1] : Path.Combine("artifacts", "part2_supervised_matrix.csv");
	var diagnosticsPredictionsPath = args.Length > 2 ? args[2] : Path.Combine("artifacts", "part3_predictions.csv");
	var diagnosticsOutputDirectory = args.Length > 3 ? args[3] : Path.Combine("artifacts", "diagnostics");
	var diagnosticsPfiPath = args.Length > 4 ? args[4] : Path.Combine("artifacts", "part3_feature_importance.csv");

	if (!File.Exists(diagnosticsInputPath))
	{
		Console.WriteLine("Diagnostics input file not found.");
		Console.WriteLine($"Input path: {diagnosticsInputPath}");
		return;
	}

	if (!File.Exists(diagnosticsPredictionsPath))
	{
		Console.WriteLine("Diagnostics predictions file not found.");
		Console.WriteLine($"Predictions path: {diagnosticsPredictionsPath}");
		return;
	}

	var diagnosticsResult = PartDiagnostics.RunDiagnostics(diagnosticsInputPath, diagnosticsPredictionsPath, diagnosticsPfiPath);
	PartDiagnostics.WriteArtifacts(diagnosticsResult, diagnosticsOutputDirectory);

	Console.WriteLine("Diagnostics residual summary:");
	Console.WriteLine("Model\tSplit\tMeanError\tMAE\tRMSE\tUnder%\tPoints");
	foreach (var summary in diagnosticsResult.ResidualSummaries
	             .OrderBy(summary => summary.ModelName, StringComparer.Ordinal)
	             .ThenBy(summary => summary.Split, StringComparer.Ordinal))
	{
		Console.WriteLine($"{summary.ModelName}\t{summary.Split}\t{summary.MeanError:F6}\t{summary.Mae:F6}\t{summary.Rmse:F6}\t{summary.UnderPredictionRate:F6}\t{summary.EvaluatedPoints}");
	}

	Console.WriteLine($"Saved diagnostics artifacts to: {diagnosticsOutputDirectory}");
	return;
}

if (args.Length > 0 && string.Equals(args[0], "part4", StringComparison.OrdinalIgnoreCase))
{
	var part4InputPath = args.Length > 1 ? args[1] : Path.Combine("artifacts", "part2_supervised_matrix.csv");
	var part4PredictionsPath = args.Length > 2 ? args[2] : Path.Combine("artifacts", "part3_predictions.csv");
	var part4MetricsOutputPath = args.Length > 3 ? args[3] : Path.Combine("artifacts", "part4_metrics.csv");
	var part4SampleOutputPath = args.Length > 4 ? args[4] : Path.Combine("artifacts", "part4_pred_vs_actual_sample.csv");

	if (!File.Exists(part4InputPath))
	{
		Console.WriteLine("Part 4 input file not found.");
		Console.WriteLine($"Input path: {part4InputPath}");
		return;
	}

	if (!File.Exists(part4PredictionsPath))
	{
		Console.WriteLine("Part 4 predictions file not found.");
		Console.WriteLine($"Predictions path: {part4PredictionsPath}");
		return;
	}

	var part4Result = Part4Evaluation.RunEvaluation(part4InputPath, part4PredictionsPath);
	Part4Evaluation.WriteMetricsCsv(part4Result, part4MetricsOutputPath);
	Part4Evaluation.WriteSampleCsv(part4Result, part4SampleOutputPath);

	Console.WriteLine("Part 4 model comparison:");
	Console.WriteLine("Model\tMAE\tRMSE\tMAPE\tPoints\tMAPEPoints");
	foreach (var metric in part4Result.Metrics.OrderBy(metric => metric.ModelName, StringComparer.Ordinal))
	{
		Console.WriteLine($"{metric.ModelName}\t{metric.Mae:F6}\t{metric.Rmse:F6}\t{metric.Mape:F6}\t{metric.EvaluatedPoints}\t{metric.MapeEvaluatedPoints}");
	}

	Console.WriteLine($"Saved Part 4 metrics to: {part4MetricsOutputPath}");
	Console.WriteLine($"Saved Part 4 sample to: {part4SampleOutputPath}");
	return;
}

if (args.Length > 0 && string.Equals(args[0], "part3", StringComparison.OrdinalIgnoreCase))
{
	var part3InputPath = args.Length > 1 ? args[1] : Path.Combine("artifacts", "part2_supervised_matrix.csv");
	var part3OutputPath = args.Length > 2 ? args[2] : Path.Combine("artifacts", "part3_predictions.csv");
	var part3SummaryPath = args.Length > 3
		? args[3]
		: Path.Combine(
			Path.GetDirectoryName(part3OutputPath) ?? "artifacts",
			$"{Path.GetFileNameWithoutExtension(part3OutputPath)}.summary.json");

	if (!File.Exists(part3InputPath))
	{
		Console.WriteLine("Part 3 input file not found.");
		Console.WriteLine($"Input path: {part3InputPath}");
		return;
	}

	var part3Rows = Part3Modeling.ReadPart2DatasetCsv(part3InputPath);
	var part3Result = Part3Modeling.RunModels(part3Rows);
	Part3Modeling.WriteForecastsCsv(part3Result.Forecasts, part3OutputPath);
	Part3Modeling.WriteSummaryJson(part3Result.Summary, part3SummaryPath);
	if (part3Result.FeatureImportance is not null)
	{
		var part3PfiPath = Path.Combine(
			Path.GetDirectoryName(part3OutputPath) ?? "artifacts",
			"part3_feature_importance.csv");
		Part3Modeling.WriteFeatureImportanceCsv(part3Result.FeatureImportance, part3PfiPath);
		Console.WriteLine($"Saved Part 3 PFI to: {part3PfiPath}");
	}

	Console.WriteLine($"Part 3 models: {string.Join(", ", part3Result.Summary.Models.Select(model => model.ModelName))}");
	Console.WriteLine($"Generated {part3Result.Forecasts.Count} forecast rows.");
	Console.WriteLine($"Saved Part 3 predictions to: {part3OutputPath}");
	Console.WriteLine($"Saved Part 3 summary to: {part3SummaryPath}");
	return;
}

if (args.Length > 0 && string.Equals(args[0], "part2", StringComparison.OrdinalIgnoreCase))
{
	var part2InputPath = args.Length > 1 ? args[1] : Path.Combine("artifacts", "part1_feature_matrix.csv");
	var part2OutputPath = args.Length > 2 ? args[2] : Path.Combine("artifacts", "part2_supervised_matrix.csv");
	var part2SummaryPath = args.Length > 3
		? args[3]
		: Path.Combine(
			Path.GetDirectoryName(part2OutputPath) ?? "artifacts",
			$"{Path.GetFileNameWithoutExtension(part2OutputPath)}.summary.json");
	var part2ValidationWindowDays = args.Length > 4
		? ParseValidationWindowDays(args[4])
		: PipelineConstants.DefaultValidationWindowDays;

	if (!File.Exists(part2InputPath))
	{
		Console.WriteLine("Part 2 input file not found.");
		Console.WriteLine($"Input path: {part2InputPath}");
		return;
	}

	var part1Rows = Part2FeatureEngineering.ReadFeatureMatrixCsv(part2InputPath);
	var part2ValidationStartUtc = part1Rows.Max(row => row.UtcTime).AddDays(-part2ValidationWindowDays);
	var part2Dataset = Part2FeatureEngineering.BuildDataset(part1Rows, part2ValidationStartUtc);
	Part2FeatureEngineering.WriteDatasetCsv(part2Dataset.Rows, part2OutputPath);
	Part2FeatureEngineering.WriteSummaryJson(part2Dataset.Summary, part2SummaryPath);

	Console.WriteLine($"Part 2 validation split starts at: {part2Dataset.Summary.ValidationStartUtc:yyyy-MM-dd HH:mm:ss} UTC");
	Console.WriteLine($"Part 2 validation window: {part2ValidationWindowDays} days");
	Console.WriteLine($"Generated {part2Dataset.Summary.OutputRows} Part 2 supervised rows.");
	Console.WriteLine($"Saved Part 2 dataset to: {part2OutputPath}");
	Console.WriteLine($"Saved Part 2 summary to: {part2SummaryPath}");
	return;
}

if (args.Length > 0 && string.Equals(args[0], "part1", StringComparison.OrdinalIgnoreCase))
{
	var dataPath = args.Length > 1 ? args[1] : Path.Combine("data", "testdata.csv");
	var holidaysPath = args.Length > 2 ? args[2] : Path.Combine("data", "holidays.public.csv");
	var outputPath = args.Length > 3 ? args[3] : Path.Combine("artifacts", "part1_feature_matrix.csv");
	var auditOutputPath = args.Length > 4
		? args[4]
		: Path.Combine(
			Path.GetDirectoryName(outputPath) ?? "artifacts",
			$"{Path.GetFileNameWithoutExtension(outputPath)}.audit.csv");
	var auditSummaryOutputPath = args.Length > 5
		? args[5]
		: Path.Combine(
			Path.GetDirectoryName(outputPath) ?? "artifacts",
			$"{Path.GetFileNameWithoutExtension(outputPath)}.audit.summary.json");
	var validationWindowDays = args.Length > 6
		? ParseValidationWindowDays(args[6])
		: PipelineConstants.DefaultValidationWindowDays;

	if (!File.Exists(dataPath) || !File.Exists(holidaysPath))
	{
		Console.WriteLine("Input files not found.");
		Console.WriteLine($"Data path: {dataPath}");
		Console.WriteLine($"Holidays path: {holidaysPath}");
		return;
	}

	var preprocessed = Part1Preprocessing.BuildPreprocessedDatasetForEvaluation(dataPath, holidaysPath, validationWindowDays);
	Part1Preprocessing.WriteFeatureMatrixCsv(preprocessed.PersistedFeatures, outputPath);
	Part1Preprocessing.WritePreprocessingAuditCsv(preprocessed.AuditEvents, auditOutputPath);
	Part1Preprocessing.WritePreprocessingSummaryJson(preprocessed.AuditSummary, auditSummaryOutputPath);
	Console.WriteLine($"Validation split starts at: {preprocessed.AuditSummary.ValidationStartUtc:yyyy-MM-dd HH:mm:ss} UTC");
	Console.WriteLine($"Generated {preprocessed.PersistedFeatures.Count} persisted feature rows.");
	Console.WriteLine($"Saved feature matrix to: {outputPath}");
	Console.WriteLine($"Saved preprocessing audit events to: {auditOutputPath}");
	Console.WriteLine($"Saved preprocessing summary to: {auditSummaryOutputPath}");
	return;
}

Console.WriteLine("Unknown mode. Use one of: all, part1, part2, part3, part4, diagnostics.");

static int ParseValidationWindowDays(string value)
{
	if (!int.TryParse(value, out var parsed) || parsed <= 0)
	{
		throw new ArgumentException($"Invalid validation window days value '{value}'. Expected a positive integer.");
	}

	return parsed;
}
