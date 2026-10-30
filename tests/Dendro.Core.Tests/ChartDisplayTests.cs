using System.Text.Json.Nodes;
using System.Xml.Linq;
using Dendro.Core.Regression;

namespace Dendro.Core.Tests;

public class ChartDisplayTests
{
    [Fact]
    public void AutomaticBoundsUseVisibleValuesAndRespectManualLimits()
    {
        var config = new JsonObject();
        var bounds = ChartBounds.Calculate([(1d, 2d), (3d, 4d)], config);
        Assert.Equal(.92, bounds.XMin, 10);
        Assert.Equal(3.08, bounds.XMax, 10);
        config["xmin"] = 0;
        config["xmax"] = 10;
        bounds = ChartBounds.Calculate([(1d, 2d), (3d, 4d)], config);
        Assert.Equal(0, bounds.XMin);
        Assert.Equal(10, bounds.XMax);
    }

    [Fact]
    public void SvgHidesRemovedByDefaultAndShowsThemAsCrossesOnRequest()
    {
        string path = Path.Combine(Path.GetTempPath(), "dendro-display-" + Guid.NewGuid().ToString("N") + ".svg");
        try
        {
            var chart = new JsonObject { ["title"] = "Teste", ["width"] = 800, ["height"] = 500 };
            var result = new ChartResult(chart, [new(1, 2, 1, "Global"), new(2, 3, 2, "Global"), new(100, 100, 3, "Global", true)], [], 0);
            XNamespace ns = "http://www.w3.org/2000/svg";
            ChartExport.Svg(path, result);
            var svg = XDocument.Load(path);
            Assert.Equal(2, svg.Descendants(ns + "circle").Count());
            Assert.All(svg.Descendants(ns + "circle"), p => Assert.Equal("1", p.Attribute("opacity")?.Value));
            Assert.DoesNotContain(svg.Descendants(ns + "path"), p => p.Attribute("stroke")?.Value == "#b8bfc7");
            chart["show_removed"] = true;
            chart["point_alpha"] = .25;
            ChartExport.Svg(path, result);
            svg = XDocument.Load(path);
            Assert.Equal(2, svg.Descendants(ns + "circle").Count());
            Assert.Single(svg.Descendants(ns + "path"), p => p.Attribute("stroke")?.Value == "#b8bfc7");
            Assert.All(svg.Descendants(ns + "circle"), p => Assert.Equal("0.25", p.Attribute("opacity")?.Value));
        }
        finally { File.Delete(path); }
    }
}
