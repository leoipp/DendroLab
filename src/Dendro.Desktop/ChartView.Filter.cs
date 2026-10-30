using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Dendro.Core.Regression;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal sealed partial class ChartView
{
    private static readonly (string Name,FilterOperator Operator)[] PointFilters = [
        ("Igual a",FilterOperator.Equal),("Diferente de",FilterOperator.NotEqual),("Contém",FilterOperator.Contains),
        ("Começa com",FilterOperator.StartsWith),("Maior que",FilterOperator.Greater),("Maior ou igual a",FilterOperator.GreaterOrEqual),
        ("Menor que",FilterOperator.Less),("Menor ou igual a",FilterOperator.LessOrEqual),("Está vazio",FilterOperator.Empty),("Não está vazio",FilterOperator.NotEmpty)];

    internal async Task<RegressionFilterPreview> PreviewPointFilter(JsonObject saved, ScatterPlot plot, IReadOnlyList<TableFilter> filters)
    {
        var source=new TableSource(session.Resolve(saved.S("path")),saved.S("sheet"),(int)saved.N("header_row",1));
        int[] eligible=(plot.Data?["points"] as JsonArray??[]).Where(p=>!p.B("excluded")).Select(p=>(int)p.N("row")).ToArray();
        var progress=owner.StartTableOperation("Filtrando observações da regressão…");var cancellation=owner.OperationCancellation;
        return await Task.Run(()=>RegressionPointFilter.Select(source,filters,eligible,cancellation,progress),cancellation);
    }

    private readonly Dictionary<string,Action> dataCleanups=new();
    internal Task RegressionDataReady {get;private set;}=Task.CompletedTask;
    private ContentControl AddRegressionTable(JsonObject saved,ScatterPlot plot)
    {
        string identity=Identity(saved);
        if(dataCleanups.Remove(identity,out var old))old();
        var host=new ContentControl {Content=Ui.Text("Carregando dados mantidos da regressão…")};
        var cancel=new CancellationTokenSource();TableBrowser? browser=null;bool disposed=false;
        void Dispose(){if(disposed)return;disposed=true;cancel.Cancel();browser?.Dispose();}
        dataCleanups[identity]=Dispose;
        async Task Load()
        {
            string path=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"DendroLab-regression-data-"+Guid.NewGuid().ToString("N")+".dlb");
            try
            {
                var eligible=(plot.Data?["points"] as JsonArray??[]).Where(p=>!p.B("excluded")).Select(p=>(int)p.N("row")).ToHashSet();
                var sourceTable=new TableSource(session.Resolve(saved.S("path")),saved.S("sheet"),(int)saved.N("header_row",1));
                var ids=await Task.Run(()=>{
                    using var cursor=new TableCursor(sourceTable);
                    using var writer=new CompactBankWriter(path);
                    string rowName="Linha_original";while(cursor.Columns.Contains(rowName))rowName+="_";
                    string table=writer.AddTable(new[]{rowName}.Concat(cursor.Columns).ToArray(),"Dados da regressão");var mapping=new List<int>();int row=0;
                    foreach(var values in cursor.Rows()) {cancel.Token.ThrowIfCancellationRequested();row++;if(!eligible.Contains(row))continue;writer.Add(table,new object?[]{(double)(row+sourceTable.HeaderRow)}.Concat(values).ToArray());mapping.Add(row);}
                    writer.Complete();return mapping.ToArray();
                },cancel.Token);
                if(disposed)return;
                browser=new TableBrowser(owner,new(path),saved.S("path"),true,()=>{if(System.IO.File.Exists(path))System.IO.File.Delete(path);},true);
                await browser.Load();if(disposed)return;
                var positions=ids.Select((id,i)=>(id,line:(long)i+2)).ToDictionary(p=>p.id,p=>p.line);
                browser.RowsSelected+=rows=>plot.SelectRows(rows.Where(r=>r>=2&&r-2<ids.Length).Select(r=>ids[(int)r-2]));
                plot.SelectionChanged+=rows=>{if(!disposed)browser.SelectSourceRows(rows.Where(positions.ContainsKey).Select(r=>positions[r]));};
                var toolbar=new WrapPanel();
                toolbar.Children.Add(Ui.AsyncButton("Selecionar filtradas",()=>owner.Execute(async()=>{
                    var rows=await Task.Run(()=>browser.ViewSourceRows().ToArray());plot.SelectRows(rows.Select(r=>ids[(int)r-2]));
                })));
                toolbar.Children.Add(Ui.Button("Remover seleção",plot.ConfirmRemoval));toolbar.Children.Add(Ui.Button("Limpar",plot.ClearSelection));
                toolbar.Children.Add(Ui.Help("Dados mantidos no ajuste. A seleção da tabela acompanha o gráfico. Selecionar filtradas marca todas as linhas do filtro, inclusive outras páginas; Remover seleção confirma a retirada apenas do ajuste."));
                var panel=new DockPanel();DockPanel.SetDock(toolbar,Dock.Top);panel.Children.Add(toolbar);panel.Children.Add(browser);host.Content=panel;
            }
            catch(OperationCanceledException) { }
            catch(Exception ex){if(!disposed){host.Content=Ui.Text("Não foi possível carregar os dados: "+ex.Message);owner.Log(ex.Message);}}
            finally {if((disposed||browser==null)&&System.IO.File.Exists(path))System.IO.File.Delete(path);}
        }
        RegressionDataReady=Load();
        return host;
    }
}
