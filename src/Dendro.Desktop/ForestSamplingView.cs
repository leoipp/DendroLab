using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Dendro.Core.Sampling;

namespace Dendro.Desktop;

internal sealed class ForestSamplingView : ScrollViewer
{
    private readonly MainWindow owner;
    private readonly ComboBox source = new() { IsEditable = false, IsTextSearchEnabled = true, ToolTip = "Escolha um shapefile do projeto ou use Procurar shapefile." };
    private readonly ComboBox idField = new() { IsEditable = false, IsEnabled = false };
    private readonly ComboBox projectField = new() { IsEditable = false, IsEnabled = false };
    private const string NoProjectField = "(Sem campo de projeto)";
    private int fieldLoadVersion;
    private bool changingSource;
    private string? loadedSource;
    private string SelectedSource => source.SelectedItem as string ?? "";
    private readonly TextBox hectares = Field(90, "3");
    private readonly TextBox edge = Field(90, "-20");
    private readonly TextBox distance = Field(90, "56.42");
    private readonly TextBox areaMin = Field(90, "3");
    private readonly TextBox areaMid = Field(90, "6");
    private readonly TextBox countMin = Field(90, "1");
    private readonly TextBox countMid = Field(90, "2");
    private readonly TextBox seed = Field(90, "42");
    private readonly ComboBox method = new();
    private readonly GrtsSettingsPanel grtsSettings = new();
    private readonly CheckBox firstVertices = new() { Content = "D1 usa vértices em vez do centro da malha" };
    private readonly CheckBox sequentialNumbering = new() { Content = "Usar numeração sequencial por lote (prefixo de 14 caracteres)", IsChecked = true };
    private readonly CheckBox sequenceByProject = new() { Content = "Separar sequência por projeto", IsChecked = true };
    private readonly CheckBox sequenceXY = new() { Content = "Ordenar parcelas do talhão por X e Y", IsChecked = true };
    private readonly TextBlock status = Ui.Text("Selecione um shapefile de polígonos projetado em metros.", 12);
    private readonly Func<ForestSamplingResult, Task> showOnMap;

    internal ForestSamplingView(MainWindow owner, IEnumerable<string>? projectShapefiles, Func<ForestSamplingResult, Task> showOnMap)
    {
        this.owner = owner;
        this.showOnMap = showOnMap;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        method.ItemsSource = new[]
        {
            "Sistemática atual · D1 a D4",
            "Sistemática com início aleatório",
            "Distância máxima (maximin)",
            "GRTS · endereços hierárquicos e probabilidades"
        };
        method.SelectedIndex = 0;
        var projectFiles = projectShapefiles?.ToArray() ?? [];
        source.ItemsSource = projectFiles;
        source.SelectedIndex = projectFiles.Length > 0 ? 0 : -1;
        System.Windows.Automation.AutomationProperties.SetAutomationId(source, "SamplingSource");
        System.Windows.Automation.AutomationProperties.SetAutomationId(idField, "SamplingIdField");
        System.Windows.Automation.AutomationProperties.SetAutomationId(projectField, "SamplingProjectField");

        var form = Ui.Form(); Content = form;
        form.Children.Add(owner.ToolHeader("Amostragem"));
        Ui.AddHelp(form, "Selecione os talhões e configure o desenho amostral. As parcelas geradas são adicionadas a Conteúdos e aparecem diretamente no mapa.");
        Ui.Section(form, "Entrada");
        Ui.Field(form, "Shapefile de talhões", source);
        form.Children.Add(Ui.AsyncButton("Procurar shapefile…", () => owner.Execute(Browse)));
        Ui.Section(form, "Método e intensidade");
        Ui.Field(form, "Método", method);
        Ui.Field(form, "Hectares por parcela", hectares);
        Ui.Field(form, "Recuo da borda (m)", edge);
        Ui.Field(form, "Raio da zona de exclusão (m)", distance);
        Ui.Field(form, "Talhão abaixo desta área (ha)", areaMin);
        Ui.Field(form, "Parcelas abaixo da área mínima", countMin);
        Ui.Field(form, "Faixa intermediária (ha)", areaMid);
        Ui.Field(form, "Parcelas na faixa intermediária", countMid);
        Ui.Field(form, "Semente aleatória", seed);
        form.Children.Add(firstVertices);
        form.Children.Add(grtsSettings);
        Ui.Section(form, "Campos e numeração");
        Ui.Field(form, "Campo identificador", idField);
        Ui.Field(form, "Campo de projeto", projectField);
        form.Children.Add(sequentialNumbering);
        form.Children.Add(sequenceByProject);
        form.Children.Add(sequenceXY);
        form.Children.Add(Ui.AsyncButton("Gerar no mapa", () => owner.Execute(Run)));
        status.TextWrapping = TextWrapping.Wrap;
        form.Children.Add(status);
        source.SelectionChanged += async (_, _) =>
        {
            if (!changingSource) await LoadAttributeFields();
        };
        method.SelectionChanged += (_, _) => UpdateMethodFields();
        grtsSettings.SettingsChanged += UpdateMethodFields;
        idField.SelectionChanged += (_, _) => grtsSettings.SetIdentifier(idField.SelectedItem as string ?? "");
        System.Windows.Automation.AutomationProperties.SetAutomationId(method, "SamplingMethod");
        System.Windows.Automation.AutomationProperties.SetAutomationId(distance, "SamplingExclusionRadius");
        UpdateMethodFields();
        Unloaded += (_, _) => fieldLoadVersion++;
    }

