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
    private readonly System.Windows.Threading.DispatcherTimer sourceAvailabilityTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool historyRefreshing;
    private bool sourceAvailabilityRefreshing;
    private readonly Dictionary<string, bool> sourceAvailability = new(StringComparer.OrdinalIgnoreCase);
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
    private GisView? gisView;
    private readonly Dictionary<string, LayoutDocument> documents = new();
    private readonly Dictionary<string, Action> documentSelection = new();
    private readonly Dictionary<string, bool> contentsExpansion = new(StringComparer.Ordinal);
    private bool resetContentsExpansion;
    private Point contentDragOrigin;
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
        ContentsTree.AllowDrop = true;
        ContentsTree.PreviewMouseLeftButtonDown += (_, e) => contentDragOrigin = e.GetPosition(ContentsTree);
        ContentsTree.PreviewMouseMove += ContentsTreeMouseMove;
        ContentsTree.DragOver += ContentsTreeDragOver;
        ContentsTree.Drop += ContentsTreeDrop;
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
        sourceAvailabilityTimer.Tick += async (_, _) => await RefreshSourceAvailability();
        Loaded += (_, _) => { if (!App.IsSelfTest) { historyRefreshTimer.Start(); sourceAvailabilityTimer.Start(); } };
        Closed += (_, _) => { historyRefreshTimer.Stop(); sourceAvailabilityTimer.Stop(); };
        using (var initial = new StringWriter())
        {
            new AvalonDock.Layout.Serialization.XmlLayoutSerializer(DockManager).Serialize(initial);
            defaultLayout = initial.ToString();
        }
        Session.Changed += Refresh;
        ShowTools();
        Refresh();
        Loaded += (_, _) => RestoreLayout();
        Loaded += async (_, _) => { if (!App.IsSelfTest) { await Task.Delay(5000); if (!closing) await CheckUpdates(false); } };
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
                    SetTool(processor);
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
            var b in new[] { SaveButton, SaveAsButton, SettingsButton, VolumeButton, GrowthButton, GISButton }
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

    private bool ContentReferenceExists(string path)
    {
        try
        {
            string resolved = Session.Resolve(path);
            // A DLB table may be a dataset reference inside the project bank.
            // Checking the physical container catches a moved/deleted source without
            // opening the bank or scanning its table catalog during tree rendering.
            return File.Exists(CompactBankFile.Location(resolved).File);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private bool ContentAvailable(string path)
    {
        if (sourceAvailability.TryGetValue(path, out bool available)) return available;
        available = ContentReferenceExists(path);
        sourceAvailability[path] = available;
        return available;
    }

    private async Task RefreshSourceAvailability()
    {
        if (sourceAvailabilityRefreshing || busy || closing || !Session.IsOpen) return;
        sourceAvailabilityRefreshing = true;
        string project = Session.FilePath;
        string[] paths = Session.Files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        try
        {
            var current = await Task.Run(() => paths.ToDictionary(path => path, ContentReferenceExists, StringComparer.OrdinalIgnoreCase));
            if (closing || !Session.IsOpen || !string.Equals(project, Session.FilePath, StringComparison.OrdinalIgnoreCase)) return;
            bool changed = current.Any(pair => !sourceAvailability.TryGetValue(pair.Key, out bool previous) || previous != pair.Value)
                || sourceAvailability.Keys.Any(key => !current.ContainsKey(key));
            sourceAvailability.Clear();
            foreach (var pair in current) sourceAvailability[pair.Key] = pair.Value;
            if (changed) RefreshTree();
        }
        finally { sourceAvailabilityRefreshing = false; }
    }

    private static string ContentsNodeKey(TreeViewItem node)
    {
        static string PathKey(string path) => path.Replace('\\', '/').ToUpperInvariant();
        return node.Tag switch
        {
            string path => "table:" + PathKey(path),
            ResultSheetReference result => "result:" + PathKey(result.Path) + "\0" + result.Sheet,
            ContentGroupReference group => "content-group:" + group.Type + ":" + group.Id,
            ContentGroupRootReference root => "content-group-root:" + root.Type,
            BasemapReference basemap => "basemap:" + basemap.Id,
            JsonObject chart => ChartView.Identity(chart),
            AnalysisReference analysis => "analysis:" + analysis.Entry.S("id"),
            AnalysisPlotReference plot => "analysis-plot:" + plot.Entry.S("id"),
            AnalysisTableReference table => "analysis-table:" + table.Entry.S("id"),
            ProcessingVersionReference run => "processing-version:" + run.Id,
            _ => "group:" + ShellVisuals.GetIcon(node) + ":" + node.Header,
        };
    }

    private static string? ContentGroupType(ProjectSession session, string path)
    {
        if (session.IsSpreadsheet(path)) return "spreadsheet";
        if (Path.GetExtension(path).Equals(".shp", StringComparison.OrdinalIgnoreCase)) return "vector";
        return null;
    }

    private static bool IsRasterPath(string path) => Path.GetExtension(path) is string extension
        && (extension.Equals(".tif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase));

    private IEnumerable<JsonObject> ContentGroups(string type) =>
        Session.Meta.Arr("content_groups").OfType<JsonObject>()
            .Where(group => group.S("type") == type);

    private string? GroupForContent(string path, string type) => ContentGroups(type)
        .FirstOrDefault(group => group.Arr("paths").Any(item =>
            string.Equals(item.Display(), path, StringComparison.OrdinalIgnoreCase)))?.S("id");

    private TreeViewItem CreateContentGroupNode(JsonObject group)
    {
        string id = group.S("id"), type = group.S("type");
        var node = ContentItem(group.S("name"), CommandIconKind.Folder);
        node.Tag = new ContentGroupReference(id, type);
        node.ToolTip = "Arraste arquivos deste tipo para o grupo, ou use o menu de contexto para movê-los.";
        node.ContextMenu = new ContextMenu();
        Menu(node, "Renomear grupo…", CommandIconKind.Rename, () => RenameContentGroup(id));
        Menu(node, "Excluir grupo…", CommandIconKind.Remove, () =>
        {
            if (!Ui.Confirm("Excluir grupo", $"O grupo ‘{group.S("name")}’ será removido. Os conteúdos voltarão para a lista de { (type == "spreadsheet" ? "planilhas" : "dados vetoriais")}.", "Excluir grupo", destructive: true))
                return Task.CompletedTask;
            Session.Meta.Arr("content_groups").Remove(group);
            Session.Touch(); RefreshTree();
            return Task.CompletedTask;
        });
        return node;
    }

    private void AddCreateGroupCommand(TreeViewItem root, string type)
    {
        root.Tag = new ContentGroupRootReference(type);
        root.ContextMenu ??= new ContextMenu();
        root.ContextMenu.Items.Add(new Separator());
        var create = new MenuItem
        {
            Header = type == "spreadsheet" ? "Criar grupo de planilhas…" : "Criar grupo de dados vetoriais…",
            Icon = ContentMenuIcon(CommandIconKind.Folder),
        };
        create.Click += async (_, e) => { e.Handled = true; await Execute(() => CreateContentGroup(type)); };
        root.ContextMenu.Items.Add(create);
    }

    private Task CreateContentGroup(string type)
    {
        var form = Ui.Form();
        form.Children.Add(Ui.Text(type == "spreadsheet" ? "Crie um grupo para organizar planilhas." : "Crie um grupo para organizar camadas vetoriais."));
        var name = Ui.Input();
        Ui.Field(form, "Nome do grupo", name);
        var dialog = Ui.Dialog("Novo grupo", form, 480, 250);
        var create = Ui.Button("Criar grupo", () =>
        {
            string label = name.Text.Trim();
            if (label.Length == 0) throw new InvalidOperationException("Informe um nome para o grupo.");
            if (ContentGroups(type).Any(group => string.Equals(group.S("name"), label, StringComparison.CurrentCultureIgnoreCase)))
                throw new InvalidOperationException("Já existe um grupo com esse nome nesta categoria.");
            Session.Meta.Arr("content_groups").Add(new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString("N"), ["type"] = type, ["name"] = label, ["paths"] = new JsonArray(),
            });
            Session.Touch(); RefreshTree(); dialog.DialogResult = true;
        });
        var cancel = Ui.Button("Cancelar", () => dialog.DialogResult = false);
        dialog.Content = Ui.WithToolbar(form, create, cancel);
        dialog.ShowDialog();
        return Task.CompletedTask;
    }

    private Task RenameContentGroup(string id)
    {
        var group = ContentGroups("spreadsheet").Concat(ContentGroups("vector")).FirstOrDefault(candidate => candidate.S("id") == id);
        if (group == null) return Task.CompletedTask;
        var form = Ui.Form();
        var name = Ui.Input(group.S("name"));
        Ui.Field(form, "Nome do grupo", name);
        var dialog = Ui.Dialog("Renomear grupo", form, 480, 240);
        var save = Ui.Button("Salvar nome", () =>
        {
            string label = name.Text.Trim();
            if (label.Length == 0) throw new InvalidOperationException("Informe um nome para o grupo.");
            if (ContentGroups(group.S("type")).Any(candidate => candidate.S("id") != id && string.Equals(candidate.S("name"), label, StringComparison.CurrentCultureIgnoreCase)))
                throw new InvalidOperationException("Já existe um grupo com esse nome nesta categoria.");
            group["name"] = label; Session.Touch(); RefreshTree(); dialog.DialogResult = true;
        });
        var cancel = Ui.Button("Cancelar", () => dialog.DialogResult = false);
        dialog.Content = Ui.WithToolbar(form, save, cancel);
        dialog.ShowDialog();
        return Task.CompletedTask;
    }

    private void MoveContentToGroup(string path, string? groupId, bool refresh = true)
    {
        string? type = ContentGroupType(Session, path);
        if (type == null) return;
        var groups = ContentGroups("spreadsheet").Concat(ContentGroups("vector")).ToArray();
        foreach (var group in groups)
        {
            var paths = group.Arr("paths");
            for (int i = paths.Count - 1; i >= 0; i--)
                if (string.Equals(paths[i].Display(), path, StringComparison.OrdinalIgnoreCase)) paths.RemoveAt(i);
        }
        var target = groups.FirstOrDefault(group => group.S("id") == groupId && group.S("type") == type);
        if (target != null && !target.Arr("paths").Any(item => string.Equals(item.Display(), path, StringComparison.OrdinalIgnoreCase)))
            target.Arr("paths").Add(path);
        Session.Touch();
        if (refresh) RefreshTree();
    }

    private void AddMoveToGroupMenu(TreeViewItem item, string path, string type)
    {
        var move = new MenuItem { Header = "Mover para grupo", Icon = ContentMenuIcon(CommandIconKind.Folder) };
        var ungroup = new MenuItem { Header = "Sem grupo", Icon = ContentMenuIcon(CommandIconKind.Ungroup) };
        ungroup.Click += async (_, e) => { e.Handled = true; await Execute(() => { MoveContentToGroup(path, null); return Task.CompletedTask; }); };
        move.Items.Add(ungroup);
        var groups = ContentGroups(type).ToArray();
        if (groups.Length > 0) move.Items.Add(new Separator());
        foreach (var group in groups)
        {
            var choice = new MenuItem { Header = group.S("name"), Icon = ContentMenuIcon(CommandIconKind.Folder), IsCheckable = true, IsChecked = GroupForContent(path, type) == group.S("id") };
            string id = group.S("id");
            choice.Click += async (_, e) => { e.Handled = true; await Execute(() => { MoveContentToGroup(path, id); return Task.CompletedTask; }); };
            move.Items.Add(choice);
        }
        item.ContextMenu!.Items.Add(move);
    }

    private static TreeViewItem? TreeItemFromSource(object? source)
    {
        var current = source as DependencyObject;
        while (current != null)
        {
            if (current is TreeViewItem item) return item;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void ContentsTreeMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        Point current = e.GetPosition(ContentsTree);
        if (Math.Abs(current.X - contentDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - contentDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (TreeItemFromSource(e.OriginalSource) is not { Tag: string path }
            || ContentGroupType(Session, path) == null) return;
        var data = new DataObject("Dendro.ContentPath", path);
        DragDrop.DoDragDrop(ContentsTree, data, DragDropEffects.Move);
    }

    private void ContentsTreeDragOver(object sender, DragEventArgs e)
    {
        object? target = TreeItemFromSource(e.OriginalSource)?.Tag;
        if (!e.Data.GetDataPresent("Dendro.ContentPath") || (target is not ContentGroupReference && target is not ContentGroupRootReference))
        { e.Effects = DragDropEffects.None; return; }
        string? pathType = ContentGroupType(Session, e.Data.GetData("Dendro.ContentPath") as string ?? "");
        string targetType = target switch { ContentGroupReference group => group.Type, ContentGroupRootReference root => root.Type, _ => "" };
        e.Effects = pathType == targetType ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void ContentsTreeDrop(object sender, DragEventArgs e)
    {
        object? target = TreeItemFromSource(e.OriginalSource)?.Tag;
        if (e.Data.GetData("Dendro.ContentPath") is not string path || (target is not ContentGroupReference && target is not ContentGroupRootReference)) return;
        string? groupId = target is ContentGroupReference group ? group.Id : null;
        MoveContentToGroup(path, groupId);
        e.Handled = true;
    }

    private void BuildContentsTree()
    {
        ContentsTree.Items.Clear();
        var sheets = ContentItem("Planilhas", CommandIconKind.Table);
        ContentsTree.Items.Add(sheets);
        var add = new MenuItem { Header = "Adicionar planilha…", Icon = ContentMenuIcon(CommandIconKind.Add) };
        add.SetBinding(UIElement.IsEnabledProperty, new System.Windows.Data.Binding(nameof(IsEnabled))
        {
            Source = ContentsTree,
        });
        add.Click += async (_, _) => await AddData();
        sheets.ContextMenu = new ContextMenu();
        sheets.ContextMenu.Items.Add(add);
        Menu(sheets, "Juntar tabelas…", CommandIconKind.Merge, () => OpenTableTool(false));
        Menu(sheets, "Parear medições…", CommandIconKind.Merge, () => OpenPairedMeasurementsTool());
        AddCreateGroupCommand(sheets, "spreadsheet");
        var db = ContentItem("Bancos de dados", CommandIconKind.Database);
        db.ContextMenu = new ContextMenu();
        Menu(db, "Adicionar banco DendroLab…", CommandIconKind.Database, AddBank);
        var shapes = ContentItem("Dados vetoriais", CommandIconKind.Shape);
        shapes.ContextMenu = new ContextMenu();
        AddCreateGroupCommand(shapes, "vector");
        var basemaps = ContentItem("Mapas base", CommandIconKind.Map);
        basemaps.ContextMenu = new ContextMenu();
        AddGISLayerMenu(basemaps);
        ContentsTree.Items.Add(db);
        ContentsTree.Items.Add(shapes);
        ContentsTree.Items.Add(basemaps);
        if (!Session.IsOpen)
            return;
        if (Session.Meta.S("gis_basemap") == "openstreetmap")
        {
            var basemap = ContentItem("OpenStreetMap", CommandIconKind.Map);
            basemap.Tag = new BasemapReference("openstreetmap");
            basemap.ToolTip = "Mapa base exibido sob os dados vetoriais no GIS.";
            basemap.ContextMenu = new ContextMenu();
            AddGISLayerMenu(basemap);
            basemaps.Items.Add(basemap);
        }
        var groupNodes = new Dictionary<string, TreeViewItem>(StringComparer.Ordinal);
        foreach (var group in ContentGroups("spreadsheet").Concat(ContentGroups("vector")))
        {
            var groupNode = CreateContentGroupNode(group);
            groupNodes[group.S("id")] = groupNode;
            (group.S("type") == "spreadsheet" ? sheets : shapes).Items.Add(groupNode);
        }
        TreeViewItem managedBank = db;
        string managedPath = Session.Meta.S("database_path");
        if (managedPath.Length > 0)
        {
            managedBank = ContentItem("Dados do projeto", CommandIconKind.Database);
            managedBank.ToolTip = "Tabelas e histórico salvos em " + Session.FilePath;
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
                IsRasterPath(path) ? CommandIconKind.Map
                    : Session.IsSpreadsheet(path) ? CommandIconKind.Table
                    : Session.IsManagedTable(path) ? CommandIconKind.Table
                    : TableStore.IsBank(path) ? CommandIconKind.Database
                    : CommandIconKind.Shape
            );
            item.Tag = path;
            bool missingSource = !ContentAvailable(path);
            ShellVisuals.SetMissing(item, missingSource);
            item.ToolTip = missingSource
                ? "Arquivo de origem não encontrado: " + Session.Resolve(path) + "\nUse o menu do botão direito para referenciá-lo novamente."
                : path;
            nodes[path] = item;
            bool processingOutput = Session.Meta.Arr("processing_results").Any(r => r.S("path") == path);
            if (!processingOutput)
            {
                string? groupType = ContentGroupType(Session, path);
                TreeViewItem parent = groupType == "spreadsheet" ? sheets
                    : groupType == "vector" ? shapes
                    : IsRasterPath(path) ? basemaps
                    : managedPath.Length > 0 && Session.Resolve(path).StartsWith(Session.Resolve(managedPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        ? managedBank : db;
                string? groupId = groupType == null ? null : GroupForContent(path, groupType);
                if (groupId != null && groupNodes.TryGetValue(groupId, out var groupNode)) parent = groupNode;
                parent.Items.Add(item);
            }
            item.ContextMenu = new ContextMenu();
            if (missingSource)
                AddRelinkMenu(item, path);
            if (Session.IsTabular(path) && !missingSource)
            {
                Menu(item, "Abrir tabela", CommandIconKind.Table, () => OpenTable(path));
                Menu(item, "Copiar para o projeto…", CommandIconKind.Copy, async () => {
                    string bank = Session.ProjectWorkFolder();
                    var options = Session.ExcelOptions(path);
                    string name = TableTools.SuggestedName(Path.GetFileNameWithoutExtension(path));
                    for (int n = 2; File.Exists(Path.Combine(bank, name + CompactBankReader.Extension))
                        || CompactBankFile.Exists(Session.Resolve(Session.ProjectReference(Path.Combine(bank, name + CompactBankReader.Extension)))); n++)
                        name = TableTools.SuggestedName(Path.GetFileNameWithoutExtension(path)) + "_" + n;
                    var result = await Task.Run(() => TableTools.Import(new(Session.Resolve(path), options.S("sheet"), (int)options.N("header_row", 1)), bank, name));
                    await Session.AddTableResult(result, path);
                    Log($"{result.Rows:N0} linhas copiadas para o arquivo do projeto.");
                });
                if (Session.IsTabular(path))
                    Menu(item, "Worksheet e cabeçalho…", CommandIconKind.Settings, () => ConfigureExcel(path));
                Menu(item, "Gráfico de dispersão…", CommandIconKind.Chart, () => OpenChart(path, false));
                Menu(item, "Estratificar…", CommandIconKind.Stratify, () => OpenTableTool(true, path));
                Menu(item, "Parear medições…", CommandIconKind.Merge, () => OpenPairedMeasurementsTool(path));
                Menu(item, "Gerar coluna…", CommandIconKind.CalculateColumn, () => OpenTableTool(false, path, true));
                Menu(item, "Isolation Forest…", CommandIconKind.Anomaly, () => OpenAnomalyTool(path));
                Menu(item, "Limpeza integrada…", CommandIconKind.IntegratedCleaning, () => OpenIntegratedCleaning(path));
                Menu(item, "Testes estatísticos…", CommandIconKind.Statistics, () => OpenStatistics(false, path));
            }
            string? itemGroupType = ContentGroupType(Session, path);
            if (itemGroupType == "vector" && !missingSource)
                Menu(item, "Abrir no GIS", CommandIconKind.Map, () => OpenGIS(path));
            if (IsRasterPath(path) && !missingSource)
                Menu(item, "Abrir no GIS", CommandIconKind.Map, () => OpenGIS(path));
            if ((itemGroupType is "spreadsheet" or "vector") && !processingOutput) AddMoveToGroupMenu(item, path, itemGroupType);
            item.ContextMenu.Items.Add(new Separator());
            Menu(item, "Renomear…", CommandIconKind.Rename, () => RenameContent(path));
            Menu(
                item,
                "Remover",
                CommandIconKind.Remove,
                async () =>
                {
                    int index = Session.Files.ToList().IndexOf(path);
                    string[] affected = Session.PreviewRemoveFile(index);
                    string[] descendants = affected.Where(removed => !SameProjectPath(removed, path)).ToArray();
                    bool deletesBankData = affected.Any(Session.IsManagedTable);
                    string message = deletesBankData
                        ? $"Remover {Session.ContentName(path)} do projeto também apagará seus dados do arquivo .dlb do projeto."
                        : $"Remover {Session.ContentName(path)} apenas retira este conteúdo do projeto. O arquivo de origem continuará no local original.";
                    if (descendants.Length > 0)
                        message += "\n\nTambém serão removidos os resultados dependentes:\n" +
                            string.Join("\n", descendants.Select(removed => "• " + Session.ContentName(removed)));
                    message += "\n\nDeseja continuar?";
                    if (!ConfirmRemoval(message)) return;
                    await Session.RemoveFile(index);
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
                bool missingChartSource = !ContentAvailable(path);
                ShellVisuals.SetMissing(node, missingChartSource);
                if (missingChartSource) node.ToolTip = "Arquivo de origem não encontrado: " + Session.Resolve(path);
                node.ContextMenu = new ContextMenu();
                if (missingChartSource) AddRelinkMenu(node, path);
                Menu(node, "Abrir gráfico", CommandIconKind.Chart, () => OpenChart(path, chart.B("fitting"), chart, false));
                Menu(node, "Regressões…", CommandIconKind.Regression, () => OpenChart(path, true, chart));
                Menu(node, "Fronteira de eficiência…", CommandIconKind.Frontier, () => {
                    SetTool(new FrontierView(this, Session, chart)); return Task.CompletedTask;
                });
                Menu(
                    node,
                    "ICA e IMA…",
                    CommandIconKind.Increments,
                    () =>
                    {
                        SetTool(new GrowthValidationView(this, Session, chart));
                        return Task.CompletedTask;
                    }
                );
                node.ContextMenu.Items.Add(new Separator());
                Menu(node, "Renomear…", CommandIconKind.Rename, () => RenameContent(path, chart));
                Menu(
                    node,
                    "Remover gráfico",
                    CommandIconKind.Remove,
                    () =>
                    {
                        string title = chart.S("title", "gráfico sem título");
                        if (!Ui.Confirm("Remover gráfico", $"O gráfico ‘{title}’ será removido do projeto e sua janela será fechada. Deseja continuar?", "Remover", destructive: true))
                            return Task.CompletedTask;
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
                    Menu(versionNode, "Revisar / aprovar / exportar…", CommandIconKind.ProcessingReview, () => OpenProcessingReview(result.S("id")));
                    bool hasLaterRevision = Session.Meta.Arr("processing_results").Any(r => r.S("parent_id") == result.S("id"));
                    bool preserved = result.S("status") == "aprovado" || result.B("current_approved");
                    var removeRevision = new MenuItem
                    {
                        Header = "Remover revisão…",
                        IsEnabled = !preserved && !hasLaterRevision,
                        ToolTip = preserved ? "Versões aprovadas são preservadas."
                            : hasLaterRevision ? "Remova primeiro as revisões posteriores."
                            : "Apaga os dados desta revisão do arquivo DLB do projeto.",
                        Icon = ContentMenuIcon(CommandIconKind.Remove),
                    };
                    removeRevision.Click += async (_, e) =>
                    {
                        e.Handled = true;
                        await Execute(() => RemoveProcessingRevision(result.S("id")));
                    };
                    versionNode.ContextMenu.Items.Add(new Separator());
                    versionNode.ContextMenu.Items.Add(removeRevision);
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
                    bool missingOutput = !ContentAvailable(output);
                    ShellVisuals.SetMissing(node, missingOutput);
                    if (missingOutput)
                    {
                        node.ToolTip = "Arquivo de origem não encontrado: " + Session.Resolve(output) + "\nUse o menu do botão direito para referenciá-lo novamente.";
                        AddRelinkMenu(node, output);
                    }
                    Menu(node, ProcessingReportView.Handles(name) ? "Abrir informações e gráficos" : "Abrir tabela",
                        ProcessingReportView.Handles(name) ? CommandIconKind.Chart : CommandIconKind.Table, () => OpenTable(output, name));
                    Menu(node, "Gráfico de dispersão…", CommandIconKind.Chart, () => OpenResultTool(reference, () => OpenChart(output, false,
                        new JsonObject { ["path"] = output, ["sheet"] = name, ["header_row"] = 1 })));
                    Menu(node, "Estratificar…", CommandIconKind.Stratify, () => OpenResultTool(reference, () => OpenTableTool(true, output)));
                    Menu(node, "Gerar coluna…", CommandIconKind.CalculateColumn, () => OpenResultTool(reference, () => OpenTableTool(false, output, true)));
                    Menu(node, "Isolation Forest…", CommandIconKind.Anomaly, () => OpenAnomalyTool(output, name));
                    Menu(node, "Limpeza integrada…", CommandIconKind.IntegratedCleaning, () => OpenIntegratedCleaning(output, name));
                    if (name is "DIRETO - PARCELAS" or "RESULTADO - PARCELAS")
                        Menu(node, "Auditar inventário…", CommandIconKind.InventoryAudit, () => OpenInventoryAudit(output, name));
                    Menu(node, "Testes estatísticos…", CommandIconKind.Statistics, () => OpenResultTool(reference, () => OpenStatistics(false, output)));
                    node.ContextMenu.Items.Add(new Separator());
                    Menu(node, "Remover tabela", CommandIconKind.Remove, async () => {
                        string[] descendants = Session.PreviewRemoveResultTable(output, name);
                        string bank = Session.IsManagedTable(output)
                            ? "arquivo .dlb do projeto"
                            : $"arquivo .dlb de origem ‘{Path.GetFileName(Session.Resolve(output))}’";
                        string message = $"Excluir a tabela ‘{name}’ apagará seus dados do {bank}.";
                        if (descendants.Length > 0)
                            message += "\n\nTambém serão removidos os resultados dependentes:\n" +
                                string.Join("\n", descendants.Select(removed => "• " + Session.ContentName(removed)));
                        message += "\n\nDeseja continuar?";
                        if (!ConfirmRemoval(message)) return;
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
        var node = ContentItem(analysis.S("title"), AnalysisIcon(analysis));
        node.Tag = new AnalysisReference(analysis);
        node.ContextMenu = new ContextMenu();
        Menu(node, "Abrir análise", AnalysisIcon(analysis), () => AnalysisDocuments.OpenSaved(this, Session, analysis));
        if (analysis["definition"].S("kind") == "isolation_forest")
        {
            var table = ContentItem("Tabela de resultados · escores e classificação", CommandIconKind.Table);
            table.Tag = new AnalysisTableReference(analysis);
            table.ContextMenu = new ContextMenu();
            Menu(table, "Abrir tabela", CommandIconKind.Table, () => AnalysisDocuments.OpenResultTable(this, Session, analysis));
            node.Items.Add(table);
            Menu(node, "Abrir tabela de resultados", CommandIconKind.Table, () => AnalysisDocuments.OpenResultTable(this, Session, analysis));
        }
        if (analysis["definition"].S("kind") != "growth_comparison"
            && (Dendro.Core.Configuration.ProjectSnapshots.HasPlot(analysis) || analysis["definition"].S("kind") == "increments"))
        {
            var graph = ContentItem("Gráfico · " + analysis.S("title"), CommandIconKind.Chart);
            graph.Tag = new AnalysisPlotReference(analysis);
            graph.ContextMenu = new ContextMenu();
            Menu(graph, "Abrir gráfico", CommandIconKind.Chart, () => AnalysisDocuments.OpenPlot(this, Session, analysis));
            node.Items.Add(graph);
            Menu(node, "Abrir gráfico", CommandIconKind.Chart, () => AnalysisDocuments.OpenPlot(this, Session, analysis));
        }
        Menu(
            node,
            "Remover",
            CommandIconKind.Remove,
            () =>
            {
                string title = analysis.S("title", "análise sem título");
                if (!Ui.Confirm("Remover análise", $"A análise ‘{title}’ será removida do projeto e suas janelas abertas serão fechadas. Deseja continuar?", "Remover", destructive: true))
                    return Task.CompletedTask;
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

    private static CommandIconKind AnalysisIcon(JsonObject analysis) => analysis["definition"].S("kind") switch
    {
        "isolation_forest" => CommandIconKind.Anomaly,
        "inventory_audit" => CommandIconKind.InventoryAudit,
        "growth_comparison" => CommandIconKind.CurveComparison,
        "increments" => CommandIconKind.Increments,
        _ => CommandIconKind.Statistics,
    };

    private static IEnumerable<TreeViewItem> ContentsDescendants(ItemsControl parent)
    {
        foreach (var child in parent.Items.OfType<TreeViewItem>())
        {
            yield return child;
            foreach (var descendant in ContentsDescendants(child)) yield return descendant;
        }
    }

    private static CommandIcon ContentMenuIcon(CommandIconKind kind) => new()
    {
        Kind = kind,
        Width = 17,
        Height = 17,
    };

    private void Menu(TreeViewItem item, string title, CommandIconKind iconKind, Func<Task> action)
    {
        var m = new MenuItem
        {
            Header = title,
            Icon = ContentMenuIcon(iconKind),
        };
        if (iconKind == CommandIconKind.Rename && title == "Renomear…")
            m.InputGestureText = "F2";
        m.Click += async (_, e) =>
        {
            e.Handled = true;
            await Execute(action);
        };
        item.ContextMenu!.Items.Add(m);
    }

    private void AddRelinkMenu(TreeViewItem node, string path)
    {
        var relink = new MenuItem
        {
            Header = "Referenciar arquivo novamente…",
            Icon = ContentMenuIcon(CommandIconKind.Open),
            ToolTip = "Selecione a planilha ou o arquivo de origem no novo local.",
        };
        relink.Click += async (_, e) => { e.Handled = true; await RelinkContent(path); };
        node.ContextMenu?.Items.Insert(0, relink);
        node.ContextMenu?.Items.Insert(1, new Separator());
    }

    private void AddGISLayerMenu(TreeViewItem node)
    {
        var addLayer = new MenuItem
        {
            Header = "Adicionar raster TIFF ou shapefile…",
            Icon = ContentMenuIcon(CommandIconKind.Add),
            ToolTip = "Selecione um raster GeoTIFF (.tif/.tiff) ou um vetor shapefile (.shp).",
        };
        addLayer.Click += async (_, e) => { e.Handled = true; await AddGISLayerFiles(); };
        node.ContextMenu?.Items.Add(addLayer);
    }

    private async Task AddGISLayerFiles()
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Adicionar raster ou shapefile ao mapa",
            Filter = "Raster GeoTIFF|*.tif;*.tiff|Shapefile vetorial|*.shp|Todos os formatos aceitos|*.tif;*.tiff;*.shp",
            Multiselect = true,
            CheckFileExists = true,
        };
        if (picker.ShowDialog(this) != true) return;
        string[] selected = picker.FileNames.Select(Path.GetFullPath).ToArray();
        foreach (string path in selected)
            if (!IsRasterPath(path) && !Path.GetExtension(path).Equals(".shp", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Selecione um raster TIFF ou um shapefile (.shp).");
        await Execute(async () =>
        {
            foreach (string path in selected)
                if (!Session.Files.Contains(path, StringComparer.OrdinalIgnoreCase)) Session.AddFile(path);
            await OpenGIS(selected.LastOrDefault());
            Log($"{selected.Length:N0} camada(s) GIS adicionada(s) ao projeto.");
        });
    }

    private async Task RelinkContent(string oldPath)
    {
        string extension = Path.GetExtension(oldPath);
        string filter = Session.IsSpreadsheet(oldPath) ? "Planilhas Excel|*.xlsx;*.xls|Todos os arquivos|*.*"
            : extension.Equals(".shp", StringComparison.OrdinalIgnoreCase) ? "Shapefiles|*.shp|Todos os arquivos|*.*"
            : extension.Equals(".dlb", StringComparison.OrdinalIgnoreCase) ? "Bancos DendroLab|*.dlb|Todos os arquivos|*.*"
            : extension.Length > 0 ? $"Arquivos {extension.ToUpperInvariant()}|*{extension}|Todos os arquivos|*.*"
            : "Todos os arquivos|*.*";
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Localizar arquivo de origem",
            Filter = filter,
            FileName = Path.GetFileName(oldPath),
            CheckFileExists = true,
        };
        if (picker.ShowDialog(this) != true) return;
        string newPath = Path.GetFullPath(picker.FileName);
        await Execute(() =>
        {
            foreach (var chart in Session.Meta.Arr("charts").OfType<JsonObject>()
                .Where(chart => SameProjectPath(chart.S("path"), oldPath)).ToArray())
                CloseDocument(ChartView.Identity(chart));
            foreach (string documentId in documents.Keys.Where(id =>
                id.Equals("table:" + oldPath, StringComparison.OrdinalIgnoreCase)
                || id.StartsWith("table:" + oldPath + ":", StringComparison.OrdinalIgnoreCase)).ToArray())
                CloseDocument(documentId);
            Session.RelinkFile(oldPath, newPath);
            sourceAvailability.Remove(oldPath);
            sourceAvailability[newPath] = true;
            Log($"Arquivo referenciado novamente: {newPath}");
            return Task.CompletedTask;
        });
    }

    private bool ConfirmRemoval(string message) => Ui.Confirm("Confirmar remoção", message, "Remover", destructive: true);

    private async Task RemoveProcessingRevision(string id)
    {
        await Session.RefreshHistory(true);
        var revision = Session.Meta.Arr("processing_results").OfType<JsonObject>().SingleOrDefault(record => record.S("id") == id)
            ?? throw new InvalidOperationException("Esta revisão não existe mais no histórico atualizado.");
        if (revision.S("status") == "aprovado" || revision.B("current_approved"))
            throw new InvalidOperationException("Uma versão aprovada é preservada e não pode ser removida.");
        if (Session.Meta.Arr("processing_results").Any(record => record.S("parent_id") == id))
            throw new InvalidOperationException("Esta versão tem revisões posteriores. Remova primeiro as revisões descendentes.");
        string message = $"Remover {revision.S("inventory_type")} V{revision.N("number")} e apagar os dados desta revisão do arquivo DLB do projeto? Esta ação não pode ser desfeita.";
        if (!ConfirmRemoval(message)) return;
        await Session.RemoveProcessingRevision(revision, OperationCancellation);
        CloseDocuments();
        processor = null;
        ShowTools();
        Log($"Revisão removida: {id}");
    }

    private static bool SameProjectPath(string first, string second) =>
        string.Equals(first.Replace('/', '\\'), second.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);

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
            if (!ReferenceEquals(existing.Content, content))
            {
                object? previousContent = existing.Content;
                // Release the old WPF logical child before disposing it or attaching its replacement.
                existing.Content = null;
                if (previousContent is IDisposable disposable) disposable.Dispose();
                existing.Content = content;
            }
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
        var path = Ui.PickFile("Projeto DendroLab|*.dlb");
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
                path = Ui.SaveFile("Projeto DendroLab|*.dlb", "Projeto.dlb");
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
        // Searching should filter the tool list without replacing an open editor/form.
        if (IsInitialized && ToolContent?.Content is ListBox)
            ShowTools();
    }

    internal void ShowTools()
    {
        if (ToolContent == null)
            return;
        ToolSearchPanel.Visibility = Visibility.Visible;
        var list = new ListBox
        {
            Margin = new Thickness(12, 6, 12, 12),
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
        };
        bool MatchesToolSearch(string name) =>
            name.Contains(ToolSearch.Text, StringComparison.CurrentCultureIgnoreCase)
            || name == "Testes estatísticos"
                && "identidade Graybill regressão Welch Student t F ANOVA Levene Brown Forsythe variâncias médias".Contains(
                    ToolSearch.Text, StringComparison.CurrentCultureIgnoreCase);
        var toolGroups = new (string Title, string[] Items)[]
        {
            ("Preparar e revisar dados", ["Processador", "Revisão de inventário", "Auditoria pós-processamento", "Juntar tabelas", "Parear medições", "Gerar coluna", "Estratificar", "Limpeza integrada", "Ativo biológico"]),
            ("Gráficos e modelos", ["Gráficos", "Regressões", "Fronteira de eficiência"]),
            ("Comparar e validar", ["Comparar curvas de produção", "ICA e IMA", "Testes estatísticos", "Isolation Forest"]),
        };
        foreach (var (groupTitle, groupTools) in toolGroups)
        {
            var visibleTools = groupTools.Where(MatchesToolSearch).ToArray();
            if (visibleTools.Length == 0) continue;
            if (string.IsNullOrWhiteSpace(ToolSearch.Text))
                list.Items.Add(new ListBoxItem
                {
                    Content = new TextBlock { Text = groupTitle, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(104, 122, 137)), FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(3, 8, 0, 2) },
                    IsEnabled = false, IsHitTestVisible = false, Focusable = false,
                    Padding = new Thickness(5, 0, 5, 0), BorderThickness = new Thickness(0),
                    Background = System.Windows.Media.Brushes.Transparent,
                });
            foreach (var tool in visibleTools)
            {
                var entry = new ListBoxItem
                {
                    Content = tool,
                    Style = (Style)FindResource("ToolCardItem"),
                    Tag = tool,
                    ToolTip = tool switch
                    {
                        "Processador" => "Processe inventários em lote e gere resultados por árvore, parcela e nível do projeto.",
                        "Revisão de inventário" => "Revise fatores, crie versões corrigidas, aprove resultados e exporte a versão vigente.",
                        "Auditoria pós-processamento" => "Confira resultados processados com dados de origem e sinalize inconsistências para revisão.",
                        "Ativo biológico" => "Vincule a medição mais recente do processamento à tabela base do inventário.",
                        "Parear medições" => "Organize medições repetidas em colunas por ano, usando as chaves de árvore ou parcela configuradas no projeto.",
                        "Limpeza integrada" => "Revise pontos de vários gráficos, remova observações e grave uma tabela limpa.",
                        "Gráficos" => "Crie um gráfico e confira a prévia antes de ajustar modelos.",
                        "Regressões" => "Ajuste modelos em um gráfico já criado. Também há um atalho no editor de gráficos.",
                        "Comparar curvas de produção" => "Escolha uma curva base e compare uma ou mais candidatas; a prévia mostra as séries selecionadas.",
                        "Testes estatísticos" => "Execute testes de médias, variâncias e identidade de modelos.",
                        _ => null,
                    },
                    IsEnabled = tool switch
                    {
                        "Parear medições" => Session.IsOpen,
                        "Regressões" or "Fronteira de eficiência" => Session.IsOpen && Session.Meta.Arr("charts").Count > 0,
                        _ => true,
                    },
                };
                if (entry.ToolTip is string description)
                    System.Windows.Automation.AutomationProperties.SetHelpText(entry, description);
                ShellVisuals.SetIcon(entry, tool switch
                {
                    "Processador" => CommandIconKind.Processor,
                    "Revisão de inventário" => CommandIconKind.ProcessingReview,
                    "Auditoria pós-processamento" => CommandIconKind.InventoryAudit,
                    "Ativo biológico" => CommandIconKind.BiologicalAsset,
                    "Gráficos" => CommandIconKind.Chart,
                    "Regressões" => CommandIconKind.Regression,
                    "Fronteira de eficiência" => CommandIconKind.Frontier,
                    "Juntar tabelas" => CommandIconKind.Merge,
                    "Parear medições" => CommandIconKind.Merge,
                    "Gerar coluna" => CommandIconKind.CalculateColumn,
                    "ICA e IMA" => CommandIconKind.Increments,
                    "Comparar curvas de produção" => CommandIconKind.CurveComparison,
                    "Isolation Forest" => CommandIconKind.Anomaly,
                    "Limpeza integrada" => CommandIconKind.IntegratedCleaning,
                    "Testes estatísticos" => CommandIconKind.Statistics,
                    _ => CommandIconKind.Stratify,
                });
                list.Items.Add(entry);
            }
        }
        list.SelectionChanged += async (_, _) =>
        {
            if (list.SelectedItem is not ListBoxItem i)
                return;
            if ((string)i.Tag == "Processador")
            {
                processor = new ProcessorView(this, Session);
                SetTool(processor);
            }
            else if ((string)i.Tag == "Revisão de inventário")
                await Execute(() => OpenProcessingReview());
            else if ((string)i.Tag is "Juntar tabelas" or "Estratificar")
                await Execute(() => OpenTableTool((string)i.Tag == "Estratificar"));
            else if ((string)i.Tag == "Parear medições")
                await Execute(() => OpenPairedMeasurementsTool());
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
            else if ((string)i.Tag == "Testes estatísticos")
                await Execute(() => OpenStatistics(false));
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
        ToolSearchPanel.Visibility = Visibility.Collapsed;
        ToolContent.Content = content;
    }

    internal async Task OpenTableTool(bool stratify, string? path = null, bool calculateColumns = false)
    {
        var view = new TableToolsView(this, Session, stratify, path, calculateColumns);
        SetTool(view);
        await view.Initialize();
    }

    internal async Task OpenPairedMeasurementsTool(string? path = null)
    {
        var view = new PairedMeasurementsView(this, Session);
        SetTool(view);
        await view.Initialize(path);
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

    private async void GISClick(object sender, RoutedEventArgs e) => await Execute(() => OpenGIS());

    private async Task OpenGIS(string? selectedVector = null)
    {
        if (!Session.IsOpen)
            throw new InvalidOperationException("Abra um projeto para visualizar os dados vetoriais no GIS.");
        if (Session.Meta.S("gis_basemap") != "openstreetmap")
        {
            Session.Meta["gis_basemap"] = "openstreetmap";
            Session.Touch();
        }
        if (gisView != null && documents.TryGetValue("gis", out var existing) && ReferenceEquals(existing.Content, gisView))
        {
            existing.IsActive = true;
            if (selectedVector == null || !gisView.HasLayer(selectedVector)) await gisView.Initialize(selectedVector);
            else gisView.FocusLayer(selectedVector);
            return;
        }
        var view = new GisView(Session);
        gisView = view;
        var document = Document("gis", "GIS · Mapa", view);
        document.Closed += (_, _) => { if (ReferenceEquals(gisView, view)) gisView = null; };
        await view.Initialize(selectedVector);
    }

    internal DockPanel ToolHeader(string title)
    {
        var header = new DockPanel { LastChildFill = true };
        var back = Ui.Button("", ShowTools);
        back.Style = (Style)FindResource("ToolBackButton");
        back.ToolTip = "Voltar às ferramentas";
        System.Windows.Automation.AutomationProperties.SetName(back, "Voltar às ferramentas");
        back.Content = new CommandIcon { Kind = CommandIconKind.ArrowLeft, Width = 18, Height = 18 };
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
    private sealed record ContentGroupReference(string Id, string Type);
    private sealed record ContentGroupRootReference(string Type);
    private sealed record BasemapReference(string Id);

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
            throw new InvalidOperationException("Crie um gráfico antes de abrir Regressões.");
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
            else if (i.Tag is BasemapReference)
                await Execute(() => OpenGIS());
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
