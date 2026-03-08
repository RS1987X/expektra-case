// Output data types produced by Part3 modeling (forecasts, summaries, PFI results).
namespace Forecasting.App;

public sealed record Part3ForecastRow(
    DateTime AnchorUtcTime,
    string Split,
    string ModelName,
    int ExogenousFallbackSteps,
    IReadOnlyList<double> PredictedTargets,
    IReadOnlyList<double> ActualTargets);

public sealed record Part3ModelSummary(
    string ModelName,
    int AnchorsForecasted,
    int HorizonSteps,
    int ExogenousFallbackSteps);

public sealed record Part3RunSummary(
    DateTime GeneratedAtUtc,
    int InputRows,
    int TrainRows,
    int ValidationRows,
    IReadOnlyList<Part3ModelSummary> Models);

public sealed record Part3RunResult(
    IReadOnlyList<Part3ForecastRow> Forecasts,
    Part3RunSummary Summary,
    Part3PfiResult? FeatureImportance);

public sealed record Part3PfiFeatureResult(
    int Rank,
    string FeatureName,
    double MaeDelta,
    double MaeDeltaStdDev,
    double RmseDelta,
    double RmseDeltaStdDev,
    double R2Delta,
    double R2DeltaStdDev);

public sealed record Part3PfiResult(
    IReadOnlyList<Part3PfiFeatureResult> Features,
    int PermutationCount,
    int EvaluationRowCount,
    int HorizonStep);
