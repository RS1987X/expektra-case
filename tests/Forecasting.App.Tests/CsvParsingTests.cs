using Forecasting.App;

namespace Forecasting.App.Tests;

public class CsvParsingTests
{
    [Fact]
    public void FindRequiredColumnIndex_MissingColumn_ThrowsExpectedMessage()
    {
        var ex = Assert.Throws<FormatException>(() =>
            CsvParsing.FindRequiredColumnIndex(["A", "B"], "C", "predictions CSV"));

        Assert.Equal("Missing required column 'C' in predictions CSV.", ex.Message);
    }

    [Fact]
    public void ParseRequiredDouble_InvalidText_ThrowsExpectedMessage()
    {
        var ex = Assert.Throws<FormatException>(() =>
            CsvParsing.ParseRequiredDouble("not-a-number", 7, "Pred_tPlus1"));

        Assert.Equal("Invalid Pred_tPlus1 at line 7: 'not-a-number'.", ex.Message);
    }

    [Fact]
    public void ParseRequiredDouble_NonFinite_WhenRejected_ThrowsExpectedMessage()
    {
        var ex = Assert.Throws<FormatException>(() =>
            CsvParsing.ParseRequiredDouble("NaN", 9, "Pred_tPlus4", rejectNonFinite: true));

        Assert.Equal("Invalid Pred_tPlus4 at line 9: non-finite value 'NaN'.", ex.Message);
    }

    [Fact]
    public void ParseRequiredInt_InvalidText_ThrowsExpectedMessage()
    {
        var ex = Assert.Throws<FormatException>(() =>
            CsvParsing.ParseRequiredInt("not-an-int", 3, "HourOfDay"));

        Assert.Equal("Invalid HourOfDay at line 3: 'not-an-int'.", ex.Message);
    }

    [Fact]
    public void ParseRequiredBool_InvalidText_ThrowsExpectedMessage()
    {
        var ex = Assert.Throws<FormatException>(() =>
            CsvParsing.ParseRequiredBool("not-bool", 5, "IsHoliday"));

        Assert.Equal("Invalid IsHoliday at line 5: 'not-bool'.", ex.Message);
    }

    [Fact]
    public void ParseRequiredUtcDateTime_InvalidText_ThrowsExpectedMessage()
    {
        var ex = Assert.Throws<FormatException>(() =>
            CsvParsing.ParseRequiredUtcDateTime("nope", 11, "anchorUtcTime"));

        Assert.Equal("Invalid anchorUtcTime at line 11: 'nope'.", ex.Message);
    }
}