    internal Task Initialize() => LoadAttributeFields();

    private void UpdateMethodFields()
    {
        bool grts = method.SelectedIndex == 3;
        grtsSettings.Visibility = grts ? Visibility.Visible : Visibility.Collapsed;
        if (distance.Parent is UIElement distanceGroup) distanceGroup.IsEnabled = !grts;
        firstVertices.Visibility = method.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var field in new[] { hectares, areaMin, areaMid, countMin, countMid })
            if (field.Parent is UIElement group) group.Visibility = !grts || grtsSettings.UsesIntensity ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task LoadAttributeFields()
    {
        string path = SelectedSource;
        int version = ++fieldLoadVersion;
        loadedSource = null;
        idField.ItemsSource = null; projectField.ItemsSource = null;
        idField.IsEnabled = projectField.IsEnabled = false;
        grtsSettings.LoadFields([], [], "");
        if (string.IsNullOrWhiteSpace(path)) { status.Text = "Selecione um shapefile para carregar seus campos."; return; }
        status.Text = "Lendo tabela de atributos…";
        try
        {
            var schema = await Task.Run(() => (Fields: ForestSampler.ReadAttributeFields(path), Records: ForestSampler.ReadAttributeRecords(path)));
            if (version != fieldLoadVersion) return;
            var fields = schema.Fields;
            if (fields.Length == 0) throw new InvalidDataException("A tabela de atributos não contém campos.");
            idField.ItemsSource = fields;
            idField.SelectedItem = fields.FirstOrDefault(field => field.Equals("REF_ID", StringComparison.OrdinalIgnoreCase)) ?? fields[0];
            projectField.ItemsSource = new[] { NoProjectField }.Concat(fields).ToArray();
            projectField.SelectedItem = fields.FirstOrDefault(field => field.Equals("PROJETO", StringComparison.OrdinalIgnoreCase)) ?? NoProjectField;
            idField.IsEnabled = projectField.IsEnabled = true;
            loadedSource = path;
            grtsSettings.LoadFields(fields, schema.Records, idField.SelectedItem as string ?? "");
            status.Text = $"{fields.Length} campos carregados da tabela de atributos.";
        }
        catch (Exception ex)
        {
            if (version == fieldLoadVersion) status.Text = ex.Message;
        }
    }

    private static TextBox Field(double width, string text) => Ui.Input(text);

