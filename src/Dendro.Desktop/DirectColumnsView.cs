using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Dendro.Core.Processing;

namespace Dendro.Desktop;

internal sealed class DirectColumnsView : DockPanel
{
    private readonly Dictionary<(string Level,string Column),CheckBox> choices=new();
    internal DirectColumnsView(JsonObject project)
    {
        var help=Ui.Help("Escolha as colunas de saída de cada nível. Identificação, rastreabilidade e campos essenciais para auditoria ficam obrigatórios. Desabilitar uma coluna não muda os cálculos internos, as variáveis da RNA ou o finetune. A seleção vale para novos processamentos; resultados anteriores são preservados. Ative os níveis desejados na ferramenta Processador.");
        help.Margin=new Thickness(12);SetDock(help,Dock.Top);Children.Add(help);
        var tabs=new TabControl();Children.Add(tabs);
        foreach(var (level,title) in new[]{("arvore","Árvore"),("parcela","Parcela"),("talhao","Talhão"),("lote","Lote"),("projeto","Projeto")})
        {
            var panel=Ui.Form();var grid=new System.Windows.Controls.Primitives.UniformGrid {Columns=3};
            var actions=new WrapPanel();
            void Set(bool value){foreach(var pair in choices.Where(p=>p.Key.Level==level))if(pair.Value.IsEnabled)pair.Value.IsChecked=value;}
            actions.Children.Add(Ui.Button("Habilitar opcionais",()=>Set(true)));actions.Children.Add(Ui.Button("Desabilitar opcionais",()=>Set(false)));panel.Children.Add(actions);
            foreach(string column in DirectOutputColumns.Headers(level))
            {
                bool required=DirectOutputColumns.Required(level,column);
                var box=new CheckBox {Content=column+(required?" · obrigatória":""),IsEnabled=!required,
                    IsChecked=DirectOutputColumns.Enabled(project["processador"],level,column),Margin=new Thickness(5,9,8,9),
                    ToolTip=required?"Obrigatória para identificar o resultado ou manter os indicadores necessários à auditoria.":"Incluir esta coluna no resultado deste nível."};
                System.Windows.Automation.AutomationProperties.SetAutomationId(box,"DirectColumn."+level+"."+column);
                choices[(level,column)]=box;grid.Children.Add(box);
            }
            panel.Children.Add(grid);tabs.Items.Add(new TabItem {Header=title,Content=Ui.Scroll(panel)});
        }
    }
    internal JsonObject Read()
    {
        var result=new JsonObject();foreach(var level in DirectInventorySummary.Levels)
        {
            var columns=new JsonObject();foreach(var pair in choices.Where(p=>p.Key.Level==level))columns[pair.Key.Column]=!pair.Value.IsEnabled||pair.Value.IsChecked==true;
            result[level]=columns;
        }
        return result;
    }
    internal static void Show(MainWindow owner,ProjectSession session)
    {
        var view=new DirectColumnsView(session.Data!.DeepClone().AsObject());var apply=Ui.Button("Aplicar colunas do resumo direto",()=>{});
        var window=Ui.Dialog("Colunas do resumo direto",Ui.WithToolbar(view,apply),1100,720);
        apply.Click+=(_,_)=>{session.Data!.Obj("processador")["colunas_direto"]=view.Read();session.Touch();owner.Log("Colunas do resumo direto atualizadas. Salve o projeto para reutilizar a seleção.");window.Close();};
        window.ShowDialog();
    }
}
