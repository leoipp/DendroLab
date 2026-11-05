using System.Data;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Dendro.Core.Regression;

namespace Dendro.Desktop;

internal sealed class SiteIndexModelsView : DockPanel
{
    private readonly MainWindow owner;
    private readonly ProjectSession session;
    private readonly DataGrid grid = new();
    private readonly Button editButton;
    private readonly Button removeButton;
    private readonly Button currentButton;
    private JsonArray Models => session.Meta.Arr("site_index_models");

    internal SiteIndexModelsView(MainWindow owner, ProjectSession session)
    {
        this.owner = owner;
        this.session = session;
        LastChildFill = true;
        var header = new StackPanel { Margin = new Thickness(8, 6, 8, 4) };
        header.Children.Add(Ui.Text("Índice de sítio", 22));
        Ui.AddHelp(header, "Catálogo no padrão de Crescimento e produção: ID, modelo, ano, versão vigente, coeficientes, fórmula e entradas. Para adicionar um ajuste, abra o resultado de Ajuste de regressão, clique com o botão direito em um modelo 1P e escolha Salvar como modelo de índice de sítio.");
        DockPanel.SetDock(header, Dock.Top);
        Children.Add(header);

        var actions = new WrapPanel { Margin = new Thickness(8, 1, 8, 4) };
        editButton = Ui.IconButton("Editar modelo selecionado", CommandIconKind.Rename, () => _ = owner.Execute(() => Edit(grid.SelectedIndex)));
        currentButton = Ui.IconButton("Tornar vigente para este ID", CommandIconKind.Activate, () => _ = owner.Execute(ActivateSelected));
        removeButton = Ui.IconButton("Remover modelo selecionado", CommandIconKind.Remove, () => _ = owner.Execute(RemoveSelected));
        actions.Children.Add(editButton);
        actions.Children.Add(currentButton);
        actions.Children.Add(removeButton);
        DockPanel.SetDock(actions, Dock.Top);
        Children.Add(actions);

        grid.AutoGenerateColumns = false;
        grid.IsReadOnly = true;
        grid.CanUserAddRows = false;
        grid.SelectionMode = DataGridSelectionMode.Single;
        grid.MinColumnWidth = 75;
        grid.Columns.Add(Column("ID", "ID", 100));
        grid.Columns.Add(Column("Modelo", "Modelo", 175));
        grid.Columns.Add(Column("Ano", "Ano", 65));
        grid.Columns.Add(Column("Vigente", "Vigente", 65));
        grid.Columns.Add(Column("b0", "b0", 80));
        grid.Columns.Add(Column("b1", "b1", 80));
        grid.Columns.Add(Column("b2", "b2", 80));
        grid.Columns.Add(Column("b3", "b3", 80));
        grid.Columns.Add(Column("Fórmula", "Fórmula", new DataGridLength(1, DataGridLengthUnitType.Star)));
        grid.Columns.Add(Column("Entradas esperadas", "Entradas esperadas", 180));
        grid.Columns.Add(Column("Espécie / material", "Espécie / material", 150));
        grid.Columns.Add(Column("Idade-base", "Idade-base", 85));
        grid.Columns.Add(Column("Unidade", "Unidade", 70));
        grid.SelectionChanged += (_, _) => UpdateActions();
        grid.MouseDoubleClick += async (_, _) => { if (grid.SelectedIndex >= 0) await Edit(grid.SelectedIndex); };
        Children.Add(grid);
        Refresh();
    }

    private static DataGridTextColumn Column(string header, string binding, double width) =>
        new() { Header = header, Binding = new System.Windows.Data.Binding(binding), Width = width };

    private static DataGridTextColumn Column(string header, string binding, DataGridLength width) =>
        new() { Header = header, Binding = new System.Windows.Data.Binding(binding), Width = width };

