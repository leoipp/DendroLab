using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Dendro.Core.Configuration;

namespace Dendro.Desktop;

internal sealed class RegressionCatalogView : ScrollViewer
{
    private readonly JsonObject project;
    private readonly TabControl groups = new();
    private readonly DataGrid[] grids = new DataGrid[3];
    private readonly TextBox id = Ui.Input(),
        name = Ui.Input(),
        formula = Ui.Input(),
        coefficients = Ui.Input("2");
    private readonly HelpIcon hint = Ui.Help("");
    private readonly TextBlock error = Ui.Text("");
    private JsonObject? editing;
    private bool loading,
        draft;
    private int draftPredictors = 1;
    private JsonArray Models => project.Obj("regressoes").Arr("modelos");
    private int Predictors => Math.Max(0, groups.SelectedIndex) + 1;

    public RegressionCatalogView(JsonObject project)
    {
        this.project = project;
        RegressionCatalog.Ensure(project);
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Foreground = System.Windows.Media.Brushes.DarkSlateGray;
        Background = System.Windows.Media.Brushes.White;
        var panel = Ui.Form();
        Content = panel;
        panel.Children.Add(Ui.Text("Modelos de regressão", 22));
        Ui.AddHelp(panel, "Catálogo deste projeto. 1P, 2P e 3P indicam a quantidade de variáveis explicativas; b0 a b3 são os coeficientes a ajustar.");
        for (int i = 0; i < 3; i++)
        {
            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                Height = 180,
                SelectionMode = DataGridSelectionMode.Single,
                EnableColumnVirtualization = false,
                MinColumnWidth = 80,
            };
            grid.Columns.Add(
                new DataGridTextColumn
                {
                    Header = "ID",
                    Binding = new Binding("Id"),
                    Width = 120,
                }
            );
            grid.Columns.Add(
                new DataGridTextColumn
                {
                    Header = "Nome",
                    Binding = new Binding("Name"),
                    Width = 170,
                }
            );
            grid.Columns.Add(
                new DataGridTextColumn
                {
                    Header = "Fórmula",
                    Binding = new Binding("Formula"),
                    Width = 500,
                }
            );
            grid.Columns.Add(
                new DataGridTextColumn
                {
                    Header = "Coeficientes",
                    Binding = new Binding("Coefficients"),
                    Width = 95,
                }
            );
            grid.SelectionChanged += (_, _) =>
            {
                if (!loading && grid.SelectedItem is CatalogRow row)
                    Load(row.Model);
            };
            grids[i] = grid;
            groups.Items.Add(new TabItem { Header = $"{i + 1}P", Content = grid });
        }
        groups.SelectedIndex = 0;
        panel.Children.Add(groups);
        var actions = new WrapPanel();
        actions.Children.Add(Ui.Button("Novo modelo", New));
        actions.Children.Add(Ui.Button("Salvar modelo", SaveCurrent));
        actions.Children.Add(
            Ui.Button(
                "Excluir modelo",
                () =>
                {
                    if (editing != null)
                    {
                        Models.Remove(editing);
                        Refresh();
                        New();
                    }
                }
            )
        );
        panel.Children.Add(actions);
        Ui.AddHelp(panel, hint);
        panel.Children.Add(error);
        var names = new Grid();
        names.ColumnDefinitions.Add(new ColumnDefinition());
        names.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) }
        );
        var idPanel = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        Ui.Field(idPanel, "ID do modelo", id);
        names.Children.Add(idPanel);
        var namePanel = new StackPanel();
        Ui.Field(namePanel, "Nome", name);
        Grid.SetColumn(namePanel, 1);
        names.Children.Add(namePanel);
        panel.Children.Add(names);
        Ui.Field(panel, "Fórmula", formula);
        Ui.Field(panel, "Número de coeficientes (1 a 4)", coefficients);
        foreach (var field in new[] { id, name, formula, coefficients })
            field.TextChanged += (_, _) =>
            {
                if (!loading)
                    draft = true;
            };
        groups.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Source, groups) && !loading)
            {
                try
                {
                    if (draft)
                        SaveCurrent();
                    New();
                }
                catch (Exception ex)
                {
                    loading = true;
                    groups.SelectedIndex = draftPredictors - 1;
                    loading = false;
                    error.Text = ex.Message;
                }
            }
        };
        Refresh();
        New();
    }

    internal sealed record CatalogRow(JsonObject Model)
    {
        public string Id => Model.S("id");
        public string Name => Model.S("nome");
        public string Formula => Model.S("formula");
        public int Coefficients => (int)Model.N("coeficientes");
    }

    private void Refresh()
    {
        loading = true;
        try
        {
            for (int i = 0; i < 3; i++)
                grids[i].ItemsSource = Models
                    .OfType<JsonObject>()
                    .Where(m => m.N("preditores") == i + 1)
                    .Select(m => new CatalogRow(m))
                    .ToArray();
        }
        finally
        {
            loading = false;
        }
    }

    private void New()
    {
        error.Text = "";
        loading = true;
        editing = null;
        draftPredictors = Predictors;
        id.Text = "modelo_" + Guid.NewGuid().ToString("N")[..8];
        name.Text = "";
        coefficients.Text = (Predictors + 1).ToString();
        formula.Text =
            "b0" + string.Concat(Enumerable.Range(1, Predictors).Select(i => $"+b{i}*x{i}"));
        hint.Text =
            Predictors == 1
                ? "1P: use x1 (ou x/t) e b0 a b3."
                : $"{Predictors}P: use {string.Join(", ", Enumerable.Range(1, Predictors).Select(i => "x" + i))} e b0 a b3.";
        foreach (var grid in grids)
            grid.SelectedItem = null;
        draft = false;
        loading = false;
    }

    private void Load(JsonObject model)
    {
        loading = true;
        editing = model;
        draftPredictors = (int)model.N("preditores");
        id.Text = model.S("id");
        name.Text = model.S("nome");
        formula.Text = model.S("formula");
        coefficients.Text = model.N("coeficientes").ToString();
        draft = false;
        loading = false;
    }

    private void SaveCurrent()
    {
        int predictors = draftPredictors;
        var model = new JsonObject
        {
            ["id"] = id.Text.Trim(),
            ["nome"] = name.Text.Trim(),
            ["preditores"] = predictors,
            ["formula"] = formula.Text.Trim(),
            ["coeficientes"] = Ui.Number(coefficients.Text),
        };
        var test = project.DeepClone().AsObject();
        var array = test.Obj("regressoes").Arr("modelos");
        int index = editing == null ? -1 : Models.IndexOf(editing);
        if (index >= 0)
            array[index] = model.DeepClone();
        else
            array.Add(model.DeepClone());
        RegressionCatalog.Validate(test);
        if (index >= 0)
            Models[index] = model;
        else
            Models.Add(model);
        Refresh();
        Load(model);
    }

    public JsonObject Read()
    {
        if (draft)
            SaveCurrent();
        RegressionCatalog.Validate(project);
        return project.Obj("regressoes").DeepClone().AsObject();
    }
}
