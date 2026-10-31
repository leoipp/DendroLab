using System.Data;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace Dendro.Desktop;

internal sealed class ModelsView : DockPanel
{
    private readonly MainWindow owner;
    private readonly ProjectSession session;
    private readonly bool growth;
    private readonly DataGrid grid = new();
    private readonly Button editButton;
    private readonly Button removeButton;
    private readonly Button? activateButton;
    private JsonArray Rows =>
        growth
            ? session.Data!.Obj("curvas_crescimento").Arr("equacoes")
            : session.Data!.Obj("volume").Arr("equacoes");

    public ModelsView(MainWindow owner, ProjectSession session, bool growth)
    {
        this.owner = owner;
        this.session = session;
        this.growth = growth;
        var bar = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        bar.Children.Add(Ui.IconButton("Novo modelo", CommandIconKind.New, () =>
        {
            _ = owner.Execute(() => Edit(-1));
        }));
        editButton = Ui.IconButton("Editar modelo selecionado", CommandIconKind.Rename, () =>
        {
            _ = owner.Execute(() => Edit(grid.SelectedIndex));
        });
        removeButton = Ui.IconButton("Excluir modelo selecionado", CommandIconKind.Remove, () =>
        {
            _ = owner.Execute(async () =>
            {
                if (grid.SelectedIndex < 0) return;
                await session.Mutate(new()
                {
                    ["op"] = growth ? "curve_remove" : "model_remove",
                    ["index"] = grid.SelectedIndex,
                });
                Refresh();
            });
        });
        bar.Children.Add(editButton);
        bar.Children.Add(removeButton);
        if (growth)
        {
            activateButton = Ui.IconButton("Tornar vigente para este ID", CommandIconKind.Activate, () =>
            {
                _ = owner.Execute(async () =>
                {
                    if (grid.SelectedIndex < 0) return;
                    await session.Mutate(new() { ["op"] = "curve_activate", ["index"] = grid.SelectedIndex });
                    Refresh();
                });
            });
            bar.Children.Add(activateButton);
        }
        var toolbar = new DockPanel { Margin = new Thickness(4, 1, 4, 3), LastChildFill = true };
        toolbar.Children.Add(bar);
        DockPanel.SetDock(toolbar, Dock.Top);
        Children.Add(toolbar);
        Children.Add(grid);
        grid.SelectionChanged += (_, _) => UpdateActionStates();
        grid.MouseDoubleClick += async (_, _) =>
        {
            if (grid.SelectedIndex >= 0) await Edit(grid.SelectedIndex);
        };
        Refresh();
    }

    private void UpdateActionStates()
    {
        bool hasSelection = grid.SelectedIndex >= 0 && grid.SelectedIndex < Rows.Count;
        editButton.IsEnabled = hasSelection;
        removeButton.IsEnabled = hasSelection;
        if (activateButton != null)
            activateButton.IsEnabled = hasSelection && !Rows[grid.SelectedIndex]!.B("vigente", true);
    }

    private void Refresh()
    {
        var t = new DataTable();
        foreach (
            var name in new[]
            {
                "ID",
                "Modelo",
                "Ano",
                "Vigente",
                "b0",
                "b1",
                "b2",
                "Fórmula",
                "Entradas esperadas",
            }
        )
            t.Columns.Add(name);
        if (!growth) t.Columns.Add("Tipo de volume");
        foreach (var r in Rows.OfType<JsonObject>())
        {
            var inputs = r["entradas"] as JsonArray ?? DefaultInputs(growth);
            var added = t.Rows.Add(
                r.S("codigo"),
                r.S("modelo_nome", r.S("modelo", growth ? "" : "Schumacher–Hall")),
                growth ? r.N("ano").ToString() : "",
                growth ? (r.B("vigente", true) ? "Sim" : "Não") : "",
                r.N("b0"),
                r.N("b1"),
                r.N("b2"),
                r.S("formula", growth ? "" : "exp(b0 + b1*log(x1) + b2*log(x2))"),
                string.Join(
                    "; ",
                    inputs.Select(n =>
                        $"{n!.S("simbolo")} = {n.S("origem")}.{n.S("coluna")} [{n.S("unidade")}] × {n.N("fator", 1)}"
                    )
                )
            );
            if (!growth) added["Tipo de volume"] = r.S("tipo_volume", "VTCC");
        }
        if (!growth) t.Columns["Tipo de volume"]!.SetOrdinal(1);
        grid.ItemsSource = t.DefaultView;
        UpdateActionStates();
    }

    internal static JsonArray DefaultInputs(bool growth) =>
        growth
            ? new JsonArray(
                new JsonObject
                {
                    ["simbolo"] = "t",
                    ["origem"] = "calculado",
                    ["coluna"] = "idade_meses",
                    ["unidade"] = "meses",
                    ["fator"] = 1,
                    ["agregacao"] = "media",
                }
            )
            : new JsonArray(
                new JsonObject
                {
                    ["simbolo"] = "x1",
                    ["origem"] = "calculado",
                    ["coluna"] = "dap_cm",
                    ["unidade"] = "cm",
                    ["fator"] = 1,
                },
                new JsonObject
                {
                    ["simbolo"] = "x2",
                    ["origem"] = "calculado",
                    ["coluna"] = "altura_m",
                    ["unidade"] = "m",
                    ["fator"] = 1,
                }
            );

