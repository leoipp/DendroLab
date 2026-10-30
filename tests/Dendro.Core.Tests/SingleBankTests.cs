using Dendro.Core.Tabular;

namespace Dendro.Core.Tests;

public sealed class SingleBankTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "dendro-single-" + Guid.NewGuid().ToString("N"));
    public SingleBankTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);
    private string Source(string filename, double value)
    {
        string path = Path.Combine(folder, filename);
        using var writer = new CompactBankWriter(path);
        string id = writer.AddTable(["X", "Y"], "Resultado");
        for (int row = 0; row < 5000; row++) writer.Add(id, [(double)row, value]);
        writer.Complete(); return path;
    }

    [Fact]
    public void OnePhysicalFileContainsIndependentDatasetsAndSupportsReplacement()
    {
        string bank = Path.Combine(folder, "Projeto.dlb");
        string a = Source("a.dlb", 2), b = Source("b.dlb", 3);
        CompactBankFile.Import(bank, [(a, "Entrada.dlb"), (b, "Saida.dlb")]);
        string input = Path.Combine(bank, "Entrada.dlb"), output = Path.Combine(bank, "Saida.dlb");
        Assert.True(File.Exists(bank)); Assert.False(Directory.Exists(bank)); Assert.False(File.Exists(input));
        Assert.True(CompactBankFile.Exists(input));
        Assert.Equal(new[] { "Resultado" }, TableStore.Tables(input));
        Assert.Equal(2, TableStore.Page(new(input, "Resultado"), 4999, 1).Data.Number(1, 1));
        Assert.Equal(3, TableStore.Page(new(output), 4999, 1).Data.Number(1, 1));
        CompactBankFile.Import(bank, [(a, "Saida.dlb")], replace: true);
        Assert.Equal(2, TableStore.Page(new(output), 4999, 1).Data.Number(1, 1));
        using var reader = new CompactBankReader(bank);
        Assert.Equal(2, reader.Catalog.Tables.Length);
    }

    [Fact]
    public void FailedImportAndCollisionLeavePreviousBankByteForByteIntact()
    {
        string bank = Path.Combine(folder, "Projeto.dlb"), input = Source("input.dlb", 4);
        CompactBankFile.Import(bank, [(input, "A.dlb")]); byte[] previous = File.ReadAllBytes(bank);
        Assert.Throws<IOException>(() => CompactBankFile.Import(bank, [(input, "A.dlb")]));
        Assert.Equal(previous, File.ReadAllBytes(bank));
        string corrupt = Path.Combine(folder, "bad.dlb"); File.WriteAllText(corrupt, "bad");
        Assert.Throws<InvalidDataException>(() => CompactBankFile.Import(bank, [(input, "B.dlb"), (corrupt, "C.dlb")]));
        Assert.Equal(previous, File.ReadAllBytes(bank)); Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
    }
}
