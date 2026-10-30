using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Dendro.Core.Processing;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal sealed class IdentitySettingsView : ScrollViewer
{
    internal sealed class FieldRow
    {
        public string Column { get; set; } = "";
        public bool Tree { get; set; }
        public bool Plot { get; set; }
        public bool Origin { get; set; }
        public string Token { get; init; } = "";
        public string Initial { get; init; } = "";
    }
    private readonly ObservableCollection<FieldRow> fields = new();
    private readonly DataGrid grid = new() { AutoGenerateColumns = false, CanUserAddRows = true, CanUserDeleteRows = true, IsReadOnly = false, MinHeight = 260, MaxHeight = 440 };
    private readonly ComboBox type = new() { IsEditable = true };
    public IdentitySettingsView(JsonObject project, ProjectSession session)
    {
        InventoryIdentity.Ensure(project);
        var settings = project.Obj("identificacao"); var map = project.At("arquivo_entrada.colunas");
        foreach (string field in settings.Arr("arvore").Concat(settings.Arr("parcela")).Concat(settings.Arr("colunas_origem")).Select(f => f.Display()).Distinct())
        {
            string column = map?[field] is JsonValue ? map.S(field) : field;
            fields.Add(new() { Column = column, Initial = column, Token = field,
                Tree = settings.Arr("arvore").Any(v => v.Display() == field), Plot = settings.Arr("parcela").Any(v => v.Display() == field), Origin = settings.Arr("colunas_origem").Any(v => v.Display() == field) });
        }
        grid.ItemsSource = fields;
        grid.Columns.Add(new DataGridTextColumn { Header = "Coluna da tabela", Binding = new Binding("Column"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        foreach (var (title, property) in new[] { ("Chave de árvore", "Tree"), ("Chave de parcela", "Plot"), ("Identifica a base", "Origin") })
            grid.Columns.Add(new DataGridCheckBoxColumn { Header = title, Binding = new Binding(property), Width = 150 });
        System.Windows.Automation.AutomationProperties.SetAutomationId(grid, "Identity.Fields");
        System.Windows.Automation.AutomationProperties.SetAutomationId(type, "Identity.InventoryTypeColumn");
        type.Text = settings.S("coluna_tipo_inventario");
        var form = Ui.Form(); form.Children.Add(Ui.Text("Identificação das árvores, parcelas e bases", 20));
        Ui.AddHelp(form, "Marque as colunas que compõem cada chave. As colunas de base e o tipo IFC/IFPC acompanham todas as chaves, evitando juntar inventários diferentes do mesmo talhão. Fonte é a origem padrão criada por Juntar tabelas; pode ser substituída por outras colunas da sua base.");
        form.Children.Add(grid);
        Ui.Field(form, "Coluna que indica IFC ou IFPC (vazio = automático)", type);
        Ui.AddHelp(form, "A coluna escolhida deve conter IFC ou IFPC em cada linha. No automático, são procuradas colunas de tipo conhecidas e, depois, IFC/IFPC no nome da Fonte. Quando isso não identifica o tipo, usa-se a escolha do Processador. No modo Misto, a origem precisa permitir distinguir o tipo de cada linha.");
        var paths = session.Files.Where(session.IsTabular).ToArray(); var source = Ui.Combo(paths.Select(p => session.ContentName(p)));
        Ui.Field(form, "Tabela para consultar colunas disponíveis", source);
        form.Children.Add(Ui.AsyncButton("Carregar colunas da tabela", async () =>
        {
            if (source.SelectedIndex < 0) return;
            string path = paths[source.SelectedIndex]; var options = session.ExcelOptions(path);
            string[] columns = await Task.Run(() => { using var cursor = new TableCursor(new(session.Resolve(path), options.S("sheet"), (int)options.N("header_row", 1))); return cursor.Columns; });
            foreach (string column in columns) if (!fields.Any(f => f.Column == column)) fields.Add(new() { Column = column });
            string selected = type.Text; type.ItemsSource = new[] { "" }.Concat(columns).ToArray(); type.Text = selected;
        }));
        Content = form; VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
    }
    public JsonObject Read()
    {
        grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true);
        JsonArray Selected(Func<FieldRow, bool> predicate) => new(fields.Where(predicate).Select(f =>
            (JsonNode?)JsonValue.Create(f.Token.Length > 0 && f.Column == f.Initial ? f.Token : f.Column.Trim())).ToArray());
        return new() { ["arvore"] = Selected(f => f.Tree), ["parcela"] = Selected(f => f.Plot), ["colunas_origem"] = Selected(f => f.Origin), ["coluna_tipo_inventario"] = type.Text.Trim() };
    }
}
