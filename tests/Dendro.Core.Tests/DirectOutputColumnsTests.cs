using System.Text.Json.Nodes;
using Dendro.Core.Configuration;
using Dendro.Core.Processing;
using Dendro.Core.Tabular;

namespace Dendro.Core.Tests;

public sealed class DirectOutputColumnsTests
{
    [Fact]
    public void ProjectsEachLevelIndependentlyKeepsMandatoryColumnsAndDoesNotChangeTotals()
    {
        string folder=Path.Combine(Path.GetTempPath(),"dendro-direct-columns-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        try
        {
            var cfg=ProjectStore.Create("Columns");var settings=cfg.Obj("processador");settings["modo"]="resumo_direto";settings.Obj("saidas")["arvore"]=true;
            var omitted=new[]{("arvore","AS","DIRETO - ARVORES"),("parcela","Dq","DIRETO - PARCELAS"),("talhao","Volume_amostrado_m3","DIRETO - TALHOES"),("lote","VMI_amostrado_m3_fuste","DIRETO - LOTES"),("projeto","Fustes_totais_estimados","DIRETO - PROJETO")};
            foreach(var (level,column,_) in omitted)settings.Obj("colunas_direto").Obj(level)[column]=false;
            string project=Path.Combine(folder,"columns.dendro");ProjectStore.Save(project,cfg);cfg=ProjectStore.Read(project);
            string output=Path.Combine(folder,"columns.dlb");
            using(var summary=new DirectInventorySummary(output,cfg))
            {
                summary.Add("T","1","L",2025,2,10,2,true,100,10,"","",200,300,measurement:"2025-01-01");
                summary.Add("T","1","L",2025,3,10,3,true,100,10,"","",250,350,measurement:"2025-01-01");
                Assert.Equal(5,summary.Complete().Volume);
            }
            foreach(var (level,column,sheet) in omitted)
            {
                var table=TableStore.Read(new(output,sheet));Assert.Equal(-1,table.Column(column));
                Assert.Equal(DirectOutputColumns.Headers(level).Where(c=>DirectOutputColumns.Enabled(cfg["processador"],level,c)),table.Rows[0].Select(v=>v!.ToString()));
                Assert.Equal(2,table.Count-(level=="arvore"?1:0));
            }
            var plots=TableStore.Read(new(output,"DIRETO - PARCELAS"));Assert.Equal(500,plots.Number(1,plots.Require("Volume_m3_ha")));
            Assert.True(plots.Column("Parcela_ID")>=0);Assert.True(plots.Column("Data_Medicao")>=0);
            cfg.Obj("processador").Obj("colunas_direto").Obj("parcela")["Parcela_ID"]=false;
            Assert.Throws<InvalidDataException>(()=>DirectInventorySummary.Validate(cfg["processador"]));
        }
        finally{Directory.Delete(folder,true);}
    }
}
