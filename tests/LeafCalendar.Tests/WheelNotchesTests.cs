using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public class WheelNotchesTests
{
    [Fact]
    public void Add_OneFullNotch_StepsOnce()
    {
        var notches = new WheelNotches();

        Assert.Equal(1, notches.Add(120));
        Assert.Equal(-1, notches.Add(-120));
    }

    [Fact]
    public void Add_SmoothWheelDeltas_StepOncePerFullNotch()
    {
        var notches = new WheelNotches();

        Assert.Equal(0, notches.Add(40));
        Assert.Equal(0, notches.Add(40));
        Assert.Equal(1, notches.Add(40));
        Assert.Equal(0, notches.Add(60));
        Assert.Equal(1, notches.Add(60));
    }

    [Fact]
    public void Add_ZeroDelta_DoesNothing()
    {
        var notches = new WheelNotches();

        Assert.Equal(0, notches.Add(0));
        Assert.Equal(0, notches.Add(119));
        Assert.Equal(1, notches.Add(1));
    }

    [Fact]
    public void Add_LargeDelta_StepsSeveralTimesAndKeepsTheRest()
    {
        var notches = new WheelNotches();

        Assert.Equal(-2, notches.Add(-300));
        Assert.Equal(-1, notches.Add(-60));
    }
}
