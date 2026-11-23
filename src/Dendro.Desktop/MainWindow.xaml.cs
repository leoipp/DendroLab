using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using AvalonDock.Controls;
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
    private readonly System.Windows.Threading.DispatcherTimer progressHideTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly System.Windows.Threading.DispatcherTimer historyRefreshTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly System.Windows.Threading.DispatcherTimer sourceAvailabilityTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly System.Windows.Threading.DispatcherTimer contentSearchTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly System.Windows.Threading.DispatcherTimer toolSearchTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
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
    private readonly Dictionary<string, GisView> gisViews = new(StringComparer.Ordinal);
    private Popup? gisSourcePopup;
    private FrameworkElement? gisSourcePopupSurface;
    private readonly System.Windows.Threading.DispatcherTimer gisSourceCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly Dictionary<string, LayoutDocument> documents = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<LayoutAnchorablePaneControl, Style> paneTabStyles = new();
    private readonly Dictionary<string, Action> documentSelection = new();
    private Button? previousDocumentButton, nextDocumentButton;
    private readonly Dictionary<string, bool> contentsExpansion = new(StringComparer.Ordinal);
    private bool resetContentsExpansion;
    private Point contentDragOrigin;
    private bool chartFullScreen;
    private bool fullScreenContentsHidden, fullScreenToolsHidden;
    private Visibility fullScreenRibbonVisibility, fullScreenStatusVisibility;
    private WindowState fullScreenWindowState;
    internal bool IsChartFullScreen => chartFullScreen;
    internal event Action<bool>? ChartFullScreenChanged;
    private readonly HashSet<string> selectedContentPaths = new(StringComparer.OrdinalIgnoreCase);
    private string? contentSelectionAnchorPath;
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
        ConfigureGisSourceMenu();
        gisSourceCloseTimer.Tick += (_, _) =>
        {
            gisSourceCloseTimer.Stop();
            if (!GISButton.IsMouseOver && gisSourcePopupSurface?.IsMouseOver != true && gisSourcePopup != null)
                gisSourcePopup.IsOpen = false;
        };
        DockManager.LayoutUpdated += (_, _) =>
        {
            var style = (Style)((Style)DockManager.Resources["StackedPaneStyle"]).Setters.OfType<Setter>().Single().Value;
            foreach (var pane in FindVisualChildren<LayoutAnchorablePaneControl>(DockManager))
            {
                var applied = paneTabStyles.GetValue(pane, control =>
                {
                    // Preserve AvalonDock's model/content and selection bindings when
                    // changing appearance. Dock/auto-hide recreates these containers.
                    var combined = new Style(typeof(TabItem), control.ItemContainerStyle);
                    foreach (var setter in style.Setters) combined.Setters.Add(setter);
                    return combined;
                });
                if (!ReferenceEquals(pane.ItemContainerStyle, applied)) pane.ItemContainerStyle = applied;
            }
            // Start tabs beyond the panel's rounded corner, so the first selected
            // tab joins the straight segment rather than intersecting the arc.
            var tabInset = new Thickness(12, 0, 12, 0);
            foreach (var tabs in FindVisualChildren<AnchorablePaneTabPanel>(DockManager))
                if (tabs.Margin != tabInset) tabs.Margin = tabInset;
        };
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(MinWidth, workArea.Width * 0.94));
        Height = Math.Min(Height, Math.Max(MinHeight, workArea.Height * 0.92));
        ContentsTree.AllowDrop = true;
        ContentsTree.PreviewMouseLeftButtonDown += ContentsTreeMouseDown;
        ContentsTree.PreviewMouseRightButtonDown += ContentsTreeRightMouseDown;
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
        contentSearchTimer.Tick += (_, _) => { contentSearchTimer.Stop(); RefreshTree(); };
        toolSearchTimer.Tick += (_, _) => { toolSearchTimer.Stop(); if (ToolContent?.Content is ListBox) ShowTools(); };
        Loaded += (_, _) => { if (!App.IsSelfTest) { historyRefreshTimer.Start(); sourceAvailabilityTimer.Start(); } };
        Closed += (_, _) => { historyRefreshTimer.Stop(); sourceAvailabilityTimer.Stop(); };
        Closed += (_, _) => { contentSearchTimer.Stop(); toolSearchTimer.Stop(); };
        using (var initial = new StringWriter())
        {
            // Keep the history available from Exibir, but out of the way in a fresh workspace.
            HistoryPane.Hide();
            new AvalonDock.Layout.Serialization.XmlLayoutSerializer(DockManager).Serialize(initial);
            defaultLayout = initial.ToString();
        }
        Session.Changed += Refresh;
        ShowTools();
        Refresh();
        Loaded += (_, _) => RestoreLayout();
        Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(AttachDocumentNavigator));
        Loaded += async (_, _) => { if (!App.IsSelfTest) { await Task.Delay(5000); if (!closing) await CheckUpdates(false); } };
        CommandBindings.Add(
            new CommandBinding(ApplicationCommands.Save, async (_, _) => await SaveProject())
        );
        InputBindings.Add(new KeyBinding(ApplicationCommands.Save, Key.S, ModifierKeys.Control));
        PreviewKeyDown += (_, e) => {
            if (e.Key == Key.K && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            { ToolsPane.Show(); ShowTools(); ToolSearch.Focus(); e.Handled = true; }
            else if (e.Key == Key.L && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            { ContentsPane.Show(); ContentSearch.Focus(); e.Handled = true; }
            else if (e.Key == Key.Escape && chartFullScreen)
            { ExitChartFullScreen(); e.Handled = true; }
            else if (e.Key == Key.F1 && Keyboard.Modifiers == ModifierKeys.Control)
            { SetRibbonPinned(!RibbonPinned); e.Handled = true; }
            else if (e.Key == Key.F1 && Keyboard.Modifiers == ModifierKeys.None)
            { new ManualWindow { Owner = this }.ShowDialog(); e.Handled = true; }
            else if (e.Key == Key.Right && (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt)
                || Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)))
            { _ = NavigateSavedChart(1); e.Handled = true; }
            else if (e.Key == Key.Left && (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt)
                || Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)))
            { _ = NavigateSavedChart(-1); e.Handled = true; }
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

    internal void ToggleChartFullScreen()
    {
        if (chartFullScreen) { ExitChartFullScreen(); return; }
        fullScreenContentsHidden = ContentsPane.IsHidden;
        fullScreenToolsHidden = ToolsPane.IsHidden;
        fullScreenRibbonVisibility = RibbonHost.Visibility;
        fullScreenStatusVisibility = StatusBarHost.Visibility;
        fullScreenWindowState = WindowState;
        if (!ContentsPane.IsHidden) ContentsPane.Hide();
        if (!ToolsPane.IsHidden) ToolsPane.Hide();
        RibbonHost.Visibility = Visibility.Collapsed;
        StatusBarHost.Visibility = Visibility.Collapsed;
        WindowState = WindowState.Maximized;
        chartFullScreen = true;
        ChartFullScreenChanged?.Invoke(true);
    }

    internal void ExitChartFullScreen()
    {
        if (!chartFullScreen) return;
        chartFullScreen = false;
        RibbonHost.Visibility = fullScreenRibbonVisibility;
        StatusBarHost.Visibility = fullScreenStatusVisibility;
        if (!fullScreenContentsHidden) ContentsPane.Show();
        if (!fullScreenToolsHidden) ToolsPane.Show();
        WindowState = fullScreenWindowState;
        ChartFullScreenChanged?.Invoke(false);
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
            var b in new[] { SaveButton, SaveAsButton, CompactButton, SettingsButton, TaperButton, VolumeButton, GrowthButton, SiteIndexButton, SavedRegressionsButton, GISButton }
        )
            b.IsEnabled = Session.IsOpen && !busy;
        ToolContent.IsEnabled = Session.IsOpen && !busy;
        ContentsTree.IsEnabled = Session.IsOpen && !busy;
        AddDataButton.IsEnabled = Session.IsOpen && !busy;
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
            selectedContentPaths.Clear();
            contentSelectionAnchorPath = null;
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
        UpdateContentSelectionVisuals();
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

    internal void RevealAddedContents(IEnumerable<string> paths)
    {
        var added = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (added.Count == 0) return;
        // A previous search or collapsed folder must not hide a newly added file.
        ContentSearch.Clear();
        contentSearchTimer.Stop();
        RefreshTree();
        ContentsPane.Show();
        TreeViewItem? selected = null;
        foreach (var node in ContentsDescendants(ContentsTree).Where(node => node.Tag is string path && added.Contains(path)))
        {
            for (var parent = ItemsControl.ItemsControlFromItemContainer(node) as TreeViewItem; parent != null;
                parent = ItemsControl.ItemsControlFromItemContainer(parent) as TreeViewItem)
            {
                parent.IsExpanded = true;
                contentsExpansion[ContentsNodeKey(parent)] = true;
            }
            selected = node;
        }
        if (selected != null)
        {
            selected.IsSelected = true;
            selected.BringIntoView();
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
            GisMapReference map => "gis-map:" + map.Id,
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
            || extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".asc", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.ChangeExtension(path, ".pgw"))
                && File.Exists(Path.ChangeExtension(path, ".prj")));

    private IEnumerable<JsonObject> ContentGroups(string type) =>
        Session.Meta.Arr("content_groups").OfType<JsonObject>()
            .Where(group => group.S("type") == type);

    private static bool IsGisLayer(string path) => Path.GetExtension(path).Equals(".shp", StringComparison.OrdinalIgnoreCase) || IsRasterPath(path);

    private void EnsureLegacyGisMaps()
    {
        if (!Session.IsOpen || Session.Meta.Arr("gis_maps").OfType<JsonObject>().Any()) return;
        string[] paths = Session.Files.Where(IsGisLayer).ToArray();
        if (paths.Length == 0 && Session.Meta.S("gis_basemap") != "openstreetmap") return;
        var map = new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString("N"),
            ["name"] = "Mapa 1",
            ["basemap"] = Session.Meta.S("gis_basemap", "openstreetmap"),
            ["layers"] = new JsonArray(paths.Select(path => (JsonNode?)JsonValue.Create(path)).ToArray()),
        };
        Session.Meta.Arr("gis_maps").Add(map);
        Dispatcher.BeginInvoke(new Action(Session.Touch));
    }

    private JsonObject? FindGisMap(string id) => Session.Meta.Arr("gis_maps").OfType<JsonObject>()
        .FirstOrDefault(map => map.S("id") == id);

    private string? GisMapForLayer(string path) => Session.Meta.Arr("gis_maps").OfType<JsonObject>()
        .FirstOrDefault(map => map.Arr("layers").Any(layer => string.Equals(layer?.GetValue<string>(), path, StringComparison.OrdinalIgnoreCase)))?.S("id");

    private string NextGisMapName()
    {
        int next = Session.Meta.Arr("gis_maps").OfType<JsonObject>().Select(map =>
        {
            string name = map.S("name");
            return name.StartsWith("Mapa ", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(name.AsSpan(5), out int number) ? number : 0;
        }).DefaultIfEmpty(0).Max() + 1;
        return $"Mapa {next}";
    }

    private JsonObject AddGisMap(string sourceId)
    {
        var map = new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString("N"), ["name"] = NextGisMapName(), ["basemap"] = sourceId, ["layers"] = new JsonArray(),
        };
        Session.Meta.Arr("gis_maps").Add(map);
        Session.Touch();
        return map;
    }

    private string EnsureGisMapForLayer(string path)
    {
        string? mapId = GisMapForLayer(path);
        if (mapId != null) return mapId;
        var map = Session.Meta.Arr("gis_maps").OfType<JsonObject>().LastOrDefault() ?? AddGisMap("openstreetmap");
        map.Arr("layers").Add(JsonValue.Create(path));
        Session.Touch();
        return map.S("id");
    }

    private void ConfigureGisSourceMenu()
    {
        var options = new StackPanel { MinWidth = 220 };
        foreach (var source in GisBasemapSource.Available)
        {
            var option = new Button
            {
                Content = source.Name, HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 6, 14, 6), MinHeight = 34, Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent, Tag = source.Id,
            };
            option.MouseEnter += (_, _) => gisSourceCloseTimer.Stop();
            option.Click += async (_, _) =>
            {
                if (gisSourcePopup != null) gisSourcePopup.IsOpen = false;
                await Execute(() => CreateGISMap(source.Id));
            };
            options.Children.Add(option);
        }
        var surface = new Border
        {
            Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(4),
            Child = options, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 3, Opacity = 0.2 },
        };
        surface.MouseEnter += (_, _) => gisSourceCloseTimer.Stop();
        surface.MouseLeave += (_, _) => ScheduleGisSourceMenuClose();
        gisSourcePopupSurface = surface;
        gisSourcePopup = new Popup
        {
            PlacementTarget = GISButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = true, AllowsTransparency = true, Child = surface, VerticalOffset = -2,
        };
    }

    private void ShowGisSourceMenu()
    {
        if (!Session.IsOpen || gisSourcePopup == null) return;
        gisSourceCloseTimer.Stop();
        gisSourcePopup.IsOpen = true;
    }

    private void GISButtonMouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => ShowGisSourceMenu();

    private void GISButtonMouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => ScheduleGisSourceMenuClose();

    private void ScheduleGisSourceMenuClose()
    {
        gisSourceCloseTimer.Stop();
        gisSourceCloseTimer.Start();
    }

    private async Task CreateGISMap(string sourceId)
    {
        if (!Session.IsOpen) throw new InvalidOperationException("Abra um projeto antes de criar um mapa base.");
        var map = AddGisMap(sourceId);
        RefreshTree();
        await OpenGIS(map.S("id"));
    }

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
            if (!Ui.Confirm("Excluir grupo", $"O grupo '{group.S("name")}' será removido. Os conteúdos voltarão para a lista de { (type == "spreadsheet" ? "planilhas" : "dados vetoriais")}.", "Excluir grupo", destructive: true))
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
        var name = Ui.Input();
        Ui.Field(form, "Nome do grupo", name);
        Window dialog = null!;
        var create = Ui.IconButton("Criar grupo", CommandIconKind.Add, () =>
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
        var cancel = Ui.IconButton("Cancelar", CommandIconKind.ClearSelection, () => dialog.DialogResult = false);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(10, 6, 12, 8),
        };
        actions.Children.Add(create);
        actions.Children.Add(cancel);
        var content = new DockPanel { Background = Brushes.White };
        DockPanel.SetDock(actions, Dock.Bottom);
        content.Children.Add(actions);
        content.Children.Add(form);
        dialog = Ui.Dialog("Novo grupo", content, 420, 190);
        dialog.MinWidth = 420;
        dialog.MinHeight = 190;
        dialog.ResizeMode = ResizeMode.NoResize;
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
        Window dialog = null!;
        var save = Ui.Button("Salvar nome", () =>
        {
            string label = name.Text.Trim();
            if (label.Length == 0) throw new InvalidOperationException("Informe um nome para o grupo.");
            if (ContentGroups(group.S("type")).Any(candidate => candidate.S("id") != id && string.Equals(candidate.S("name"), label, StringComparison.CurrentCultureIgnoreCase)))
                throw new InvalidOperationException("Já existe um grupo com esse nome nesta categoria.");
            group["name"] = label; Session.Touch(); RefreshTree(); dialog.DialogResult = true;
        });
        var cancel = Ui.Button("Cancelar", () => dialog.DialogResult = false);
        dialog = Ui.Dialog("Renomear grupo", Ui.WithToolbar(form, save, cancel), 480, 240);
        dialog.ShowDialog();
        return Task.CompletedTask;
    }

    private void MoveContentToGroup(string path, string? groupId, bool refresh = true)
    {
        string? type = ContentGroupType(Session, path);
        if (type == null) return;
        MoveContentsToGroup([path], type, groupId, refresh);
    }

    private void MoveContentsToGroup(IEnumerable<string> paths, string type, string? groupId, bool refresh = true)
    {
        string[] moving = paths.Where(path => ContentGroupType(Session, path) == type)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (moving.Length == 0) return;
        var groups = ContentGroups("spreadsheet").Concat(ContentGroups("vector")).ToArray();
        var movingSet = moving.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var groupPaths = group.Arr("paths");
            for (int i = groupPaths.Count - 1; i >= 0; i--)
                if (movingSet.Contains(groupPaths[i].Display())) groupPaths.RemoveAt(i);
        }
        var target = groups.FirstOrDefault(group => group.S("id") == groupId && group.S("type") == type);
        if (target != null)
            foreach (string path in moving)
                if (!target.Arr("paths").Any(item => string.Equals(item.Display(), path, StringComparison.OrdinalIgnoreCase)))
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
        string[] paths = selectedContentPaths.Contains(path)
            ? selectedContentPaths.Where(selected => Session.Files.Contains(selected, StringComparer.OrdinalIgnoreCase)
                && ContentGroupType(Session, selected) != null).ToArray()
            : [path];
        var data = new DataObject("Dendro.ContentPaths", paths);
        DragDrop.DoDragDrop(ContentsTree, data, DragDropEffects.Move);
    }

    private void ContentsTreeDragOver(object sender, DragEventArgs e)
    {
        object? target = TreeItemFromSource(e.OriginalSource)?.Tag;
        if (!e.Data.GetDataPresent("Dendro.ContentPaths") || (target is not ContentGroupReference && target is not ContentGroupRootReference))
        { e.Effects = DragDropEffects.None; return; }
        string[] paths = e.Data.GetData("Dendro.ContentPaths") as string[] ?? [];
        string targetType = target switch { ContentGroupReference group => group.Type, ContentGroupRootReference root => root.Type, _ => "" };
        e.Effects = paths.Any(path => ContentGroupType(Session, path) == targetType) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void ContentsTreeDrop(object sender, DragEventArgs e)
    {
        object? target = TreeItemFromSource(e.OriginalSource)?.Tag;
        if (e.Data.GetData("Dendro.ContentPaths") is not string[] paths || (target is not ContentGroupReference && target is not ContentGroupRootReference)) return;
        string? groupId = target is ContentGroupReference group ? group.Id : null;
        string targetType = target is ContentGroupReference targetGroup ? targetGroup.Type : ((ContentGroupRootReference)target).Type;
        MoveContentsToGroup(paths, targetType, groupId);
        e.Handled = true;
    }

    private void BuildContentsTree()
    {
        ContentsTree.Items.Clear();
        EnsureLegacyGisMaps();
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
        Menu(sheets, "Parear medições…", CommandIconKind.PairedMeasurements, () => OpenPairedMeasurementsTool());
        AddCreateGroupCommand(sheets, "spreadsheet");
        var db = ContentItem("Bancos de dados", CommandIconKind.Database);
        db.ContextMenu = new ContextMenu();
        Menu(db, "Adicionar banco DendroLab…", CommandIconKind.Database, AddBank);
        var gisRoot = ContentItem("GIS", CommandIconKind.Map);
        gisRoot.ContextMenu = new ContextMenu();
        Menu(gisRoot, "Novo mapa base…", CommandIconKind.Map, () => { ShowGisSourceMenu(); return Task.CompletedTask; });
        ContentsTree.Items.Add(db);
        ContentsTree.Items.Add(gisRoot);
        if (!Session.IsOpen)
            return;
        var mapNodes = new Dictionary<string, TreeViewItem>(StringComparer.Ordinal);
        var vectorNodes = new Dictionary<string, TreeViewItem>(StringComparer.Ordinal);
        foreach (var map in Session.Meta.Arr("gis_maps").OfType<JsonObject>())
        {
            string id = map.S("id"), name = map.S("name", "Mapa");
            string sourceId = map.S("basemap", "openstreetmap");
            var mapNode = ContentItem(name, CommandIconKind.Map);
            mapNode.Tag = new GisMapReference(id);
            mapNode.ToolTip = "Mapa com fonte base e camadas raster e vetoriais próprias.";
            mapNode.ContextMenu = new ContextMenu();
            Menu(mapNode, "Adicionar raster ASCII, TIFF ou shapefile…", CommandIconKind.Add, () => AddGISLayerFiles(id));
            Menu(mapNode, "Abrir mapa", CommandIconKind.Map, () => OpenGIS(id));
            gisRoot.Items.Add(mapNode);
            mapNodes[id] = mapNode;
            var sourceNode = ContentItem(GisBasemapSource.FromId(sourceId).Name, CommandIconKind.Map);
            sourceNode.ToolTip = "Camada de mapa base deste mapa.";
            mapNode.Items.Add(sourceNode);
            string[] mapPaths = map.Arr("layers").Select(node => node?.GetValue<string>() ?? "").Where(path => path.Length > 0).ToArray();
            if (mapPaths.Any(path => Path.GetExtension(path).Equals(".shp", StringComparison.OrdinalIgnoreCase)))
            {
                var shapes = ContentItem("Dados vetoriais", CommandIconKind.Shape);
                shapes.ContextMenu = new ContextMenu();
                Menu(shapes, "Adicionar shapefile…", CommandIconKind.Add, () => AddGISLayerFiles(id, vectorOnly: true));
                AddCreateGroupCommand(shapes, "vector");
                mapNode.Items.Add(shapes);
                vectorNodes[id] = shapes;
            }
        }
        var groupNodes = new Dictionary<string, TreeViewItem>(StringComparer.Ordinal);
        foreach (var group in ContentGroups("spreadsheet"))
        {
            var groupNode = CreateContentGroupNode(group);
            groupNodes[group.S("id")] = groupNode;
            sheets.Items.Add(groupNode);
        }
        foreach (var group in ContentGroups("vector"))
        {
            foreach (var map in Session.Meta.Arr("gis_maps").OfType<JsonObject>())
            {
                string mapId = map.S("id");
                if (!vectorNodes.TryGetValue(mapId, out var vectorNode)) continue;
                var mapPaths = map.Arr("layers").Select(node => node?.GetValue<string>() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
                bool containsGroupFiles = group.Arr("paths").Any(item => mapPaths.Contains(item.Display()));
                bool emptyGroupForThisMap = group.Arr("paths").Count == 0 && map == Session.Meta.Arr("gis_maps").OfType<JsonObject>().FirstOrDefault();
                if (!containsGroupFiles && !emptyGroupForThisMap) continue;
                string key = group.S("id") + "\0" + mapId;
                var groupNode = CreateContentGroupNode(group);
                groupNodes[key] = groupNode;
                vectorNode.Items.Add(groupNode);
            }
        }
        TreeViewItem managedBank = db;
        string managedPath = Session.Meta.S("database_path");
        if (managedPath.Length > 0)
        {
            managedBank = ContentItem("Dados do projeto", CommandIconKind.Database);
            managedBank.ToolTip = "Tabelas e histórico salvos em " + Session.FilePath;
            managedBank.ContextMenu = new ContextMenu();
            Menu(managedBank, "Otimizar banco DLB…", CommandIconKind.Settings, () => Execute(CompactProjectBank));
            db.Items.Add(managedBank);
        }
        var charts = Session.Meta.Arr("charts").OfType<JsonObject>().ToArray();
        var results = Session.Meta.Arr("processing_results").OfType<JsonObject>().ToArray();
        var analyses = Session.Meta.Arr("analyses").OfType<JsonObject>().ToArray();
        var chartsByPath = charts.ToLookup(c => c.S("path"), StringComparer.OrdinalIgnoreCase);
        var resultsBySource = results.ToLookup(r => r.S("source_path"), StringComparer.OrdinalIgnoreCase);
        var resultsByPath = results.ToLookup(r => r.S("path"), StringComparer.OrdinalIgnoreCase);
        var analysesByPath = analyses.ToLookup(a => a.S("path"), StringComparer.OrdinalIgnoreCase);
        var revisedRunIds = results.Select(r => r.S("parent_id")).Where(id => id.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        static string ResultNodeKey(string path, string sheet) => path.Replace('\\', '/').ToUpperInvariant() + "\0" + sheet;
        var nodes = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase);
        var resultSheetNodes = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase);
        var resultSheetNodesByPath = new Dictionary<string, List<TreeViewItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Session.Files)
        {
            if (
                !Session
                    .ContentName(path)
                    .Contains(ContentSearch.Text, StringComparison.CurrentCultureIgnoreCase)
                && !Path.GetFileName(path)
                    .Contains(ContentSearch.Text, StringComparison.CurrentCultureIgnoreCase)
                && !chartsByPath[path].Any(c =>
                    Session.ContentName(path, c).Contains(ContentSearch.Text, StringComparison.CurrentCultureIgnoreCase))
                && !resultsBySource[path].Any(r =>
                    r.Arr("sheets").Any(s => s.Display().Contains(ContentSearch.Text, StringComparison.CurrentCultureIgnoreCase)))
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
            if (Path.GetExtension(path).Equals(".shp", StringComparison.OrdinalIgnoreCase) || IsRasterPath(path))
            {
                ShellVisuals.SetLayerToggle(item, true);
                ShellVisuals.SetLayerVisible(item, Session.Meta.Obj("gis_layer_visibility")[path]?.GetValue<bool>() ?? true);
                ShellVisuals.SetLayerVisibilityChanged(item, visible =>
                {
                    Session.Meta.Obj("gis_layer_visibility")[path] = visible;
                    foreach (var view in gisViews.Values) view.SetLayerVisible(Session.Resolve(path), visible);
                    Session.Touch();
                });
            }
            bool missingSource = !ContentAvailable(path);
            ShellVisuals.SetMissing(item, missingSource);
            item.ToolTip = missingSource
                ? "Arquivo de origem não encontrado: " + Session.Resolve(path) + "\nUse o menu do botão direito para referenciá-lo novamente."
                : path;
            nodes[path] = item;
            bool processingOutput = resultsByPath[path].Any();
            if (!processingOutput)
            {
                string? groupType = ContentGroupType(Session, path);
                TreeViewItem parent;
                if (groupType == "spreadsheet") parent = sheets;
                else if (IsGisLayer(path) && GisMapForLayer(path) is string mapId && mapNodes.TryGetValue(mapId, out var mapNode))
                    parent = groupType == "vector" && vectorNodes.TryGetValue(mapId, out var vectorNode) ? vectorNode : mapNode;
                else parent = managedPath.Length > 0 && Session.Resolve(path).StartsWith(Session.Resolve(managedPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    ? managedBank : db;
                string? groupId = groupType == null ? null : GroupForContent(path, groupType);
                string? layerMapId = IsGisLayer(path) ? GisMapForLayer(path) : null;
                string groupNodeKey = groupType == "vector" && layerMapId != null ? groupId + "\0" + layerMapId : groupId ?? "";
                if (groupId != null && groupNodes.TryGetValue(groupNodeKey, out var groupNode)) parent = groupNode;
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
                    // This is a standalone project copy, not a result derived from
                    // the source. Keep it beside the other project tables in the bank.
                    await Session.AddTableResult(result);
                    Log($"{result.Rows:N0} linhas copiadas para o arquivo do projeto.");
                });
                if (Session.IsTabular(path))
                    Menu(item, "Worksheet e cabeçalho…", CommandIconKind.Settings, () => ConfigureExcel(path));
                Menu(item, "Gráfico de dispersão…", CommandIconKind.Chart, () => OpenChart(path, false));
                Menu(item, "Aplicar regressões…", CommandIconKind.ApplyRegression, () => OpenRegressionApplicationTool(path));
                Menu(item, "Estratificar…", CommandIconKind.Stratify, () => OpenTableTool(true, path));
                Menu(item, "Parear medições…", CommandIconKind.PairedMeasurements, () => OpenPairedMeasurementsTool(path));
                Menu(item, "Gerar coluna…", CommandIconKind.CalculateColumn, () => OpenTableTool(false, path, true));
                Menu(item, "Isolation Forest…", CommandIconKind.Anomaly, () => OpenAnomalyTool(path));
                Menu(item, "Limpeza integrada…", CommandIconKind.IntegratedCleaning, () => OpenIntegratedCleaning(path));
                Menu(item, "Testes estatísticos…", CommandIconKind.Statistics, () => OpenStatistics(false, path));
            }
            string? itemGroupType = ContentGroupType(Session, path);
            if (itemGroupType == "vector" && !missingSource)
            {
                Menu(item, "Simbologia…", CommandIconKind.Settings, async () =>
                {
                    if (VectorSymbology.Edit(this, Session, path))
                        foreach (var view in gisViews.Values) await view.RefreshSymbology(path);
                });
                Menu(item, "Abrir tabela de atributos", CommandIconKind.Table, () => OpenVectorAttributeTable(path));
                Menu(item, "Abrir no mapa", CommandIconKind.Map, () => OpenGISForLayer(path));
            }
            if (!missingSource && Session.Meta.Arr("sampling_results").Any(result => result.S("path") == path))
                Menu(item, "Exportar amostragem…", CommandIconKind.SaveAs, () => ExportSamplingContent(path));
            if (IsRasterPath(path) && !missingSource)
            {
                Menu(item, "Abrir no mapa", CommandIconKind.Map, () => OpenGISForLayer(path));
                if (Path.GetExtension(path).Equals(".asc", StringComparison.OrdinalIgnoreCase))
                    Menu(item, "Salvar como GeoTIFF…", CommandIconKind.SaveAs, () => ExportAsciiGeoTiff(path));
                if (Path.GetExtension(path) is string ext && (ext.Equals(".asc", StringComparison.OrdinalIgnoreCase) || ext.Equals(".tif", StringComparison.OrdinalIgnoreCase) || ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase)))
                    Menu(item, "Recortar por shapefile…", CommandIconKind.SaveAs, () => ClipRasterByShape(path));
            }
            if ((itemGroupType is "spreadsheet" or "vector") && !processingOutput) AddMoveToGroupMenu(item, path, itemGroupType);
            item.ContextMenu.Items.Add(new Separator());
            Menu(item, "Renomear…", CommandIconKind.Rename, () => RenameContent(path));
            Menu(item, "Remover", CommandIconKind.Remove, () => RemoveContentFiles(path));
            foreach (var chart in chartsByPath[path])
            {
                var node = ContentItem(Session.ContentName(path, chart), CommandIconKind.Chart);
                node.Tag = chart.DeepClone();
                bool missingChartSource = !ContentAvailable(path);
                ShellVisuals.SetMissing(node, missingChartSource);
                if (missingChartSource) node.ToolTip = "Arquivo de origem não encontrado: " + Session.Resolve(path);
                node.ContextMenu = new ContextMenu();
                if (missingChartSource) AddRelinkMenu(node, path);
                Menu(node, "Abrir gráfico", CommandIconKind.Chart, () => OpenChart(path, chart.B("fitting"), chart, false));
                Menu(node, "Ajuste de regressão…", CommandIconKind.Regression, () => OpenChart(path, true, chart));
                Menu(node, "Fronteira de eficiência…", CommandIconKind.Frontier, () => {
                    SetTool(new FrontierView(this, Session, chart)); return Task.CompletedTask;
                });
                Menu(
                    node,
                    "ICA e IMA…",
                    CommandIconKind.Increments,
                    () =>
                    {
                        SetTool(new GrowthValidationView(this, Session));
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
                        if (!Ui.Confirm("Remover gráfico", $"O gráfico '{title}' será removido do projeto e sua janela será fechada. Deseja continuar?", "Remover gráfico", destructive: true))
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
            foreach (var analysis in analysesByPath[path])
                item.Items.Add(AnalysisNode(analysis));
            var resultNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var result in resultsBySource[path].Reverse())
            {
                string output = result.S("path");
                TreeViewItem resultParent = item;
                bool historical = result.S("id").Length > 0;
                bool directSummary = !historical && result.Arr("sheets")
                    .Any(s => s.Display().StartsWith("DIRETO -", StringComparison.Ordinal));
                if (historical)
                {
                    var versionNode = ContentItem($"{result.S("inventory_type")} · V{result.N("number")} {(result.N("number") == 1 ? "original" : "corrigida")} · {ProcessingReviewView.Status(result)}", CommandIconKind.ProcessingReview);
                    versionNode.Tag = new ProcessingVersionReference(result.S("id"));
                    versionNode.ContextMenu = new ContextMenu();
                    Menu(versionNode, "Revisar / aprovar / exportar…", CommandIconKind.ProcessingReview, () => OpenProcessingReview(result.S("id")));
                    bool hasLaterRevision = revisedRunIds.Contains(result.S("id"));
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
                else if (directSummary)
                {
                    // Cada execução direta tem sua própria referência no banco. Agrupe
                    // suas tabelas por saída para que uma execução posterior não oculte
                    // a anterior só por produzir abas com os mesmos nomes.
                    string outputName = Path.GetFileNameWithoutExtension(output);
                    resultParent = ContentItem("Resumo direto · " + outputName, CommandIconKind.Processor);
                    resultParent.ToolTip = output + (result.S("created").Length > 0 ? "\nCriado: " + result.S("created") : "");
                    resultParent.ContextMenu = new ContextMenu();
                    Menu(resultParent, "Reprocessar", CommandIconKind.Processor,
                        () => ReprocessDirect(result));
                    Menu(resultParent, "Remover processamento…", CommandIconKind.Remove,
                        () => RemoveDirectProcessing(output, result.S("source_path")));
                    item.Items.Add(resultParent);
                }
                foreach (var sheet in result.Arr("sheets"))
                {
                    string name = sheet.Display();
                    if (name.Equals("Graficos", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!historical && !directSummary && !resultNames.Add(name)) continue;
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
                    Menu(node, "Aplicar regressões…", CommandIconKind.ApplyRegression, () => OpenResultTool(reference, () => OpenRegressionApplicationTool(output, name)));
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
                            : $"arquivo .dlb de origem '{Path.GetFileName(Session.Resolve(output))}'";
                        string message = $"Excluir a tabela '{name}' apagará seus dados do {bank}.";
                        if (descendants.Length > 0)
                            message += "\n\nTambém serão removidos os resultados dependentes:\n" +
                                string.Join("\n", descendants.Select(removed => "• " + Session.ContentName(removed)));
                        message += "\n\nDeseja continuar?";
                        message += "\n\nA exclusão é imediata e não será desfeita ao descartar alterações do projeto.";
                        if (!ConfirmRemoval(message)) return;
                        await Session.RemoveResultTable(output, name);
                        CloseDocuments();
                        processor = null;
                        ShowTools();
                        Log($"Tabela {name} removida do banco.");
                    });
                    resultParent.Items.Add(node);
                    resultSheetNodes[ResultNodeKey(output, name)] = node;
                    if (!resultSheetNodesByPath.TryGetValue(output, out var outputNodes))
                        resultSheetNodesByPath[output] = outputNodes = [];
                    outputNodes.Add(node);
                }
            }
        }
        // Charts and analyses belong to their actual result table, not its hidden dataset container.
        foreach (var outputNode in nodes.Values)
            foreach (var chartNode in outputNode.Items.OfType<TreeViewItem>().Where(n =>
                n.Tag is JsonObject || n.Tag is AnalysisReference).ToArray())
            {
                string sheet = chartNode.Tag is JsonObject chart ? chart.S("sheet")
                    : chartNode.Tag is AnalysisReference analysis ? analysis.Entry["definition"].S("sheet") : "";
                if (sheet.Length == 0 || !resultSheetNodes.TryGetValue(ResultNodeKey((string)outputNode.Tag!, sheet), out var tableNode)) continue;
                outputNode.Items.Remove(chartNode);
                tableNode.Items.Add(chartNode);
            }
        foreach (var derived in Session.Meta.Arr("derived_tables").OfType<JsonObject>())
        {
            string path = derived.S("path"),
                parent = derived.S("source_path");
            if (!nodes.TryGetValue(path, out var child)) continue;
            var derivedSheets = derived["sheets"] as JsonArray;
            foreach(var sheet in derivedSheets is { Count: > 1 } ? derivedSheets : new JsonArray())
            {
                string tableName=sheet.Display();
                var tableNode=ContentItem(tableName,CommandIconKind.Table);
                tableNode.Tag=new ResultSheetReference(path,tableName);
                tableNode.ContextMenu=new ContextMenu();
                Menu(tableNode,"Abrir tabela",CommandIconKind.Table,()=>OpenTable(path,tableName));
                child.Items.Add(tableNode);
            }
            TreeViewItem? parentNode = null;
            string sourceSheet = derived.S("source_sheet");
            if (sourceSheet.Length > 0)
                resultSheetNodes.TryGetValue(ResultNodeKey(parent, sourceSheet), out parentNode);
            if (parentNode == null && resultSheetNodesByPath.TryGetValue(parent, out var possibleParents))
                parentNode = possibleParents.FirstOrDefault();
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
                if (!Ui.Confirm("Remover análise", $"A análise '{title}' será removida do projeto e suas janelas abertas serão fechadas. Deseja continuar?", "Remover análise", destructive: true))
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

    private IEnumerable<TreeViewItem> VisibleContentFiles(ItemsControl parent)
    {
        foreach (var node in parent.Items.OfType<TreeViewItem>())
        {
            if (node.Tag is string path && Session.Files.Contains(path, StringComparer.OrdinalIgnoreCase))
                yield return node;
            if (node.IsExpanded)
                foreach (var descendant in VisibleContentFiles(node)) yield return descendant;
        }
    }

    private void UpdateContentSelectionVisuals()
    {
        foreach (var node in ContentsDescendants(ContentsTree))
            ShellVisuals.SetMultiSelected(node,
                node.Tag is string path && selectedContentPaths.Contains(path));
    }

    private void ContentsTreeMouseDown(object sender, MouseButtonEventArgs e)
    {
        contentDragOrigin = e.GetPosition(ContentsTree);
        var node = TreeItemFromSource(e.OriginalSource);
        string? path = node?.Tag as string;
        bool isContentFile = path != null && Session.Files.Contains(path, StringComparer.OrdinalIgnoreCase);
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (!isContentFile)
        {
            if (!shift && !control)
            {
                selectedContentPaths.Clear();
                contentSelectionAnchorPath = null;
                UpdateContentSelectionVisuals();
            }
            return;
        }

        if (shift)
        {
            var visible = VisibleContentFiles(ContentsTree).ToArray();
            int clickedIndex = Array.FindIndex(visible, item => ReferenceEquals(item, node));
            int anchorIndex = Array.FindIndex(visible, item =>
                string.Equals(item.Tag as string, contentSelectionAnchorPath, StringComparison.OrdinalIgnoreCase));
            if (clickedIndex < 0) return;
            if (!control) selectedContentPaths.Clear();
            if (anchorIndex < 0) anchorIndex = clickedIndex;
            int first = Math.Min(anchorIndex, clickedIndex), last = Math.Max(anchorIndex, clickedIndex);
            for (int i = first; i <= last; i++)
                if (visible[i].Tag is string selectedPath) selectedContentPaths.Add(selectedPath);
            contentSelectionAnchorPath ??= path;
            node!.IsSelected = true;
            e.Handled = true;
        }
        else if (control)
        {
            if (!selectedContentPaths.Add(path!)) selectedContentPaths.Remove(path!);
            contentSelectionAnchorPath ??= path;
            node!.IsSelected = true;
            e.Handled = true;
        }
        else
        {
            if (!selectedContentPaths.Contains(path!))
            {
                selectedContentPaths.Clear();
                selectedContentPaths.Add(path!);
                contentSelectionAnchorPath = path;
            }
        }
        UpdateContentSelectionVisuals();
    }

    private void ContentsTreeRightMouseDown(object sender, MouseButtonEventArgs e)
    {
        var node = TreeItemFromSource(e.OriginalSource);
        if (node?.Tag is not string path || !Session.Files.Contains(path, StringComparer.OrdinalIgnoreCase))
            return;
        if (!selectedContentPaths.Contains(path))
        {
            selectedContentPaths.Clear();
            selectedContentPaths.Add(path);
            contentSelectionAnchorPath = path;
        }
        node.IsSelected = true;
        UpdateContentSelectionVisuals();
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

    private async Task AddGISLayerFiles(string mapId, bool vectorOnly = false)
    {
        if (FindGisMap(mapId) == null) throw new InvalidOperationException("O mapa selecionado não existe mais.");
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = vectorOnly ? "Adicionar shapefile ao mapa" : "Adicionar camadas ao mapa",
            Filter = vectorOnly ? "Shapefile vetorial|*.shp" : "Raster ASCII, GeoTIFF ou PNG|*.asc;*.tif;*.tiff;*.png|Shapefile vetorial|*.shp|Todos os formatos aceitos|*.asc;*.tif;*.tiff;*.png;*.shp",
            Multiselect = true,
            CheckFileExists = true,
        };
        if (picker.ShowDialog(this) != true) return;
        string[] selected = picker.FileNames.Select(Path.GetFullPath).ToArray();
        foreach (string path in selected)
            if (vectorOnly ? !Path.GetExtension(path).Equals(".shp", StringComparison.OrdinalIgnoreCase)
                : !IsGisLayer(path))
                throw new InvalidDataException("Selecione um raster ASCII (.asc), TIFF ou um shapefile (.shp).");
        foreach (string path in selected.Where(path => Path.GetExtension(path).Equals(".asc", StringComparison.OrdinalIgnoreCase)))
        {
            if (Session.Meta.Obj("gis_layer_epsg")[path] is JsonValue saved && saved.TryGetValue<int>(out _)) continue;
            var epsgInput = Ui.Input();
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(Ui.Text($"O arquivo {Path.GetFileName(path)} não informa o sistema de coordenadas. Informe o código EPSG para posicionar o raster no mapa e gravar o GeoTIFF."));
            panel.Children.Add(Ui.Text("Código EPSG (por exemplo, 4326, 3857 ou UTM WGS84):"));
            panel.Children.Add(epsgInput);
            var dialog = Ui.Dialog("Sistema de coordenadas do ASCII", panel, 520, 250);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            buttons.Children.Add(Ui.Button("Cancelar", () => dialog.DialogResult = false));
            buttons.Children.Add(Ui.Button("Continuar", () => dialog.DialogResult = true, Ui.ActionEmphasis.Primary));
            panel.Children.Add(buttons);
            if (dialog.ShowDialog() != true) return;
            if (!int.TryParse(epsgInput.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int epsg)
                || !(epsg == 4326 || epsg == 3857 || epsg is >= 32601 and <= 32660 or >= 32701 and <= 32760))
                throw new InvalidDataException("EPSG inválido. Use EPSG:4326, EPSG:3857 ou UTM WGS84 (32601–32660/32701–32760).");
            Session.Meta.Obj("gis_layer_epsg")[path] = epsg;
        }
        var map = FindGisMap(mapId)!;
        var contents = Session.Meta.Arr("files");
        foreach (string path in selected)
        {
            if (!Session.Files.Contains(path, StringComparer.OrdinalIgnoreCase)) contents.Add(JsonValue.Create(path));
            if (!map.Arr("layers").Any(layer => string.Equals(layer?.GetValue<string>(), path, StringComparison.OrdinalIgnoreCase)))
                map.Arr("layers").Add(JsonValue.Create(path));
        }
        Session.Touch();
        foreach (string path in selected) sourceAvailability.Remove(path);
        RevealAddedContents(selected);
        await OpenGIS(mapId, selected.LastOrDefault());
        Log($"{selected.Length:N0} camada(s) adicionada(s) a {map.S("name")}.");
    }

    private async Task ExportAsciiGeoTiff(string path)
    {
        int epsg = Session.Meta.Obj("gis_layer_epsg")[path]?.GetValue<int>()
            ?? throw new InvalidOperationException("Adicione o ASCII a um mapa e informe seu EPSG antes de exportar.");
        string? output = Ui.SaveFile("GeoTIFF (*.tif)|*.tif", Path.GetFileNameWithoutExtension(path) + ".tif");
        if (output == null) return;
        if (!Path.GetExtension(output).Equals(".tif", StringComparison.OrdinalIgnoreCase)) output += ".tif";
        await Task.Run(() => AsciiGridGeoTiff.Write(Session.Resolve(path), output, epsg));
        string? mapId = GisMapForLayer(path);
        if (mapId != null && FindGisMap(mapId) is JsonObject map)
        {
            if (!Session.Files.Contains(output, StringComparer.OrdinalIgnoreCase)) Session.Meta.Arr("files").Add(JsonValue.Create(output));
            if (!map.Arr("layers").Any(layer => string.Equals(layer?.GetValue<string>(), output, StringComparison.OrdinalIgnoreCase)))
                map.Arr("layers").Add(JsonValue.Create(output));
            Session.Touch();
            RevealAddedContents([output]);
            await OpenGIS(mapId, output);
        }
        Ui.Alert("GeoTIFF", "GeoTIFF salvo com os valores originais do raster.", Ui.MessageTone.Information);
        Log($"GeoTIFF exportado: {output}");
    }

    private async Task ClipRasterByShape(string rasterPath)
    {
        string? shapePath = Ui.PickFile("Shapefile (*.shp)|*.shp");
        if (shapePath == null) return;
        shapePath = Path.GetFullPath(shapePath);
        string resolvedRaster = Session.Resolve(rasterPath);
        int epsgOverride = Session.Meta.Obj("gis_layer_epsg")[rasterPath]?.GetValue<int>() ?? 0;
        var input = await Task.Run(() =>
        {
            var raster = GeoTiffReader.ReadNumericRaster(resolvedRaster, epsgOverride);
            var dbf = DbfTable.Read(Path.ChangeExtension(shapePath, ".dbf"));
            var shapes = ShapefileReader.Read(shapePath, Path.GetFileNameWithoutExtension(shapePath));
            return (raster, dbf, shapes);
        });
        if (input.dbf.Fields.Count == 0) throw new InvalidDataException("O DBF do shapefile não contém campos para nomear os arquivos.");
        string defaultFolder = Path.Combine(Path.GetDirectoryName(resolvedRaster) ?? Environment.CurrentDirectory,
            Path.GetFileNameWithoutExtension(resolvedRaster) + "_recortes");
        var options = RasterShapeClipDialog.Show(this, rasterPath,
            input.dbf.Fields.Select(field => field.Name).ToArray(), shapePath, defaultFolder);
        if (options == null) return;
        var outputs = await Task.Run(() => RasterShapeClipper.Clip(input.raster.Grid, input.raster.Epsg,
            input.shapes, input.dbf, options.FieldIndex, options.OutputFolder, options.Crop, options.AllTouched,
            options.Prefix, options.Suffix, options.NoData));
        if (outputs.Count == 0)
        {
            Ui.Alert("Recorte sem resultados", "Nenhuma feição intersecta o raster com pixels válidos.", Ui.MessageTone.Warning);
            return;
        }
        string? mapId = GisMapForLayer(rasterPath);
        if (mapId == null) mapId = EnsureGisMapForLayer(rasterPath);
        var map = FindGisMap(mapId);
        foreach (var output in outputs)
        {
            if (!Session.Files.Contains(output.Path, StringComparer.OrdinalIgnoreCase)) Session.Meta.Arr("files").Add(JsonValue.Create(output.Path));
            if (map != null && !map.Arr("layers").Any(layer => string.Equals(layer?.GetValue<string>(), output.Path, StringComparison.OrdinalIgnoreCase)))
                map.Arr("layers").Add(JsonValue.Create(output.Path));
        }
        Session.Touch();
        RevealAddedContents(outputs.Select(output => output.Path).ToArray());
        if (mapId != null) await OpenGIS(mapId, outputs[^1].Path);
        Ui.Alert("Recorte concluído", $"Criados {outputs.Count:N0} GeoTIFF(s) em {options.OutputFolder}.", Ui.MessageTone.Information);
        Log($"Recorte raster concluído: {outputs.Count:N0} feições exportadas.");
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

    private bool ConfirmRemoval(string message, string objectName = "conteúdos do projeto") => Ui.Confirm("Remover " + objectName + "?", message, "Remover " + objectName, destructive: true);

    private async Task RemoveContentFiles(string clickedPath)
    {
        string[] selected = selectedContentPaths.Contains(clickedPath)
            ? selectedContentPaths.Where(path => Session.Files.Contains(path, StringComparer.OrdinalIgnoreCase)).ToArray()
            : [clickedPath];
        bool keptProcessingSources = false;
        if (selected.Length > 1)
        {
            string[] safeSelection = Session.RemovalRootsPreservingProcessingSources(selected);
            keptProcessingSources = safeSelection.Length < selected.Length;
            selected = safeSelection;
        }
        if (selected.Length == 0) return;

        string[] affected = selected.Length == 1
            ? Session.PreviewRemoveFile(Session.Files.ToList().FindIndex(path => SameProjectPath(path, selected[0])))
            : Session.PreviewRemoveFiles(selected);
        string[] descendants = affected.Where(removed => !selected.Any(path => SameProjectPath(removed, path))).ToArray();
        bool deletesBankData = affected.Any(Session.IsManagedTable);
        string message = selected.Length == 1
            ? deletesBankData
                ? $"Remover {Session.ContentName(selected[0])} do projeto também apagará seus dados do arquivo .dlb do projeto."
                : $"Remover {Session.ContentName(selected[0])} apenas retira este conteúdo do projeto. O arquivo de origem continuará no local original."
            : deletesBankData
                ? $"Remover {selected.Length:N0} conteúdos selecionados também apagará os dados correspondentes do arquivo .dlb do projeto."
                : $"Remover {selected.Length:N0} conteúdos selecionados apenas retira as referências do projeto. Os arquivos de origem continuarão nos locais originais.";
        if (descendants.Length > 0)
            message += "\n\nTambém serão removidos os resultados dependentes:\n" +
                string.Join("\n", descendants.Select(removed => "• " + Session.ContentName(removed)));
        if (keptProcessingSources)
            message += "\n\nAs bases de origem dos processamentos selecionados serão mantidas no projeto.";
        if (deletesBankData)
            message += "\n\nA exclusão é imediata e não será desfeita ao descartar alterações do projeto.";
        message += "\n\nDeseja continuar?";
        if (!ConfirmRemoval(message, selected.Length == 1 ? "conteúdo" : $"{selected.Length} conteúdos")) return;

        foreach (var map in Session.Meta.Arr("gis_maps").OfType<JsonObject>())
            foreach (var path in selected)
                for (int i = map.Arr("layers").Count - 1; i >= 0; i--)
                    if (string.Equals(map.Arr("layers")[i]?.GetValue<string>(), path, StringComparison.OrdinalIgnoreCase)) map.Arr("layers").RemoveAt(i);
        if (selected.Length == 1)
            await Session.RemoveFile(Session.Files.ToList().FindIndex(path => SameProjectPath(path, selected[0])));
        else
            await Session.RemoveFiles(selected);
        selectedContentPaths.Clear();
        contentSelectionAnchorPath = null;
        CloseDocuments();
        processor = null;
        ShowTools();
    }

    private async Task CompactProjectBank()
    {
        string bank = Session.Resolve(Session.Meta.S("database_path"));
        if (!CompactBankFile.IsCompactBank(bank))
            throw new InvalidDataException("O banco DLB do projeto não foi encontrado ou não é válido.");
        var footprint = CompactBankFile.StorageUsage(bank);
        long estimatedDiscard = Math.Max(0, footprint.FileBytes - footprint.ActiveBlockBytes - 184);
        if (!Ui.Confirm("Otimizar banco DLB",
            $"Arquivo atual: {footprint.FileBytes / 1048576.0:N1} MB.\nBlocos de dados em uso: {footprint.ActiveBlockBytes / 1048576.0:N1} MB.\n" +
            $"Até {estimatedDiscard / 1048576.0:N1} MB parecem estar fora dos blocos atuais e podem ser recuperados.\n\n" +
            "A operação mantém todos os dados ativos e exige espaço temporário próximo ao tamanho dos dados em uso.",
            "Otimizar")) return;

        long beforeBytes = footprint.FileBytes;
        StartTableOperation("Otimizando banco DLB do projeto…");
        var cancellation = OperationCancellation;
        await Task.Run(() => CompactBankFile.Compact(bank, cancellation, (copied, total) =>
            Dispatcher.BeginInvoke(() =>
            {
                if (total > 0) SetProgress(98.0 * copied / total);
                StatusText.Text = $"Otimizando banco: {copied / 1048576.0:N1} de {total / 1048576.0:N1} MB…";
            })));
        long afterBytes = new FileInfo(bank).Length;
        Log($"Banco DLB otimizado: {beforeBytes / 1048576.0:N1} → {afterBytes / 1048576.0:N1} MB; " +
            $"{Math.Max(0, beforeBytes - afterBytes) / 1048576.0:N1} MB recuperados. " +
            $"Blocos ativos preservados: {footprint.ActiveBlockBytes / 1048576.0:N1} MB.");
    }

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
        if (!ConfirmRemoval(message, "revisão de inventário")) return;
        await Session.RemoveProcessingRevision(revision, OperationCancellation);
        CloseDocuments();
        processor = null;
        ShowTools();
        Log($"Revisão removida: {id}");
    }

    private async Task RemoveDirectProcessing(string output, string source)
    {
        int index = Session.Files.ToList().FindIndex(path => SameProjectPath(path, output));
        if (index < 0)
            throw new InvalidOperationException("O resultado deste processamento não existe mais no projeto.");
        string[] affected = Session.PreviewRemoveFile(index);
        string[] descendants = affected.Where(path => !SameProjectPath(path, output)).ToArray();
        string message = $"Remover o processamento '{Path.GetFileNameWithoutExtension(output)}' apagará seus resultados do banco do projeto.";
        if (descendants.Length > 0)
            message += "\n\nTambém serão removidos os resultados derivados:\n" +
                string.Join("\n", descendants.Select(path => "• " + Session.ContentName(path)));
        message += "\n\nDeseja continuar?";
        if (!ConfirmRemoval(message, "processamento")) return;
        await Session.RemoveFile(index);
        selectedContentPaths.Clear();
        contentSelectionAnchorPath = null;
        CloseDocuments();
        processor = null;
        ShowTools();
        Log($"Processamento removido; base de origem preservada: {Session.ContentName(source)}.");
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
            UpdateDocumentNavigator();
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
                UpdateDocumentNavigator();
            }
        };
        doc.IsActive = true;
        AttachChartPanels(content as ChartWorkspace);
        UpdateDocumentNavigator();
        return doc;
    }

    private void AttachDocumentNavigator()
    {
        if (previousDocumentButton == null)
        {
            previousDocumentButton = Ui.IconButton("Janela anterior", CommandIconKind.ArrowLeft, () => NavigateDocument(-1));
            nextDocumentButton = Ui.IconButton("Próxima janela", CommandIconKind.ArrowRight, () => NavigateDocument(1));
            foreach (var button in new[] { previousDocumentButton, nextDocumentButton })
            {
                button.Width = button.MinWidth = 28;
                button.Height = button.MinHeight = 26;
                button.Margin = new Thickness(2, 0, 0, 0);
                button.Padding = new Thickness(3);
                DocumentNavigationButtons.Children.Add(button);
            }
        }
        UpdateDocumentNavigator();
    }

    private void UpdateDocumentNavigator()
    {
        if (previousDocumentButton == null)
        {
            if (IsLoaded) Dispatcher.BeginInvoke(new Action(AttachDocumentNavigator));
            return;
        }
        bool enabled = (Documents?.Children.OfType<LayoutDocument>().Count() ?? 0) > 1;
        previousDocumentButton.IsEnabled = enabled;
        nextDocumentButton!.IsEnabled = enabled;
        DocumentNavigationHost.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
    }
    private void NavigateDocument(int direction)
    {
        var tabs = Documents.Children.OfType<LayoutDocument>().ToArray();
        if (tabs.Length < 2) return;
        int selected = Array.FindIndex(tabs, tab => tab.IsSelected);
        if (selected < 0) selected = Array.FindIndex(tabs, tab => tab.IsActive);
        if (selected < 0) selected = 0;
        int target = (selected + direction + tabs.Length) % tabs.Length;
        var targetDocument = tabs[target];
        Documents.SelectedContentIndex = target;
        DockManager.ActiveContent = targetDocument.Content;
        targetDocument.IsSelected = true;
        targetDocument.IsActive = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var paneControl = FindDocumentPaneControl();
            if (paneControl == null) return;
            paneControl.SelectedIndex = target;
            var tabItem = FindVisualChildren<LayoutDocumentTabItem>(paneControl)
                .FirstOrDefault(item => ReferenceEquals(item.Model, targetDocument));
            tabItem?.BringIntoView();
        }));
    }

    private LayoutDocumentPaneControl? FindDocumentPaneControl() =>
        FindVisualChildren<LayoutDocumentPaneControl>(DockManager)
            .Where(control => control.IsVisible
                && control.ActualWidth > 0
                && control.ActualHeight > 0)
            .OrderByDescending(control => ReferenceEquals(control.Model, Documents))
            .ThenByDescending(control => control.ActualWidth * control.ActualHeight)
            .FirstOrDefault();

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                yield return match;
            foreach (var nested in FindVisualChildren<T>(child))
                yield return nested;
        }
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
        var content = new Grid { Margin = new Thickness(16) };
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

        var buttons = new UniformGrid
        {
            Columns = 3,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var dialog = Ui.Dialog("Salvar alterações", content, 450, 190);
        dialog.Owner = this;
        dialog.ResizeMode = ResizeMode.NoResize;
        dialog.MinWidth = 400;
        dialog.MinHeight = 170;
        Grid.SetRow(buttons, 1);
        content.Children.Add(buttons);

        CloseChoice choice = CloseChoice.Cancel;
        var cancel = Ui.Button("Cancelar", () => dialog.Close());
        cancel.IsCancel = true;
        var discard = Ui.Button("Descartar", () => { choice = CloseChoice.Discard; dialog.Close(); });
        var save = Ui.Button("Salvar", () => { choice = CloseChoice.Save; dialog.Close(); });
        foreach (var button in new[] { cancel, discard, save })
        {
            button.MinWidth = 0;
            button.Width = 122;
            button.MinHeight = 32;
            button.Height = 32;
            button.Margin = new Thickness(3, 0, 3, 0);
            button.Padding = new Thickness(4, 2, 4, 2);
        }
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

    private async void CompactClick(object s, RoutedEventArgs e) => await Execute(CompactProjectBank);

    private async void SettingsClick(object s, RoutedEventArgs e) =>
        await Execute(async () =>
        {
            await SettingsView.Show(this, Session);
            if (ToolContent.Content is ChartView chart)
                await chart.Load(chart.CurrentConfig.S("path"), chart.CurrentConfig, false);
        });

    private void VolumeClick(object s, RoutedEventArgs e) =>
        Document("volumes", "Modelos volumétricos", new ModelsView(this, Session, false));

    private void TaperClick(object sender, RoutedEventArgs e) =>
        Document("taper-catalog", "Ajustes salvos de taper", new TaperModelsView(this, Session, catalogOnly: true));

    private void GrowthClick(object s, RoutedEventArgs e) =>
        Document("growth", "Crescimento e produção", new ModelsView(this, Session, true));

    private void SiteIndexClick(object s, RoutedEventArgs e) =>
        Document("site-index", "Índice de sítio", new SiteIndexModelsView(this, Session));

    private void SavedRegressionsClick(object s, RoutedEventArgs e) =>
        Document("saved-regressions", "Regressões salvas", new SavedRegressionCatalogView(this, Session));

    private void ContentsClick(object s, RoutedEventArgs e) => ContentsPane.Show();

    private void ToolsClick(object s, RoutedEventArgs e) => ToolsPane.Show();

    private void HistoryClick(object s, RoutedEventArgs e) => HistoryPane.Show();

    private void ClearHistoryClick(object s, RoutedEventArgs e) => HistoryText.Clear();

    private async void AddDataClick(object sender, RoutedEventArgs e)
    {
        if (Session.IsOpen && !busy) await AddData();
    }

    private void ClearToolSearchClick(object sender, RoutedEventArgs e)
    {
        ToolSearch.Clear();
        ToolCategoryFilter.SelectedIndex = 0;
        ShowTools();
        ToolSearch.Focus();
    }

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
        {
            contentSearchTimer.Stop();
            contentSearchTimer.Start();
        }
    }

    private void ToolSearchChanged(object s, TextChangedEventArgs e)
    {
        // Searching should filter the tool list without replacing an open editor/form.
        if (IsInitialized && ToolContent?.Content is ListBox)
        {
            toolSearchTimer.Stop();
            toolSearchTimer.Start();
        }
    }

    private void ToolCategoryChanged(object s, SelectionChangedEventArgs e)
    {
        if (IsInitialized && ToolContent?.Content is ListBox)
        {
            toolSearchTimer.Stop();
            toolSearchTimer.Start();
        }
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
        bool MatchesTool(string name) => MatchesToolSearch(name)
            || name == "K-means" && "kmeans cluster agrupamento silhouette silhueta elbow cotovelo inertia inércia".Contains(ToolSearch.Text, StringComparison.CurrentCultureIgnoreCase);
        string selectedGroup = (ToolCategoryFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        var toolGroups = new (string Title, string[] Items)[]
        {
            ("Preparar e revisar dados", ["Processador", "Revisão de inventário", "Auditoria pós-processamento", "Juntar tabelas", "Parear medições", "Estratificar", "Limpeza integrada", "Ativo biológico"]),
            ("Gráficos e modelos", ["Gráficos", "Ajuste de regressão", "Ajuste de taper", "Aplicar taper", "Aplicar regressões", "Ajustar rede neural", "Ajustar Random Forest", "Ajustar Gradient Boosting", "Ajustar SVR", "Fronteira de eficiência"]),
            ("Comparar e validar", ["Comparar curvas de produção", "ICA e IMA", "Testes estatísticos", "Validação de modelos", "Isolation Forest", "K-means"]),
            ("Planejamento de campo", ["Amostragem"]),
            ("Mapa e satélite", ["Imagens NOAA", "Imagens Sentinel-2", "Catálogo temporal Sentinel-2", "Série de pixels Sentinel-2", "Explorar série temporal Sentinel-2"]),
        };
        foreach (var (groupTitle, groupTools) in toolGroups)
        {
            if (selectedGroup.Length > 0 && groupTitle != selectedGroup) continue;
            var visibleTools = groupTools.Where(MatchesTool).ToArray();
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
                        "Parear medições" => "Organize medições repetidas em formato amplo por ano ou longitudinal por pares de ocasiões, usando as chaves de árvore ou parcela configuradas no projeto.",
                        "Limpeza integrada" => "Revise pontos de vários gráficos, remova observações e grave uma tabela limpa.",
                        "Gráficos" => "Crie um gráfico e confira a prévia antes de ajustar modelos.",
                        "Ajuste de regressão" => "Ajuste modelos em um gráfico já criado. Os ajustes podem ser salvos para aplicação em outras bases.",
                        "Aplicar regressões" => "Aplique equações ajustadas e salvas no projeto a uma tabela, mapeando X1, X2 e X3 por modelo.",
                        "Aplicar taper" => "Aplique um ajuste salvo de taper a uma árvore teórica, calcule os volumes por seção e visualize o fuste.",
                        "Ajustar rede neural" => "Treine uma rede Dense/MLP e gere o ZIP para o Processador.",
                        "Ajustar Random Forest" => "Treine um Random Forest e gere o ZIP para o Processador.",
                        "Ajustar Gradient Boosting" => "Treine um Gradient Boosting e gere o ZIP para o Processador.",
                        "Ajustar SVR" => "Treine um SVR e gere o ZIP para o Processador.",
                        "Comparar curvas de produção" => "Escolha uma curva base e compare uma ou mais candidatas; a prévia mostra as séries selecionadas.",
                        "Testes estatísticos" => "Execute testes de médias, variâncias e identidade de modelos.",
                        "Validação de modelos" => "Compare uma ou várias colunas estimadas com os valores observados, com métricas, dispersão e histograma de erros.",
                        "K-means" => "Agrupe observações numéricas e compare o número de grupos com elbow (inertia) e silhouette.",
                        "Imagens NOAA" => "Baixe imagens NOAA em RGB natural ou NDVI no mapa do projeto.",
                        "Imagens Sentinel-2" => "Busque cenas Copernicus e gere RGB natural, falso colorido ou índices espectrais com máscara de nuvens.",
                        _ => null,
                    },
                    IsEnabled = tool switch
                    {
                        "Parear medições" => Session.IsOpen,
                        "Ajuste de regressão" or "Fronteira de eficiência" => Session.IsOpen && Session.Meta.Arr("charts").Count > 0,
                        "Aplicar regressões" or "Aplicar taper" => Session.IsOpen,
                        "Ajustar rede neural" or "Ajustar Random Forest" or "Ajustar Gradient Boosting" or "Ajustar SVR" => Session.IsOpen,
                        "Validação de modelos" => Session.IsOpen,
                        _ => true,
                    },
                };
                if (!entry.IsEnabled)
                {
                    string requirement = !Session.IsOpen ? "Abra um projeto para usar esta ferramenta."
                        : "Crie um gráfico para usar esta ferramenta.";
                    entry.ToolTip = requirement + "\n" + entry.ToolTip;
                    ToolTipService.SetShowOnDisabled(entry, true);
                    System.Windows.Automation.AutomationProperties.SetItemStatus(entry, requirement);
                }
                if (entry.ToolTip is string description)
                    System.Windows.Automation.AutomationProperties.SetHelpText(entry, description);
                ShellVisuals.SetIcon(entry, tool switch
                {
                    "Processador" => CommandIconKind.Processor,
                    "Revisão de inventário" => CommandIconKind.ProcessingReview,
                    "Auditoria pós-processamento" => CommandIconKind.InventoryAudit,
                    "Ativo biológico" => CommandIconKind.BiologicalAsset,
                    "Gráficos" => CommandIconKind.Chart,
                    "Ajuste de regressão" => CommandIconKind.Regression,
                    "Ajuste de taper" => CommandIconKind.Taper,
                    "Aplicar regressões" => CommandIconKind.ApplyRegression,
                    "Aplicar taper" => CommandIconKind.Taper,
                    "Ajustar rede neural" => CommandIconKind.NeuralNetwork,
                    "Ajustar Random Forest" => CommandIconKind.RandomForest,
                    "Ajustar Gradient Boosting" => CommandIconKind.GradientBoosting,
                    "Ajustar SVR" => CommandIconKind.SupportVectorMachine,
                    "Fronteira de eficiência" => CommandIconKind.Frontier,
                    "Juntar tabelas" => CommandIconKind.Merge,
                    "Parear medições" => CommandIconKind.PairedMeasurements,
                    "Gerar coluna" => CommandIconKind.CalculateColumn,
                    "ICA e IMA" => CommandIconKind.Increments,
                    "Comparar curvas de produção" => CommandIconKind.CurveComparison,
                    "Isolation Forest" => CommandIconKind.Anomaly,
                    "K-means" => CommandIconKind.Statistics,
                    "Imagens NOAA" => CommandIconKind.Satellite,
                    "Imagens Sentinel-2" => CommandIconKind.Multispectral,
                    "Catálogo temporal Sentinel-2" => CommandIconKind.Multispectral,
                    "Série de pixels Sentinel-2" => CommandIconKind.Multispectral,
                    "Explorar série temporal Sentinel-2" => CommandIconKind.Multispectral,
                    "Limpeza integrada" => CommandIconKind.IntegratedCleaning,
                    "Testes estatísticos" => CommandIconKind.Statistics,
                    "Validação de modelos" => CommandIconKind.ModelValidation,
                    _ => CommandIconKind.Stratify,
                });
                list.Items.Add(entry);
            }
        }
        if (list.Items.Count == 0)
            list.Items.Add(new ListBoxItem
            {
                Content = new StackPanel
                {
                    Margin = new Thickness(4, 14, 4, 0),
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Nenhuma ferramenta encontrada",
                            FontWeight = FontWeights.SemiBold,
                            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(48, 74, 96)),
                            Margin = new Thickness(0, 0, 0, 4),
                        },
                        new TextBlock
                        {
                            Text = "Tente outro termo ou selecione outra categoria.",
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(104, 122, 137)),
                        },
                    },
                },
                IsEnabled = false,
                IsHitTestVisible = false,
                Focusable = false,
                Padding = new Thickness(8, 0, 8, 8),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(104, 122, 137)),
            });
        list.SelectionChanged += async (_, _) =>
        {
            if (list.SelectedItem is not ListBoxItem i)
                return;
            if ((string)i.Tag == "Imagens NOAA")
                SetTool(new NoaaNdviView(this, Session));
            else if ((string)i.Tag == "Imagens Sentinel-2")
                SetTool(new Sentinel2NdviView(this, Session));
            else if ((string)i.Tag == "Explorar série temporal Sentinel-2")
                SetTool(new Sentinel2SeriesExplorerView(this, Session));
            else if ((string)i.Tag == "Série de pixels Sentinel-2")
                SetTool(new Sentinel2PixelSeriesView(this, Session));
            else if ((string)i.Tag == "Catálogo temporal Sentinel-2")
                SetTool(new Sentinel2TemporalCatalogView(this, Session));
            else if ((string)i.Tag == "Processador")
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
            else if ((string)i.Tag == "Aplicar regressões")
                await Execute(() => OpenRegressionApplicationTool());
            else if ((string)i.Tag == "Aplicar taper")
                SetTool(new TaperApplicationView(this, Session));
            else if ((string)i.Tag is "Ajustar rede neural" or "Ajustar Random Forest" or "Ajustar Gradient Boosting" or "Ajustar SVR")
            {
                string modelType = i.Tag.ToString() switch
                {
                    "Ajustar Random Forest" => "random_forest",
                    "Ajustar Gradient Boosting" => "gradient_boosting",
                    "Ajustar SVR" => "svr",
                    _ => "neural_net",
                };
                await Execute(() => OpenMachineLearningTool(modelType));
            }
            else if ((string)i.Tag == "Gerar coluna")
                await Execute(() => OpenTableTool(false, calculateColumns: true));
            else if ((string)i.Tag == "ICA e IMA")
                SetTool(new GrowthValidationView(this, Session));
            else if ((string)i.Tag == "Comparar curvas de produção")
                await Execute(async () => { var view = new GrowthComparisonView(this, Session); SetTool(view); await view.Initialize(); });
            else if ((string)i.Tag == "Isolation Forest")
                await Execute(() => OpenAnomalyTool());
            else if ((string)i.Tag == "K-means")
                await Execute(() => OpenKMeansTool());
            else if ((string)i.Tag == "Amostragem")
                await Execute(OpenForestSampling);
            else if ((string)i.Tag == "Ajuste de taper")
                SetTool(new TaperModelsView(this, Session));
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
            else if ((string)i.Tag == "Validação de modelos")
                await Execute(() => OpenModelValidationTool());
            else
                await Execute(() => OpenChart(null, (string)i.Tag != "Gráficos"));
        };
        ToolContent.Content = list;
        RefreshEnabled();
    }

    internal void SetTool(object content, bool reveal = true)
    {
        if (closing)
            return;
        if (reveal)
        {
            ToolsPane.Show();
            ToolSearchPanel.Visibility = Visibility.Collapsed;
        }
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

    internal async Task OpenRegressionApplicationTool(string? path = null, string? table = null)
    {
        var view = new RegressionApplicationView(this, Session);
        SetTool(view);
        await view.Initialize(path, table);
    }

    internal async Task OpenAnomalyTool(string? path = null, string? sheet = null)
    {
        var view = new IsolationForestView(this, Session);
        SetTool(view); await view.Initialize(path, sheet);
    }

    internal async Task OpenKMeansTool(string? path = null, string? sheet = null)
    {
        var view = new KMeansView(this, Session);
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

    internal async Task OpenModelValidationTool(string? path = null)
    {
        var view = new ModelValidationView(this, Session);
        SetTool(view);
        await view.Initialize(path);
    }

    internal async Task OpenMachineLearningTool(string modelType, string? path = null, string? table = null)
    {
        var view = new MachineLearningView(this, Session, modelType);
        SetTool(view);
        await view.Initialize(path, table);
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

    private void GISClick(object sender, RoutedEventArgs e) => ShowGisSourceMenu();

    private void NoaaNdviClick(object sender, RoutedEventArgs e) => SetTool(new NoaaNdviView(this, Session));
    private void Sentinel2NdviClick(object sender, RoutedEventArgs e) => SetTool(new Sentinel2NdviView(this, Session));

    internal async Task RegisterNoaaNdviImage(string path, string product)
        => await RegisterSatelliteImage(path, "NOAA", product);

    internal async Task RegisterSentinel2NdviImage(string path, string product)
        => await RegisterSatelliteImage(path, "Sentinel-2", product);

    private async Task RegisterSatelliteImage(string path, string source, string product)
    {
        if (!Session.Files.Contains(path, StringComparer.OrdinalIgnoreCase)) Session.AddFile(path);
        string mapId = EnsureGisMapForLayer(path);
        RevealAddedContents([path]);
        await OpenGIS(mapId, path);
        Log($"Imagem {product} {source} adicionada a {FindGisMap(mapId)?.S("name")}.");
    }

    private void ManualClick(object sender, RoutedEventArgs e) => new ManualWindow { Owner = this }.ShowDialog();

    private async Task OpenForestSampling()
    {
        if (!Session.IsOpen)
            throw new InvalidOperationException("Abra um projeto para usar a ferramenta de amostragem.");
        var shapefiles = Session.Files.Where(path => Path.GetExtension(path).Equals(".shp", StringComparison.OrdinalIgnoreCase))
            .Where(path => !Session.Meta.Arr("sampling_results").Any(result => result.S("path") == path))
            .Select(Session.Resolve).ToArray();
        var view = new ForestSamplingView(this, shapefiles, async result =>
        {
            string path = await RegisterSamplingContent(result);
            await OpenGISForLayer(path);
        });
        SetTool(view);
        await view.Initialize();
    }

    internal async Task<string> RegisterSamplingContent(Dendro.Core.Sampling.ForestSamplingResult result)
    {
        string baseFolder = Session.FilePath.Length > 0
            ? Path.Combine(Path.GetDirectoryName(Session.FilePath)!, Path.GetFileNameWithoutExtension(Session.FilePath) + "_camadas")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DENDRO", "amostragens");
        string folder = Path.Combine(baseFolder, Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, Path.GetFileNameWithoutExtension(result.SourcePath) + "_amostra_" + result.Method + ".shp");
        await Task.Run(() =>
        {
            Directory.CreateDirectory(folder);
            Dendro.Core.Sampling.ForestSampler.WriteShapefile(result, path);
            Dendro.Core.Sampling.ForestSampler.WriteCsv(result, Path.ChangeExtension(path, ".csv"));
            Dendro.Core.Sampling.ForestSampler.WriteMapSvg(result, Path.ChangeExtension(path, ".svg"));
            Dendro.Core.Sampling.ForestSampler.WriteDesign(result, Path.ChangeExtension(path, ".design.json"));
        });
        Session.Meta.Arr("sampling_results").Add(new JsonObject
        {
            ["path"] = path, ["source_path"] = result.SourcePath, ["method"] = result.Method.ToString(),
            ["rounds"] = new JsonArray(result.Samples.Select(sample => (JsonNode?)JsonValue.Create(sample.Round)).ToArray()),
            ["design"] = System.Text.Json.JsonSerializer.SerializeToNode(result.GrtsDesign),
            ["options"] = System.Text.Json.JsonSerializer.SerializeToNode(result.Options),
        });
        Session.AddFile(path);
        RevealAddedContents([path]);
        return path;
    }

    private Task ExportSamplingContent(string path)
    {
        var picker = new Microsoft.Win32.SaveFileDialog { Filter = "Shapefile|*.shp", FileName = Path.GetFileName(path), Title = "Exportar amostragem" };
        if (picker.ShowDialog(this) != true) return Task.CompletedTask;
        foreach (string extension in new[] { ".shp", ".shx", ".dbf", ".prj", ".cpg", ".csv", ".svg", ".design.json" })
        {
            string from = Path.ChangeExtension(Session.Resolve(path), extension), to = Path.ChangeExtension(picker.FileName, extension);
            if (File.Exists(from) && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) File.Copy(from, to, true);
        }
        return Task.CompletedTask;
    }

    private async Task OpenGISForLayer(string path)
    {
        bool assigned = GisMapForLayer(path) != null;
        string mapId = EnsureGisMapForLayer(path);
        if (!assigned) RefreshTree();
        await OpenGIS(mapId, path);
    }

    private async Task OpenGIS(string mapId, string? selectedLayer = null)
    {
        if (!Session.IsOpen) throw new InvalidOperationException("Abra um projeto antes de abrir um mapa base.");
        var mapInfo = FindGisMap(mapId) ?? throw new InvalidOperationException("O mapa selecionado não existe mais.");
        string documentId = "gis:" + mapId;
        if (gisViews.TryGetValue(mapId, out var existingView) && documents.TryGetValue(documentId, out var existing)
            && ReferenceEquals(existing.Content, existingView))
        {
            existing.IsActive = true;
            existingView.SetLayerPaths(mapInfo.Arr("layers").Select(node => node?.GetValue<string>() ?? "").Where(path => path.Length > 0).ToArray());
            if (selectedLayer == null || !existingView.HasLayer(Session.Resolve(selectedLayer))) await existingView.Initialize(selectedLayer);
            else existingView.FocusLayer(Session.Resolve(selectedLayer));
            return;
        }
        string[] paths = mapInfo.Arr("layers").Select(node => node?.GetValue<string>() ?? "").Where(path => path.Length > 0).ToArray();
        var view = new GisView(Session, paths, GisBasemapSource.FromId(mapInfo.S("basemap", "openstreetmap")));
        gisViews[mapId] = view;
        var document = Document(documentId, mapInfo.S("name", "Mapa Base"), view);
        document.Closed += (_, _) => { if (gisViews.TryGetValue(mapId, out var current) && ReferenceEquals(current, view)) gisViews.Remove(mapId); };
        await view.Initialize(selectedLayer);
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

    private async Task AddData(bool vectorOnly = false)
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Filter = vectorOnly ? "Shapefile|*.shp" : "Planilhas Excel|*.xlsx;*.xls|Dados|*.dlb;*.dendrodb;*.db;*.shp;*.tif;*.tiff;*.png|Todos|*.*",
            Multiselect = true,
        };
        if (picker.ShowDialog() != true)
            return;
        await Execute(async () =>
        {
            string? selectedShape = null;
            var addedPaths = new List<string>();
            var contents = Session.Meta.Arr("files");
            foreach (var path in picker.FileNames)
            {
                if (Session.IsSpreadsheet(path))
                {
                    if (!await ExcelSelectionDialog.Configure(this, Session, path))
                        continue;
                }
                if (!Session.Files.Contains(path, StringComparer.OrdinalIgnoreCase)) contents.Add(JsonValue.Create(path));
                addedPaths.Add(path);
                if (Path.GetExtension(path).Equals(".shp", StringComparison.OrdinalIgnoreCase) || IsRasterPath(path)) selectedShape = path;
            }
            foreach (string path in addedPaths.Where(IsGisLayer)) EnsureGisMapForLayer(path);
            Session.Touch();
            foreach (string path in addedPaths) sourceAvailability.Remove(path);
            string? targetMap = selectedShape == null ? null : GisMapForLayer(selectedShape);
            RevealAddedContents(addedPaths);
            if (selectedShape != null && targetMap != null) await OpenGIS(targetMap, selectedShape);
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
    private sealed record GisMapReference(string Id);

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

    private async Task OpenVectorAttributeTable(string path)
    {
        string resolved = Session.Resolve(path);
        var prepared=await Task.Run(()=>VectorAttributeTable.Prepare(resolved));
        var view = new VectorAttributeTable(this, prepared.Dbf,prepared.Table);
        Document("attributes:" + path, "Atributos · " + Session.ContentName(path), view);
        Log("Tabela de atributos aberta. Edite as células e use 'Salvar edições' para gravar no DBF.");
    }

    internal async Task RegisterVectorOutput(string path,string sourcePath)
    {
        if(!Session.Files.Contains(path,StringComparer.OrdinalIgnoreCase))Session.Meta.Arr("files").Add(JsonValue.Create(path));
        string? mapId=GisMapForLayer(sourcePath);
        mapId??=Session.Meta.Arr("gis_maps").OfType<JsonObject>().FirstOrDefault(candidate=>candidate.Arr("layers").Any(layer=>layer is JsonValue value&&value.TryGetValue<string>(out var stored)&&string.Equals(Session.Resolve(stored),Session.Resolve(sourcePath),StringComparison.OrdinalIgnoreCase)))?.S("id");
        mapId??=EnsureGisMapForLayer(path);
        var map=FindGisMap(mapId)??throw new InvalidOperationException("O mapa de origem não está mais disponível.");
        if(!map.Arr("layers").Any(layer=>string.Equals(layer?.GetValue<string>(),path,StringComparison.OrdinalIgnoreCase)))map.Arr("layers").Add(JsonValue.Create(path));
        Session.Touch();sourceAvailability.Remove(path);RevealAddedContents([path]);await OpenGIS(mapId,path);
        Log("Novo shapefile criado com as feições restantes e adicionado a Conteúdos.");
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
            throw new InvalidOperationException("Crie um gráfico antes de abrir Ajuste de regressão.");
        var view = new ChartView(this, Session, fitting);
        if (activateTool) SetTool(view, reveal: !chartFullScreen);
        await view.Load(path, config);
    }

    internal async Task NavigateSavedChart(int direction)
    {
        var charts = Session.Meta.Arr("charts").OfType<JsonObject>().ToArray();
        if (charts.Length == 0) return;
        string? activeId = Documents.Children.OfType<LayoutDocument>()
            .FirstOrDefault(document => document.IsSelected && charts.Any(chart => ChartView.Identity(chart) == document.ContentId))
            ?.ContentId;
        int current = Array.FindIndex(charts, chart => ChartView.Identity(chart) == activeId);
        int next = current < 0
            ? direction >= 0 ? 0 : charts.Length - 1
            : (current + direction + charts.Length) % charts.Length;
        var chart = charts[next];
        bool regressionMode = (ToolContent.Content as ChartView)?.IsFitting == true;
        bool fitting = regressionMode || chart.B("fitting");
        bool preserveFullScreen = chartFullScreen;
        bool preserveMaximized = WindowState == WindowState.Maximized;
        try
        {
            await Execute(() => OpenChart(chart.S("path"), fitting, chart, fitting));
        }
        finally
        {
            // Activating another AvalonDock document can restore the window while the
            // fullscreen chart is being replaced. Reapply the display state after the swap.
            if (preserveFullScreen)
                RestoreChartFullScreen();
            else if (preserveMaximized && WindowState != WindowState.Maximized)
                WindowState = WindowState.Maximized;
        }
    }

    private void RestoreChartFullScreen()
    {
        if (!ContentsPane.IsHidden) ContentsPane.Hide();
        if (!ToolsPane.IsHidden) ToolsPane.Hide();
        RibbonHost.Visibility = Visibility.Collapsed;
        StatusBarHost.Visibility = Visibility.Collapsed;
        chartFullScreen = true;
        if (WindowState != WindowState.Maximized) WindowState = WindowState.Maximized;
        ChartFullScreenChanged?.Invoke(true);
    }

    private async void TreeDoubleClick(object s, MouseButtonEventArgs e)
    {
        if (ContentsTree.SelectedItem is TreeViewItem i)
        {
            if (i.Tag is string path && Session.IsTabular(path))
                await Execute(() => OpenTable(path));
            else if (i.Tag is string vectorPath && (Path.GetExtension(vectorPath).Equals(".shp", StringComparison.OrdinalIgnoreCase) || IsRasterPath(vectorPath)))
                await Execute(() => OpenGISForLayer(vectorPath));
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
            else if (i.Tag is GisMapReference map)
                await Execute(() => OpenGIS(map.Id));
            else if (i.Tag is ResultSheetReference result)
                await Execute(() => OpenTable(result.Path, result.Sheet));
        }
    }

    private async void ContentsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && selectedContentPaths.Count > 0)
        {
            e.Handled = true;
            string selectedPath = selectedContentPaths.First();
            await Execute(() => RemoveContentFiles(selectedPath));
            return;
        }
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
            string? selectedShape = null;
            var addedPaths = new List<string>();
            var contents = Session.Meta.Arr("files");
            foreach (var path in (string[])e.Data.GetData(DataFormats.FileDrop))
            {
                if (
                    Session.IsSpreadsheet(path)
                    && !await ExcelSelectionDialog.Configure(this, Session, path)
                )
                    continue;
                if (!Session.Files.Contains(path, StringComparer.OrdinalIgnoreCase)) contents.Add(JsonValue.Create(path));
                addedPaths.Add(path);
                if (Path.GetExtension(path).Equals(".shp", StringComparison.OrdinalIgnoreCase) || IsRasterPath(path)) selectedShape = path;
            }
            foreach (string path in addedPaths.Where(IsGisLayer)) EnsureGisMapForLayer(path);
            Session.Touch();
            foreach (string path in addedPaths) sourceAvailability.Remove(path);
            string? targetMap = selectedShape == null ? null : GisMapForLayer(selectedShape);
            RevealAddedContents(addedPaths);
            if (selectedShape != null && targetMap != null) await OpenGIS(targetMap, selectedShape);
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
        foreach (var pane in panes) ShellVisuals.EnsureAutoHideSize(pane);
        ContentsPane = panes.First(p => p.ContentId == "contents");
        ToolsPane = panes.First(p => p.ContentId == "tools");
        HistoryPane = panes.First(p => p.ContentId == "history");
        RebindChartPanels(panes);
        Documents = DockManager.Layout.Descendents().OfType<LayoutDocumentPane>().First();
        Dispatcher.BeginInvoke(new Action(AttachDocumentNavigator));
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
                    if (documents.Values.Select(d => d.Content).OfType<VectorAttributeTable>().Any(v => v.HasUnsavedChanges)
                        && !Ui.Confirm("Edições não salvas", "A tabela de atributos tem alterações sem salvar. Deseja fechar o DendroLab mesmo assim?", "Fechar sem salvar"))
                        return;
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
