using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal sealed class MachineLearningView : ScrollViewer
{
    private sealed record SourceChoice(string Path, string Name, TableSource Table);

    private readonly MainWindow owner;
    private readonly ProjectSession session;
    private readonly string fixedModelType;
    private readonly TextBox ifpcRoot = Ui.Input();
    private readonly ComboBox source = Ui.Combo([]), target = Ui.Combo([]), validation = Ui.Combo(["Divisão treino/teste (80/20)", "Validação cruzada K-Fold", "Treinar com toda a base"]);
    private readonly TextBox folds = Ui.Input("5");
    private readonly ListBox features = new() { SelectionMode = SelectionMode.Multiple, MinHeight = 120, MaxHeight = 250 };
    private readonly ListBox strata = new() { SelectionMode = SelectionMode.Multiple, MinHeight = 70, MaxHeight = 150 };
    private readonly StackPanel parameters = new();
    private readonly TextBox layers = Ui.Input("16,16,8"), epochs = Ui.Input("300"), batchSize = Ui.Input("32"), patience = Ui.Input("30"), learningRate = Ui.Input("0,001");
    private readonly TextBox trees = Ui.Input("200"), depth = Ui.Input(""), minSamples = Ui.Input("2"), maxFeatures = Ui.Input("1,0"), subsample = Ui.Input("0,8"), cValue = Ui.Input("1,0"), epsilon = Ui.Input("0,1"), degree = Ui.Input("3");
    private readonly ComboBox activation = Ui.Combo(["relu", "tanh", "sigmoid"]), kernel = Ui.Combo(["rbf", "linear", "poly"]);
    private readonly TextBlock status = Ui.Text("");
    private SourceChoice[] sources = [];
    private bool loading;

    internal MachineLearningView(MainWindow owner, ProjectSession session, string modelType)
    {
        this.owner = owner;
        this.session = session;
        fixedModelType = modelType;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var form = Ui.Form(); Content = form;
        string toolName = modelType switch { "neural_net" => "Ajustar rede neural", "random_forest" => "Ajustar Random Forest", "gradient_boosting" => "Ajustar Gradient Boosting", "svr" => "Ajustar SVR", _ => "Ajustar modelo" };
        form.Children.Add(owner.ToolHeader(toolName));
        Ui.AddHelp(form, "Ajuste o modelo com dados do projeto. A saída é um único arquivo ZIP que pode ser selecionado diretamente no Processador.");

        Ui.Section(form, "Ambiente de treinamento");
        Ui.Field(form, "Pasta do projeto IFPC", ifpcRoot);
        var rootActions = new WrapPanel();
        rootActions.Children.Add(Ui.Button("Localizar IFPC…", ChooseIfpcRoot));
        form.Children.Add(rootActions);
        Ui.AddHelp(form, "O ambiente Python local fornece o motor de treinamento. O aplicativo salva apenas o ZIP final do modelo.");

        Ui.Section(form, "Dados de treinamento");
        Ui.Field(form, "Tabela de origem", source);
        Ui.Field(form, "Variável resposta (Y numérico)", target);
        Ui.Field(form, "Variáveis explicativas (numéricas e categóricas)", features);
        Ui.AddHelp(form, "Selecione Y e uma ou mais preditoras. Variáveis categóricas são codificadas com OneHotEncoder, e o mesmo encoder é salvo para aplicação futura. Valores ausentes são descartados pelo fluxo legado de treinamento.");
        Ui.Field(form, "Estratificar e treinar um modelo por grupo (opcional)", strata);

        Ui.Section(form, "Validação");
        Ui.Field(form, "Validação", validation);
        var foldField = Ui.Field(form, "Número de partições K-Fold", folds);
        foldField.Visibility = Visibility.Collapsed;

        Ui.Section(form, "Parâmetros do algoritmo");
        form.Children.Add(parameters);
        Ui.Field(parameters, "Camadas ocultas (neurônios por camada)", layers);
        Ui.Field(parameters, "Ativação", activation);
        Ui.Field(parameters, "Épocas máximas", epochs);
        Ui.Field(parameters, "Tamanho do lote", batchSize);
        Ui.Field(parameters, "Paciência para parada antecipada", patience);
        Ui.Field(parameters, "Taxa de aprendizado", learningRate);
        Ui.Field(parameters, "Número de árvores / estimadores", trees);
        Ui.Field(parameters, "Profundidade máxima (vazio = sem limite)", depth);
        Ui.Field(parameters, "Mínimo de amostras para divisão", minSamples);
        Ui.Field(parameters, "Variáveis por árvore (max_features)", maxFeatures);
        Ui.Field(parameters, "Subamostragem (subsample)", subsample);
        Ui.Field(parameters, "Kernel SVR", kernel);
        Ui.Field(parameters, "C do SVR", cValue);
        Ui.Field(parameters, "Épsilon do SVR", epsilon);
        Ui.Field(parameters, "Grau do kernel polinomial", degree);
        validation.SelectionChanged += (_, _) => foldField.Visibility = validation.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        kernel.SelectionChanged += (_, _) => UpdateParameters();
        UpdateParameters();

        form.Children.Add(Ui.AsyncButton("Ajustar e salvar ZIP para o Processador", () => owner.Execute(Train)));
        form.Children.Add(status);

        ifpcRoot.Text = DetectIfpcRoot() ?? "";
        source.SelectionChanged += async (_, _) => { if (!loading) await owner.Execute(LoadColumns); };
        target.SelectionChanged += (_, _) => UpdateFeatureChoices();
    }

    internal async Task Initialize(string? initialPath = null, string? initialTable = null)
    {
        loading = true;
        try
        {
            sources = await Task.Run(BuildSources);
            source.ItemsSource = sources.Select(item => item.Name).ToArray();
            int index = initialPath == null ? -1 : Array.FindIndex(sources, item =>
                string.Equals(item.Path, initialPath, StringComparison.OrdinalIgnoreCase)
                && (initialTable == null || item.Table.Table == initialTable));
            source.SelectedIndex = index >= 0 ? index : sources.Length > 0 ? 0 : -1;
        }
        finally { loading = false; }
        UpdateParameters();
        if (source.SelectedIndex >= 0) await LoadColumns();
        else status.Text = "Adicione uma tabela ao projeto para começar.";
    }

    private static string? DetectIfpcRoot()
    {
        foreach (string candidate in new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "IFPC")),
            @"G:\PycharmProjects\IFPC",
        })
            if (File.Exists(Path.Combine(candidate, "cli", "ModelFitter.py"))) return candidate;
        return null;
    }

    private void ChooseIfpcRoot()
    {
        string? folder = Ui.PickFolder();
        if (folder != null) ifpcRoot.Text = folder;
    }

    private SourceChoice[] BuildSources()
    {
        var result = new List<SourceChoice>();
        var catalogs = new ProjectTableCatalogCache();
        foreach (string path in session.Files.Where(session.IsTabular))
        {
            string resolved = session.Resolve(path);
            string[] tables;
            try { tables = catalogs.Tables(session, path); }
            catch (IOException) { continue; }
            catch (InvalidDataException) { continue; }
            foreach (string table in tables)
            {
                var options = session.ExcelOptions(path);
                result.Add(new(path, session.ContentName(path) + (tables.Length > 1 ? " · " + table : ""),
                    new TableSource(resolved, table, session.IsSpreadsheet(path) ? (int)options.N("header_row", 1) : 1)));
            }
        }
        return result.ToArray();
    }

    private async Task LoadColumns()
    {
        if (source.SelectedIndex < 0 || source.SelectedIndex >= sources.Length) return;
        var selected = sources[source.SelectedIndex];
        var data = await Task.Run(() =>
        {
            using var cursor = new TableCursor(selected.Table);
            var sample = cursor.Rows().Take(200).ToArray();
            string[] numeric = Enumerable.Range(0, cursor.Columns.Length)
                .Where(index => sample.Any(row => IsNumber(row.ElementAtOrDefault(index))))
                .Select(index => cursor.Columns[index]).ToArray();
            return (cursor.Columns, Numeric: numeric);
        });
        loading = true;
        try
        {
            target.ItemsSource = data.Numeric;
            target.SelectedIndex = data.Numeric.Length > 0 ? 0 : -1;
            string[] previous = features.SelectedItems.Cast<string>().ToArray();
            features.ItemsSource = data.Columns;
            strata.ItemsSource = data.Columns;
            foreach (string name in previous.Where(data.Columns.Contains)) features.SelectedItems.Add(name);
        }
        finally { loading = false; }
        UpdateFeatureChoices();
        status.Text = data.Numeric.Length == 0 ? "Não encontrei colunas numéricas para Y." : "Selecione Y e os preditores; a estratificação é opcional.";
    }

    private void UpdateFeatureChoices()
    {
        string y = target.SelectedItem?.ToString() ?? "";
        if (features.ItemsSource is not IEnumerable<string>) return;
        // Keep the user selection but make the selected response unavailable as a predictor.
        foreach (var item in features.Items)
        {
            if (item is not string name) continue;
            var container = features.ItemContainerGenerator.ContainerFromItem(name) as ListBoxItem;
            if (container != null) container.IsEnabled = !string.Equals(name, y, StringComparison.Ordinal);
        }
    }

    private void UpdateParameters()
    {
        string type = ModelType();
        bool neural = type == "neural_net", forest = type == "random_forest", boosting = type is "gradient_boosting" or "xgboost", svr = type == "svr";
        SetVisible(layers, neural); SetVisible(activation, neural); SetVisible(epochs, neural); SetVisible(batchSize, neural);
        SetVisible(patience, neural); SetVisible(learningRate, neural || boosting);
        SetVisible(trees, !neural && !svr); SetVisible(depth, forest || boosting);
        SetVisible(minSamples, forest); SetVisible(maxFeatures, forest); SetVisible(subsample, boosting);
        SetVisible(kernel, svr); SetVisible(cValue, svr); SetVisible(epsilon, svr); SetVisible(degree, svr && kernel.SelectedItem?.ToString() == "poly");
    }

    private static void SetVisible(FrameworkElement element, bool visible)
    {
        if (element.Parent is FrameworkElement field) field.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private string ModelType() => fixedModelType;

    private async Task Train()
    {
        string root = ValidateIfpcRoot();
        if (string.IsNullOrWhiteSpace(session.FilePath)) throw new InvalidOperationException("Salve o projeto antes de ajustar modelos.");
        if (source.SelectedIndex < 0 || source.SelectedIndex >= sources.Length) throw new InvalidOperationException("Selecione uma tabela para treinamento.");
        string response = target.SelectedItem?.ToString() ?? throw new InvalidOperationException("Selecione a variável resposta Y.");
        string[] predictors = features.SelectedItems.Cast<string>().Where(name => name != response).ToArray();
        if (predictors.Length == 0) throw new InvalidOperationException("Selecione ao menos uma variável explicativa diferente de Y.");
        string type = ModelType();
        string method = validation.SelectedIndex switch { 1 => "kfold", 2 => "full", _ => "split" };
        int k = method == "kfold" ? ParseInt(folds.Text, "Número de partições K-Fold") : 5;
        var config = new Dictionary<string, object>
        {
            ["model_type"] = type, ["metodo_validacao"] = method, ["k_folds"] = k,
            ["seed"] = 42,
        };
        if (type == "neural_net")
        {
            int[] hidden = layers.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(value => int.TryParse(value, out int parsed) && parsed is > 0 and <= 4096 ? parsed : throw new InvalidOperationException("Informe camadas como 16,16,8, com tamanhos entre 1 e 4096.")).ToArray();
            if (hidden.Length is < 1 or > 16) throw new InvalidOperationException("Informe de 1 a 16 camadas ocultas.");
            config["camadas"] = hidden; config["ativacao"] = activation.SelectedItem?.ToString() ?? "relu";
            config["epochs"] = ParseInt(epochs.Text, "Épocas"); config["batch_size"] = ParseInt(batchSize.Text, "Tamanho do lote");
            config["patience"] = ParseInt(patience.Text, "Paciência"); config["learning_rate"] = Number(learningRate.Text, "Taxa de aprendizado");
        }
        else if (type == "random_forest")
        {
            config["n_estimators"] = ParseInt(trees.Text, "Número de árvores"); config["max_depth"] = OptionalInt(depth.Text);
            config["min_samples_split"] = ParseInt(minSamples.Text, "Mínimo de amostras para divisão"); config["max_features"] = ParseMaxFeatures(maxFeatures.Text);
        }
        else if (type is "gradient_boosting" or "xgboost")
        {
            config["n_estimators"] = ParseInt(trees.Text, "Número de estimadores"); config["max_depth"] = ParseInt(depth.Text, "Profundidade máxima");
            config["learning_rate"] = Number(learningRate.Text, "Taxa de aprendizado"); config["subsample"] = Number(subsample.Text, "Subamostragem");
            if (type == "xgboost") config["colsample_bytree"] = .8;
        }
        else
        {
            config["kernel"] = kernel.SelectedItem?.ToString() ?? "rbf"; config["C"] = Number(cValue.Text, "C do SVR");
            config["epsilon"] = Number(epsilon.Text, "Épsilon do SVR");
            if (config["kernel"].ToString() == "poly") config["degree"] = ParseInt(degree.Text, "Grau do kernel");
        }
        var selected = sources[source.SelectedIndex];
        string[] stratifiers = strata.SelectedItems.Cast<string>().ToArray();
        string job = Path.Combine(Path.GetTempPath(), "DendroLab-ModelFitter-" + Guid.NewGuid().ToString("N"));
        string modelDirectory = Path.Combine(Path.GetDirectoryName(session.FilePath)!, Path.GetFileNameWithoutExtension(session.FilePath) + "_modelos");
        Directory.CreateDirectory(job);
        Directory.CreateDirectory(modelDirectory);
        try
        {
            string csv = Path.Combine(job, "input.csv"), requestPath = Path.Combine(job, "request.json"), responsePath = Path.Combine(job, "response.json");
            await Task.Run(() => WriteCsv(selected.Table, csv));
            var request = new JsonObject
            {
                ["mode"] = "train", ["ifpc_root"] = root, ["input_csv"] = csv,
                ["output_directory"] = modelDirectory,
                ["response_json"] = responsePath, ["target"] = response,
                ["features"] = new JsonArray(predictors.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()),
                ["stratifiers"] = new JsonArray(stratifiers.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()),
                ["config"] = JsonSerializer.SerializeToNode(config),
            };
            await File.WriteAllTextAsync(requestPath, request.ToJsonString());
            status.Text = "Treinando pelo motor do IFPC…";
            var result = await RunBridge(root, requestPath, responsePath, owner.OperationCancellation);
            string[] names = result["models"]!.AsArray().Select(item => Path.GetFileName(item!["bundle"]!.GetValue<string>())).ToArray();
            status.Text = $"Treinamento concluído · ZIP salvo em {modelDirectory}: {string.Join(", ", names)}. Selecione-o no Processador.";
            owner.Log(status.Text);
        }
        finally { try { Directory.Delete(job, true); } catch (IOException) { } }
    }

    private string ValidateIfpcRoot()
    {
        string root = Path.GetFullPath(ifpcRoot.Text.Trim());
        if (!File.Exists(Path.Combine(root, "cli", "ModelFitter.py"))) throw new InvalidOperationException("Selecione a pasta do projeto IFPC que contém cli/ModelFitter.py.");
        string python = Path.Combine(root, ".venv", "Scripts", "python.exe");
        if (!File.Exists(python)) throw new InvalidOperationException($"Ambiente Python do IFPC não encontrado: {python}. Crie/ative o .venv do IFPC antes de usar esta ferramenta.");
        if (!File.Exists(BridgePath())) throw new FileNotFoundException("Script de integração do ModelFitter ausente.", BridgePath());
        return root;
    }

    private static string BridgePath() => Path.Combine(AppContext.BaseDirectory, "Scripts", "model_fitter_bridge.py");

    private static async Task<JsonObject> RunBridge(string root, string requestPath, string responsePath, CancellationToken cancellation)
    {
        string python = Path.Combine(root, ".venv", "Scripts", "python.exe");
        var start = new ProcessStartInfo
        {
            FileName = python,
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(BridgePath()); start.ArgumentList.Add(root); start.ArgumentList.Add(requestPath);
        start.Environment["TF_CPP_MIN_LOG_LEVEL"] = "2";
        using var process = new Process { StartInfo = start };
        process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var registration = cancellation.Register(() => { try { process.Kill(true); } catch { } });
        try { await process.WaitForExitAsync(cancellation); }
        catch (OperationCanceledException) { throw; }
        string output = await stdout, error = await stderr;
        if (!File.Exists(responsePath)) throw new InvalidOperationException("O motor Python do IFPC terminou sem resposta. " + FirstText(error, output));
        var response = JsonNode.Parse(await File.ReadAllTextAsync(responsePath))?.AsObject() ?? throw new InvalidDataException("Resposta inválida do ModelFitter.");
        if (process.ExitCode != 0 || !response.B("ok")) throw new InvalidOperationException(response.S("error", FirstText(error, output)));
        return response;
    }

    private static string FirstText(string first, string second) => !string.IsNullOrWhiteSpace(first) ? first.Trim() : second.Trim();

    private static void WriteCsv(TableSource source, string path)
    {
        using var cursor = new TableCursor(source);
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine(string.Join(";", cursor.Columns.Select(Csv)));
        foreach (var row in cursor.Rows()) writer.WriteLine(string.Join(";", row.Select(value => Csv(TableCursor.Text(value)))));
        static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static int ParseInt(string text, string label)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < 1)
            throw new InvalidOperationException($"Informe um valor inteiro positivo para {label}.");
        return value;
    }

    private static int OptionalInt(string text) => string.IsNullOrWhiteSpace(text) ? 0 : ParseInt(text, "profundidade máxima");

    private static object ParseMaxFeatures(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 1.0;
        return double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : text.Trim();
    }

    private static double Number(string text, string label)
    {
        try { return Ui.Number(text); }
        catch (InvalidOperationException) { throw new InvalidOperationException($"Informe um número válido para {label}."); }
    }

    private static bool IsNumber(object? value)
    {
        if (value is double number) return double.IsFinite(number);
        string text = TableCursor.Text(value).Trim();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.GetCultureInfo("pt-BR"), out number) && double.IsFinite(number);
    }

}
