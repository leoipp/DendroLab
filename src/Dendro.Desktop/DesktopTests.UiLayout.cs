using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AvalonDock.Layout;

namespace Dendro.Desktop;

internal static partial class DesktopTests
{
    internal static async Task RunUiLayout(string root)
    {
        Directory.CreateDirectory(root);
        var shell = new MainWindow { WorkspacePath = Path.Combine(root, "layout.xml"), CloseConfirmation = () => Task.FromResult(true) };
        await shell.Session.New("UI/UX");
        shell.Show();
        Check(!shell.DockManager.Layout.Descendents().OfType<AvalonDock.Layout.LayoutDocument>().Any(),
            "área de trabalho inicia sem página inicial");
        await shell.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var paneTitles = Controls<AvalonDock.Controls.AnchorablePaneTitle>(shell).ToArray();
        Check(paneTitles.Length >= 2 && paneTitles.All(title => Controls<AvalonDock.Controls.DropDownButton>(title).All(button => button.Visibility == Visibility.Collapsed)),
            "painéis não exibem a seta redundante de opções");
        foreach (var title in paneTitles)
        {
            var area = Controls<AvalonDock.Controls.DropDownControlArea>(title).First();
            area.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,
                Environment.TickCount, System.Windows.Input.MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent });
            Check(area.DropDownContextMenu.IsOpen && area.DropDownContextMenu.DataContext == area.DropDownContextMenuDataContext,
                "botão direito no título abre o menu do painel correto");
            area.DropDownContextMenu.IsOpen = false;
            var header = (Border)System.Windows.Media.VisualTreeHelper.GetParent(title);
            foreach (double x in new[] { 1d, header.ActualWidth * .5, header.ActualWidth - 2 })
            {
                var target = header.InputHitTest(new Point(x, header.ActualHeight - 1)) as UIElement;
                Check(target != null, "toda a faixa do cabeçalho recebe interação");
                target!.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,
                    Environment.TickCount, System.Windows.Input.MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent });
                Check(area.DropDownContextMenu.IsOpen && area.DropDownContextMenu.PlacementTarget == header,
                    "botão direito nas margens e no espaço vazio abre o menu do painel");
                area.DropDownContextMenu.IsOpen = false;
            }
        }
        Capture(shell, Path.Combine(root, "paineis-sem-seta.png"));
        shell.ToolSearch.Focus();
        Check(shell.ContentSearchHint.Visibility == Visibility.Visible, "conteúdos mostra dica dentro do campo vazio");
        shell.ContentSearch.Focus();
        Check(shell.ContentSearchHint.Visibility == Visibility.Collapsed, "foco oculta a dica de conteúdos");
        shell.ContentSearch.Text = "Inventário";
        shell.ToolSearch.Focus();
        Check(shell.ContentSearchHint.Visibility == Visibility.Collapsed, "dica de conteúdos não sobrepõe texto");
        shell.ContentSearch.Clear();
        Check(shell.ContentSearchHint.Visibility == Visibility.Visible, "dica retorna ao limpar conteúdos sem foco");
        var addPoint = shell.AddDataButton.TranslatePoint(new Point(), shell.ContentSearchPanel);
        var searchPoint = shell.ContentSearch.TranslatePoint(new Point(), shell.ContentSearchPanel);
        Check(Math.Abs(addPoint.Y - searchPoint.Y) < 1 && addPoint.X > searchPoint.X,
            "adicionar dados fica ao lado da pesquisa na mesma linha");
        shell.ToolCategoryFilter.Focus();
        Check(shell.ToolSearchHint.Visibility == Visibility.Visible, "campo vazio sem foco mostra a dica de busca");
        shell.ToolSearch.Focus();
        Check(shell.ToolSearchHint.Visibility == Visibility.Collapsed, "dica desaparece ao focar a busca");
        shell.ToolSearch.Text = "Processador";
        shell.ToolCategoryFilter.Focus();
        Check(shell.ToolSearchHint.Visibility == Visibility.Collapsed, "dica não sobrepõe o texto digitado");
        shell.ToolCategoryFilter.SelectedIndex = 1;
        shell.ClearToolSearchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(shell.ToolSearch.Text.Length == 0 && shell.ToolCategoryFilter.SelectedIndex == 0,
            "vassoura limpa o termo e restaura todas as categorias");
        shell.SetTool(new ProcessorView(shell, shell.Session));
        await shell.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Capture(shell, Path.Combine(root, "processador.png"));
        shell.Session.Data!["processador"]!["modo"] = "resumo_direto";
        var bases = new[] { Path.Combine(root, "Inventario_A.dlb"), Path.Combine(root, "Inventario_B.dlb") };
        foreach (string path in bases)
        {
            using var writer = new Dendro.Core.Tabular.CompactBankWriter(path);
            string id = writer.AddTable(["DAP", "H"]);
            writer.Add(id, [10d, 15d]);
            writer.Complete();
            shell.Session.AddFile(path);
        }
        shell.Session.Meta["batch_inputs"] = new System.Text.Json.Nodes.JsonArray(bases.Select(path => (System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(path)).ToArray());
        var direct = new ProcessorView(shell, shell.Session);
        shell.SetTool(direct);
        await shell.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var execute = Controls<Button>(direct).Single(button => button.Content as string == "Executar");
        var executePoint = execute.TranslatePoint(new Point(), direct);
        Check(executePoint.Y > direct.ActualHeight - 80 && executePoint.X + execute.ActualWidth > direct.ActualWidth - 35,
            "Executar permanece no canto inferior direito");
        Check(!Controls<TextBlock>(direct).Any(text => text.Text == "Prefixo dos resultados"), "processador não pede prefixo");
        Check(Controls<HelpIcon>(direct).All(help => help.Width == help.Height && help.CornerRadius.TopLeft == help.Width / 2),
            "ajudas são circulares");
        Capture(shell, Path.Combine(root, "processador-direto.png"));
        Check(Controls<Button>(direct).Any(button => button.Content as string == "2 inventários selecionados  ▾"),
            "seleção múltipla mostra a quantidade de entradas");
        var removeInput = Controls<Button>(direct).First(button => button.Content as string == "×");
        removeInput.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(Controls<Button>(direct).Any(button => button.Content as string == "1 inventário selecionado  ▾"),
            "retirar etiqueta atualiza a quantidade selecionada");
        Check(shell.Session.Meta.Arr("batch_inputs").Count == 1, "retirar etiqueta atualiza a seleção salva no projeto");
        Capture(shell, Path.Combine(root, "processador-selecao-unica.png"));
        ToolFormView[] forms = [new KMeansView(shell, shell.Session), new ModelValidationView(shell, shell.Session),
            new InventoryAuditView(shell, shell.Session), new StatisticalTestsView(shell, shell.Session),
            new MachineLearningView(shell, shell.Session, "random_forest"), new FrontierView(shell, shell.Session),
            new GrowthValidationView(shell, shell.Session), new PairedMeasurementsView(shell, shell.Session)];
        foreach (var tool in forms)
        {
            shell.SetTool(tool);
            await shell.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var footer = tool.Children.OfType<Border>().Single();
            Check(footer.TranslatePoint(new Point(), tool).Y > tool.ActualHeight - footer.ActualHeight - 2,
                tool.GetType().Name + " mantém ação principal no rodapé");
            Capture(shell, Path.Combine(root, tool.GetType().Name + ".png"));
        }
        var review = new ProcessingReviewView(shell, shell.Session);
        shell.SetTool(review);
        await review.Initialize();
        await shell.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Capture(shell, Path.Combine(root, "revisao.png"));
        shell.ShowTools();
        await shell.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Capture(shell, Path.Combine(root, "ferramentas.png"));
        _ = shell.Dispatcher.BeginInvoke(new Action(() =>
        {
            var dialog = Application.Current.Windows.OfType<Window>().Single(w => w.Title == "Remover gráfico?");
            var accept = Controls<Button>(dialog).Single(b => !b.IsCancel);
            Check(!accept.IsDefault, "Enter não confirma remoção por padrão");
            Check(System.Windows.Input.Keyboard.FocusedElement is Button cancel && cancel.Content as string == "Cancelar",
                "confirmação destrutiva abre com foco em Cancelar");
            Capture(dialog, Path.Combine(root, "confirmacao.png"));
            dialog.DialogResult = false;
        }), DispatcherPriority.ApplicationIdle);
        Check(!Ui.ShowMessage("Remover gráfico?", "O gráfico de exemplo será removido do projeto.", true,
            "Remover gráfico", true, Ui.MessageTone.Warning), "cancelar confirmação não aceita a ação");
        var scroll = new ScrollViewer { Content = new Border { Width = 1400, Height = 1600 },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var scrollHost = new Window { Width = 420, Height = 300, Content = scroll, ShowInTaskbar = false };
        scrollHost.Show();
        await scrollHost.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var bars = Controls<System.Windows.Controls.Primitives.ScrollBar>(scrollHost).ToArray();
        Check(bars.Length == 2 && bars.All(bar => ReferenceEquals(bar.Style, Application.Current.FindResource(typeof(System.Windows.Controls.Primitives.ScrollBar)))),
            "barras vertical e horizontal usam o estilo global");
        var vertical = bars.Single(bar => bar.Orientation == Orientation.Vertical);
        var horizontal = bars.Single(bar => bar.Orientation == Orientation.Horizontal);
        System.Windows.Controls.Primitives.ScrollBar.PageDownCommand.Execute(null, vertical);
        System.Windows.Controls.Primitives.ScrollBar.PageRightCommand.Execute(null, horizontal);
        await scrollHost.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(scroll.VerticalOffset > 0 && scroll.HorizontalOffset > 0, "paginação funciona nos dois eixos");
        double previousOffset = scroll.VerticalOffset;
        Controls<System.Windows.Controls.Primitives.Thumb>(vertical).Single().RaiseEvent(
            new System.Windows.Controls.Primitives.DragDeltaEventArgs(0, 20));
        await scrollHost.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(scroll.VerticalOffset > previousOffset, "arrastar o indicador mantém a rolagem vertical");
        Capture(scrollHost, Path.Combine(root, "barras-rolagem.png"));
        scrollHost.Close();
        await TestVectorSymbology(shell, root);
        await TestTaper(shell, root);
        shell.Close();
        File.WriteAllText(Path.Combine(root, "ui-layout.log"), $"OK: {checks} verificações; telas renderizadas.");
    }
}
