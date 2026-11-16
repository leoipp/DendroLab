using Dendro.Core.Tabular;

namespace Dendro.Core.Tests;

public sealed class BiologicalAssetTests
{
    [Fact]
    public void UsesOrderedFallbacksWithIndependentMappingsThenAbsoluteModel()
    {
        string folder=Path.Combine(Path.GetTempPath(),"asset-fallback-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string basis=Path.Combine(folder,"base.dlb"),primary=Path.Combine(folder,"primary.dlb"),
                second=Path.Combine(folder,"second.dlb"),third=Path.Combine(folder,"third.dlb"),output=Path.Combine(folder,"asset.dlb");
            using(var w=new CompactBankWriter(basis))
            {
                string id=w.AddTable(["Key","AltKey","Planting","Curve"]);
                foreach(string code in new[]{"A","B","C","D"})w.Add(id,[code,"alt-"+code,"2020-01-01","squared"]);
                w.Complete();
            }
            using(var w=new CompactBankWriter(primary))
            {
                string id=w.AddTable(["Key","Date","Volume","Note"]);
                w.Add(id,["A","2024-03-10",100d,"primary"]);w.Complete();
            }
            using(var w=new CompactBankWriter(second))
            {
                string id=w.AddTable(["Code","Measured","V","Description","CurveCode"]);
                w.Add(id,["alt-A","2025-01-01",999d,"newer but lower priority","squared"]);
                w.Add(id,["alt-B","2023-02-10",40d,"older second","squared"]);
                w.Add(id,["alt-B","2024-06-10",80d,"second","squared"]);
                w.Add(id,["alt-C","invalid",10d,"invalid date","squared"]);w.Complete();
            }
            using(var w=new CompactBankWriter(third))
            {
                string id=w.AddTable(["Code","Measured","V","Description"]);
                w.Add(id,["C","2025-03-10",60d,"third"]);w.Complete();
            }
            var options=new BiologicalAssetOptions("Key","Key","Date",["Volume","Note"],PlantingDate:"Planting",
                CurveId:"Curve",VolumeColumn:"Volume",ThroughYear:2026,Curves:[new("squared","t^2",[1d],"anos")],
                ProjectionMonth:12,ProjectionDay:31,Fallbacks:[
                    new(new(second),"AltKey","Code","Measured","V",new Dictionary<string,string>{{"Note","Description"}},Label:"Second",CurveIdColumn:"CurveCode"),
                    new(new(third),"Key","Code","Measured","V",new Dictionary<string,string>{{"Note","Description"}},Label:"Third")],
                UseModelWhenMissing:true);
            var result=BiologicalAsset.Join(new(basis),new(primary),options,output);
            var table=TableStore.Read(new(output,"Ativo biológico"));
            Assert.Equal(4,result.BaseRows);Assert.Equal(3,result.Linked);Assert.Equal(2,result.FallbackLinked);Assert.Equal(1,result.ModelOnly);
            Assert.Equal(100d,table.Number(1,table.Require("INV_Volume")));
            Assert.Equal("primary",table.Text(1,table.Require("INV_Note")));
            Assert.Equal(80d,table.Number(2,table.Require("INV_Volume")));
            Assert.Equal("Second",table.Text(2,table.Require("AB_Fonte")));
            Assert.Equal("second",table.Text(2,table.Require("INV_Note")));
            Assert.Equal(60d,table.Number(3,table.Require("INV_Volume")));
            Assert.Equal("Third",table.Text(3,table.Require("AB_Fonte")));
            Assert.Equal("Modelo direto",table.Text(4,table.Require("AB_Metodo")));
            Assert.Equal("",table.Text(4,table.Require("AB_Data_Medicao")));
            double age=(new DateTime(2026,12,31)-new DateTime(2020,1,1)).TotalDays/365.25;
            Assert.Equal(age*age,table.Number(4,table.Require("AB_Volume_m3_ha_2026")),8);
            Assert.Equal(1d,table.Number(4,table.Require("AB_Volume_m3_ha_2021")),8);
            Assert.True(double.IsNaN(table.Number(1,table.Require("AB_Volume_m3_ha_2021"))));
            Assert.Equal(2d,table.Number(4,table.Require("AB_IMA_2_anos_m3_ha_ano")),8);
            Assert.Equal("IMA7",table.Text(0,table.Width-2));
            Assert.Equal("IMA7-Curva",table.Text(0,table.Width-1));
            double measuredAge=(new DateTime(2024,3,10)-new DateTime(2020,1,1)).TotalDays/365.25;
            Assert.Equal(700d/(measuredAge*measuredAge),table.Number(1,table.Require("IMA7")),8);
            Assert.Equal(7d,table.Number(1,table.Require("IMA7-Curva")),8);
            Assert.Equal(7d,table.Number(4,table.Require("IMA7")),8);
            string noModel=Path.Combine(folder,"no-model.dlb");
            var without=BiologicalAsset.Join(new(basis),new(primary),options with { UseModelWhenMissing=false },noModel);
            Assert.Equal(0,without.ModelOnly);
            var noModelTable=TableStore.Read(new(noModel,"Ativo biológico"));
            Assert.True(double.IsNaN(noModelTable.Number(4,noModelTable.Require("AB_Volume_m3_ha_2026"))));
            Assert.True(double.IsNaN(noModelTable.Number(4,noModelTable.Require("IMA7"))));
            Assert.Equal(7d,noModelTable.Number(4,noModelTable.Require("IMA7-Curva")),8);
        }
        finally { Directory.Delete(folder,true); }
    }

