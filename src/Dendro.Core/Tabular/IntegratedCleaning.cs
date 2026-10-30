using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Dendro.Core.Configuration;
using Dendro.Core.Regression;

namespace Dendro.Core.Tabular;

/// <summary>An immutable table snapshot and a single reversible row mask shared by all plots.</summary>
public sealed class IntegratedCleaning : IDisposable
{
    public TableSource Original { get; }
    public TableSource Snapshot { get; }
    public JsonObject[] Charts { get; }
    public int RowCount { get; }
    public int RemovedCount => removed.Count;
    public bool CanUndo => history.Count > 0;
    private readonly HashSet<int> removed = [];
    private readonly Stack<int[]> history = new();
    private bool disposed;

    private IntegratedCleaning(TableSource original, string snapshot, JsonObject[] charts, int rows)
    { Original = original; Snapshot = new(snapshot,"Dados"); Charts = charts; RowCount = rows; }

    public static IntegratedCleaning Create(TableSource source, IReadOnlyList<JsonObject> templates, string snapshot,
        CancellationToken cancellation = default, IProgress<TableProgress>? progress = null)
    {
        if (templates.Count < 2) throw new InvalidDataException("Selecione pelo menos dois gráficos da mesma tabela.");
        if (File.Exists(snapshot)) throw new IOException("A cópia de trabalho já existe.");
        using var cursor = new TableCursor(source);
        int Column(string name) => Array.IndexOf(cursor.Columns,name) is int i && i >= 0 ? i : throw new InvalidDataException("Coluna ausente: "+name);
        var configs = templates.Select(t=>t.DeepClone().AsObject()).ToArray();
        var fields = configs.Select(c=>(X:Column(c.S("x")),Y:Column(c.S("y")),Group:c.S("stratum").Length>0?Column(c.S("stratum")):-1)).ToArray();
        foreach(var c in configs)
        {
            foreach(string key in new[]{"excluded","fits","fit_signature","selected_fit","selected_fits","frontier_points","frontier"})c.Remove(key);
            c["show_removed"] = false;
        }
        var points = configs.Select(_=>new JsonArray()).ToArray();var invalid = new int[configs.Length];
        var updates = new TableProgressReporter(progress);int line = 0;
        double Number(object? value) => double.TryParse(TableCursor.Text(value).Trim().Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out double n)?n:double.NaN;
        try
        {
            using(var writer = new CompactBankWriter(snapshot))
            {
                string table = writer.AddTable(cursor.Columns,"Dados");
                foreach(var row in cursor.Rows())
                {
                    cancellation.ThrowIfCancellationRequested();line=checked(line+1);writer.Add(table,row);
                    for(int i=0;i<configs.Length;i++)
                    {
                        var c=configs[i];var f=fields[i];string group=f.Group<0?"Global":TableCursor.Text(row[f.Group]);
                        if((c.B("group_selected")||c.S("group").Length>0)&&c.S("group")!=group)continue;
                        double x=Number(row[f.X]),y=Number(row[f.Y]);
                        if(!double.IsFinite(x)||!double.IsFinite(y)||(c.B("log_x")&&x<=0)||(c.B("log_y")&&y<=0)){invalid[i]++;continue;}
                        points[i].Add(new JsonObject{["x"]=x,["y"]=y,["row"]=line,["group"]=group,["excluded"]=false});
                    }
                    if((line&2047)==0)updates.Report(cursor.TotalRows>0?95d*line/cursor.TotalRows:null,$"Preparando gráficos vinculados: {line:N0} linhas",line);
                }
                cancellation.ThrowIfCancellationRequested();writer.Complete();
            }
            var charts=new JsonObject[configs.Length];
            for(int i=0;i<configs.Length;i++)
            {
                if(points[i].Count==0)throw new InvalidDataException("Gráfico sem pares numéricos válidos: "+configs[i].S("title"));
                var bounds=ChartBounds.Calculate(points[i].Select(p=>(p.N("x"),p.N("y"))),configs[i]);
                _=ChartTicks.Create(bounds.XMin,bounds.XMax,configs[i].N("x_tick_step"),configs[i].B("log_x"));
                _=ChartTicks.Create(bounds.YMin,bounds.YMax,configs[i].N("y_tick_step"),configs[i].B("log_y"));
                if(configs[i].S("stratum").Length>0)
                {
                    var colors=configs[i]["group_colors"]?.DeepClone().AsObject()??new JsonObject();int color=0;
                    foreach(string group in points[i].Select(p=>p.S("group")).Distinct().Order(StringComparer.Ordinal))
                        if(colors[group]==null)colors[group]=ChartPalette.At(color++);
                    configs[i]["group_colors"]=colors;
                }
                charts[i]=new JsonObject{["chart"]=configs[i],["points"]=points[i],["curves"]=new JsonArray(),["invalid"]=invalid[i]};
            }
            updates.Report(100,$"Limpeza integrada: {line:N0} linhas e {charts.Length} gráficos",line,true);
            return new(source,snapshot,charts,line);
        }
        catch { if(File.Exists(snapshot))File.Delete(snapshot);throw; }
    }

