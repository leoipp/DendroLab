using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Dendro.Core.Regression;

namespace Dendro.Desktop;

/// <summary>Asynchronous violin and Tukey outlier view over the points of a chart.</summary>
internal sealed class ViolinPlot : FrameworkElement
{
    private readonly record struct ViolinPoint(int Row, int BinIndex, double Y);
    private sealed record Distribution(
        int BinIndex, string Name, int Count, int OutlierCount, double[] Density,
        double Q1, double Median, double Q3, double LowWhisker, double HighWhisker,
        double Minimum, double Maximum, double Bandwidth, double[] Outliers);
    private sealed record Scene(int Generation, Distribution[] Groups, ViolinPoint[] Points, int[] OutlierRows, int BinCount, double Minimum, double Maximum,
        int Count, int OutlierCount, string Error = "");

    private JsonObject data = new(), config = new();
    private Scene? scene;
    private CancellationTokenSource? cancellation;
    private int generation;
    private bool pending;
    private readonly HashSet<int> selectedRows = [];
    private ViolinPoint[] selectedPoints = [];
    private Point? selectionStart;
    private readonly List<Point> selectionPath = [];
    private bool selecting;
    private bool straightSelection, horizontalSelection;
    private bool constrainedStroke;
    private Point straightAnchor;
    private CancellationTokenSource? selectionCancellation;
    internal bool RemoveEnabled { get; set; }
    internal int[] SelectedRows => selectedRows.Order().ToArray();
    internal int OutlierCount => scene?.OutlierCount ?? 0;
    internal event Action<int[]>? SelectionChanged;
    internal event Action<int[]>? RemoveRequested;

    internal JsonObject Data
    {
        get => data;
        set { data = value; InvalidateScene(); }
    }

    internal JsonObject Config
    {
        get => config;
        set { config = value; InvalidateScene(); }
    }

    internal event Action<string>? SummaryChanged;

    internal ViolinPlot()
    {
        Height = 540;
        MinHeight = 400;
        ClipToBounds = true;
        Focusable = true;
        Unloaded += (_, _) => Cancel();
        Unloaded += (_, _) => CancelSafely(selectionCancellation);
    }

    private void Cancel()
    {
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        pending = false;
    }

