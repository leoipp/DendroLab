using System.Data;
using System.Windows.Controls;
using Dendro.Core.Regression;

namespace Dendro.Desktop;

internal sealed class TaperEquationCatalogView : DockPanel
{
    internal TaperEquationCatalogView()
    {
        Background = System.Windows.Media.Brushes.White;
        var heading = new StackPanel { Margin = new System.Windows.Thickness(12) };
        heading.Children.Add(Ui.Text("Modelos de taper", 22));
        Ui.AddHelp(heading, "Equações disponíveis para ajuste na ferramenta Ajuste de taper.");
        DockPanel.SetDock(heading, Dock.Top);
        Children.Add(heading);
        var table = new DataTable();
        table.Columns.Add("ID"); table.Columns.Add("Modelo"); table.Columns.Add("Equação"); table.Columns.Add("Parâmetros");
        foreach (var model in TaperAdjustment.Models)
            table.Rows.Add(model.Id, model.Name, model.Formula, string.Join(", ", model.Parameters));
        var grid = new DataGrid { AutoGenerateColumns = true, IsReadOnly = true, CanUserAddRows = false, ItemsSource = table.DefaultView };
        Children.Add(grid);
    }
}
