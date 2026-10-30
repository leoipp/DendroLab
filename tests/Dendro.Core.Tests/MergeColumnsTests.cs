using Dendro.Core.Spreadsheets;
using Dendro.Core.Tabular;

namespace Dendro.Core.Tests;

public class MergeColumnsTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "dendro-merge-columns-" + Guid.NewGuid().ToString("N"));
    public MergeColumnsTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);
    private TableSource Bank(string name, string[] columns, params object?[] values)
    {
        string path = Path.Combine(folder, name + ".dlb");
        using var writer = new CompactBankWriter(path);
        string id = writer.AddTable(columns); writer.Add(id, values); writer.Complete();
        return new(path);
    }

    [Fact]
    public void IntersectionUsesEverySourceAndAlignsValuesByColumnName()
    {
        var first = Bank("first", ["A", "B", "C"], 1d, 2d, 3d);
        var second = Bank("second", ["D", "C", "A", "B"], 40d, 30d, 10d, 20d);
        var third = Bank("third", ["C", "A"], 300d, 100d);
        var result = TableTools.Merge([first, second, third], folder, "merged", compact: true, commonColumnsOnly: true);
        var data = TableStore.Read(new(result.Path));
        Assert.Equal(new[] { "A", "C", "Fonte" }, Enumerable.Range(0, data.Width).Select(c => data.Text(0, c)));
        Assert.Equal(4, data.Count);
        Assert.Equal(new[] { 1d, 10d, 100d }, Enumerable.Range(1, 3).Select(r => data.Number(r, 0)));
        Assert.Equal(new[] { 3d, 30d, 300d }, Enumerable.Range(1, 3).Select(r => data.Number(r, 1)));
        Assert.Equal(new[] { "first", "second", "third" }, Enumerable.Range(1, 3).Select(r => data.Text(r, data.Require("Fonte"))));
    }

    [Fact]
    public void StrictModeStillRejectsExtraColumnsAndLeavesNoOutput()
    {
        var first = Bank("first", ["A", "B", "C"], 1d, 2d, 3d);
        var second = Bank("second", ["A", "B", "C", "D"], 10d, 20d, 30d, 40d);
        Assert.Throws<InvalidDataException>(() => TableTools.Merge([first, second], folder, "strict", compact: true));
        Assert.False(File.Exists(Path.Combine(folder, "strict.dlb")));
        Assert.Empty(Directory.GetDirectories(folder, ".dendro-*"));
    }

    [Fact]
    public void NoSharedColumnFailsClearlyWithoutPublishingEmptySchema()
    {
        var first = Bank("first", ["A"], 1d);
        var second = Bank("second", ["B"], 2d);
        var error = Assert.Throws<InvalidDataException>(() => TableTools.Merge([first, second], folder, "none", compact: true, commonColumnsOnly: true));
        Assert.Contains("Não há colunas", error.Message);
        Assert.False(File.Exists(Path.Combine(folder, "none.dlb")));
    }

    [Fact]
    public void MixedExcelAndDlbSourcesWorkWithIntersection()
    {
        string path = Path.Combine(folder, "excel.xlsx");
        var table = new DataTable(["A", "B", "C"]); table.Add(1d, 2d, 3d);
        WorkbookWriter.Write(path, [new("Medições", table)]);
        var bank = Bank("bank", ["C", "B", "A", "D"], 30d, 20d, 10d, 40d);
        var result = TableTools.Merge([new(path, "Medições"), bank], folder, "mixed", compact: true, commonColumnsOnly: true);
        var data = TableStore.Read(new(result.Path));
        Assert.Equal(4, data.Width); Assert.Equal(3, data.Count);
        Assert.Equal(10d, data.Number(2, 0)); Assert.Equal(30d, data.Number(2, 2));
        Assert.Equal("excel · Medições", data.Text(1, data.Require("Fonte")));
        Assert.Equal("bank", data.Text(2, data.Require("Fonte")));
    }

    [Fact]
    public void ExistingSourceColumnsArePreservedAndDisplayNamesAreUsed()
    {
        var first = Bank("a", ["A", "Fonte", "Fonte_anterior"], 1d, "origem inicial", "origem antiga");
        var second = Bank("b", ["Fonte_anterior", "A", "Fonte"], "outra antiga", 2d, "outra inicial");
        var result = TableTools.Merge([first, second], folder, "provenance", compact: true, sourceNames: ["Inventário 2025", "Inventário 2026"]);
        var data = TableStore.Read(new(result.Path));
        Assert.Equal("origem inicial", data.Text(1, data.Require("Fonte_anterior_2")));
        Assert.Equal("origem antiga", data.Text(1, data.Require("Fonte_anterior")));
        Assert.Equal("outra inicial", data.Text(2, data.Require("Fonte_anterior_2")));
        Assert.Equal("Inventário 2025", data.Text(1, data.Require("Fonte")));
        Assert.Equal("Inventário 2026", data.Text(2, data.Require("Fonte")));
    }
}
