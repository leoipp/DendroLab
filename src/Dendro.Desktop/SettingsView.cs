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
        var satelliteSettings = new StackPanel { Margin = new Thickness(18) };
        satelliteSettings.Children.Add(Ui.Text("Acesso a imagens NOAA", 18));
        Ui.AddHelp(satelliteSettings, "Os serviços públicos de NDVI usados pelo DendroLab não exigem chave. Se você recebeu um token específico para o ImageServer NOAA, ele fica criptografado pelo Windows neste usuário e não é salvo no arquivo do projeto. Tokens de outros produtos NOAA, como Climate Data Online, não autenticam esse serviço de imagens.");
        var noaaKey = new PasswordBox { Password = WindowsCredentialStore.ReadNoaaToken(), Margin = new Thickness(0, 8, 0, 8), MaxLength = 4096 };
        Ui.Field(satelliteSettings, "Token do ImageServer NOAA (opcional)", noaaKey);
        var saveNoaaKey = Ui.Button("Salvar token neste computador", () =>
        {
            WindowsCredentialStore.SaveNoaaToken(noaaKey.Password);
            owner.Log("Token NOAA protegido e salvo para este usuário do Windows.");
        });
        var removeNoaaKey = Ui.Button("Remover token", () =>
        {
            WindowsCredentialStore.RemoveNoaaToken();
            noaaKey.Clear();
            owner.Log("Token NOAA removido deste computador.");
        });
        var keyActions = new WrapPanel { Margin = new Thickness(0, 4, 0, 10) };
        keyActions.Children.Add(saveNoaaKey);
        keyActions.Children.Add(removeNoaaKey);
        satelliteSettings.Children.Add(keyActions);
        satelliteSettings.Children.Add(Ui.Text("Copernicus Data Space · Sentinel‑2", 18));
        Ui.AddHelp(satelliteSettings, "Crie um OAuth Client no painel Sentinel Hub da sua conta Copernicus Data Space e informe Client ID e Client Secret. As credenciais são protegidas pelo Windows para este usuário e não entram no arquivo do projeto. O token OAuth é obtido automaticamente e renovado quando necessário.");
        var copernicusCredentials = WindowsCredentialStore.ReadCopernicusCredentials();
        var copernicusClientId = Ui.Input(copernicusCredentials?.ClientId ?? "");
        var copernicusClientSecret = new PasswordBox { Password = copernicusCredentials?.ClientSecret ?? "", MaxLength = 4096 };
        Ui.Field(satelliteSettings, "Client ID", copernicusClientId);
        Ui.Field(satelliteSettings, "Client Secret", copernicusClientSecret);
        var saveCopernicus = Ui.Button("Salvar credenciais Copernicus", () =>
        {
            WindowsCredentialStore.SaveCopernicusCredentials(copernicusClientId.Text, copernicusClientSecret.Password);
            owner.Log("Credenciais Copernicus protegidas e salvas para este usuário do Windows.");
        });
        var removeCopernicus = Ui.Button("Remover credenciais", () =>
        {
            WindowsCredentialStore.RemoveCopernicusCredentials();
            copernicusClientId.Clear();
            copernicusClientSecret.Clear();
            owner.Log("Credenciais Copernicus removidas deste computador.");
        });
        var testCopernicus = Ui.AsyncButton("Testar conexão", async () =>
        {
            await CopernicusDataSpaceClient.TestCredentialsAsync(copernicusClientId.Text, copernicusClientSecret.Password);
            owner.Log("Autenticação Copernicus validada com sucesso.");
        });
        var copernicusActions = new WrapPanel { Margin = new Thickness(0, 4, 0, 10) };
        copernicusActions.Children.Add(saveCopernicus);
        copernicusActions.Children.Add(testCopernicus);
        copernicusActions.Children.Add(removeCopernicus);
        satelliteSettings.Children.Add(copernicusActions);
        tabs.Items.Add(new TabItem { Header = "Imagens de satélite", Content = Ui.Scroll(satelliteSettings) });
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
