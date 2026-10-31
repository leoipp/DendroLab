using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace Dendro.Desktop;

internal static class GrowthFitRegistration
{
    public static bool CanRegister(JsonObject fit) =>
        fit.B("converged") && fit.N("predictor_count", 1) is >= 1 and <= 3 && fit.N("parameters") is >= 1 and <= 3;

    public static Task Open(MainWindow owner, ProjectSession session, JsonObject chart, JsonObject fit)
    {
        if (!CanRegister(fit))
            throw new InvalidOperationException("Cadastre ajustes convergidos 1P, 2P ou 3P com até três coeficientes.");

        string guessedUnit = chart.S("x").Contains("mes", StringComparison.OrdinalIgnoreCase) ? "meses" : "anos";
        var id = Ui.Input("REG" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant());
        string defaultName = fit.S("model", "Modelo")
            + (fit.S("group") is "" or "Global" ? "" : " · " + fit.S("group"));
        var name = Ui.Input(defaultName);
        var year = Ui.Input(DateTime.Now.Year.ToString());
        var ageUnit = Ui.Combo(["anos", "meses"], guessedUnit);
        int predictorCount = (int)fit.N("predictor_count", 1);
        var additionalInputs = new JsonArray();
        var xVariables = chart["x_variables"] as JsonArray ?? new JsonArray(chart.S("x"));
        for (int index = 1; index < predictorCount; index++)
        {
            string symbol = "x" + (index + 1);
            string column = xVariables.ElementAtOrDefault(index)?.GetValue<string>() ?? "";
            additionalInputs.Add(new JsonObject
            {
                ["simbolo"] = symbol,
                ["origem"] = "entrada",
                ["coluna"] = column,
                ["unidade"] = InferUnit(column),
                ["fator"] = 1,
                ["agregacao"] = "media",
            });
        }
        var inputEditor = predictorCount > 1
            ? new StructuredEditor(additionalInputs, "entradas", growthInputs: true)
            : null;
        var confirmAge = new CheckBox
        {
            Content = "Confirmo que X representa idade e Y representa produção acumulada.",
            IsChecked = false,
            Margin = new Thickness(2, 7, 2, 5),
        };
        var form = Ui.Form();
        Ui.Section(form, "Identificação da curva");
        Ui.Field(form, "ID da equação", id);
        Ui.Field(form, "Nome do modelo", name);
        Ui.Field(form, "Ano da versão", year);
        Ui.Field(form, "Unidade de X", ageUnit);
        Ui.AddHelp(form, "O ajuste será salvo como um modelo personalizado em Crescimento e produção. t será ligada à idade calculada. Para 2P/3P, revise o mapeamento de X2/X3: ele precisa apontar para os mesmos dados e unidades usados no ajuste. A curva de comparação manterá essas entradas nos valores médios do ajuste.");
        if (inputEditor != null)
        {
            Ui.Section(form, "Entradas adicionais do modelo");
            form.Children.Add(inputEditor);
        }
        form.Children.Add(confirmAge);
        var save = Ui.Button("Cadastrar em Crescimento e produção", () => { });
        var cancel = Ui.Button("Cancelar", () => { });
        var window = Ui.Dialog("Cadastrar regressão como modelo de crescimento", Ui.WithToolbar(Ui.Scroll(form), cancel, save), 760, predictorCount > 1 ? 820 : 620);
        cancel.Click += (_, _) => window.Close();
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            try
            {
                if (confirmAge.IsChecked != true)
                    throw new InvalidOperationException("Confirme que o eixo X é idade e Y é produção acumulada.");
                string code = id.Text.Trim(), modelName = name.Text.Trim(), unit = ageUnit.SelectedItem?.ToString() ?? "";
                if (code.Length is < 1 or > 31 || Regex.IsMatch(code, "[^A-Za-z0-9_-]"))
                    throw new InvalidOperationException("Use um ID de 1 a 31 caracteres: letras, números, hífen ou sublinhado.");
                if (string.IsNullOrWhiteSpace(modelName) || modelName.Length > 120)
                    throw new InvalidOperationException("Informe um nome de até 120 caracteres.");
                if (!int.TryParse(year.Text, out int versionYear) || versionYear is < 1800 or > 9999)
                    throw new InvalidOperationException("Informe o ano da versão entre 1800 e 9999.");
                if (unit is not ("anos" or "meses"))
                    throw new InvalidOperationException("Selecione anos ou meses para X.");

                string formula = Regex.Replace(fit.S("formula"), @"\b(?:x1|x|t)\b", "t");
                string familyId = "reg_" + Guid.NewGuid().ToString("N")[..12];
                string ageColumn = unit == "meses" ? "idade_meses" : "idade_anos";
                var inputs = new JsonArray(new JsonObject
                {
                    ["simbolo"] = "t",
                    ["origem"] = "calculado",
                    ["coluna"] = ageColumn,
                    ["unidade"] = unit,
                    ["fator"] = 1,
                    ["agregacao"] = "media",
                });
                var fixedInputs = new JsonObject();
                for (int index = 1; index < predictorCount; index++)
                {
                    string symbol = "x" + (index + 1);
                    string meanKey = symbol + "_mean";
                    if (fit[meanKey] == null || !double.IsFinite(fit.N(meanKey)))
                        throw new InvalidOperationException($"O ajuste não contém a média necessária de {symbol}.");
                    fixedInputs[symbol] = fit.N(meanKey);
                }
                if (inputEditor != null)
                {
                    var extras = inputEditor.Read().AsArray();
                    if (extras.Count != predictorCount - 1)
                        throw new InvalidOperationException("Mantenha uma entrada para cada variável adicional do ajuste.");
                    var requiredSymbols = Enumerable.Range(2, predictorCount - 1).Select(i => "x" + i).ToHashSet(StringComparer.Ordinal);
                    if (extras.OfType<JsonObject>().Select(input => input.S("simbolo")).ToHashSet(StringComparer.Ordinal).SetEquals(requiredSymbols) == false)
                        throw new InvalidOperationException("Os símbolos das entradas adicionais devem corresponder a X2 e X3 do ajuste.");
                    foreach (var input in extras) inputs.Add(input?.DeepClone());
                }
                var request = new JsonObject
                {
                    ["op"] = "curve_set",
                    ["index"] = -1,
                    ["id"] = code,
                    ["name"] = modelName,
                    ["b0"] = fit.N("b0"),
                    ["b1"] = fit.N("b1"),
                    ["b2"] = fit.N("b2"),
                    ["formula"] = formula,
                    ["inputs"] = inputs.DeepClone(),
                    ["fixed_inputs"] = fixedInputs,
                    ["model"] = familyId,
                    ["year"] = versionYear,
                    ["current"] = true,
                    ["definition"] = new JsonObject
                    {
                        ["id"] = familyId,
                        ["name"] = modelName,
                        ["formula"] = formula,
                        ["unit"] = unit,
                        ["inputs"] = inputs.DeepClone(),
                    },
                };
                await session.Mutate(request);
                owner.Log($"Regressão {modelName} cadastrada em Crescimento e produção como {code} · {versionYear}.");
                window.Close();
            }
            catch (Exception ex)
            {
                Ui.Error(ex);
            }
            finally
            {
                save.IsEnabled = true;
            }
        };
        window.ShowDialog();
        return Task.CompletedTask;
    }

    private static string InferUnit(string column)
    {
        string value = Regex.Replace(column, "[^A-Za-z]", "").ToLowerInvariant();
        if (value.Contains("dap") || value.Contains("diam")) return "cm";
        if (value.Contains("alt") || value == "h") return "m";
        if (value.Contains("mes")) return "meses";
        if (value.Contains("ano")) return "anos";
        return "unidade da coluna";
    }
}
