using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Dendro.Core.Regression;

namespace Dendro.Desktop;

internal static partial class DesktopTests
{
    internal static Task RunChartVisuals(string root)
    {
        Directory.CreateDirectory(root);
        var owner = new MainWindow { WorkspacePath = Path.Combine(root, "layout.xml"), CloseConfirmation = () => Task.FromResult(true) };
        owner.Show();
        var chart = new JsonObject { ["title"] = "Volume por idade", ["x_label"] = "Idade (anos)", ["y_label"] = "VTCC (m³/ha)", ["grid"] = true };
        var points = Enumerable.Range(1, 100).Select(i => new ChartPoint(i / 10d, 20 + i * 2 + Math.Sin(i) * 15, i, "Global")).ToArray();
        var data = RegressionEngine.ToJson(new ChartResult(chart, points, [], 0));
        string initial = chart.ToJsonString();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var window = Application.Current.Windows.OfType<Window>().Single(w => w.Title == "Configurações visuais do gráfico");
            Check(LogicalControls<TabControl>(window).Single().Items.Count == 5, "cinco seções de aparência disponíveis");
            Capture(window, Path.Combine(root, "visual-settings.png"));
            var tabs = LogicalControls<TabControl>(window).Single();
            tabs.SelectedIndex = 1;
            window.UpdateLayout();
            Capture(window, Path.Combine(root, "appearance-settings.png"));
            tabs.SelectedIndex = 3;
            window.UpdateLayout();
            Capture(window, Path.Combine(root, "point-settings.png"));
            window.Close();
        };
        timer.Start();
        var edited = ChartVisualDialog.Show(chart, data);
        Check(edited == null && chart.ToJsonString() == initial, "cancelar preserva o gráfico original");
        var applyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        applyTimer.Tick += (_, _) =>
        {
            applyTimer.Stop();
            var window = Application.Current.Windows.OfType<Window>().Single(w => w.Title == "Configurações visuais do gráfico");
            LogicalControls<TextBox>(window).First().Text = "Título alterado";
            LogicalControls<Slider>(window).First().Value = 25;
            LogicalControls<Button>(window).Single(b => b.Content?.ToString() == "Aplicar")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        applyTimer.Start();
        edited = ChartVisualDialog.Show(chart, data);
        Check(edited?.S("title") == "Título alterado", "aplicar retorna as edições visuais");
        Check(edited?.N("visual_title_size") == 25, "slider grava o tamanho selecionado");
        Check(chart.ToJsonString() == initial, "a prévia não altera o estado original");
        File.WriteAllText(Path.Combine(root, "checks.txt"), "Prévia, cinco abas, cancelar e aplicar: OK");
        owner.Close(); return Task.CompletedTask;
    }
}
