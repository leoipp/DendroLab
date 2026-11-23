using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Dendro.Core.NeuralNetworks;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal sealed class MachineLearningView : ToolFormView
{
    private sealed record SourceChoice(string Path, string Name, TableSource Table);

    private readonly MainWindow owner;
    private readonly ProjectSession session;
    private readonly string fixedModelType;
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

        var form = Ui.Form(); SetForm(form);
        string toolName = modelType switch { "neural_net" => "Ajustar rede neural", "random_forest" => "Ajustar Random Forest", "gradient_boosting" => "Ajustar Gradient Boosting", "svr" => "Ajustar SVR", _ => "Ajustar modelo" };
        form.Children.Add(owner.ToolHeader(toolName));
        Ui.AddHelp(form, "Ajuste o modelo com dados do projeto. A saída é um único arquivo ZIP que pode ser selecionado diretamente no Processador.");

        Ui.Section(form, "Treinamento local em C#");
        Ui.AddHelp(form, "O modelo é ajustado dentro do DendroLab. Os dados não são exportados para CSV nem enviados a outro projeto.");

        Ui.Section(form, "Dados de treinamento");
        Ui.Field(form, "Tabela de origem", source);
        Ui.Field(form, "Variável resposta (Y numérico)", target);
        Ui.Field(form, "Variáveis explicativas (numéricas e categóricas)", features);
        Ui.AddHelp(form, "Selecione Y e uma ou mais preditoras. Variáveis categóricas são codificadas e salvas com o modelo. Linhas com valores ausentes nas colunas escolhidas são descartadas.");
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

        SetPrimaryAction(Ui.AsyncButton("Ajustar e salvar ZIP para o Processador", () => owner.Execute(Train)));
        form.Children.Add(status);

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
        bool neural = type == "neural_net", forest = type == "random_forest", boosting = type == "gradient_boosting", svr = type == "svr";
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
        if (string.IsNullOrWhiteSpace(session.FilePath)) throw new InvalidOperationException("Salve o projeto antes de ajustar modelos.");
        if (source.SelectedIndex < 0 || source.SelectedIndex >= sources.Length) throw new InvalidOperationException("Selecione uma tabela para treinamento.");
        string response = target.SelectedItem?.ToString() ?? throw new InvalidOperationException("Selecione a variável resposta Y.");
        string[] predictors = features.SelectedItems.Cast<string>().Where(name => name != response).ToArray();
        if (predictors.Length == 0) throw new InvalidOperationException("Selecione ao menos uma variável explicativa diferente de Y.");
        string type = ModelType();
        string method = validation.SelectedIndex switch { 1 => "kfold", 2 => "full", _ => "split" };
        int k = method == "kfold" ? ParseInt(folds.Text, "Número de partições K-Fold") : 5;
        int estimatorCount = 200, maxDepth = 0, minSplit = 2;
        int[] hiddenLayers = [16, 16, 8];
        string activationName = activation.SelectedItem?.ToString() ?? "relu";
        double rate = .001, subsampleRate = .8, c = 1, epsilonValue = .1;
        string maxFeaturesValue = "1.0", kernelName = kernel.SelectedItem?.ToString() ?? "rbf";
        int epochsCount = 300, batchCount = 32, patienceCount = 30, degreeValue = 3;
        if (type == "neural_net")
        {
            int[] hidden = layers.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(value => int.TryParse(value, out int parsed) && parsed is > 0 and <= 4096 ? parsed : throw new InvalidOperationException("Informe camadas como 16,16,8, com tamanhos entre 1 e 4096.")).ToArray();
            if (hidden.Length is < 1 or > 16) throw new InvalidOperationException("Informe de 1 a 16 camadas ocultas.");
            hiddenLayers = hidden; epochsCount = ParseInt(epochs.Text, "Épocas"); batchCount = ParseInt(batchSize.Text, "Tamanho do lote");
            patienceCount = ParseInt(patience.Text, "Paciência"); rate = Number(learningRate.Text, "Taxa de aprendizado");
        }
        else if (type == "random_forest")
        {
            estimatorCount = ParseInt(trees.Text, "Número de árvores"); maxDepth = OptionalInt(depth.Text);
            minSplit = ParseInt(minSamples.Text, "Mínimo de amostras para divisão"); maxFeaturesValue = ParseMaxFeatures(maxFeatures.Text);
        }
        else if (type == "gradient_boosting")
        {
            estimatorCount = ParseInt(trees.Text, "Número de estimadores"); maxDepth = ParseInt(depth.Text, "Profundidade máxima");
            rate = Number(learningRate.Text, "Taxa de aprendizado"); subsampleRate = Number(subsample.Text, "Subamostragem");
        }
        else
        {
            kernelName = kernel.SelectedItem?.ToString() ?? "rbf"; c = Number(cValue.Text, "C do SVR");
            epsilonValue = Number(epsilon.Text, "Épsilon do SVR");
            if (kernelName == "poly") degreeValue = ParseInt(degree.Text, "Grau do kernel");
        }
        var selected = sources[source.SelectedIndex];
        string[] stratifiers = strata.SelectedItems.Cast<string>().ToArray();
        string modelDirectory = Path.Combine(Path.GetDirectoryName(session.FilePath)!, Path.GetFileNameWithoutExtension(session.FilePath) + "_modelos");
        var options = new NativeModelTrainingOptions(type, method, k, Seed: 42,
            HiddenLayers: hiddenLayers, Activation: activationName, Epochs: epochsCount, BatchSize: batchCount,
            Patience: patienceCount, LearningRate: rate, Estimators: estimatorCount, MaxDepth: maxDepth,
            MinSamplesSplit: minSplit, MaxFeatures: maxFeaturesValue, Subsample: subsampleRate,
            Kernel: kernelName, C: c, Epsilon: epsilonValue, Degree: degreeValue);
        Directory.CreateDirectory(modelDirectory);
        status.Text = "Treinando localmente em C#…";
        var bundles = await Task.Run(() => NativeModelTrainer.Train(selected.Table, response, predictors,
            stratifiers, modelDirectory, options, owner.OperationCancellation,
            message => owner.Dispatcher.Invoke(() => status.Text = message)));
        status.Text = $"Treinamento concluído · {bundles.Length} modelo(s) salvo(s) em {modelDirectory}. Selecione um ZIP no Processador.";
        owner.Log(status.Text);
    }

    private static int ParseInt(string text, string label)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < 1)
            throw new InvalidOperationException($"Informe um valor inteiro positivo para {label}.");
        return value;
    }

    private static int OptionalInt(string text) => string.IsNullOrWhiteSpace(text) ? 0 : ParseInt(text, "profundidade máxima");

    private static string ParseMaxFeatures(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "1.0";
        string value = text.Trim().Replace(',', '.');
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return value;
        if (value is "sqrt" or "log2") return value;
        throw new InvalidOperationException("max_features deve ser uma fração entre 0 e 1, sqrt ou log2.");
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
