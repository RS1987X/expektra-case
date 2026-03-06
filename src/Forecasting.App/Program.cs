using Forecasting.App;

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

	if (!File.Exists(part2InputPath))
	{
		Console.WriteLine("Part 2 input file not found.");
		Console.WriteLine($"Input path: {part2InputPath}");
		return;
	}

	var part1Rows = Part2FeatureEngineering.ReadFeatureMatrixCsv(part2InputPath);
	var part2Dataset = Part2FeatureEngineering.BuildDataset(part1Rows);
	Part2FeatureEngineering.WriteDatasetCsv(part2Dataset.Rows, part2OutputPath);
	Part2FeatureEngineering.WriteSummaryJson(part2Dataset.Summary, part2SummaryPath);

	Console.WriteLine($"Part 2 validation split starts at: {part2Dataset.Summary.ValidationStartUtc:yyyy-MM-dd HH:mm:ss} UTC");
	Console.WriteLine($"Generated {part2Dataset.Summary.OutputRows} Part 2 supervised rows.");
	Console.WriteLine($"Saved Part 2 dataset to: {part2OutputPath}");
	Console.WriteLine($"Saved Part 2 summary to: {part2SummaryPath}");
	return;
}

var dataPath = args.Length > 0 ? args[0] : Path.Combine("data", "testdata.csv");
var holidaysPath = args.Length > 1 ? args[1] : Path.Combine("data", "holidays.public.csv");
var outputPath = args.Length > 2 ? args[2] : Path.Combine("artifacts", "part1_feature_matrix.csv");
var auditOutputPath = args.Length > 3
	? args[3]
	: Path.Combine(
		Path.GetDirectoryName(outputPath) ?? "artifacts",
		$"{Path.GetFileNameWithoutExtension(outputPath)}.audit.csv");
var auditSummaryOutputPath = args.Length > 4
	? args[4]
	: Path.Combine(
		Path.GetDirectoryName(outputPath) ?? "artifacts",
		$"{Path.GetFileNameWithoutExtension(outputPath)}.audit.summary.json");

if (!File.Exists(dataPath) || !File.Exists(holidaysPath))
{
	Console.WriteLine("Input files not found.");
	Console.WriteLine($"Data path: {dataPath}");
	Console.WriteLine($"Holidays path: {holidaysPath}");
	return;
}

var preprocessed = Part1Preprocessing.BuildPreprocessedDatasetForEvaluation(dataPath, holidaysPath);
Part1Preprocessing.WriteFeatureMatrixCsv(preprocessed.PersistedFeatures, outputPath);
Part1Preprocessing.WritePreprocessingAuditCsv(preprocessed.AuditEvents, auditOutputPath);
Part1Preprocessing.WritePreprocessingSummaryJson(preprocessed.AuditSummary, auditSummaryOutputPath);
Console.WriteLine($"Validation split starts at: {preprocessed.AuditSummary.ValidationStartUtc:yyyy-MM-dd HH:mm:ss} UTC");
Console.WriteLine($"Generated {preprocessed.PersistedFeatures.Count} persisted feature rows.");
Console.WriteLine($"Saved feature matrix to: {outputPath}");
Console.WriteLine($"Saved preprocessing audit events to: {auditOutputPath}");
Console.WriteLine($"Saved preprocessing summary to: {auditSummaryOutputPath}");
