using Dendro.Core.Tabular;

namespace Dendro.Core.Tests;

public sealed class BiologicalAssetTests
{
    [Fact]
    public void UsesLatestMeasurementPreservesBaseAndFlagsConflictingTies()
    {
        string folder=Path.Combine(Path.GetTempPath(),"dendro-asset-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        try
        {
            string basis=Path.Combine(folder,"base.dlb"),processed=Path.Combine(folder,"processed.dlb"),output=Path.Combine(folder,"asset.dlb");
            using(var w=new CompactBankWriter(basis)){string id=w.AddTable(["Codigo","Cadastro"]);foreach(string key in new[]{"A","B","C","A"})w.Add(id,[key,"base"]);w.Complete();}
            using(var w=new CompactBankWriter(processed))
            {
                string id=w.AddTable(["Talhão","Data_Medicao","Volume","Tipo_Inventario"]);
                w.Add(id,["A","2025-01-01",100d,"IFPC"]);w.Add(id,["A",new DateTime(2026,1,1).ToOADate(),200d,"IFPC"]);
                w.Add(id,["A","2027-01-01",999d,"IFC"]);w.Add(id,["A","data inválida",9999d,"IFPC"]);
                w.Add(id,["B","2026-01-01",300d,"IFPC"]);w.Add(id,["B","2026-01-01",400d,"IFPC"]);w.Complete();
            }
            var result=BiologicalAsset.Join(new(basis),new(processed),new("Codigo","Talhão","Data_Medicao",["Volume"],InventoryType:"IFPC"),output);
            Assert.Equal(4,result.BaseRows);Assert.Equal(2,result.Linked);Assert.Equal(1,result.Missing);Assert.Equal(1,result.Ambiguous);Assert.Equal(1,result.InvalidDates);
            var table=TableStore.Read(new(output,"Ativo biológico"));Assert.Equal(5,table.Count);
            Assert.Equal(200,table.Number(1,table.Require("INV_Volume")));Assert.Equal(200,table.Number(4,table.Require("INV_Volume")));
            Assert.Equal("2026-01-01",table.Text(1,table.Require("AB_Data_Medicao")));
            Assert.Equal("Conflito na data mais recente",table.Text(2,table.Require("AB_Vinculo")));Assert.True(double.IsNaN(table.Number(2,table.Require("INV_Volume"))));
            Assert.Equal(4,TableStore.Page(new(basis)).Total);
            string parcels=Path.Combine(folder,"parcels.dlb");
            using(var w=new CompactBankWriter(parcels))
            {
                string id=w.AddTable(["Talhão","Parcela","Data_Medicao","Volume"]);
                w.Add(id,["A","1","2025-01-01",10d]);w.Add(id,["A","2","2026-01-01",20d]);w.Complete();
            }
            string mixedOutput=Path.Combine(folder,"mixed-asset.dlb");
            var mixed=BiologicalAsset.Join(new(basis),new(parcels),new("Codigo","Talhão","Data_Medicao",["Volume"]),mixedOutput);
            Assert.Equal(2,mixed.Ambiguous);
            Assert.Equal("Chave reúne parcelas distintas",TableStore.Read(new(mixedOutput,"Ativo biológico")).Text(1,4));
        }
        finally{Directory.Delete(folder,true);}
    }
}
