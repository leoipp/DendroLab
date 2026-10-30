using Dendro.Core.Tabular;

namespace Dendro.Core.Tests;

public class ColumnExpressionTests
{
    [Fact]
    public void ReplacesOnlySelectedTableInProcessingDataset()
    {
        string folder = Path.Combine(Path.GetTempPath(), "dendro-replace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string source = Path.Combine(folder, "output.dlb"), bank = Path.Combine(folder, "project.dlb");
            using (var writer = new CompactBankWriter(source))
            {
                string trees = writer.AddTable(["Código"], "DIRETO - ARVORES");
                string plots = writer.AddTable(["AB"], "DIRETO - PARCELAS");
                writer.Add(trees, ["RDBOBA00123P20-001"]);
                writer.Add(plots, [42d]); writer.Complete();
            }
            CompactBankFile.Import(bank, [(source, "output.dlb")]);
            var target = new TableSource(Path.Combine(bank, "output.dlb"), "DIRETO - ARVORES");
            var updated = TableTools.CalculateColumns(target,
                [new("Tipo", [], Expression: "MEIO([Código]; 12; 1)")], folder, "calculated");
            CompactBankFile.ReplaceTable(target, updated.Path);
            Assert.Equal("P", TableStore.Read(target).Text(1, 1));
            Assert.Equal(42d, TableStore.Read(target with { Table = "DIRETO - PARCELAS" }).Number(1, 0));
            using (var reader = new CompactBankReader(bank))
            {
                Assert.Equal(2, reader.Catalog.Tables.Length);
                Assert.Equal(2, reader.Catalog.Tables.Select(t => t.Id).Distinct().Count());
            }
            byte[] before = File.ReadAllBytes(bank);
            Assert.Throws<InvalidDataException>(() => CompactBankFile.ReplaceTable(target with { Table = "Ausente" }, updated.Path));
            Assert.Equal(before, File.ReadAllBytes(bank));
            CompactBankFile.Import(bank, [(updated.Path, "child.dlb"), (updated.Path, "grandchild.dlb")]);
            Assert.True(CompactBankFile.RemoveTable(target, ["child.dlb", "grandchild.dlb"]));
            using (var reader = new CompactBankReader(bank))
                Assert.Equal("DIRETO - PARCELAS", Assert.Single(reader.Catalog.Tables).Name);
            Assert.Equal(42d, TableStore.Read(target with { Table = "DIRETO - PARCELAS" }).Number(1, 0));
            Assert.True(CompactBankFile.RemoveTable(target with { Table = "DIRETO - PARCELAS" }));
            Assert.False(File.Exists(bank));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("ESQUERDA([Código]; 6)", "RDBOBA")]
    [InlineData("MEIO([Código]; 12; 3)", "P20")]
    [InlineData("EXT.TEXTO([Código]; 12; 1)", "P")]
    [InlineData("DIREITA(ANTES([Código]; \"-\"); 3)", "P20")]
    [InlineData("SE(MEIO([Código]; 12; 1) = \"P\"; \"Plantio\"; \"Outro\")", "Plantio")]
    [InlineData("SE(1=1; \"ok\"; MEIO([Código]; 0; 1))", "ok")]
    [InlineData("ESQUERDA([Código]; 6) & \"-\" & DEPOIS([Código]; \"-\")", "RDBOBA-001")]
    public void ExtractsAndClassifiesUserExample(string expression, string expected)
        => Assert.Equal(expected, new ColumnExpression(expression, ["Código"]).Evaluate(["RDBOBA00123P20-001"]));

    [Fact]
    public void RejectsUnknownColumnsAndFunctions()
    {
        Assert.Throws<InvalidDataException>(() => new ColumnExpression("[Ausente]", ["Código"]));
        Assert.Throws<InvalidDataException>(() => new ColumnExpression("EXEC(\"x\")", ["Código"]));
        Assert.Throws<InvalidDataException>(() => new ColumnExpression(new string('-', 100) + "1", []));
    }

    [Fact]
    public void SavesCalculatedColumnsAndUsesThemForStrata()
    {
        string folder = Path.Combine(Path.GetTempPath(), "dendro-columns-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string source = Path.Combine(folder, "source.dlb");
            using (var writer = new CompactBankWriter(source))
            {
                string id = writer.AddTable(["Código"]);
                writer.Add(id, ["RDBOBA00123P20-001"]);
                writer.Add(id, ["RDBOBA00123R20-002"]);
                writer.Complete();
            }
            DerivedColumn[] specs = [new("Prefixo", [], Expression: "ESQUERDA([Código]; 6)"),
                new("Tipo", [], Expression: "MEIO([Código]; 12; 1)"),
                new("Classe", [], Expression: "SE([Tipo]=\"P\"; \"Plantio\"; \"Outro\")")];
            var result = TableTools.CalculateColumns(new(source), specs, folder, "calculated");
            var table = TableStore.Read(new(result.Path));
            Assert.Equal(4, table.Width);
            Assert.Equal("RDBOBA", table.Text(1, 1));
            Assert.Equal("Plantio", table.Text(1, 3));
            Assert.Equal("Outro", table.Text(2, 3));
            var strata = TableTools.Discover(new(source), new[] { "Classe" }, [], specs);
            Assert.Equal(2, strata.Length);
            Assert.Equal(1, TableStore.Read(new(source)).Width);
        }
        finally { Directory.Delete(folder, true); }
    }
}
