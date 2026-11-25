using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dendro.Core.Mathematics;
using Dendro.Core.Regression;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal static class FitDiagnosticWindow
{
    internal static async Task Open(MainWindow owner, ProjectSession session, JsonObject chart, JsonObject fit)
    {
        (double Observed, double Estimated)[] pairs = [];
        await owner.Execute(async () =>
        {
            if (!fit.B("converged")) throw new InvalidOperationException("O modelo precisa ter convergido para abrir o diagnóstico.");
            if (chart.N("frontier") != 0) throw new InvalidOperationException("Este diagnóstico está disponível para ajustes sem seleção de fronteira.");
            pairs = await Task.Run(() =>
            {
                using var cursor = new TableCursor(new(session.Resolve(chart.S("path")), chart.S("sheet"), (int)chart.N("header_row", 1)));
                var points = new RegressionEngine().Generate(cursor.Columns, cursor.Rows(), chart).Points;
                var formula = new Formula(fit.S("formula"), ["x", "x1", "x2", "x3", "t", "b0", "b1", "b2", "b3"]);
                return points.Where(p => !p.Excluded && p.Group == fit.S("group"))
                    .Select(p => (Observed: p.Y, Estimated: formula.Evaluate(s => s switch
                    {
                        "x" or "x1" or "t" => p.X, "x2" => p.Input(1), "x3" => p.Input(2), _ => fit.N(s),
                    }))).Where(p => double.IsFinite(p.Estimated) && double.IsFinite(p.Observed)).ToArray();
            });
            if (pairs.Length < 3) throw new InvalidOperationException("Não há observações válidas suficientes para o diagnóstico.");
            if (pairs.Length != (int)fit.N("n")) throw new InvalidOperationException("Os dados atuais diferem do ajuste salvo. Ajuste novamente antes de abrir o diagnóstico.");
        });
        if (pairs.Length < 3 || pairs.Length != (int)fit.N("n")) return;
        var residuals = pairs.Select(p => p.Estimated - p.Observed).ToArray();
        var form = Ui.Form();
        form.Children.Add(Ui.Text(fit.S("group") + " · " + fit.S("model"), 18));
        form.Children.Add(Ui.Text($"N = {pairs.Length:N0}    R² = {fit.N("r2"):0.####}    RMSE = {fit.N("rmse"):0.####}    Erro médio = {residuals.Average():0.####}"));
        form.Children.Add(Ui.Text($"Diagnóstico: {fit.S("diagnostic_status", fit.S("status"))} · Algoritmo: {fit.S("algorithm", "legado")}"));
        form.Children.Add(Ui.Text($"Tentativas: {fit.N("starts_attempted"):N0} · Iterações: {fit.N("iterations"):N0} · Avaliações da fórmula: {fit.N("function_evaluations"):N0} · Tempo: {fit.N("elapsed_ms"):N0} ms"));
        form.Children.Add(Ui.Text($"Encerramento: {fit.S("termination_reason", "não registrado")}"));
        if (fit["diagnostics"] is JsonArray diagnostics)
            foreach (var diagnostic in diagnostics.Select(value => value?.GetValue<string>()).Where(value => !string.IsNullOrWhiteSpace(value)))
                form.Children.Add(Ui.Text("Alerta: " + diagnostic));
        form.Children.Add(Ui.Text("Resíduo = estimado − observado. Valores positivos indicam superestimação."));
        var charts = new Grid { Height = 370 };
        for (int i = 0; i < 3; i++) charts.ColumnDefinitions.Add(new ColumnDefinition());
        void Add(string title, string x, string y, (double X, double Y)[] values, int mode, int column)
        {
            var plot = new DiagnosticPlot(title, x, y, values, mode) { Margin = new Thickness(4) };
            Grid.SetColumn(plot, column); charts.Children.Add(plot);
        }
        Add("Resíduos × estimado", "Estimado", "Resíduo", pairs.Select(p => (p.Estimated, p.Estimated - p.Observed)).ToArray(), 0, 0);
        Add("Observado × estimado", "Estimado", "Observado", pairs.Select(p => (p.Estimated, p.Observed)).ToArray(), 1, 1);
        const int bins = 20;
        const double lo = -100, width = 10;
        var counts = new int[bins];
        int omitted = 0;
        foreach (var pair in pairs)
        {
            double residualPercent = pair.Observed == 0 ? double.NaN : 100 * (pair.Estimated - pair.Observed) / pair.Observed;
            if (!double.IsFinite(residualPercent) || residualPercent < -100 || residualPercent > 100) { omitted++; continue; }
            counts[Math.Clamp((int)((residualPercent - lo) / width), 0, bins - 1)]++;
        }
        Add("Distribuição dos resíduos", "Resíduo (%)", "Frequência", counts.Select((n, i) => (lo + width * (i + .5), (double)n)).ToArray(), 2, 2);
        charts.Children[2].SetValue(FrameworkElement.ToolTipProperty,
            $"Resíduo (%) = 100 × (estimado − observado) / observado. {omitted:N0} observações fora de −100% a 100% ou com observado zero não entram no histograma.");
        form.Children.Add(charts);
        Ui.Dialog("Diagnóstico do ajuste", Ui.Scroll(form), 1120, 610).ShowDialog();
    }

    private sealed class DiagnosticPlot(string title, string xLabel, string yLabel, (double X, double Y)[] points, int mode) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 100 || h < 100) return;
            void Text(string value, double x, double y, double size = 11) => dc.DrawText(new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, Brushes.DarkSlateGray, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
            dc.DrawRoundedRectangle(Brushes.White, new Pen(Brushes.LightSteelBlue, 1), new Rect(0, 0, w, h), 5, 5);
            Text(title, 12, 10, 13); Text(yLabel, 12, 36); Text(xLabel, w / 2 - 20, h - 25);
            double xmin = points.Min(p => p.X), xmax = points.Max(p => p.X), ymin = Math.Min(0, points.Min(p => p.Y)), ymax = points.Max(p => p.Y);
            if (mode == 1) { xmin = ymin = Math.Min(xmin, ymin); xmax = ymax = Math.Max(xmax, ymax); }
            if (xmax <= xmin) { xmin -= .5; xmax += .5; }
            if (ymax <= ymin) { ymin -= .5; ymax += .5; }
            double xp = mode == 2 ? 0 : (xmax - xmin) * .05, yp = (ymax - ymin) * .08;
            if (mode == 2) { xmin = -100; xmax = 100; ymin = 0; }
            xmin -= xp; xmax += xp; if (mode != 2) ymin -= yp; ymax += yp;
            var area = new Rect(53, 62, w - 67, h - 112);
            Point Map(double x, double y) => new(area.Left + (x - xmin) / (xmax - xmin) * area.Width, area.Bottom - (y - ymin) / (ymax - ymin) * area.Height);
            for (int i = 0; i <= 4; i++)
            {
                double x = xmin + (xmax - xmin) * i / 4, y = ymin + (ymax - ymin) * i / 4;
                dc.DrawLine(new Pen(Brushes.WhiteSmoke, 1), Map(xmin, y), Map(xmax, y));
                Text(y.ToString("G3"), 3, Map(xmin, y).Y - 7, 10);
                Text(x.ToString("G3"), Map(x, ymin).X - 10, area.Bottom + 7, 10);
            }
            var accent = new SolidColorBrush(Color.FromArgb(145, 33, 102, 157));
            dc.PushClip(new RectangleGeometry(area));
            if (mode == 0) dc.DrawLine(new Pen(Brushes.Gray, 1), Map(xmin, 0), Map(xmax, 0));
            if (mode == 1) dc.DrawLine(new Pen(Brushes.Gray, 1), Map(xmin, xmin), Map(xmax, xmax));
            foreach (var p in points)
            {
                Point pixel = Map(p.X, p.Y);
                if (mode == 2)
                {
                    double barWidth = area.Width / (points.Length * 1.1);
                    dc.DrawRectangle(accent, null, new Rect(pixel.X - barWidth / 2, pixel.Y, barWidth, Math.Max(0, Map(p.X, 0).Y - pixel.Y)));
                }
                else dc.DrawEllipse(accent, null, pixel, 2.2, 2.2);
            }
            dc.Pop();
        }
    }
}
