using System.IO;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Dendro.Core.Configuration;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal sealed class BiologicalAssetView : ToolFormView
{
    private readonly MainWindow owner;private readonly ProjectSession session;private string[] paths=[];private bool loading;
    private readonly ComboBox basis=Ui.Combo([]),processed=Ui.Combo([]),baseSheet=Ui.Combo([]),processedSheet=Ui.Combo([]),baseKey=Ui.Combo([]),curveId=Ui.Combo([]),plantingDate=Ui.Combo([]),processingKey=Ui.Combo([]),date=Ui.Combo([]),volume=Ui.Combo([]),inventory=Ui.Combo(["Todos","IFC","IFPC"]);
    private readonly StackPanel baseTables=new(){Visibility=Visibility.Collapsed},processedTables=new(){Visibility=Visibility.Collapsed};
    private readonly ListBox columns=new(){SelectionMode=SelectionMode.Multiple,MinHeight=150,MaxHeight=300};
    private readonly TextBox prefix=Ui.Input("INV_"),name=Ui.Input("Ativo_biologico");
    private readonly TextBox throughYear=Ui.Input((DateTime.Now.Year+5).ToString(CultureInfo.InvariantCulture));
    private readonly ComboBox projectionReference=Ui.Combo(["Meio do ano (30/06)","Final do ano (31/12)","Dia e mês personalizados"]);
    private readonly TextBox projectionDay=Ui.Input("30"),projectionMonth=Ui.Input("6");
    private readonly List<BiologicalAssetFallback> fallbacks = new();
    private readonly ListBox fallbackList = new() { MinHeight=60, MaxHeight=160 };
    private readonly CheckBox useModelWhenMissing = new() { Content="Se nenhuma fonte tiver medição válida, usar a curva diretamente" };
    internal BiologicalAssetView(MainWindow owner,ProjectSession session)
    {
        this.owner=owner;this.session=session;var form=Ui.Form();SetForm(form);
        form.Children.Add(owner.ToolHeader("Ativo biológico"));
        Ui.Section(form,"Tabelas de origem");
        Ui.Field(form,"Tabela base que receberá os resultados",basis);Ui.Field(baseTables,"Tabela ou aba no arquivo base",baseSheet);form.Children.Add(baseTables);
        Ui.Field(form,"Arquivo / conjunto do processamento",processed);Ui.Field(processedTables,"Tabela ou aba do processamento",processedSheet);form.Children.Add(processedTables);
        Ui.Section(form,"Vínculo entre a base e o processamento");
        Ui.Field(form,"Coluna de vínculo na base que receberá os resultados",baseKey);
        Ui.Field(form,"Coluna correspondente na tabela do processamento",processingKey);
        Ui.AddHelp(form,"Escolha uma coluna de cada tabela cujos valores identifiquem o mesmo talhão ou unidade. Exemplo: Código do talhão na base = Talhão no processamento. Os nomes das colunas podem ser diferentes; seus valores precisam corresponder. Para cada valor da base, será trazida a medição mais recente do processamento.");
        Ui.Field(form,"Data de medição na tabela do processamento",date);Ui.Field(form,"Volume observado no processamento (m³/ha)",volume);
        Ui.Field(form,"Tipo de inventário para buscar",inventory);inventory.SelectedIndex=0;
        Ui.Field(form,"Colunas do processamento a trazer (selecione várias)",columns);
        Ui.Section(form,"Fontes alternativas por ordem de prioridade");
        Ui.AddHelp(form,"Busca primeiro na fonte principal. Sem medição válida, tenta as alternativas abaixo na ordem indicada. Cada fonte usa a sua medição mais recente. Configure as duas chaves, data, volume, ID opcional e campos correspondentes em cada alternativa.");
        form.Children.Add(fallbackList);
        void RefreshFallbacks(int index=-1)
        {
            fallbackList.ItemsSource=fallbacks.Select((item,i)=>$"{i+1}. {item.Label}").ToArray();
            fallbackList.SelectedIndex=index;
        }
        async Task EditFallback(bool edit)
        {
            var source=Source(false);
            string[] baseColumns=await Task.Run(()=>{using var cursor=new TableCursor(source);return cursor.Columns;});
            int index=edit?fallbackList.SelectedIndex:-1;
            if(edit&&index<0)throw new InvalidOperationException("Selecione a fonte alternativa para editar.");
            string[] fields=columns.SelectedItems.Cast<string>().ToArray();
            await BiologicalAssetFallbackEditor.Show(owner,session,baseColumns,fields,item=>
            {
                if(index<0)fallbacks.Add(item);else fallbacks[index]=item;
                RefreshFallbacks(index<0?fallbacks.Count-1:index);
            },index<0?null:fallbacks[index]);
        }
        var fallbackActions=new WrapPanel();
        fallbackActions.Children.Add(Ui.AsyncButton("Adicionar fonte…",()=>owner.Execute(()=>EditFallback(false))));
        fallbackActions.Children.Add(Ui.AsyncButton("Editar…",()=>owner.Execute(()=>EditFallback(true))));
        fallbackActions.Children.Add(Ui.Button("Subir",()=>{int i=fallbackList.SelectedIndex;if(i<=0)return;(fallbacks[i-1],fallbacks[i])=(fallbacks[i],fallbacks[i-1]);RefreshFallbacks(i-1);}));
        fallbackActions.Children.Add(Ui.Button("Descer",()=>{int i=fallbackList.SelectedIndex;if(i<0||i>=fallbacks.Count-1)return;(fallbacks[i+1],fallbacks[i])=(fallbacks[i],fallbacks[i+1]);RefreshFallbacks(i+1);}));
        fallbackActions.Children.Add(Ui.Button("Remover fonte",()=>{int i=fallbackList.SelectedIndex;if(i<0)return;fallbacks.RemoveAt(i);RefreshFallbacks();}));
        form.Children.Add(fallbackActions);
        Ui.Section(form,"Projeção e IMA");
        Ui.Field(form,"Coluna com o ID da curva de crescimento e produção na base",curveId);
        Ui.AddHelp(form,"Selecione, na base que receberá os resultados, a coluna que contém o ID cadastrado em Crescimento e produção. O valor de cada linha será usado para localizar a curva vigente com esse mesmo ID e projetar o volume dessa unidade.");
        Ui.Field(form,"Data de plantio na base que receberá os resultados",plantingDate);
        Ui.Field(form,"Projetar até o ano",throughYear);
        form.Children.Add(useModelWhenMissing);
        var modelSettings=new StackPanel { Visibility=Visibility.Collapsed };
        Ui.AddHelp(modelSettings,"Sem medição em nenhuma fonte, começa no primeiro aniversário do plantio com V = f(1). O ano desse aniversário recebe o valor inicial, e os anos seguintes usam a referência anual escolhida, até o ano final. Usa o ID da curva e o plantio da base receptora. A tabela identifica Modelo direto em AB_Metodo; AB_Data_Medicao fica vazia.");
        form.Children.Add(modelSettings);
        useModelWhenMissing.Checked+=(_,_)=>modelSettings.Visibility=Visibility.Visible;
        useModelWhenMissing.Unchecked+=(_,_)=>modelSettings.Visibility=Visibility.Collapsed;
        Ui.Field(form,"Referência anual das projeções",projectionReference);
        var referenceFields=new StackPanel { Visibility=Visibility.Collapsed };
        Ui.Field(referenceFields,"Dia da projeção anual",projectionDay);
        Ui.Field(referenceFields,"Mês da projeção anual",projectionMonth);
        form.Children.Add(referenceFields);
        projectionReference.SelectionChanged+=(_,_)=>referenceFields.Visibility=projectionReference.SelectedIndex==2?Visibility.Visible:Visibility.Collapsed;
        projectionReference.SelectedIndex=0;
        Ui.Section(form,"Nome e gravação do resultado");
        Ui.Field(form,"Prefixo para as colunas trazidas",prefix);Ui.Field(form,"Nome da tabela de ativo biológico",name);
        Ui.AddHelp(form,"Gera uma única tabela com os dados da base, a última medição e uma coluna de volume por ano, em ordem crescente. O ano da medição recebe o volume observado; os anos seguintes usam a referência escolhida. A projeção parte da data real da medição e usa Y₂ = Y₁ × F(I₂)/F(I₁) na referência anual escolhida. Todas as projeções usam a última medição real como referência fixa, sem acumular projeções. O IMA(n) = [Y₁ × F(n)/F(I₁)] / n é calculado desde a idade 1, inclusive antes da medição, até a referência do ano final. A data de cada idade é o aniversário do plantio; cada idade tem seu valor e data na mesma linha.");
        SetPrimaryAction(Ui.AsyncButton("Gerar ativo biológico",()=>owner.Execute(Run)));
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
        names=names.Where(n=>!n.StartsWith(Dendro.Core.Processing.ProcessingHistory.InternalPrefix,StringComparison.Ordinal)).ToArray();
        loading=true;sheet.ItemsSource=names;string previous=session.ExcelOptions(paths[selector.SelectedIndex]).S("sheet");sheet.SelectedItem=names.Contains(previous)?previous:names.FirstOrDefault();
        (processing?processedTables:baseTables).Visibility=Visibility.Visible;loading=false;
        if(names.Length>0)await LoadColumns(processing);
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
            volume.ItemsSource=names;volume.SelectedItem=names.FirstOrDefault(c=>c is "VTCC_m3_ha" or "Volume_m3_ha" or "VTCC_ha" or "VCCC_ha");
            columns.ItemsSource=names;foreach(string c in names.Where(c=>c is "VTCC_m3_ha" or "Volume_m3_ha" or "VTCC_ha" or "Fustes_ha" or "VMI_m3_fuste" or "VMI" or "Idade_anos"))columns.SelectedItems.Add(c);
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
            )throw new InvalidOperationException("Selecione a coluna de vínculo na base que receberá os resultados, a coluna correspondente no processamento e a data de medição do processamento. Resultados antigos sem Data_Medicao precisam ser reprocessados.");
        bool projectionRequested=curveId.SelectedItem is string||plantingDate.SelectedItem is string;
        string curveColumn=curveId.SelectedItem as string??"",plantedColumn=plantingDate.SelectedItem as string??"",volumeColumn=volume.SelectedItem as string??"";
        int finalYear=0;
        int referenceMonth=projectionReference.SelectedIndex==1?12:6;
        int referenceDay=projectionReference.SelectedIndex==1?31:30;
        if(projectionRequested&&projectionReference.SelectedIndex==2
            &&(!int.TryParse(projectionMonth.Text,out referenceMonth)||!int.TryParse(projectionDay.Text,out referenceDay)))
            throw new InvalidOperationException("Informe o dia e o mês da referência anual.");
        if(projectionRequested&&(curveColumn.Length==0||plantedColumn.Length==0||volumeColumn.Length==0))
            throw new InvalidOperationException("Para projetar, selecione na base a coluna com o ID da curva de Crescimento e produção e a data de plantio; no processamento, selecione o volume observado.");
        if(projectionRequested&&(!int.TryParse(throughYear.Text.Trim(),NumberStyles.Integer,CultureInfo.CurrentCulture,out finalYear)
            ||finalYear<1800||finalYear>9999))throw new InvalidOperationException("Informe um ano final entre 1800 e 9999.");
        if(useModelWhenMissing.IsChecked==true&&!projectionRequested)
            throw new InvalidOperationException("Para usar o modelo direto, selecione o ID da curva e a data de plantio na base.");
        string outputName=TableTools.SuggestedName(name.Text);if(string.IsNullOrWhiteSpace(outputName))throw new InvalidOperationException("Informe o nome do resultado.");
        string path=Path.Combine(session.ProjectWorkFolder(),outputName+".dlb");
        if(CompactBankFile.Exists(session.Resolve(Path.Combine(session.Meta.S("database_path"),outputName+".dlb"))))throw new InvalidOperationException("Já existe uma tabela com esse nome no arquivo do projeto. Escolha outro nome.");
        var curves=projectionRequested?ActiveCurves():[];
        if(projectionRequested&&curves.Length==0)throw new InvalidOperationException("Cadastre e marque como vigente ao menos uma curva de crescimento com ID correspondente aos talhões.");
        var selectedColumns=columns.SelectedItems.Cast<string>().Concat(volumeColumn.Length>0?[volumeColumn]:[]).Distinct(StringComparer.Ordinal).ToArray();
        var options=new BiologicalAssetOptions(b,p,d,selectedColumns,prefix.Text,inventory.SelectedIndex==1?"IFC":inventory.SelectedIndex==2?"IFPC":"",
            true,plantedColumn,curveColumn,volumeColumn,finalYear,curves,referenceMonth,referenceDay,
            fallbacks.ToArray(),useModelWhenMissing.IsChecked==true);
        var progress=owner.StartTableOperation("Buscando as medições mais recentes para o ativo biológico…");var cancellation=owner.OperationCancellation;
        try
        {
            var result=await Task.Run(()=>BiologicalAsset.Join(source,processing,options,path,cancellation,progress));
            string[] resultTables=await Task.Run(()=>TableStore.Tables(path));
            var previousFiles=session.Files.ToHashSet(StringComparer.OrdinalIgnoreCase);
            await session.AddTableResults([new(path,"Ativo biológico",result.BaseRows)],paths[basis.SelectedIndex],source.Table);
            string reference=session.Files.Single(file=>!previousFiles.Contains(file));
            var entry=session.Meta.Arr("derived_tables").OfType<JsonObject>().Last(item=>item.S("path")==reference);
            if(resultTables.Length>1)
                entry["sheets"]=new JsonArray(resultTables.Select(table=>(JsonNode?)JsonValue.Create(table)).ToArray());
            else entry.Remove("sheets");
            session.Touch();
            await owner.OpenTable(reference,"Ativo biológico");
            owner.Log($"Fontes alternativas: {result.FallbackLinked:N0} vinculados; modelo direto: {result.ModelOnly:N0} unidades calculadas.");
            owner.Log(projectionRequested?$"Ativo biológico: {result.Linked:N0} vinculados; {result.ProjectedRows:N0} projeções anuais; {result.ImaRows:N0} idades de IMA."
                :$"Ativo biológico: {result.Linked:N0} vinculados; {result.Missing:N0} sem medição válida; {result.Ambiguous:N0} conflitos na data mais recente.");
            if(projectionRequested)
            {
                owner.Log($"Diagnóstico da projeção: {result.Missing:N0} sem medição; {result.Ambiguous:N0} chaves ambíguas; {result.MissingPlantingDates:N0} datas de plantio/idades inválidas; {result.MissingCurves:N0} IDs sem curva vigente; {result.InvalidVolumes:N0} volumes ou avaliações de curva inválidos.");
                if(result.ProjectedRows==0)
                    throw new InvalidOperationException("O vínculo foi salvo, mas nenhuma projeção anual foi calculada. Confira o Histórico e a coluna AB_Projecao_Status. A referência do ano final precisa ser posterior à medição. Chaves que reúnem parcelas distintas precisam de um resumo por talhão ou de uma chave que identifique cada parcela.");
            }
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
