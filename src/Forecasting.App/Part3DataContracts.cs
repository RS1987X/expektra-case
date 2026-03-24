// Output data types produced by Part3 modeling (forecasts, summaries, PFI results).
namespace Forecasting.App;

public sealed record Part3ForecastRow(
    DateTime AnchorUtcTime,
    string Split,
    string ModelName,
    // BaselineSeasonal: seasonal-key misses that use global-mean fallback.
    // FastTreeRecursive: recursive inference steps beyond the anchor timestamp.
    // Meaning depends on model, just used for bookkeeping not governing behaviour.
    int FallbackOrRecursiveSteps,
    IReadOnlyList<double> PredictedTargets,
    IReadOnlyList<double> ActualTargets);

public sealed record Part3ModelSummary(
    string ModelName,
    int AnchorsForecasted,
    int HorizonSteps,
    int FallbackOrRecursiveSteps);

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

public sealed record Part3PfiSeedDetail(
    int Seed,
    string FeatureName,
    int Rank,
    double MaeDelta);

public sealed record Part3PfiResult(
    IReadOnlyList<Part3PfiFeatureResult> Features,
    int PermutationCount,
    int EvaluationRowCount,
    int HorizonStep,
    IReadOnlyList<Part3PfiSeedDetail>? PerSeedDetails = null);
