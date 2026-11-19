using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AvalonDock.Layout;
using Dendro.Core.Regression;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal static partial class DesktopTests
{
    private static async Task TestTaper(MainWindow shell,string root)
    {
        string input=Path.Combine(root,"Cubagem.dlb");
        using(var writer=new CompactBankWriter(input))
        {
            var id=writer.AddTable(["Arvore","DAP","H","h","d"]);
            for(int tree=0;tree<6;tree++)for(int section=0;section<10;section++)
            {
                double dap=15+tree*2,total=20+tree,h=total*section/11;
                writer.Add(id,[tree.ToString(),dap,total,h,TaperAdjustment.Predict("ormerod_diameter_model",[.7],dap,total,h)]);
            }
            writer.Complete();
        }
        shell.Session.AddFile(input);
        shell.TaperButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var document=shell.DockManager.Layout.Descendents().OfType<LayoutDocument>().Single(d=>d.ContentId=="taper-models");
        var view=(TaperModelsView)document.Content;
        ComboBox Source()=>LogicalControls<ComboBox>(view).Single(c=>System.Windows.Automation.AutomationProperties.GetAutomationId(c)=="Taper.Source");
        await Until(()=>Source().Items.Count>0);
        Source().SelectedItem=Source().Items.Cast<string>().First(s=>s.StartsWith("Cubagem.dlb"));
        await shell.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        await Until(()=>LogicalControls<ComboBox>(view).Any(c=>c.SelectedItem as string=="Arvore"));
        Capture(view,Path.Combine(root,"afilamento-ajuste.png"));
        LogicalControls<Button>(view).Single(b=>b.Content as string=="Ajustar modelos").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(()=>!shell.IsBusy&&shell.Session.Meta.Arr("taper_fits").Count>0);
        Check(shell.Session.Meta.Arr("taper_fits").Count==5,"menu Inventário abre ajuste de cinco modelos de afilamento");
        var fit=shell.Session.Meta.Arr("taper_fits").First(f=>f!.S("model")=="ormerod_diameter_model")!;
        Check(fit.S("status")=="OK"&&fit.N("rmse")<1e-4,"fluxo nativo ajusta perfil conhecido");
        Check(TableStore.Tables(shell.Session.Resolve(fit.S("path"))).Length==3,"três tabelas importadas no projeto");
        await shell.Session.Save(shell.Session.FilePath);
        var reopened=new ProjectSession();await reopened.Open(shell.Session.FilePath);
        Check(reopened.Meta.Arr("taper_fits").Count==5,"catálogo de afilamento persiste ao reabrir projeto");
    }
}
