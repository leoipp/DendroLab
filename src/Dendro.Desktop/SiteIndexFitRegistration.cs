using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Dendro.Core.Regression;

namespace Dendro.Desktop;

internal static class SiteIndexFitRegistration
{
    public static bool CanRegister(JsonObject fit) =>
        RegressionApplication.CanSave(fit) && fit.N("predictor_count", 1) == 1;

    public static bool CanRegister(JsonObject chart, JsonObject fit) =>
        CanRegister(fit) && (chart.S("y").Equals("DAPd", StringComparison.OrdinalIgnoreCase)
            || chart.S("y").Equals("Hd", StringComparison.OrdinalIgnoreCase));

    public static Task Open(MainWindow owner, ProjectSession session, JsonObject chart, JsonObject fit)
    {
        if (!CanRegister(chart, fit))
            throw new InvalidOperationException("Para o catálogo de índice de sítio, salve um ajuste 1P convergido usando DAPd ou Hd como Y e idade como X.");

        string xColumn = chart.S("x"), yColumn = chart.S("y");
        int parameterCount = (int)fit.N("parameters");
        var id = Ui.Input("SI-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant());
        var name = Ui.Input(fit.S("model", "Curva-guia") + " · " + yColumn);
        var modelName = Ui.Input(fit.S("model", "Personalizado"));
        var year = Ui.Input(DateTime.Now.Year.ToString());
        var species = Ui.Input("");
        var referenceAge = Ui.Input("");
        var ageUnit = Ui.Combo(["anos", "meses"], xColumn.Contains("mes", StringComparison.OrdinalIgnoreCase) ? "meses" : "anos");
        var formula = Ui.Input(fit.S("formula"));
        var coefficients = Enumerable.Range(0, parameterCount).Select(i => Ui.Input(fit.N("b" + i).ToString("G17", System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        var current = new CheckBox { Content = "Versão vigente para este ID", IsChecked = true, Margin = new Thickness(2, 7, 2, 5) };
        var form = Ui.Form();
        Ui.AddHelp(form, "Ajuste salvo como curva-guia candidata. A geração das famílias anamórficas e polimórficas virá na próxima etapa.");
        Ui.Section(form, "Identificação da equação");
        Ui.Field(form, "ID da equação", id);
        Ui.Field(form, "Nome do modelo", name);
        Ui.Field(form, "Modelo", modelName);
        Ui.Field(form, "Ano da versão", year);
        form.Children.Add(current);
        Ui.Section(form, "Índice de sítio");
        Ui.Field(form, "Espécie / material genético", species);
        Ui.Field(form, "Idade-base", referenceAge);
        Ui.Field(form, "Unidade de idade", ageUnit);
        Ui.Section(form, "Equação ajustada");
        Ui.Field(form, "Fórmula", formula);
        for (int i = 0; i < coefficients.Length; i++) Ui.Field(form, "b" + i, coefficients[i]);
        Ui.Field(form, "Entrada esperada · idade (t)", Ui.Text(xColumn, 13));
        Ui.Field(form, "Variável resposta (Y)", Ui.Text(yColumn, 13));

        var save = Ui.Button("Salvar modelo", () => { });
        var cancel = Ui.Button("Cancelar", () => { });
        var window = Ui.Dialog("Cadastrar modelo de índice de sítio", Ui.WithToolbar(Ui.Scroll(form), cancel, save), 720, 860);
        cancel.Click += (_, _) => window.Close();
        save.Click += (_, _) =>
        {
            save.IsEnabled = false;
            try
            {
                string code = id.Text.Trim(), modelNameValue = name.Text.Trim();
                if (code.Length is < 1 or > 31 || code.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
                    throw new InvalidOperationException("Use um ID de até 31 caracteres com letras, números, hífen ou sublinhado.");
                if (modelNameValue.Length is < 1 or > 120) throw new InvalidOperationException("Informe um nome de até 120 caracteres.");
                if (!int.TryParse(year.Text, out int versionYear) || versionYear is < 1800 or > 9999)
                    throw new InvalidOperationException("Informe o ano da versão entre 1800 e 9999.");
                double baseAge = Ui.Number(referenceAge.Text);
                if (!double.IsFinite(baseAge) || baseAge <= 0) throw new InvalidOperationException("A idade-base deve ser positiva.");
                string unit = ageUnit.SelectedItem?.ToString() ?? "";
                if (unit is not ("anos" or "meses")) throw new InvalidOperationException("Selecione anos ou meses.");

                var catalog = session.Meta.Arr("site_index_models");
                if (catalog.Count >= 500) throw new InvalidOperationException("O catálogo aceita até 500 modelos por projeto.");
                if (catalog.OfType<JsonObject>().Any(row => row.S("codigo").Equals(code, StringComparison.OrdinalIgnoreCase) && row.N("ano") == versionYear))
                    throw new InvalidOperationException("Já existe uma versão desse ID para o ano informado.");

                var fitted = RegressionApplication.FromFit(chart, fit, modelNameValue);
                var coefficientsValues = coefficients.Select((field, index) => Ui.Number(field.Text)).ToArray();
                var regression = fitted with { Formula = formula.Text.Trim(), Coefficients = coefficientsValues };
                RegressionApplication.Validate(regression);
                var row = RegressionApplication.Write(regression);
                row["codigo"] = code;
                row["modelo"] = modelName.Text.Trim();
                row["ano"] = versionYear;
                row["vigente"] = current.IsChecked == true;
                row["species"] = species.Text.Trim();
                row["reference_age"] = baseAge;
                row["age_unit"] = unit;
                row["source_y"] = yColumn;
                row["source_chart"] = ChartView.Identity(chart);
                row["kind"] = "anamorphic_guide_curve";
                for (int i = 0; i < coefficientsValues.Length; i++) row["b" + i] = coefficientsValues[i];
                row["entradas"] = new JsonArray(new JsonObject
                {
                    ["simbolo"] = "t", ["origem"] = "entrada", ["coluna"] = xColumn,
                    ["unidade"] = unit, ["fator"] = 1, ["agregacao"] = "media",
                });
                if (current.IsChecked == true)
                    foreach (var previous in catalog.OfType<JsonObject>().Where(previous => previous.S("codigo") == code))
                        previous["vigente"] = false;
                row["created"] = DateTimeOffset.Now.ToString("O");
                catalog.Add(row);
                RegressionApplication.ValidateCatalog(catalog);
                session.Touch();
                owner.Log($"Modelo de índice de sítio {modelNameValue} cadastrado como {code} · {versionYear}.");
                window.Close();
            }
            catch (Exception ex) { Ui.Error(ex); }
            finally { save.IsEnabled = true; }
        };
        window.ShowDialog();
        return Task.CompletedTask;
    }
}
