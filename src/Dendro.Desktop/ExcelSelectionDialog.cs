using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace Dendro.Desktop;

internal static class ExcelSelectionDialog
{
    public static async Task<bool> Configure(Window owner, ProjectSession project, string path)
    {
        var names = (
            await WorkspaceOperations.Run(
                new() { ["op"] = "sheets", ["path"] = project.Resolve(path) }
            )
        )["result"]!
            .AsArray()
            .Select(n => n!.GetValue<string>())
            .ToArray();
        var option = project.ExcelOptions(path);
        var sheets = Ui.Combo(names, option.S("sheet"));
        var header = Ui.Input(option.N("header_row", 1).ToString());
        var area = new ContentControl();
        var form = Ui.Form();
        Ui.Field(form, "Worksheet", sheets);
        Ui.Field(form, "Linha do cabeçalho (clique na prévia para selecionar)", header);
        var layout = new DockPanel();
        DockPanel.SetDock(form, Dock.Top);
        layout.Children.Add(form);
        layout.Children.Add(area);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var accept = Ui.Button("Usar esta worksheet", () => { });
        buttons.Children.Add(accept);
        var cancel = Ui.Button("Cancelar", () => { });
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Insert(0, buttons);
        var window = Ui.Dialog("Selecionar worksheet e cabeçalho", layout, 1000, 720);
        window.Owner = owner;
        bool accepted = false;
        int revision = 0;
        JsonNode? preview = null;
        async Task Load()
        {
            int current = ++revision;
            try
            {
                var data = (
                    await WorkspaceOperations.Run(
                        new()
                        {
                            ["op"] = "table",
                            ["path"] = project.Resolve(path),
                            ["sheet"] = sheets.SelectedItem?.ToString(),
                            ["header_row"] = 1,
                            ["limit"] = 200,
                        }
                    )
                )["result"]!;
                if (current != revision)
                    return;
                preview = data;
                var columns = new JsonArray();
                int count = data["columns"]!.AsArray().Count;
                for (int i = 0; i < count; i++)
                    columns.Add($"Coluna {i + 1}");
                var rows = new JsonArray { data["columns"]!.DeepClone() };
                foreach (var row in data["rows"]!.AsArray())
                    rows.Add(row!.DeepClone());
                var grid = Ui.Grid(new JsonObject { ["columns"] = columns, ["rows"] = rows });
                grid.SelectionChanged += (_, _) =>
                {
                    if (grid.SelectedIndex >= 0)
                        header.Text = (grid.SelectedIndex + 1).ToString();
                };
                area.Content = grid;
            }
            catch (Exception ex)
            {
                Ui.Error(ex);
            }
        }
        sheets.SelectionChanged += async (_, _) => await Load();
        window.Loaded += async (_, _) => await Load();
        cancel.Click += (_, _) => window.Close();
        accept.Click += (_, _) =>
        {
            try
            {
                int row = int.Parse(header.Text);
                if (row < 1 || row > 1048576)
                    throw new InvalidOperationException("Linha de cabeçalho inválida.");
                project.SetExcel(
                    path,
                    sheets.SelectedItem?.ToString()
                        ?? throw new InvalidOperationException("Selecione uma worksheet."),
                    row
                );
                accepted = true;
                window.Close();
            }
            catch (Exception ex)
            {
                Ui.Error(ex);
            }
        };
        window.ShowDialog();
        return accepted;
    }
}
