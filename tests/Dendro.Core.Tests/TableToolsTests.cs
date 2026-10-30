using Dendro.Core.Spreadsheets;
using Dendro.Core.Tabular;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

namespace Dendro.Core.Tests;

public sealed class TableToolsTests : IDisposable
{
    private readonly string folder = Path.Combine(
        Path.GetTempPath(),
        "DENDRO-tables-" + Guid.NewGuid().ToString("N")
    );

    public TableToolsTests() => Directory.CreateDirectory(folder);

    private TableSource Write(string name, string[] columns, params object?[][] rows)
    {
        var table = new DataTable(columns);
        foreach (var row in rows)
            table.Add(row);
        string path = Path.Combine(folder, name + ".xlsx");
        WorkbookWriter.Write(path, [new("Anual", table)]);
        return new(path, "Anual");
    }

    [Theory]
    [InlineData(1048575, 2, true)]
    [InlineData(1048576, 2, false)]
    [InlineData(10, 16384, true)]
    [InlineData(10, 16385, false)]
    public void ExcelLimitIncludesHeader(long rows, int columns, bool expected) =>
        Assert.Equal(expected, TableTools.FitsExcel(rows, columns));

    [Fact]
    public void MergeAlignsColumnsAndRetainsTypes()
    {
        var a = Write("2025", ["Ano", "Área"], [2025d, "001"]);
        var b = Write("2026", ["Área", "Ano"], ["002", 2026d]);
        var result = TableTools.Merge([a, b], folder, "União");
        Assert.Equal(2, result.Rows);
        Assert.EndsWith(".xlsx", result.Path);
        using var cursor = new TableCursor(new(result.Path, result.Table));
        var rows = cursor.Rows().ToArray();
        Assert.Equal(2026d, rows[1][0]);
        Assert.Equal("001", rows[0][1]);
        using var doc = SpreadsheetDocument.Open(result.Path, false);
        Assert.Empty(new OpenXmlValidator().Validate(doc));
    }

    [Fact]
    public void DatabasePagesAndStratificationKeepOnlyOwnRows()
    {
        var a = Write(
            "a",
            ["Ano", "Grupo", "Valor"],
            [2025d, "A", 1d],
            [2025d, "B", 2d],
            [2025d, null, 3d]
        );
        var b = Write("b", ["Ano", "Grupo", "Valor"], [2026d, "A", 4d], [2026d, "B", 5d]);
        var merged = TableTools.Merge([a, b], folder, "Banco", true);
        Assert.EndsWith(".dendrodb", merged.Path);
        Assert.Equal(["Dados"], TableStore.Tables(merged.Path));
        var page = TableStore.Page(new(merged.Path), 3, 1);
        Assert.Equal(5, page.Total);
        Assert.Equal(2026d, page.Data.Value(1, 0));
        Assert.Equal("A", page.Data.Value(1, 1));
        var filters = new[]
        {
            new TableFilter("Valor", FilterOperator.GreaterOrEqual, "2"),
            new TableFilter("Ano", FilterOperator.GreaterOrEqual, "2025"),
        };
        var strata = TableTools.Discover(new(merged.Path), "Grupo", filters);
        Assert.Equal(3, strata.Length);
        Assert.Equal(2, strata.Single(s => s.Value == "B").Rows);
        var outputs = TableTools.Stratify(
            new(merged.Path),
            "Grupo",
            filters,
            strata.Select((s, i) => new StratumOutput(s.Key, "Estrato_" + i)).ToArray(),
            folder
        );
        Assert.Equal(4, outputs.Sum(o => o.Rows));
        foreach (var output in outputs)
        {
            using var table = new TableCursor(new(output.Path));
            Assert.Single(table.Rows().Select(r => TableTools.Key(r[1])).Distinct());
        }
    }

    [Fact]
    public void SchemaErrorCancellationAndCollisionsDoNotPublishOrOverwrite()
    {
        var a = Write("a", ["ID"], [1d]);
        var b = Write("b", ["Outra"], [2d]);
        Assert.Throws<InvalidDataException>(() => TableTools.Merge([a, b], folder, "Erro"));
        Assert.False(File.Exists(Path.Combine(folder, "Erro.xlsx")));
        var cancelled = new CancellationToken(true);
        Assert.Throws<OperationCanceledException>(() =>
            TableTools.Merge([a, a], folder, "Cancelado", cancellation: cancelled)
        );
        var result = TableTools.Merge([a, a], folder, "OK");
        byte[] original = File.ReadAllBytes(result.Path);
        Assert.Throws<IOException>(() => TableTools.Merge([a, a], folder, "OK"));
        Assert.Equal(original, File.ReadAllBytes(result.Path));
        Assert.Empty(Directory.EnumerateDirectories(folder, ".dendro-*"));
        var namedStage = TableTools.Merge([a, a], folder, "stage", true);
        Assert.Equal(2, TableStore.Page(new(namedStage.Path)).Total);
        Assert.Throws<InvalidDataException>(() => TableTools.FileName("../fora"));
        Assert.Throws<InvalidDataException>(() => TableTools.FileName("CON"));
    }

    [Fact]
    public void SelectedStrataCanBeExportedAsDatabase()
    {
        var a = Write("Origem", ["Classe", "Valor"], ["A", 10d], ["B", 20d], ["A", 30d]);
        var strata = TableTools.Discover(a, "Classe", []);
        var output = Assert.Single(
            TableTools.Stratify(
                a,
                "Classe",
                [],
                [new(strata.Single(s => s.Value == "A").Key, "A")],
                folder,
                true
            )
        );
        Assert.Equal(2, output.Rows);
        Assert.EndsWith(".dendrodb", output.Path);
        using var cursor = new TableCursor(new(output.Path));
        Assert.All(cursor.Rows(), row => Assert.Equal("A", row[0]));
    }

    [Fact]
    public void LegacyXlsCanBeMerged()
    {
        var source = new TableSource(
            Path.Combine(MigrationTests.Fixtures, "inventario_legacy.XLS"),
            "Dados"
        );
        var output = TableTools.Merge([source, source], folder, "Legado");
        Assert.Equal(6, output.Rows);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(folder))
            File.Delete(file);
        Directory.Delete(folder);
    }
}
