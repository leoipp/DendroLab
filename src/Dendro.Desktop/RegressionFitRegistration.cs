using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Dendro.Core.Regression;

namespace Dendro.Desktop;

internal static class RegressionFitRegistration
{
    public static bool CanRegister(JsonObject fit) => RegressionApplication.CanSave(fit);

    public static Task Open(MainWindow owner, ProjectSession session, JsonObject chart, JsonObject fit)
    {
        if (!CanRegister(fit))
            throw new InvalidOperationException("Salve apenas um ajuste convergido com 1 a 3 variáveis e 1 a 4 coeficientes.");
        string defaultName = fit.S("model", "Modelo")
            + (fit.S("group") is "" or "Global" ? "" : " · " + fit.S("group"));
        var name = Ui.Input(defaultName);
        var form = Ui.Form();
        Ui.AddHelp(form, "Esta equação ajustada será salva no catálogo geral do projeto, com seus coeficientes. Depois, use Aplicar regressões para escolher uma base e mapear X1, X2 e X3 às colunas correspondentes.");
        Ui.Field(form, "Nome da regressão salva", name);
        Ui.Field(form, "Equação", Ui.Text(fit.S("formula"), 13));
        Ui.Field(form, "Variáveis explicativas", Ui.Text((int)fit.N("predictor_count", 1) + "P", 13));
        var save = Ui.Button("Salvar regressão", () => { });
        var cancel = Ui.Button("Cancelar", () => { });
        var window = Ui.Dialog("Salvar regressão para aplicação", Ui.WithToolbar(Ui.Scroll(form), cancel, save), 680, 520);
        cancel.Click += (_, _) => window.Close();
        save.Click += (_, _) =>
        {
            save.IsEnabled = false;
            try
            {
                var model = RegressionApplication.FromFit(chart, fit, name.Text);
                var models = session.Meta.Arr(RegressionApplication.StorageKey);
                RegressionApplication.ValidateCatalog(models);
                if (models.Count >= 1000)
                    throw new InvalidOperationException("O catálogo permite até 1.000 regressões ajustadas por projeto.");
                models.Add(RegressionApplication.Write(model));
                session.Touch();
                owner.Log($"Regressão {model.Name} salva no catálogo geral para aplicação.");
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
}
