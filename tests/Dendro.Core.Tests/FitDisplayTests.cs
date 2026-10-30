using System.Text.Json.Nodes;
using System.Xml.Linq;
using Dendro.Core;
using Dendro.Core.Regression;

namespace Dendro.Core.Tests;

public class FitDisplayTests
{
    private static JsonObject Chart() =>
        new()
        {
            ["title"] = "Comparação",
            ["fits"] = new JsonArray(
                new JsonObject
                {
                    ["converged"] = true,
                    ["model"] = "Linear",
                    ["group"] = "A",
                },
                new JsonObject
                {
                    ["converged"] = true,
                    ["model"] = "Quadrático",
                    ["group"] = "A",
                },
                new JsonObject
                {
                    ["converged"] = false,
                    ["model"] = "Cúbico",
                    ["group"] = "A",
                },
                new JsonObject
                {
                    ["converged"] = true,
                    ["model"] = "Linear",
                    ["group"] = "B",
                }
            ),
        };

    [Fact]
    public void MultipleSelectionOverridesLegacyAndIgnoresInvalidOrFailedFits()
    {
        var chart = Chart();
        chart["selected_fit"] = 3;
        chart["selected_fits"] = new JsonArray(0, 1, 2, 100, -1, 0);
        Assert.Equal([0, 1], FitDisplay.Visible(chart));
        chart["selected_fits"] = new JsonArray();
        Assert.Empty(FitDisplay.Visible(chart));
    }

    [Fact]
    public void LegacyAndDefaultSelectionsRemainCompatible()
    {
        var chart = Chart();
        Assert.Equal([0, 3], FitDisplay.Visible(chart));
        chart["selected_fit"] = 1;
        Assert.Equal([1], FitDisplay.Visible(chart));
        Assert.NotEqual(FitDisplay.Color(chart["fits"]![0]!), FitDisplay.Color(chart["fits"]![1]!));
    }

    [Fact]
    public void SvgExportsBothSelectedCurvesAboveScatterPoints()
    {
        var chart = Chart();
        chart["selected_fits"] = new JsonArray(0, 1);
        var result = new ChartResult(
            chart,
            [new(1, 1, 1, "A"), new(3, 3, 2, "A")],
            [
                [
                    [1, 1],
                    [3, 3],
                ],
                [
                    [1, 1.3],
                    [3, 2.7],
                ],
                [],
                [
                    [1, 1],
                    [3, 3],
                ],
            ],
            0
        );
        string path = Path.Combine(
            Path.GetTempPath(),
            "DENDRO-fit-" + Guid.NewGuid().ToString("N") + ".svg"
        );
        try
        {
            ChartExport.Svg(path, result);
            var document = XDocument.Load(path);
            XNamespace ns = "http://www.w3.org/2000/svg";
            var elements = document.Descendants(ns + "g").Single().Elements().ToList();
            var lines = elements
                .Where(e => e.Name == ns + "path" && e.Attribute("stroke-width") != null)
                .ToArray();
            Assert.Equal(2, lines.Length);
            Assert.Equal(2, lines.Select(l => l.Attribute("stroke")!.Value).Distinct().Count());
            Assert.All(
                lines,
                line =>
                    Assert.True(
                        elements.IndexOf(line)
                            > elements.FindLastIndex(e => e.Name == ns + "circle")
                    )
            );
        }
        finally
        {
            File.Delete(path);
        }
    }
}
