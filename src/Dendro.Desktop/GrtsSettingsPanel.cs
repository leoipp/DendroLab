using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Dendro.Core.Sampling;

namespace Dendro.Desktop;

internal sealed class GrtsSettingsPanel : StackPanel
{
    private const string ByStand = "Cada talhão", All = "Área inteira (sem estratificação)";
    private readonly ComboBox stratum = new(), allocation = new(), probability = new(), auxiliary = new(), category = new();
    private readonly TextBox fixedSize = Ui.Input("10"), density = Ui.Input("10"), minimumDistance = Ui.Input("0"), attempts = Ui.Input("10"), reserves = Ui.Input("0");
    private readonly TextBox confidence = Ui.Input("95"), relativeError = Ui.Input("10"), cv = Ui.Input("30");
    private readonly DataGrid sizes = Grid(), categories = Grid();
    private Dictionary<string, object?>[] records = [];
    private string idField = "";
    private bool loading;
    internal event Action? SettingsChanged;
    internal bool UsesIntensity => allocation.SelectedIndex == 0;

    internal sealed class StratumRow
    {
        public string Stratum { get; set; } = "";
        public int Count { get; set; } = 10;
    }
    internal sealed class CategoryRow
    {
        public string Stratum { get; set; } = "";
        public string Category { get; set; } = "";
        public int Count { get; set; } = 1;
    }

    internal GrtsSettingsPanel()
    {
        Ui.Section(this, "Desenho GRTS");
        stratum.ItemsSource = new[] { ByStand, All }; stratum.SelectedIndex = 0;
        allocation.ItemsSource = new[] { "Intensidade em ha/parcela", "Quantidade fixa por estrato", "Quantidade informada para cada estrato", "Dimensionar por confiança, erro e CV" }; allocation.SelectedIndex = 0;
        probability.ItemsSource = new[] { "Igual por unidade de área", "Proporcional a campo numérico positivo", "Quantidade esperada por categoria" }; probability.SelectedIndex = 0;
        Ui.Field(this, "Estratificação", stratum);
        Ui.Field(this, "Como definir a quantidade de parcelas de base", allocation);
        Ui.Field(this, "Parcelas de base em cada estrato", fixedSize);
        sizes.Columns.Add(Column("Estrato", "Stratum", true)); sizes.Columns.Add(Column("Parcelas", "Count"));
        Ui.Field(this, "Quantidade por estrato", sizes);
        Ui.Field(this, "Confiança desejada (%)", confidence);
        Ui.Field(this, "Erro relativo desejado (%)", relativeError);
        Ui.Field(this, "CV estimado por inventário anterior ou piloto (%)", cv);
        Ui.AddHelp(this, "O dimensionamento usa n = teto((z × CV / erro)²), com mínimo de 2 parcelas por estrato. É uma aproximação normal de planejamento; o erro efetivo depende das medições.");
        Ui.Field(this, "Probabilidades de seleção", probability);
        Ui.Field(this, "Campo auxiliar positivo (intensidade por unidade de área)", auxiliary);
        Ui.AddHelp(this, "Cada candidato recebe o valor do polígono onde está. O campo auxiliar é um multiplicador por unidade de área; valores maiores aumentam a probabilidade de seleção.");
        Ui.Field(this, "Campo de categoria", category);
        categories.Columns.Add(Column("Estrato", "Stratum", true)); categories.Columns.Add(Column("Categoria", "Category", true)); categories.Columns.Add(Column("Esperadas", "Count"));
        Ui.Field(this, "Parcelas esperadas por categoria em cada estrato", categories);
        Ui.AddHelp(this, "Informe valores positivos para todas as categorias. A soma em cada estrato deve ser igual ao número de parcelas de base. São quantidades esperadas, não cotas garantidas.");
        Ui.Field(this, "Candidatos por parcela (densidade de aproximação)", density);
        Ui.Field(this, "Distância mínima desejada no estrato (m; 0 = livre)", minimumDistance);
        Ui.Field(this, "Máximo de tentativas para atingir a distância", attempts);
        Ui.Field(this, "Parcelas substitutas em cada estrato", reserves);
        Ui.AddHelp(this, "As substitutas são selecionadas por ordem hierárquica reversa e identificadas como Reserva, com ordem de uso. A distância é verificada no conjunto de base e reservas de cada estrato. Se não for alcançada, o aviso informa o valor obtido e mantém a quantidade solicitada. Com distância ou reservas, as probabilidades exportadas são nominais e PI_EXATA fica falso.");
        stratum.SelectionChanged += (_, _) => { if (!loading) RebuildTables(); };
        category.SelectionChanged += (_, _) => { if (!loading) RebuildTables(); };
        allocation.SelectionChanged += (_, _) => UpdateFields();
        probability.SelectionChanged += (_, _) => UpdateFields();
        System.Windows.Automation.AutomationProperties.SetAutomationId(stratum, "GrtsStratum");
        System.Windows.Automation.AutomationProperties.SetAutomationId(allocation, "GrtsAllocation");
        System.Windows.Automation.AutomationProperties.SetAutomationId(probability, "GrtsProbability");
        System.Windows.Automation.AutomationProperties.SetAutomationId(density, "GrtsDensity");
        UpdateFields();
    }

