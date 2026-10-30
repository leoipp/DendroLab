using System.Text.Json.Nodes;
using Dendro.Core.Configuration;
using Dendro.Core.Mathematics;
using Dendro.Core.Processing;
using Dendro.Core.Spreadsheets;
using Dendro.Core.Tabular;

namespace Dendro.Core.Tests;

public sealed class InventoryAuditTests : IDisposable
{
    private readonly string folder=Path.Combine(Path.GetTempPath(),"dendro-audit-"+Guid.NewGuid().ToString("N"));
    public InventoryAuditTests()=>Directory.CreateDirectory(folder);
    public void Dispose()=>Directory.Delete(folder,true);
    private static DataTable Results()=>new(["Talhão","Parcela","Lote","Base_Origem","Tipo_Inventario","Parcela_ID","Idade_anos","Volume_m3_ha","Fustes_ha","VMI_m3_fuste"]);
    [Fact]
    public void ReconstructsUnitsDuplicatesConflictsAndArithmeticWithoutChangingSource()
    {
        var cfg=ProjectStore.Create("Audit"); cfg.Obj("processador")["inventory_type"]="IFPC";
        cfg.Obj("volume").Arr("equacoes").Add(new JsonObject{["codigo"]="AL1R",["b0"]=Math.Log(.001),["b1"]=1,["b2"]=1});
        var input=new DataTable(["Talhão","Parcela","Lote","Fonte","Cova","Fuste","Fila","Data","DAP","H","Compr","Larg","Dec_Compr","Dec_Larg"]);
        input.Add("T","1","XXXXAL00000P","IFPC.xlsx",1d,1d,1d,45000d,100d,200d,100d,100d,0d,0d);
        input.Add(input.Rows[1].ToArray());input.Set(2,input.Require("DAP"),200d);
        string path=Path.Combine(folder,"input.dlb");
        using(var writer=new CompactBankWriter(path)) {string id=writer.AddTable(input.Rows[0].Select(v=>v!.ToString()!).ToArray(),"Dados");foreach(var r in input.Rows.Skip(1))writer.Add(id,r.ToArray());writer.Complete();}
        byte[] before=File.ReadAllBytes(path);
        string plotId=new InventoryIdentity(input,cfg,true).PlotKey(1);
        var plots=Results();plots.Add("T","1","XXXXAL00000P","IFPC.xlsx","IFPC",plotId,10d,20d,100d,.2d);
        var audit=InventoryAudit.Run(plots,cfg,new(path,"Dados"),new(VolumeAlert:10));
        Assert.Equal(2,audit.Trees.Count);Assert.Equal(10,audit.Trees.Number(1,audit.Trees.Require("DAP_cm")),8);
        Assert.Equal(20,audit.Trees.Number(1,audit.Trees.Require("H_origem_m")),8);
        Assert.Equal(.2,audit.Trees.Number(1,audit.Trees.Require("Volume_origem_m3")),8);
        Assert.Equal(1,audit.Plots.Number(1,audit.Plots.Require("Duplicadas_conflitantes")));
        Assert.Equal(20,audit.Plots.Number(1,audit.Plots.Require("Volume_conhecido_origem_ha")),8);
        Assert.Contains("DAP/H/dimensões diferentes",audit.Plots.Text(1,audit.Plots.Require("Diagnostico")));
        Assert.Equal(before,File.ReadAllBytes(path));
        Assert.All(new[]{audit.Plots,audit.Trees,audit.Information,audit.Evolution},t=>Assert.All(t.Rows,r=>Assert.Equal(t.Rows[0].Count,r.Count)));
    }
    [Fact]
    public void LocalPeersDoNotMixInventoryTypesAndSmallGroupsHaveNoStatisticalLimit()
    {
        var input=Results();
        for(int i=0;i<8;i++)input.Add("T",i.ToString(),"L","Base","IFPC","P"+i,10d,100d,100d,1d);
        input.Add("T","9","L","Base","IFPC","out",10d,400d,100d,4d);
        input.Add("T","9","L","Base","IFC","other",10d,900d,100d,9d);
        var audit=InventoryAudit.Run(input,ProjectStore.Create("Test"),options:new(VolumeAlert:10000));
        int row=Enumerable.Range(1,audit.Plots.Count-1).Single(r=>audit.Plots.Text(r,5)=="out");
        Assert.Contains("IQR",audit.Plots.Text(row,audit.Plots.Require("Diagnostico")));
        int other=Enumerable.Range(1,audit.Plots.Count-1).Single(r=>audit.Plots.Text(r,5)=="other");
        Assert.True(double.IsNaN(audit.Plots.Number(other,audit.Plots.Require("Limite_IQR_grupo"))));
    }
    [Fact]
    public void EvolutionSeparatesTypesAndReportsDeclineDensityAndAmbiguousDates()
    {
        var input=Results();
        input.Add("T","1","L","2024","IFPC","a",10d,100d,100d,1d);
        input.Add("T","1","L","2025","IFPC","b",11d,80d,50d,1.6d);
        input.Add("T","1","L","2026","IFPC","c",12d,120d,50d,2.4d);
        input.Add("T","1","L","2025","IFC","d",11d,1000d,100d,10d);
        var options=new InventoryAuditOptions(TemporalColumns:["Talhão","Parcela"]);
        var temporal=InventoryAudit.Evolution(input,options);
        Assert.Equal(3,temporal.Count);
        Assert.Equal("Diminuiu — revisar",temporal.Text(1,temporal.Require("Situacao")));
        Assert.Equal(-20,temporal.Number(1,temporal.Require("Variacao_pct")),8);
        Assert.Equal(-50,temporal.Number(1,temporal.Require("Variacao_fustes_pct")),8);
        Assert.Equal("Cresceu",temporal.Text(2,temporal.Require("Situacao")));
        input.Add(input.Rows[2].ToArray());
        temporal=InventoryAudit.Evolution(input,options);
        Assert.Equal(2,temporal.Count);Assert.Equal("Ambígua",temporal.Text(1,temporal.Require("Situacao")));
        Assert.All(temporal.Rows,r=>Assert.Equal(temporal.Rows[0].Count,r.Count));
    }
    [Fact]
    public void SourceMissingHeightRemainsPartialAndDoesNotClaimProcessingError()
    {
        var cfg=ProjectStore.Create("x");var input=Results();input.Add("T","1","L","X","IFPC","a",2d,100d,100d,1d);
        var report=InventoryAudit.Run(input,cfg,options:new(VolumeAlert:50));
        Assert.Equal("Não solicitada",report.Plots.Text(1,report.Plots.Require("Conferencia_origem")));
        Assert.Throws<InvalidDataException>(()=>InventoryAudit.Run(input,cfg,options:new(DeclineTolerancePercent:-1)));
    }
}
