using System.ComponentModel;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AvalonDock.Layout;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

public partial class MainWindow : Window
{
    private enum CloseChoice { Save, Discard, Cancel }

    internal ProjectSession Session { get; } = new();
    internal bool IsBusy => busy;
    private CancellationTokenSource? operationCancellation;
    private long operationVersion;
    private readonly System.Windows.Threading.DispatcherTimer progressHideTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly System.Windows.Threading.DispatcherTimer historyRefreshTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool historyRefreshing;
    internal CancellationToken OperationCancellation => operationCancellation?.Token ?? CancellationToken.None;
    private bool busy,
        closing,
        closePending,
        suppressDocumentSelection;
    internal Func<Task<bool>>? CloseConfirmation { get; set; }
    internal string WorkspacePath { get; set; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DENDRO",
            "workspace-wpf.xml"
        );
    private ProcessorView? processor;
    private readonly Dictionary<string, LayoutDocument> documents = new();
    private readonly Dictionary<string, Action> documentSelection = new();
    private readonly Dictionary<string, bool> contentsExpansion = new(StringComparer.Ordinal);
    private bool resetContentsExpansion;
    private readonly string layoutPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DENDRO",
        "workspace-wpf.xml"
    );
    private readonly string defaultLayout;

    public MainWindow()
    {
        ShellVisuals.Initialize();
        InitializeComponent();
        progressHideTimer.Tick += (_, _) => {
            progressHideTimer.Stop();
            if (!busy) ProgressDisplay.Visibility = Visibility.Collapsed;
        };
        Closed += (_, _) =>
        {
            progressHideTimer.Stop();
            foreach (var content in documents.Values.Select(d => d.Content).OfType<IDisposable>()) content.Dispose();
        };
        historyRefreshTimer.Tick += async (_, _) => {
            if (busy || closing || historyRefreshing || !Session.IsOpen || Session.FilePath.Length == 0) return;
            historyRefreshing = true;
            try { await Session.RefreshHistory(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log("Atualização do histórico indisponível: " + ex.Message); }
            finally { historyRefreshing = false; }
        };
        Loaded += (_, _) => historyRefreshTimer.Start(); Closed += (_, _) => historyRefreshTimer.Stop();
        using (var initial = new StringWriter())
        {
            new AvalonDock.Layout.Serialization.XmlLayoutSerializer(DockManager).Serialize(initial);
            defaultLayout = initial.ToString();
        }
        Session.Changed += Refresh;
        ShowTools();
        Refresh();
        Loaded += (_, _) => RestoreLayout();
        CommandBindings.Add(
            new CommandBinding(ApplicationCommands.Save, async (_, _) => await SaveProject())
        );
        InputBindings.Add(new KeyBinding(ApplicationCommands.Save, Key.S, ModifierKeys.Control));
        PreviewKeyDown += (_, e) => {
            if (e.Key == Key.F1 && Keyboard.Modifiers == ModifierKeys.Control)
            { SetRibbonPinned(!RibbonPinned); e.Handled = true; }
            else if (e.Key == Key.Escape && !RibbonPinned && RibbonExpanded)
            { SetRibbonExpanded(false); e.Handled = true; }
        };
    }

    internal void Log(string message)
    {
        StatusText.Text = message;
        HistoryText.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
        HistoryText.ScrollToEnd();
    }

    internal async Task Execute(Func<Task> action)
    {
        if (busy || closing)
            return;
        busy = true;
        using var cancellation = new CancellationTokenSource();
        operationCancellation = cancellation;
        operationVersion++;
        progressHideTimer.Stop();
        ProgressDisplay.Visibility = Visibility.Visible;
        SetProgress(0);
        Progress.IsIndeterminate = true;
        ProgressPercent.Text = "…";
        RefreshEnabled();
        bool succeeded = false;
        try
        {
            await action();
            succeeded = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Log("Operação cancelada. Nenhum resultado parcial foi registrado.");
        }
        catch (Exception ex)
        {
            Log(ex.Message);
            Ui.Error(ex);
        }
        finally
        {
            operationCancellation = null;
            CancelOperationButton.Visibility = Visibility.Collapsed;
            busy = false;
            if (ToolContent.Content is ProcessorView active && active.SourcesChanged)
            {
                try
                {
                    active.Capture();
                    processor = new ProcessorView(this, Session);
                    ToolContent.Content = processor;
                }
                catch (Exception ex)
                {
                    Log(ex.Message);
                }
            }
            RefreshEnabled();
            Progress.IsIndeterminate = false;
            SetProgress(succeeded ? 100 : 0);
            if (succeeded) progressHideTimer.Start();
            else ProgressDisplay.Visibility = Visibility.Collapsed;
        }
    }

    internal void SetProgress(double value)
    {
        Progress.Value = Math.Clamp(value, Progress.Minimum, Progress.Maximum);
        ProgressPercent.Text = $"{Progress.Value:0}%";
    }

    internal IProgress<TableProgress> StartTableOperation(string message)
    {
        StatusText.Text = message;
        progressHideTimer.Stop();
        ProgressDisplay.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        ProgressPercent.Text = "…";
        CancelOperationButton.IsEnabled = true;
        CancelOperationButton.Visibility = Visibility.Visible;
        long version = operationVersion;
        return new Progress<TableProgress>(update =>
        {
            if (!busy || version != operationVersion || OperationCancellation.IsCancellationRequested) return;
            Progress.IsIndeterminate = !update.Percent.HasValue;
            if (update.Percent.HasValue) SetProgress(Math.Clamp(update.Percent.Value, 0, 98));
            else ProgressPercent.Text = "…";
            StatusText.Text = update.Message;
        });
    }

    private void CancelOperationClick(object sender, RoutedEventArgs e)
    {
        operationCancellation?.Cancel();
        CancelOperationButton.IsEnabled = false;
        StatusText.Text = "Cancelando operação…";
    }

    private void RefreshEnabled()
    {
        DockManager.IsEnabled = !busy;
        foreach (
            var b in new[] { SaveButton, SaveAsButton, SettingsButton, VolumeButton, GrowthButton }
        )
            b.IsEnabled = Session.IsOpen && !busy;
        ToolContent.IsEnabled = Session.IsOpen && !busy;
        ContentsTree.IsEnabled = Session.IsOpen && !busy;
    }

    private void Refresh()
    {
        if (closing)
            return;
        Title = Session.Caption;
        RefreshEnabled();
        RefreshTree();
    }

    private void RefreshTree()
    {
        if (ContentsTree == null)
            return;
        static IEnumerable<TreeViewItem> Nodes(ItemsControl parent)
        {
            foreach (var node in parent.Items.OfType<TreeViewItem>())
            {
                yield return node;
                foreach (var child in Nodes(node)) yield return child;
            }
        }
        string? selected = null;
        if (resetContentsExpansion)
        {
            contentsExpansion.Clear();
            resetContentsExpansion = false;
        }
        else
        {
            foreach (var node in Nodes(ContentsTree)) contentsExpansion[ContentsNodeKey(node)] = node.IsExpanded;
            if (ContentsTree.SelectedItem is TreeViewItem current) selected = ContentsNodeKey(current);
        }
        BuildContentsTree();
        void Restore(ItemsControl parent, bool visible)
        {
            foreach (var node in parent.Items.OfType<TreeViewItem>())
            {
                string key = ContentsNodeKey(node);
                if (contentsExpansion.TryGetValue(key, out bool expanded)) node.IsExpanded = expanded;
                if (visible && key == selected) node.IsSelected = true;
                Restore(node, visible && node.IsExpanded);
            }
        }
        Restore(ContentsTree, true);
    }

    private static string ContentsNodeKey(TreeViewItem node)
    {
        static string PathKey(string path) => path.Replace('\\', '/').ToUpperInvariant();
        return node.Tag switch
        {
            string path => "table:" + PathKey(path),
            ResultSheetReference result => "result:" + PathKey(result.Path) + "\0" + result.Sheet,
            JsonObject chart => ChartView.Identity(chart),
            AnalysisReference analysis => "analysis:" + analysis.Entry.S("id"),
            AnalysisPlotReference plot => "analysis-plot:" + plot.Entry.S("id"),
            AnalysisTableReference table => "analysis-table:" + table.Entry.S("id"),
            ProcessingVersionReference run => "processing-version:" + run.Id,
            _ => "group:" + ShellVisuals.GetIcon(node) + ":" + node.Header,
        };
    }

    private void BuildContentsTree()
    {
        ContentsTree.Items.Clear();
        var sheets = ContentItem("Planilhas", CommandIconKind.Table);
        ContentsTree.Items.Add(sheets);
        var add = new MenuItem
        {
            Header = "Adicionar planilha…",
            Icon = new CommandIcon
            {
                Kind = CommandIconKind.Add,
                Width = 17,
                Height = 17,
            },
        };
        add.SetBinding(UIElement.IsEnabledProperty, new System.Windows.Data.Binding(nameof(IsEnabled))
        {
            Source = ContentsTree,
        });
        add.Click += async (_, _) => await AddData();
        sheets.ContextMenu = new ContextMenu();
        sheets.ContextMenu.Items.Add(add);
        Menu(sheets, "Juntar tabelas…", () => OpenTableTool(false));
        var db = ContentItem("Bancos de dados", CommandIconKind.Database);
        db.ContextMenu = new ContextMenu();
        Menu(db, "Adicionar banco DendroLab…", AddBank);
        var shapes = ContentItem("Dados vetoriais", CommandIconKind.Shape);
        ContentsTree.Items.Add(db);
        ContentsTree.Items.Add(shapes);
        if (!Session.IsOpen)
            return;
        TreeViewItem managedBank = db;
        string managedPath = Session.Meta.S("database_path");
        if (managedPath.Length > 0)
        {
            managedBank = ContentItem(Path.GetFileName(managedPath), CommandIconKind.Database);
            managedBank.ToolTip = Session.Resolve(managedPath);
            db.Items.Add(managedBank);
        }
        var nodes = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Session.Files)
        {
            if (
                !Session
                    .ContentName(path)
                    .Contains(ContentSearch.Text, StringComparison.CurrentCultureIgnoreCase)
                && !Path.GetFileName(path)
                    .Contains(ContentSearch.Text, StringComparison.CurrentCultureIgnoreCase)
                && !Session
                    .Meta.Arr("charts")
                    .OfType<JsonObject>()
                    .Any(c =>
                        string.Equals(c.S("path"), path, StringComparison.OrdinalIgnoreCase)
                        && Session
                            .ContentName(path, c)
                            .Contains(ContentSearch.Text, StringComparison.CurrentCultureIgnoreCase)
                    )
                && !Session.Meta.Arr("processing_results").OfType<JsonObject>().Any(r =>
                    string.Equals(r.S("source_path"), path, StringComparison.OrdinalIgnoreCase)
                    && r.Arr("sheets").Any(s => s.Display().Contains(ContentSearch.Text, StringComparison.CurrentCultureIgnoreCase)))
            )
                continue;
            var item = ContentItem(
                Session.ContentName(path),
                Session.IsSpreadsheet(path) ? CommandIconKind.Table
                    : Session.IsManagedTable(path) ? CommandIconKind.Table
                    : TableStore.IsBank(path) ? CommandIconKind.Database
                    : CommandIconKind.Shape
            );
            item.Tag = path;
            item.ToolTip = path;
            nodes[path] = item;
            if (!Session.Meta.Arr("processing_results").Any(r => r.S("path") == path)) (
                Session.IsSpreadsheet(path) ? sheets
                : Path.GetExtension(path).Equals(".shp", StringComparison.OrdinalIgnoreCase)
                    ? shapes
                : managedPath.Length > 0 && Session.Resolve(path).StartsWith(Session.Resolve(managedPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    ? managedBank : db
            ).Items.Add(item);
            item.ContextMenu = new ContextMenu();
            if (Session.IsTabular(path))
            {
                Menu(item, "Abrir tabela", () => OpenTable(path));
                Menu(item, "Copiar para o banco do projeto…", async () => {
                    string bank = Session.ProjectWorkFolder();
                    var options = Session.ExcelOptions(path);
                    string name = TableTools.SuggestedName(Path.GetFileNameWithoutExtension(path));
                    for (int n = 2; File.Exists(Path.Combine(bank, name + CompactBankReader.Extension))
                        || CompactBankFile.Exists(Session.Resolve(Session.ProjectReference(Path.Combine(bank, name + CompactBankReader.Extension)))); n++)
                        name = TableTools.SuggestedName(Path.GetFileNameWithoutExtension(path)) + "_" + n;
                    var result = await Task.Run(() => TableTools.Import(new(Session.Resolve(path), options.S("sheet"), (int)options.N("header_row", 1)), bank, name));
                    await Session.AddTableResult(result, path);
                    Log($"{result.Rows:N0} linhas copiadas para o banco do projeto.");
                });
                if (Session.IsTabular(path))
                    Menu(item, "Worksheet e cabeçalho…", () => ConfigureExcel(path));
                Menu(item, "Gráfico de dispersão…", () => OpenChart(path, false));
                Menu(item, "Estratificar…", () => OpenTableTool(true, path));
                Menu(item, "Gerar coluna…", () => OpenTableTool(false, path, true));
                Menu(item, "Isolation Forest…", () => OpenAnomalyTool(path));
                Menu(item, "Limpeza integrada…", () => OpenIntegratedCleaning(path));
                Menu(item, "Testes estatísticos…", () => OpenStatistics(false, path));
                Menu(item, "Identidade de modelos…", () => OpenStatistics(true, path));
            }
            item.ContextMenu.Items.Add(new Separator());
            Menu(item, "Renomear…", () => RenameContent(path));
            Menu(
                item,
                "Remover",
                async () =>
                {
                    await Session.RemoveFile(Session.Files.ToList().IndexOf(path));
                    CloseDocuments();
                    processor = null;
                    ShowTools();
                }
            );
            foreach (
                var chart in Session
                    .Meta.Arr("charts")
                    .OfType<JsonObject>()
                    .Where(c =>
                        string.Equals(c.S("path"), path, StringComparison.OrdinalIgnoreCase)
                    )
            )
            {
                var node = ContentItem(Session.ContentName(path, chart), CommandIconKind.Chart);
                node.Tag = chart.DeepClone();
                node.ContextMenu = new ContextMenu();
                Menu(node, "Abrir gráfico", () => OpenChart(path, chart.B("fitting"), chart, false));
                Menu(node, "Regressões 1P…", () => OpenChart(path, true, chart));
                Menu(node, "Fronteira de eficiência…", () => {
                    SetTool(new FrontierView(this, Session, chart)); return Task.CompletedTask;
                });
                Menu(
                    node,
                    "ICA e IMA…",
                    () =>
                    {
                        SetTool(new GrowthValidationView(this, Session, chart));
                        return Task.CompletedTask;
                    }
                );
                node.ContextMenu.Items.Add(new Separator());
                Menu(node, "Renomear…", () => RenameContent(path, chart));
                Menu(
                    node,
                    "Remover gráfico",
                    () =>
                    {
                        var charts = Session.Meta.Arr("charts");
                        var match = charts.FirstOrDefault(c => JsonNode.DeepEquals(c, chart));
                        if (match != null)
                            charts.Remove(match);
                        CloseDocument(ChartView.Identity(chart));
                        Session.Touch();
                        return Task.CompletedTask;
                    }
                );
                item.Items.Add(node);
            }
            foreach (
                var analysis in Session
                    .Meta.Arr("analyses")
                    .OfType<JsonObject>()
                    .Where(a =>
                        string.Equals(a.S("path"), path, StringComparison.OrdinalIgnoreCase)
                    )
            )
                item.Items.Add(AnalysisNode(analysis));
            var resultNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var result in Session.Meta.Arr("processing_results").OfType<JsonObject>()
                .Where(r => string.Equals(r.S("source_path"), path, StringComparison.OrdinalIgnoreCase)).Reverse())
            {
                string output = result.S("path");
                TreeViewItem resultParent = item;
                bool historical = result.S("id").Length > 0;
                if (historical)
                {
                    var versionNode = ContentItem($"{result.S("inventory_type")} · V{result.N("number")} {(result.N("number") == 1 ? "original" : "corrigida")} · {ProcessingReviewView.Status(result)}", CommandIconKind.ProcessingReview);
                    versionNode.Tag = new ProcessingVersionReference(result.S("id"));
                    versionNode.ContextMenu = new ContextMenu();
                    Menu(versionNode, "Revisar / aprovar / exportar…", () => OpenProcessingReview(result.S("id")));
                    versionNode.ToolTip = result.S("created") + " · " + result.S("user");
                    item.Items.Add(versionNode); resultParent = versionNode;
                }
                foreach (var sheet in result.Arr("sheets"))
                {
                    string name = sheet.Display();
                    if (name.Equals("Graficos", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!historical && !resultNames.Add(name)) continue;
                    var reference = new ResultSheetReference(output, name);
                    var node = ContentItem(name, CommandIconKind.Table);
                    node.Tag = reference;
                    node.ToolTip = output + " · " + name;
                    node.ContextMenu = new ContextMenu();
                    Menu(node, ProcessingReportView.Handles(name) ? "Abrir informações e gráficos" : "Abrir tabela", () => OpenTable(output, name));
                    Menu(node, "Gráfico de dispersão…", () => OpenResultTool(reference, () => OpenChart(output, false,
                        new JsonObject { ["path"] = output, ["sheet"] = name, ["header_row"] = 1 })));
                    Menu(node, "Estratificar…", () => OpenResultTool(reference, () => OpenTableTool(true, output)));
                    Menu(node, "Gerar coluna…", () => OpenResultTool(reference, () => OpenTableTool(false, output, true)));
                    Menu(node, "Isolation Forest…", () => OpenAnomalyTool(output, name));
                    Menu(node, "Limpeza integrada…", () => OpenIntegratedCleaning(output, name));
                    if (name is "DIRETO - PARCELAS" or "RESULTADO - PARCELAS")
                        Menu(node, "Auditar inventário…", () => OpenInventoryAudit(output, name));
                    Menu(node, "Testes estatísticos…", () => OpenResultTool(reference, () => OpenStatistics(false, output)));
                    node.ContextMenu.Items.Add(new Separator());
                    Menu(node, "Remover tabela", async () => {
                        await Session.RemoveResultTable(output, name);
                        CloseDocuments();
                        processor = null;
                        ShowTools();
                        Log($"Tabela {name} removida do banco.");
                    });
                    resultParent.Items.Add(node);
                }
            }
        }
        // Charts and analyses belong to their actual result table, not its hidden dataset container.
        foreach (var inputNode in nodes.Values)
            foreach (var tableNode in ContentsDescendants(inputNode).Where(n => n.Tag is ResultSheetReference).ToArray()) {
                var result = (ResultSheetReference)tableNode.Tag;
                if (!nodes.TryGetValue(result.Path, out var outputNode)) continue;
                foreach (var chartNode in outputNode.Items.OfType<TreeViewItem>().Where(n =>
                    n.Tag is JsonObject c && c.S("sheet") == result.Sheet
                    || n.Tag is AnalysisReference a && a.Entry["definition"].S("sheet") == result.Sheet).ToArray()) {
                    outputNode.Items.Remove(chartNode); tableNode.Items.Add(chartNode);
                }
            }
        foreach (var derived in Session.Meta.Arr("derived_tables").OfType<JsonObject>())
        {
            string path = derived.S("path"),
                parent = derived.S("source_path");
            if (!nodes.TryGetValue(path, out var child)) continue;
            var resultParents = nodes.Values.SelectMany(ContentsDescendants)
                .Where(n => n.Tag is ResultSheetReference r && r.Path == parent).ToArray();
            TreeViewItem? parentNode = resultParents.FirstOrDefault(n =>
                ((ResultSheetReference)n.Tag).Sheet == derived.S("source_sheet")) ?? resultParents.FirstOrDefault();
            if (parentNode == null && !nodes.TryGetValue(parent, out parentNode)) continue;
            // A corrupted project must not create a cycle in the visual tree.
            var ancestor = parentNode;
            bool cycle = false;
            while (ancestor != null)
            {
                if (ancestor == child)
                {
                    cycle = true;
                    break;
                }
                ancestor = ItemsControl.ItemsControlFromItemContainer(ancestor) as TreeViewItem;
            }
            if (cycle)
                continue;
            (ItemsControl.ItemsControlFromItemContainer(child))?.Items.Remove(child);
            parentNode.Items.Add(child);
        }
        var standalone = Session
            .Meta.Arr("analyses")
            .OfType<JsonObject>()
            .Where(a => !nodes.ContainsKey(a.S("path")))
            .ToArray();
        if (standalone.Length > 0)
        {
            var analysisRoot = ContentItem("Análises", CommandIconKind.History);
            foreach (var analysis in standalone)
                analysisRoot.Items.Add(AnalysisNode(analysis));
            ContentsTree.Items.Add(analysisRoot);
        }
    }

    private TreeViewItem AnalysisNode(JsonObject analysis)
    {
        var node = ContentItem(analysis.S("title"), analysis["definition"].S("kind") switch {
            "isolation_forest" => CommandIconKind.Anomaly,
            "inventory_audit" => CommandIconKind.InventoryAudit,
            "growth_comparison" => CommandIconKind.CurveComparison,
            "increments" => CommandIconKind.Increments,
            _ => CommandIconKind.Statistics,
        });
        node.Tag = new AnalysisReference(analysis);
        node.ContextMenu = new ContextMenu();
        Menu(node, "Abrir análise", () => AnalysisDocuments.OpenSaved(this, Session, analysis));
        if (analysis["definition"].S("kind") == "isolation_forest")
        {
            var table = ContentItem("Tabela de resultados · escores e classificação", CommandIconKind.Table);
            table.Tag = new AnalysisTableReference(analysis);
            table.ContextMenu = new ContextMenu();
            Menu(table, "Abrir tabela", () => AnalysisDocuments.OpenResultTable(this, Session, analysis));
            node.Items.Add(table);
            Menu(node, "Abrir tabela de resultados", () => AnalysisDocuments.OpenResultTable(this, Session, analysis));
        }
        if (analysis["definition"].S("kind") != "growth_comparison"
            && (Dendro.Core.Configuration.ProjectSnapshots.HasPlot(analysis) || analysis["definition"].S("kind") == "increments"))
        {
            var graph = ContentItem("Gráfico · " + analysis.S("title"), CommandIconKind.Chart);
            graph.Tag = new AnalysisPlotReference(analysis);
            graph.ContextMenu = new ContextMenu();
            Menu(graph, "Abrir gráfico", () => AnalysisDocuments.OpenPlot(this, Session, analysis));
            node.Items.Add(graph);
            Menu(node, "Abrir gráfico", () => AnalysisDocuments.OpenPlot(this, Session, analysis));
        }
        Menu(
            node,
            "Remover",
            () =>
            {
                Session.Meta.Arr("analyses").Remove(analysis);
                CloseDocument("analysis:" + analysis.S("id"));
                CloseDocument("analysis-plot:" + analysis.S("id"));
                CloseDocument("analysis-table:" + analysis.S("id"));
                Session.Touch();
                return Task.CompletedTask;
            }
        );
        return node;
    }

    private static IEnumerable<TreeViewItem> ContentsDescendants(ItemsControl parent)
    {
        foreach (var child in parent.Items.OfType<TreeViewItem>())
        {
            yield return child;
            foreach (var descendant in ContentsDescendants(child)) yield return descendant;
        }
    }

    private void Menu(TreeViewItem item, string title, Func<Task> action)
    {
        var kind = title switch
        {
            "Renomear…" => CommandIconKind.Rename,
            "Remover" or "Remover gráfico" => CommandIconKind.Remove,
            "Abrir gráfico" or "Gráfico de dispersão…" => CommandIconKind.Chart,
            "Regressões 1P…" => CommandIconKind.Regression,
            "Estratificar…" => CommandIconKind.Stratify,
            "Gerar coluna…" => CommandIconKind.CalculateColumn,
            "Isolation Forest…" => CommandIconKind.Anomaly,
            "Auditar inventário…" => CommandIconKind.InventoryAudit,
            "Juntar tabelas…" => CommandIconKind.Merge,
            "Adicionar banco DendroLab…" => CommandIconKind.Database,
            _ => CommandIconKind.Table,
        };
        var m = new MenuItem
        {
            Header = title,
            Icon = new CommandIcon
            {
                Kind = kind,
                Width = 17,
                Height = 17,
            },
        };
        if (title == "Renomear…")
            m.InputGestureText = "F2";
        m.Click += async (_, e) =>
        {
            e.Handled = true;
            await Execute(action);
        };
        item.ContextMenu!.Items.Add(m);
    }

    private TreeViewItem ContentItem(string name, CommandIconKind icon)
    {
        var item = new TreeViewItem
        {
            Header = name,
            HeaderTemplate = (DataTemplate)FindResource("ContentsItemHeader"),
            IsExpanded = true,
        };
        ShellVisuals.SetIcon(item, icon);
        return item;
    }

    internal Task RenameContent(string path, JsonObject? chart = null)
    {
        var panel = Ui.Form();
        var input = Ui.Input(Session.ContentName(path, chart));
        System.Windows.Automation.AutomationProperties.SetAutomationId(input, "ContentName");
        Ui.Field(panel, "Nome exibido neste projeto", input);
        var error = Ui.Text("");
        error.Foreground = System.Windows.Media.Brushes.Firebrick;
        panel.Children.Add(error);
        var dialog = Ui.Dialog("Renomear conteúdo", panel, 500, 255);
        dialog.MinHeight = 230;
        dialog.ResizeMode = ResizeMode.NoResize;
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var save = Ui.Button(
            "Renomear",
            () =>
            {
                try
                {
                    Session.RenameContent(path, input.Text, chart);
                    if (ToolContent.Content is ChartView graphTool)
                        graphTool.RefreshContentNames();
                    if (ToolContent.Content is ProcessorView processorTool)
                        processorTool.RefreshContentNames();
                    string key = chart == null ? "table:" + path : ChartView.Identity(chart);
                    if (documents.TryGetValue(key, out var doc))
                        doc.Title =
                            chart == null
                                ? "Tabela · " + Session.ContentName(path)
                                : Session.ContentName(path, chart);
                    dialog.DialogResult = true;
                }
                catch (InvalidOperationException ex)
                {
                    error.Text = ex.Message;
                }
            }
        );
        save.IsDefault = true;
        var cancel = Ui.Button("Cancelar", () => dialog.DialogResult = false);
        cancel.IsCancel = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        panel.Children.Add(buttons);
        dialog.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
        dialog.ShowDialog();
        return Task.CompletedTask;
    }

    private void CloseDocuments()
    {
        foreach (var d in documents.Values.ToArray())
            d.Close();
        documents.Clear();
        documentSelection.Clear();
    }

    private void CloseDocument(string key)
    {
        if (documents.Remove(key, out var doc))
        {
            documentSelection.Remove(key);
            doc.Close();
        }
    }

    internal LayoutDocument Document(
        string key,
        string title,
        object content,
        Action? selected = null
    )
    {
        if (selected != null)
            documentSelection[key] = selected;
        if (documents.TryGetValue(key, out var existing))
        {
            if (!ReferenceEquals(existing.Content, content) && existing.Content is IDisposable disposable) disposable.Dispose();
            existing.Content = content;
            existing.Title = title;
            existing.IsActive = true;
            AttachChartPanels(content as ChartWorkspace);
            return existing;
        }
        var doc = new LayoutDocument
        {
            ContentId = key,
            Title = title,
            Content = content,
        };
        documents[key] = doc;
        doc.IsSelectedChanged += (_, _) =>
        {
            if (!suppressDocumentSelection && doc.IsSelected) AttachChartPanels(doc.Content as ChartWorkspace);
            if (
                !suppressDocumentSelection
                && doc.IsSelected
                && documentSelection.TryGetValue(key, out var activate)
            )
                activate();
        };
        Documents.Children.Add(doc);
        doc.Closed += (_, _) =>
        {
            if (!suppressDocumentSelection && doc.Content is IDisposable disposable) disposable.Dispose();
            if (documents.TryGetValue(key, out var current) && ReferenceEquals(current, doc))
            {
                documents.Remove(key);
                documentSelection.Remove(key);
            }
        };
        doc.IsActive = true;
        AttachChartPanels(content as ChartWorkspace);
        return doc;
    }

    internal void FocusDocument(LayoutDocument doc) => doc.IsActive = true;

    private async Task<bool> MayReplace()
    {
        if (busy)
            return false;
        if (!Session.IsDirty)
            return true;
        return AskSaveChanges() switch
        {
            CloseChoice.Save => await SaveProject(),
            CloseChoice.Discard => true,
            _ => false,
        };
    }

    private CloseChoice AskSaveChanges()
    {
        var content = new Grid { Margin = new Thickness(20) };
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var message = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        message.Children.Add(new TextBlock
        {
            Text = "Salvar alterações do projeto atual?",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = (System.Windows.Media.Brush)FindResource("Accent"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 7),
        });
        message.Children.Add(new TextBlock
        {
            Text = "As alterações não salvas serão perdidas se você continuar sem salvar.",
            Foreground = System.Windows.Media.Brushes.SlateGray,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(message);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var dialog = Ui.Dialog("Salvar alterações", content, 510, 225);
        dialog.Owner = this;
        dialog.ResizeMode = ResizeMode.NoResize;
        Grid.SetRow(buttons, 1);
        content.Children.Add(buttons);

        CloseChoice choice = CloseChoice.Cancel;
        var cancel = Ui.Button("Cancelar", () => dialog.Close());
        cancel.IsCancel = true;
        var discard = Ui.Button("Descartar", () => { choice = CloseChoice.Discard; dialog.Close(); });
        var save = Ui.Button("Salvar", () => { choice = CloseChoice.Save; dialog.Close(); });
        save.IsDefault = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(discard);
        buttons.Children.Add(save);
        dialog.ShowDialog();
        return choice;
    }

    public async Task OpenProject(string path)
    {
        if (!await MayReplace())
            return;
        await Execute(async () =>
        {
            await Session.Open(path);
            ProjectLoaded();
            Log("Projeto aberto.");
        });
    }

    private void ProjectLoaded()
    {
        resetContentsExpansion = true;
        CloseDocuments();
        processor = null;
        ShowTools();
        Refresh();
        Document("project", "Projeto", new ProjectView(Session));
    }

    private async void NewClick(object s, RoutedEventArgs e)
    {
        if (!await MayReplace())
            return;
        await Execute(async () =>
        {
            await Session.New("Novo projeto");
            ProjectLoaded();
        });
    }

    private async void OpenClick(object s, RoutedEventArgs e)
    {
        var path = Ui.PickFile("Projeto DendroLab|*.dendro");
        if (path != null)
            await OpenProject(path);
    }

    internal async Task<bool> SaveProject(bool saveAs = false)
    {
        if (busy)
            return false;
        if (!Session.IsOpen)
            return true;
        busy = true;
        RefreshEnabled();
        try
        {
            processor?.Capture();
            var path = Session.FilePath;
            if (saveAs || string.IsNullOrEmpty(path))
                path = Ui.SaveFile("Projeto DendroLab|*.dendro", "Projeto.dendro");
            if (path == null)
                return false;
            string original = Session.FilePath;
            await Session.Save(path);
            if (original.Length > 0 && !string.Equals(original, Session.FilePath, StringComparison.OrdinalIgnoreCase)
                && Session.Meta.S("database_path").Length > 0) { CloseDocuments(); ShowTools(); }
            Log("Projeto salvo.");
            return true;
        }
        catch (Exception ex)
        {
            Ui.Error(ex);
            return false;
        }
        finally
        {
            busy = false;
            RefreshEnabled();
        }
    }

    private async void SaveClick(object s, RoutedEventArgs e) => await SaveProject();

    private async void SaveAsClick(object s, RoutedEventArgs e) => await SaveProject(true);

    private async void SettingsClick(object s, RoutedEventArgs e) =>
        await Execute(async () =>
        {
            await SettingsView.Show(this, Session);
            if (ToolContent.Content is ChartView chart)
                await chart.Load(chart.CurrentConfig.S("path"), chart.CurrentConfig, false);
        });

    private void VolumeClick(object s, RoutedEventArgs e) =>
        Document("volumes", "Modelos volumétricos", new ModelsView(this, Session, false));

    private void GrowthClick(object s, RoutedEventArgs e) =>
        Document("growth", "Crescimento e produção", new ModelsView(this, Session, true));

    private void ContentsClick(object s, RoutedEventArgs e) => ContentsPane.Show();

    private void ToolsClick(object s, RoutedEventArgs e) => ToolsPane.Show();

    private void HistoryClick(object s, RoutedEventArgs e) => HistoryPane.Show();

    private void ClearHistoryClick(object s, RoutedEventArgs e) => HistoryText.Clear();

    private void ResetLayoutClick(object s, RoutedEventArgs e)
    {
        ResetWorkspace();
    }

    internal void ResetWorkspace()
    {
        SetRibbonPinned(true);
        var opened = documents
            .Select(pair =>
                (
                    pair.Key,
                    pair.Value.Title,
                    pair.Value.Content,
                    Selected: documentSelection.GetValueOrDefault(pair.Key)
                )
            )
            .ToArray();
        suppressDocumentSelection = true;
        try
        {
            var serializer = LayoutSerializer();
            serializer.Deserialize(new StringReader(defaultLayout));
            RebindLayout();
            documents.Clear();
            documentSelection.Clear();
            foreach (var entry in opened)
                Document(entry.Key, entry.Title, entry.Content, entry.Selected);
        }
        finally
        {
            suppressDocumentSelection = false;
        }
        var selected = documents.FirstOrDefault(pair => pair.Value.IsSelected);
        if (selected.Key != null && documentSelection.TryGetValue(selected.Key, out var activate))
            activate();
    }

    private void ContentSearchChanged(object s, TextChangedEventArgs e)
    {
        if (IsInitialized)
            RefreshTree();
    }

    private void ToolSearchChanged(object s, TextChangedEventArgs e)
    {
        if (IsInitialized)
            ShowTools();
    }

    private void ShowTools()
    {
        if (ToolContent == null)
            return;
        var list = new ListBox
        {
            Margin = new Thickness(12, 6, 12, 12),
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
        };
        foreach (
            var tool in new[]
            {
                "Processador",
                "Revisão de inventário",
                "Auditoria pós-processamento",
                "Ativo biológico",
                "Gráficos",
                "Regressões 1P",
                "Fronteira de eficiência",
                "Juntar tabelas",
                "Gerar coluna",
                "Estratificar",
                "ICA e IMA",
                "Comparar curvas de produção",
                "Isolation Forest",
                "Limpeza integrada",
                "Identidade de modelos",
                "Testes estatísticos",
            }.Where(n =>
                n.Contains(ToolSearch.Text, StringComparison.CurrentCultureIgnoreCase)
                || n == "Identidade de modelos"
                    && "Graybill F equações grupos".Contains(
                        ToolSearch.Text,
                        StringComparison.CurrentCultureIgnoreCase
                    )
                || n == "Testes estatísticos"
                    && "Welch Student t F ANOVA Levene Brown Forsythe variâncias médias".Contains(
                        ToolSearch.Text,
                        StringComparison.CurrentCultureIgnoreCase
                    )
            )
        )
        {
            var entry = new ListBoxItem
            {
                Content = tool,
                Style = (Style)FindResource("ToolCardItem"),
                Tag = tool,
                IsEnabled =
                    (tool != "Regressões 1P" && tool != "Fronteira de eficiência")
                    || (Session.IsOpen && Session.Meta.Arr("charts").Count > 0),
            };
            ShellVisuals.SetIcon(
                entry,
                tool switch
                {
                    "Processador" => CommandIconKind.Processor,
                    "Revisão de inventário" => CommandIconKind.ProcessingReview,
                    "Auditoria pós-processamento" => CommandIconKind.InventoryAudit,
                    "Ativo biológico" => CommandIconKind.BiologicalAsset,
                    "Gráficos" => CommandIconKind.Chart,
                    "Regressões 1P" => CommandIconKind.Regression,
                    "Fronteira de eficiência" => CommandIconKind.Frontier,
                    "Juntar tabelas" => CommandIconKind.Merge,
                    "Gerar coluna" => CommandIconKind.CalculateColumn,
                    "ICA e IMA" => CommandIconKind.Increments,
                    "Comparar curvas de produção" => CommandIconKind.CurveComparison,
                    "Isolation Forest" => CommandIconKind.Anomaly,
                    "Limpeza integrada" => CommandIconKind.IntegratedCleaning,
                    "Identidade de modelos" => CommandIconKind.Identity,
                    "Testes estatísticos" => CommandIconKind.Statistics,
                    _ => CommandIconKind.Stratify,
                }
            );
            list.Items.Add(entry);
        }
        list.SelectionChanged += async (_, _) =>
        {
            if (list.SelectedItem is not ListBoxItem i)
                return;
            if ((string)i.Tag == "Processador")
            {
                processor = new ProcessorView(this, Session);
                ToolContent.Content = processor;
            }
            else if ((string)i.Tag == "Revisão de inventário")
                await Execute(() => OpenProcessingReview());
            else if ((string)i.Tag is "Juntar tabelas" or "Estratificar")
                await Execute(() => OpenTableTool((string)i.Tag == "Estratificar"));
            else if ((string)i.Tag == "Gerar coluna")
                await Execute(() => OpenTableTool(false, calculateColumns: true));
            else if ((string)i.Tag == "ICA e IMA")
                SetTool(new GrowthValidationView(this, Session));
            else if ((string)i.Tag == "Comparar curvas de produção")
                await Execute(async () => { var view = new GrowthComparisonView(this, Session); SetTool(view); await view.Initialize(); });
            else if ((string)i.Tag == "Isolation Forest")
                await Execute(() => OpenAnomalyTool());
            else if ((string)i.Tag == "Limpeza integrada")
                await Execute(() => OpenIntegratedCleaning());
            else if ((string)i.Tag == "Auditoria pós-processamento")
                await Execute(() => OpenInventoryAudit());
            else if ((string)i.Tag == "Ativo biológico")
                await Execute(async()=>{var view=new BiologicalAssetView(this,Session);SetTool(view);await view.Initialize();});
            else if ((string)i.Tag == "Fronteira de eficiência")
                SetTool(new FrontierView(this, Session));
            else if ((string)i.Tag is "Identidade de modelos" or "Testes estatísticos")
                await Execute(() => OpenStatistics((string)i.Tag == "Identidade de modelos"));
            else
                await Execute(() => OpenChart(null, (string)i.Tag != "Gráficos"));
        };
        ToolContent.Content = list;
        RefreshEnabled();
    }

    internal void SetTool(object content)
    {
        if (closing)
            return;
        ToolsPane.Show();
        ToolContent.Content = content;
    }

    internal async Task OpenTableTool(bool stratify, string? path = null, bool calculateColumns = false)
    {
        var view = new TableToolsView(this, Session, stratify, path, calculateColumns);
        SetTool(view);
        await view.Initialize();
    }

    internal async Task OpenAnomalyTool(string? path = null, string? sheet = null)
    {
        var view = new IsolationForestView(this, Session);
        SetTool(view); await view.Initialize(path, sheet);
    }

    internal async Task OpenIntegratedCleaning(string? path = null, string? sheet = null)
    {
        var view = new IntegratedCleaningView(this, Session);
        SetTool(view); await view.Initialize(path, sheet);
    }

    internal async Task OpenInventoryAudit(string? path = null, string? sheet = null)
    {
        var view = new InventoryAuditView(this, Session);
        SetTool(view); await view.Initialize(path, sheet);
    }

    internal async Task OpenProcessingReview(string? id = null)
    {
        var view = new ProcessingReviewView(this, Session); SetTool(view); await view.Initialize(id);
    }

    internal async Task OpenStatistics(bool identity, string? path = null)
    {
        var view = new StatisticalTestsView(this, Session, identity, path);
        SetTool(view);
        await view.Initialize();
    }

    private async Task AddBank()
    {
        var path = Ui.PickFile("Banco DendroLab|*.dlb;*.dendrodb;*.db");
        if (path == null)
            return;
        var tables = await Task.Run(() => TableStore.Tables(path));
        if (tables.Length == 0)
            throw new InvalidDataException("O banco não contém tabelas.");
        Session.AddFile(path);
        Session.SetExcel(path, tables[0], 1);
    }

    internal DockPanel ToolHeader(string title)
    {
        var header = new DockPanel { LastChildFill = true };
        var back = Ui.Button("", ShowTools);
        back.Style = (Style)FindResource("ToolBackButton");
        back.ToolTip = "Voltar às ferramentas";
        System.Windows.Automation.AutomationProperties.SetName(back, "Voltar às ferramentas");
        back.Content = new System.Windows.Shapes.Path
        {
            Data = System.Windows.Media.Geometry.Parse("M 9,2 L 3,8 L 9,14 M 3,8 L 15,8"),
            Stroke = System.Windows.Media.Brushes.SlateGray,
            StrokeThickness = 1.6,
            StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
            StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
            Width = 18,
            Height = 16,
        };
        DockPanel.SetDock(back, Dock.Left);
        header.Children.Add(back);
        var label = Ui.Text(title, 21);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(0);
        header.Children.Add(label);
        return header;
    }

    private async Task AddData()
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Planilhas Excel|*.xlsx;*.xls|Dados|*.dlb;*.dendrodb;*.db;*.shp|Todos|*.*",
            Multiselect = true,
        };
        if (picker.ShowDialog() != true)
            return;
        await Execute(async () =>
        {
            foreach (var path in picker.FileNames)
            {
                if (Session.IsSpreadsheet(path))
                {
                    if (!await ExcelSelectionDialog.Configure(this, Session, path))
                        continue;
                }
                Session.AddFile(path);
            }
        });
    }

    private async Task ConfigureExcel(string path)
    {
        await ExcelSelectionDialog.Configure(this, Session, path);
    }

    internal sealed record ResultSheetReference(string Path, string Sheet);
    internal sealed record ProcessingVersionReference(string Id);

    private Task OpenResultTool(ResultSheetReference result, Func<Task> open)
    {
        Session.SetExcel(result.Path, result.Sheet, 1);
        return open();
    }

    internal async Task OpenTable(string path, string? sheet = null)
    {
        var o = Session.ExcelOptions(path);
        string selectedSheet = sheet ?? o.S("sheet");
        if (ProcessingReportView.Handles(selectedSheet))
        {
            var view = await ProcessingReportView.Create(new(Session.Resolve(path), selectedSheet), OperationCancellation);
            Document("table:" + path + (sheet == null ? "" : ":" + sheet), selectedSheet == "H Ajuste" ? "Ajuste de altura" : "Estatísticas do inventário", view);
            return;
        }
        var source = new TableSource(Session.Resolve(path), selectedSheet, sheet == null ? (int)o.N("header_row", 1) : 1);
        if (selectedSheet == "AJUSTADO")
        {
            var run = Session.Meta.Arr("processing_results").FirstOrDefault(r => r.S("id").Length > 0 && Session.Resolve(r.S("path")) == Session.Resolve(path));
            var original = run == null ? null : Session.Meta.Arr("processing_results").FirstOrDefault(r => r.S("family_id") == run.S("family_id") && r.N("number") == 1);
            if (original != null)
            {
                string originalPath = Session.Resolve(original.S("path"));
                source = await Task.Run(() =>
                {
                    using var bank = new CompactBankReader(originalPath);
                    var frozen = bank.Catalog.Tables.FirstOrDefault(t => t.Name == Dendro.Core.Processing.ProcessingHistory.InternalPrefix + "INPUT");
                    if (frozen == null) return source;
                    using var current = new TableCursor(source);
                    return frozen.Columns.Except(current.Columns, StringComparer.Ordinal).Any()
                        ? new TableSource(originalPath, frozen.Name) : source;
                });
            }
        }
        var browser = new TableBrowser(this, source, path);
        await browser.Load();
        Document("table:" + path + (sheet == null ? "" : ":" + sheet),
            "Tabela · " + (sheet ?? Session.ContentName(path)), browser);
        Log($"{browser.Total:N0} linhas. Visualização em páginas de 500 linhas.");
    }

    internal async Task RefreshUpdatedTable(string path, string sheet)
    {
        CloseDocument("table:" + path);
        CloseDocument("table:" + path + ":" + sheet);
        await OpenTable(path, sheet);
    }

    internal async Task OpenChart(string? path, bool fitting, JsonObject? config = null, bool activateTool = true)
    {
        if (fitting && Session.Meta.Arr("charts").Count == 0)
            throw new InvalidOperationException("Crie um gráfico antes de abrir Regressões 1P.");
        var view = new ChartView(this, Session, fitting);
        if (activateTool) SetTool(view);
        await view.Load(path, config);
    }

    private async void TreeDoubleClick(object s, MouseButtonEventArgs e)
    {
        if (ContentsTree.SelectedItem is TreeViewItem i)
        {
            if (i.Tag is string path && Session.IsTabular(path))
                await Execute(() => OpenTable(path));
            else if (i.Tag is JsonObject chart)
                await Execute(() => OpenChart(chart.S("path"), chart.B("fitting"), chart, false));
            else if (i.Tag is AnalysisReference analysis)
                await Execute(() => AnalysisDocuments.OpenSaved(this, Session, analysis.Entry));
            else if (i.Tag is AnalysisPlotReference analysisPlot)
                await Execute(() => AnalysisDocuments.OpenPlot(this, Session, analysisPlot.Entry));
            else if (i.Tag is AnalysisTableReference analysisTable)
                await Execute(() => AnalysisDocuments.OpenResultTable(this, Session, analysisTable.Entry));
            else if (i.Tag is ProcessingVersionReference version)
                await Execute(() => OpenProcessingReview(version.Id));
            else if (i.Tag is ResultSheetReference result)
                await Execute(() => OpenTable(result.Path, result.Sheet));
        }
    }

    private async void ContentsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F2 || ContentsTree.SelectedItem is not TreeViewItem selected)
            return;
        e.Handled = true;
        if (selected.Tag is string path)
            await Execute(() => RenameContent(path));
        else if (selected.Tag is JsonObject chart)
            await Execute(() => RenameContent(chart.S("path"), chart));
    }

    private async void OnDrop(object s, DragEventArgs e)
    {
        if (!Session.IsOpen || !e.Data.GetDataPresent(DataFormats.FileDrop))
            return;
        await Execute(async () =>
        {
            foreach (var path in (string[])e.Data.GetData(DataFormats.FileDrop))
            {
                if (
                    Session.IsSpreadsheet(path)
                    && !await ExcelSelectionDialog.Configure(this, Session, path)
                )
                    continue;
                Session.AddFile(path);
            }
        });
    }

    private void RestoreLayout()
    {
        if (App.IsSelfTest || !File.Exists(layoutPath))
            return;
        try
        {
            LoadWorkspace(layoutPath);
        }
        catch (Exception ex)
        {
            Log("Não foi possível restaurar o layout: " + ex.Message);
        }
    }

    private AvalonDock.Layout.Serialization.XmlLayoutSerializer LayoutSerializer()
    {
        var content = new Dictionary<string, object?>
        {
            { "contents", ContentsPane.Content },
            { "tools", ToolsPane.Content },
            { "history", HistoryPane.Content },
            { "regression-models", RegressionModelsPane?.Content ?? Ui.Text("Modelos") },
            { "regression-data", RegressionDataPane?.Content ?? Ui.Text("Dados do ajuste") },
        };
        var serializer = new AvalonDock.Layout.Serialization.XmlLayoutSerializer(DockManager);
        serializer.LayoutSerializationCallback += (_, args) =>
        {
            if (content.TryGetValue(args.Model.ContentId ?? "", out var value))
                args.Content = value;
            else
                args.Cancel = true;
        };
        return serializer;
    }

    internal void LoadWorkspace(string path)
    {
        suppressDocumentSelection = true;
        try
        {
            if (activeChartWorkspace != null) DetachChartPanels(activeChartWorkspace);
            CloseDocuments();
            var layout = System.Xml.Linq.XDocument.Load(path);
            bool pinned = (bool?)layout.Root?.Attribute("DendroRibbonPinned") ?? true;
            layout.Root?.Attribute("DendroRibbonPinned")?.Remove();
            LayoutSerializer().Deserialize(new StringReader(layout.ToString()));
            SetRibbonPinned(pinned);
            RebindLayout();
            documents.Clear();
            documentSelection.Clear();
        }
        finally
        {
            suppressDocumentSelection = false;
        }
    }

    private void RebindLayout()
    {
        var panes = DockManager.Layout.Descendents().OfType<LayoutAnchorable>().ToArray();
        ContentsPane = panes.First(p => p.ContentId == "contents");
        ToolsPane = panes.First(p => p.ContentId == "tools");
        HistoryPane = panes.First(p => p.ContentId == "history");
        RebindChartPanels(panes);
        Documents = DockManager.Layout.Descendents().OfType<LayoutDocumentPane>().First();
    }

    internal void SaveWorkspace(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            new AvalonDock.Layout.Serialization.XmlLayoutSerializer(DockManager).Serialize(
                temporary
            );
            var layout = System.Xml.Linq.XDocument.Load(temporary);
            layout.Root!.SetAttributeValue("DendroRibbonPinned", RibbonPinned);
            layout.Save(temporary);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closing)
            return;
        e.Cancel = true;
        if (closePending)
            return;
        closePending = true;
        // Even a completed confirmation task must leave WPF's Closing event before Close.
        Dispatcher.BeginInvoke(
            new Action(async () =>
            {
                try
                {
                    if (!await (CloseConfirmation?.Invoke() ?? MayReplace()))
                        return;
                    try
                    {
                        SaveWorkspace(WorkspacePath);
                    }
                    catch (Exception ex)
                    {
                        Log("Não foi possível salvar o layout: " + ex.Message);
                    }
                    closing = true;
                    suppressDocumentSelection = true;
                    Close();
                }
                finally
                {
                    closePending = false;
                }
            })
        );
    }
}
