using OrientPyx.BusinessLogic.Models;
using Xunit;

namespace OrientPyx.Tests.Core;

/// <summary>The fixed test slip printed from the print-settings dialog keeps the values the operator checks it by.</summary>
public sealed class SplitPrintSampleTests
{
    [Fact]
    public void SampleSlipHasTheExpectedRunnerAndPassage()
    {
        var doc = SplitPrintSample.Create();

        Assert.Equal("Колесник Вадим", doc.FullName);
        Assert.Equal("Ч21Е", doc.GroupName);
        Assert.Equal("8000067", doc.ChipNumber);
        Assert.Equal("12:34:56", doc.StartClock);
        Assert.Equal("13:04:33.0", doc.FinishClock);
        Assert.Equal("0:29:37", doc.ResultText);
        Assert.Equal("OK", doc.StatusText);
        Assert.Equal(3, doc.Place);
        Assert.Equal(7, doc.ReadOutCount);

        Assert.Equal(["31", "32", "33", "34", "35", "36", "37", "100", "F"], doc.Rows.Select(r => r.Code));
        Assert.Equal(["1", "2", "3", "4", "5", "6", "7", "8", ""], doc.Rows.Select(r => r.Index));
        Assert.All(doc.Rows, r => Assert.True(r.OnCourse));
        Assert.All(doc.Rows, r => Assert.NotEmpty(r.DistanceText));
        Assert.All(doc.Rows, r => Assert.NotEmpty(r.PaceText));
        Assert.False(doc.HasPoints);
        Assert.Equal("29:37", doc.Rows[^1].ElapsedText);
        Assert.NotEmpty(doc.TotalDistanceText);
        Assert.NotEmpty(doc.AvgPaceText);
    }
}