    private void InvalidateScene()
    {
        generation++;
        Cancel();
        pending = false;
            scene = null;
        selectedPoints = [];
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int bins = scene?.BinCount ?? (int)config.N("violin_bins", 12);
        double width = Math.Max(760, 108 + bins * 52);
        return new(width, double.IsInfinity(availableSize.Height) ? Height : Math.Max(Height, availableSize.Height));
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
        if (scene is null)
        {
            if (!pending) StartPreparation();
            DrawText(dc, pending ? "Calculando distribuição e outliers…" : "Aguardando dados…", 28, 28, 16, Brush(Color.FromRgb(73, 93, 112)));
            return;
        }
        if (scene.Generation != generation) return;
        if (scene.Error.Length > 0)
        {
            DrawText(dc, scene.Error, 24, 28, 15, Brush(Color.FromRgb(160, 54, 54)));
            return;
        }
        if (scene.Groups.Length == 0)
        {
            DrawText(dc, "Não há valores válidos mantidos para montar o gráfico de violino.", 24, 28, 15, Brush(Color.FromRgb(73, 93, 112)));
            return;
        }
        DrawScene(dc, scene);
        if (selectionPath.Count > 1)
        {
            if (straightSelection)
            {
                var linePen = new Pen(Brush(Color.FromArgb(220, 33, 102, 157)), 2) { DashStyle = DashStyles.Dash };
                dc.DrawLine(linePen, straightAnchor, selectionPath[^1]);
            }
            else
            {
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    context.BeginFigure(selectionPath[0], false, false);
                    foreach (var point in selectionPath.Skip(1)) context.LineTo(point, true, false);
                }
                geometry.Freeze();
                var pen = new Pen(Brush(Color.FromRgb(242, 153, 48)), 1.5) { DashStyle = DashStyles.Dash };
                dc.DrawGeometry(null, pen, geometry);
            }
        }
    }

    private void StartPreparation()
    {
        pending = true;
        int current = generation;
        var snapshot = config.DeepClone().AsObject();
        var tokenSource = cancellation = new CancellationTokenSource();
        _ = Task.Run(() => Prepare(data, snapshot, current, tokenSource.Token), tokenSource.Token)
            .ContinueWith(task =>
            {
                if (task.IsCanceled || tokenSource.IsCancellationRequested || Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(() =>
                {
                    if (tokenSource.IsCancellationRequested || current != generation) return;
                    scene = task.IsFaulted
                        ? new(current, [], [], [], 0, 0, 1, 0, 0, task.Exception?.GetBaseException().Message ?? "Falha ao calcular o violino.")
                        : task.Result;
                    pending = false;
                    SummaryChanged?.Invoke($"{scene.Count:N0} valores · {scene.BinCount:N0} bins · {scene.Groups.Length:N0} bins com dados · {scene.OutlierCount:N0} possíveis outliers");
                    InvalidateMeasure();
                    InvalidateVisual();
                });
            }, TaskScheduler.Default);
    }

    private static Scene Prepare(JsonObject data, JsonObject config, int generation, CancellationToken cancellation)
    {
        bool logX = config.B("log_x"), logY = config.B("log_y");
        var rawPoints = new List<(int Row, double X, double Y)>();
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
        int scanned = 0;
        foreach (var point in ChartPointView.Open(data, config).Points())
        {
            if ((++scanned & 4095) == 0) cancellation.ThrowIfCancellationRequested();
            if (point.Excluded || !double.IsFinite(point.X) || !double.IsFinite(point.Y)
                || (logX && point.X <= 0) || (logY && point.Y <= 0)) continue;
            double x = logX ? Math.Log10(point.X) : point.X;
            double y = logY ? Math.Log10(point.Y) : point.Y;
            if (!double.IsFinite(x) || !double.IsFinite(y)) continue;
            rawPoints.Add((point.Row, x, y));
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
        }
        int binCount = Math.Clamp((int)config.N("violin_bins", 12), 2, 100);
        if (rawPoints.Count == 0) return new(generation, [], [], [], binCount, 0, 1, 0, 0);
        if (!(maxX > minX)) binCount = 1;
        var values = new Dictionary<int, List<double>>();
        var rowsByBin = new Dictionary<int, List<(int Row, double Y)>>();
        var plottedPoints = new List<ViolinPoint>(rawPoints.Count);
        double binWidth = binCount > 1 ? (maxX - minX) / binCount : 1;
        foreach (var point in rawPoints)
        {
            int binIndex = binCount == 1 ? 0 : Math.Clamp((int)((point.X - minX) / binWidth), 0, binCount - 1);
            if (!values.TryGetValue(binIndex, out var list)) values[binIndex] = list = [];
            list.Add(point.Y);
            if (!rowsByBin.TryGetValue(binIndex, out var binRows)) rowsByBin[binIndex] = binRows = [];
            binRows.Add((point.Row, point.Y));
            plottedPoints.Add(new(point.Row, binIndex, point.Y));
        }

        int resolution = Math.Clamp((int)config.N("violin_resolution", 96), 48, 192);
        double bandwidthFactor = Math.Clamp(config.N("violin_bandwidth", 1), .25, 4);
        double iqrLimit = Math.Clamp(config.N("violin_outlier_iqr", 1.5), 1, 3);
        var distributions = new List<Distribution>(values.Count);
        var outlierRows = new List<int>();
        double globalMin = double.PositiveInfinity, globalMax = double.NegativeInfinity;
        int total = 0, totalOutliers = 0;
        foreach (var (binIndex, source) in values.OrderBy(pair => pair.Key))
        {
            cancellation.ThrowIfCancellationRequested();
            source.Sort();
            int n = source.Count;
            double q1 = Quantile(source, .25), median = Quantile(source, .5), q3 = Quantile(source, .75);
            double iqr = q3 - q1;
            double lowerFence = q1 - iqrLimit * iqr, upperFence = q3 + iqrLimit * iqr;
            int lo = source.FindIndex(value => value >= lowerFence);
            int hi = source.FindLastIndex(value => value <= upperFence);
            double lowWhisker = lo >= 0 ? source[lo] : source[0];
            double highWhisker = hi >= 0 ? source[hi] : source[^1];
            var outliers = source.Where(value => value < lowerFence || value > upperFence).ToArray();
            outlierRows.AddRange(rowsByBin[binIndex].Where(point => point.Y < lowerFence || point.Y > upperFence).Select(point => point.Row));
            double min = source[0], max = source[^1];
            globalMin = Math.Min(globalMin, min); globalMax = Math.Max(globalMax, max);
            total += n; totalOutliers += outliers.Length;

            double mean = source.Average();
            double variance = source.Sum(value => (value - mean) * (value - mean)) / Math.Max(1, n - 1);
            double sd = Math.Sqrt(variance);
            double robustScale = iqr > 0 ? Math.Min(sd, iqr / 1.34) : sd;
            if (!(robustScale > 0) || !double.IsFinite(robustScale)) robustScale = (max - min) / 6;
            double bandwidth = Math.Max(robustScale * .9 * Math.Pow(n, -.2) * bandwidthFactor,
                Math.Max(1e-10, (max - min) * 1e-5));
            var sample = Sample(source, 2048);
            var density = new double[resolution];
            if (max == min)
            {
                density.AsSpan().Fill(1);
            }
            else
            {
                double from = min - bandwidth * 2, to = max + bandwidth * 2;
                for (int i = 0; i < resolution; i++)
                {
                    if ((i & 31) == 0) cancellation.ThrowIfCancellationRequested();
                    double y = from + (to - from) * i / (resolution - 1d);
                    double sum = 0;
                    foreach (double observation in sample)
                    {
                        double z = (y - observation) / bandwidth;
                        if (Math.Abs(z) <= 8) sum += Math.Exp(-.5 * z * z);
                    }
                    density[i] = sum / sample.Length;
                }
            }
            double binLo = binCount == 1 ? minX : minX + binIndex * binWidth;
            double binHi = binCount == 1 ? maxX : minX + (binIndex + 1) * binWidth;
            double displayLo = logX ? Math.Pow(10, binLo) : binLo;
            double displayHi = logX ? Math.Pow(10, binHi) : binHi;
            string label = binCount == 1
                ? $"X={Format(displayLo)}"
                : $"{Format(displayLo)}–{Format(displayHi)}";
            distributions.Add(new(binIndex, label, n, outliers.Length, density, q1, median, q3,
                lowWhisker, highWhisker, min, max, bandwidth, Sample(outliers, 500)));
        }
        if (globalMin == globalMax) { globalMin -= .5; globalMax += .5; }
        double padding = (globalMax - globalMin) * .04;
        return new(generation, distributions.ToArray(), plottedPoints.ToArray(), outlierRows.ToArray(), binCount, globalMin - padding, globalMax + padding, total, totalOutliers);
    }

    private static string Format(double value) => value.ToString("G4", CultureInfo.CurrentCulture);

    private static double Quantile(IReadOnlyList<double> values, double probability)
    {
        if (values.Count == 1) return values[0];
        double position = (values.Count - 1) * probability;
        int lower = (int)position, upper = Math.Min(values.Count - 1, lower + 1);
        return values[lower] + (values[upper] - values[lower]) * (position - lower);
    }

    private static double[] Sample(IReadOnlyList<double> values, int maximum)
    {
        if (values.Count <= maximum) return values.ToArray();
        var sample = new double[maximum];
        for (int index = 0; index < maximum; index++)
            sample[index] = values[(int)((long)index * (values.Count - 1) / (maximum - 1))];
        return sample;
    }

    private void DrawScene(DrawingContext dc, Scene result)
    {
        const double left = 110, top = 24, right = 22, bottom = 110;
        double width = Math.Max(1, RenderSize.Width - left - right);
        double height = Math.Max(1, RenderSize.Height - top - bottom);
        double range = result.Maximum - result.Minimum;
        double yPos(double value) => top + (result.Maximum - value) / range * height;
        double categoryWidth = width / result.BinCount;
        var axis = new Pen(Brush(Color.FromRgb(105, 122, 139)), 1);
        var grid = new Pen(Brush(Color.FromRgb(228, 234, 240)), 1) { DashStyle = DashStyles.Dash };
        var fill = new SolidColorBrush(Color.FromArgb(82, 33, 102, 157)); fill.Freeze();
        var outline = new Pen(Brush(Color.FromRgb(33, 102, 157)), 1.4);
        var outlierBrush = Brush(Color.FromRgb(205, 83, 60));
        double tickStep = range / 5;
        foreach (var tick in ChartTicks.Create(result.Minimum, result.Maximum, tickStep, false))
        {
            double y = yPos(tick.Position);
            dc.DrawLine(grid, new Point(left, y), new Point(left + width, y));
            DrawTextRight(dc, config.B("log_y") ? Math.Pow(10, tick.Position).ToString("G5", CultureInfo.CurrentCulture) : tick.Position.ToString("G5", CultureInfo.CurrentCulture), left - 7, y - 8, 11, Brush(Color.FromRgb(73, 93, 112)));
        }
        dc.DrawLine(axis, new Point(left, top), new Point(left, top + height));
        dc.DrawLine(axis, new Point(left, top + height), new Point(left + width, top + height));
        foreach (var group in result.Groups)
        {
            double center = left + categoryWidth * (group.BinIndex + .5);
            double half = Math.Max(5, Math.Min(categoryWidth * .38, 36));
            double maxDensity = Math.Max(1e-12, group.Density.Max());
            double densityFrom = group.Minimum - group.Bandwidth * 2;
            double densityTo = group.Maximum + group.Bandwidth * 2;
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                for (int i = 0; i < group.Density.Length; i++)
                {
                    double y = densityFrom + (densityTo - densityFrom) * i / (group.Density.Length - 1d);
                    double x = center + half * group.Density[i] / maxDensity;
                    if (i == 0) context.BeginFigure(new Point(x, yPos(y)), true, true);
                    else context.LineTo(new Point(x, yPos(y)), true, false);
                }
                for (int i = group.Density.Length - 1; i >= 0; i--)
                {
                    double y = densityFrom + (densityTo - densityFrom) * i / (group.Density.Length - 1d);
                    double x = center - half * group.Density[i] / maxDensity;
                    context.LineTo(new Point(x, yPos(y)), true, false);
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(fill, outline, geometry);

            double boxWidth = Math.Min(13, categoryWidth * .2);
            double qTop = yPos(group.Q3), qBottom = yPos(group.Q1);
            dc.DrawRectangle(Brushes.White, axis, new Rect(center - boxWidth / 2, qTop, boxWidth, Math.Max(1, qBottom - qTop)));
            dc.DrawLine(new Pen(Brush(Color.FromRgb(24, 54, 78)), 2), new Point(center - boxWidth / 2, yPos(group.Median)), new Point(center + boxWidth / 2, yPos(group.Median)));
            dc.DrawLine(axis, new Point(center, yPos(group.LowWhisker)), new Point(center, yPos(group.Q1)));
            dc.DrawLine(axis, new Point(center, yPos(group.Q3)), new Point(center, yPos(group.HighWhisker)));
            dc.DrawLine(axis, new Point(center - boxWidth / 2, yPos(group.LowWhisker)), new Point(center + boxWidth / 2, yPos(group.LowWhisker)));
            dc.DrawLine(axis, new Point(center - boxWidth / 2, yPos(group.HighWhisker)), new Point(center + boxWidth / 2, yPos(group.HighWhisker)));

            double jitterMax = Math.Max(2, Math.Min(categoryWidth * .18, 13));
            for (int i = 0; i < group.Outliers.Length; i++)
            {
                double jitter = (StableUnit(group.Name, i) * 2 - 1) * jitterMax;
                dc.DrawEllipse(outlierBrush, null, new Point(center + jitter, yPos(group.Outliers[i])), 2.5, 2.5);
            }
            DrawRotatedText(dc, group.Name, center + 3, top + height + 12, Math.Min(52, categoryWidth - 4), 10);
            DrawText(dc, $"n={group.Count:N0} · out={group.OutlierCount:N0}", center - Math.Min(47, categoryWidth * .45), top + height + 68, 9, Brush(Color.FromRgb(73, 93, 112)));
        }
        double jitterMaxPoints = Math.Max(2, Math.Min(categoryWidth * .18, 13));
        foreach (var point in selectedPoints)
        {
            double center = left + categoryWidth * (point.BinIndex + .5);
            double jitter = (StableUnit("row", point.Row) * 2 - 1) * jitterMaxPoints;
            double y = yPos(point.Y);
            dc.DrawEllipse(Brush(Color.FromArgb(70, 244, 163, 64)), null, new Point(center + jitter, y), 7, 7);
            dc.DrawEllipse(Brush(Color.FromRgb(238, 132, 30)), new Pen(Brushes.White, 1.2), new Point(center + jitter, y), 4.6, 4.6);
        }
        DrawVerticalText(dc, config.S("y_label", config.S("y")), 38, top + height / 2, 12, Brush(Color.FromRgb(40, 62, 82)));
        DrawText(dc, config.S("x_label", config.S("x")), left + width / 2 - 35, RenderSize.Height - 22, 12, Brush(Color.FromRgb(40, 62, 82)));
    }

    private static double StableUnit(string key, int index)
    {
        uint hash = 2166136261;
        foreach (char character in key) hash = (hash ^ character) * 16777619;
        hash = (hash ^ (uint)index) * 16777619;
        return hash / (double)uint.MaxValue;
    }

    internal void SynchronizeSelection(IEnumerable<int> rows)
    {
        CancelSafely(selectionCancellation);
        selectedRows.Clear();
        selectedRows.UnionWith(rows);
        RefreshSelectedPoints();
        InvalidateVisual();
    }

    internal void ClearSelection()
    {
        CancelSafely(selectionCancellation);
        selectionPath.Clear();
        selecting = false;
        selectionStart = null;
        straightSelection = false;
        constrainedStroke = false;
        selectedRows.Clear();
        selectedPoints = [];
        SelectionChanged?.Invoke([]);
        InvalidateVisual();
    }

    internal void ConfirmRemoval()
    {
        if (!RemoveEnabled || selectedRows.Count == 0) return;
        var rows = SelectedRows;
        SynchronizeSelection([]);
        SelectionChanged?.Invoke([]);
        RemoveRequested?.Invoke(rows);
    }

    internal int SelectOutliers()
    {
        if (scene is null || scene.Generation != generation) return 0;
        var rows = scene.OutlierRows;
        ApplySelection(rows, append: false);
        return rows.Length;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!RemoveEnabled || scene is null || scene.Points.Length == 0) return;
        Point start = e.GetPosition(this);
        if (start.X < 110 || start.X > RenderSize.Width - 22 || start.Y < 24 || start.Y > RenderSize.Height - 110) return;
        Focus();
        selectionStart = start;
        selectionPath.Clear(); selectionPath.Add(selectionStart.Value);
        straightSelection = false;
        constrainedStroke = false;
        selecting = true;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!selecting || e.LeftButton != MouseButtonState.Pressed) return;
        var position = e.GetPosition(this);
        const double left = 110, top = 24, right = 22, bottom = 110;
        position = new Point(Math.Clamp(position.X, left, Math.Max(left, RenderSize.Width - right)),
            Math.Clamp(position.Y, top, Math.Max(top, RenderSize.Height - bottom)));
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (!straightSelection && shift && (position - selectionPath[^1]).Length >= 6)
        {
            straightAnchor = selectionPath[^1];
            double dx = Math.Abs(position.X - straightAnchor.X), dy = Math.Abs(position.Y - straightAnchor.Y);
            horizontalSelection = dx >= dy;
            straightSelection = true;
            constrainedStroke = true;
        }
        if (straightSelection && shift)
        {
            position = horizontalSelection
                ? new Point(position.X, straightAnchor.Y)
                : new Point(straightAnchor.X, position.Y);
            if ((selectionPath[^1] - straightAnchor).Length < 1e-6) selectionPath.Add(position);
            else selectionPath[^1] = position;
        }
        else
        {
            straightSelection = false;
            if (selectionPath.Count == 0 || (position - selectionPath[^1]).Length >= 3) selectionPath.Add(position);
        }
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!selecting || scene is null || selectionStart is null) return;
        Point endpoint = e.GetPosition(this);
        const double left = 110, top = 24, right = 22, bottom = 110;
        endpoint = new Point(Math.Clamp(endpoint.X, left, Math.Max(left, RenderSize.Width - right)),
            Math.Clamp(endpoint.Y, top, Math.Max(top, RenderSize.Height - bottom)));
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (straightSelection && shift)
        {
            endpoint = horizontalSelection
                ? new Point(endpoint.X, straightAnchor.Y)
                : new Point(straightAnchor.X, endpoint.Y);
            if ((selectionPath[^1] - straightAnchor).Length < 1e-6) selectionPath.Add(endpoint);
            else selectionPath[^1] = endpoint;
        }
        else
        {
            straightSelection = false;
            if (selectionPath.Count == 0 || (endpoint - selectionPath[^1]).Length >= 2)
                selectionPath.Add(endpoint);
        }
        bool lasso = selectionPath.Count >= 4 && selectionPath.Any(p => (p - selectionStart.Value).Length >= 7);
        bool lineSelection = constrainedStroke && selectionPath.Count == 2;
        bool append = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var points = scene.Points;
        int bins = scene.BinCount;
        double min = scene.Minimum, max = scene.Maximum;
        var polygon = selectionPath.ToArray();
        selecting = false;
        selectionStart = null;
        straightSelection = false;
        constrainedStroke = false;
        ReleaseMouseCapture();
        selectionPath.Clear();
        if (!lasso && !lineSelection)
        {
            var hit = FindNearestPoint(e.GetPosition(this), points, bins, min, max);
            ApplySelection(hit >= 0 ? [hit] : [], append);
        }
        else if (lineSelection && polygon.Length >= 2)
        {
            StartStrokeSelection(points, polygon[0], polygon[^1], bins, min, max, RenderSize.Width, RenderSize.Height, append);
        }
        else
        {
            CancelSafely(selectionCancellation);
            var cancellation = selectionCancellation = new CancellationTokenSource();
            Cursor = Cursors.AppStarting;
            double renderWidth = RenderSize.Width, renderHeight = RenderSize.Height;
            _ = Task.Run(() => points.Where((point, index) =>
            {
                if ((index & 4095) == 0) cancellation.Token.ThrowIfCancellationRequested();
                return IsInside(polygon, PointPosition(point, bins, min, max, renderWidth, renderHeight));
            }).Select(point => point.Row).Distinct().ToArray(), cancellation.Token).ContinueWith(task =>
            {
                if (task.IsCanceled || Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(() =>
                {
                    if (cancellation.IsCancellationRequested) return;
                    ApplySelection(task.IsFaulted ? [] : task.Result, append);
                    if (ReferenceEquals(selectionCancellation, cancellation)) { selectionCancellation = null; Cursor = null; }
                    cancellation.Dispose();
                });
            }, TaskScheduler.Default);
        }
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (!selecting) return;
        selecting = false; straightSelection = false; constrainedStroke = false; selectionPath.Clear(); InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (RemoveEnabled && e.Key == Key.Delete) { ConfirmRemoval(); e.Handled = true; }
        else if (e.Key == Key.Escape) { straightSelection = false; constrainedStroke = false; ClearSelection(); e.Handled = true; }
    }

    private void ApplySelection(IEnumerable<int> rows, bool append)
    {
        if (!append) selectedRows.Clear();
        selectedRows.UnionWith(rows);
        RefreshSelectedPoints();
        SelectionChanged?.Invoke(SelectedRows);
        InvalidateVisual();
    }

    private void StartStrokeSelection(ViolinPoint[] points, Point from, Point to, int bins, double min, double max,
        double renderWidth, double renderHeight, bool append)
    {
        CancelSafely(selectionCancellation);
        var cancellation = selectionCancellation = new CancellationTokenSource();
        Cursor = Cursors.AppStarting;
        _ = Task.Run(() => points.Where((point, index) =>
        {
            if ((index & 4095) == 0) cancellation.Token.ThrowIfCancellationRequested();
            return DistanceToSegmentSquared(PointPosition(point, bins, min, max, renderWidth, renderHeight), from, to) <= 49;
        }).Select(point => point.Row).Distinct().ToArray(), cancellation.Token).ContinueWith(task =>
        {
            if (task.IsCanceled || Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (cancellation.IsCancellationRequested) return;
                ApplySelection(task.IsFaulted ? [] : task.Result, append);
                if (ReferenceEquals(selectionCancellation, cancellation)) { selectionCancellation = null; Cursor = null; }
                cancellation.Dispose();
            });
        }, TaskScheduler.Default);
    }

    private static double DistanceToSegmentSquared(Point point, Point from, Point to)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double lengthSquared = dx * dx + dy * dy;
        double t = lengthSquared <= 1e-12 ? 0
            : Math.Clamp(((point.X - from.X) * dx + (point.Y - from.Y) * dy) / lengthSquared, 0, 1);
        double x = point.X - (from.X + t * dx), y = point.Y - (from.Y + t * dy);
        return x * x + y * y;
    }

    private void RefreshSelectedPoints()
    {
        selectedPoints = selectedRows.Count == 0 || scene is null
            ? []
            : scene.Points.Where(point => selectedRows.Contains(point.Row)).ToArray();
    }

    private int FindNearestPoint(Point position, ViolinPoint[] points, int bins, double min, double max)
    {
        const double left = 110, top = 24, right = 22, bottom = 110;
        double width = Math.Max(1, RenderSize.Width - left - right);
        double height = Math.Max(1, RenderSize.Height - top - bottom);
        if (position.X < left || position.X > left + width || position.Y < top || position.Y > top + height) return -1;
        double categoryWidth = width / bins;
        int bin = Math.Clamp((int)((position.X - left) / categoryWidth), 0, bins - 1);
        // The violin shows a density silhouette, not a marker for every row.
        // Pick the closest observation by Y within the clicked X bin instead of
        // requiring the cursor to land on an invisible jittered point.
        double bestDistance = double.PositiveInfinity;
        int best = -1;
        foreach (var point in points)
        {
            if (point.BinIndex != bin) continue;
            double distance = Math.Abs(PointPosition(point, bins, min, max, RenderSize.Width, RenderSize.Height).Y - position.Y);
            if (distance < bestDistance) { bestDistance = distance; best = point.Row; }
        }
        return best;
    }

    private static Point PointPosition(ViolinPoint point, int bins, double min, double max, double renderWidth, double renderHeight)
    {
        const double left = 110, top = 24, right = 22, bottom = 110;
        double width = Math.Max(1, renderWidth - left - right);
        double height = Math.Max(1, renderHeight - top - bottom);
        double categoryWidth = width / bins;
        double center = left + categoryWidth * (point.BinIndex + .5);
        double jitterMax = Math.Max(2, Math.Min(categoryWidth * .18, 13));
        double jitter = (StableUnit("row", point.Row) * 2 - 1) * jitterMax;
        return new(center + jitter, top + (max - point.Y) / (max - min) * height);
    }

    private static bool IsInside(IReadOnlyList<Point> polygon, Point point)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i]; var b = polygon[j];
            if ((a.Y > point.Y) != (b.Y > point.Y)
                && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    private static void CancelSafely(CancellationTokenSource? source)
    {
        try { source?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void DrawText(DrawingContext dc, string text, double x, double y, double size, Brush brush)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, new Point(x, y));
    }

    private void DrawTextRight(DrawingContext dc, string text, double right, double y, double size, Brush brush)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, new Point(right - formatted.Width, y));
    }

    private void DrawVerticalText(DrawingContext dc, string text, double centerX, double centerY, double size, Brush brush)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.PushTransform(new RotateTransform(-90, centerX, centerY));
        dc.DrawText(formatted, new Point(centerX - formatted.Width / 2, centerY - formatted.Height / 2));
        dc.Pop();
    }

    private void DrawRotatedText(DrawingContext dc, string text, double x, double y, double maxWidth, double size)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, Brush(Color.FromRgb(50, 69, 87)), VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = Math.Max(24, maxWidth), Trimming = TextTrimming.CharacterEllipsis };
        dc.PushTransform(new RotateTransform(-42, x, y));
        dc.DrawText(formatted, new Point(x, y));
        dc.Pop();
    }
}
