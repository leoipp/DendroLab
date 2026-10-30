using Dendro.Core.Spreadsheets;
using Dendro.Core.Tabular;

namespace Dendro.Core.Regression;

public sealed record RegressionFilterPreview(int[] Rows, DataTable Preview);

public static class RegressionPointFilter
{
    public static RegressionFilterPreview Select(TableSource source, IReadOnlyList<TableFilter> filters,
        IEnumerable<int> eligibleRows, CancellationToken cancellation = default, IProgress<TableProgress>? progress = null)
    {
        if(filters.Count==0)throw new InvalidDataException("Adicione pelo menos um filtro para selecionar pontos.");
        var eligible=eligibleRows.ToHashSet(); var selected=new List<int>();
        using var cursor=new TableCursor(source);
        var matches=TableTools.Predicate(cursor.Columns,filters);
        string rowColumn="Linha de origem";while(cursor.Columns.Contains(rowColumn))rowColumn+="_";
        var preview=new DataTable(new[]{rowColumn}.Concat(cursor.Columns));
        var updates=new TableProgressReporter(progress); int row=0;
        foreach(var values in cursor.Rows())
        {
            cancellation.ThrowIfCancellationRequested();row=checked(row+1);
            if(eligible.Contains(row)&&matches(values))
            {
                selected.Add(row);
                if(preview.Count<=200)preview.Add(new object?[]{(double)(source.HeaderRow+row)}.Concat(values).ToArray());
            }
            if((row&2047)==0)updates.Report(cursor.TotalRows>0?99d*row/cursor.TotalRows:null,$"Filtrando pontos: {row:N0} linhas · {selected.Count:N0} encontrados",row);
        }
        updates.Report(100,$"Filtro concluído: {selected.Count:N0} pontos.",row,true);
        return new(selected.ToArray(),preview);
    }
}