    private async Task Browse()
    {
        var picker = new OpenFileDialog { Filter = "Shapefile|*.shp", CheckFileExists = true, Title = "Selecionar polígonos dos talhões" };
        if (picker.ShowDialog(owner) == true) await SelectSourcePath(picker.FileName);
    }

    internal async Task SelectSourcePath(string path)
    {
        changingSource = true;
        try
        {
            var paths = (source.ItemsSource as IEnumerable<string> ?? []).ToList();
            string selected = paths.FirstOrDefault(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase)) ?? path;
            if (!paths.Contains(selected)) paths.Add(selected);
            source.ItemsSource = paths.ToArray();
            source.SelectedItem = selected;
        }
        finally { changingSource = false; }
        await LoadAttributeFields();
    }

    private async Task Run()
    {
        try
        {
            if (!string.Equals(loadedSource, SelectedSource, StringComparison.OrdinalIgnoreCase) || idField.SelectedItem is not string)
                throw new InvalidOperationException("Selecione um shapefile válido e aguarde o carregamento dos campos da tabela de atributos.");
            var options = ReadOptions();
            string sourcePath = SelectedSource;
            status.Text = "Calculando…";
            IsEnabled = false;
            var result = await Task.Run(() => ForestSampler.Run(sourcePath, options, owner.OperationCancellation));
            await showOnMap(result);
            status.Text = $"{result.Samples.Count(sample => sample.SiteUse == "Base"):N0} parcelas de base e {result.Samples.Count(sample => sample.SiteUse == "Reserva"):N0} reservas geradas no mapa. {result.Warning}";
            if (result.GrtsDesign is { Count: > 0 }) status.Text += "\n" + string.Join("\n", result.GrtsDesign.Select(design =>
                $"{design.Stratum}: {design.BaseSize} base + {design.ReplacementSize} reservas; {design.FrameSize} candidatos; distância obtida {(design.AchievedMinimumDistance is double value ? value.ToString("N1") + " m" : "não se aplica")}."));
        }
        catch (Exception ex) { status.Text = ex.Message; throw; }
        finally { IsEnabled = true; }
    }

    private ForestSamplingOptions ReadOptions()
    {
        bool usesIntensity = method.SelectedIndex != 3 || grtsSettings.UsesIntensity;
        var options = new ForestSamplingOptions
        {
        IdField = (string)idField.SelectedItem, ProjectField = projectField.SelectedItem is string field && field != NoProjectField ? field : "",
        HectaresPerPlot = usesIntensity ? Number(hectares) : 3, EdgeBufferMeters = Number(edge), MinimumDistanceMeters = method.SelectedIndex == 3 ? 0 : Number(distance),
        MinimumAreaHa = usesIntensity ? Number(areaMin) : 3, IntermediateAreaHa = usesIntensity ? Number(areaMid) : 6,
        PlotsBelowMinimum = usesIntensity ? Integer(countMin) : 1, PlotsBelowIntermediate = usesIntensity ? Integer(countMid) : 2, Seed = Integer(seed),
        FirstRoundUsesVertices = firstVertices.IsChecked == true,
        SequentialNumbering = sequentialNumbering.IsChecked == true, SequenceByProject = sequenceByProject.IsChecked == true && projectField.SelectedItem is string project && project != NoProjectField,
        SequenceWithinStandByCoordinates = sequenceXY.IsChecked == true,
        Method = method.SelectedIndex switch
        {
            1 => ForestSamplingMethod.RandomStartSystematic,
            2 => ForestSamplingMethod.SpatiallyBalanced,
            3 => ForestSamplingMethod.Grts,
            _ => ForestSamplingMethod.ExistingSystematicRounds
        }
        };
        return method.SelectedIndex == 3 ? grtsSettings.Apply(options) : options;
    }

    private static double Number(TextBox box) => double.Parse(box.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
    private static int Integer(TextBox box) => int.Parse(box.Text.Trim(), CultureInfo.InvariantCulture);

}
