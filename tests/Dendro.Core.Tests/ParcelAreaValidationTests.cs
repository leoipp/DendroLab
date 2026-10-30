using Dendro.Core.Configuration;
using Dendro.Core.Processing;
using Dendro.Core.Spreadsheets;

namespace Dendro.Core.Tests;

public sealed class ParcelAreaValidationTests
{
    private static DataTable Input() => new(["Talhão", "Parcela", "Lote", "Data", "Compr", "Larg", "Dec_Compr", "Dec_Larg", "Fonte"]);
    private static void Add(DataTable input, string plot, object? length, object? width, object? slopeLength, object? slopeWidth)
        => input.Add("T1", plot, "L1", new DateTime(2025, 1, 1).ToOADate(), length, width, slopeLength, slopeWidth, "Inventário 2025");

    [Fact]
    public void ReportsEveryInvalidGeometryWithOriginalValuesAndOccurrenceCounts()
    {
        var input = Input(); Add(input, "P1", 100d, 100d, 0d, 0d);
        Add(input, "P2", 0d, 100d, 0d, 0d); Add(input, "P2", 0d, 100d, 0d, 0d);
        Add(input, "P3", 100d, 100d, "inválido", 0d); Add(input, "P4", 100d, 100d, 120d, 0d);
        string before = input.ToJson().ToJsonString();
        var error = Assert.Throws<InvalidDataException>(() => ParcelAreaValidation.Compute(input, ProjectStore.Create("Teste")));
        var issues = Assert.IsType<DataTable>(error.Data[ParcelAreaValidation.DiagnosticsKey]);
        Assert.Equal(4, issues.Count);
        Assert.Equal("3", issues.Text(1, issues.Require("Primeira linha processada")));
        Assert.Equal("2", issues.Text(1, issues.Require("Ocorrências")));
        Assert.Equal("Inventário 2025", issues.Text(1, issues.Require("Fonte")));
        Assert.Equal(0, issues.Number(1, issues.Require("Comprimento original")));
        Assert.Equal("inválido", issues.Text(2, issues.Require("Declinação comprimento original")));
        Assert.Contains("não numérico", issues.Text(2, issues.Require("Motivo")));
        Assert.Contains("declinações", issues.Text(3, issues.Require("Motivo")));
        Assert.Equal(before, input.ToJson().ToJsonString());
    }

    [Fact]
    public void EmptySlopeFlagsAreIndependentAndDoNotReplaceInvalidText()
    {
        var cfg = ProjectStore.Create("Teste"); var input = Input(); Add(input, "P1", 100d, 100d, " ", null);
        cfg.Obj("arquivo_entrada").Obj("declinacoes")["comprimento_vazio_zero"] = true;
        Assert.Throws<InvalidDataException>(() => ParcelAreaValidation.Compute(input, cfg));
        Assert.Equal(0d, input.Number(1, 6)); Assert.Null(input.Value(1, 7));
        cfg.Obj("arquivo_entrada").Obj("declinacoes")["largura_vazio_zero"] = true;
        Assert.Equal(100, ParcelAreaValidation.Compute(input, cfg)[1], 10);
        input.Set(1, 7, "inválido"); Assert.Throws<InvalidDataException>(() => ParcelAreaValidation.Compute(input, cfg));
        Assert.Equal("inválido", input.Text(1, 7));
    }

    [Fact]
    public void KeepsExistingAreaUnitsAndUsesConfiguredColumnNames()
    {
        var input = Input(); Add(input, "P1", 100d, 100d, 0d, 0d);
        var cfg = ProjectStore.Create("Teste");
        Assert.Equal(100d, ParcelAreaValidation.Compute(input, cfg)[1], 10);
        input.Set(0, 4, "Dimensão X"); cfg.Obj("arquivo_entrada").Obj("colunas")["comprimento_parcela"] = "Dimensão X";
        input.Set(1, 4, null);
        var error = Assert.Throws<InvalidDataException>(() => ParcelAreaValidation.Compute(input, cfg));
        var issues = Assert.IsType<DataTable>(error.Data[ParcelAreaValidation.DiagnosticsKey]);
        Assert.Equal("Dimensão X", issues.Text(1, issues.Require("Coluna de comprimento")));
        Assert.Contains("vazio", issues.Text(1, issues.Require("Motivo")));
        Assert.Throws<OperationCanceledException>(() => ParcelAreaValidation.Compute(input, cfg, new CancellationToken(true)));
    }
}