    [Fact]
    public void ProjectsIntoOneTableInDateOrderAndImaOnPlantingAnniversaries()
    {
        string folder=Path.Combine(Path.GetTempPath(),"asset-wide-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string basis=Path.Combine(folder,"base.dlb"),processed=Path.Combine(folder,"measurements.dlb"),output=Path.Combine(folder,"asset.dlb");
            using(var writer=new CompactBankWriter(basis))
            {
                string id=writer.AddTable(["Key","Planting","Curve"]);
                writer.Add(id,["A","2020-03-15","linear"]);
                writer.Add(id,["B","2020-09-01","linear"]);
                writer.Complete();
            }
            using(var writer=new CompactBankWriter(processed))
            {
                string id=writer.AddTable(["Key","Date","Volume"]);
                writer.Add(id,["A","2023-04-10",999d]);
                writer.Add(id,["A","2025-04-10",100d]);
                writer.Add(id,["B","2024-10-15",80d]);
                writer.Complete();
            }
            var result=BiologicalAsset.Join(new(basis),new(processed),new("Key","Key","Date",["Volume"],
                PlantingDate:"Planting",CurveId:"Curve",VolumeColumn:"Volume",ThroughYear:2026,
                Curves:[new("linear","b0*t",[1d],"anos")],ProjectionMonth:12,ProjectionDay:31),output);
            Assert.Equal(new[]{"Ativo biológico"},TableStore.Tables(output));
            var table=TableStore.Read(new(output,"Ativo biológico"));
            string[] volumes=table.Rows[0].Select(value=>value?.ToString()??"").Where(column=>column.StartsWith("AB_Volume_m3_ha_")).ToArray();
            Assert.Equal(new[]{"AB_Volume_m3_ha_2024","AB_Volume_m3_ha_2025","AB_Volume_m3_ha_2026"},volumes);
            Assert.Equal("AB_Volume_m3_ha_2024",volumes[0]);
            Assert.True(double.IsNaN(table.Number(1,table.Require(volumes[0]))));
            Assert.Equal(100d,table.Number(1,table.Require("AB_Volume_m3_ha_2025")));
            double expected=100d*(new DateTime(2026,12,31)-new DateTime(2020,3,15)).TotalDays
                /(new DateTime(2025,4,10)-new DateTime(2020,3,15)).TotalDays;
            Assert.Equal(expected,table.Number(1,table.Require("AB_Volume_m3_ha_2026")),8);
            Assert.Equal("2021-03-15",table.Text(1,table.Require("AB_IMA_1_anos_Data")));
            double expectedIma=100d/((new DateTime(2025,4,10)-new DateTime(2020,3,15)).TotalDays/365.25);
            Assert.Equal(expectedIma,table.Number(1,table.Require("AB_IMA_1_anos_m3_ha_ano")),8);
            Assert.Equal(expectedIma,table.Number(1,table.Require("AB_IMA_5_anos_m3_ha_ano")),8);
            Assert.Equal("2026-03-15",table.Text(1,table.Require("AB_IMA_6_anos_Data")));
            double ima=expectedIma;
            Assert.Equal(ima,table.Number(1,table.Require("AB_IMA_6_anos_m3_ha_ano")),8);
            Assert.True(result.ProjectedRows>0);
        }
        finally { Directory.Delete(folder,true); }
    }

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
