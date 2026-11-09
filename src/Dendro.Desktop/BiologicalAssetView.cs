using System.IO;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Dendro.Core.Configuration;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal sealed class BiologicalAssetView : ScrollViewer
{
    private readonly MainWindow owner;private readonly ProjectSession session;private string[] paths=[];private bool loading;
    private readonly ComboBox basis=Ui.Combo([]),processed=Ui.Combo([]),baseSheet=Ui.Combo([]),processedSheet=Ui.Combo([]),baseKey=Ui.Combo([]),curveId=Ui.Combo([]),plantingDate=Ui.Combo([]),processingKey=Ui.Combo([]),date=Ui.Combo([]),volume=Ui.Combo([]),inventory=Ui.Combo(["Todos","IFC","IFPC"]);
    private readonly StackPanel baseTables=new(){Visibility=Visibility.Collapsed},processedTables=new(){Visibility=Visibility.Collapsed};
    private readonly ListBox columns=new(){SelectionMode=SelectionMode.Multiple,MinHeight=150,MaxHeight=300};
    private readonly TextBox prefix=Ui.Input("INV_"),name=Ui.Input("Ativo_biologico");
    private readonly TextBox throughYear=Ui.Input((DateTime.Now.Year+5).ToString(CultureInfo.InvariantCulture));
    internal BiologicalAssetView(MainWindow owner,ProjectSession session)
    {
        this.owner=owner;this.session=session;var form=Ui.Form();Content=form;VerticalScrollBarVisibility=ScrollBarVisibility.Auto;
        form.Children.Add(owner.ToolHeader("Ativo biológico"));
        Ui.Section(form,"Tabelas de origem");
        Ui.Field(form,"Tabela base que receberá os resultados",basis);Ui.Field(baseTables,"Tabela ou aba no arquivo base",baseSheet);form.Children.Add(baseTables);
        Ui.Field(form,"Resultado do processamento",processed);Ui.Field(processedTables,"Nível / tabela processada",processedSheet);form.Children.Add(processedTables);
        Ui.Field(form,"Chave na tabela base",baseKey);Ui.Field(form,"ID da curva na base",curveId);Ui.Field(form,"Data de plantio na base",plantingDate);
        Ui.Field(form,"Chave no processamento",processingKey);Ui.Field(form,"Data de medição no processamento",date);Ui.Field(form,"Volume observado (m³/ha)",volume);
        Ui.Field(form,"Tipo de inventário para buscar",inventory);inventory.SelectedIndex=0;
        Ui.Field(form,"Colunas do processamento a trazer (selecione várias)",columns);
        Ui.Section(form,"Projeção e IMA");
        Ui.Field(form,"Projetar até o ano (data de referência: 1º de junho)",throughYear);
        Ui.Section(form,"Nome e gravação do resultado");
        Ui.Field(form,"Prefixo para as colunas trazidas",prefix);Ui.Field(form,"Nome da tabela de ativo biológico",name);
        Ui.AddHelp(form,"Mantém todas as linhas da base e busca a maior data de medição por chave. O ID da base escolhe a curva vigente de mesmo ID. A projeção usa Y₂ = Y₁ × F(I₂)/F(I₁) em 1º de junho de cada ano até o ano definido. O IMA é calculado para idades inteiras desde o plantio. A saída inclui as tabelas Ativo biológico, Projeções e IMA por idade.");
        form.Children.Add(Ui.AsyncButton("Gerar ativo biológico",()=>owner.Execute(Run)));
        basis.SelectionChanged+=async(_,_)=>{if(!loading)await owner.Execute(()=>LoadTables(false));};
        processed.SelectionChanged+=async(_,_)=>{if(!loading)await owner.Execute(()=>LoadTables(true));};
        baseSheet.SelectionChanged+=async(_,_)=>{if(!loading)await owner.Execute(()=>LoadColumns(false));};
        processedSheet.SelectionChanged+=async(_,_)=>{if(!loading)await owner.Execute(()=>LoadColumns(true));};
        foreach(var (control,id) in new[]{(basis,"Base"),(processed,"Processing"),(baseKey,"BaseKey"),(curveId,"CurveId"),(plantingDate,"PlantingDate"),(processingKey,"ProcessingKey"),(date,"Date"),(volume,"Volume")})System.Windows.Automation.AutomationProperties.SetAutomationId(control,"BiologicalAsset."+id);
        System.Windows.Automation.AutomationProperties.SetAutomationId(throughYear,"BiologicalAsset.ThroughYear");
    }
    internal async Task Initialize()
    {
        loading=true;paths=session.Files.Where(session.IsTabular).ToArray();basis.ItemsSource=processed.ItemsSource=paths.Select(p=>session.ContentName(p)).ToArray();
        var latest=session.Meta.Arr("processing_results").OrderByDescending(r=>r.S("created")).FirstOrDefault();
        processed.SelectedIndex=Array.IndexOf(paths,latest.S("path"));loading=false;
        if(processed.SelectedIndex>=0)await LoadTables(true);
    }
    private async Task LoadTables(bool processing)
    {
        var selector=processing?processed:basis;var sheet=processing?processedSheet:baseSheet;if(selector.SelectedIndex<0)return;
        string selectedPath=paths[selector.SelectedIndex];
        var names=await Task.Run(()=>TableStore.Tables(session.Resolve(selectedPath)));
        if(processing){var results=names.Where(n=>n.StartsWith("DIRETO -",StringComparison.Ordinal)||n.StartsWith("RESULTADO -",StringComparison.Ordinal)).ToArray();if(results.Length>0)names=results;}
        loading=true;sheet.ItemsSource=names;string previous=session.ExcelOptions(paths[selector.SelectedIndex]).S("sheet");sheet.SelectedItem=names.Contains(previous)?previous:names.FirstOrDefault();
        (processing?processedTables:baseTables).Visibility=names.Length>1?Visibility.Visible:Visibility.Collapsed;loading=false;await LoadColumns(processing);
    }
    private async Task LoadColumns(bool processing)
    {
        var selected=Source(processing);var names=await Task.Run(()=>{using var cursor=new TableCursor(selected);return cursor.Columns;});
        var key=processing?processingKey:baseKey;key.ItemsSource=names;key.SelectedItem=names.FirstOrDefault(c=>c is "Talhão" or "Chave");if(key.SelectedIndex<0&&names.Length>0)key.SelectedIndex=0;
        if(!processing)
        {
            curveId.ItemsSource=names;curveId.SelectedItem=names.FirstOrDefault(c=>c is "ID_Curva" or "ID Curva" or "Codigo_Curva" or "Código da curva");
            string configuredPlanting=session.Data!.At("cadastro.colunas").S("data_plantio","Data de Plantio");
            plantingDate.ItemsSource=names;plantingDate.SelectedItem=names.FirstOrDefault(c=>c==configuredPlanting)
                ??names.FirstOrDefault(c=>c is "Data_Plantio" or "Data de Plantio" or "Data Plantio");
        }
        if(processing)
        {
            date.ItemsSource=names;date.SelectedItem=names.FirstOrDefault(c=>c is "Data_Medicao" or "Data" or "Data de medição");
            volume.ItemsSource=names;volume.SelectedItem=names.FirstOrDefault(c=>c is "Volume_m3_ha" or "VTCC_ha" or "VCCC_ha");
            columns.ItemsSource=names;foreach(string c in names.Where(c=>c is "Volume_m3_ha" or "VTCC_ha" or "Fustes_ha" or "VMI_m3_fuste" or "VMI" or "Idade_anos"))columns.SelectedItems.Add(c);
            inventory.IsEnabled=names.Contains("Tipo_Inventario");inventory.SelectedIndex=inventory.IsEnabled?session.Data!.At("processador").S("inventory_type") switch{"IFC"=>1,"IFPC"=>2,_=>0}:0;
        }
    }
    private TableSource Source(bool processing)
    {
        var choice=processing?processed:basis;var sheet=processing?processedSheet:baseSheet;
        if(choice.SelectedIndex<0||sheet.SelectedItem is not string table)throw new InvalidOperationException("Selecione a tabela base e o resultado do processamento.");
        string path=paths[choice.SelectedIndex];return new(session.Resolve(path),table,session.IsSpreadsheet(path)?(int)session.ExcelOptions(path).N("header_row",1):1);
    }
    internal async Task Run()
    {
        var source=Source(false);var processing=Source(true);
        if(baseKey.SelectedItem is not string b||processingKey.SelectedItem is not string p||date.SelectedItem is not string d
            )throw new InvalidOperationException("Escolha as chaves e a coluna de data de medição. Resultados antigos sem Data_Medicao precisam ser reprocessados.");
        bool projectionRequested=curveId.SelectedItem is string||plantingDate.SelectedItem is string;
        string curveColumn=curveId.SelectedItem as string??"",plantedColumn=plantingDate.SelectedItem as string??"",volumeColumn=volume.SelectedItem as string??"";
        int finalYear=0;
        if(projectionRequested&&(curveColumn.Length==0||plantedColumn.Length==0||volumeColumn.Length==0))
            throw new InvalidOperationException("Para projetar, escolha o ID da curva, a data de plantio e o volume observado.");
        if(projectionRequested&&(!int.TryParse(throughYear.Text.Trim(),NumberStyles.Integer,CultureInfo.CurrentCulture,out finalYear)
            ||finalYear<1800||finalYear>9999))throw new InvalidOperationException("Informe um ano final entre 1800 e 9999.");
        string outputName=TableTools.SuggestedName(name.Text);if(string.IsNullOrWhiteSpace(outputName))throw new InvalidOperationException("Informe o nome do resultado.");
        string path=Path.Combine(session.ProjectWorkFolder(),outputName+".dlb");
        if(CompactBankFile.Exists(session.Resolve(Path.Combine(session.Meta.S("database_path"),outputName+".dlb"))))throw new InvalidOperationException("Já existe uma tabela com esse nome no arquivo do projeto. Escolha outro nome.");
        var curves=projectionRequested?ActiveCurves():[];
        if(projectionRequested&&curves.Length==0)throw new InvalidOperationException("Cadastre e marque como vigente ao menos uma curva de crescimento com ID correspondente aos talhões.");
        var selectedColumns=columns.SelectedItems.Cast<string>().Concat(volumeColumn.Length>0?[volumeColumn]:[]).Distinct(StringComparer.Ordinal).ToArray();
        var options=new BiologicalAssetOptions(b,p,d,selectedColumns,prefix.Text,inventory.SelectedIndex==1?"IFC":inventory.SelectedIndex==2?"IFPC":"",
            true,plantedColumn,curveColumn,volumeColumn,finalYear,curves);
        var progress=owner.StartTableOperation("Buscando as medições mais recentes para o ativo biológico…");var cancellation=owner.OperationCancellation;
        try
        {
            var result=await Task.Run(()=>BiologicalAsset.Join(source,processing,options,path,cancellation,progress));
            await session.AddTableResults([new(path,"Ativo biológico",result.BaseRows)],paths[basis.SelectedIndex],source.Table);
            string reference=Path.Combine(session.Meta.S("database_path"),Path.GetFileName(path));
            await owner.OpenTable(reference,"Ativo biológico");
            owner.Log(projectionRequested?$"Ativo biológico: {result.Linked:N0} vinculados; {result.ProjectedRows:N0} projeções anuais; {result.ImaRows:N0} idades de IMA."
                :$"Ativo biológico: {result.Linked:N0} vinculados; {result.Missing:N0} sem medição válida; {result.Ambiguous:N0} conflitos na data mais recente.");
        }
        finally{if(File.Exists(path))File.Delete(path);}
    }

    private BiologicalAssetCurve[] ActiveCurves()
    {
        var result=new List<BiologicalAssetCurve>();
        foreach(var model in session.Data!.Obj("curvas_crescimento").Arr("equacoes").OfType<JsonObject>().Where(m=>m.B("vigente",true)))
        {
            var inputs=ModelDefinition.Inputs(model,true).OfType<JsonObject>().ToArray();
            var age=inputs.SingleOrDefault(i=>i.S("simbolo")=="t");
            if(age==null)throw new InvalidOperationException($"A curva {model.S("codigo")} precisa ter exatamente uma entrada de idade (t).");
            var fixedInputs=new Dictionary<string,double>(StringComparer.Ordinal);
            var savedFixed=model["fixed_inputs"] as JsonObject;
            foreach(var input in inputs.Where(i=>i.S("simbolo")!="t"))
            {
                string symbol=input.S("simbolo");
                if(savedFixed?[symbol] is null||!double.IsFinite(savedFixed.N(symbol)))
                    throw new InvalidOperationException($"A curva {model.S("codigo")} usa a entrada {symbol}; a projeção de ativo biológico requer curvas em função apenas da idade.");
                fixedInputs[symbol]=savedFixed.N(symbol);
            }
            string unit=age.S("coluna") switch{"idade_anos"=>"anos","idade_meses"=>"meses",_=>age.S("unidade",model.S("unidade_idade","meses"))};
            if(unit is not("anos" or "meses"))throw new InvalidOperationException($"Unidade de idade inválida na curva {model.S("codigo")}.");
            var coefficients=Enumerable.Range(0,4).Where(i=>model[$"b{i}"]!=null).Select(i=>model.N($"b{i}")).ToArray();
            result.Add(new(model.S("codigo"),ModelDefinition.FormulaText(model,true),coefficients,unit,age.N("fator",1),fixedInputs));
        }
        return result.ToArray();
    }
}
