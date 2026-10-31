using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal sealed class IntegratedCleaningDocument : DockPanel, IDisposable
{
    private readonly MainWindow owner;private readonly ProjectSession session;
    internal IntegratedCleaning Cleaning {get;}
    internal IReadOnlyList<ScatterPlot> Plots => plots;
    private readonly List<ScatterPlot> plots=[];
    private readonly string reference,tableName;
    private readonly TextBlock status=Ui.Text("");
    private readonly Button remove,undo;
    private readonly TextBox outputName;
    private int[] selected=[];private bool syncing;

    internal IntegratedCleaningDocument(MainWindow owner,ProjectSession session,IntegratedCleaning cleaning,string reference,string tableName)
    {
        this.owner=owner;this.session=session;Cleaning=cleaning;this.reference=reference;this.tableName=tableName;
        outputName=Ui.Input(TableTools.SuggestedName(Path.GetFileNameWithoutExtension(tableName)+"_limpa"));outputName.Width=170;
        var toolbar=new DockPanel {Margin=new Thickness(4,1,4,3),LastChildFill=true};
        remove=Ui.IconButton("Remover seleção",CommandIconKind.Remove,()=>Remove(selected));
        undo=Ui.IconButton("Desfazer remoção",CommandIconKind.Undo,Undo);
        var clear=Ui.IconButton("Limpar seleção",CommandIconKind.ClearSelection,()=>Select([]));
        var actions=new WrapPanel {VerticalAlignment=VerticalAlignment.Center};
        actions.Children.Add(remove);actions.Children.Add(undo);actions.Children.Add(clear);
        var show=new CheckBox{Content="Mostrar removidos",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(5,0,8,0)};
        show.Checked+=(_,_)=>ShowRemoved(true);show.Unchecked+=(_,_)=>ShowRemoved(false);actions.Children.Add(show);
        actions.Children.Add(Ui.AsyncIconButton("Exportar tabela limpa…",CommandIconKind.Export,()=>owner.Execute(Export)));
        outputName.ToolTip="Nome da nova tabela limpa dentro do projeto.";
        System.Windows.Automation.AutomationProperties.SetName(outputName,"Nome da tabela limpa");
        actions.Children.Add(outputName);
        actions.Children.Add(Ui.AsyncIconButton("Salvar tabela limpa no projeto",CommandIconKind.Save,()=>owner.Execute(SaveToProject)));
        var layout=Ui.Combo(["1 coluna","2 colunas","3 colunas"]);layout.Width=105;layout.SelectedIndex=1;
        layout.ToolTip="Quantidade de gráficos por linha.";
        System.Windows.Automation.AutomationProperties.SetName(layout,"Gráficos por linha");
        var help=Ui.Help("Todos os gráficos compartilham a mesma máscara de linhas. Selecione primeiro e remova depois, pelo botão ou Delete. Linhas sem valores válidos em um gráfico continuam na tabela e só saem se forem removidas em outro. A exportação mantém todas as colunas e a ordem das linhas restantes. A sessão é temporária: exporte ou salve antes de fechar.");
        var options=new WrapPanel {VerticalAlignment=VerticalAlignment.Center};
        options.Children.Add(layout);options.Children.Add(help);
        DockPanel.SetDock(options,Dock.Right);toolbar.Children.Add(options);toolbar.Children.Add(actions);
        SetDock(toolbar,Dock.Top);Children.Add(toolbar);status.Margin=new Thickness(10,4,10,7);SetDock(status,Dock.Bottom);Children.Add(status);
        var cards=new UniformGrid{Columns=2};layout.SelectionChanged+=(_,_)=>cards.Columns=layout.SelectedIndex+1;
        foreach(var data in cleaning.Charts)
        {
            var config=data["chart"]!.AsObject();var plot=new ScatterPlot{Data=data,Config=config,RemoveEnabled=true,ToolTip="Clique ou desenhe um contorno para selecionar. Delete remove a seleção de todos os gráficos; Ctrl acrescenta pontos."};
            plots.Add(plot);plot.SelectionChanged+=rows=>{if(!syncing)Select(rows);};plot.RemoveRequested+=Remove;
            var body=new DockPanel();var legend=PlotLegend.Create(config,data);SetDock(legend,Dock.Top);body.Children.Add(legend);body.Children.Add(plot);
            cards.Children.Add(new Border{Child=body,Height=350,Margin=new Thickness(4),BorderThickness=new Thickness(1),BorderBrush=new SolidColorBrush(Color.FromRgb(213,223,233)),CornerRadius=new CornerRadius(8),Background=Brushes.White});
        }
        Children.Add(new ScrollViewer{Content=cards,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});UpdateStatus();
    }

    private void Select(int[] rows)
    {
        syncing=true;
        try {selected=rows;foreach(var plot in plots)plot.SelectRows(rows);}finally{syncing=false;}
        UpdateStatus();
    }

    internal void Remove(int[] rows)
    {
        if(Cleaning.Remove(rows)==0)return;Select([]);RefreshPlots();
    }
    internal void Undo(){if(Cleaning.Undo()==0)return;Select([]);RefreshPlots();}
    private void ShowRemoved(bool show){foreach(var plot in plots){plot.Config["show_removed"]=show;plot.Config=plot.Config;}}
    private void RefreshPlots(){foreach(var plot in plots)plot.Data=plot.Data;UpdateStatus();}
    private void UpdateStatus()
    {
        status.Text=$"{Cleaning.RowCount:N0} originais · {Cleaning.RowCount-Cleaning.RemovedCount:N0} mantidas · {Cleaning.RemovedCount:N0} removidas · {selected.Length:N0} selecionadas";
        remove.IsEnabled=selected.Length>0;undo.IsEnabled=Cleaning.CanUndo;
    }

    private async Task CheckExport()
    {await session.RefreshHistory(true);session.RequireApprovedExport(reference);}
    private async Task Export()
    {
        await CheckExport();string? output=Ui.SaveFile("Excel|*.xlsx|CSV|*.csv|Banco DendroLab|*.dlb",TableTools.SuggestedName(tableName)+"_limpa.xlsx");
        if(output==null)return;await ExportTo(output);owner.Log("Tabela limpa exportada: "+output);
    }
    internal async Task ExportTo(string output)
    {
        await CheckExport();var cancellation=owner.OperationCancellation;var progress=owner.StartTableOperation("Exportando tabela limpa…");
        await Task.Run(()=>Cleaning.Export(output,cancellation,progress),cancellation);
    }

    internal async Task SaveToProject()
    {
        await CheckExport();string name=TableTools.SuggestedName(outputName.Text);
        if(string.IsNullOrWhiteSpace(name))throw new InvalidOperationException("Informe o nome da tabela limpa.");
        string temporary=Path.Combine(session.ProjectWorkFolder(),name+".dlb");
        string destination=Path.Combine(session.Meta.S("database_path"),name+".dlb");
        if(CompactBankFile.Exists(session.Resolve(destination)))throw new InvalidOperationException("Já existe uma tabela com esse nome. Escolha outro nome.");
        try
        {
            await ExportTo(temporary);
            await session.AddTableResults([new(temporary,"Dados",Cleaning.RowCount-Cleaning.RemovedCount)],reference,Cleaning.Original.Table);
            owner.Log($"Tabela limpa salva: {name} · {Cleaning.RowCount-Cleaning.RemovedCount:N0} linhas · {Cleaning.RemovedCount:N0} removidas.");
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }

    public void Dispose()=>Cleaning.Dispose();
}
