namespace Forecasting.App;

/// <summary>
/// Single source of truth for pipeline-wide constants shared across all parts.
/// </summary>
public static class PipelineConstants
{
    public const int HorizonSteps = 192;
    public const int MinutesPerStep = 15;
    public const int DefaultValidationWindowDays = 30;
}

/// <summary>
/// Feature engineering knobs: lag depths and rolling window sizes used in Part 2 and Part 3.
/// </summary>
public static class FeatureConfig
{
    public const int TargetLag192 = 192;
    public const int TargetLag672 = 672;
    public const int RollingWindow16 = 16;
    public const int RollingWindow96 = 96;
}

/// <summary>
/// ML hyperparameters for the FastTree recursive model, passed as a value so callers can override defaults.
/// </summary>
public sealed record FastTreeOptions(
    int NumberOfTrees = 10,
    int NumberOfLeaves = 4,
    int MinimumExampleCountPerLeaf = 50,
    double LearningRate = 0.05,
    int Seed = 42);
