using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Dendro.Core.Regression;

namespace Dendro.Desktop;

internal sealed class SavedRegressionCatalogView : DockPanel, IDisposable
{
    private const int PageSize = 500;
    private sealed record Row(SavedRegressionModel Model)
    {
        public string Id => Model.Id;
        public string Name => Model.Name;
        public string Predictors => Model.Predictors + "P";
        public string Formula => Model.Formula;
        public string Coefficients => string.Join("; ", Model.Coefficients.Select((value, index) => $"b{index}={value:G8}"));
        public string SourcePredictors => string.Join(", ", Model.SourcePredictors.Select((value, index) => $"X{index + 1}={value}"));
    }

    private readonly MainWindow owner;
    private readonly ProjectSession session;
    private readonly DataGrid grid = new();
    private readonly Button remove;
    private readonly Button previous;
    private readonly Button next;
    private readonly TextBlock pageCaption = Ui.Text("");
    private readonly TextBlock status = Ui.Text("");
    private Row[] rows = [];
    private int page;
    private bool disposed;

    internal SavedRegressionCatalogView(MainWindow owner, ProjectSession session)
    {
        this.owner = owner;
        this.session = session;
        session.Changed += SessionChanged;
        LastChildFill = true;
        Background = System.Windows.Media.Brushes.White;

        var header = new StackPanel { Margin = new Thickness(8, 6, 8, 4) };
        header.Children.Add(Ui.Text("Regressões salvas", 22));
        Ui.AddHelp(header, "Ajustes salvos a partir dos resultados de regressão. Este é o mesmo catálogo usado por Aplicar regressões; remova aqui os modelos que não devem mais aparecer lá.");
        DockPanel.SetDock(header, Dock.Top);
        Children.Add(header);

        grid.AutoGenerateColumns = false;
        grid.IsReadOnly = true;
        grid.CanUserAddRows = false;
        grid.SelectionMode = DataGridSelectionMode.Extended;
        grid.SelectionUnit = DataGridSelectionUnit.FullRow;
        grid.MinColumnWidth = 75;
        grid.EnableColumnVirtualization = false;
        grid.RowHeight = 32;
        grid.Columns.Add(Column("ID", nameof(Row.Id), 135));
        grid.Columns.Add(Column("Regressão", nameof(Row.Name), 190));
        grid.Columns.Add(Column("Variáveis", nameof(Row.Predictors), 80));
        grid.Columns.Add(Column("Coeficientes", nameof(Row.Coefficients), 200));
        grid.Columns.Add(Column("Fórmula", nameof(Row.Formula), new DataGridLength(1, DataGridLengthUnitType.Star)));
        grid.Columns.Add(Column("Preditores originais", nameof(Row.SourcePredictors), 220));
        grid.SelectionChanged += (_, _) => UpdateActions();
        var actions = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(Ui.IconButton("Atualizar catálogo", CommandIconKind.Restore, Refresh));
        remove = Ui.IconButton("Remover regressões selecionadas", CommandIconKind.Remove, () => _ = owner.Execute(RemoveSelected));
        actions.Children.Add(remove);

        previous = PageButton("Página anterior", CommandIconKind.ArrowLeft, () => ShowPage(page - 1));
        next = PageButton("Próxima página", CommandIconKind.ArrowRight, () => ShowPage(page + 1));
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        navigation.Children.Add(previous);
        pageCaption.Margin = new Thickness(6, 3, 6, 3);
        pageCaption.VerticalAlignment = VerticalAlignment.Center;
        navigation.Children.Add(pageCaption);
        navigation.Children.Add(next);

        var toolbar = new DockPanel { Margin = new Thickness(4, 1, 4, 3), LastChildFill = true };
        DockPanel.SetDock(navigation, Dock.Right);
        toolbar.Children.Add(navigation);
        toolbar.Children.Add(actions);
        DockPanel.SetDock(toolbar, Dock.Top);
        Children.Add(toolbar);

        status.Margin = new Thickness(8, 4, 8, 8);
        DockPanel.SetDock(status, Dock.Bottom);
        Children.Add(status);
        Children.Add(grid);

        Refresh();
    }

