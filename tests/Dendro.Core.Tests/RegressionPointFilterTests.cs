using Dendro.Core.Regression;
using Dendro.Core.Tabular;

namespace Dendro.Core.Tests;

public sealed class RegressionPointFilterTests
{
    [Fact]
    public void FiltersOriginalColumnsAndOnlySelectsEligiblePointsWithoutChangingSource()
    {
        string path=Path.Combine(Path.GetTempPath(),"dendro-regression-filter-"+Guid.NewGuid().ToString("N")+".dlb");
        try
        {
            using(var writer=new CompactBankWriter(path))
            {
                string id=writer.AddTable(["DAP","H","Fonte"]);
                writer.Add(id,[20d,10d,"IFPC2025"]);writer.Add(id,[30d,15d,"IFC2025"]);
                writer.Add(id,[40d,20d,"IFPC2025"]);writer.Add(id,[50d,25d,"IFPC2025"]);writer.Complete();
            }
            byte[] before=File.ReadAllBytes(path);
            var preview=RegressionPointFilter.Select(new(path),[new("Fonte",FilterOperator.Contains,"IFPC"),new("DAP",FilterOperator.Greater, "25")],[1,2,3]);
            Assert.Equal(new[]{3},preview.Rows);Assert.Equal(2,preview.Preview.Count);
            Assert.Equal(4,preview.Preview.Number(1,0));Assert.Equal(40,preview.Preview.Number(1,1));
            Assert.Equal(before,File.ReadAllBytes(path));
            Assert.Throws<InvalidDataException>(()=>RegressionPointFilter.Select(new(path),[],[1]));
        }
        finally{if(File.Exists(path))File.Delete(path);}
    }
}
