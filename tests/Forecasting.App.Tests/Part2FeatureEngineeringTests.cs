using Forecasting.App;

namespace Forecasting.App.Tests;

public class Part2FeatureEngineeringTests
{
    [Fact]
    public void BuildDataset_ComputesExpectedLagsRollingAndHorizonLabels()
    {
        var rows = BuildSyntheticFeatureRows(900);
        var validationStartUtc = rows[700].UtcTime;

        var dataset = Part2FeatureEngineering.BuildDataset(rows, validationStartUtc);

        Assert.Equal(8, dataset.Rows.Count);
        Assert.Equal(36, dataset.Summary.CandidateAnchors);
        Assert.Equal(28, dataset.Summary.PurgedAnchors);
        Assert.Equal(0, dataset.Summary.TrainAnchors);
        Assert.Equal(8, dataset.Summary.ValidationAnchors);

        var first = dataset.Rows[0];
        Assert.Equal(rows[700].UtcTime, first.AnchorUtcTime);
        Assert.Equal("Validation", first.Split);
        Assert.Equal(508.0, first.TargetLag192);
        Assert.Equal(28.0, first.TargetLag672);
        Assert.Equal(692.5, first.TargetMean16, 8);
        Assert.Equal(Math.Sqrt(21.25), first.TargetStd16, 8);
        Assert.Equal(652.5, first.TargetMean96, 8);
        Assert.Equal(Math.Sqrt((96d * 96d - 1d) / 12d), first.TargetStd96, 8);

        Assert.Equal(192, first.HorizonTargets.Count);
        Assert.Equal(701.0, first.HorizonTargets[0]);
        Assert.Equal(892.0, first.HorizonTargets[^1]);
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
}
