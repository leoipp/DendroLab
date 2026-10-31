using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Dendro.Core.Configuration;
using Dendro.Core.Processing;
using Dendro.Core.Spreadsheets;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal sealed class ProcessingReviewView : ScrollViewer
{
    private sealed class FactorRow
    {
        public string Code { get; init; } = "";
        public string Label { get; init; } = "";
        public string Factor { get; set; } = "1";
        public double Trees { get; init; }
        public double Volume { get; init; }
    }
    private readonly MainWindow owner;
    private readonly ProjectSession session;
    private readonly ComboBox versions = Ui.Combo([]), scope = Ui.Combo(["Talhão", "Lote"]);
    private readonly TextBlock status = Ui.Text("");
    private readonly TextBox note = Ui.Input();
    private readonly ObservableCollection<FactorRow> factors = [];
    private readonly DataGrid grid = new() { AutoGenerateColumns = false, CanUserAddRows = false, IsReadOnly = false, Height = 240 };
    private readonly StackPanel exports = new();
    private readonly Button removeRevision;
    private JsonObject[] records = [];
    private JsonObject? selected;
    private DataTable? template;
    private bool loading;

    public ProcessingReviewView(MainWindow owner, ProjectSession session)
    {
        this.owner = owner; this.session = session;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var panel = Ui.Form(); Content = panel;
        panel.Children.Add(owner.ToolHeader("Revisão de inventário"));
        Ui.AddHelp(panel, "Histórico do processamento completo. As versões são preservadas; qualquer usuário pode revisar e aprovar, com identificação do Windows.");
        Ui.Section(panel,"Selecionar versão");
        Ui.Field(panel, "Versão do processamento", versions);
        panel.Children.Add(Ui.AsyncButton("Atualizar histórico da rede", () => owner.Execute(() => Initialize(selected?.S("id")))));
        panel.Children.Add(status);
        removeRevision = Ui.AsyncButton("Remover revisão selecionada…", () => owner.Execute(RemoveSelected));
        panel.Children.Add(removeRevision);
        Ui.AddHelp(panel, "É possível remover versões ainda não aprovadas e sem revisões posteriores. Versões aprovadas e seus históricos ficam preservados.");
        var open = new WrapPanel();
        open.Children.Add(Ui.AsyncButton("Abrir árvores", () => owner.Execute(() => Open(ProcessingHistory.Trees))));
        open.Children.Add(Ui.AsyncButton("Abrir parcelas", () => owner.Execute(() => Open(ProcessingHistory.Plots)))); panel.Children.Add(open);
        Ui.Section(panel,"Revisar e corrigir fatores");
        Ui.Field(panel, "Nível dos fatores (trocar redefine a grade)", scope);
        grid.ItemsSource = factors;
        grid.Columns.Add(new DataGridTextColumn { Header = "Talhão / Lote · Base · Tipo", Binding = new Binding(nameof(FactorRow.Label)), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Fator (0 a 1)", Binding = new Binding(nameof(FactorRow.Factor)) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus } });
        grid.Columns.Add(new DataGridTextColumn { Header = "Árvores", Binding = new Binding(nameof(FactorRow.Trees)), IsReadOnly = true });
        grid.Columns.Add(new DataGridTextColumn { Header = "VTCC original", Binding = new Binding(nameof(FactorRow.Volume)) { StringFormat = "N3" }, IsReadOnly = true });
        panel.Children.Add(grid);
        var factorActions = new WrapPanel();
        factorActions.Children.Add(Ui.AsyncButton("Exportar tabela de fatores…", () => owner.Execute(ExportFactors)));
        factorActions.Children.Add(Ui.AsyncButton("Importar fatores…", () => owner.Execute(ImportFactors))); panel.Children.Add(factorActions);
        Ui.Field(panel, "Motivo da revisão / observações", note);
        panel.Children.Add(Ui.AsyncButton("Criar nova versão corrigida", () => owner.Execute(Correct)));
        Ui.AddHelp(panel, "A grade prepara uma NOVA versão. Aprovar a versão selecionada não aplica edições ainda não publicadas. O cálculo usa sempre o VTCC original, sem repetir o ajuste de altura.");
        Ui.Section(panel,"Status e exportação");
        var approvals = new WrapPanel();
        approvals.Children.Add(Ui.AsyncButton("Marcar em análise", () => owner.Execute(() => ChangeStatus("em_analise"))));
        approvals.Children.Add(Ui.AsyncButton("Aprovar versão selecionada", () => owner.Execute(() => ChangeStatus("aprovado")))); panel.Children.Add(approvals);
        Ui.Field(panel, "Abas para o Excel (somente versão aprovada vigente)", exports);
        panel.Children.Add(Ui.AsyncButton("Exportar Excel aprovado…", () => owner.Execute(ExportApproved)));
        versions.SelectionChanged += async (_, _) => { if (!loading && versions.SelectedIndex >= 0) await owner.Execute(LoadSelected); };
        scope.SelectionChanged += async (_, _) => { if (!loading && selected != null) await owner.Execute(LoadFactors); };
    }

    internal async Task Initialize(string? id = null)
    {
        await session.RefreshHistory(true);
        loading = true;
        try
        {
            records = session.Meta.Arr("processing_results").OfType<JsonObject>().Where(r => r.S("id").Length > 0)
                .OrderByDescending(r => r.S("created"), StringComparer.Ordinal).ToArray();
            versions.ItemsSource = records.Select(r => $"{r.S("inventory_type")} · V{r.N("number")} · {Status(r)} · {session.ContentName(r.S("source_path"))} · {r.S("created")}").ToArray();
            versions.SelectedIndex = id == null ? (records.Length > 0 ? 0 : -1) : Array.FindIndex(records, r => r.S("id") == id);
        }
        finally { loading = false; }
        if (versions.SelectedIndex >= 0) await LoadSelected();
        else { status.Text = "Nenhum processamento completo versionado. Crie o original no Processador."; removeRevision.IsEnabled = false; }
    }

    internal static string Status(JsonObject run) => run.S("status") switch { "aprovado" => run.B("current_approved") ? "Aprovado vigente" : "Aprovado histórico", "em_analise" => "Em análise", _ => "Processado" };

    private async Task LoadSelected()
    {
        selected = records[versions.SelectedIndex].DeepClone().AsObject();
        removeRevision.IsEnabled = selected.S("status") != "aprovado" && !selected.B("current_approved")
            && !records.Any(r => r.S("parent_id") == selected.S("id"));
        status.Text = $"{selected.S("inventory_type")} · V{selected.N("number")} · {Status(selected)}\nProcessado por {selected.S("user")} em {selected.S("created")}\n"
            + (selected.S("approved_by").Length > 0 ? $"Aprovado por {selected.S("approved_by")} em {selected.S("approved_at")}" : "Aguardando revisão/aprovação.");
        loading = true; scope.SelectedIndex = selected.S("factor_scope") == "lote" ? 1 : 0; loading = false;
        exports.Children.Clear();
        var preferred = selected.Arr("export_sheets").Select(s => s.Display()).ToHashSet(StringComparer.Ordinal);
        foreach (string sheet in selected.Arr("sheets").Select(s => s.Display()))
            exports.Children.Add(new CheckBox { Content = sheet, IsChecked = sheet == "PROCESSAMENTO" || preferred.Count == 0 || preferred.Contains(sheet),
                IsEnabled = sheet != "PROCESSAMENTO", ToolTip = sheet == "PROCESSAMENTO" ? "Identificação e aprovação acompanham obrigatoriamente o relatório." : null });
        await LoadFactors();
    }

    private JsonObject Original() => session.Meta.Arr("processing_results").OfType<JsonObject>().Single(r => r.S("id") == selected!.S("family_id"));

    private async Task LoadFactors()
    {
        if (selected == null) return;
        string level = scope.SelectedIndex == 1 ? "lote" : "talhao";
        var original = Original().DeepClone().AsObject();
        template = await Task.Run(() => ProcessingHistory.FactorTemplate(session.Resolve(original.S("path")), original, level));
        var prior = selected.S("factor_scope") == level ? selected.Arr("factors").OfType<JsonObject>().ToDictionary(f => f.S("code"), f => f.N("factor"), StringComparer.Ordinal) : new Dictionary<string, double>();
        factors.Clear();
        for (int r = 1; r < template.Count; r++) factors.Add(new() { Code = ProcessingHistory.FactorKey(template, r),
            Label = string.Join(" · ", new[] { template.Text(r, 2), template.Text(r, template.Column("Base_Origem")), template.Text(r, template.Column("Tipo_Inventario")) }.Where(s => s.Length > 0)),
            Factor = prior.GetValueOrDefault(ProcessingHistory.FactorKey(template, r), 1).ToString(System.Globalization.CultureInfo.CurrentCulture), Trees = template.Number(r, 4), Volume = template.Number(r, 5) });
    }

    private DataTable FactorTable()
    {
        if (template == null || selected == null) throw new InvalidOperationException("Selecione uma versão.");
        grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true);
        var data = template.Copy();
        for (int row = 0; row < factors.Count; row++) data.Set(row + 1, 3, Ui.Number(factors[row].Factor));
        ProcessingHistory.ValidateFactors(data, template); return data;
    }

    private async Task ExportFactors()
    {
        var data = FactorTable(); string? path = Ui.SaveFile("Tabela de fatores|*.xlsx", "Fatores.xlsx"); if (path == null) return;
        await Task.Run(() => ProjectStore.AtomicWrite(path, temp => WorkbookWriter.Write(temp, [new("Fatores", data)]), true));
    }
    private async Task ImportFactors()
    {
        if (template == null) throw new InvalidOperationException("Selecione uma versão.");
        string? path = Ui.PickFile("Tabela de fatores|*.xlsx;*.xls;*.dlb"); if (path == null) return;
        var data = await Task.Run(() => TableStore.Read(new(path)));
        var values = ProcessingHistory.ValidateFactors(data, template);
        foreach (var row in factors) row.Factor = values[row.Code].ToString(System.Globalization.CultureInfo.CurrentCulture);
        grid.Items.Refresh();
    }

    private async Task Correct()
    {
        if (selected == null) throw new InvalidOperationException("Selecione uma versão.");
        var data = FactorTable(); await session.RefreshHistory(true);
        var original = Original().DeepClone().AsObject();
        int number = session.Meta.Arr("processing_results").Where(r => r.S("family_id") == original.S("family_id")).Max(r => (int)r.N("number")) + 1;
        var revision = ProcessingCatalog.NewRecord(original.S("source_path"), original.S("inventory_type"), scope.SelectedIndex == 1 ? "lote" : "talhao", selected, number);
        revision["note"] = note.Text;
        revision["factors"] = new JsonArray(ProcessingHistory.ValidateFactors(data, template!).Select(p => (JsonNode?)new JsonObject { ["code"] = p.Key, ["factor"] = p.Value }).ToArray());
        string stage = Path.Combine(session.ProjectWorkFolder(), "revision_" + revision.S("id") + ".dlb");
        var cancellation = owner.OperationCancellation;
        var progress = owner.StartTableOperation("Aplicando fatores ao original e atualizando resumos…");
        try
        {
            var result = await Task.Run(() => ProcessingHistory.Correct(session.Resolve(original.S("path")), original, revision, data, stage, cancellation,
                (percent, message) => progress.Report(new TableProgress(percent, message))));
            cancellation.ThrowIfCancellationRequested();
            revision["sheets"] = new JsonArray((await Task.Run(() => TableStore.Tables(stage))).Where(s => !s.StartsWith(ProcessingHistory.InternalPrefix, StringComparison.Ordinal)).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
            revision["export_sheets"] = selected["export_sheets"]?.DeepClone() ?? revision["sheets"]!.DeepClone();
            revision["totals"] = new JsonObject { ["trees"] = result.Trees, ["plots"] = result.Plots, ["stands"] = result.Stands, ["volume"] = result.Volume };
            await session.PublishProcessing(stage, revision, cancellation, (copied, total) =>
                progress.Report(new TableProgress(98, $"Publicando banco: {copied / 1048576.0:N1} de {total / 1048576.0:N1} MB…")));
            await Initialize(revision.S("id"));
            owner.Log($"V{number} corrigida publicada. O original e as versões anteriores foram preservados.");
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }

    private Task Open(string sheet)
    {
        if (selected == null) throw new InvalidOperationException("Selecione uma versão.");
        return owner.OpenTable(selected.S("path"), sheet);
    }
    private async Task ChangeStatus(string state)
    {
        if (selected == null) throw new InvalidOperationException("Selecione uma versão.");
        string id = selected.S("id");
        await session.ChangeProcessingStatus(selected, state, note.Text, owner.OperationCancellation);
        await Initialize(id); owner.Log("Estado atualizado por " + ProcessingCatalog.User);
    }

    private async Task RemoveSelected()
    {
        if (selected == null) throw new InvalidOperationException("Selecione uma revisão.");
        if (selected.S("status") == "aprovado" || selected.B("current_approved"))
            throw new InvalidOperationException("Uma versão aprovada é preservada e não pode ser removida.");
        if (records.Any(r => r.S("parent_id") == selected.S("id")))
            throw new InvalidOperationException("Esta versão tem revisões posteriores. Remova primeiro as revisões descendentes.");
        if (!Ui.Confirm("Remover revisão de inventário",
            $"Remover {selected.S("inventory_type")} V{selected.N("number")} e apagar os dados desta revisão do arquivo DLB do projeto? Esta ação não pode ser desfeita.",
            "Remover revisão", destructive: true)) return;
        string id = selected.S("id");
        await session.RemoveProcessingRevision(selected, owner.OperationCancellation);
        await Initialize();
        owner.Log("Revisão removida: " + id);
    }

    private async Task ExportApproved()
    {
        if (selected == null) throw new InvalidOperationException("Selecione uma versão.");
        string id = selected.S("id"); await session.RefreshHistory(true);
        var run = session.Meta.Arr("processing_results").OfType<JsonObject>().Single(r => r.S("id") == id);
        session.RequireApprovedExport(run.S("path"));
        string[] sheets = exports.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => c.Content.ToString()!).ToArray();
        if (sheets.Length == 0) throw new InvalidOperationException("Selecione pelo menos uma aba para exportar.");
        string? path = Ui.SaveFile("Relatório Excel aprovado|*.xlsx", $"{run.S("inventory_type")}_V{run.N("number")}_aprovado.xlsx"); if (path == null) return;
        var snapshot = run.DeepClone().AsObject();
        owner.StartTableOperation("Exportando a versão aprovada…");
        var seed = session.Meta.Arr("processing_results").DeepClone().AsArray();
        var cancellation = owner.OperationCancellation;
        await Task.Run(() => ProcessingHistory.ExportApproved(session.FilePath, seed, id, sheets, path, cancellation));
        owner.Log($"Excel exportado: {snapshot.S("inventory_type")} V{snapshot.N("number")} aprovada por {snapshot.S("approved_by")}.");
    }
}
