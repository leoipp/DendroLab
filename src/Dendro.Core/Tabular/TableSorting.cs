using System.Globalization;

namespace Dendro.Core.Tabular;

public enum TableSortKind { Text, Number }
public sealed record TableSort(string Column, TableSortKind Kind = TableSortKind.Text, bool Descending = false);

/// <summary>External stable sort; at most 16 runs are merged simultaneously. Original row numbers survive paging/export.</summary>
internal static class TableSorting
{
    private readonly record struct Key(int Missing, double Number, string Text, long Row);
    private sealed record Entry(object?[] Values, Key Key);
    internal static long Create(TableSource source, IReadOnlyList<TableFilter> filters, TableSort sort, string output, string index,
        CancellationToken cancellation, IProgress<TableProgress>? progress, IReadOnlySet<long>? includedSourceRows = null)
    {
        string folder=Path.Combine(Path.GetDirectoryName(output)!,"DendroLab-sort-"+Guid.NewGuid().ToString("N"));
        var updates=new TableProgressReporter(progress);
        using var cursor=new TableCursor(source);
        int column=Array.IndexOf(cursor.Columns,sort.Column); if(column<0)throw new InvalidDataException("Coluna para ordenar não encontrada.");
        string rowColumn="__DENDRO_ROW"; while(cursor.Columns.Contains(rowColumn))rowColumn+="_";
        string[] columns=[..cursor.Columns,rowColumn];
        var matches=TableTools.Predicate(cursor.Columns,filters);
        Directory.CreateDirectory(folder);
        var textComparer=StringComparer.Create(CultureInfo.CurrentCulture,true);
        var comparer=Comparer<Key>.Create((a,b)=>{
            int c=a.Missing.CompareTo(b.Missing); if(c!=0)return c;
            c=sort.Kind==TableSortKind.Number && a.Missing==0?a.Number.CompareTo(b.Number):textComparer.Compare(a.Text,b.Text);
            if(c!=0)return sort.Descending?-Math.Sign(c):c;
            return a.Row.CompareTo(b.Row);
        });
        Key MakeKey(object?[] row)
        {
            string text=TableCursor.Text(row[column]).Trim(); bool empty=text.Length==0;
            bool valid=double.TryParse(text.Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out double n)&&double.IsFinite(n);
            return new(empty?2:sort.Kind==TableSortKind.Number&&!valid?1:0,n,text,Convert.ToInt64(row[^1],CultureInfo.InvariantCulture));
        }
        int serial=0; string NewRun()=>Path.Combine(folder,"run-"+(serial++)+".dlb");
        var runs=new List<string>(); var buffer=new List<Entry>(); long scanned=0,estimatedBytes=0,matched=0;
        void Flush()
        {
            if(buffer.Count==0)return; cancellation.ThrowIfCancellationRequested();
            buffer.Sort((a,b)=>comparer.Compare(a.Key,b.Key));
            string path=NewRun(); using var writer=new CompactBankWriter(path); string id=writer.AddTable(columns);
            foreach(var item in buffer){cancellation.ThrowIfCancellationRequested();writer.Add(id,item.Values);}
            writer.Complete();runs.Add(path);buffer.Clear();estimatedBytes=0;
        }
        void Merge(string[] inputs,string target,bool final)
        {
            var readers=new List<TableCursor>();var enumerators=new List<IEnumerator<object?[]>>();
            using var writer=new CompactBankWriter(target); using var rowIndex=final?new BinaryWriter(File.Create(index)):null;
            string id=writer.AddTable(final?cursor.Columns:columns);
            var queue=new PriorityQueue<(int Reader,object?[] Row),Key>(comparer);
            try
            {
                foreach(string path in inputs)
                {
                    var reader=new TableCursor(new(path));readers.Add(reader);var enumerator=reader.Rows().GetEnumerator();enumerators.Add(enumerator);
                    if(enumerator.MoveNext())queue.Enqueue((enumerators.Count-1,enumerator.Current),MakeKey(enumerator.Current));
                }
                long count=0;
                while(queue.TryDequeue(out var item,out var key))
                {
                    cancellation.ThrowIfCancellationRequested();
                    writer.Add(id,final?item.Row[..^1]:item.Row);rowIndex?.Write(key.Row);
                    var enumerator=enumerators[item.Reader]; if(enumerator.MoveNext())queue.Enqueue((item.Reader,enumerator.Current),MakeKey(enumerator.Current));
                    if((++count&2047)==0)updates.Report(final?80+19d*count/Math.Max(1,matched):null,$"Ordenando: {count:N0} linhas reunidas",count);
                }
                writer.Complete();
            }
            finally { foreach(var e in enumerators)e.Dispose();foreach(var r in readers)r.Dispose(); }
        }
        try
        {
            foreach(var row in cursor.Rows())
            {
                cancellation.ThrowIfCancellationRequested();scanned++;
                if((includedSourceRows==null||includedSourceRows.Contains(source.HeaderRow+scanned))&&matches(row))
                {
                    object?[] values=[..row,(double)(source.HeaderRow+scanned)];buffer.Add(new(values,MakeKey(values)));matched++;
                    estimatedBytes+=64+row.Length*24L+row.OfType<string>().Sum(s=>(long)s.Length*2);
                    if(buffer.Count>=20000||estimatedBytes>=16*1024*1024)Flush();
                }
                if((scanned&2047)==0)updates.Report(cursor.TotalRows>0?60d*scanned/cursor.TotalRows:null,$"Lendo para ordenar: {scanned:N0} linhas",scanned);
            }
            Flush();
            while(runs.Count>16)
            {
                var next=new List<string>();
                foreach(var batch in runs.Chunk(16)) {string path=NewRun();Merge(batch,path,false);next.Add(path);foreach(string old in batch)File.Delete(old);}
                runs=next;
            }
            Merge(runs.ToArray(),output,true);updates.Report(100,$"Ordenação concluída: {matched:N0} linhas.",matched,true);return matched;
        }
        finally { foreach(string file in Directory.EnumerateFiles(folder))File.Delete(file);Directory.Delete(folder); }
    }
}
