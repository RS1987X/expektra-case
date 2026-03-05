using Forecasting.App;

var dataPath = args.Length > 0 ? args[0] : Path.Combine("data", "testdata.csv");
var holidaysPath = args.Length > 1 ? args[1] : Path.Combine("data", "holidays.public.csv");

if (!File.Exists(dataPath) || !File.Exists(holidaysPath))
{
	Console.WriteLine("Input files not found.");
	Console.WriteLine($"Data path: {dataPath}");
	Console.WriteLine($"Holidays path: {holidaysPath}");
	return;
}

var features = Part1Preprocessing.BuildFeatureMatrix(dataPath, holidaysPath);
Console.WriteLine($"Generated {features.Count} feature rows.");
