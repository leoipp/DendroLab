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
        public string OperationalVolume { get; set; } = "0";
        public double Trees { get; init; }
        public double Volume { get; init; }
    }
    private readonly MainWindow owner;
    private readonly ProjectSession session;
    private readonly ComboBox versions = Ui.Combo([]), scope = Ui.Combo(["Talhão", "Lote"]);
    private readonly ComboBox factorMethod = Ui.Combo(["Manual", "Calculado pelo volume operacional"]);
    private readonly TextBlock status = Ui.Text("");
    private readonly TextBlock factorMethodHelp = Ui.Text("", 12);
    private readonly TextBox note = Ui.Input();
    private readonly ObservableCollection<FactorRow> factors = [];
    private readonly DataGrid grid = new() { AutoGenerateColumns = false, CanUserAddRows = false, IsReadOnly = false, Height = 240 };
    private readonly StackPanel exports = new();
    private readonly Button removeRevision;
    private readonly Button startReview, requestChanges, approveVersion, createRevision, exportApproved;
    private DataGridTextColumn? factorColumn, operationalVolumeColumn;
    private JsonObject[] records = [];
    private JsonObject? selected;
    private DataTable? template;
    private bool loading;
    private readonly TextBlock versionHeading = Ui.Text("Selecione uma versão", 17);
    private readonly TextBlock versionOrigin = Ui.Text("");
    private readonly TextBlock pendingSummary = Ui.Text("");
    private readonly Dictionary<string, string> loadedFactors = new();

    private string SelectedFactorMethod => factorMethod.SelectedIndex == 1 ? "calculado" : "manual";
    private static string Snapshot(FactorRow row) => row.Factor + "\u001f" + row.OperationalVolume;
    private bool IsLatestVersion => selected != null && selected.N("number") == records
        .Where(r => r.S("family_id") == selected.S("family_id")).Select(r => r.N("number")).DefaultIfEmpty(0).Max();
    private bool CanEditFactors => selected != null && IsLatestVersion
        && (selected.S("status") is "em_revisao" or "ajustes_solicitados" or "em_analise");
    private bool FactorsChanged => factors.Any(row => !loadedFactors.TryGetValue(row.Code, out var prior) || Snapshot(row) != prior)
        || selected != null && SelectedFactorMethod != selected.S("factor_method", "manual");

    private int PendingFactorCount => factors.Count(row => !loadedFactors.TryGetValue(row.Code, out var prior) || Snapshot(row) != prior);

    private void RefreshPendingSummary()
    {
        int count = PendingFactorCount;
        int aboveOne = SelectedFactorMethod == "calculado" ? factors.Count(row =>
            double.TryParse(row.Factor, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var value) && value > 1) : 0;
        pendingSummary.Text = (SelectedFactorMethod == "calculado"
            ? "Calculado = volume operacional observado ÷ VTCC inventariado."
            : "Manual: informe a fração de aproveitamento entre 0 e 1.")
            + (count == 0 ? " Fatores publicados nesta versão."
                : $" {count} grupo(s) alterado(s), ainda não publicado(s); publique uma nova versão para aplicar.")
            + (aboveOne > 0 ? $" Atenção: {aboveOne} fator(es) acima de 1; confira os volumes informados." : "");
        UpdateActions();
    }

    private void UpdateActions()
    {
        if (selected == null)
        {
            startReview.IsEnabled = requestChanges.IsEnabled = approveVersion.IsEnabled = createRevision.IsEnabled = false;
            removeRevision.IsEnabled = exportApproved.IsEnabled = scope.IsEnabled = factorMethod.IsEnabled = false;
            grid.IsReadOnly = true;
            return;
        }
        string state = selected.S("status");
        bool latest = IsLatestVersion;
        bool reviewing = state is "em_revisao" or "em_analise";
        startReview.IsEnabled = latest && (state is "aguardando_revisao" or "processado");
        requestChanges.IsEnabled = latest && reviewing;
        approveVersion.IsEnabled = latest && reviewing && !FactorsChanged;
        createRevision.IsEnabled = CanEditFactors && (PendingFactorCount > 0 || FactorsChanged);
        removeRevision.IsEnabled = latest && state != "aprovado" && !selected.B("current_approved")
            && !records.Any(r => r.S("parent_id") == selected.S("id"));
        exportApproved.IsEnabled = state == "aprovado" && selected.B("current_approved");
        scope.IsEnabled = CanEditFactors;
        factorMethod.IsEnabled = CanEditFactors;
        grid.IsReadOnly = !CanEditFactors;
        if (factorColumn != null)
        {
            factorColumn.IsReadOnly = SelectedFactorMethod == "calculado" || !CanEditFactors;
            factorColumn.Header = SelectedFactorMethod == "calculado" ? "Fator calculado" : "Fator manual (0 a 1)";
        }
        if (operationalVolumeColumn != null)
        {
            operationalVolumeColumn.IsReadOnly = SelectedFactorMethod != "calculado" || !CanEditFactors;
            operationalVolumeColumn.Visibility = SelectedFactorMethod == "calculado" ? Visibility.Visible : Visibility.Collapsed;
        }
        factorMethodHelp.Text = SelectedFactorMethod == "calculado"
            ? "Informe o volume operacional observado (m³) por talhão/lote. O fator é observado ÷ VTCC inventariado. Fatores acima de 1 são aceitos e destacados para conferência."
            : "Informe a fração manual de aproveitamento, de 0 a 1. O fator multiplica os volumes estimados e não altera DAP nem altura.";
    }

    private void ApplyFactorMethod()
    {
        if (SelectedFactorMethod == "calculado") RefreshCalculatedFactors();
        UpdateActions();
    }

    private void RefreshCalculatedFactors()
    {
        if (SelectedFactorMethod != "calculado") return;
        foreach (var row in factors)
        {
            double observed = Ui.Number(row.OperationalVolume);
            if (!double.IsFinite(observed) || observed < 0) { row.Factor = ""; continue; }
            double factor = row.Volume == 0 ? (observed == 0 ? 1 : double.NaN) : observed / row.Volume;
            row.Factor = double.IsFinite(factor) ? factor.ToString(System.Globalization.CultureInfo.CurrentCulture) : "";
        }
        grid.Items.Refresh();
    }

    public ProcessingReviewView(MainWindow owner, ProjectSession session)
    {
        this.owner = owner; this.session = session;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var panel = Ui.Form(); Content = panel;
        panel.Children.Add(owner.ToolHeader("Revisão de inventário"));
        Ui.AddHelp(panel, "Fluxo do processamento: aguarde revisão, entre em revisão, aprove ou solicite ajustes. Cada versão corrigida retorna à fila de revisão.");
        Ui.Section(panel,"Selecionar versão");
        Ui.Field(panel, "Versão do processamento", versions);
        panel.Children.Add(Ui.AsyncButton("Atualizar histórico da rede", () => owner.Execute(() => Initialize(selected?.S("id")))));
        versionHeading.FontWeight = FontWeights.SemiBold;
        panel.Children.Add(versionHeading);
        panel.Children.Add(versionOrigin);
        status.FontSize = 12;
        panel.Children.Add(status);
        removeRevision = Ui.AsyncButton("Remover revisão selecionada…", () => owner.Execute(RemoveSelected));
        panel.Children.Add(removeRevision);
        Ui.AddHelp(panel, "É possível remover versões ainda não aprovadas e sem revisões posteriores. Versões aprovadas e seus históricos ficam preservados.");
        var open = new WrapPanel();
        open.Children.Add(Ui.AsyncButton("Abrir árvores", () => owner.Execute(() => Open(ProcessingHistory.Trees))));
        open.Children.Add(Ui.AsyncButton("Abrir parcelas", () => owner.Execute(() => Open(ProcessingHistory.Plots)))); panel.Children.Add(open);
        Ui.Section(panel,"Revisar e corrigir fatores");
        panel.Children.Add(Ui.Text("O fator representa aproveitamento operacional do volume. DAP e altura permanecem como medidos.", 12));
        Ui.Field(panel, "Nível dos fatores (trocar redefine a grade)", scope);
        Ui.Field(panel, "Método do fator de aproveitamento", factorMethod);
        factorMethodHelp.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(factorMethodHelp);
        grid.ItemsSource = factors;
        grid.Columns.Add(new DataGridTextColumn { Header = "Talhão / Lote · Base · Tipo", Binding = new Binding(nameof(FactorRow.Label)), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        factorColumn = new DataGridTextColumn { Header = "Fator manual (0 a 1)", Binding = new Binding(nameof(FactorRow.Factor)) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus } };
        grid.Columns.Add(factorColumn);
        operationalVolumeColumn = new DataGridTextColumn { Header = "Volume operacional observado (m³)", Binding = new Binding(nameof(FactorRow.OperationalVolume)) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }, Visibility = Visibility.Collapsed };
        grid.Columns.Add(operationalVolumeColumn);
        grid.Columns.Add(new DataGridTextColumn { Header = "Árvores", Binding = new Binding(nameof(FactorRow.Trees)), IsReadOnly = true });
        grid.Columns.Add(new DataGridTextColumn { Header = "VTCC inventariado (m³)", Binding = new Binding(nameof(FactorRow.Volume)) { StringFormat = "N3" }, IsReadOnly = true });
        panel.Children.Add(grid);
        panel.Children.Add(pendingSummary);
        grid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(new Action(() => { RefreshCalculatedFactors(); RefreshPendingSummary(); }));
        var factorActions = new WrapPanel();
        factorActions.Children.Add(Ui.AsyncButton("Exportar tabela de fatores…", () => owner.Execute(ExportFactors)));
        factorActions.Children.Add(Ui.AsyncButton("Importar fatores…", () => owner.Execute(ImportFactors))); panel.Children.Add(factorActions);
        Ui.Field(panel, "Justificativa / observação", note);
        createRevision = Ui.AsyncButton("Criar versão corrigida e reenviar", () => owner.Execute(Correct), Ui.ActionEmphasis.Primary);
        panel.Children.Add(createRevision);
        Ui.AddHelp(panel, "A nova versão é calculada a partir da V1 e entra automaticamente em Aguardando revisão. Os volumes inventariados e as medições originais são preservados; os volumes operacionais ficam identificados separadamente.");
        Ui.Section(panel,"Status e exportação");
        var approvals = new WrapPanel();
        startReview = Ui.AsyncButton("Iniciar revisão", () => owner.Execute(() => ChangeStatus("em_revisao")));
        requestChanges = Ui.AsyncButton("Solicitar ajustes", () => owner.Execute(() => ChangeStatus("ajustes_solicitados")));
        approveVersion = Ui.AsyncButton("Aprovar versão", () => owner.Execute(() => ChangeStatus("aprovado")), Ui.ActionEmphasis.Secondary);
        approvals.Children.Add(startReview); approvals.Children.Add(requestChanges); approvals.Children.Add(approveVersion); panel.Children.Add(approvals);
        Ui.Field(panel, "Abas para o Excel (somente versão aprovada vigente)", exports);
        exportApproved = Ui.AsyncButton("Exportar versão aprovada…", () => owner.Execute(ExportApproved));
        panel.Children.Add(exportApproved);
        versions.SelectionChanged += async (_, _) => { if (!loading && versions.SelectedIndex >= 0) await owner.Execute(LoadSelected); };
        scope.SelectionChanged += async (_, _) => { if (!loading && selected != null) await owner.Execute(LoadFactors); };
        factorMethod.SelectionChanged += (_, _) => { if (!loading && selected != null) { ApplyFactorMethod(); RefreshPendingSummary(); } };
    }

    internal async Task Initialize(string? id = null)
    {
        await session.RefreshHistory(true);
        loading = true;
        try
        {
            records = session.Meta.Arr("processing_results").OfType<JsonObject>().Where(r => r.S("id").Length > 0)
                .OrderByDescending(r => r.S("created"), StringComparer.Ordinal).ToArray();
            versions.ItemsSource = records.Select(r => $"{session.ContentName(r.S("source_path"))} · {r.S("inventory_type")} · V{r.N("number")} · {Status(r)} · {r.S("created")}").ToArray();
            versions.SelectedIndex = id == null ? (records.Length > 0 ? 0 : -1) : Array.FindIndex(records, r => r.S("id") == id);
        }
        finally { loading = false; }
        if (versions.SelectedIndex >= 0) await LoadSelected();
        else { selected = null; versionHeading.Text = "Nenhuma versão disponível"; versionOrigin.Text = ""; status.Text = "Crie o processamento original no Processador."; factors.Clear(); loadedFactors.Clear(); pendingSummary.Text = ""; }
        UpdateActions();
    }

    internal static string Status(JsonObject run) => run.S("status") switch
    {
        "aprovado" => run.B("current_approved") ? "Aprovado vigente" : "Substituída",
        "em_revisao" or "em_analise" => "Em revisão",
        "ajustes_solicitados" => "Ajustes solicitados",
        "aguardando_revisao" or "processado" => "Aguardando revisão",
        _ => run.S("status")
    };

    private async Task LoadSelected()
    {
        selected = records[versions.SelectedIndex].DeepClone().AsObject();
        versionHeading.Text = $"{selected.S("inventory_type")} · V{selected.N("number")} · {Status(selected)}";
        versionOrigin.Text = session.ContentName(selected.S("source_path"));
        string lastAuditNote = selected.Arr("audit").OfType<JsonObject>().LastOrDefault(e => !string.IsNullOrWhiteSpace(e.S("note")))?.S("note") ?? "";
        status.Text = $"Processado por {selected.S("user")} em {selected.S("created")}\n"
            + (selected.S("approved_by").Length > 0 ? $"Aprovado por {selected.S("approved_by")} em {selected.S("approved_at")}\n" : "")
            + (selected.S("note").Length > 0 ? $"Justificativa: {selected.S("note")}\n" : lastAuditNote.Length > 0 ? $"Última observação: {lastAuditNote}\n" : "")
            + $"Cálculo das revisões parte da V1 original. Método do fator: {(selected.S("factor_method", "manual") == "calculado" ? "calculado" : "manual")}.";
        loading = true;
        scope.SelectedIndex = selected.S("factor_scope") == "lote" ? 1 : 0;
        factorMethod.SelectedIndex = selected.S("factor_method", "manual") == "calculado" ? 1 : 0;
        loading = false;
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
        var prior = selected.S("factor_scope") == level ? selected.Arr("factors").OfType<JsonObject>().ToDictionary(f => f.S("code"), StringComparer.Ordinal) : new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        factors.Clear();
        for (int r = 1; r < template.Count; r++)
        {
            string code = ProcessingHistory.FactorKey(template, r);
            double volume = template.Number(r, template.Require("VTCC_Original"));
            var saved = prior.GetValueOrDefault(code);
            double factor = saved?.N("factor") ?? 1;
            string operational = saved != null && saved["operational_volume"] != null
                ? saved.N("operational_volume").ToString(System.Globalization.CultureInfo.CurrentCulture) : "";
            factors.Add(new() { Code = code,
                Label = string.Join(" · ", new[] { template.Text(r, 2), template.Text(r, template.Column("Base_Origem")), template.Text(r, template.Column("Tipo_Inventario")) }.Where(s => s.Length > 0)),
                Factor = factor.ToString(System.Globalization.CultureInfo.CurrentCulture),
                OperationalVolume = operational,
                Trees = template.Number(r, template.Require("Arvores")), Volume = volume });
        }
        ApplyFactorMethod();
        CaptureLoadedFactors();
    }

    private void CaptureLoadedFactors()
    {
        loadedFactors.Clear();
        foreach (var row in factors) loadedFactors[row.Code] = Snapshot(row);
        RefreshPendingSummary();
    }

    private DataTable FactorTable(bool validate = true)
    {
        if (template == null || selected == null) throw new InvalidOperationException("Selecione uma versão.");
        grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true);
        var data = template.Copy();
        int factorIndex = data.Require("Fator"), observedIndex = data.Require("Volume_Operacional_Observado");
        for (int row = 0; row < factors.Count; row++)
        {
            data.Set(row + 1, factorIndex, Ui.Number(factors[row].Factor));
            data.Set(row + 1, observedIndex, SelectedFactorMethod == "calculado" ? Ui.Number(factors[row].OperationalVolume) : 0d);
        }
        if (validate) ProcessingHistory.ValidateFactors(data, template, SelectedFactorMethod);
        return data;
    }

    private async Task ExportFactors()
    {
        var data = FactorTable(validate: false); string? path = Ui.SaveFile("Tabela de fatores|*.xlsx", "Fatores.xlsx"); if (path == null) return;
        await Task.Run(() => ProjectStore.AtomicWrite(path, temp => WorkbookWriter.Write(temp, [new("Fatores", data)]), true));
    }
    private async Task ImportFactors()
    {
        if (template == null) throw new InvalidOperationException("Selecione uma versão.");
        if (!CanEditFactors) throw new InvalidOperationException("Inicie a revisão antes de importar fatores.");
        string? path = Ui.PickFile("Tabela de fatores|*.xlsx;*.xls;*.dlb"); if (path == null) return;
        var data = await Task.Run(() => TableStore.Read(new(path)));
        var values = ProcessingHistory.ValidateFactors(data, template, SelectedFactorMethod);
        int observedColumn = data.Column("Volume_Operacional_Observado");
        foreach (var row in factors)
        {
            int sourceRow = Enumerable.Range(1, data.Count - 1).Single(r => ProcessingHistory.FactorKey(data, r) == row.Code);
            row.Factor = values[row.Code].ToString(System.Globalization.CultureInfo.CurrentCulture);
            if (observedColumn >= 0) row.OperationalVolume = data.Number(sourceRow, observedColumn).ToString(System.Globalization.CultureInfo.CurrentCulture);
        }
        grid.Items.Refresh();
        RefreshPendingSummary();
    }

    private async Task Correct()
    {
        if (selected == null) throw new InvalidOperationException("Selecione uma versão.");
        if (!CanEditFactors) throw new InvalidOperationException("Inicie a revisão ou use uma versão devolvida para editar os fatores.");
        if (string.IsNullOrWhiteSpace(note.Text)) throw new InvalidOperationException("Informe a justificativa do ajuste operacional.");
        var data = FactorTable(); await session.RefreshHistory(true);
        if (!IsLatestVersion) throw new InvalidOperationException("Atualize o histórico e selecione a versão mais recente antes de criar uma revisão.");
        var original = Original().DeepClone().AsObject();
        int number = session.Meta.Arr("processing_results").Where(r => r.S("family_id") == original.S("family_id")).Max(r => (int)r.N("number")) + 1;
        var revision = ProcessingCatalog.NewRecord(original.S("source_path"), original.S("inventory_type"), scope.SelectedIndex == 1 ? "lote" : "talhao", selected, number);
        revision["factor_method"] = SelectedFactorMethod;
        revision["note"] = note.Text;
        var factorValues = ProcessingHistory.ValidateFactors(data, template!, SelectedFactorMethod);
        revision["factors"] = new JsonArray(factorValues.Select(pair =>
        {
            int row = Enumerable.Range(1, data.Count - 1).Single(index => ProcessingHistory.FactorKey(data, index) == pair.Key);
            var saved = new JsonObject { ["code"] = pair.Key, ["factor"] = pair.Value };
            if (SelectedFactorMethod == "calculado") saved["operational_volume"] = data.Number(row, data.Require("Volume_Operacional_Observado"));
            return (JsonNode?)saved;
        }).ToArray());
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
            note.Clear();
            await Initialize(revision.S("id"));
            owner.Log($"V{number} publicada e enviada para revisão. O inventário original e as versões anteriores foram preservados.");
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
        if (state == "ajustes_solicitados" && string.IsNullOrWhiteSpace(note.Text))
            throw new InvalidOperationException("Informe o motivo para solicitar ajustes.");
        grid.CommitEdit(DataGridEditingUnit.Cell, true);
        grid.CommitEdit(DataGridEditingUnit.Row, true);
        if (FactorsChanged)
            throw new InvalidOperationException("Há fatores alterados ainda não publicados. Crie uma nova versão antes de mudar o status.");
        if (!IsLatestVersion) throw new InvalidOperationException("Atualize o histórico e selecione a versão mais recente.");
        if (state == "aprovado" && !Ui.Confirm("Aprovar volume operacional?",
            $"A aprovação será aplicada ao volume operacional de {selected.S("inventory_type")} V{selected.N("number")} · {session.ContentName(selected.S("source_path"))}. DAP e altura permanecem como medidos.",
            "Aprovar esta versão")) return;
        string id = selected.S("id");
        await session.ChangeProcessingStatus(selected, state, note.Text, owner.OperationCancellation);
        note.Clear();
        await Initialize(id); owner.Log(state switch
        {
            "em_revisao" => "Revisão iniciada por " + ProcessingCatalog.User,
            "ajustes_solicitados" => "Ajustes solicitados por " + ProcessingCatalog.User,
            _ => "Volume operacional aprovado por " + ProcessingCatalog.User
        });
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
