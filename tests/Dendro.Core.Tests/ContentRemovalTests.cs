using System.Text.Json.Nodes;
using Dendro.Core;
using Dendro.Core.Configuration;

namespace Dendro.Core.Tests;

public class ContentRemovalTests
{
    private static JsonObject Edge(string parent, string child) => new() { ["source_path"] = parent, ["path"] = child };
    private static JsonObject Reference(string path) => new() { ["path"] = path };

    [Fact]
    public void RemovingParentCascadesAcrossStrataReportsChartsAndAnalyses()
    {
        var project = ProjectStore.Create("Remoção");
        var meta = project.Obj("_dendro");
        meta["files"] = new JsonArray("input.xlsx", "stratum.dlb", "nested.dlb", "report.dlb", "report-child.dlb", "other.xlsx");
        meta["derived_tables"] = new JsonArray(Edge("input.xlsx", "stratum.dlb"), Edge("stratum.dlb", "nested.dlb"), Edge("report.dlb", "report-child.dlb"));
        meta["processing_results"] = new JsonArray(Edge("input.xlsx", "report.dlb"));
        foreach (string section in new[] { "file_options", "charts", "content_names" })
            meta[section] = new JsonArray(Reference("input.xlsx"), Reference("nested.dlb"), Reference("report-child.dlb"), Reference("other.xlsx"));
        meta["analyses"] = new JsonArray(Reference("report.dlb"), Reference("other.xlsx"),
            new JsonObject { ["definition"] = new JsonObject { ["chart"] = Reference("nested.dlb") } },
            new JsonObject { ["title"] = "Curva sem fonte tabular" });
        meta["batch_inputs"] = new JsonArray("input.xlsx", "nested.dlb", "other.xlsx");
        meta["input"] = "input.xlsx";
        ProjectModels.RemoveFile(project, 0);
        Assert.Equal(new[] { "other.xlsx" }, meta.Arr("files").Select(f => f.Display()));
        Assert.Empty(meta.Arr("derived_tables")); Assert.Empty(meta.Arr("processing_results"));
        foreach (string section in new[] { "file_options", "charts", "content_names" })
            Assert.Equal("other.xlsx", Assert.Single(meta.Arr(section)).S("path"));
        Assert.Equal(2, meta.Arr("analyses").Count);
        Assert.Equal("other.xlsx", Assert.Single(meta.Arr("batch_inputs")).Display());
        Assert.Equal("", meta.S("input"));
        Assert.NotNull(project["cadastro"]);
    }

    [Fact]
    public void RemovingChildPreservesParentAndSibling()
    {
        var project = ProjectStore.Create("Remoção"); var meta = project.Obj("_dendro");
        meta["files"] = new JsonArray("parent", "child", "grandchild", "sibling");
        meta["derived_tables"] = new JsonArray(Edge("parent", "child"), Edge("child", "grandchild"), Edge("parent", "sibling"));
        ProjectModels.RemoveFile(project, 1);
        Assert.Equal(new[] { "parent", "sibling" }, meta.Arr("files").Select(f => f.Display()));
        Assert.Equal("sibling", Assert.Single(meta.Arr("derived_tables")).S("path"));
    }

    [Fact]
    public void CyclesDuplicatesAndWindowsPathVariationsTerminate()
    {
        var project = ProjectStore.Create("Remoção"); var meta = project.Obj("_dendro");
        meta["files"] = new JsonArray("Data/Parent.dlb", "Data/Child.dlb", "unrelated");
        meta["derived_tables"] = new JsonArray(Edge("DATA\\PARENT.DLB", "data\\child.dlb"),
            Edge("data/child.dlb", "data/parent.dlb"), Edge("data/parent.dlb", "data/child.dlb"));
        ProjectModels.RemoveFile(project, 0);
        Assert.Equal("unrelated", Assert.Single(meta.Arr("files")).Display());
        Assert.Empty(meta.Arr("derived_tables"));
    }

    [Fact]
    public void RemovalPreservesPhysicalFilesAndInvalidIndexDoesNotMutateProject()
    {
        string file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "dados preservados");
            var project = ProjectStore.Create("Remoção"); project.Obj("_dendro")["files"] = new JsonArray(file);
            string before = project.ToJsonString();
            Assert.Throws<InvalidDataException>(() => ProjectModels.RemoveFile(project, -1));
            Assert.Equal(before, project.ToJsonString());
            ProjectModels.RemoveFile(project, 0);
            Assert.Empty(project.Obj("_dendro").Arr("files"));
            Assert.Equal("dados preservados", File.ReadAllText(file));
        }
        finally { File.Delete(file); }
    }
}