    private void Refresh()
    {
        var table = new DataTable();
        foreach (string column in new[] { "ID", "Modelo", "Ano", "Vigente", "b0", "b1", "b2", "b3", "Fórmula", "Entradas esperadas", "Espécie / material", "Idade-base", "Unidade" })
            table.Columns.Add(column);
        foreach (var row in Models.OfType<JsonObject>())
        {
            double[] coefficients = (row["coefficients"] as JsonArray ?? []).Select(value => JsonExtensions.Number(value)).ToArray();
            string inputs = string.Join("; ", (row["entradas"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(input => $"{input.S("simbolo")} = {input.S("origem")}.{input.S("coluna")} [{input.S("unidade")}] × {input.N("fator", 1):G5}"));
            table.Rows.Add(row.S("codigo"), row.S("modelo"), row.N("ano"), row.B("vigente", true) ? "Sim" : "Não",
                Coefficient(coefficients, 0), Coefficient(coefficients, 1), Coefficient(coefficients, 2), Coefficient(coefficients, 3),
                row.S("formula"), inputs, row.S("species"), row.N("reference_age"), row.S("age_unit"));
        }
        grid.ItemsSource = table.DefaultView;
        UpdateActions();
    }

    private static string Coefficient(double[] values, int index) => index < values.Length ? values[index].ToString("G8") : "";

    private void UpdateActions()
    {
        bool selected = grid.SelectedIndex >= 0 && grid.SelectedIndex < Models.Count;
        editButton.IsEnabled = selected;
        removeButton.IsEnabled = selected;
        currentButton.IsEnabled = selected && !Models[grid.SelectedIndex]!.B("vigente", true);
    }

    private Task Edit(int index)
    {
        if (index < 0 || index >= Models.Count || Models[index] is not JsonObject model) return Task.CompletedTask;
        var code = Ui.Input(model.S("codigo"));
        var modelName = Ui.Input(model.S("modelo"));
        var name = Ui.Input(model.S("name"));
        var year = Ui.Input(model.N("ano").ToString());
        var species = Ui.Input(model.S("species"));
        var referenceAge = Ui.Input(model.N("reference_age").ToString(System.Globalization.CultureInfo.InvariantCulture));
        var unit = Ui.Combo(["anos", "meses"], model.S("age_unit"));
        var formula = Ui.Input(model.S("formula"));
        var current = new CheckBox { Content = "Versão vigente para este ID", IsChecked = model.B("vigente", true), Margin = new Thickness(2, 7, 2, 5) };
        var coefficientValues = (model["coefficients"] as JsonArray ?? []).ToArray();
        var coefficients = coefficientValues.Select(value => Ui.Input(JsonExtensions.Number(value).ToString("G17", System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        string[] sourcePredictors = (model["source_predictors"] as JsonArray ?? []).Select(value => value?.GetValue<string>() ?? "").ToArray();
        var ageColumn = Ui.Input(sourcePredictors.FirstOrDefault() ?? "");

        var form = Ui.Form();
        Ui.Section(form, "Identificação da equação");
        Ui.Field(form, "ID da equação", code);
        Ui.Field(form, "Nome do modelo", name);
        Ui.Field(form, "Modelo", modelName);
        Ui.Field(form, "Ano da versão", year);
        form.Children.Add(current);
        Ui.Section(form, "Índice de sítio");
        Ui.Field(form, "Espécie / material genético", species);
        Ui.Field(form, "Idade-base", referenceAge);
        Ui.Field(form, "Unidade de idade", unit);
        Ui.Section(form, "Equação e entradas");
        Ui.Field(form, "Fórmula", formula);
        for (int i = 0; i < coefficients.Length; i++) Ui.Field(form, "b" + i, coefficients[i]);
        Ui.Field(form, "Coluna de idade (t)", ageColumn);
        var save = Ui.Button("Salvar modelo", () => { });
        var cancel = Ui.Button("Cancelar", () => { });
        var window = Ui.Dialog("Editar modelo de índice de sítio", Ui.WithToolbar(Ui.Scroll(form), cancel, save), 720, 840);
        cancel.Click += (_, _) => window.Close();
        save.Click += (_, _) =>
        {
            save.IsEnabled = false;
            try
            {
                string id = code.Text.Trim(), displayName = name.Text.Trim(), family = modelName.Text.Trim();
                if (id.Length is < 1 or > 31 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
                    throw new InvalidOperationException("Use um ID de até 31 caracteres com letras, números, hífen ou sublinhado.");
                if (displayName.Length is < 1 or > 120 || family.Length is < 1 or > 120)
                    throw new InvalidOperationException("Informe o nome e o modelo.");
                if (!int.TryParse(year.Text, out int versionYear) || versionYear is < 1800 or > 9999)
                    throw new InvalidOperationException("Informe o ano da versão entre 1800 e 9999.");
                if (Models.OfType<JsonObject>().Where((_, i) => i != index).Any(row => row.S("codigo").Equals(id, StringComparison.OrdinalIgnoreCase) && row.N("ano") == versionYear))
                    throw new InvalidOperationException("Já existe uma versão desse ID para o ano informado.");
                double baseAge = Ui.Number(referenceAge.Text);
                if (!double.IsFinite(baseAge) || baseAge <= 0) throw new InvalidOperationException("A idade-base deve ser positiva.");
                string ageUnit = unit.SelectedItem?.ToString() ?? "";
                if (ageUnit is not ("anos" or "meses")) throw new InvalidOperationException("Selecione anos ou meses.");
                if (string.IsNullOrWhiteSpace(ageColumn.Text)) throw new InvalidOperationException("Informe a coluna de idade (t).");
                var values = coefficients.Select(field => Ui.Number(field.Text)).ToArray();
                var predictorColumns = new JsonArray(JsonValue.Create(ageColumn.Text.Trim()));
                var updated = new SavedRegressionModel(model.S("id"), displayName, 1, formula.Text.Trim(), values,
                    predictorColumns.Select(value => value!.GetValue<string>()).ToArray());
                RegressionApplication.Validate(updated);

                model["codigo"] = id; model["name"] = displayName; model["modelo"] = family;
                model["ano"] = versionYear; model["vigente"] = current.IsChecked == true;
                model["species"] = species.Text.Trim(); model["reference_age"] = baseAge; model["age_unit"] = ageUnit;
                model["formula"] = updated.Formula;
                model["coefficients"] = new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
                for (int i = 0; i < values.Length; i++) model["b" + i] = values[i];
                model["source_predictors"] = predictorColumns.DeepClone();
                model["entradas"] = new JsonArray(new JsonObject
                {
                    ["simbolo"] = "t", ["origem"] = "entrada", ["coluna"] = ageColumn.Text.Trim(),
                    ["unidade"] = ageUnit, ["fator"] = 1, ["agregacao"] = "media",
                });
                if (current.IsChecked == true)
                    foreach (var other in Models.OfType<JsonObject>().Where((other, i) => i != index && other.S("codigo") == id)) other["vigente"] = false;
                RegressionApplication.ValidateCatalog(Models);
                session.Touch();
                Refresh();
                window.Close();
            }
            catch (Exception ex) { Ui.Error(ex); }
            finally { save.IsEnabled = true; }
        };
        window.ShowDialog();
        return Task.CompletedTask;
    }

    private Task ActivateSelected()
    {
        if (grid.SelectedIndex < 0 || grid.SelectedIndex >= Models.Count) return Task.CompletedTask;
        string id = Models[grid.SelectedIndex]!.S("codigo");
        for (int i = 0; i < Models.Count; i++)
            if (Models[i] is JsonObject item && item.S("codigo") == id) item["vigente"] = i == grid.SelectedIndex;
        session.Touch();
        Refresh();
        return Task.CompletedTask;
    }

    private Task RemoveSelected()
    {
        if (grid.SelectedIndex < 0 || grid.SelectedIndex >= Models.Count) return Task.CompletedTask;
        var model = Models[grid.SelectedIndex]!;
        if (!Ui.Confirm("Remover modelo de índice de sítio", $"Remover {model.S("name")} do catálogo?", "Remover", true)) return Task.CompletedTask;
        Models.RemoveAt(grid.SelectedIndex);
        session.Touch();
        Refresh();
        owner.Log("Modelo removido do catálogo de índice de sítio.");
        return Task.CompletedTask;
    }
}
