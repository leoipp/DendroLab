using Dendro.Core.Spreadsheets;
using Dendro.Core.Tabular;

namespace Dendro.Core.Tests;

public sealed class TableSortingTests : IDisposable
{
    private readonly string folder=Path.Combine(Path.GetTempPath(),"dendro-sort-"+Guid.NewGuid().ToString("N"));
    public TableSortingTests()=>Directory.CreateDirectory(folder);
    public void Dispose()=>Directory.Delete(folder,true);
    private TableSource Save()
    {
        string path=Path.Combine(folder,"input.dlb");using var writer=new CompactBankWriter(path);
        string id=writer.AddTable(["Valor","Grupo"]);
        for(int i=42004;i>=0;i--)writer.Add(id,[(double)i,i%2==0?"A":"B"]);
        writer.Add(id,[null,"C"]);writer.Add(id,["x","C"]);writer.Complete();return new(path);
    }
    [Fact]
    public void NumericOrderMergesRunsPagesAndPreservesOriginalRowNumbersForDeletion()
    {
        var source=Save();
        using var sorted=FilteredTable.Create(source,[],temporaryFolder:folder,sort:new("Valor",TableSortKind.Number));
        Assert.Equal(0,sorted.Page().Page.Data.Number(1,0));Assert.Equal(500,sorted.Page(500).Page.Data.Number(1,0));
        Assert.Equal(42008,sorted.Page(42005).SourceRows[0]); // nonnumeric text, then empty
        Assert.Equal("x",sorted.Page(42005).Page.Data.Text(1,0));
        Assert.Equal(42006,sorted.Page().SourceRows[0]);
        Assert.Equal(42004,TableStore.Read(source).Number(1,0));
        using var filtered=FilteredTable.Create(source,[new("Grupo",FilterOperator.Equal,"B")],temporaryFolder:folder,sort:new("Valor",TableSortKind.Number,true));
        var page=filtered.Page();Assert.Equal(42003,page.Page.Data.Number(1,0));
        long[] delete=page.SourceRows.Take(2).Order().ToArray();
        Assert.Equal(2,TableRowEditor.Delete(source,TableRowEditor.Version(source),delete).Removed);
        Assert.Equal(42002,TableStore.Read(source).Number(2,0));
    }
    [Fact]
    public void AlphabeticalNumericDirectionsEmptyAndCancellation()
    {
        string path=Path.Combine(folder,"tiny.dlb");
        using(var w=new CompactBankWriter(path)) {string id=w.AddTable(["X"]);foreach(object? v in new object?[]{"2","10","a",null,"2"})w.Add(id,[v]);w.Complete();}
        using(var sorted=FilteredTable.Create(new(path),[],temporaryFolder:folder,sort:new("X",TableSortKind.Text)))Assert.Equal("10",sorted.Page().Page.Data.Text(1,0));
        using(var sorted=FilteredTable.Create(new(path),[],temporaryFolder:folder,sort:new("X",TableSortKind.Number,true)))
        {
            Assert.Equal("10",sorted.Page().Page.Data.Text(1,0));Assert.Equal(2,sorted.Page().SourceRows[1]);Assert.Equal(6,sorted.Page().SourceRows[2]);Assert.Equal("",sorted.Page().Page.Data.Text(5,0));
        }
        using var cts=new CancellationTokenSource();cts.Cancel();
        Assert.Throws<OperationCanceledException>(()=>FilteredTable.Create(new(path),[],cts.Token,temporaryFolder:folder,sort:new("X")));
        Assert.Equal(new[]{path},Directory.GetFiles(folder));
    }
}