    internal void LoadFields(string[] fields, Dictionary<string, object?>[] values, string identifier)
    {
        loading = true;
        try
        {
            records = values; idField = identifier;
            stratum.ItemsSource = new[] { ByStand, All }.Concat(fields).ToArray(); stratum.SelectedIndex = 0;
            auxiliary.ItemsSource = fields; auxiliary.SelectedIndex = fields.Length > 0 ? 0 : -1;
            category.ItemsSource = fields; category.SelectedIndex = fields.Length > 0 ? 0 : -1;
        }
        finally { loading = false; }
        RebuildTables();
    }

    internal void SetIdentifier(string identifier) { idField = identifier; if (!loading) RebuildTables(); }
    private string StratumField => stratum.SelectedIndex switch { 0 => "", 1 => "*", _ => stratum.SelectedItem as string ?? "" };

    private void RebuildTables()
    {
        var oldSizes = (sizes.ItemsSource as IEnumerable<StratumRow> ?? []).ToDictionary(row => row.Stratum, row => row.Count);
        var oldCategories = (categories.ItemsSource as IEnumerable<CategoryRow> ?? []).ToDictionary(row => (row.Stratum, row.Category), row => row.Count);
        var keys = records.Select((record, index) =>
        {
            string id = record.TryGetValue(idField, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" : "";
            if (string.IsNullOrWhiteSpace(id)) id = $"T{index + 1:0000}";
            string key = StratumField switch { "" => id, "*" => "Todos", _ => Convert.ToString(record.GetValueOrDefault(StratumField), CultureInfo.InvariantCulture) ?? "" };
            string cat = Convert.ToString(record.GetValueOrDefault(category.SelectedItem as string ?? ""), CultureInfo.InvariantCulture) ?? "";
            return (Stratum: key, Category: cat);
        }).ToArray();
        sizes.ItemsSource = keys.Select(key => key.Stratum).Distinct(StringComparer.Ordinal).Select(key => new StratumRow { Stratum = key, Count = oldSizes.GetValueOrDefault(key, 10) }).ToArray();
        categories.ItemsSource = keys.Distinct().Select(key => new CategoryRow { Stratum = key.Stratum, Category = key.Category, Count = oldCategories.GetValueOrDefault(key, 1) }).ToArray();
        UpdateFields();
    }

    private static DataGrid Grid() => new() { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, MinHeight = 80, MaxHeight = 240, ColumnWidth = new DataGridLength(1, DataGridLengthUnitType.Star) };
    private static DataGridTextColumn Column(string title, string field, bool readOnly = false) => new()
    {
        Header = title, IsReadOnly = readOnly,
        Binding = new Binding(field) { Mode = readOnly ? BindingMode.OneWay : BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true }
    };

    private static void Show(FrameworkElement control, bool visible)
    {
        if (control.Parent is UIElement parent) parent.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }
    private void UpdateFields()
    {
        Show(fixedSize, allocation.SelectedIndex == 1); Show(sizes, allocation.SelectedIndex == 2);
        foreach (var box in new[] { confidence, relativeError, cv }) Show(box, allocation.SelectedIndex == 3);
        Show(auxiliary, probability.SelectedIndex == 1);
        Show(category, probability.SelectedIndex == 2); Show(categories, probability.SelectedIndex == 2);
        SettingsChanged?.Invoke();
    }

    internal ForestSamplingOptions Apply(ForestSamplingOptions options)
    {
        if (!sizes.CommitEdit(DataGridEditingUnit.Cell, true) || !sizes.CommitEdit(DataGridEditingUnit.Row, true) ||
            !categories.CommitEdit(DataGridEditingUnit.Cell, true) || !categories.CommitEdit(DataGridEditingUnit.Row, true))
            throw new InvalidOperationException("Corrija as quantidades nas tabelas de estratos e categorias.");
        int Integer(TextBox control) => int.Parse(control.Text, CultureInfo.InvariantCulture);
        double Number(TextBox control) => double.Parse(control.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
        return options with
        {
            GrtsStratumField = StratumField,
            GrtsSampleSizeMode = (GrtsSampleSizeMode)allocation.SelectedIndex,
            GrtsFixedSampleSize = allocation.SelectedIndex == 1 ? Integer(fixedSize) : 10,
            GrtsStratumSampleSizes = (sizes.ItemsSource as IEnumerable<StratumRow> ?? []).ToDictionary(row => row.Stratum, row => row.Count, StringComparer.Ordinal),
            GrtsSelectionType = (GrtsSelectionType)probability.SelectedIndex,
            GrtsAuxiliaryField = auxiliary.SelectedItem as string ?? "",
            GrtsCategoryField = category.SelectedItem as string ?? "",
            GrtsCategorySampleSizes = (categories.ItemsSource as IEnumerable<CategoryRow> ?? []).GroupBy(row => row.Stratum)
                .ToDictionary(group => group.Key, group => group.ToDictionary(row => row.Category, row => row.Count, StringComparer.Ordinal), StringComparer.Ordinal),
            GrtsPointDensity = Integer(density), GrtsMinimumDistance = Number(minimumDistance), GrtsMaxAttempts = Integer(attempts),
            GrtsReplacementCount = Integer(reserves),
            GrtsConfidencePercent = allocation.SelectedIndex == 3 ? Number(confidence) : 95,
            GrtsRelativeErrorPercent = allocation.SelectedIndex == 3 ? Number(relativeError) : 10,
            GrtsCvPercent = allocation.SelectedIndex == 3 ? Number(cv) : 30
        };
    }
}
