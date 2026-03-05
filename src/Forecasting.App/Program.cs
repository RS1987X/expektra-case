using Forecasting.App;

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
