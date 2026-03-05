using Forecasting.App;

var dataPath = args.Length > 0 ? args[0] : Path.Combine("data", "testdata.csv");
var holidaysPath = args.Length > 1 ? args[1] : Path.Combine("data", "holidays.public.csv");
var outputPath = args.Length > 2 ? args[2] : Path.Combine("artifacts", "part1_feature_matrix.csv");

if (!File.Exists(dataPath) || !File.Exists(holidaysPath))
{
	Console.WriteLine("Input files not found.");
	Console.WriteLine($"Data path: {dataPath}");
	Console.WriteLine($"Holidays path: {holidaysPath}");
	return;
}

var features = Part1Preprocessing.BuildFeatureMatrix(dataPath, holidaysPath);
Part1Preprocessing.WriteFeatureMatrixCsv(features, outputPath);
Console.WriteLine($"Generated {features.Count} feature rows.");
Console.WriteLine($"Saved feature matrix to: {outputPath}");
