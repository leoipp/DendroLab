using System.IO;
using System.Windows;
using System.Windows.Controls;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal sealed class PairedMeasurementsView : ScrollViewer
{
    private sealed record SourceChoice(string Name, string ProjectPath, TableSource Table);

    private readonly MainWindow owner;
    private readonly ProjectSession session;
    private readonly ComboBox source = Ui.Combo([]), date = Ui.Combo([]), aggregation = Ui.Combo([
        "Somar valores repetidos", "Calcular a média", "Usar o maior valor", "Usar o menor valor", "Exigir um único valor"
    ]);
    private readonly ListBox values = new() { SelectionMode = SelectionMode.Multiple, MinHeight = 100, MaxHeight = 190 };
    private readonly CheckBox trees = new() { Content = "Por árvore", IsChecked = true };
    private readonly CheckBox plots = new() { Content = "Por parcela", IsChecked = false };
    private readonly TextBox outputName = Ui.Input("Medicoes_pareadas");
    private readonly TextBlock status = Ui.Text("");
    private readonly TextBlock keySummary = Ui.Text("");
    private string[] treeKeyColumns = [], plotKeyColumns = [];
    private SourceChoice[] choices = [];
    private bool loading;

    internal PairedMeasurementsView(MainWindow owner, ProjectSession session)
    {
        this.owner = owner;
        this.session = session;
        Content = BuildForm();
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        source.SelectionChanged += async (_, _) =>
        {
            if (!loading) await owner.Execute(LoadColumns);
        };
    }

    private StackPanel BuildForm()
    {
        var form = Ui.Form();
        form.Children.Add(owner.ToolHeader("Parear medições"));
        Ui.Section(form, "Origem e período");
        Ui.Field(form, "Tabela com as medições", source);
        Ui.Field(form, "Coluna de ano ou data de medição", date);
        Ui.Field(form, "Campos que serão comparados ao longo do tempo (selecione vários)", values);
        Ui.Section(form, "Nível do pareamento");
        form.Children.Add(trees);
        form.Children.Add(plots);
        form.Children.Add(keySummary);
        Ui.AddHelp(form, "As chaves são usadas diretamente de Projeto > Configurações > Identificação; a data é retirada para encontrar o mesmo indivíduo ou parcela em anos diferentes. Em Ambos, são criadas tabelas separadas para árvores e parcelas.");
        Ui.Field(form, "Quando houver mais de um registro para a mesma chave e ano", aggregation);
        Ui.AddHelp(form, "Soma é adequada a volumes por árvore quando o resultado é por parcela. Para indicadores por hectare ou valores repetidos da parcela, escolha Média. Exigir um único valor evita agregar e interrompe a operação ao encontrar duplicatas.");
        Ui.Section(form, "Resultado");
        Ui.Field(form, "Nome base das tabelas", outputName);
        form.Children.Add(Ui.AsyncButton("Gerar dados pareados", () => owner.Execute(Run)));
        form.Children.Add(status);
        return form;
    }

    internal async Task Initialize(string? selectedPath = null)
    {
        loading = true;
        choices = await Task.Run(() => BuildChoices(selectedPath));
        source.ItemsSource = choices.Select(c => c.Name).ToArray();
        int selected = selectedPath == null ? -1 : Array.FindIndex(choices, c =>
            string.Equals(c.ProjectPath, selectedPath, StringComparison.OrdinalIgnoreCase));
        if (selected < 0)
        {
            string latest = session.Meta.Arr("processing_results").OrderByDescending(r => r.S("created")).FirstOrDefault().S("path");
            selected = Array.FindIndex(choices, c => string.Equals(c.ProjectPath, latest, StringComparison.OrdinalIgnoreCase));
        }
        source.SelectedIndex = selected >= 0 ? selected : choices.Length > 0 ? 0 : -1;
        loading = false;
        if (source.SelectedIndex >= 0) await LoadColumns();
        else status.Text = "Adicione uma tabela de medições ao projeto para começar.";
    }

    private SourceChoice[] BuildChoices(string? selectedPath)
    {
        var result = new List<SourceChoice>();
        foreach (string path in session.Files.Where(session.IsTabular))
        {
            string resolved = session.Resolve(path);
            string[] tables;
            try { tables = TableStore.Tables(resolved); }
            catch (IOException) { continue; }
            catch (InvalidDataException) { continue; }
            foreach (string table in tables)
            {
                var options = session.ExcelOptions(path);
                var source = new TableSource(resolved, table,
                    session.IsSpreadsheet(path) ? (int)options.N("header_row", 1) : 1);
                string display = session.ContentName(path) + (tables.Length > 1 ? " · " + table : "");
                result.Add(new(display, path, source));
            }
        }
        return result.ToArray();
    }

    private async Task LoadColumns()
    {
        if (source.SelectedIndex < 0 || source.SelectedIndex >= choices.Length) return;
        var selected = choices[source.SelectedIndex];
        string[] columns = await Task.Run(() =>
        {
            using var cursor = new TableCursor(selected.Table);
            return cursor.Columns;
        });
        var config = session.Data!;
        var inputMap = config.At("arquivo_entrada.colunas");
        string Resolve(string token) => inputMap?[token] is System.Text.Json.Nodes.JsonValue
            ? inputMap.S(token) : token;
        string mappedDate = config.At("arquivo_entrada.colunas").S("data_medicao", "Data_Medicao");
        string[] dateNames = [mappedDate, "Data_Medicao", "Data de medição", "Data", "Ano", "year"];
        string[] tree = config.At("identificacao.arvore") is System.Text.Json.Nodes.JsonArray treeConfig
            ? treeConfig.Select(n => Resolve(n!.GetValue<string>())).ToArray() : [];
        string[] plot = config.At("identificacao.parcela") is System.Text.Json.Nodes.JsonArray plotConfig
            ? plotConfig.Select(n => Resolve(n!.GetValue<string>())).ToArray() : [];
        string[] candidates = dateNames.Select(Resolve).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string selectedDate = candidates.FirstOrDefault(candidate => columns.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            ?? columns.FirstOrDefault(c => c.Contains("data", StringComparison.OrdinalIgnoreCase) || c.Contains("ano", StringComparison.OrdinalIgnoreCase))
            ?? "";
        loading = true;
        date.ItemsSource = columns;
        date.SelectedItem = selectedDate;
        values.ItemsSource = columns;
        values.SelectedItems.Clear();
        string[] preferredValues = ["Volume_m3_ha", "VTCC_ha", "Fustes_ha", "VMI_m3_fuste", "VMI", "Idade_anos"];
        foreach (string field in preferredValues.Where(field => columns.Contains(field, StringComparer.Ordinal)))
            values.SelectedItems.Add(field);
        if (values.SelectedItems.Count == 0)
        {
            string? volume = columns.FirstOrDefault(c => c.Contains("volume", StringComparison.OrdinalIgnoreCase));
            if (volume != null) values.SelectedItems.Add(volume);
        }
        var excludedDates = dateNames.Concat(candidates).ToHashSet(StringComparer.OrdinalIgnoreCase);
        treeKeyColumns = tree.Distinct(StringComparer.Ordinal)
            .Where(key => columns.Contains(key, StringComparer.Ordinal) && !excludedDates.Contains(key)).ToArray();
        plotKeyColumns = plot.Distinct(StringComparer.Ordinal)
            .Where(key => columns.Contains(key, StringComparer.Ordinal) && !excludedDates.Contains(key)).ToArray();
        if (treeKeyColumns.Length == 0) treeKeyColumns = plotKeyColumns;
        keySummary.Text = "Chaves de configuração · árvore: " + FormatKeys(treeKeyColumns)
            + " · parcela: " + FormatKeys(plotKeyColumns);
        loading = false;
        status.Text = columns.Length == 0 ? "A origem não tem colunas disponíveis." : "Chaves carregadas das configurações do projeto.";
    }

    private async Task Run()
    {
        if (source.SelectedIndex < 0 || source.SelectedIndex >= choices.Length)
            throw new InvalidOperationException("Selecione uma tabela com medições.");
        var selected = choices[source.SelectedIndex];
        if (date.SelectedItem is not string dateColumn)
            throw new InvalidOperationException("Selecione a coluna de ano/data.");
        string[] selectedValues = values.SelectedItems.Cast<string>().ToArray();
        if (selectedValues.Length == 0)
            throw new InvalidOperationException("Selecione um ou mais campos para comparar ao longo do tempo.");
        string[] levels = new[] { (trees, "arvore"), (plots, "parcela") }
            .Where(item => item.Item1.IsChecked == true).Select(item => item.Item2).ToArray();
        if (levels.Length == 0) throw new InvalidOperationException("Selecione árvore, parcela ou ambos.");
        if (levels.Contains("arvore") && treeKeyColumns.Length == 0)
            throw new InvalidOperationException("Selecione as chaves que identificam uma árvore.");
        if (levels.Contains("parcela") && plotKeyColumns.Length == 0)
            throw new InvalidOperationException("Selecione as chaves que identificam uma parcela.");
        string baseName = TableTools.SuggestedName(outputName.Text);
        string[] proposed = levels.Select(level => TableTools.SuggestedName(baseName + " · " + (level == "arvore" ? "Árvores" : "Parcelas"))).ToArray();
        string bank = session.Resolve(session.Meta.S("database_path"));
        var existing = await Task.Run(() => TableStore.Tables(bank));
        if (proposed.Any(name => existing.Contains(name, StringComparer.Ordinal)))
            throw new InvalidOperationException("Já existe uma tabela com esse nome no banco do projeto. Informe outro nome base.");
        var options = new PairedMeasurementOptions(
            dateColumn, selectedValues, treeKeyColumns, plotKeyColumns, levels,
            new[] { "sum", "mean", "max", "min", "unique" }[aggregation.SelectedIndex < 0 ? 0 : aggregation.SelectedIndex]
        );
        string folder = session.ProjectWorkFolder();
        var cancellation = owner.OperationCancellation;
        var progress = owner.StartTableOperation("Pareando medições por chave e ano…");
        PairedMeasurementsResult? result = null;
        try
        {
            result = await Task.Run(() => PairedMeasurements.Create(selected.Table, folder, baseName, options, cancellation, progress));
            await session.AddTableResults(result.Tables, selected.ProjectPath, selected.Table.Table);
        }
        finally
        {
            foreach (var table in result?.Tables ?? [])
                if (File.Exists(table.Path)) File.Delete(table.Path);
        }
        if (result == null) return;
        foreach (var table in result.Tables)
        {
            string reference = Path.Combine(session.Meta.S("database_path"), Path.GetFileName(table.Path));
            session.RenameContent(reference, table.Table);
            await owner.OpenTable(reference, table.Table);
        }
        status.Text = $"{result.ReadRows:N0} linhas lidas · {result.InvalidDates:N0} datas inválidas · {result.EmptyKeys:N0} chaves vazias · {result.DuplicateKeyYears:N0} chave/ano repetidos · N incluído por ano.";
        owner.Log($"Pareamento concluído: {string.Join("; ", result.Tables.Select(t => $"{t.Table}: {t.Rows:N0} linhas"))}. {selectedValues.Length:N0} campos comparados; N por ano incluído.");
    }

    private static string FormatKeys(string[] keys) => keys.Length == 0 ? "não configuradas na origem" : string.Join(", ", keys);
}