    private static DataGridTextColumn Column(string header, string binding, double width) =>
        new() { Header = header, Binding = new Binding(binding), Width = width };

    private static DataGridTextColumn Column(string header, string binding, DataGridLength width) =>
        new() { Header = header, Binding = new Binding(binding), Width = width };

    private static Button PageButton(string tooltip, CommandIconKind icon, Action action)
    {
        var button = Ui.IconButton(tooltip, icon, action);
        button.Width = button.MinWidth = 32;
        button.Height = button.MinHeight = 30;
        button.Padding = new Thickness(5);
        button.Margin = new Thickness(1);
        return button;
    }

    private void Refresh()
    {
        var catalog = session.Meta.Arr(RegressionApplication.StorageKey);
        RegressionApplication.ValidateCatalog(catalog);
        rows = catalog.OfType<JsonObject>()
            .Reverse()
            .Select(item => new Row(RegressionApplication.Read(item)))
            .ToArray();
        page = Math.Clamp(page, 0, Math.Max(0, (rows.Length - 1) / PageSize));
        status.Text = rows.Length == 0
            ? "Nenhuma regressão salva. Salve um ajuste pelo menu de contexto do resultado de Ajuste de regressão."
            : $"{rows.Length:N0} regressão(ões) no catálogo do projeto.";
        ShowPage(page);
    }

    private void SessionChanged()
    {
        if (disposed) return;
        if (Dispatcher.CheckAccess()) Refresh();
        else Dispatcher.BeginInvoke(Refresh);
    }

    private void ShowPage(int index)
    {
        int pageCount = Math.Max(1, (rows.Length + PageSize - 1) / PageSize);
        page = Math.Clamp(index, 0, pageCount - 1);
        grid.ItemsSource = rows.Skip(page * PageSize).Take(PageSize).ToArray();
        int first = rows.Length == 0 ? 0 : page * PageSize + 1;
        int last = Math.Min((page + 1) * PageSize, rows.Length);
        pageCaption.Text = $"{first:N0}–{last:N0} de {rows.Length:N0}";
        previous.IsEnabled = page > 0;
        next.IsEnabled = (page + 1) * PageSize < rows.Length;
        UpdateActions();
    }

    private async Task RemoveSelected()
    {
        var selected = grid.SelectedItems.OfType<Row>().ToArray();
        if (selected.Length == 0) return;
        string names = string.Join(", ", selected.Take(3).Select(row => row.Name));
        string suffix = selected.Length > 3 ? $" e mais {selected.Length - 3:N0}" : "";
        if (!Ui.Confirm("Remover regressões salvas",
            $"Remover {selected.Length:N0} regressão(ões) do catálogo e da ferramenta Aplicar regressões?\n\n{names}{suffix}",
            "Remover", destructive: true)) return;

        var ids = selected.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        var catalog = session.Meta.Arr(RegressionApplication.StorageKey);
        int removed = 0;
        for (int index = catalog.Count - 1; index >= 0; index--)
        {
            if (catalog[index] is JsonObject model && ids.Contains(model.S("id")))
            {
                catalog.RemoveAt(index);
                removed++;
            }
        }
        if (removed > 0) session.Touch();
        Refresh();
        status.Text = removed == 0
            ? "Nenhuma regressão selecionada foi encontrada no catálogo atual. Atualize a lista."
            : $"{removed:N0} regressão(ões) removida(s) do catálogo em memória. Salve o projeto para persistir a alteração.";
        await Task.CompletedTask;
    }

    private void UpdateActions() => remove.IsEnabled = grid.SelectedItems.Count > 0;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        session.Changed -= SessionChanged;
    }
}
