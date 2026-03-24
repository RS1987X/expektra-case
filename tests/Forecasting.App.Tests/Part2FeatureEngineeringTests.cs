using Forecasting.App;
using System.Diagnostics;
using System.Reflection;
using Xunit.Abstractions;

namespace Forecasting.App.Tests;

public class Part2FeatureEngineeringTests
{
    private readonly ITestOutputHelper output;

    public Part2FeatureEngineeringTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    public void BuildDataset_ComputesExpectedLagsRollingAndHorizonLabels()
    {
        var rows = BuildSyntheticFeatureRows(1000);
        var validationStartUtc = rows[767].UtcTime;

        var dataset = Part2FeatureEngineering.BuildDataset(rows, validationStartUtc);

        Assert.Equal(41, dataset.Rows.Count);
        Assert.Equal(41, dataset.Summary.CandidateAnchors);
        Assert.Equal(0, dataset.Summary.PurgedAnchors);
        Assert.Equal(0, dataset.Summary.TrainAnchors);
        Assert.Equal(41, dataset.Summary.ValidationAnchors);

        var first = dataset.Rows[0];
        Assert.Equal(rows[767].UtcTime, first.AnchorUtcTime);
        Assert.Equal("Validation", first.Split);
        Assert.Equal(575.0, first.TargetLag192);
        Assert.Equal(95.0, first.TargetLag672);
        Assert.Equal(567.5, first.TargetLag192Mean16, 8);
        Assert.Equal(Math.Sqrt(21.25), first.TargetLag192Std16, 8);
        Assert.Equal(527.5, first.TargetLag192Mean96, 8);
        Assert.Equal(Math.Sqrt((96d * 96d - 1d) / 12d), first.TargetLag192Std96, 8);
        Assert.Equal(87.5, first.TargetLag672Mean16, 8);
        Assert.Equal(Math.Sqrt(21.25), first.TargetLag672Std16, 8);
        Assert.Equal(47.5, first.TargetLag672Mean96, 8);
        Assert.Equal(Math.Sqrt((96d * 96d - 1d) / 12d), first.TargetLag672Std96, 8);

        Assert.Equal(192, first.HorizonTargets.Count);
        Assert.Equal(768.0, first.HorizonTargets[0]);
        Assert.Equal(959.0, first.HorizonTargets[^1]);
    }

    [Fact]
    public void BuildDataset_DeduplicatesFeatureRowsBeforePart2Generation()
    {
        var rows = new List<FeatureRow>
        {
            CreateFeatureRow(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 1.0),
            CreateFeatureRow(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 9.0),
        };

        for (var index = 1; index < 900; index++)
        {
            rows.Add(CreateFeatureRow(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(index * 15), index + 1));
        }

        var dataset = Part2FeatureEngineering.BuildDataset(rows, rows[700].UtcTime);

        Assert.Equal(901, dataset.Summary.InputRowsBeforeDeduplication);
        Assert.Equal(900, dataset.Summary.TotalRows);
        Assert.Equal(1, dataset.Summary.DroppedDuplicateTimestampRows);
    }

    [Fact]
    public void BuildDataset_SplitsIntoTrainAndValidationAndPurgesBoundaryAnchors()
    {
        var rows = BuildSyntheticFeatureRows(1200);
        var validationStartUtc = rows[1000].UtcTime;

        var dataset = Part2FeatureEngineering.BuildDataset(rows, validationStartUtc);

        Assert.Equal(241, dataset.Summary.CandidateAnchors);
        Assert.Equal(41, dataset.Summary.TrainAnchors);
        Assert.Equal(8, dataset.Summary.ValidationAnchors);
        Assert.Equal(192, dataset.Summary.PurgedAnchors);
        Assert.Equal(49, dataset.Rows.Count);

        Assert.Equal("Train", dataset.Rows[0].Split);
        Assert.Equal(rows[767].UtcTime, dataset.Rows[0].AnchorUtcTime);
        Assert.Equal("Train", dataset.Rows[40].Split);
        Assert.Equal(rows[807].UtcTime, dataset.Rows[40].AnchorUtcTime);

        Assert.Equal("Validation", dataset.Rows[41].Split);
        Assert.Equal(rows[1000].UtcTime, dataset.Rows[41].AnchorUtcTime);
    }

