using System.Globalization;
using Dendro.Core.Mathematics;

namespace Dendro.Core.Tabular;

public sealed record BiologicalAssetOptions(string BaseKey,string ProcessingKey,string MeasurementDate,string[] Columns,string Prefix="INV_",string InventoryType="",bool TrimKeys=true);
public sealed record BiologicalAssetResult(long BaseRows,long Linked,long Missing,long Ambiguous,long InvalidDates,string Path);

/// <summary>Left join by exact key, selecting the newest measurement. Conflicting latest ties never pick an arbitrary observation.</summary>
public static class BiologicalAsset
{
    private sealed record Latest(double Date,object?[] Values,bool Conflict);
    private static double Date(object? value)
    {
        if(value is double d)return double.IsFinite(d)&&d>0&&d<=2958465?Math.Floor(d):double.NaN;
        string text=TableCursor.Text(value);
        double parsed=InventoryMath.Date(text);
        if(double.IsFinite(parsed))return parsed>0&&parsed<=2958465?Math.Floor(parsed):double.NaN;
        return DateTime.TryParse(text,CultureInfo.GetCultureInfo("pt-BR"),DateTimeStyles.None,out var date)?date.Date.ToOADate():double.NaN;
    }
    public static BiologicalAssetResult Join(TableSource basis,TableSource processed,BiologicalAssetOptions options,string output,
        CancellationToken cancellation=default,IProgress<TableProgress>? progress=null)
    {
        if(options.Columns.Length==0||options.Columns.Distinct().Count()!=options.Columns.Length)throw new InvalidDataException("Selecione colunas únicas para trazer do processamento.");
        if(options.InventoryType is not("" or "IFC" or "IFPC"))throw new InvalidDataException("Selecione IFC, IFPC ou todos os tipos.");
        if(File.Exists(output))throw new IOException("A tabela de saída já existe. Escolha outro nome.");
        using var source=new TableCursor(processed);using var target=new TableCursor(basis);
        int key=Array.IndexOf(source.Columns,options.ProcessingKey),dateCol=Array.IndexOf(source.Columns,options.MeasurementDate),baseKey=Array.IndexOf(target.Columns,options.BaseKey);
        int type=Array.IndexOf(source.Columns,"Tipo_Inventario");
        int[] fields=options.Columns.Select(c=>Array.IndexOf(source.Columns,c)).ToArray();
        if(key<0||dateCol<0||baseKey<0||fields.Any(c=>c<0))throw new InvalidDataException("Revise as chaves, a data de medição e as colunas selecionadas.");
        if(options.InventoryType.Length>0&&type<0)throw new InvalidDataException("A tabela processada não contém Tipo_Inventario para aplicar a seleção IFC/IFPC.");
        string[] headers=[..target.Columns,..options.Columns.Select(c=>options.Prefix+c),"AB_Data_Medicao","AB_Vinculo"];
        if(headers.Distinct(StringComparer.Ordinal).Count()!=headers.Length)throw new InvalidDataException("As colunas de saída já existem na base. Escolha outro prefixo ou outra tabela base.");
        string Key(object? value){string s=TableCursor.Text(value);return options.TrimKeys?s.Trim():s;}
        var latest=new Dictionary<string,Latest>(StringComparer.Ordinal);long read=0,invalid=0;
        int parcel=Array.IndexOf(source.Columns,"Parcela"),stand=Array.IndexOf(source.Columns,"Talhão");
        var units=new Dictionary<string,string>(StringComparer.Ordinal);var mixedUnits=new HashSet<string>(StringComparer.Ordinal);
        var updates=new TableProgressReporter(progress);
        foreach(var row in source.Rows())
        {
            cancellation.ThrowIfCancellationRequested();read++;
            if(options.InventoryType.Length>0&&TableCursor.Text(row[type]).Trim().ToUpperInvariant()!=options.InventoryType)continue;
            string id=Key(row[key]);if(id.Length==0)continue;
            double date=Date(row[dateCol]);if(!double.IsFinite(date)){invalid++;continue;}
            if(parcel>=0&&TableCursor.Text(row[parcel]).Length>0)
            {
                string unit=System.Text.Json.JsonSerializer.Serialize(new[]{stand>=0?TableCursor.Text(row[stand]):"",TableCursor.Text(row[parcel]),type>=0?TableCursor.Text(row[type]):""});
                if(units.TryGetValue(id,out string? previousUnit)&&previousUnit!=unit)mixedUnits.Add(id);else units[id]=unit;
            }
            var values=fields.Select(c=>row[c]).ToArray();
            if(!latest.TryGetValue(id,out var previous)||date>previous.Date)latest[id]=new(date,values,false);
            else if(date==previous.Date && !values.Select(TableCursor.Text).SequenceEqual(previous.Values.Select(TableCursor.Text)))latest[id]=previous with {Conflict=true};
            if((read&2047)==0)updates.Report(source.TotalRows>0?50d*read/source.TotalRows:null,$"Identificando medições mais recentes: {read:N0} linhas",read);
        }
        string temporary=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!,".ativo-"+Guid.NewGuid().ToString("N")+".tmp");
        long count=0,linked=0,missing=0,ambiguous=0;
        try
        {
            using(var writer=new CompactBankWriter(temporary))
            {
                string id=writer.AddTable(headers,"Ativo biológico");
                foreach(var row in target.Rows())
                {
                    cancellation.ThrowIfCancellationRequested();count++;
                    string code=Key(row[baseKey]);
                    bool exists=code.Length>0&&latest.TryGetValue(code,out _);var match=exists?latest[code]:null;
                    bool mixed=mixedUnits.Contains(code);
                    string state=match==null?"Sem medição válida":mixed?"Chave reúne parcelas distintas":match.Conflict?"Conflito na data mais recente":"Vinculado";
                    if(match==null)missing++;else if(match.Conflict||mixed)ambiguous++;else linked++;
                    writer.Add(id,[..row,..(match is {Conflict:false}&&!mixed?match.Values:new object?[fields.Length]),match==null?null:DateTime.FromOADate(match.Date).ToString("yyyy-MM-dd"),state]);
                    if((count&2047)==0)updates.Report(target.TotalRows>0?50+49d*count/target.TotalRows:null,$"Vinculando ativo biológico: {count:N0} linhas",count);
                }
                string info=writer.AddTable(["Item","Resultado"],"INFORMACOES");
                foreach(var pair in new (string,object?)[]{("Tabela base",basis.Path+" / "+basis.Table),("Tabela processada",processed.Path+" / "+processed.Table),("Chaves",options.BaseKey+" = "+options.ProcessingKey),("Data de medição",options.MeasurementDate),("Tipo de inventário",options.InventoryType.Length==0?"Todos":options.InventoryType),("Campos trazidos",string.Join("; ",options.Columns)),("Vinculados",linked),("Sem medição válida",missing),("Conflitos na data mais recente",ambiguous),("Linhas do processamento com data inválida",invalid),("Regra","Maior data de medição por chave. Empates com campos diferentes ficam sem valores vinculados; escolha o nível correto de processamento ou uma chave mais específica. Não agrega parcelas nem altera dados da origem." )})writer.Add(info,[pair.Item1,pair.Item2]);
                cancellation.ThrowIfCancellationRequested();writer.Complete();
            }
            File.Move(temporary,output);updates.Report(100,$"Ativo biológico: {linked:N0} vinculados; {missing:N0} sem medição; {ambiguous:N0} conflitos.",count,true);
            return new(count,linked,missing,ambiguous,invalid,output);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
