using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Dendro.Core.Configuration;
using Dendro.Core.Processing;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal sealed class InventoryAuditView : ScrollViewer
{
    private readonly MainWindow owner;
    private readonly ProjectSession session;
    private readonly ComboBox result = Ui.Combo([]), resultSheet = Ui.Combo([]), origin = Ui.Combo([]), originSheet = Ui.Combo([]);
    private readonly StackPanel resultTableField = new() { Visibility=Visibility.Collapsed }, originTableField = new() { Visibility=Visibility.Collapsed };
    private readonly CheckBox checkOrigin = new() { Content="Conferir árvores e dimensões na tabela de origem", IsChecked=true };
    private readonly ListBox peers = new() { SelectionMode=SelectionMode.Multiple, MaxHeight=160, MinHeight=90 };
    private readonly ListBox temporal = new() { SelectionMode=SelectionMode.Multiple, MaxHeight=160, MinHeight=90 };
    private readonly TextBox volume=Ui.Input("1000"), height=Ui.Input("50"), dap=Ui.Input("80"), density=Ui.Input("2000"),
        age=Ui.Input("5"), minimum=Ui.Input("8"), iqr=Ui.Input("3"), limit=Ui.Input("200"), decline=Ui.Input("0");
    private string[] paths=[];
    private bool loading;
    internal InventoryAuditView(MainWindow owner,ProjectSession session)
    {
        this.owner=owner; this.session=session;
        var form=Ui.Form(); Content=form; VerticalScrollBarVisibility=ScrollBarVisibility.Auto;
        form.Children.Add(owner.ToolHeader("Auditoria pós-processamento"));
        Ui.Section(form,"Origens da auditoria");
        Ui.Field(form,"Processamento para auditar",result);
        Ui.Field(resultTableField,"Resultado por parcela no arquivo importado",resultSheet); form.Children.Add(resultTableField);
        form.Children.Add(checkOrigin); Ui.Field(form,"Tabela de origem",origin);
        Ui.Field(originTableField,"Tabela dentro do arquivo importado",originSheet); form.Children.Add(originTableField);
        Ui.AddHelp(form,"Selecionar o processamento recupera automaticamente sua tabela de origem. Tabelas do arquivo do projeto, como Tabelas_unidas, são usadas diretamente: não é necessário escolher outra tabela dentro delas. Arquivos externos com várias tabelas ou abas permitem escolher uma delas quando necessário. A origem é lida em sequência, sem modificar os dados ou executar RNA. Usa as chaves e unidades da configuração selecionada; revisões com fatores específicos exigem conferir a versão.");
        Ui.Field(form,"Colunas do grupo de referência (selecione várias)",peers);
        Ui.AddHelp(form,"Compara apenas parcelas das mesmas colunas e faixa de idade. Lote, Fonte e tipo evitam misturar bases. Inclua espécie, regime ou material genético quando essas colunas existirem. A parcela participa do próprio grupo; grupos pequenos ficam sem limiar estatístico.");
        Ui.Section(form,"Grupo de referência");
        Ui.Field(form,"Volume para triagem (m³/ha)",volume); Ui.Field(form,"Altura para triagem na origem (m)",height);
        Ui.Field(form,"DAP para triagem na origem (cm)",dap); Ui.Field(form,"Densidade para triagem (fustes/ha)",density);
        Ui.Field(form,"Largura da faixa de idade (anos)",age); Ui.Field(form,"Mínimo de parcelas no grupo",minimum);
        Ui.Field(form,"Multiplicador do IQR",iqr); Ui.Field(form,"Máximo de parcelas para conferir na origem",limit);
        Ui.Field(form,"Colunas que identificam a mesma parcela entre medições",temporal);
        Ui.Field(form,"Tolerância para sinalizar queda de volume (%)",decline);
        Ui.Section(form,"Executar auditoria");
        Ui.AddHelp(form,"O vínculo temporal normalmente usa talhão, parcela, lote e tipo. Fonte pode mudar de ano para ano; inclua um identificador permanente quando houver parcelas diferentes com o mesmo nome. Mostra volume anterior/atual, diferença, incremento anual e mudanças em densidade e VMI. Datas/idades repetidas ficam ambíguas e interrompem a sequência.");
        Ui.AddHelp(form,"Os valores iniciais são filtros de triagem editáveis, sem significado de limite biológico. Sinaliza volume > limiar, volume > Q3 + multiplicador × IQR, densidade alta e inconsistências aritméticas. Confere na origem as parcelas sinalizadas de maior volume, até o limite escolhido; as demais ficam identificadas como não conferidas.");
        form.Children.Add(Ui.AsyncButton("Auditar e salvar evidências",()=>owner.Execute(Run)));
        result.SelectionChanged+=async(_,_)=>{if(!loading)await owner.Execute(()=>SelectResult());};
        origin.SelectionChanged+=async(_,_)=>{if(!loading)await owner.Execute(()=>LoadTables(true));};
        resultSheet.SelectionChanged+=async(_,_)=>{if(!loading)await owner.Execute(LoadColumns);};
        System.Windows.Automation.AutomationProperties.SetAutomationId(result,"Audit.Result");
        System.Windows.Automation.AutomationProperties.SetAutomationId(origin,"Audit.Origin");
        System.Windows.Automation.AutomationProperties.SetAutomationId(originSheet,"Audit.OriginSheet");
        System.Windows.Automation.AutomationProperties.SetAutomationId(checkOrigin,"Audit.CheckOrigin");
        System.Windows.Automation.AutomationProperties.SetAutomationId(volume,"Audit.Volume");
    }
    internal async Task Initialize(string? path=null,string? sheet=null)
    {
        loading=true;
        paths=session.Files.Where(session.IsTabular).ToArray();
        result.ItemsSource=paths.Select(p=>ResultLabel(p)).ToArray();
        origin.ItemsSource=paths.Select(p=>session.ContentName(p)).ToArray();
        var latest=Results().FirstOrDefault(r=>path!=null && SamePath(r.S("path"),path))
            ?? Results().FirstOrDefault(r=>path!=null && SamePath(r.S("source_path"),path)) ?? Results().FirstOrDefault();
        string selected=latest.S("path",path??"");
        result.SelectedIndex=Array.FindIndex(paths,p=>SamePath(p,selected));
        loading=false;
        if(result.SelectedIndex>=0)await SelectResult(sheet);
    }
    private bool SamePath(string a,string b) => a.Length>0 && b.Length>0 && string.Equals(session.Resolve(a),session.Resolve(b),StringComparison.OrdinalIgnoreCase);
    private IEnumerable<JsonObject> Results()=>session.Meta.Arr("processing_results").OfType<JsonObject>()
        .Where(r=>r.Arr("sheets").Any(s=>s.Display() is "DIRETO - PARCELAS" or ProcessingHistory.Plots))
        .OrderByDescending(r=>DateTimeOffset.TryParse(r.S("created"),out var date)?date:DateTimeOffset.MinValue);
    private string ResultLabel(string path)
    {
        var run=Results().FirstOrDefault(r=>SamePath(r.S("path"),path));
        if(run==null)return session.ContentName(path);
        string table=run.Arr("sheets").Select(s=>s.Display()).First(s=>s is "DIRETO - PARCELAS" or ProcessingHistory.Plots);
        return session.ContentName(run.S("source_path"))+" → "+table+(run.N("number")>0?$" · V{run.N("number"):0}":"");
    }
    private async Task SelectResult(string? preferred=null)
    {
        if(result.SelectedIndex<0)return;
        var run=Results().FirstOrDefault(r=>SamePath(r.S("path"),paths[result.SelectedIndex]));
        loading=true;
        origin.SelectedIndex=run==null?-1:Array.FindIndex(paths,p=>SamePath(p,run.S("source_path")));
        originSheet.ItemsSource=Array.Empty<string>(); originTableField.Visibility=Visibility.Collapsed;
        loading=false;
        await LoadTables(false,preferred);
        if(origin.SelectedIndex>=0)await LoadTables(true,run.S("source_sheet"));
    }
    private async Task LoadTables(bool input,string? preferred=null)
    {
        var combo=input?origin:result; var table=input?originSheet:resultSheet;
        if(combo.SelectedIndex<0)return;
        string path=paths[combo.SelectedIndex];
        var names=await Task.Run(()=>TableStore.Tables(session.Resolve(path)));
        if(!input)
        {
            var parcels=names.Where(n=>n is "DIRETO - PARCELAS" or ProcessingHistory.Plots).ToArray();
            if(parcels.Length>0)names=parcels;
        }
        loading=true;
        table.ItemsSource=names;
        string saved=string.IsNullOrWhiteSpace(preferred)?session.ExcelOptions(path).S("sheet"):preferred;
        table.SelectedItem=names.Contains(saved)?saved:names.FirstOrDefault();
        if(table.SelectedIndex<0&&names.Length>0)table.SelectedIndex=0;
        (input?originTableField:resultTableField).Visibility=names.Length>1?Visibility.Visible:Visibility.Collapsed;
        loading=false;
        if(!input)await LoadColumns();
    }
    private async Task LoadColumns()
    {
        if(result.SelectedIndex<0||resultSheet.SelectedItem is not string sheet)return;
        var table=new TableSource(session.Resolve(paths[result.SelectedIndex]),sheet);
        var columns=await Task.Run(()=>{using var c=new TableCursor(table);return c.Columns;});
        peers.ItemsSource=temporal.ItemsSource=columns;
        foreach(string column in new[]{"Lote","Base_Origem","Tipo_Inventario"})if(columns.Contains(column))peers.SelectedItems.Add(column);
        foreach(string column in new[]{"Talhão","Parcela","Lote","Tipo_Inventario"})if(columns.Contains(column))temporal.SelectedItems.Add(column);
    }
    private static int Integer(TextBox box)
    {
        double n=Ui.Number(box.Text);if(!double.IsFinite(n)||n!=Math.Truncate(n)||n<int.MinValue||n>int.MaxValue)throw new InvalidDataException("Informe parâmetros inteiros válidos."); return(int)n;
    }
    internal async Task Run()
    {
        if(result.SelectedIndex<0||resultSheet.SelectedItem is not string sheet)throw new InvalidDataException("Selecione o resultado por parcela.");
        if(checkOrigin.IsChecked==true&&(origin.SelectedIndex<0||originSheet.SelectedItem is not string))throw new InvalidDataException("Selecione a tabela original de árvores.");
        if(peers.SelectedItems.Count==0)throw new InvalidDataException("Selecione as colunas do grupo de referência.");
        if(temporal.SelectedItems.Count==0)throw new InvalidDataException("Selecione as colunas de identidade da parcela entre medições.");
        string path=paths[result.SelectedIndex];
        var options=new InventoryAuditOptions(Ui.Number(volume.Text),Ui.Number(height.Text),Ui.Number(dap.Text),Ui.Number(density.Text),Ui.Number(age.Text),Integer(minimum),Ui.Number(iqr.Text),Integer(limit),peers.SelectedItems.Cast<string>().ToArray(),Ui.Number(decline.Text),temporal.SelectedItems.Cast<string>().ToArray());
        var configuration=ProcessingHistory.Freeze(session.Data!);
        TableSource? source=checkOrigin.IsChecked==true?new(session.Resolve(paths[origin.SelectedIndex]),originSheet.SelectedItem!.ToString()!,session.IsSpreadsheet(paths[origin.SelectedIndex])?(int)session.ExcelOptions(paths[origin.SelectedIndex]).N("header_row",1):1):null;
        var progress=owner.StartTableOperation("Auditando resultados do inventário…"); var cancellation=owner.OperationCancellation;
        var audit=await Task.Run(()=>InventoryAudit.Run(TableStore.Read(new(session.Resolve(path),sheet)),configuration,source,options,cancellation,(p,m)=>progress.Report(new(p,m))));
        string temporary=Path.Combine(Path.GetTempPath(),"Auditoria_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+"_"+Guid.NewGuid().ToString("N")[..6]+".dlb");
        string reference;
        try
        {
            await Task.Run(()=>{
                using var writer=new CompactBankWriter(temporary);
                foreach(var (name,table) in new[]{("AUDITORIA - PARCELAS",audit.Plots),("AUDITORIA - ARVORES",audit.Trees),("AUDITORIA - EVOLUCAO",audit.Evolution),("AUDITORIA - INFORMACOES",audit.Information)})
                {
                    cancellation.ThrowIfCancellationRequested();
                    string id=writer.AddTable(table.Rows[0].Select(v=>v?.ToString()??"").ToArray(),name);
                    foreach(var row in table.Rows.Skip(1)){cancellation.ThrowIfCancellationRequested();writer.Add(id,row.ToArray());}
                }
                writer.Complete();
            });
            reference=await session.CommitBankFile(temporary); session.AddFile(reference);
        }
        finally { if(File.Exists(temporary))File.Delete(temporary); }
        var definition=new JsonObject { ["kind"]="inventory_audit", ["path"]=path, ["sheet"]=sheet, ["configuration"]=configuration,
            ["source_path"]=source==null?"":paths[origin.SelectedIndex], ["source_sheet"]=source?.Table??"", ["source_header"]=source?.HeaderRow??1,
            ["options"]=JsonSerializer.SerializeToNode(options), ["evidence_path"]=reference };
        AnalysisDocuments.Show(owner,session,InventoryAudit.Presentation(audit),definition);
        owner.Log($"Auditoria salva: {audit.Plots.Count-1:N0} parcelas · {audit.Trees.Count-1:N0} árvores conferidas. Evidências no banco {session.ContentName(reference)}.");
    }
}
