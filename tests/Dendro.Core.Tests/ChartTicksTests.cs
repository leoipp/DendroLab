using Dendro.Core.Regression;

namespace Dendro.Core.Tests;

public sealed class ChartTicksTests
{
    [Fact]
    public void OrdinaryScalesPreferIntegersAndFractionalScalesKeepDistinctLabels()
    {
        var ordinary=ChartTicks.Create(-.4,10.4);
        Assert.All(ordinary,t=>Assert.Equal(Math.Round(t.Value),t.Value));
        var small=ChartTicks.Create(.001,.008);
        Assert.True(small.Length>=3);Assert.Equal(small.Length,small.Select(t=>t.Label).Distinct().Count());
        var manual=ChartTicks.Create(0,10,2);
        Assert.Equal(new[]{0d,2d,4d,6d,8d,10d},manual.Select(t=>t.Value));
        Assert.Throws<InvalidDataException>(()=>ChartTicks.Create(0,100,.001));
        Assert.All(ChartTicks.Create(0,3,1,true),t=>Assert.Equal(Math.Pow(10,t.Position),t.Value));
    }
}