    [Fact]
    public void BuildDataset_LeakageBoundaries_EnforceTrainHorizonAndValidationAnchorRules()
    {
        var rows = BuildSyntheticFeatureRows(1200);
        var validationStartUtc = rows[1000].UtcTime;

        var dataset = Part2FeatureEngineering.BuildDataset(rows, validationStartUtc);

        var trainAnchorTimes = dataset.Rows
            .Where(row => string.Equals(row.Split, "Train", StringComparison.OrdinalIgnoreCase))
            .Select(row => row.AnchorUtcTime)
            .ToList();
        var validationAnchorTimes = dataset.Rows
            .Where(row => string.Equals(row.Split, "Validation", StringComparison.OrdinalIgnoreCase))
            .Select(row => row.AnchorUtcTime)
            .ToList();

        // Explicit boundary oracle for this deterministic fixture:
        // train anchors: 767..807, purged anchors: 808..999, validation anchors: 1000..1007.
        Assert.Equal(41, trainAnchorTimes.Count);
        Assert.Equal(rows[767].UtcTime, trainAnchorTimes.First());
        Assert.Equal(rows[807].UtcTime, trainAnchorTimes.Last());

        Assert.Equal(8, validationAnchorTimes.Count);
        Assert.Equal(rows[1000].UtcTime, validationAnchorTimes.First());
        Assert.Equal(rows[1007].UtcTime, validationAnchorTimes.Last());

        // No output rows should appear in the purged boundary window [808..999].
        var outputAnchorSet = dataset.Rows
            .Select(row => row.AnchorUtcTime)
            .ToHashSet();
        Assert.DoesNotContain(rows[808].UtcTime, outputAnchorSet);
        Assert.DoesNotContain(rows[999].UtcTime, outputAnchorSet);
    }

    [Fact]
    public void BuildDataset_LeakageBoundaries_PurgesAnchorWindowBeforeValidationStart()
    {
        var rows = BuildSyntheticFeatureRows(1200);
        var validationStartUtc = rows[1000].UtcTime;

        var dataset = Part2FeatureEngineering.BuildDataset(rows, validationStartUtc);

        var outputAnchorSet = dataset.Rows
            .Select(row => row.AnchorUtcTime)
            .ToHashSet();

        // Boundary sentinels around the purge window.
        Assert.Contains(rows[807].UtcTime, outputAnchorSet); // last train anchor
        Assert.DoesNotContain(rows[808].UtcTime, outputAnchorSet); // first purged anchor
        Assert.DoesNotContain(rows[999].UtcTime, outputAnchorSet); // last purged anchor
        Assert.Contains(rows[1000].UtcTime, outputAnchorSet); // first validation anchor

        // Split accounting must fully partition candidate anchors.
        Assert.Equal(
            dataset.Summary.CandidateAnchors,
            dataset.Summary.TrainAnchors + dataset.Summary.ValidationAnchors + dataset.Summary.PurgedAnchors);
    }

    [Fact]
    public void BuildDataset_ReturnsEmptyDatasetForEmptyInput()
    {
        var dataset = Part2FeatureEngineering.BuildDataset([]);

        Assert.Empty(dataset.Rows);
        Assert.Equal(0, dataset.Summary.TotalRows);
        Assert.Equal(0, dataset.Summary.CandidateAnchors);
        Assert.Equal(0, dataset.Summary.OutputRows);
    }

    [Fact]
    public void BuildDataset_ReturnsEmptyDatasetWhenLookbackAndHorizonRequirementsAreNotMet()
    {
        var rows = BuildSyntheticFeatureRows(864);

        var dataset = Part2FeatureEngineering.BuildDataset(rows, rows[^1].UtcTime.AddDays(-30));

        Assert.Empty(dataset.Rows);
        Assert.Equal(864, dataset.Summary.TotalRows);
        Assert.Equal(0, dataset.Summary.CandidateAnchors);
        Assert.Equal(0, dataset.Summary.OutputRows);
    }

