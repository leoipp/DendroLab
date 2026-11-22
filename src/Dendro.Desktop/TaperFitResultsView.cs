using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Dendro.Desktop;

internal sealed class TaperFitResultsView : DockPanel
{
    private sealed record FitRow(JsonObject Fit)
    {
        public string Id => Fit.S("id");
        public string Model => Fit.S("name");
        public string Group => Fit.S("group");
        public string N => Fit.N("n").ToString("N0");
        public string Trees => Fit.N("trees").ToString("N0");
        public string Status => Fit.S("status");
        public string R2 => Number("r2");
        public string Rmse => Number("rmse");
        public string Bias => Number("bias_pct");
        public string Coefficients => Fit["coefficients"]?.ToJsonString() ?? "";
        public bool CanSave => Status == "OK";
        private string Number(string key) => Fit[key] is JsonValue value && value.TryGetValue<double>(out var number)
            ? number.ToString("G6") : "";
    }

    private readonly MainWindow owner;
    private readonly ProjectSession session;
    private readonly FitRow[] rows;
    private readonly DataGrid grid = new();
    private readonly Button save;
    private readonly TextBlock status = Ui.Text("");

    internal TaperFitResultsView(MainWindow owner, ProjectSession session, JsonObject[] fits)
    {
        this.owner = owner;
        this.session = session;
        rows = fits.Select(fit => new FitRow(fit)).ToArray();
        LastChildFill = true;
        Background = System.Windows.Media.Brushes.White;

        var header = new StackPanel { Margin = new Thickness(8, 6, 8, 4) };
        header.Children.Add(Ui.Text("Resultados do ajuste de taper", 22));
        Ui.AddHelp(header, "Confira os ajustes por modelo e estrato. Selecione os que deseja incluir no catálogo Ajustes salvos de taper; somente ajustes com status OK podem ser salvos para aplicação.");
        DockPanel.SetDock(header, Dock.Top);
        Children.Add(header);

        grid.AutoGenerateColumns = false;
        grid.IsReadOnly = true;
        grid.CanUserAddRows = false;
        grid.SelectionMode = DataGridSelectionMode.Extended;
        grid.SelectionUnit = DataGridSelectionUnit.FullRow;
        grid.MinColumnWidth = 70;
        grid.RowHeight = 32;
        AddColumn("Modelo", nameof(FitRow.Model), 180);
        AddColumn("Grupo", nameof(FitRow.Group), 140);
        AddColumn("N", nameof(FitRow.N), 70);
        AddColumn("Árvores", nameof(FitRow.Trees), 80);
        AddColumn("Status", nameof(FitRow.Status), 135);
        AddColumn("R²", nameof(FitRow.R2), 95);
        AddColumn("RMSE (cm)", nameof(FitRow.Rmse), 105);
        AddColumn("Bias (%)", nameof(FitRow.Bias), 95);
        AddColumn("Coeficientes", nameof(FitRow.Coefficients), new DataGridLength(1, DataGridLengthUnitType.Star));
        grid.ItemsSource = rows;
        grid.SelectionChanged += (_, _) => UpdateActions();
        grid.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (e.OriginalSource is not DependencyObject source) return;
            var row = ItemsControl.ContainerFromElement(grid, source) as DataGridRow;
            if (row?.Item is FitRow fit)
            {
                grid.SelectedItem = fit;
                row.IsSelected = true;
            }
        };
        grid.LoadingRow += (_, e) =>
        {
            var row = e.Row;
            var saveMenuItem = new MenuItem { Header = "Salvar no projeto" };
            saveMenuItem.Click += (_, _) =>
            {
                if (row.Item is FitRow fit) SaveRows([fit]);
            };
            var rowMenu = new ContextMenu();
            rowMenu.Items.Add(saveMenuItem);
            rowMenu.Opened += (_, _) =>
            {
                var fit = row.Item as FitRow;
                saveMenuItem.IsEnabled = fit?.CanSave == true;
                saveMenuItem.Header = fit?.CanSave == true ? "Salvar no projeto" : "Somente ajustes OK podem ser salvos";
            };
            row.ContextMenu = rowMenu;
        };

        save = Ui.IconButton("Salvar ajustes selecionados", CommandIconKind.Save, () => _ = owner.Execute(SaveSelected));
        save.IsEnabled = false;
        var actions = new WrapPanel { Margin = new Thickness(8, 3, 8, 4) };
        actions.Children.Add(save);
        DockPanel.SetDock(actions, Dock.Top);
        Children.Add(actions);

        status.Margin = new Thickness(8, 4, 8, 8);
        DockPanel.SetDock(status, Dock.Bottom);
        Children.Add(status);
        Children.Add(grid);
        UpdateActions();
    }

    private void AddColumn(string title, string binding, double width) =>
        grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(binding), Width = width });

    private void AddColumn(string title, string binding, DataGridLength width) =>
        grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(binding), Width = width });

    private void UpdateActions() => save.IsEnabled = grid.SelectedItems.OfType<FitRow>().Any(row => row.CanSave);

    private Task SaveSelected()
    {
        var selected = grid.SelectedItems.OfType<FitRow>().Where(row => row.CanSave).ToArray();
        SaveRows(selected);
        return Task.CompletedTask;
    }

    private void SaveRows(IEnumerable<FitRow> selectedRows)
    {
        var selected = selectedRows.Where(row => row.CanSave).DistinctBy(row => row.Id).ToArray();
        if (selected.Length == 0) return;
        var catalog = session.Meta.Arr("taper_fits");
        var existing = catalog.OfType<JsonObject>().Select(item => item.S("id")).ToHashSet(StringComparer.Ordinal);
        var toSave = selected.Where(row => !existing.Contains(row.Id)).ToArray();
        foreach (var row in toSave) catalog.Add(row.Fit.DeepClone());
        if (toSave.Length > 0) session.Touch();
        string skipped = selected.Length == toSave.Length ? "" : $" · {selected.Length - toSave.Length} já estava(m) salvo(s)";
        status.Text = toSave.Length == 0 ? "Os ajustes selecionados já estão no catálogo." : $"{toSave.Length} ajuste(s) salvo(s) em Ajustes salvos de taper{skipped}.";
        owner.Log(status.Text);
    }
}