    public int Remove(IEnumerable<int> rows)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        var requested=rows.Distinct().ToArray();
        if(requested.Any(r=>r<1||r>RowCount))throw new InvalidDataException("Linha fora da cópia de trabalho.");
        int[] added=requested.Where(r=>!removed.Contains(r)).ToArray();
        if(added.Length==0)return 0;
        foreach(int row in added)removed.Add(row);history.Push(added);Mark(added,true);return added.Length;
    }

    public int Undo()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(!history.TryPop(out var rows))return 0;
        foreach(int row in rows)removed.Remove(row);Mark(rows,false);return rows.Length;
    }

    private void Mark(int[] rows,bool excluded)
    {
        foreach(var chart in Charts)
        {
            var points=chart["points"]!.AsArray();
            foreach(int row in rows)
            {
                int lo=0,hi=points.Count-1;
                while(lo<=hi){int mid=lo+(hi-lo)/2;int id=(int)points[mid].N("row");if(id==row){points[mid]!["excluded"]=excluded;break;}if(id<row)lo=mid+1;else hi=mid-1;}
            }
        }
    }

    public void Export(string output,CancellationToken cancellation=default,IProgress<TableProgress>? progress=null)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        string absolute=Path.GetFullPath(output);
        if(new[]{Original.Path,Snapshot.Path,CompactBankFile.Location(Original.Path).File}.Any(p=>string.Equals(Path.GetFullPath(p),absolute,StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Escolha outro arquivo para preservar a tabela original e a cópia de trabalho.");
        string extension=Path.GetExtension(output).ToLowerInvariant();
        if(extension is not(".xlsx" or ".csv" or ".dlb"))throw new InvalidDataException("Exporte em Excel, CSV ou DLB.");
        var exclusions=removed.ToHashSet();var updates=new TableProgressReporter(progress);
        ProjectStore.AtomicWrite(output,temporary=>
        {
            using var cursor=new TableCursor(Snapshot);
            IEnumerable<object?[]> Rows()
            {
                int line=0;
                foreach(var row in cursor.Rows())
                {
                    cancellation.ThrowIfCancellationRequested();line++;
                    if((line&2047)==0)updates.Report(99d*line/Math.Max(1,RowCount),$"Exportando tabela limpa: {line:N0} linhas",line);
                    if(!exclusions.Contains(line))yield return row;
                }
            }
            if(extension==".dlb")
            { using var writer=new CompactBankWriter(temporary);string id=writer.AddTable(cursor.Columns,"Dados");foreach(var row in Rows())writer.Add(id,row);cancellation.ThrowIfCancellationRequested();writer.Complete(); }
            else if(extension==".csv")
            { using var writer=new StreamWriter(temporary,false,new UTF8Encoding(true));string Cell(object? value)=>"\""+TableCursor.Text(value).Replace("\"","\"\"")+"\"";writer.WriteLine(string.Join(";",cursor.Columns.Select(c=>Cell(c))));foreach(var row in Rows())writer.WriteLine(string.Join(";",row.Select(Cell))); }
            else
            { if(!TableTools.FitsExcel(RowCount-exclusions.Count,cursor.Columns.Length))throw new InvalidOperationException("A tabela ultrapassa o limite do Excel. Exporte em CSV ou DLB.");StreamingExcelWriter.Write(temporary,cursor.Columns,Rows(),cancellation); }
            cancellation.ThrowIfCancellationRequested();
        },true);
        updates.Report(100,$"Tabela limpa exportada: {RowCount-exclusions.Count:N0} linhas",RowCount,true);
    }

    public void Dispose(){if(disposed)return;disposed=true;if(File.Exists(Snapshot.Path))File.Delete(Snapshot.Path);}
}
