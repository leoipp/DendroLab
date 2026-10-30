using System.Text.Json.Nodes;
using Dendro.Core.Tabular;

namespace Dendro.Core.Tests;

public sealed class IntegratedCleaningTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"dendro-clean-tests-"+Guid.NewGuid().ToString("N"));
    public IntegratedCleaningTests()=>Directory.CreateDirectory(root);
    private string FilePath(string name)=>Path.Combine(root,name);
    private static JsonObject Chart(string y)=>new(){["x"]="X",["y"]=y,["title"]=y+" × X"};
    private string Source()
    {
        string file=FilePath("source.dlb");using var w=new CompactBankWriter(file);string id=w.AddTable(["ID","X","Y","Z","Notas"]);
        w.Add(id,["A",1d,2d,3d,"igual"]);w.Add(id,["A",1d,2d,3d,"igual"]);
        w.Add(id,["B",2d,null,8d,"sem Y"]);w.Add(id,["C",null,5d,null,"sem pares"]);w.Complete();return file;
    }

    [Fact]
    public void SharedMaskUsesPhysicalRowsKeepsInvalidRowsAndExportsAllColumns()
    {
        string source=Source();using var clean=IntegratedCleaning.Create(new(source),[Chart("Y"),Chart("Z")],FilePath("snapshot.dlb"));
        Assert.Equal(4,clean.RowCount);Assert.Equal(2,clean.Charts[0]["points"]!.AsArray().Count);Assert.Equal(3,clean.Charts[1]["points"]!.AsArray().Count);
        Assert.Equal(1,clean.Remove([1,1]));Assert.Equal(0,clean.Remove([1]));
        Assert.All(clean.Charts,c=>Assert.True(c["points"]!.AsArray()[0]!.B("excluded")));
        Assert.All(clean.Charts,c=>Assert.False(c["points"]!.AsArray()[1]!.B("excluded")));
        Assert.Equal(1,clean.Remove([3])); // This row has no point in the first chart.
        string output=FilePath("clean.dlb");clean.Export(output);var result=TableStore.Read(new(output));
        Assert.Equal(3,result.Count);Assert.Equal(5,result.Width);Assert.Equal("A",result.Text(1,0));Assert.Equal("C",result.Text(2,0));Assert.Equal("sem pares",result.Text(2,4));
        Assert.Equal(1,clean.Undo());Assert.Equal(1,clean.RemovedCount);Assert.Equal(1,clean.Undo());Assert.Equal(0,clean.RemovedCount);Assert.False(clean.CanUndo);
        Assert.All(clean.Charts,c=>Assert.All(c["points"]!.AsArray(),p=>Assert.False(p.B("excluded"))));
        Assert.Equal(4,TableStore.Page(new(source)).Total);
    }

    [Fact]
    public void SnapshotSurvivesSourceChangeAndPreservesExportFormats()
    {
        string source=Source(),snapshot=FilePath("snapshot.dlb");
        using(var clean=IntegratedCleaning.Create(new(source),[Chart("Y"),Chart("Z")],snapshot))
        {
            clean.Remove([2]);File.Delete(source); // All later reads use the frozen snapshot.
            string excel=FilePath("clean.xlsx"),csv=FilePath("clean.csv");clean.Export(excel);clean.Export(csv);
            Assert.Equal(4,TableStore.Read(new(excel)).Count);Assert.Equal(4,File.ReadAllLines(csv).Length);
            Assert.Contains("sem Y",File.ReadAllText(csv));Assert.Contains("sem pares",File.ReadAllText(csv));
            Assert.Throws<InvalidOperationException>(()=>clean.Export(source));Assert.Throws<InvalidOperationException>(()=>clean.Export(snapshot));
        }
        Assert.False(File.Exists(snapshot));
    }

    [Fact]
    public void StartsFromCurrentRowsWithoutPreviousFitAndRejectsInvalidChanges()
    {
        string source=Source();var first=Chart("Y");first["excluded"]=new JsonArray(1);first["fits"]=new JsonArray(new JsonObject{["converged"]=true});
        using var clean=IntegratedCleaning.Create(new(source),[first,Chart("Z")],FilePath("snapshot.dlb"));
        Assert.Null(clean.Charts[0]["chart"]!["fits"]);Assert.Equal(0,clean.RemovedCount);Assert.NotNull(first["fits"]);
        Assert.Throws<InvalidDataException>(()=>clean.Remove([1,99]));Assert.Equal(0,clean.RemovedCount);
        Assert.Throws<InvalidDataException>(()=>clean.Export(FilePath("bad.txt")));
        var cancellation=new CancellationToken(true);string cancelled=FilePath("cancelled.dlb");
        Assert.Throws<OperationCanceledException>(()=>clean.Export(cancelled,cancellation));Assert.False(File.Exists(cancelled));
        string bad=FilePath("bad-snapshot.dlb");var missing=Chart("Missing");
        Assert.Throws<InvalidDataException>(()=>IntegratedCleaning.Create(new(source),[first,missing],bad));Assert.False(File.Exists(bad));
    }

    public void Dispose()=>Directory.Delete(root,true);
}
