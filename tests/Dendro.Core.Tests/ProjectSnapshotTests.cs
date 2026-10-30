using System.Text.Json;
using System.Text.Json.Nodes;
using Dendro.Core.Configuration;
using Dendro.Core.Regression;
using Dendro.Core.Statistics;

namespace Dendro.Core.Tests;

public class ProjectSnapshotTests
{
    [Fact]
    public void OpensProjectLargerThanOldSixteenMegabyteLimit()
    {
        string path = Path.Combine(Path.GetTempPath(), "large-project-" + Guid.NewGuid().ToString("N") + ".dendro");
        try
        {
            var project = ProjectStore.Create("Projeto grande");
            project["anotacoes"] = new string('A', 17 * 1024 * 1024);
            ProjectStore.Save(path, project);
            Assert.True(new FileInfo(path).Length > 16 * 1024 * 1024);
            var restored = ProjectStore.Read(path);
            Assert.Equal(17 * 1024 * 1024, restored["anotacoes"]!.GetValue<string>().Length);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CompressesLegacyPlotsAndDetailsWithoutLosingInformation()
    {
        var report = GrowthDiagnostics.Report([new("Curva", "b0*t", [2d])], 1, 10, false);
        var entry = new JsonObject { ["id"] = "a1", ["plot_snapshot"] = JsonSerializer.SerializeToNode(report.Plot),
            ["details_snapshot"] = report.Details.ToJson(), ["summary"] = report.Summary.DeepClone() };
        string plot = entry["plot_snapshot"]!.ToJsonString(), details = entry["details_snapshot"]!.ToJsonString();
        int oldLength = entry.ToJsonString().Length;
        ProjectSnapshots.CompactEntry(entry);
        Assert.Null(entry["plot_snapshot"]);
        Assert.True(ProjectSnapshots.HasPlot(entry));
        Assert.True(entry.ToJsonString().Length < oldLength / 2);
        var restored = ProjectSnapshots.Read(entry);
        Assert.Equal(plot, restored["plot_snapshot"]!.ToJsonString());
        Assert.Equal(details, restored["details_snapshot"]!.ToJsonString());
        var chart = JsonSerializer.Deserialize<ChartResult>(restored["plot_snapshot"]!.ToJsonString())!;
        Assert.Equal(2, chart.Curves.Length);
        string compact = entry.ToJsonString(); ProjectSnapshots.CompactEntry(entry);
        Assert.Equal(compact, entry.ToJsonString());
    }

    [Fact]
    public void SavesAndReopensCompressedAnalysisSnapshots()
    {
        string path = Path.Combine(Path.GetTempPath(), "snapshot-project-" + Guid.NewGuid().ToString("N") + ".dendro");
        try
        {
            var project = ProjectStore.Create("Resultados");
            var entry = new JsonObject { ["plot_snapshot"] = new JsonObject { ["name"] = "ICA/IMA" },
                ["details_snapshot"] = new JsonObject { ["texto"] = new string('B', 100000) } };
            project["_dendro"]!["analyses"] = new JsonArray(entry);
            ProjectStore.Save(path, project);
            var restored = ProjectStore.Read(path);
            var snapshot = ProjectSnapshots.Read(restored["_dendro"]!["analyses"]![0]!.AsObject());
            Assert.Equal("ICA/IMA", snapshot["plot_snapshot"]!["name"]!.GetValue<string>());
            Assert.Equal(100000, snapshot["details_snapshot"]!["texto"]!.GetValue<string>().Length);
        }
        finally { File.Delete(path); }
    }
}
