using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal static class BiologicalAssetFallbackEditor
{
    internal static async Task Show(MainWindow owner, ProjectSession session, string[] baseColumns, string[] outputFields,
        Action<BiologicalAssetFallback> save, BiologicalAssetFallback? existing = null)
    {
        var paths = session.Files.Where(session.IsTabular).ToArray();
        var file = Ui.Combo(paths.Select(sessionPath => session.ContentName(sessionPath)));
        var sheet = Ui.Combo([]);
        var baseKey = Ui.Combo(baseColumns, existing?.BaseKey ?? "");
        var key = Ui.Combo([]); var date = Ui.Combo([]); var volume = Ui.Combo([]); var curve = Ui.Combo([]);
        var inventory = Ui.Combo(["Todos", "IFC", "IFPC"]);
        inventory.SelectedIndex = existing?.InventoryType switch { "IFC" => 1, "IFPC" => 2, _ => 0 };
        var mappings = new Dictionary<string, ComboBox>();
        var form = Ui.Form();
        Ui.Field(form, "Arquivo / conjunto alternativo", file);
        Ui.Field(form, "Tabela ou aba", sheet);
        Ui.Field(form, "Coluna de vínculo na base receptora", baseKey);
        Ui.Field(form, "Coluna correspondente nesta fonte", key);
        Ui.Field(form, "Data da medição", date);
        Ui.Field(form, "Volume observado (m³/ha)", volume);
        Ui.Field(form, "ID da curva nesta fonte (opcional)", curve);
        Ui.AddHelp(form, "Sem ID nesta fonte, usa o ID da curva na base receptora. O volume e a data virão desta medição alternativa. Escolha qual coluna desta fonte corresponde a cada campo trazido da principal; campos sem correspondência ficam vazios.");
        Ui.Field(form, "Tipo de inventário", inventory);
        foreach (string field in outputFields)
        {
            var choice = Ui.Combo([]); mappings[field] = choice;
            Ui.Field(form, "Coluna correspondente a " + field, choice);
        }
        var apply = Ui.Button("Salvar fonte alternativa", () => { });
        var window = Ui.Dialog("Fonte alternativa do ativo biológico", Ui.WithToolbar(Ui.Scroll(form), apply), 700, 760);
        bool loading = false;
        async Task LoadColumns()
        {
            if (file.SelectedIndex < 0 || sheet.SelectedItem is not string table) return;
            string path = paths[file.SelectedIndex];
            var names = await Task.Run(() => { using var cursor = new TableCursor(new(session.Resolve(path), table,
                session.IsSpreadsheet(path) ? (int)session.ExcelOptions(path).N("header_row", 1) : 1)); return cursor.Columns; });
            void Set(ComboBox choice, string? selected, string[] defaults)
            {
                choice.ItemsSource = names;
                choice.SelectedItem = names.FirstOrDefault(name => name == selected) ?? names.FirstOrDefault(name => defaults.Contains(name));
            }
            Set(key, existing?.ProcessingKey, ["Talhão", "Chave"]);
            Set(date, existing?.MeasurementDate, ["Data_Medicao", "Data"]);
            Set(volume, existing?.VolumeColumn, ["Volume_m3_ha", "VTCC_ha", "VCCC_ha"]);
            curve.ItemsSource = new[] { "(Usar ID da base)" }.Concat(names).ToArray();
            curve.SelectedItem = existing?.CurveIdColumn.Length > 0 && names.Contains(existing.CurveIdColumn) ? existing.CurveIdColumn : "(Usar ID da base)";
            foreach (var pair in mappings)
            {
                pair.Value.ItemsSource = new[] { "(Não trazer)" }.Concat(names).ToArray();
                string selected = existing?.FieldMap.GetValueOrDefault(pair.Key) ?? pair.Key;
                pair.Value.SelectedItem = names.Contains(selected) ? selected : "(Não trazer)";
            }
        }
        async Task LoadTables()
        {
            if (file.SelectedIndex < 0) return;
            string path = paths[file.SelectedIndex];
            string[] names = await Task.Run(() => TableStore.Tables(session.Resolve(path)));
            loading = true;
            sheet.ItemsSource = names.Where(name => !name.StartsWith(Dendro.Core.Processing.ProcessingHistory.InternalPrefix, StringComparison.Ordinal)).ToArray();
            sheet.SelectedItem = names.Contains(existing?.Source.Table ?? "") ? existing!.Source.Table : names.FirstOrDefault();
            loading = false;
            await LoadColumns();
        }
        file.SelectionChanged += async (_, _) => { if (!loading) { try { await LoadTables(); } catch(Exception ex) { Ui.Error(ex); } } };
        sheet.SelectionChanged += async (_, _) => { if (!loading) { try { await LoadColumns(); } catch(Exception ex) { Ui.Error(ex); } } };
        apply.Click += (_, _) =>
        {
            try
            {
                if (file.SelectedIndex < 0 || sheet.SelectedItem is not string table || baseKey.SelectedItem is not string b
                    || key.SelectedItem is not string k || date.SelectedItem is not string d || volume.SelectedItem is not string v)
                    throw new InvalidOperationException("Selecione a tabela, as duas chaves, a data e o volume desta fonte.");
                string path = paths[file.SelectedIndex];
                var source = new TableSource(session.Resolve(path), table, session.IsSpreadsheet(path) ? (int)session.ExcelOptions(path).N("header_row", 1) : 1);
                var map = mappings.Where(pair => pair.Value.SelectedIndex > 0).ToDictionary(pair => pair.Key, pair => (string)pair.Value.SelectedItem);
                save(new(source, b, k, d, v, map, inventory.SelectedIndex switch { 1 => "IFC", 2 => "IFPC", _ => "" },
                    session.ContentName(path) + " / " + table, curve.SelectedIndex > 0 ? (string)curve.SelectedItem : ""));
                window.Close();
            }
            catch (Exception ex) { Ui.Error(ex); }
        };
        loading = true;
        file.SelectedIndex = existing == null ? -1 : Array.FindIndex(paths, path => session.Resolve(path) == existing.Source.Path);
        loading = false;
        if (file.SelectedIndex >= 0) await LoadTables();
        window.ShowDialog();
    }
}
