using Dendro.Core.Regression;
using Dendro.Core.Tabular;

namespace Dendro.Core.Tests;

public sealed class TaperAdjustmentTests : IDisposable
{
    private readonly string folder=Path.Combine(Path.GetTempPath(),"dendro-taper-"+Guid.NewGuid().ToString("N"));
    public TaperAdjustmentTests()=>Directory.CreateDirectory(folder);
    public void Dispose()=>Directory.Delete(folder,true);
    [Theory]
    [InlineData("kozak_model")]
    [InlineData("demaerschalk_model")]
    [InlineData("ormerod_diameter_model")]
    [InlineData("garcia_model")]
    [InlineData("garay_model")]
    public void RecoversSyntheticDiameterProfiles(string model)
    {
        string input=Path.Combine(folder,"input.dlb"),output=Path.Combine(folder,"output.dlb");
        var definition=TaperAdjustment.Models.Single(m=>m.Id==model);
        using(var writer=new CompactBankWriter(input))
        {
            string table=writer.AddTable(["Tree","DAP","H","h","d","Group"]);
            for(int tree=0;tree<8;tree++)for(int section=0;section<12;section++)
            {
                double dap=12+tree*2,H=17+tree*1.4,h=H*section/13;
                double d=TaperAdjustment.Predict(model,definition.Initial.Select(v=>v*1.08).ToArray(),dap,H,h);
                writer.Add(table,[tree.ToString(),dap,H,h,d,tree%2==0?"A":"B"]);
            }
            writer.Add(table,["invalid",0,20,1,3,"A"]);writer.Complete();
        }
        var result=TaperAdjustment.Run(new(input),new("Tree","DAP","H","h","d","Group",[model]),output);
        Assert.Equal(2,result.Fits.Count);Assert.Equal(1,result.Invalid);
        foreach(var fit in result.Fits)
        {
            Assert.Equal("OK",fit!["status"]!.GetValue<string>());
            Assert.True(fit["rmse"]!.GetValue<double>()<1e-4,fit.ToJsonString());
        }
        Assert.Equal(new[]{"Dados","Parametros","Estatisticas"},TableStore.Tables(output));
        using var cursor=new TableCursor(new(output,"Dados"));
        Assert.Equal(97,cursor.Rows().Count());
        Assert.Contains("d_"+model,cursor.Columns);
    }
    [Fact]
    public void RejectsAmbiguousMappingAndSupportsCancellation()
    {
        string input=Path.Combine(folder,"input.dlb");
        using(var writer=new CompactBankWriter(input)){var id=writer.AddTable(["ID","DAP","H","h","d"]);writer.Add(id,["1",20,25,1,20]);writer.Complete();}
        Assert.Throws<InvalidDataException>(()=>TaperAdjustment.Run(new(input),new("ID","DAP","H","H","d","",["kozak_model"]),Path.Combine(folder,"bad.dlb")));
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        Assert.Throws<OperationCanceledException>(()=>TaperAdjustment.Run(new(input),new("ID","DAP","H","h","d","",["kozak_model"]),Path.Combine(folder,"cancel.dlb"),cancel.Token));
    }
}