    internal async Task Edit(int index)
    {
        if (index >= Rows.Count)
            return;
        var row = index >= 0 ? Rows[index]!.AsObject() : new JsonObject();
        var metadata = (
            await WorkspaceOperations.Run(
                new() { ["op"] = "metadata", ["project"] = session.Data!.DeepClone() }
            )
        )["result"]!.AsObject();
        var form = Ui.Form();
        var id = Ui.Input(row.S("codigo"));
        Ui.Field(form, "ID da equação", id);
        var volumeType = Ui.Combo(["VTCC", "VCCC"], row.S("tipo_volume", "VTCC"));
        if (!growth) Ui.Field(form, "Tipo de volume", volumeType);
        var name = Ui.Input(row.S("modelo_nome", growth ? "Gompertz" : "Schumacher–Hall"));
        Ui.Field(form, "Nome do modelo", name);
        var b0 = Ui.Input(row.N("b0", growth ? 300 : 0).ToString());
        var b1 = Ui.Input(row.N("b1", 1).ToString());
        var b2 = Ui.Input(row.N("b2", growth ? .03 : 1).ToString());
        Ui.Field(form, "b0", b0);
        Ui.Field(form, "b1", b1);
        Ui.Field(form, "b2", b2);
        ComboBox? model = null;
        var year = Ui.Input(row.N("ano", DateTime.Now.Year).ToString());
        var current = new CheckBox
        {
            Content = "Versão vigente para este ID",
            IsChecked = row.B("vigente", true),
        };
        var definitions = metadata["growth"]!.AsArray();
        if (growth)
        {
            model = Ui.Combo(
                definitions.Select(n => n!.S("name")).Append("Personalizado"),
                definitions.FirstOrDefault(n => n!.S("id") == row.S("modelo"))?.S("name")
            );
            Ui.Field(form, "Modelo de crescimento e produção", model);
            Ui.Field(form, "Ano da versão", year);
            form.Children.Add(current);
        }
        string defaultFormula = growth
            ? definitions
                .FirstOrDefault(n => n!.S("id") == row.S("modelo", "gompertz"))
                ?.S("formula")
                ?? "b0 * exp(-exp(b1 - b2*t))"
            : "exp(b0 + b1*log(x1) + b2*log(x2))";
        var formula = Ui.Input(row.S("formula", defaultFormula));
        Ui.Field(form, "Fórmula", formula);
        var inputs = row["entradas"]?.DeepClone().AsArray() ?? DefaultInputs(growth);
        var bindings = new StructuredEditor(
            new JsonObject { ["entradas"] = inputs },
            growthInputs: growth
        );
        form.Children.Add(
            Ui.Text(growth ? "Entradas e agregação por parcela" : "Entradas por árvore")
        );
        form.Children.Add(bindings);
        var customId = Ui.Input(row.S("modelo", "personalizado"));
        if (growth)
            Ui.Field(form, "Identificador do modelo personalizado", customId);
        if (model != null)
            model.SelectionChanged += (_, _) =>
            {
                if (model.SelectedIndex < definitions.Count && model.SelectedIndex >= 0)
                {
                    var definition = definitions[model.SelectedIndex]!;
                    formula.Text = definition.S("formula");
                    name.Text = definition.S("name");
                }
            };
        var apply = Ui.Button(index < 0 ? "Cadastrar modelo" : "Salvar modelo", () => { });
        form.Children.Add(apply);
        var window = Ui.Dialog(
            growth ? "Modelo de crescimento e produção" : "Modelo volumétrico",
            Ui.Scroll(form),
            760,
            850
        );
        apply.Click += async (_, _) =>
        {
            apply.IsEnabled = false;
            try
            {
                var request = new JsonObject
                {
                    ["op"] = growth ? "curve_set" : "model_set",
                    ["index"] = index,
                    ["id"] = id.Text.Trim(),
                    ["name"] = name.Text.Trim(),
                    ["b0"] = Ui.Number(b0.Text),
                    ["b1"] = Ui.Number(b1.Text),
                    ["b2"] = Ui.Number(b2.Text),
                    ["formula"] = formula.Text,
                    ["inputs"] = bindings.Read()["entradas"]!.DeepClone(),
                };
                if (!growth) request["volume_type"] = volumeType.SelectedItem?.ToString() ?? "VTCC";
                if (growth)
                {
                    request["year"] = (int)Ui.Number(year.Text);
                    request["current"] = current.IsChecked == true;
                    bool custom = model!.SelectedIndex >= definitions.Count;
                    request["model"] = custom
                        ? customId.Text.Trim()
                        : definitions[model.SelectedIndex]!.S("id");
                    if (custom)
                        request["definition"] = new JsonObject
                        {
                            ["id"] = customId.Text.Trim(),
                            ["name"] = name.Text,
                            ["formula"] = formula.Text,
                            ["unit"] = "meses",
                            ["inputs"] = request["inputs"]!.DeepClone(),
                        };
                }
                await session.Mutate(request);
                Refresh();
                window.Close();
            }
            catch (Exception ex)
            {
                Ui.Error(ex);
            }
            finally
            {
                apply.IsEnabled = true;
            }
        };
        window.ShowDialog();
    }
}
