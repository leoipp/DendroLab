using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace Dendro.Desktop;

internal static class SettingsView
{
    public static Task Show(MainWindow owner, ProjectSession session)
    {
        var copy = session.Data!.DeepClone().AsObject();
        var tabs = new TabControl();
        var editors = new Dictionary<string, StructuredEditor>();
        foreach (
            var entry in copy.Where(k =>
                    k.Key != "_dendro"
                    && k.Key != "volume"
                    && k.Key != "curvas_crescimento"
                    && k.Key != "regressoes"
                    && k.Key != "identificacao"
                )
                .ToArray()
        )
        {
            var editor = new StructuredEditor(
                entry.Value!,
                entry.Key,
                spreadsheetPicker: entry.Key == "cadastro"
                    ? async () =>
                    {
                        var path = Ui.PickFile();
                        if (path == null)
                            return null;
                        var temporary = new ProjectSession();
                        temporary.Replace(copy.DeepClone().AsObject());
                        if (!await ExcelSelectionDialog.Configure(owner, temporary, path))
                            return null;
                        copy["_dendro"] = temporary.Meta.DeepClone();
                        return (path, temporary.ExcelOptions(path).S("sheet"));
                    }
                    : null
            );
            editors[entry.Key] = editor;
            tabs.Items.Add(
                new TabItem { Header = StructuredEditor.Label(entry.Key), Content = editor }
            );
        }
        var identification = new IdentitySettingsView(copy, session);
        var directColumns=new DirectColumnsView(copy);
        tabs.Items.Add(new TabItem {Header="Colunas do resumo direto",Content=directColumns});
        tabs.Items.Add(new TabItem { Header = "Identificação", Content = identification });
        var volumeOptions = new JsonObject();
        foreach (
            var entry in copy.Obj("volume").Where(k => k.Key != "equacoes" && k.Key != "fator")
        )
            volumeOptions[entry.Key] = entry.Value?.DeepClone();
        var volumeEditor = new StructuredEditor(volumeOptions);
        tabs.Items.Add(new TabItem { Header = "Regras de volumetria", Content = volumeEditor });
        var regressions = new RegressionCatalogView(copy);
        tabs.Items.Add(new TabItem { Header = "Modelos de regressão", Content = regressions });
        var apply = Ui.Button("Aplicar configurações", () => { });
        var window = Ui.Dialog("Configurações do projeto", Ui.WithToolbar(tabs, apply), 1100, 780);
        apply.Click += async (_, _) =>
        {
            apply.IsEnabled = false;
            try
            {
                foreach (var pair in editors)
                    copy[pair.Key] = pair.Value.Read();
                copy["identificacao"] = identification.Read();
                copy.Obj("processador")["colunas_direto"]=directColumns.Read();
                foreach (var pair in volumeEditor.Read().AsObject())
                    copy.Obj("volume")[pair.Key] = pair.Value?.DeepClone();
                copy["regressoes"] = regressions.Read();
                copy.Obj("_dendro").Remove("registry");
                var cadastro = copy.Obj("cadastro");
                if (!string.IsNullOrWhiteSpace(cadastro.S("caminho")))
                {
                    var temporary = new ProjectSession();
                    temporary.Replace(copy);
                    var path = cadastro.S("caminho");
                    temporary.SetExcel(
                        path,
                        cadastro.S("worksheet", "Data"),
                        (int)temporary.ExcelOptions(path).N("header_row", 1)
                    );
                }
                var valid = await WorkspaceOperations.Run(
                    new() { ["op"] = "validate", ["project"] = copy.DeepClone() }
                );
                session.Replace(valid["project"]!.AsObject());
                owner.Log("Configurações atualizadas. Salve o projeto para reutilizá-las.");
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
        return Task.CompletedTask;
    }
}
