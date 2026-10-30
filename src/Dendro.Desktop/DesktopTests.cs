using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AvalonDock.Layout;

namespace Dendro.Desktop;

internal static partial class DesktopTests
{
    private static int checks;

    private static Grid ChartBody(object content) => content is ChartWorkspace workspace ? workspace.GraphBody : (Grid)content;
    private static DependencyObject ChartModels(object content) => content is ChartWorkspace workspace ? workspace.ModelsContent! : (DependencyObject)content;

    private static async Task Until(Func<bool> completed)
    {
        var start = System.Diagnostics.Stopwatch.StartNew();
        while (!completed())
        {
            if (start.Elapsed.TotalSeconds > 8)
                throw new TimeoutException("A interação do gráfico não concluiu.");
            await Task.Delay(15);
        }
    }

    private static void Check(bool condition, string message)
    {
        checks++;
        if (!condition)
            throw new InvalidOperationException("Teste: " + message);
    }

    private static IEnumerable<T> Controls<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                yield return match;
            foreach (var nested in Controls<T>(child))
                yield return nested;
        }
    }

    private static IEnumerable<T> LogicalControls<T>(DependencyObject parent)
        where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T match)
                yield return match;
            foreach (var nested in LogicalControls<T>(child))
                yield return nested;
        }
    }

    private static async Task WithDialog(
        Func<Task> open,
        string button,
        string? id = null,
        Action<Window>? inspect = null,
        Func<Window, Task>? inspectAsync = null
    )
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        int attempts = 0;
        Exception? failure = null;
        timer.Tick += async (_, _) =>
        {
            try
            {
                var dialog = Application
                    .Current.Windows.OfType<Window>()
                    .FirstOrDefault(w =>
                        w != Application.Current.MainWindow
                        && w.Owner == Application.Current.MainWindow
                        && w.IsVisible
                        && w.IsLoaded
                    );
                if (dialog == null || !dialog.IsVisible || !dialog.IsLoaded)
                {
                    if (++attempts > 100)
                        throw new TimeoutException(
                            "Janela de cadastro não abriu: "
                                + string.Join(
                                    ";",
                                    Application
                                        .Current.Windows.OfType<Window>()
                                        .Select(w =>
                                            $"{w.Title} vis={w.IsVisible} load={w.IsLoaded} owner={w.Owner?.Title}"
                                        )
                                )
                        );
                    return;
                }
                dialog.UpdateLayout();
                var action = LogicalControls<Button>(dialog)
                    .Concat(Controls<Button>(dialog))
                    .FirstOrDefault(b => b.Content?.ToString() == button);
                if (action == null)
                {
                    if (++attempts > 100)
                        throw new TimeoutException(
                            "Comando não encontrado em "
                                + dialog.Title
                                + ": "
                                + button
                                + "; botões: "
                                + string.Join(
                                    ",",
                                    LogicalControls<Button>(dialog).Select(b => b.Content)
                                )
                        );
                    return;
                }
                timer.Stop();
                inspect?.Invoke(dialog);
                if (inspectAsync != null)
                    await inspectAsync(dialog);
                if (id != null)
                {
                    var input = LogicalControls<TextBox>(dialog).First();
                    input.Text = id;
                }
                action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception ex)
            {
                failure = ex;
                timer.Stop();
                foreach (
                    var dialog in Application
                        .Current.Windows.OfType<Window>()
                        .Where(w => w != Application.Current.MainWindow)
                        .ToArray()
                )
                    dialog.Close();
            }
        };
        timer.Start();
        try
        {
            await open();
        }
        finally
        {
            timer.Stop();
        }
        if (failure != null)
            throw failure;
    }

    public static async Task Run(string[] args)
    {
        var root =
            args.Length > 0
                ? Path.GetFullPath(args[0])
                : Path.Combine(Path.GetTempPath(), "DENDRO-WPF-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Check(
            new JsonObject { ["value"] = 3 }.N("value") == 3
                && new JsonObject { ["value"] = .25 }.N("value") == .25
                && JsonNode.Parse("{\"value\":2}").N("value") == 2,
            "parâmetros numéricos mantêm inteiros e números decimais"
        );
        var session = new ProjectSession();
        await session.New("Teste WPF");
        Check(session.IsOpen, "novo projeto");
        var legacyRegistry = session.Data!.DeepClone().AsObject();
        legacyRegistry.Obj("cadastro")["caminho"] = "";
        legacyRegistry.Obj("_dendro")["registry"] = "cadastro-antigo.xlsx";
        legacyRegistry.Obj("_dendro")["file_options"] = new JsonArray(
            new JsonObject
            {
                ["path"] = "cadastro-antigo.xlsx",
                ["sheet"] = "Cadastro antigo",
                ["header_row"] = 3,
            }
        );
        var migratedRegistry = new ProjectSession();
        migratedRegistry.Replace(legacyRegistry);
        Check(
            migratedRegistry.RegistryPath == "cadastro-antigo.xlsx"
                && migratedRegistry.Data!["cadastro"].S("worksheet") == "Cadastro antigo"
                && !migratedRegistry.Meta.ContainsKey("registry"),
            "projeto antigo migra caminho e worksheet para Cadastro florestal"
        );
        var canonicalRegistry = migratedRegistry.Data!.DeepClone().AsObject();
        canonicalRegistry.Obj("cadastro")["caminho"] = "cadastro-vigente.xlsx";
        canonicalRegistry.Obj("_dendro")["registry"] = "cadastro-ignorado.xlsx";
        migratedRegistry.Replace(canonicalRegistry);
        var registrySnapshot = migratedRegistry.ProcessingSnapshot();
        Check(
            migratedRegistry.RegistryPath == "cadastro-vigente.xlsx"
                && registrySnapshot["cadastro"].S("caminho")
                    == migratedRegistry.Resolve("cadastro-vigente.xlsx")
                && registrySnapshot["_dendro"]?["registry"] == null,
            "processamento usa somente o caminho do Cadastro florestal, com precedência sobre o legado"
        );
        await session.Mutate(
            new()
            {
                ["op"] = "model_set",
                ["index"] = -1,
                ["id"] = "AL1R",
                ["b0"] = 0,
                ["b1"] = 1,
                ["b2"] = 1,
            }
        );
        Check(session.Data!.Obj("volume").Arr("equacoes").Count == 1, "modelo C#");
        var path = Path.Combine(root, "wpf-test.dendro");
        await session.Save(path);
        await session.Open(path);
        Check(!session.IsDirty, "persistência");
        Check(Ui.Factor("0,5") == .5 && Ui.Factor("") == null, "fator decimal");
        bool invalid = false;
        try
        {
            Ui.Factor("1.1");
        }
        catch
        {
            invalid = true;
        }
        Check(invalid, "fator fora do intervalo");
        var chart = new JsonObject
        {
            ["path"] = "C:\\dados\\inventario.xlsx",
            ["sheet"] = "Dados",
            ["header_row"] = 1,
            ["title"] = "Primeiro",
            ["x"] = "DAP",
            ["y"] = "H",
            ["point_size"] = 3,
        };
        session.StoreChart(chart);
        chart["title"] = "Segundo";
        session.StoreChart(chart);
        chart["title"] = "Primeiro";
        chart["point_size"] = 6;
        session.StoreChart(chart);
        Check(session.Meta.Arr("charts").Count == 2, "gráficos com títulos distintos");
        await session.Save(path);
        await session.Open(path);
        Check(session.Meta.Arr("charts").Count == 2, "reabertura de gráficos");
        var window = new MainWindow
        {
            Left = -30000,
            Top = -30000,
            ShowInTaskbar = false,
        };
        Application.Current.MainWindow = window;
        window.Show();
        await window.Session.New("Validação da interface WPF");
        await Application.Current.Dispatcher.InvokeAsync(
            () => { },
            DispatcherPriority.ApplicationIdle
        );
        window.UpdateLayout();
        Capture(window, Path.Combine(root, "desktop-wpf.png"));
        TestFreehandSelection(root);
        await TestLargePointSelection(root);
        TestTrendlineLayer(root);
        bool requiresChart = false;
        try
        {
            await new ChartView(window, window.Session, true).Load(null, null);
        }
        catch (InvalidOperationException ex)
        {
            requiresChart = ex.Message.Contains("Crie um gráfico");
        }
        Check(
            requiresChart,
            "Regressões 1P exige um gráfico existente, mesmo quando há planilhas disponíveis"
        );
        foreach (
            var tool in new FrameworkElement[]
            {
                new ProcessorView(window, window.Session),
                new ChartView(window, window.Session, false),
                new ChartView(window, window.Session, true),
                new TableToolsView(window, window.Session, false),
                new TableToolsView(window, window.Session, true),
                new GrowthValidationView(window, window.Session),
                new StatisticalTestsView(window, window.Session, true),
                new StatisticalTestsView(window, window.Session),
            }
        )
        {
            window.SetTool(tool);
            var back = LogicalControls<Button>(tool)
                .Single(b =>
                    System.Windows.Automation.AutomationProperties.GetName(b)
                    == "Voltar às ferramentas"
                );
            Check(
                back.Parent is DockPanel header && header.Children.OfType<TextBlock>().Count() == 1,
                "seta discreta na mesma linha do título da ferramenta"
            );
            back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(
                !ReferenceEquals(window.ToolContent.Content, tool),
                "seta retorna à pesquisa de ferramentas"
            );
        }
        Check(window.DockManager.Layout != null, "layout encaixável WPF");
        var volumes = new ModelsView(window, window.Session, false);
        window.Document("test-volumes", "Modelos volumétricos", volumes);
        await WithDialog(() => volumes.Edit(-1), "Cadastrar modelo", "AL1R");
        Check(
            window.Session.Data!.Obj("volume").Arr("equacoes").Count == 1,
            "cadastro volumétrico pela janela WPF"
        );
        var growth = new ModelsView(window, window.Session, true);
        window.Document("test-growth", "Crescimento e produção", growth);
        await WithDialog(() => growth.Edit(-1), "Cadastrar modelo", "AL1R");
        Check(
            window.Session.Data!.Obj("curvas_crescimento").Arr("equacoes").Count == 1,
            "cadastro de crescimento pela janela WPF"
        );
        var snapshot = window.Session.Data!.DeepClone();
        await WithDialog(
            () => SettingsView.Show(window, window.Session),
            "Aplicar configurações",
            inspect: dialog =>
            {
                var tabs = LogicalControls<TabControl>(dialog).First();
                var sections = tabs.Items.OfType<TabItem>().ToArray();
                Check(
                    sections.Count(t => t.Header?.ToString() == "Cadastro florestal") == 1
                        && !sections.Any(t => t.Header?.ToString() == "Arquivo do cadastro"),
                    "Configurações contém apenas a seção Cadastro florestal"
                );
                var cadastro = sections.Single(t => t.Header?.ToString() == "Cadastro florestal");
                tabs.SelectedItem = cadastro;
                dialog.UpdateLayout();
                Check(
                    LogicalControls<TextBlock>((DependencyObject)cadastro.Content)
                        .Count(t => t.Text == "Caminho") == 1
                        && LogicalControls<Button>((DependencyObject)cadastro.Content)
                            .Any(b =>
                                b.Content?.ToString()
                                == "Selecionar cadastro, worksheet e cabeçalho…"
                            ),
                    "cadastro tem caminho único com seleção de worksheet e cabeçalho"
                );
                Capture(dialog, Path.Combine(root, "settings-registry.png"));
            }
        );
        if(!JsonNode.DeepEquals(snapshot,window.Session.Data))
        {
            File.WriteAllText(Path.Combine(root,"settings-before.json"),snapshot.ToJsonString());
            File.WriteAllText(Path.Combine(root,"settings-after.json"),window.Session.Data!.ToJsonString());
        }
        Check(
            JsonNode.DeepEquals(snapshot, window.Session.Data),
            "configurações preservam todos os parâmetros"
        );
        var toolsParent = (LayoutAnchorablePane)window.ToolsPane.Parent;
        var contentParent = (LayoutAnchorablePane)window.ContentsPane.Parent;
        toolsParent.Children.Remove(window.ToolsPane);
        contentParent.Children.Add(window.ToolsPane);
        Check(window.ToolsPane.Parent == window.ContentsPane.Parent, "agrupar painéis em abas");
        window.ToolsPane.Float();
        await Application.Current.Dispatcher.InvokeAsync(
            () => { },
            DispatcherPriority.ApplicationIdle
        );
        Check(window.ToolsPane.IsFloating, "painel flutuante");
        window.ToolsPane.Dock();
        await Application.Current.Dispatcher.InvokeAsync(
            () => { },
            DispatcherPriority.ApplicationIdle
        );
        window.ToolsPane.Hide();
        window.ToolsPane.Show();
        Check(!window.ToolsPane.IsHidden, "ocultar e reabrir painel");
        for (int i = 0; i < 12; i++)
        {
            window.ToolsPane.Float();
            await Application.Current.Dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.ApplicationIdle
            );
            Check(window.ToolsPane.IsFloating, "destacar painel repetidamente");
            window.ToolsPane.Dock();
            await Application.Current.Dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.ApplicationIdle
            );
            Check(!window.ToolsPane.IsFloating, "reencaixar painel repetidamente");
        }
        window.ResetWorkspace();
        await Application.Current.Dispatcher.InvokeAsync(
            () => { },
            DispatcherPriority.ApplicationIdle
        );
        Check(window.ToolsPane.Parent != window.ContentsPane.Parent, "restaurar posições padrão");
        if (args.Length >= 3)
        {
            string input = args[1],
                registry = args[2];
            await window.Session.Mutate(
                new()
                {
                    ["op"] = "model_set",
                    ["index"] = 0,
                    ["id"] = "AL1R",
                    ["b0"] = 0,
                    ["b1"] = 1,
                    ["b2"] = 1,
                }
            );
            window.Session.AddFile(input);
            window.Session.SetExcel(input, "Inventário", 1);
            window.Session.Data!.Obj("cadastro")["caminho"] = registry;
            var table = (
                await WorkspaceOperations.Run(
                    new()
                    {
                        ["op"] = "table",
                        ["path"] = input,
                        ["sheet"] = "Inventário",
                        ["header_row"] = 1,
                    }
                )
            )["result"]!;
            Check(table.N("total") == 3, "leitura XLSX nativa");
            await window.OpenTable(input);
            Capture(window, Path.Combine(root, "table-wpf.png"));
            foreach (double factor in new[] { 1, .5, 0 })
            {
                window.Session.Data!.Obj("volume")["fator"] = factor;
                string cfg = Path.Combine(root, $"factor-{factor}.dendro"),
                    output = Path.Combine(root, $"factor-{factor}.xlsx");
                await window.Session.Save(cfg);
                int progress = 0;
                var processed = await WorkspaceOperations.Run(
                    new()
                    {
                        ["op"] = "process",
                        ["input"] = input,
                        ["registry"] = registry,
                        ["config"] = cfg,
                        ["output"] = output,
                        ["sheet"] = "Inventário",
                    },
                    (percent, _) => progress = Math.Max(progress, percent)
                );
                Check(
                    Math.Abs(processed["result"]!.N("volume") - 11 * factor) < 1e-9,
                    "processamento C# com fator"
                );
                Check(progress > 0, "progresso do motor");
            }
            var c = new JsonObject
            {
                ["path"] = input,
                ["sheet"] = "Inventário",
                ["header_row"] = 1,
                ["title"] = "DAP × H",
                ["x"] = "DAP",
                ["y"] = "H",
                ["point_size"] = 3,
                ["formula"] = "b0+b1*x",
                ["parameters"] = 2,
                ["bins"] = 20,
            };
            var fit = (
                await WorkspaceOperations.Run(
                    new()
                    {
                        ["op"] = "fit",
                        ["chart"] = c,
                        ["model"] = 0,
                    }
                )
            )["result"]!;
            Check(fit["chart"]!["fits"]!.AsArray().Count == 1, "ajuste C#");
            foreach (var (ext, format) in new[] { ("png", 0), ("svg", 1), ("xlsx", 2) })
            {
                string output = Path.Combine(root, "chart." + ext);
                await WorkspaceOperations.Run(
                    new()
                    {
                        ["op"] = "export",
                        ["chart"] = fit["chart"]!.DeepClone(),
                        ["format"] = format,
                        ["output"] = output,
                    }
                );
                Check(new FileInfo(output).Length > 50, "exportação " + ext);
            }
            string legacy = Path.Combine(Path.GetDirectoryName(input)!, "inventario_legacy.XLS");
            if (File.Exists(legacy))
            {
                var legacyTable = (
                    await WorkspaceOperations.Run(
                        new()
                        {
                            ["op"] = "table",
                            ["path"] = legacy,
                            ["sheet"] = "Dados",
                            ["header_row"] = 1,
                        }
                    )
                )["result"]!;
                Check(legacyTable.N("total") == 3, "leitura XLS em C#");
                window.Session.AddFile(legacy);
                window.Session.SetExcel(legacy, "Dados", 1);
                window.Session.Data!.Obj("volume")["fator"] = .5;
                window.Session.Meta["batch_inputs"] = new JsonArray(input, legacy);
                window.Session.Meta["output_folder"] = root;
                window.Session.Meta["output_prefix"] = "batch_";
                var processor = new ProcessorView(window, window.Session);
                window.SetTool(processor);
                await processor.Process();
                Check(File.Exists(window.Session.Resolve(window.Session.Meta.S("database_path")))
                    && !Directory.Exists(window.Session.Resolve(window.Session.Meta.S("database_path")))
                    && Directory.GetFiles(window.Session.ProjectWorkFolder(), "batch_*.dlb").Length == 0, "lote misto XLS/XLSX grava apenas um arquivo de banco no projeto");
                Check(
                    window.Session.Files.Count(p => Path.GetFileName(p).StartsWith("batch_")) == 2,
                    "resultados populam os Conteúdos"
                );
                var results = window.Session.Meta.Arr("processing_results").OfType<JsonObject>().ToArray();
                Check(results.Length == 2 && results.All(r => r.Arr("sheets").Any(s => s.Display() == "RESULTADO - ARVORES")
                    && r.Arr("sheets").Any(s => s.Display() == "RESUMO - BYLOTE")),
                    "cada entrada do batch conserva suas abas de árvores e lote");
                var sourceNode = LogicalControls<TreeViewItem>(window.ContentsTree).First(n => n.Tag is string p && p == input);
                Check(sourceNode.Items.OfType<TreeViewItem>().Any(n => n.Tag is MainWindow.ResultSheetReference r
                    && r.Sheet == "RESUMO - BYPLOT"), "abas de saída ficam abaixo do inventário de entrada");
                string report = results[0].S("path");
                var storedTrees = Dendro.Core.Tabular.TableStore.Read(new(window.Session.Resolve(report), "RESULTADO - ARVORES"));
                Check(Math.Abs(Enumerable.Range(1, storedTrees.Count - 1).Sum(r => storedTrees.Number(r, storedTrees.Column("VTCC"))) - 5.5) < 1e-9,
                    "resultados do Processador em DLB preservam os volumes e o fator");
                await window.OpenTable(report, "RESULTADO - ARVORES");
                await window.OpenTable(report, "RESUMO - BYLOTE");
                Check(window.Documents.Children.Count(d => d.ContentId?.StartsWith("table:" + report + ":") == true) == 2,
                    "abas de resultado abrem documentos independentes");
                Capture(window, Path.Combine(root, "processor-wpf.png"));
                int chartCount = window.Session.Meta.Arr("charts").Count;
                var resultChart = new ChartView(window, window.Session, false);
                window.SetTool(resultChart);
                await resultChart.Load(report, new JsonObject { ["path"] = report, ["sheet"] = "RESUMO - BYPLOT", ["header_row"] = 1 });
                Check(window.Session.Meta.Arr("charts").Count == chartCount, "abrir gráfico de resultado DLB aguarda seleção X/Y antes de gerar");
                var chartAxes = LogicalControls<ComboBox>(resultChart).ToArray();
                chartAxes.Single(c => System.Windows.Automation.AutomationProperties.GetAutomationId(c) == "Chart.X").SelectedItem = "Idade_meses";
                chartAxes.Single(c => System.Windows.Automation.AutomationProperties.GetAutomationId(c) == "Chart.Y").SelectedItem = "VTCC_ha";
                await resultChart.Generate();
                Check(resultChart.CurrentConfig.S("sheet") == "RESUMO - BYPLOT" && resultChart.CurrentConfig.S("x") == "Idade_meses", "gráfico usa eixos e tabela de parcela dentro do resultado DLB");
                Capture(window, Path.Combine(root, "result-chart-wpf.png"));
                string resultChartId = ChartView.Identity(resultChart.CurrentConfig);
                window.Documents.Children.OfType<LayoutDocument>().Single(d => d.ContentId == resultChartId).Close();
                var temporaryChart = window.Session.Meta.Arr("charts").Single(n => n is JsonObject c && ChartView.Identity(c) == resultChartId);
                window.Session.Meta.Arr("charts").Remove(temporaryChart);
                window.Session.Touch();
                var modeControl = LogicalControls<ComboBox>(processor).Single(c => System.Windows.Automation.AutomationProperties.GetAutomationId(c) == "Processor.Mode");
                modeControl.SelectedIndex = 1;
                foreach (var check in LogicalControls<CheckBox>(processor).Where(c => System.Windows.Automation.AutomationProperties.GetAutomationId(c).StartsWith("Processor.Summary."))) {
                    check.IsChecked = System.Windows.Automation.AutomationProperties.GetAutomationId(check) == "Processor.Summary.projeto";
                    check.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                }
                Check(window.Session.Data.At("processador").S("modo") == "resumo_direto"
                    && window.Session.Data.At("processador.saidas").B("projeto") && !window.Session.Data.At("processador.saidas").B("parcela"), "modo direto captura níveis selecionados no projeto");
                // Recreate the tool to verify saved selections and avoid retaining the previous batch prefix.
                window.Session.Meta["output_prefix"] = "direto_";
                var directProcessor = new ProcessorView(window, window.Session);
                window.SetTool(directProcessor);
                await directProcessor.Process();
                var directResult = window.Session.Meta.Arr("processing_results").OfType<JsonObject>().Last();
                Check(directResult.Arr("sheets").Select(n => n.Display()).SequenceEqual(new[] { "DIRETO - PROJETO", "INFORMACOES" }), "modo direto publica somente nível escolhido e informações");
                Capture(window, Path.Combine(root, "processor-direct-wpf.png"));
                int processingCount = window.Session.Meta.Arr("processing_results").Count;
                await directProcessor.Process();
                Check(window.Session.Meta.Arr("processing_results").Count == processingCount,
                    "reprocessar atualiza resultados existentes sem criar outro banco ou duplicar registros");
                foreach (var inputPath in new[] { input, legacy }) {
                    var inputTree = LogicalControls<TreeViewItem>(window.ContentsTree).Single(n => n.Tag as string == inputPath);
                    var resultNames = inputTree.Items.OfType<TreeViewItem>().Where(n => n.Tag is MainWindow.ResultSheetReference)
                        .Select(n => ((MainWindow.ResultSheetReference)n.Tag).Sheet).ToArray();
                    Check(resultNames.Length == resultNames.Distinct().Count(), "abas de resultado aparecem uma única vez por entrada");
                }
                window.Session.Data.Obj("processador")["modo"] = "completo";
            }
            var chartView = new ChartView(window, window.Session, true);
            var originGraph = new ChartView(window, window.Session, false);
            await originGraph.Load(input, c);
            await originGraph.Generate();
            window.SetTool(chartView);
            await chartView.Load(input, fit["chart"]!.AsObject());
            Check(
                !LogicalControls<TabItem>(chartView).Any(t => t.Header?.ToString() == "Aparência")
                    && LogicalControls<TextBlock>(chartView)
                        .Any(t => t.Text == "Gráfico de origem"),
                "Regressões 1P usa Gráfico de origem e não oferece Aparência"
            );
            var inherited = window
                .Session.Meta.Arr("charts")
                .OfType<JsonObject>()
                .Single(g => ChartView.Identity(g) == ChartView.Identity(chartView.CurrentConfig))
                .DeepClone()
                .AsObject();
            inherited["point_size"] = 7;
            inherited["x_label"] = "DAP do gráfico original";
            window.Session.StoreChart(inherited);
            await chartView.Generate();
            Check(
                chartView.CurrentConfig.N("point_size") == 7
                    && chartView.CurrentConfig.S("x_label") == "DAP do gráfico original",
                "Regressões 1P herda a aparência atual do gráfico original"
            );
            var inputNode = window
                .ContentsTree.Items.OfType<TreeViewItem>()
                .First()
                .Items.OfType<TreeViewItem>()
                .Single(n => n.Tag is string p && p == input);
            Check(
                !inputNode
                    .ContextMenu!.Items.OfType<MenuItem>()
                    .Any(m => m.Header?.ToString() == "Regressões 1P…")
                    && inputNode
                        .Items.OfType<TreeViewItem>()
                        .Any(n =>
                            n.ContextMenu!.Items.OfType<MenuItem>()
                                .Any(m => m.Header?.ToString() == "Regressões 1P…")
                        ),
                "menu Regressões 1P aparece no gráfico, sem aparecer na planilha"
            );
            await chartView.RemoveRows(new[] { 1 });
            Check(
                chartView.CurrentConfig["excluded"]!.AsArray().Count == 1
                    && chartView.CurrentConfig["fits"] == null,
                "remoção invalida o ajuste anterior"
            );
            await chartView.UndoRemoval();
            Check(
                chartView.CurrentConfig["excluded"]!.AsArray().Count == 0,
                "desfazer remoção no gráfico WPF"
            );
            await chartView.Generate("fit", true);
            Check(
                chartView.CurrentConfig["fits"]!.AsArray().Count == 12,
                "comparação de modelos pela interface"
            );
            var metrics = Controls<DataGrid>(
                    ChartModels(window
                            .DockManager.Layout.Descendents()
                            .OfType<LayoutDocument>()
                            .First(d => d.ContentId == ChartView.Identity(chartView.CurrentConfig))
                            .Content)
                )
                .First();
            metrics.SelectedIndex = 1;
            Check(
                chartView.CurrentConfig.N("selected_fit", -1) == 1,
                "linha escolhida seleciona o ajuste correto"
            );
            metrics.SelectedItems.Add(metrics.Items[0]);
            Check(
                chartView.CurrentConfig["selected_fits"]!.AsArray().Count == 2
                    && Dendro.Core.Regression.FitDisplay.Visible(chartView.CurrentConfig).Length
                        == 2,
                "selecionar dois modelos mantém ambas as curvas visíveis"
            );
            var metricsView = (System.Data.DataView)metrics.ItemsSource;
            metricsView.Sort = "c1 DESC";
            metrics.SelectedItems.Clear();
            var sortedRow = metrics
                .Items.Cast<System.Data.DataRowView>()
                .First(row => (int)row["fit_index"] == 1);
            metrics.SelectedItems.Add(sortedRow);
            Check(
                chartView.CurrentConfig["selected_fits"]![0]!.GetValue<int>() == 1,
                "ordenar tabela mantém a identidade do modelo selecionado"
            );
            metrics.SelectedItems.Add(
                metrics
                    .Items.Cast<System.Data.DataRowView>()
                    .First(row => (int)row["fit_index"] == 0)
            );
            string selectedProject = Path.Combine(root, "multiple-fits.dendro");
            await window.Session.Save(selectedProject);
            var selectedSession = new ProjectSession();
            await selectedSession.Open(selectedProject);
            var storedFit = selectedSession
                .Meta.Arr("charts")
                .OfType<JsonObject>()
                .Single(c => ChartView.Identity(c) == ChartView.Identity(chartView.CurrentConfig));
            Check(
                storedFit["selected_fits"]!.AsArray().Count == 2,
                "seleção múltipla de modelos persiste no projeto"
            );
            await Application.Current.Dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.ApplicationIdle
            );
            window.UpdateLayout();
            Capture(window, Path.Combine(root, "chart-wpf.png"));
            window.ToolsPane.IsActive = true;
            Check(
                !LogicalControls<CheckBox>(chartView)
                    .Any(c => c.Content?.ToString()?.StartsWith("Selecionar pontos:") == true),
                "seleção de pontos não depende de checkbox"
            );
            var scatter = (
                ChartBody(window
                        .DockManager.Layout.Descendents()
                        .OfType<LayoutDocument>()
                        .First(d => d.ContentId == ChartView.Identity(chartView.CurrentConfig))
                        .Content)
            )
                .Children.OfType<ScatterPlot>()
                .Single();
            Check(scatter.RemoveEnabled, "seleção permanece habilitada com foco em Ferramentas");
            var firstPoint=scatter.Data!["points"]!.AsArray().First(p=>!p.B("excluded"))!;
            var filteredPreview=await chartView.PreviewPointFilter(chartView.CurrentConfig,scatter,
                [new Dendro.Core.Tabular.TableFilter(chartView.CurrentConfig.S("x"),Dendro.Core.Tabular.FilterOperator.Equal,firstPoint.N("x").ToString("G17",System.Globalization.CultureInfo.InvariantCulture))]);
            scatter.SelectRows(filteredPreview.Rows);
            Check(filteredPreview.Rows.Contains((int)firstPoint.N("row")) && scatter.SelectedRows.SequenceEqual(filteredPreview.Rows)
                && chartView.CurrentConfig["excluded"]?.AsArray().Count is null or 0,
                "filtro da tabela primeiro destaca os pontos sem excluir observações");
            scatter.ClearSelection();
            Point click = scatter.PositionOf(1);
            scatter.BeginSelection(click);
            scatter.EndSelection(click);
            Check(
                chartView.CurrentConfig["excluded"]?.AsArray().Count is null or 0
                    && scatter.SelectedRows.SequenceEqual(new[] { 1 }),
                "clique somente seleciona; não remove antes da confirmação"
            );
            scatter.RaiseEvent(
                new System.Windows.Input.KeyEventArgs(
                    System.Windows.Input.Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(window)!,
                    0,
                    System.Windows.Input.Key.Delete
                )
                {
                    RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent,
                }
            );
            await Until(() =>
                !window.IsBusy && chartView.CurrentConfig["excluded"]?.AsArray().Count == 1
            );
            Check(
                chartView.CurrentConfig["excluded"]!.AsArray()[0]!.GetValue<int>() == 1,
                "clique percorre seleção e remoção"
            );
            await chartView.UndoRemoval();
            await Application.Current.Dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.ApplicationIdle
            );
            window.UpdateLayout();
            Capture(window, Path.Combine(root, "selection-before-drag.png"));
            scatter = (
                ChartBody(window
                        .DockManager.Layout.Descendents()
                        .OfType<LayoutDocument>()
                        .First(d => d.ContentId == ChartView.Identity(chartView.CurrentConfig))
                        .Content)
            )
                .Children.OfType<ScatterPlot>()
                .Single();
            Point first = scatter.PositionOf(3),
                second = scatter.PositionOf(1);
            int[] lassoRows = [];
            scatter.SelectionChanged += rows => lassoRows = rows;
            scatter.BeginSelection(new Point(first.X - 5, first.Y - 5));
            scatter.ContinueSelection(new Point(second.X + 5, second.Y - 5));
            scatter.ContinueSelection(new Point(second.X + 5, second.Y + 5));
            scatter.ContinueSelection(new Point(first.X - 5, first.Y + 5));
            Capture(window, Path.Combine(root, "lasso-integration.png"));
            scatter.EndSelection(new Point(first.X - 5, first.Y - 5));
            Check(
                lassoRows.Order().SequenceEqual(new[] { 1, 3 }),
                "laço na regressão seleciona os IDs esperados: " + string.Join(",", lassoRows)
            );
            var plotBody = ChartBody(window
                    .DockManager.Layout.Descendents()
                    .OfType<LayoutDocument>()
                    .First(d => d.ContentId == ChartView.Identity(chartView.CurrentConfig))
                    .Content);
            var confirmRemoval = LogicalControls<Button>(plotBody)
                .First(b => b.Content?.ToString() == "Remover selecionados");
            Check(
                confirmRemoval.IsEnabled
                    && chartView.CurrentConfig["excluded"]?.AsArray().Count is null or 0,
                "seleção habilita a confirmação sem alterar removidos"
            );
            confirmRemoval.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() =>
                !window.IsBusy && chartView.CurrentConfig["excluded"]?.AsArray().Count == 2
            );
            Check(
                chartView.CurrentConfig["excluded"]!.AsArray().Count == 2,
                "contorno livre fechado remove os dois pontos abrangidos"
            );
            var removedBody = ChartBody(window
                    .DockManager.Layout.Descendents()
                    .OfType<LayoutDocument>()
                    .First(d => d.ContentId == ChartView.Identity(chartView.CurrentConfig))
                    .Content);
            var remainingPlot = removedBody.Children.OfType<ScatterPlot>().Single();
            Capture(window, Path.Combine(root, "removed-hidden.png"));
            Check(
                !remainingPlot.Config.B("show_removed") && remainingPlot.DisplayBounds.XMin > 30,
                "removidos ocultos por padrão; eixos automáticos usam somente pontos mantidos"
            );
            var showRemoved = LogicalControls<CheckBox>(removedBody)
                .Single(c => c.Content?.ToString() == "Mostrar removidos");
            showRemoved.IsChecked = true;
            Capture(window, Path.Combine(root, "removed-visible.png"));
            Check(
                remainingPlot.DisplayBounds.XMin < 10 && chartView.CurrentConfig.B("show_removed"),
                "Mostrar removidos revela os X e amplia os eixos para incluí-los"
            );
            showRemoved.IsChecked = false;
            await chartView.UndoRemoval();
            var compare = LogicalControls<Button>(chartView)
                .First(b => b.Content?.ToString() == "Comparar modelos");
            compare.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() =>
                !window.IsBusy
                && chartView.CurrentConfig["fits"] is JsonArray compared
                && compared.Count == 12
            );
            Check(
                chartView.CurrentConfig.N("selected_fit", -1) == -1,
                "comparar modelos pelo botão restaura a escolha automática"
            );
            string firstTitle = chartView.CurrentConfig.S("title"),
                firstIdentity = ChartView.Identity(chartView.CurrentConfig);
            var secondConfig = chartView.CurrentConfig.DeepClone().AsObject();
            secondConfig["title"] = "Segunda regressão";
            var secondGraph = new ChartView(window, window.Session, false);
            await secondGraph.Load(input, secondConfig);
            await chartView.Load(input, secondGraph.CurrentConfig);
            var graphSelector = LogicalControls<ComboBox>(chartView).First();
            Check(
                graphSelector.Items.Count == window.Session.Meta.Arr("charts").Count
                    && graphSelector
                        .Items.Cast<object>()
                        .Any(item => item.ToString()!.Contains(firstTitle))
                    && graphSelector
                        .Items.Cast<object>()
                        .Any(item => item.ToString()!.Contains("Segunda regressão")),
                "combo de origem lista gráficos distintos da mesma planilha"
            );
            graphSelector.SelectedIndex = graphSelector.Items.Cast<object>().ToList().FindIndex(item => item.ToString()!.Contains(firstTitle));
            await Until(() => !window.IsBusy && chartView.CurrentConfig.S("title") == firstTitle);
            Check(
                chartView.CurrentConfig.S("x") == "DAP" && chartView.CurrentConfig.S("y") == "H",
                "troca de gráfico preserva suas variáveis de origem"
            );
            graphSelector.SelectedIndex = graphSelector.Items.Cast<object>().ToList().FindIndex(item => item.ToString()!.Contains("Segunda regressão"));
            await Until(() =>
                !window.IsBusy && chartView.CurrentConfig.S("title") == "Segunda regressão"
            );
            var firstDocument = window
                .DockManager.Layout.Descendents()
                .OfType<LayoutDocument>()
                .First(d => d.ContentId == firstIdentity);
            firstDocument.IsSelected = true;
            await Until(() => !window.IsBusy && chartView.CurrentConfig.S("title") == firstTitle);
            Check(
                ReferenceEquals(window.ToolContent.Content, chartView),
                "aba de gráfico restaura sua ferramenta"
            );
            scatter = ChartBody(firstDocument.Content).Children.OfType<ScatterPlot>().Single();
            Check(scatter.RemoveEnabled, "remoção acompanha a aba reativada");
            string frontierSource = Path.Combine(
                Path.GetDirectoryName(input)!,
                "chart-ui-input.xlsx"
            );
            if (!File.Exists(frontierSource))
                frontierSource = Path.Combine(
                    Path.GetDirectoryName(Path.GetDirectoryName(input)!)!,
                    "chart-ui-input.xlsx"
                );
            if (File.Exists(frontierSource))
            {
                window.Session.AddFile(frontierSource);
                window.Session.SetExcel(frontierSource, "Dados", 1);
                var frontierConfig = new JsonObject
                {
                    ["path"] = frontierSource,
                    ["sheet"] = "Dados",
                    ["header_row"] = 1,
                    ["x"] = "Idade",
                    ["y"] = "Produção",
                    ["stratum"] = "Estrato",
                    ["title"] = "Fronteiras por estrato",
                    ["frontier"] = 3,
                    ["bins"] = 4,
                    ["model"] = 0,
                    ["parameters"] = 2,
                    ["point_size"] = 3,
                };
                var regressions = new ChartView(window, window.Session, true);
                var frontierGraph = new ChartView(window, window.Session, false);
                await frontierGraph.Load(frontierSource, frontierConfig);
                window.SetTool(regressions);
                await regressions.Load(frontierSource, frontierConfig);
                Check(!LogicalControls<Button>(regressions).Any(b => b.Content?.ToString()?.Contains("Calcular fronteira") == true),
                    "Regressões 1P não contém comando de fronteira");
                var frontierTool = new FrontierView(window, window.Session, frontierConfig);
                window.SetTool(frontierTool);
                var controls = LogicalControls<TextBox>(frontierTool).ToArray();
                controls[0].Text = "4";
                controls[1].Text = "3";
                var sideChoice = LogicalControls<ComboBox>(frontierTool).First(c => c.Items.Cast<object>().Any(i => i?.ToString() == "Superior e inferior"));
                sideChoice.SelectedIndex = 2;
                await frontierTool.Calculate();
                var frontierEntry = window.Session.Meta.Arr("analyses").OfType<JsonObject>().Last();
                Check(frontierEntry.Obj("definition").S("kind") == "frontier"
                    && frontierEntry.Obj("summary").N("points") == 16, "ferramenta independente calcula ambas as fronteiras por estrato");
                var recalculated = await AnalysisDocuments.Compute(window.Session, frontierEntry.Obj("definition"));
                Check(recalculated.Details.Count == 17 && recalculated.Plot?.Curves.Length == 4,
                    "fronteira salva pode ser reexecutada com dados e gráfico");
                Capture(window, Path.Combine(root, "frontier-wpf.png"));
                var plain = frontierConfig.DeepClone().AsObject();
                plain["title"] = "Gráfico sem regressão";
                var graph = new ChartView(window, window.Session, false);
                window.SetTool(graph);
                await graph.Load(frontierSource, plain);
                Check(
                    !LogicalControls<TabItem>(graph).Any(t => t.Header?.ToString() == "Regressão")
                        && graph.CurrentConfig["fits"] == null
                        && graph.CurrentConfig.N("frontier") == 0,
                    "Gráficos não expõe nem executa ajuste de modelos"
                );
            }
        }
        int openDocuments = window
            .DockManager.Layout.Descendents()
            .OfType<LayoutDocument>()
            .Count();
        window.ResetWorkspace();
        await Until(() => !window.IsBusy);
        Check(
            window.DockManager.Layout.Descendents().OfType<LayoutDocument>().Count()
                == openDocuments,
            "restaurar layout preserva abas e contexto de gráficos"
        );
        string layout = Path.Combine(root, "layout.xml");
        new AvalonDock.Layout.Serialization.XmlLayoutSerializer(window.DockManager).Serialize(
            layout
        );
        Check(new FileInfo(layout).Length > 100, "persistência do layout");
        window.LoadWorkspace(layout);
        Check(window.DockManager.Layout != null, "restaurar layout");
        var reopenedDocument = window.Document(
            "after-layout",
            "Após restaurar layout",
            Ui.Text("Conteúdo restaurado")
        );
        Check(
            reopenedDocument.Root == window.DockManager.Layout,
            "documentos continuam funcionais após restaurar layout"
        );
        if (args.Length > 3)
        {
            string userProject = args[3];
            var before = System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(userProject)
            );
            var compatibility = new ProjectSession();
            await compatibility.Open(userProject, migrateBank: false);
            int modelsCount = compatibility.Data!.Obj("volume").Arr("equacoes").Count;
            await compatibility.Save(Path.Combine(root, "compatibility.dendro"));
            await compatibility.Open(Path.Combine(root, "compatibility.dendro"));
            Check(
                compatibility.Data!.Obj("volume").Arr("equacoes").Count == modelsCount,
                "compatibilidade com projeto existente"
            );
            Check(
                before.SequenceEqual(
                    System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(userProject))
                ),
                "projeto original intacto"
            );
        }
        await TestTableTools(window, root);
        await TestInlineReportSettings(window, root);
        await TestRegressionCatalog(window, root);
        await TestScientificTools(window, root);
        await TestContentNames(window, root);
        await TestContentRemoval(window);
        await TestCloseLifecycle(root);
        await TestSpreadsheetMenuAfterOpening(root);
        await TestLargeMergeProgress(root);
        await TestSingleBankMigration(root);
        await window.Session.Save(Path.Combine(root, "ui-project.dendro"));
        window.CloseConfirmation = () => Task.FromResult(true);
        window.WorkspacePath = Path.Combine(root, "closing-layout.xml");
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Close();
        await Until(() => closed);
        Check(
            File.Exists(window.WorkspacePath),
            "fechar com confirmação imediata salva layout e encerra sem reentrância"
        );
        File.WriteAllText(
            Path.Combine(root, "self-test.log"),
            $"WPF + núcleo C#: {checks} verificações aprovadas.\n"
        );
    }

    private static async Task TestContentNames(MainWindow window, string root)
    {
        string bank = window.Session.ProjectReference(Path.Combine(window.Session.ProjectWorkFolder(), "Uniao_UI.dlb"));
        var hash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Dendro.Core.Tabular.CompactBankFile.Location(window.Session.Resolve(bank)).File));
        await window.OpenTable(bank);
        await WithDialog(
            () => window.RenameContent(bank),
            "Renomear",
            inspect: dialog =>
            {
                var input = LogicalControls<TextBox>(dialog)
                    .Single(t =>
                        System.Windows.Automation.AutomationProperties.GetAutomationId(t)
                        == "ContentName"
                    );
                input.Text = "Inventários anuais";
            }
        );
        Check(
            window.Session.ContentName(bank) == "Inventários anuais",
            "renomear conteúdo altera o nome exibido"
        );
        Check(
            hash.SequenceEqual(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Dendro.Core.Tabular.CompactBankFile.Location(window.Session.Resolve(bank)).File))
            ),
            "renomear mantém os dados de origem intactos"
        );
        var tableDoc = window
            .DockManager.Layout.Descendents()
            .OfType<LayoutDocument>()
            .Single(d => d.ContentId == "table:" + bank);
        Check(
            tableDoc.Title == "Tabela · Inventários anuais",
            "aba da tabela acompanha o nome do conteúdo"
        );
        var graph = window.Session.Meta.Arr("charts").OfType<JsonObject>().First();
        string key = ChartView.Identity(graph);
        int count = window.Session.Meta.Arr("charts").Count;
        await WithDialog(
            () => window.RenameContent(graph.S("path"), graph),
            "Renomear",
            inspect: dialog =>
            {
                LogicalControls<TextBox>(dialog)
                    .Single(t =>
                        System.Windows.Automation.AutomationProperties.GetAutomationId(t)
                        == "ContentName"
                    )
                    .Text = "Relação DAP e altura";
            }
        );
        Check(
            window.Session.ContentName(graph.S("path"), graph) == "Relação DAP e altura"
                && ChartView.Identity(graph) == key
                && window.Session.Meta.Arr("charts").Count == count,
            "renomear gráfico preserva identidade e ajustes"
        );
        await WithDialog(() => window.RenameContent(bank), "Cancelar");
        Check(
            window.Session.ContentName(bank) == "Inventários anuais",
            "cancelar mantém o nome anterior"
        );
        bool invalid = false;
        try
        {
            window.Session.RenameContent(bank, "   ");
        }
        catch (InvalidOperationException)
        {
            invalid = true;
        }
        Check(
            invalid && window.Session.ContentName(bank) == "Inventários anuais",
            "nomes vazios são rejeitados sem alteração"
        );
        string project = Path.Combine(root, "renamed.dendro");
        await window.Session.Save(project);
        var restored = new ProjectSession();
        await restored.Open(project);
        Check(
            restored.ContentName(restored.Files.Single(p => Path.GetFileName(p) == "Uniao_UI.dlb")) == "Inventários anuais"
                && restored.ContentName(graph.S("path"), graph) == "Relação DAP e altura",
            "nomes dos conteúdos persistem ao reabrir projeto"
        );
        window.ToolSearch.Text = "";
        // Exercise the real search event to show the complete catalogue.
        window.ToolSearch.Text = " ";
        window.ToolSearch.Text = "";
        await Application.Current.Dispatcher.InvokeAsync(
            () => { },
            DispatcherPriority.ApplicationIdle
        );
        window.UpdateLayout();
        Check(Controls<AvalonDock.Controls.LayoutAnchorablePaneControl>(window).All(p =>
            p.Background is System.Windows.Media.SolidColorBrush b && b.Color.A == 0 && p.BorderThickness == new Thickness(0)),
            "contêiner do encaixe transparente, sem caixa retangular atrás da moldura");
        Check(Controls<RoundedPanel>(window).All(p => p.Clip == null && p.Child?.Clip != null),
            "conteúdo recortado dentro da borda, preservando o traço arredondado");
        Check(
            Controls<RoundedPanel>(window).Count(p => p.CornerRadius.TopLeft == 10) >= 3,
            "painéis de Conteúdos, Ferramentas e Histórico usam moldura arredondada"
        );
        Check(Controls<AvalonDock.Controls.AnchorablePaneTitle>(window).All(t => t.BorderThickness == new Thickness(0)
            && t.Background == System.Windows.Media.Brushes.Transparent), "cabeçalhos dos painéis não desenham uma segunda caixa interna");
        Check(window.Session.Caption.EndsWith("DendroLab") && window.Icon != null,
            "nome DendroLab e novo ícone na janela");
        Check(
            Controls<CommandIcon>(window).Count() > 12,
            "ícones vetoriais presentes na faixa de opções, árvore e ferramentas"
        );
        var toolIcons = ((ListBox)window.ToolContent.Content).Items.OfType<ListBoxItem>().Select(ShellVisuals.GetIcon).ToArray();
        Check(toolIcons.Length == 9 && toolIcons.Distinct().Count() == 9,
            "cada ferramenta possui um ícone próprio");
        Check(!Controls<Image>(window.Ribbon.Parent as DependencyObject ?? window).Any(i => i.Source?.ToString()?.Contains("logo.png") == true),
            "logo removido do canto superior direito da faixa de opções");
        Capture(window, Path.Combine(root, "modern-shell-wpf.png"));
    }

    private static async Task TestSingleBankMigration(string root)
    {
        string path = Path.Combine(root, "legacy-bank-project.dendro");
        var session = new ProjectSession();
        await session.New("Migração do banco"); await session.Save(path);
        string bank = Path.ChangeExtension(path, ".dlb"); Directory.CreateDirectory(bank);
        File.WriteAllText(Path.Combine(bank, "bank.json"), "{\"format\":\"DENDROLAB_GEODATABASE\",\"version\":2}");
        string oldTable = Path.Combine(bank, "Entrada.dlb");
        using (var writer = new Dendro.Core.Tabular.CompactBankWriter(oldTable)) {
            string id = writer.AddTable(["Idade", "Volume"]); writer.Add(id, [10d, 100d]); writer.Complete();
        }
        session.Meta["database_path"] = Path.GetFileName(bank);
        string reference = Path.Combine(Path.GetFileName(bank), "Entrada.dlb");
        session.AddFile(reference); session.SetExcel(reference, "Dados", 1);
        Dendro.Core.Configuration.ProjectStore.Save(path, session.Data!);
        await session.Open(path);
        Check(File.Exists(bank) && !Directory.Exists(bank), "abrir projeto antigo converte pasta DLB em arquivo único");
        Check(Dendro.Core.Tabular.TableStore.Page(new(session.Resolve(reference))).Data.Number(1, 1) == 100,
            "migração preserva tabelas e referências existentes");
        Check(Directory.GetDirectories(root, "legacy-bank-project.dlb.legacy-backup-*").Length == 1,
            "migração preserva cópia da pasta original");
        string copy = Path.Combine(root, "single-bank-copy.dendro"); await session.Save(copy);
        Check(File.Exists(Path.ChangeExtension(copy, ".dlb")) && !Directory.Exists(Path.ChangeExtension(copy, ".dlb")),
            "Salvar como copia somente um arquivo de banco");
        Check(Dendro.Core.Tabular.TableStore.Page(new(session.Resolve(session.Files.Single()))).Total == 1,
            "tabela permanece acessível no banco copiado");
    }

    private static async Task TestContentRemoval(MainWindow window)
    {
        string parent = window.Session.Files.Single(p => Path.GetFileName(p) == "Uniao_UI.dlb");
        var children = window.Session.Meta.Arr("derived_tables").OfType<JsonObject>()
            .Where(d => d.S("source_path") == parent).Select(d => d.S("path")).ToArray();
        var removed = children.Append(parent).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var physical = removed.Select(window.Session.Resolve).ToArray();
        int otherFiles = window.Session.Files.Count(p => !removed.Contains(p));
        await window.OpenTable(parent);
        var node = LogicalControls<TreeViewItem>(window.ContentsTree).Single(n => n.Tag as string == parent);
        var command = node.ContextMenu.Items.OfType<MenuItem>().Single(m => m.Header?.ToString() == "Remover");
        command.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => !window.IsBusy && !window.Session.Files.Contains(parent));
        Check(children.Length == 2 && window.Session.Files.Count() == otherFiles
            && !window.Session.Files.Any(removed.Contains), "remover tabela pai remove os estratos descendentes dos Conteúdos");
        Check(!LogicalControls<TreeViewItem>(window.ContentsTree).Any(n => n.Tag is string p && removed.Contains(p)),
            "subitens removidos não reaparecem na raiz da árvore");
        Check(physical.All(Dendro.Core.Tabular.CompactBankFile.Exists), "remoção de conteúdos preserva os dados físicos no banco DLB");
    }

    private static async Task TestScientificTools(MainWindow window, string root)
    {
        var table = new Dendro.Core.Spreadsheets.DataTable([
            "Idade",
            "Volume",
            "Grupo",
            "Antes",
            "Depois",
        ]);
        for (int i = 1; i <= 15; i++)
            foreach (string group in new[] { "A", "B" })
                table.Add(
                    (double)i,
                    (group == "A" ? 2 : 15) + 3d * i + (i % 3 - 1) * .2,
                    group,
                    (double)i,
                    i + .3 * (i % 3) + (group == "A" ? .5 : 1)
                );
        string path = Path.Combine(root, "scientific-input.xlsx");
        Dendro.Core.Spreadsheets.WorkbookWriter.Write(path, [new("Dados", table)]);
        window.Session.AddFile(path);
        window.Session.SetExcel(path, "Dados", 1);
        await window.OpenStatistics(true, path);
        var tool = (StatisticalTestsView)window.ToolContent.Content;
        ComboBox Combo(string name) =>
            LogicalControls<ComboBox>(tool)
                .Single(c =>
                    System.Windows.Automation.AutomationProperties.GetAutomationId(c)
                    == "Statistics." + name
                );
        Combo("X").SelectedItem = "Idade";
        Combo("Value").SelectedItem = "Volume";
        Combo("Group").SelectedItem = "Grupo";
        LogicalControls<Button>(tool)
            .Single(b => b.Content?.ToString() == "Listar grupos")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !window.IsBusy);
        int countBefore = window.Session.Meta.Arr("analyses").Count;
        for (int test = 0; test < 6; test++)
        {
            Combo("Test").SelectedIndex = test;
            Combo("Value").SelectedItem = test == 2 ? "Antes" : "Volume";
            Combo("Second").SelectedItem = "Depois";
            LogicalControls<Button>(tool)
                .Single(b => b.Content?.ToString() == "Executar teste")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !window.IsBusy);
            Check(
                window.Session.Meta.Arr("analyses").Count == countBefore + test + 1,
                "teste científico gera resultado pela interface: " + test
            );
        }
        var identity = window
            .Session.Meta.Arr("analyses")
            .OfType<JsonObject>()
            .First(a => a.Obj("definition").S("kind") == "statistics");
        Check(
            identity.Obj("summary").N("p_value") < .05 && identity.Obj("summary").N("df1") == 2,
            "identidade de modelos separa equações diferentes pela interface"
        );
        await AnalysisDocuments.OpenSaved(window, window.Session, identity);
        Check(
            window.Session.Meta.Arr("analyses").Count == countBefore + 6,
            "abrir resultado salvo não duplica análise"
        );
        var analysisDoc = window
            .DockManager.Layout.Descendents()
            .OfType<LayoutDocument>()
            .Single(d => d.ContentId == "analysis:" + identity.S("id"));
        LogicalControls<Button>((DependencyObject)analysisDoc.Content)
            .Single(b => b.Content?.ToString() == "Reexecutar análise")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !window.IsBusy);
        await Application.Current.Dispatcher.InvokeAsync(
            () => { },
            DispatcherPriority.ApplicationIdle
        );
        window.UpdateLayout();
        Capture(window, Path.Combine(root, "identity-test-wpf.png"));
        window
            .Session.Data!.Obj("curvas_crescimento")
            .Arr("equacoes")
            .Add(
                new JsonObject
                {
                    ["codigo"] = "scientific",
                    ["modelo"] = "schumacher",
                    ["ano"] = 2026,
                    ["vigente"] = true,
                    ["formula"] = "exp(b0+b1/t)",
                    ["unidade_idade"] = "anos",
                    ["b0"] = Math.Log(1000),
                    ["b1"] = -10,
                    ["b2"] = 0,
                }
            );
        window.Session.Touch();
        var increments = new GrowthValidationView(window, window.Session);
        window.SetTool(increments);
        var choice = LogicalControls<DataGrid>(increments)
            .Single()
            .ItemsSource.Cast<GrowthValidationView.CurveChoice>()
            .Single(c => !c.FromChart && c.Name.StartsWith("scientific"));
        choice.Selected = true;
        LogicalControls<Button>(increments)
            .Single(b => b.Content?.ToString() == "Analisar ICA e IMA")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !window.IsBusy);
        var result = window.Session.Meta.Arr("analyses").OfType<JsonObject>().Last();
        var crossing = result.Obj("summary")["curves"]![0]!["crossings"]!.AsArray();
        Check(
            crossing.Count == 1 && Math.Abs(crossing[0]!.N("age") - 10) < 1e-5,
            "ICA e IMA mostram a culminação do Schumacher na idade esperada"
        );
        await Application.Current.Dispatcher.InvokeAsync(
            () => { },
            DispatcherPriority.ApplicationIdle
        );
        window.UpdateLayout();
        Capture(window, Path.Combine(root, "ica-ima-wpf.png"));
        Check(
            window
                .ContentsTree.Items.OfType<TreeViewItem>()
                .Any(t => t.Header?.ToString() == "Análises"),
            "diagnósticos sem tabela de origem aparecem em Análises nos Conteúdos"
        );
        string saved = Path.Combine(root, "scientific-project.dendro");
        await window.Session.Save(saved);
        var restored = new ProjectSession();
        await restored.Open(saved);
        Check(
            restored.Meta.Arr("analyses").Count == countBefore + 7,
            "resultados e parâmetros científicos persistem no projeto"
        );
    }

    private static async Task TestInlineReportSettings(MainWindow window, string root)
    {
        var before = window.Session.Data!.DeepClone();
        await WithDialog(
            () => SettingsView.Show(window, window.Session),
            "Aplicar configurações",
            inspect: dialog =>
            {
                var tabs = LogicalControls<TabControl>(dialog).First();
                var inventoryTab = tabs.Items.OfType<TabItem>().Single(t => t.Header?.ToString() == "Campos do inventário");
                tabs.SelectedItem = inventoryTab;
                var inventoryEditor = (StructuredEditor)inventoryTab.Content;
                Check(LogicalControls<CheckBox>(inventoryEditor).Any(c => System.Windows.Automation.AutomationProperties.GetAutomationId(c) == "declinacoes.comprimento_vazio_zero")
                    && LogicalControls<CheckBox>(inventoryEditor).Any(c => System.Windows.Automation.AutomationProperties.GetAutomationId(c) == "declinacoes.largura_vazio_zero"), "declinações vazias possuem opções independentes em Configurações");
                var identityTab = tabs.Items.OfType<TabItem>().Single(t => t.Header?.ToString() == "Identificação");
                var identityGrid = LogicalControls<DataGrid>((IdentitySettingsView)identityTab.Content).Single();
                Check(identityGrid.Columns.Count == 4 && identityGrid.ItemsSource.Cast<IdentitySettingsView.FieldRow>().Any(r => r.Tree && r.Plot),
                    "configuração de chaves usa grade de árvore, parcela e base");
                Check(LogicalControls<HelpIcon>(inventoryEditor).Any(h => h.Text.Contains("normaliza")), "explicação de unidades acessível no tooltip");
                Check(LogicalControls<System.Windows.Controls.Primitives.UniformGrid>(inventoryEditor).Any(g => g.Columns == 3), "mapeamentos do inventário organizados em três colunas");
                Check(!LogicalControls<Button>(inventoryEditor).Any(b => b.Content?.ToString()?.StartsWith("Editar ") == true), "campos do inventário editam colunas sem subjanela");
                LogicalControls<TextBox>(inventoryEditor).Single(t => System.Windows.Automation.AutomationProperties.GetAutomationId(t) == "colunas.dap").Text = "DAP";
                var registryTab = tabs.Items.OfType<TabItem>().Single(t => t.Header?.ToString() == "Cadastro florestal");
                tabs.SelectedItem = registryTab;
                var registryEditor = (StructuredEditor)registryTab.Content;
                Check(!LogicalControls<Button>(registryEditor).Any(b => b.Content?.ToString()?.StartsWith("Editar Mapeamento") == true)
                    && LogicalControls<TextBox>(registryEditor).Any(t => System.Windows.Automation.AutomationProperties.GetAutomationId(t) == "colunas.data_plantio"), "colunas do cadastro editáveis na própria tela");
                LogicalControls<CheckBox>(registryEditor).Single(c => System.Windows.Automation.AutomationProperties.GetAutomationId(c) == "fallback_idade.ativo").IsChecked = true;
                LogicalControls<TextBox>(registryEditor).Single(c => System.Windows.Automation.AutomationProperties.GetAutomationId(c) == "fallback_idade.limite_ano_2000").Text = "29";
                Check(!LogicalControls<Button>(registryEditor).Any(b => b.Content?.ToString()?.Contains("Editar Fallback") == true), "fallback editável diretamente no cadastro florestal");
                var ifc = tabs
                    .Items.OfType<TabItem>()
                    .Single(t => t.Header?.ToString() == "Comparação IFC");
                tabs.SelectedItem = ifc;
                var editor = (StructuredEditor)ifc.Content;
                Check(!LogicalControls<CheckBox>(editor).Any(c => System.Windows.Automation.AutomationProperties.GetAutomationId(c) == "comparativo_ifc.ativo"), "comparação IFC sempre ativa sem checkbox");
                var heightTab = tabs.Items.OfType<TabItem>().Single(t => t.Header?.ToString() == "Altura e RNA");
                var heightMode = LogicalControls<ComboBox>((StructuredEditor)heightTab.Content).Single(c => System.Windows.Automation.AutomationProperties.GetAutomationId(c) == "ajuste_altura.h_mode");
                Check(!heightMode.IsEditable && heightMode.Items.Count == 3, "altura oferece somente os três modos implementados");
                heightMode.SelectedIndex = 2;
                Check(((StructuredEditor)heightTab.Content).Read().S("h_mode") == "FINETUNE_MODEL", "rótulo da RNA preserva código do modo selecionado");
                heightMode.SelectedIndex = 0;
                Check(
                    !LogicalControls<Button>(editor)
                        .Any(b => b.Content?.ToString()?.StartsWith("Editar ") == true),
                    "Comparação IFC oferece limiares e descrições sem abrir subjanela"
                );
                TextBox Field(string id) =>
                    LogicalControls<TextBox>(editor)
                        .Single(t =>
                            System.Windows.Automation.AutomationProperties.GetAutomationId(t) == id
                        );
                Field("limiares.verde").Text = "7";
                Field("limiares.amarelo").Text = "18";
                Field("descricao.verde").Text = "Dentro da faixa do projeto";
                var output = tabs
                    .Items.OfType<TabItem>()
                    .Single(t => t.Header?.ToString() == "Relatório de saída");
                tabs.SelectedItem = output;
                var outputEditor = (StructuredEditor)output.Content;
                Check(
                    !LogicalControls<Button>(outputEditor)
                        .Any(b => b.Content?.ToString()?.StartsWith("Editar ") == true),
                    "Relatório de saída oferece seleção de abas no próprio espaço"
                );
                LogicalControls<CheckBox>(outputEditor)
                    .Single(c =>
                        System.Windows.Automation.AutomationProperties.GetAutomationId(c)
                        == "sheets.graficos"
                    )
                    .IsChecked = false;
                Check(
                    JsonNode.DeepEquals(before, window.Session.Data),
                    "edições inline ficam pendentes até aplicar Configurações"
                );
            },
            inspectAsync: async dialog =>
            {
                var tabs = LogicalControls<TabControl>(dialog).First();
                foreach (string title in new[] { "Campos do inventário", "Cadastro florestal", "Altura e RNA", "Processador", "Identificação" })
                {
                    tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => t.Header?.ToString() == title);
                    await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Capture(dialog, Path.Combine(root, "settings-" + (title == "Campos do inventário" ? "inventory" : title == "Cadastro florestal" ? "registry" : title == "Altura e RNA" ? "height" : title == "Identificação" ? "identity" : "processor") + ".png"));
                    if (title == "Processador")
                    {
                        var icon = LogicalControls<HelpIcon>((StructuredEditor)((TabItem)tabs.SelectedItem).Content).First(h => h.Text.Contains("revisão e aprovação"));
                        var popup = (ToolTip)icon.ToolTip;
                        popup.IsOpen = true;
                        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        popup.UpdateLayout();
                        Check(popup.IsOpen && ((TextBlock)popup.Content).Text == icon.Text && popup.ActualWidth is > 100 and < 500,
                            "tooltip preserva explicação inteira em largura legível");
                        Capture(popup, Path.Combine(root, "help-tooltip.png"));
                        popup.IsOpen = false;
                    }
                }
                tabs.SelectedItem = tabs
                    .Items.OfType<TabItem>()
                    .Single(t => t.Header?.ToString() == "Comparação IFC");
                await Application.Current.Dispatcher.InvokeAsync(
                    () => { },
                    DispatcherPriority.ApplicationIdle
                );
                Capture(dialog, Path.Combine(root, "ifc-inline-wpf.png"));
                tabs.SelectedItem = tabs
                    .Items.OfType<TabItem>()
                    .Single(t => t.Header?.ToString() == "Relatório de saída");
                await Application.Current.Dispatcher.InvokeAsync(
                    () => { },
                    DispatcherPriority.ApplicationIdle
                );
                Capture(dialog, Path.Combine(root, "report-inline-wpf.png"));
            }
        );
        Check(
            window.Session.Data!.At("comparativo_ifc.limiares").N("verde") == 7
                && window.Session.Data.At("comparativo_ifc.limiares").N("amarelo") == 18
                && window.Session.Data.At("comparativo_ifc.descricao").S("verde")
                    == "Dentro da faixa do projeto",
            "limiares e descrições inline aplicados ao projeto"
        );
        Check(
            !window.Session.Data.At("saida.sheets").B("graficos", true),
            "seleção inline de sheets aplicada ao projeto"
        );
        Check(window.Session.Data.At("cadastro.fallback_idade").B("ativo") && window.Session.Data.At("cadastro.fallback_idade").N("limite_ano_2000") == 29, "fallback configurável aplicado ao projeto");
        string path = Path.Combine(root, "inline-settings.dendro");
        await window.Session.Save(path);
        var restored = new ProjectSession();
        await restored.Open(path);
        Check(restored.Data!.At("cadastro.fallback_idade").B("ativo") && restored.Data.At("cadastro.fallback_idade").N("limite_ano_2000") == 29, "fallback persiste ao reabrir projeto");
        Check(
            restored.Data!.At("comparativo_ifc.limiares").N("verde") == 7
                && !restored.Data.At("saida.sheets").B("graficos", true),
            "campos inline persistem ao reabrir projeto"
        );
    }

    private static async Task TestRegressionCatalog(MainWindow window, string root)
    {
        int before = window.Session.Data!.Obj("regressoes").Arr("modelos").Count;
        await WithDialog(
            () => SettingsView.Show(window, window.Session),
            "Aplicar configurações",
            inspect: dialog =>
            {
                var sections = LogicalControls<TabControl>(dialog).First();
                var section = sections
                    .Items.OfType<TabItem>()
                    .Single(t => t.Header?.ToString() == "Modelos de regressão");
                sections.SelectedItem = section;
                var editor = (RegressionCatalogView)section.Content;
                var tabs = LogicalControls<TabControl>(editor).Single();
                Check(tabs.Items.Count == 3, "Configurações centraliza modelos 1P, 2P e 3P");
                for (int p = 1; p <= 3; p++)
                {
                    tabs.SelectedIndex = p - 1;
                    LogicalControls<Button>(editor)
                        .Single(b => b.Content?.ToString() == "Novo modelo")
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var fields = LogicalControls<TextBox>(editor).ToArray();
                    fields[0].Text = "teste_" + p + "p";
                    fields[1].Text = "Regressão do projeto " + p + "P";
                    fields[2].Text =
                        "b0" + string.Concat(Enumerable.Range(1, p).Select(i => $"+b{i}*x{i}"));
                    fields[3].Text = (p + 1).ToString();
                    LogicalControls<Button>(editor)
                        .Single(b => b.Content?.ToString() == "Salvar modelo")
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                Check(
                    window.Session.Data!.Obj("regressoes").Arr("modelos").Count == before,
                    "edições do cadastro só chegam ao projeto ao aplicar Configurações"
                );
            },
            inspectAsync: async dialog =>
            {
                await Application.Current.Dispatcher.InvokeAsync(
                    () => { },
                    DispatcherPriority.ApplicationIdle
                );
                dialog.UpdateLayout();
                Capture(dialog, Path.Combine(root, "regression-catalog-wpf.png"));
            }
        );
        Check(
            window.Session.Data!.Obj("regressoes").Arr("modelos").Count == before + 3,
            "modelos dos três grupos cadastrados pela interface"
        );
        var origin = window.Session.Meta.Arr("charts").OfType<JsonObject>().First();
        var tool = new ChartView(window, window.Session, true);
        window.SetTool(tool);
        await tool.Load(origin.S("path"), origin);
        await tool.Generate("fit", true);
        Check(
            tool.CurrentConfig["fits"]!.AsArray().Count == 13,
            "Comparar modelos usa todos os modelos 1P cadastrados no projeto"
        );
        Check(
            tool.CurrentConfig["fits"]!.AsArray().Any(f => f.S("model_id") == "teste_1p")
                && !tool.CurrentConfig["fits"]!
                    .AsArray()
                    .Any(f => f.S("model_id") is "teste_2p" or "teste_3p"),
            "Regressões 1P filtra o catálogo por quantidade de preditores"
        );
        string saved = Path.Combine(root, "regression-catalog.dendro");
        await window.Session.Save(saved);
        var restored = new ProjectSession();
        await restored.Open(saved);
        Check(
            restored.Data!.Obj("regressoes").Arr("modelos").Count == before + 3,
            "catálogo completo persiste no projeto"
        );
    }

    private static async Task TestCloseLifecycle(string root)
    {
        var window = new MainWindow
        {
            Left = -30000,
            Top = -30000,
            ShowInTaskbar = false,
            WorkspacePath = Path.Combine(root, "close-cancel.xml"),
        };
        window.Show();
        bool closed = false;
        int confirmations = 0;
        window.Closed += (_, _) => closed = true;
        window.CloseConfirmation = () =>
        {
            confirmations++;
            return Task.FromResult(false);
        };
        window.Close();
        await Until(() => confirmations == 1);
        await Task.Delay(30);
        Check(
            !closed && window.IsVisible && !File.Exists(window.WorkspacePath),
            "cancelar fechamento mantém a janela aberta"
        );
        var answer = new TaskCompletionSource<bool>();
        confirmations = 0;
        window.CloseConfirmation = () =>
        {
            confirmations++;
            return answer.Task;
        };
        window.ToolsPane.Float();
        window.HistoryPane.Hide();
        window.Close();
        window.Close();
        await Until(() => confirmations == 1);
        Check(!closed, "fechamentos repetidos aguardam a mesma confirmação pendente");
        answer.SetResult(true);
        await Until(() => closed);
        Check(
            confirmations == 1 && File.Exists(window.WorkspacePath),
            "fechar com painel flutuante e oculto encerra após confirmação assíncrona"
        );
    }

    private static async Task TestSpreadsheetMenuAfterOpening(string root)
    {
        string path = Path.Combine(root, "menu-project.dendro");
        Dendro.Core.Configuration.ProjectStore.Save(path, Dendro.Core.Configuration.ProjectStore.Create("Menu"));
        var window = new MainWindow {
            Left = -30000, Top = -30000, ShowInTaskbar = false,
            WorkspacePath = Path.Combine(root, "menu-layout.xml"),
            CloseConfirmation = () => Task.FromResult(true),
        };
        window.Show();
        MenuItem AddCommand() => window.ContentsTree.Items.OfType<TreeViewItem>()
            .Single(n => n.Header?.ToString() == "Planilhas").ContextMenu.Items.OfType<MenuItem>()
            .Single(m => m.Header?.ToString() == "Adicionar planilha…");
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(!AddCommand().IsEnabled, "adicionar planilha desabilitado sem projeto");
        await window.OpenProject(path);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(AddCommand().IsEnabled, "adicionar planilha habilitado após abertura real do projeto");
        var completion = new TaskCompletionSource();
        var operation = window.Execute(() => { window.Session.Touch(); return completion.Task; });
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(!AddCommand().IsEnabled, "menu acompanha estado ocupado após reconstrução da árvore");
        completion.SetResult();
        await operation;
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(AddCommand().IsEnabled, "menu reabilita ao concluir operação sem reconstruir a árvore");
        window.Close();
        await Until(() => !window.IsVisible);
    }

    private static async Task TestLargeMergeProgress(string root)
    {
        const int sourceCount = 32, rowsPerSource = 8192;
        string inputsFolder = Path.Combine(root, "merge-stress-inputs");
        Directory.CreateDirectory(inputsFolder);
        var fixtures = await Task.Run(() => Enumerable.Range(0, sourceCount).Select(i => {
            string path = Path.Combine(inputsFolder, $"medicoes_{i:D2}.dlb");
            using var writer = new Dendro.Core.Tabular.CompactBankWriter(path);
            string id = writer.AddTable(["ID", "Grupo", "Volume"]);
            for (int r = 0; r < rowsPerSource; r++) writer.Add(id, [(double)(i * rowsPerSource + r), "Estrato A", r * .1]);
            writer.Complete();
            return new Dendro.Core.Tabular.TableResult(path, "Dados", rowsPerSource);
        }).ToArray());
        var window = new MainWindow {
            Left = -30000, Top = -30000, ShowInTaskbar = false,
            WorkspacePath = Path.Combine(root, "merge-stress-layout.xml"),
            CloseConfirmation = () => Task.FromResult(true),
        };
        window.Show();
        await window.Session.New("Junção grande");
        await window.Session.Save(Path.Combine(root, "merge-stress.dendro"));
        string bank = window.Session.ProjectWorkFolder();
        await window.Session.AddTableResults(fixtures);
        await window.OpenTableTool(false);
        var view = (TableToolsView)window.ToolContent.Content;
        foreach (var input in LogicalControls<DataGrid>(view).First().ItemsSource.Cast<TableToolsView.InputRow>()) input.Selected = true;
        var name = LogicalControls<TextBox>(view).Single(); name.Text = "unidas";
        var run = LogicalControls<Button>(view).Single(b => b.Content?.ToString() == "Juntar tabelas");
        int ticksWhileBusy = 0, registrations = 0;
        bool sawProgress = false, sawCancel = false;
        window.Session.Changed += () => registrations++;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => {
            if (!window.IsBusy) return;
            ticksWhileBusy++;
            sawProgress |= window.Progress.IsIndeterminate || window.Progress.Value > 0;
            sawCancel |= window.CancelOperationButton.IsVisible && window.CancelOperationButton.IsEnabled;
        };
        timer.Start();
        run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Capture(window, Path.Combine(root, "merge-progress-wpf.png"));
        await Until(() => !window.IsBusy);
        timer.Stop();
        var result = Dendro.Core.Tabular.TableStore.Page(new(window.Session.Resolve(window.Session.ProjectReference(Path.Combine(bank, "unidas.dlb")))), sourceCount * rowsPerSource - 1, 1);
        Check(result.Total == sourceCount * rowsPerSource && result.Data.Number(1, 0) == sourceCount * rowsPerSource - 1,
            "junção de 32 tabelas e 262.144 linhas mantém ordem e contagem");
        Check(ticksWhileBusy > 0 && sawProgress && sawCancel,
            "dispatcher continua atendendo eventos, com progresso e cancelamento durante a junção");
        Check(registrations == 1 && window.Progress.Value == 100 && !window.Progress.IsIndeterminate,
            "resultado atualiza os Conteúdos uma única vez e conclui a barra de progresso");
        name.Text = "cancelada";
        run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(window.IsBusy && window.CancelOperationButton.IsEnabled, "cancelamento permanece acessível fora do painel desabilitado");
        window.CancelOperationButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !window.IsBusy);
        Check(!File.Exists(Path.Combine(bank, "cancelada.dlb")) && !Directory.EnumerateDirectories(bank, ".dendro-*").Any()
            && !window.Session.Files.Any(p => Path.GetFileName(p) == "cancelada.dlb"),
            "cancelar pela interface limpa temporários sem publicar ou registrar resultado parcial");
        window.Close(); await Until(() => !window.IsVisible);
    }

    private static async Task TestTableTools(MainWindow window, string root)
    {
        Check(window.Icon != null, "ícone do aplicativo disponível na janela WPF");
        var table = new Dendro.Core.Spreadsheets.DataTable(["Ano", "Grupo", "Valor"]);
        table.Add(2025d, "A", 10d);
        table.Add(2026d, "B", 20d);
        string first = Path.Combine(root, "anual1.xlsx"),
            second = Path.Combine(root, "anual2.xlsx");
        Dendro.Core.Spreadsheets.WorkbookWriter.Write(first, [new("Dados", table)]);
        var extended = new Dendro.Core.Spreadsheets.DataTable(["Ano", "Grupo", "Valor", "Extra"]);
        extended.Add(2025d, "A", 10d, "somente segunda");
        extended.Add(2026d, "B", 20d, "somente segunda");
        Dendro.Core.Spreadsheets.WorkbookWriter.Write(second, [new("Dados", extended)]);
        window.Session.AddFile(first);
        window.Session.SetExcel(first, "Dados", 1);
        window.Session.AddFile(second);
        window.Session.SetExcel(second, "Dados", 1);
        await window.OpenTableTool(false);
        var merge = (TableToolsView)window.ToolContent.Content;
        var rows = LogicalControls<DataGrid>(merge)
            .First()
            .ItemsSource.Cast<TableToolsView.InputRow>()
            .ToArray();
        foreach (var row in rows)
            row.Selected = row.Path == first || row.Path == second;
        var text = LogicalControls<TextBox>(merge).ToArray();
        text[0].Text = "Uniao_UI";
        var commonColumns = LogicalControls<CheckBox>(merge).Single(c =>
            System.Windows.Automation.AutomationProperties.GetAutomationId(c) == "Merge.CommonColumns");
        Check(commonColumns.IsChecked == false, "junção mantém modo estrito por padrão");
        commonColumns.IsChecked = true;


        LogicalControls<Button>(merge)
            .Single(b => b.Content?.ToString() == "Juntar tabelas")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !window.IsBusy);
        string bank = window.Session.ProjectReference(Path.Combine(window.Session.ProjectWorkFolder(), "Uniao_UI.dlb"));
        Check(
            Dendro.Core.Tabular.CompactBankFile.Exists(window.Session.Resolve(bank)) && window.Session.Files.Contains(bank),
            "junção pela ferramenta registra banco de saída"
        );
        await window.OpenTable(bank);
        var document = window
            .DockManager.Layout.Descendents()
            .OfType<LayoutDocument>()
            .Single(d => d.ContentId == "table:" + bank);
        Check(
            document.Content is TableBrowser browser && browser.Total == 4,
            "banco abre em tabela paginada"
        );
        Check(Dendro.Core.Tabular.TableStore.Page(new(window.Session.Resolve(bank))).Data.Width == 4,
            "checkbox permite juntar planilhas diferentes usando somente suas colunas comuns");
        await window.OpenTableTool(true, bank);
        var stratify = (TableToolsView)window.ToolContent.Content;
        var combos = LogicalControls<ComboBox>(stratify).ToArray();
        combos.Single(c => c.Items.Contains("Grupo")).SelectedItem = "Grupo";

        LogicalControls<Button>(stratify)
            .Single(b => b.Content?.ToString() == "Listar estratos")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !window.IsBusy);
        var groups = LogicalControls<DataGrid>(stratify)
            .Single(g =>
                g.ItemsSource
                is System.Collections.ObjectModel.ObservableCollection<TableToolsView.GroupRow>
            );
        Check(groups.Items.Count == 2, "estratificação descobre grupos da tabela de banco");
        foreach (var row in groups.ItemsSource.Cast<TableToolsView.GroupRow>())
            row.Name = "UI_" + row.Value;
        window.UpdateLayout();
        Capture(window, Path.Combine(root, "table-tools-wpf.png"));
        LogicalControls<Button>(stratify)
            .Single(b => b.Content?.ToString() == "Gerar estratos")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !window.IsBusy);
        var bankNode = LogicalControls<TreeViewItem>(window.ContentsTree).Single(i => i.Tag as string == bank);
        Check(
            bankNode.Items.OfType<TreeViewItem>().Count(i => i.Tag is string p && p.Contains("UI_"))
                == 2,
            "resultados dos estratos ficam abaixo da origem"
        );
        string saved = Path.Combine(root, "tables-ui.dendro");
        await window.Session.Save(saved);
        bank = window.Session.Files.Single(p => Path.GetFileName(p) == "Uniao_UI.dlb");
        var restored = new ProjectSession();
        await restored.Open(saved);
        Check(
            restored
                .Meta.Arr("derived_tables")
                .OfType<JsonObject>()
                .Count(d => d.S("source_path") == bank) == 2,
            "hierarquia dos resultados persiste no projeto"
        );
        var graph = new ChartView(window, window.Session, false);
        await graph.Load(bank, null);
        Check(
            LogicalControls<ComboBox>(graph).Any(c => c.Items.Contains("Ano")),
            "gráficos aceitam tabelas de banco DENDRO"
        );
        string copyFolder = Path.Combine(root, "project-copy"), movedFolder = Path.Combine(root, "project-moved");
        Directory.CreateDirectory(copyFolder);
        await window.Session.Save(Path.Combine(copyFolder, "Portatil.dendro"));
        Check(window.Session.Files.Where(p => Path.GetExtension(p) == ".dlb").All(p => !Path.IsPathRooted(p)),
            "tabelas do projeto usam caminhos relativos");
        if (!Path.GetFullPath(movedFolder).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Destino de teste fora da pasta de QA.");
        Directory.Move(copyFolder, movedFolder);
        var moved = new ProjectSession();
        await moved.Open(Path.Combine(movedFolder, "Portatil.dendro"));
        string movedBank = moved.Files.Single(p => Path.GetFileName(p) == "Uniao_UI.dlb");
        Check(Dendro.Core.Tabular.TableStore.Page(new(moved.Resolve(movedBank))).Total == 4,
            "Salvar como copia o banco e mover o projeto preserva a leitura dos dados");
        await window.Session.Open(saved);
    }

    private static void TestFreehandSelection(string root)
    {
        var plot = new ScatterPlot
        {
            Width = 700,
            Height = 450,
            RemoveEnabled = true,
            Data = new JsonObject
            {
                ["points"] = new JsonArray(
                    new JsonObject
                    {
                        ["x"] = 1,
                        ["y"] = 1,
                        ["row"] = 1,
                    },
                    new JsonObject
                    {
                        ["x"] = 3,
                        ["y"] = 1,
                        ["row"] = 2,
                    },
                    new JsonObject
                    {
                        ["x"] = 1,
                        ["y"] = 3,
                        ["row"] = 3,
                    },
                    new JsonObject
                    {
                        ["x"] = 3,
                        ["y"] = 3,
                        ["row"] = 4,
                    },
                    new JsonObject
                    {
                        ["x"] = 1,
                        ["y"] = 2,
                        ["row"] = 5,
                        ["excluded"] = true,
                    },
                    new JsonObject
                    {
                        ["x"] = 100,
                        ["y"] = 100,
                        ["row"] = 6,
                        ["excluded"] = true,
                    }
                ),
            },
        };
        plot.Measure(new Size(700, 450));
        plot.Arrange(new Rect(0, 0, 700, 450));
        Capture(plot, Path.Combine(root, "lasso-before.png"));
        Check(plot.DisplayBounds.XMax < 5, "ponto extremo removido não afeta os eixos por padrão");
        int[] selected = [];
        plot.SelectionChanged += rows => selected = rows;
        Point bottomLeft = plot.PositionOf(1),
            topRight = plot.PositionOf(4);
        double left = bottomLeft.X - 8,
            bottom = bottomLeft.Y + 8,
            right = topRight.X + 8,
            top = topRight.Y - 8,
            midX = (left + right) / 2,
            midY = (top + bottom) / 2;
        var origin = new Point(left, top);
        plot.BeginSelection(origin);
        plot.ContinueSelection(new Point(midX, top));
        plot.ContinueSelection(new Point(midX, midY));
        plot.ContinueSelection(new Point(right, midY));
        plot.ContinueSelection(new Point(right, bottom));
        plot.ContinueSelection(new Point(left, bottom));
        plot.ContinueSelection(origin);
        Capture(plot, Path.Combine(root, "lasso-concave.png"));
        plot.EndSelection(origin);
        Check(
            selected.SequenceEqual(new[] { 1, 2, 3 }),
            "laço côncavo respeita o contorno, ignora o canto vazio e os pontos já removidos"
        );
        int[] removed = [];
        plot.RemoveRequested += rows => removed = rows;
        Check(removed.Length == 0, "laço apenas seleciona pontos, sem remoção automática");
        plot.ConfirmRemoval();
        Check(
            removed.SequenceEqual(new[] { 1, 2, 3 }) && plot.SelectedRows.Length == 0,
            "confirmação emite a remoção e limpa a seleção"
        );
        selected = [];
        plot.BeginSelection(origin);
        plot.ContinueSelection(new Point(right, top));
        plot.RaiseEvent(
            new System.Windows.Input.KeyEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(Application.Current.MainWindow)!,
                0,
                System.Windows.Input.Key.Escape
            )
            {
                RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent,
            }
        );
        plot.EndSelection(new Point(left, bottom));
        Check(selected.Length == 0, "Esc cancela a seleção à mão livre sem remover pontos");
        plot.Config["show_removed"] = true;
        Capture(plot, Path.Combine(root, "lasso-removed-visible.png"));
        Check(
            plot.DisplayBounds.XMax > 100,
            "mostrar removidos inclui o extremo no intervalo do gráfico"
        );
    }

    private static async Task TestLargePointSelection(string root)
    {
        const int count = 100000;
        var data = new JsonArray();
        for (int row = 0; row < count; row++) data.Add(new JsonObject {
            ["x"] = row % 400, ["y"] = row / 400, ["row"] = row + 1,
        });
        var plot = new ScatterPlot { Width = 700, Height = 450, RemoveEnabled = true,
            Data = new JsonObject { ["points"] = data }, Config = new JsonObject { ["point_size"] = 1 } };
        plot.Measure(new Size(700, 450)); plot.Arrange(new Rect(0, 0, 700, 450));
        Capture(plot, Path.Combine(root, "large-selection-before.png"));
        int builds = plot.SceneBuilds;
        Point a = plot.PositionOf(1), b = plot.PositionOf(count);
        var origin = new Point(a.X - 3, b.Y - 3);
        plot.BeginSelection(origin);
        plot.ContinueSelection(new Point(b.X + 3, b.Y - 3));
        Capture(plot, Path.Combine(root, "large-selection-drag.png"));
        plot.ContinueSelection(new Point(b.X + 3, a.Y + 3));
        plot.ContinueSelection(new Point(a.X - 3, a.Y + 3));
        plot.ContinueSelection(origin);
        plot.EndSelection(origin);
        Check(!plot.SelectionCompletion.IsCompleted, "seleção de 100 mil pontos libera o dispatcher enquanto calcula o contorno");
        await plot.SelectionCompletion;
        Check(plot.SelectedRows.Length == count, "seleção em segundo plano conserva todos os 100 mil pontos, sem amostragem");
        Check(plot.SceneBuilds == builds, "arrastar o contorno não recalcula eixos nem reconstrói o desenho dos pontos");
        plot.ClearSelection();
        plot.BeginSelection(origin);
        plot.ContinueSelection(new Point(b.X + 3, b.Y - 3));
        plot.ContinueSelection(new Point(b.X + 3, a.Y + 3));
        plot.ContinueSelection(new Point(a.X - 3, a.Y + 3));
        plot.EndSelection(origin);
        var pending = plot.SelectionCompletion;
        plot.ClearSelection();
        await pending;
        Check(plot.SelectedRows.Length == 0, "cancelar seleção impede aplicação tardia do resultado em segundo plano");
        plot.BeginSelection(a); plot.EndSelection(a);
        Check(plot.SelectedRows.SequenceEqual(new[] { 1 }), "índice de seleção encontra ponto por clique sem ordenar o conjunto inteiro");
    }

    private static void TestTrendlineLayer(string root)
    {
        var plot = new ScatterPlot
        {
            Width = 700,
            Height = 450,
            Config = new JsonObject
            {
                ["point_size"] = 12,
                ["color"] = 4,
                ["fits"] = new JsonArray(
                    new JsonObject { ["converged"] = true, ["model"] = "Linear" },
                    new JsonObject { ["converged"] = true, ["model"] = "Quadrático" }
                ),
                ["selected_fits"] = new JsonArray(0, 1),
            },
            Data = new JsonObject
            {
                ["points"] = new JsonArray(
                    new JsonObject
                    {
                        ["x"] = 1,
                        ["y"] = 1,
                        ["row"] = 1,
                    },
                    new JsonObject
                    {
                        ["x"] = 2,
                        ["y"] = 2,
                        ["row"] = 2,
                    },
                    new JsonObject
                    {
                        ["x"] = 3,
                        ["y"] = 3,
                        ["row"] = 3,
                    }
                ),
                ["curves"] = new JsonArray(
                    new JsonArray(new JsonArray(1d, 1d), new JsonArray(3d, 3d)),
                    new JsonArray(new JsonArray(1d, 1.5d), new JsonArray(3d, 2.8d))
                ),
            },
        };
        plot.Measure(new Size(700, 450));
        plot.Arrange(new Rect(0, 0, 700, 450));
        Capture(plot, Path.Combine(root, "multiple-curves-layer.png"));
        var bitmap = new RenderTargetBitmap(700, 450, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(plot);
        Point center = plot.PositionOf(2);
        byte[] pixel = new byte[36];
        bitmap.CopyPixels(
            new Int32Rect((int)Math.Round(center.X) - 1, (int)Math.Round(center.Y) - 1, 3, 3),
            pixel,
            12,
            0
        );
        Check(
            Enumerable.Range(0, 9).Any(i => pixel[i * 4] > 130 && pixel[i * 4 + 2] < 70),
            "curva azul é desenhada acima do ponto vermelho na interseção"
        );
    }

    internal static void Capture(FrameworkElement element, string path)
    {
        if (element is Window window && window.Content is FrameworkElement content)
            element = content;
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(
            (int)element.ActualWidth,
            (int)element.ActualHeight,
            96,
            96,
            PixelFormats.Pbgra32
        );
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
