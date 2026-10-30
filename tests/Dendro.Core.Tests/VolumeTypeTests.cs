using System.Text.Json.Nodes;
using Dendro.Core.Configuration;
using Dendro.Core.Processing;
using Dendro.Core.Tabular;

namespace Dendro.Core.Tests;

public class VolumeTypeTests
{
    [Fact]
    public void LegacyModelsDefaultToVtccAndSameIdCanHaveVccc()
    {
        var project = ProjectStore.Create("Tipos");
        project.Obj("volume").Arr("equacoes").Add(new JsonObject { ["codigo"] = "ID01", ["b0"] = 1, ["b1"] = 2, ["b2"] = 3 });
        ProjectStore.Validate(project);
        Assert.Equal("VTCC", project.At("volume.equacoes")![0]!.S("tipo_volume"));
        ProjectModels.SetVolume(project, -1, "ID01", 1, 2, 3, "Modelo", "", null, "VCCC");
        ProjectStore.Validate(project);
        Assert.Equal(2, project.Obj("volume").Arr("equacoes").Count);
        Assert.Throws<InvalidDataException>(() => ProjectModels.SetVolume(project, -1, "ID01", 1, 2, 3, "Modelo", "", null, "VCCC"));
        ProjectModels.SetVolume(project, 1, "ID01", 4, 5, 6, "Modelo", "", null);
        Assert.Equal("VCCC", project.At("volume.equacoes")![1]!.S("tipo_volume"));
        Assert.Throws<InvalidDataException>(() => ProjectModels.SetVolume(project, -1, "ID02", 1, 2, 3, "Modelo", "", null, "INVÁLIDO"));
    }

    [Fact]
    public void RegisteringVcccDoesNotReplaceVtccDuringCurrentProcessing()
    {
        string folder = Path.Combine(Path.GetTempPath(), "dendro-volume-types-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var project = ProjectStore.Create("Tipos");
            ProjectModels.SetVolume(project, -1, "AL1R", 0, 1, 1, "VTCC", "", null, "VTCC");
            ProjectModels.SetVolume(project, -1, "AL1R", 5, 1, 1, "VCCC", "", null, "VCCC");
            project.Obj("processador")["modo"] = "resumo_direto";
            string config = Path.Combine(folder, "types.dendro"), output = Path.Combine(folder, "types.dlb");
            ProjectStore.Save(config, project);
            var result = new InventoryProcessor().Process(new(Path.Combine(MigrationTests.Fixtures, "inventário.xlsx"), config,
                Path.Combine(MigrationTests.Fixtures, "cadastro.xlsx"), output, "Inventário"));
            Assert.Equal(11d, result.Volume, 10);
            Assert.Contains("DIRETO - PARCELAS", TableStore.Tables(output));
        }
        finally { Directory.Delete(folder, true); }
    }
}