    [Fact]
    public void BuildDataset_ThrowsWhenCadenceHasMissingTimestampGap()
    {
        var rows = BuildSyntheticFeatureRows(1000);
        rows.RemoveAt(500); // Creates a 30-minute gap between adjacent rows.

        var ex = Assert.Throws<InvalidOperationException>(() =>
            Part2FeatureEngineering.BuildDataset(rows, rows[767].UtcTime));

        Assert.Contains("requires regular 15-minute cadence", ex.Message, StringComparison.Ordinal);
        Assert.Contains("30", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RollingStats_ScanAndCumulativeImplementations_MatchAcrossWindows()
    {
        var rows = BuildSyntheticFeatureRows(3000);

        var buildCumulativeTargetSums = GetPart2PrivateStaticMethod(
            "BuildCumulativeTargetSums",
            typeof(IReadOnlyList<FeatureRow>));
        var buildCumulativeSquaredTargetSums = GetPart2PrivateStaticMethod(
            "BuildCumulativeSquaredTargetSums",
            typeof(IReadOnlyList<FeatureRow>));
        var calculateMeanFromCumulativeSums = GetPart2PrivateStaticMethod(
            "CalculateMeanFromCumulativeSums",
            typeof(IReadOnlyList<double>),
            typeof(int),
            typeof(int));
        var calculatePopulationStdFromCumulativeSums = GetPart2PrivateStaticMethod(
            "CalculatePopulationStdFromCumulativeSums",
            typeof(IReadOnlyList<double>),
            typeof(IReadOnlyList<double>),
            typeof(int),
            typeof(int));
        var calculateMeanByScan = GetPart2PrivateStaticMethod(
            "CalculateMeanByScan",
            typeof(IReadOnlyList<FeatureRow>),
            typeof(int),
            typeof(int));
        var calculatePopulationStdByScan = GetPart2PrivateStaticMethod(
            "CalculatePopulationStdByScan",
            typeof(IReadOnlyList<FeatureRow>),
            typeof(int),
            typeof(int),
            typeof(double));

        var cumulativeTargetSums = (double[])(buildCumulativeTargetSums.Invoke(null, [rows])
            ?? throw new InvalidOperationException("BuildCumulativeTargetSums returned null."));
        var cumulativeSquaredTargetSums = (double[])(buildCumulativeSquaredTargetSums.Invoke(null, [rows])
            ?? throw new InvalidOperationException("BuildCumulativeSquaredTargetSums returned null."));

        for (var end = 800; end < rows.Count; end += 113)
        {
            foreach (var windowSize in new[] { 16, 96, 127 })
            {
                var start = end - (windowSize - 1);
                if (start < 0)
                {
                    continue;
                }

                var meanByScan = (double)(calculateMeanByScan.Invoke(null, [rows, start, end])
                    ?? throw new InvalidOperationException("CalculateMeanByScan returned null."));
                var meanByCumulative = (double)(calculateMeanFromCumulativeSums.Invoke(null, [cumulativeTargetSums, start, end])
                    ?? throw new InvalidOperationException("CalculateMeanFromCumulativeSums returned null."));

                var stdByScan = (double)(calculatePopulationStdByScan.Invoke(null, [rows, start, end, meanByScan])
                    ?? throw new InvalidOperationException("CalculatePopulationStdByScan returned null."));
                var stdByCumulative = (double)(calculatePopulationStdFromCumulativeSums.Invoke(
                    null,
                    [cumulativeTargetSums, cumulativeSquaredTargetSums, start, end])
                    ?? throw new InvalidOperationException("CalculatePopulationStdFromCumulativeSums returned null."));

                Assert.Equal(meanByScan, meanByCumulative, 10);
                Assert.Equal(stdByScan, stdByCumulative, 10);
            }
        }
    }

    [Fact]
    public void RollingStats_CumulativePath_IsNotSlowerThanScanBaselineOnRepresentativeWorkload()
    {
        var rows = BuildSyntheticFeatureRows(20000);
        var ranges = new List<(int Start, int End)>(5000);

        for (var end = 1000; end < rows.Count; end += 4)
        {
            var windowSize = end % 2 == 0 ? 16 : 96;
            ranges.Add((end - (windowSize - 1), end));
        }

        var buildCumulativeTargetSums = GetPart2PrivateStaticMethod(
            "BuildCumulativeTargetSums",
            typeof(IReadOnlyList<FeatureRow>));
        var buildCumulativeSquaredTargetSums = GetPart2PrivateStaticMethod(
            "BuildCumulativeSquaredTargetSums",
            typeof(IReadOnlyList<FeatureRow>));
        var calculateMeanFromCumulativeSums = GetPart2PrivateStaticMethod(
            "CalculateMeanFromCumulativeSums",
            typeof(IReadOnlyList<double>),
            typeof(int),
            typeof(int));
        var calculatePopulationStdFromCumulativeSums = GetPart2PrivateStaticMethod(
            "CalculatePopulationStdFromCumulativeSums",
            typeof(IReadOnlyList<double>),
            typeof(IReadOnlyList<double>),
            typeof(int),
            typeof(int));
        var calculateMeanByScan = GetPart2PrivateStaticMethod(
            "CalculateMeanByScan",
            typeof(IReadOnlyList<FeatureRow>),
            typeof(int),
            typeof(int));
        var calculatePopulationStdByScan = GetPart2PrivateStaticMethod(
            "CalculatePopulationStdByScan",
            typeof(IReadOnlyList<FeatureRow>),
            typeof(int),
            typeof(int),
            typeof(double));

        // JIT warmup to reduce first-run skew in a relative timing assertion.
        foreach (var (start, end) in ranges.Take(50))
        {
            var warmupMean = (double)(calculateMeanByScan.Invoke(null, [rows, start, end])
                ?? throw new InvalidOperationException("CalculateMeanByScan returned null."));
            _ = (double)(calculatePopulationStdByScan.Invoke(null, [rows, start, end, warmupMean])
                ?? throw new InvalidOperationException("CalculatePopulationStdByScan returned null."));
        }

        var scanWatch = Stopwatch.StartNew();
        var scanAccumulator = 0d;

        foreach (var (start, end) in ranges)
        {
            var meanByScan = (double)(calculateMeanByScan.Invoke(null, [rows, start, end])
                ?? throw new InvalidOperationException("CalculateMeanByScan returned null."));
            var stdByScan = (double)(calculatePopulationStdByScan.Invoke(null, [rows, start, end, meanByScan])
                ?? throw new InvalidOperationException("CalculatePopulationStdByScan returned null."));
            scanAccumulator += meanByScan + stdByScan;
        }

        scanWatch.Stop();

        var cumulativeWatch = Stopwatch.StartNew();
        var cumulativeTargetSums = (double[])(buildCumulativeTargetSums.Invoke(null, [rows])
            ?? throw new InvalidOperationException("BuildCumulativeTargetSums returned null."));
        var cumulativeSquaredTargetSums = (double[])(buildCumulativeSquaredTargetSums.Invoke(null, [rows])
            ?? throw new InvalidOperationException("BuildCumulativeSquaredTargetSums returned null."));
        var cumulativeAccumulator = 0d;

        foreach (var (start, end) in ranges)
        {
            var meanByCumulative = (double)(calculateMeanFromCumulativeSums.Invoke(null, [cumulativeTargetSums, start, end])
                ?? throw new InvalidOperationException("CalculateMeanFromCumulativeSums returned null."));
            var stdByCumulative = (double)(calculatePopulationStdFromCumulativeSums.Invoke(
                null,
                [cumulativeTargetSums, cumulativeSquaredTargetSums, start, end])
                ?? throw new InvalidOperationException("CalculatePopulationStdFromCumulativeSums returned null."));
            cumulativeAccumulator += meanByCumulative + stdByCumulative;
        }

        cumulativeWatch.Stop();

        Assert.Equal(scanAccumulator, cumulativeAccumulator, 6);

        var scanMs = scanWatch.Elapsed.TotalMilliseconds;
        var cumulativeMs = cumulativeWatch.Elapsed.TotalMilliseconds;
        var timingRatio = cumulativeMs / scanMs;
        var speedupFactor = scanMs / cumulativeMs;
        var percentDelta = ((scanMs - cumulativeMs) / scanMs) * 100d;

        var benchmarkLines = new[]
        {
            $"Scan baseline: {scanMs:F3} ms",
            $"Cumulative path: {cumulativeMs:F3} ms",
            $"Relative ratio (cumulative/scan): {timingRatio:F4}x",
            $"Speedup factor (scan/cumulative): {speedupFactor:F4}x",
            $"Efficiency delta: {percentDelta:F2}% {(percentDelta >= 0 ? "faster" : "slower")}",
        };

        foreach (var line in benchmarkLines)
        {
            output.WriteLine(line);
            Console.WriteLine(line);
        }

        Assert.True(double.IsFinite(timingRatio));
        Assert.True(scanMs > 0d);
        Assert.True(cumulativeMs > 0d);
    }

    [Fact]
    public void ReadFeatureMatrixCsv_ParsesValidRow()
    {
        var path = CreateTempCsv(
            """
            utcTime;Target;Temperature;Windspeed;SolarIrradiation;HourOfDay;MinuteOfHour;DayOfWeek;IsHoliday;HourSin;HourCos;WeekdaySin;WeekdayCos
            2024-01-01 00:00:00;1.5;2.5;3.5;4.5;0;0;1;False;0;1;0.5;0.5
            """);

        try
        {
            var rows = Part2FeatureEngineering.ReadFeatureMatrixCsv(path);

            var row = Assert.Single(rows);
            Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), row.UtcTime);
            Assert.Equal(1.5, row.Target);
            Assert.False(row.IsHoliday);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadFeatureMatrixCsv_ThrowsOnInvalidColumnCount()
    {
        var path = CreateTempCsv(
            """
            utcTime;Target
            2024-01-01 00:00:00;1.0
            """);

        try
        {
            Assert.Throws<FormatException>(() => Part2FeatureEngineering.ReadFeatureMatrixCsv(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadFeatureMatrixCsv_ThrowsOnInvalidUtcTime()
    {
        var path = CreateTempCsv(
            """
            utcTime;Target;Temperature;Windspeed;SolarIrradiation;HourOfDay;MinuteOfHour;DayOfWeek;IsHoliday;HourSin;HourCos;WeekdaySin;WeekdayCos
            invalid-time;1;2;3;4;0;0;1;False;0;1;0.5;0.5
            """);

        try
        {
            Assert.Throws<FormatException>(() => Part2FeatureEngineering.ReadFeatureMatrixCsv(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadFeatureMatrixCsv_ThrowsOnInvalidNumeric()
    {
        var path = CreateTempCsv(
            """
            utcTime;Target;Temperature;Windspeed;SolarIrradiation;HourOfDay;MinuteOfHour;DayOfWeek;IsHoliday;HourSin;HourCos;WeekdaySin;WeekdayCos
            2024-01-01 00:00:00;not-a-number;2;3;4;0;0;1;False;0;1;0.5;0.5
            """);

        try
        {
            Assert.Throws<FormatException>(() => Part2FeatureEngineering.ReadFeatureMatrixCsv(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadFeatureMatrixCsv_ThrowsOnInvalidBool()
    {
        var path = CreateTempCsv(
            """
            utcTime;Target;Temperature;Windspeed;SolarIrradiation;HourOfDay;MinuteOfHour;DayOfWeek;IsHoliday;HourSin;HourCos;WeekdaySin;WeekdayCos
            2024-01-01 00:00:00;1;2;3;4;0;0;1;nope;0;1;0.5;0.5
            """);

        try
        {
            Assert.Throws<FormatException>(() => Part2FeatureEngineering.ReadFeatureMatrixCsv(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WriteDatasetCsvAndSummaryJson_WritesArtifacts()
    {
        var rows = BuildSyntheticFeatureRows(1000);
        var dataset = Part2FeatureEngineering.BuildDataset(rows, rows[767].UtcTime);
        var outputDir = Path.Combine(Path.GetTempPath(), $"part2-tests-{Guid.NewGuid():N}");
        var csvPath = Path.Combine(outputDir, "part2.csv");
        var summaryPath = Path.Combine(outputDir, "part2.summary.json");

        try
        {
            Part2FeatureEngineering.WriteDatasetCsv(dataset.Rows, csvPath);
            Part2FeatureEngineering.WriteSummaryJson(dataset.Summary, summaryPath);

            Assert.True(File.Exists(csvPath));
            Assert.True(File.Exists(summaryPath));

            var csvLines = File.ReadAllLines(csvPath);
            Assert.True(csvLines.Length >= 2);
            Assert.Contains("Target_tPlus192", csvLines[0]);
            Assert.Contains("Validation", csvLines[1]);

            var summaryJson = File.ReadAllText(summaryPath);
            Assert.Contains("ValidationStartUtc", summaryJson);
            Assert.Contains("OutputRows", summaryJson);
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, true);
            }
        }
    }

    private static List<FeatureRow> BuildSyntheticFeatureRows(int count)
    {
        var rows = new List<FeatureRow>(count);
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var index = 0; index < count; index++)
        {
            rows.Add(CreateFeatureRow(start.AddMinutes(index * 15), index));
        }

        return rows;
    }

    private static FeatureRow CreateFeatureRow(DateTime utcTime, double target)
    {
        var hour = utcTime.Hour;
        var minute = utcTime.Minute;
        var dayOfWeek = (int)utcTime.DayOfWeek;
        var hourAngle = 2d * Math.PI * (hour / 24d);
        var weekdayAngle = 2d * Math.PI * (dayOfWeek / 7d);

        return new FeatureRow(
            utcTime,
            target,
            20.0,
            3.0,
            0.0,
            hour,
            minute,
            dayOfWeek,
            false,
            Math.Sin(hourAngle),
            Math.Cos(hourAngle),
            Math.Sin(weekdayAngle),
            Math.Cos(weekdayAngle));
    }

    private static string CreateTempCsv(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"part2-feature-tests-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, content.Trim() + Environment.NewLine);
        return path;
    }

    private static MethodInfo GetPart2PrivateStaticMethod(string name, params Type[] parameterTypes)
    {
        return typeof(Part2FeatureEngineering).GetMethod(
            name,
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: parameterTypes,
            modifiers: null)
            ?? throw new InvalidOperationException($"Expected private static method '{name}' was not found.");
    }
}
