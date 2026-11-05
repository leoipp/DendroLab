using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Dendro.Core.Regression;

namespace Dendro.Desktop;

internal sealed class ScatterPlot : FrameworkElement
{
    private readonly record struct RenderPoint(int Row, double X, double Y, string Group, int Color, bool Excluded);
    private readonly record struct RenderBinKey(int X, int Y, string Group, int Color, bool Excluded);
    private sealed class RenderBin(double x, double y, string group, int color, bool excluded)
    {
        public double XSum = x, YSum = y;
        public int Count = 1;
        public string Group = group;
        public int Color = color;
        public bool Excluded = excluded;
        public void Add(double x, double y) { XSum += x; YSum += y; Count++; }
    }
    private sealed record PreparedScene(
        int Generation, Size Size, Rect Plot, Dendro.Core.Regression.ChartBounds Bounds,
        RenderPoint[] ExactPoints, RenderBin[] Bins, double BinSize,
        PlotSelectionIndex Index, bool HasPoints, string Error = "", string EmptyMessage = "");
    private const int ExactPointLimit = 30_000;
    private const int MaximumRenderBins = 60_000;
    private JsonObject data = new(), config = new();
    internal JsonObject Data { get => data; set { data = value; InvalidateScene(); } }
    internal JsonObject Config { get => config; set { config = value; InvalidateScene(); } }
    private readonly DrawingVisual scene = new(), highlights = new(), overlay = new();
    private bool sceneDirty = true, scenePaintDirty = true, scenePending, selectionDirty = true, renderedRemoved;
    private Size renderedSize;
    private int sceneGeneration;
    private PreparedScene? preparedScene;
    private CancellationTokenSource? scenePreparation;
    private PlotSelectionIndex index = PlotSelectionIndex.Empty();
    private CancellationTokenSource? selectionCancellation;
    internal Task SelectionCompletion { get; private set; } = Task.CompletedTask;
    internal int SceneBuilds { get; private set; }
    private static void CancelSafely(CancellationTokenSource? source)
    {
        try { source?.Cancel(); }
        catch (ObjectDisposedException) { }
    }
    private void InvalidateScene()
    {
        sceneGeneration++;
        CancelSafely(scenePreparation);
        scenePending = false;
        preparedScene = null;
        index = PlotSelectionIndex.Empty();
        sceneDirty = scenePaintDirty = selectionDirty = true;
        CancelSafely(selectionCancellation);
        InvalidateVisual();
    }
    protected override int VisualChildrenCount => 3;
    protected override Visual GetVisualChild(int i) => i switch { 0 => scene, 1 => highlights, 2 => overlay, _ => throw new ArgumentOutOfRangeException(nameof(i)) };
    public bool RemoveEnabled { get; set; }
    public event Action<int[]>? RemoveRequested;
    public event Action<int[]>? SelectionChanged;
    private readonly HashSet<int> selectedRows = new();
    internal int[] SelectedRows => selectedRows.Order().ToArray();
    internal Dendro.Core.Regression.ChartBounds DisplayBounds => new(xmin, xmax, ymin, ymax);
    private Rect plot;
    private Point? start;
    private readonly List<Point> selectionPath = new();
    private double selectionDistance;
    private bool straightSelection, horizontalSelection;
    private bool constrainedStroke;
    private Point straightAnchor;
    private double xmin,
        xmax,
        ymin,
        ymax;
    private Point? cursorPosition;
    private static readonly Color[] Colors =
    {
        Color.FromRgb(33, 102, 157),
        Color.FromRgb(28, 137, 101),
        Color.FromRgb(213, 109, 44),
        Color.FromRgb(129, 85, 180),
        Color.FromRgb(195, 62, 94),
    };

    public ScatterPlot()
    {
        AddVisualChild(scene); AddVisualChild(highlights); AddVisualChild(overlay);
        MinHeight = 220;
        ClipToBounds = true;
        Focusable = true;
        Unloaded += (_, _) =>
        {
            CancelSafely(selectionCancellation);
            CancelSafely(scenePreparation);
            sceneDirty = true;
            scenePending = false;
        };
        MouseLeftButtonDown += Down;
        MouseMove += Move;
        MouseLeftButtonUp += Up;
        MouseLeave += (_, _) =>
        {
            if (start.HasValue) return;
            cursorPosition = null;
            InvalidateVisual();
        };
        LostMouseCapture += (_, _) =>
        {
            start = null;
            straightSelection = false;
            constrainedStroke = false;
            selectionPath.Clear();
            InvalidateVisual();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && !start.HasValue && selectedRows.Count > 0)
            {
                ConfirmRemoval();
                e.Handled = true;
                return;
            }
            if (e.Key != Key.Escape)
                return;
            if (!start.HasValue)
                ClearSelection();
            start = null;
            straightSelection = false;
            constrainedStroke = false;
            selectionPath.Clear();
            ReleaseMouseCapture();
            InvalidateVisual();
            e.Handled = true;
        };
    }

    private void Text(
        DrawingContext dc,
        string text,
        double x,
        double y,
        double size = 12,
        Brush? brush = null
    )
    {
        var f = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            size,
            brush ?? Brushes.SlateGray,
            VisualTreeHelper.GetDpi(this).PixelsPerDip
        );
        dc.DrawText(f, new Point(x, y));
    }

    private void TextRight(DrawingContext dc, string text, double right, double y, double size = 12)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, Brushes.SlateGray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, new Point(right - formatted.Width, y));
    }

    private void VerticalText(DrawingContext dc, string text, double centerX, double centerY, double size = 12)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, Brushes.SlateGray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.PushTransform(new RotateTransform(-90, centerX, centerY));
        dc.DrawText(formatted, new Point(centerX - formatted.Width / 2, centerY - formatted.Height / 2));
        dc.Pop();
    }

    private void DrawCrosshair(DrawingContext dc, Point cursor)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(205, 67, 91, 112)), 1)
        { DashStyle = DashStyles.Dash };
        pen.Freeze();
        dc.DrawLine(pen, new Point(cursor.X, plot.Top), new Point(cursor.X, plot.Bottom));
        dc.DrawLine(pen, new Point(plot.Left, cursor.Y), new Point(plot.Right, cursor.Y));

        double x = xmin + (cursor.X - plot.Left) / plot.Width * (xmax - xmin);
        double y = ymin + (plot.Bottom - cursor.Y) / plot.Height * (ymax - ymin);
        if (Config.B("log_x")) x = Math.Pow(10, x);
        if (Config.B("log_y")) y = Math.Pow(10, y);
        string xText = "X = " + x.ToString("G6", CultureInfo.CurrentCulture);
        string yText = "Y = " + y.ToString("G6", CultureInfo.CurrentCulture);
        DrawValueBadge(dc, xText,
            Math.Clamp(cursor.X, plot.Left, Math.Max(plot.Left, plot.Right - 90)), plot.Bottom - 22);
        DrawValueBadge(dc, yText, plot.Left + 3,
            Math.Clamp(cursor.Y - 10, plot.Top, Math.Max(plot.Top, plot.Bottom - 21)));
    }

    private void DrawValueBadge(DrawingContext dc, string text, double x, double y)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI Semibold"), 10, new SolidColorBrush(Color.FromRgb(38, 70, 96)),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var bounds = new Rect(x, y, formatted.Width + 10, formatted.Height + 5);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(238, 255, 255, 255)),
            new Pen(new SolidColorBrush(Color.FromRgb(126, 153, 176)), 1), bounds, 4, 4);
        dc.DrawText(formatted, new Point(x + 5, y + 2));
    }

    private double Transform(double v, bool x) =>
        (Config.B(x ? "log_x" : "log_y") ? Math.Log10(v) : v);

    private Point Screen(double x, double y) =>
        new(
            plot.Left + (Transform(x, true) - xmin) / (xmax - xmin) * plot.Width,
            plot.Bottom - (Transform(y, false) - ymin) / (ymax - ymin) * plot.Height
        );

    internal static int GroupColor(string value)
    {
        uint hash = 2166136261;
        foreach (char c in value)
            hash = (hash ^ c) * 16777619;
        return (int)(hash % 5);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (renderedSize != RenderSize || renderedRemoved != Config.B("show_removed"))
        {
            renderedSize = RenderSize;
            renderedRemoved = Config.B("show_removed");
            InvalidateScene();
        }
        if (sceneDirty && !scenePending)
            StartScenePreparation();
        if (scenePaintDirty)
        {
            scenePaintDirty = false;
            using var canvas = scene.RenderOpen();
            if (preparedScene is { } ready && ready.Generation == sceneGeneration)
                DrawScene(canvas, ready);
            else
                DrawPreparing(canvas);
        }
        if (selectionDirty) {
            selectionDirty = false;
            using var marked = highlights.RenderOpen();
            marked.PushClip(new RectangleGeometry(plot));
            double radius = Config.N("point_size", 3) + 3;
            var selectedPen = new Pen(Brushes.DarkOrange, 2); selectedPen.Freeze();
            foreach (int row in selectedRows)
                if (index.TryPosition(row, out var p)) marked.DrawEllipse(null, selectedPen, p, radius, radius);
            marked.Pop();
        }
        using var selection = overlay.RenderOpen();
        selection.PushClip(new RectangleGeometry(plot));
        if (start.HasValue && selectionPath.Count > 1)
        {
            if (straightSelection)
            {
                var linePen = new Pen(new SolidColorBrush(Color.FromArgb(220, 33, 102, 157)), 2) { DashStyle = DashStyles.Dash };
                selection.DrawLine(linePen, straightAnchor, selectionPath[^1]);
            }
            else selection.DrawGeometry(new SolidColorBrush(Color.FromArgb(30, 33, 102, 157)), new Pen(Brushes.SteelBlue, 1), SelectionGeometry());
        }
        if (cursorPosition is Point cursor && plot.Contains(cursor))
            DrawCrosshair(selection, cursor);
        selection.Pop();
    }

    private void DrawPreparing(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
        if (RenderSize.Width >= 180 && RenderSize.Height >= 160)
            Text(dc, scenePending ? "Preparando gráfico…" : "Selecione X e Y e gere o gráfico.", 25, 30, 17);
    }

    private void StartScenePreparation()
    {
        sceneDirty = false;
        scenePending = true;
        SceneBuilds++;
        CancelSafely(selectionCancellation);
        if (RenderSize.Width < 180 || RenderSize.Height < 160)
        {
            scenePending = false;
            preparedScene = new(sceneGeneration, RenderSize, Rect.Empty,
                new(0, 1, 0, 1), [], [], 1, PlotSelectionIndex.Empty(), false);
            scenePaintDirty = true;
            InvalidateVisual();
            return;
        }
        var cancellation = new CancellationTokenSource();
        scenePreparation = cancellation;
        int generation = sceneGeneration;
        Size size = RenderSize;
        JsonObject input = data;
        JsonObject options = config.DeepClone().AsObject();
        _ = PrepareSceneAsync(input, options, size, generation, cancellation);
        scenePaintDirty = true;
        InvalidateVisual();
    }

    private async Task PrepareSceneAsync(JsonObject input, JsonObject options, Size size, int generation, CancellationTokenSource cancellation)
    {
        try
        {
            PreparedScene prepared = await Task.Run(() => Prepare(input, options, size, generation, cancellation.Token), cancellation.Token);
            if (!cancellation.IsCancellationRequested && Dispatcher.HasShutdownStarted == false)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (cancellation.IsCancellationRequested || generation != sceneGeneration || size != RenderSize)
                        return;
                    preparedScene = prepared;
                    index = prepared.Index;
                    plot = prepared.Plot;
                    (xmin, xmax, ymin, ymax) = (prepared.Bounds.XMin, prepared.Bounds.XMax, prepared.Bounds.YMin, prepared.Bounds.YMax);
                    scenePending = false;
                    selectionDirty = scenePaintDirty = true;
                    InvalidateVisual();
                }, DispatcherPriority.Render);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested && Dispatcher.HasShutdownStarted == false)
                await Dispatcher.InvokeAsync(() =>
                {
                    if (generation != sceneGeneration) return;
                    preparedScene = new(generation, size, Rect.Empty, new(0, 1, 0, 1), [], [], 1, PlotSelectionIndex.Empty(), false, ex.Message);
                    scenePending = false;
                    scenePaintDirty = true;
                    InvalidateVisual();
                }, DispatcherPriority.Render);
        }
        finally
        {
            if (ReferenceEquals(scenePreparation, cancellation)) scenePreparation = null;
            cancellation.Dispose();
        }
    }

    private static PreparedScene Prepare(JsonObject input, JsonObject options, Size size, int generation, CancellationToken cancellation)
    {
        var points = ChartPointView.Open(input, options);
        var plot = new Rect(110, 57, Math.Max(1, size.Width - 140), Math.Max(1, size.Height - 117));
        if (points.Count == 0)
            return new(generation, size, plot, new(0, 1, 0, 1), [], [], 1, PlotSelectionIndex.Empty(), false,
                EmptyMessage: "Selecione X e Y e gere o gráfico.");

        bool logX = options.B("log_x"), logY = options.B("log_y"), showRemoved = options.B("show_removed");
        bool stratified = options.S("stratum").Length > 0;
        var groupColors = options["group_colors"] as JsonObject ?? new JsonObject();
        double TransformValue(double value, bool x) => x && logX || !x && logY ? Math.Log10(value) : value;
        double xMinData = double.PositiveInfinity, xMaxData = double.NegativeInfinity;
        double yMinData = double.PositiveInfinity, yMaxData = double.NegativeInfinity;
        int validCount = 0;
        var categories = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < points.Count; i++)
        {
            if ((i & 8191) == 0) cancellation.ThrowIfCancellationRequested();
            var point = points.Get(i);
            bool excluded = point.Excluded;
            if (!showRemoved && excluded) continue;
            double x = TransformValue(point.X, true), y = TransformValue(point.Y, false);
            if (!double.IsFinite(x) || !double.IsFinite(y)) continue;
            xMinData = Math.Min(xMinData, x); xMaxData = Math.Max(xMaxData, x);
            yMinData = Math.Min(yMinData, y); yMaxData = Math.Max(yMaxData, y);
            validCount++;
            string group = point.Group;
            categories.Add(excluded ? "__excluded" : groupColors.ContainsKey(group)
                ? group : !stratified
                    ? "__color_" + options.N("color", 0).ToString(CultureInfo.InvariantCulture)
                    : "__color_" + GroupColor(group).ToString(CultureInfo.InvariantCulture));
        }
        var bounds = CalculateBounds(options, xMinData, xMaxData, yMinData, yMaxData);
        if (validCount == 0)
            return new(generation, size, plot, bounds, [], [], 1, PlotSelectionIndex.Empty(), false);

        bool exact = validCount <= ExactPointLimit;
        var exactPoints = exact ? new List<RenderPoint>(validCount) : null;
        var selectableIndexes = new int[validCount];
        int selectableCount = 0;
        var selectionBuilder = new PlotSelectionIndex.Builder();
        double binSize = exact ? 1 : Math.Max(1, Math.Sqrt(plot.Width * plot.Height * Math.Max(1, categories.Count) / MaximumRenderBins));
        var bins = exact ? null : new Dictionary<RenderBinKey, RenderBin>(Math.Min(MaximumRenderBins, validCount));
        var customColors = groupColors;
        int defaultColor = (int)options.N("color", 0);
        double xRange = bounds.XMax - bounds.XMin, yRange = bounds.YMax - bounds.YMin;
        for (int i = 0; i < points.Count; i++)
        {
            if ((i & 8191) == 0) cancellation.ThrowIfCancellationRequested();
            var point = points.Get(i);
            bool excluded = point.Excluded;
            if (!showRemoved && excluded) continue;
            double tx = TransformValue(point.X, true), ty = TransformValue(point.Y, false);
            if (!double.IsFinite(tx) || !double.IsFinite(ty)) continue;
            double x = plot.Left + (tx - bounds.XMin) / xRange * plot.Width;
            double y = plot.Bottom - (ty - bounds.YMin) / yRange * plot.Height;
            if (!double.IsFinite(x) || !double.IsFinite(y)) continue;
            string group = point.Group;
            int color = !stratified ? defaultColor : GroupColor(group);
            if (!excluded && plot.Contains(new Point(x, y)))
            {
                selectableIndexes[selectableCount++] = i;
                selectionBuilder.Add(point.Row, new Point(x, y));
            }
            if (exact)
            {
                if (plot.Contains(new Point(x, y))) exactPoints!.Add(new(point.Row, x, y, group, color, excluded));
                continue;
            }
            if (!plot.Contains(new Point(x, y))) continue;
            string colorGroup = excluded ? "" : customColors.ContainsKey(group) ? group : "";
            int pixelX = (int)Math.Floor((x - plot.Left) / binSize), pixelY = (int)Math.Floor((y - plot.Top) / binSize);
            var key = new RenderBinKey(pixelX, pixelY, colorGroup, color, excluded);
            if (bins!.TryGetValue(key, out var bin)) bin.Add(x, y);
            else bins.Add(key, new(x, y, colorGroup, color, excluded));
        }
        cancellation.ThrowIfCancellationRequested();
        var index = PlotSelectionIndex.Create(points, selectableIndexes, selectableCount, selectionBuilder, plot, bounds,
            logX, logY, cancellation);
        return new(generation, size, plot, bounds, exactPoints?.ToArray() ?? [],
            bins?.Values.ToArray() ?? [], binSize, index, validCount > 0);
    }

    private static Dendro.Core.Regression.ChartBounds CalculateBounds(JsonObject config, double xDataMin, double xDataMax, double yDataMin, double yDataMax)
    {
        double TransformBound(double value, bool x) => x && config.B("log_x") || !x && config.B("log_y") ? Math.Log10(value) : value;
        (double Min, double Max) Axis(bool x, double dataMin, double dataMax)
        {
            string lo = x ? "xmin" : "ymin", hi = x ? "xmax" : "ymax";
            double min = config[lo] != null ? TransformBound(config.N(lo), x) : double.IsFinite(dataMin) ? dataMin : 0;
            double max = config[hi] != null ? TransformBound(config.N(hi), x) : double.IsFinite(dataMax) ? dataMax : 1;
            if (max <= min) { min -= .5; max += .5; }
            double padding = (max - min) * .04;
            if (config[lo] == null) min -= padding;
            if (config[hi] == null) max += padding;
            return (min, max);
        }
        var x = Axis(true, xDataMin, xDataMax); var y = Axis(false, yDataMin, yDataMax);
        return new(x.Min, x.Max, y.Min, y.Max);
    }

    private void DrawScene(DrawingContext dc, PreparedScene prepared)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
        if (RenderSize.Width < 180 || RenderSize.Height < 160)
            return;
        if (prepared.Error.Length > 0)
        {
            Text(dc, "Não foi possível preparar o gráfico: " + prepared.Error, 25, 30, 14, Brushes.Firebrick);
            return;
        }
        plot = prepared.Plot;
        (xmin, xmax, ymin, ymax) = (prepared.Bounds.XMin, prepared.Bounds.XMax, prepared.Bounds.YMin, prepared.Bounds.YMax);
        if (!prepared.HasPoints)
        {
            Text(dc, prepared.EmptyMessage.Length > 0 ? prepared.EmptyMessage : "Todos os pontos foram removidos. Use Desfazer ou Mostrar removidos.", 25, 60, 15);
            return;
        }
        Text(dc, Config.S("title"), plot.Left, 15, 19, Brushes.DarkSlateGray);
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(229, 235, 243)), 1) { DashStyle = DashStyles.Dash };
        foreach(var tick in Dendro.Core.Regression.ChartTicks.Create(xmin,xmax,Config.N("x_tick_step"),Config.B("log_x")))
        {
            double x=plot.Left+(tick.Position-xmin)/(xmax-xmin)*plot.Width;
            if (Config.B("grid", true))
                dc.DrawLine(grid, new Point(x, plot.Top), new Point(x, plot.Bottom));
            Text(dc,tick.Label,x-15,plot.Bottom+9);
        }
        foreach(var tick in Dendro.Core.Regression.ChartTicks.Create(ymin,ymax,Config.N("y_tick_step"),Config.B("log_y")))
        {
            double y=plot.Bottom-(tick.Position-ymin)/(ymax-ymin)*plot.Height;
            if(Config.B("grid",true))
                dc.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            TextRight(dc, tick.Label, plot.Left - 7, y - 8);
        }
        dc.DrawLine(new Pen(Brushes.SlateGray, 1), plot.BottomLeft, plot.BottomRight);
        dc.DrawLine(new Pen(Brushes.SlateGray, 1), plot.BottomLeft, plot.TopLeft);
        Text(
            dc,
            Config.S("x_label", Config.S("x")),
            plot.Left + plot.Width / 2 - 30,
            plot.Bottom + 34
        );
        VerticalText(dc, Config.S("y_label", Config.S("y")), 38, plot.Top + plot.Height / 2);
        dc.PushClip(new RectangleGeometry(plot));
        foreach (var reference in Config["reference_lines"] as JsonArray ?? [])
        {
            double coordinate =
                plot.Left + (Transform(reference.N("x"), true) - xmin) / (xmax - xmin) * plot.Width;
            if (!double.IsFinite(coordinate) || coordinate < plot.Left || coordinate > plot.Right)
                continue;
            dc.DrawLine(
                new Pen(Brushes.SlateGray, 1) { DashStyle = DashStyles.Dot },
                new Point(coordinate, plot.Top),
                new Point(coordinate, plot.Bottom)
            );
            Text(dc, reference.S("label"), coordinate + 4, plot.Top + 4, 11);
        }
        double pointAlpha = Math.Clamp(Config.N("point_alpha", 1), 0, 1);
        var pointBrushes = Colors.Select(c => { var b = new SolidColorBrush(c) { Opacity = pointAlpha }; b.Freeze(); return b; }).ToArray();
        var groupBrushes = new Dictionary<string, SolidColorBrush>(StringComparer.Ordinal);
        foreach (var pair in Config["group_colors"] as JsonObject ?? new JsonObject())
        {
            var custom = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Value!.GetValue<string>())) { Opacity = pointAlpha };
            custom.Freeze(); groupBrushes[pair.Key] = custom;
        }
        double radius = Config.N("point_size", 3);
        var densityBrushes = new Dictionary<(string Group, int Color, bool Excluded, int Tier), Brush>();
        Brush BinBrush(RenderBin bin, int tier)
        {
            var key = (bin.Group, bin.Color, bin.Excluded, tier);
            if (densityBrushes.TryGetValue(key, out var cached)) return cached;
            Brush original = bin.Excluded ? Brushes.LightSlateGray
                : groupBrushes.GetValueOrDefault(bin.Group) ?? pointBrushes[Math.Abs(bin.Color % Colors.Length)];
            if (original is SolidColorBrush solid)
            {
                double density = .38 + .12 * tier;
                var translucent = new SolidColorBrush(solid.Color) { Opacity = Math.Clamp(solid.Opacity * density, 0, 1) };
                translucent.Freeze();
                original = translucent;
            }
            densityBrushes[key] = original;
            return original;
        }
        if (prepared.ExactPoints.Length > 0)
        {
            foreach (var p in prepared.ExactPoints)
            {
                Point pt = new(p.X, p.Y);
                var brush = p.Excluded ? Brushes.LightSlateGray
                    : groupBrushes.GetValueOrDefault(p.Group) ?? pointBrushes[Math.Abs(p.Color % Colors.Length)];
                if (p.Excluded)
                {
                    var pen = new Pen(brush, 1.3);
                    dc.DrawLine(pen, new Point(pt.X - 4, pt.Y - 4), new Point(pt.X + 4, pt.Y + 4));
                    dc.DrawLine(pen, new Point(pt.X - 4, pt.Y + 4), new Point(pt.X + 4, pt.Y - 4));
                }
                else dc.DrawEllipse(brush, null, pt, radius, radius);
            }
        }
        else
        {
            foreach (var bin in prepared.Bins)
            {
                var pt = new Point(bin.XSum / bin.Count, bin.YSum / bin.Count);
                int tier = bin.Count <= 1 ? 1 : Math.Min(5, (int)Math.Ceiling(Math.Log2(bin.Count)) + 1);
                var brush = BinBrush(bin, tier);
                if (bin.Excluded)
                {
                    var pen = new Pen(brush, 1.3);
                    dc.DrawLine(pen, new Point(pt.X - 4, pt.Y - 4), new Point(pt.X + 4, pt.Y + 4));
                    dc.DrawLine(pen, new Point(pt.X - 4, pt.Y + 4), new Point(pt.X + 4, pt.Y - 4));
                }
                else dc.DrawEllipse(brush, null, pt, radius, radius);
            }
        }
        foreach (var p in Config["frontier_points"] as JsonArray ?? new JsonArray())
        {
            Point pt = Screen(p!.N("x"), p.N("y"));
            if (double.IsFinite(pt.X) && double.IsFinite(pt.Y))
                dc.DrawRectangle(
                    null,
                    new Pen(p.N("side") == 1 ? Brushes.SeaGreen : Brushes.DarkOrange, 1.5),
                    new Rect(pt.X - 4, pt.Y - 4, 8, 8)
                );
        }
        var fits = Config["fits"] as JsonArray;
        var series = Data["curves"] as JsonArray;
        if (fits != null && series != null)
            foreach (
                int i in Dendro
                    .Core.Regression.FitDisplay.Visible(Config)
                    .Where(i => i < series.Count)
            )
            {
                var fit = fits[i]!;
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    bool begun = false;
                    foreach (var p in series[i]!.AsArray())
                    {
                        if (p?[0] == null || p[1] == null)
                            continue;
                        Point point = Screen(p[0]!.GetValue<double>(), p[1]!.GetValue<double>());
                        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
                        {
                            begun = false;
                            continue;
                        }
                        if (!begun)
                        {
                            context.BeginFigure(point, false, false);
                            begun = true;
                        }
                        else
                            context.LineTo(point, true, false);
                    }
                }
                geometry.Freeze();
                var curveBrush = (Brush)
                    new BrushConverter().ConvertFromString(
                        Dendro.Core.Regression.FitDisplay.Color(fit)
                    )!;
                var curvePen = new Pen(curveBrush, 2.2);
                if (fit.N("frontier") == 2 || fit.B("line_dashed"))
                    curvePen.DashStyle = DashStyles.Dash;
                dc.DrawGeometry(null, curvePen, geometry);
            }
        foreach (var reference in Data["reference_curves"] as JsonArray ?? [])
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                bool begun = false;
                foreach (var p in reference!["points"]!.AsArray())
                {
                    if (p?[0] == null || p[1] == null) continue;
                    Point point = Screen(p[0]!.GetValue<double>(), p[1]!.GetValue<double>());
                    if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) { begun = false; continue; }
                    if (!begun) { context.BeginFigure(point, false, false); begun = true; }
                    else context.LineTo(point, true, false);
                }
            }
            geometry.Freeze();
            var brush = (Brush)new BrushConverter().ConvertFromString(reference!.S("color", "#8E44AD"))!;
            var pen = new Pen(brush, 2.8) { DashStyle = DashStyles.Dash };
            dc.DrawGeometry(null, pen, geometry);
        }
        dc.Pop();
    }

    private void Down(object s, MouseButtonEventArgs e)
    {
        cursorPosition = e.GetPosition(this);
        if (RemoveEnabled)
            Focus();
        BeginSelection(e.GetPosition(this));
        if (start.HasValue)
            CaptureMouse();
        e.Handled = start.HasValue;
    }

    internal void BeginSelection(Point position)
    {
        if (!RemoveEnabled || !plot.Contains(position))
            return;
        CancelSafely(selectionCancellation);
        Cursor = null;
        start = position;
        selectionDistance = 0;
        selectionPath.Clear();
        selectionPath.Add(position);
        straightSelection = false;
        constrainedStroke = false;
        InvalidateVisual();
    }

    private void Move(object s, MouseEventArgs e)
    {
        Point position = e.GetPosition(this);
        cursorPosition = plot.Contains(position) ? position
            : start.HasValue ? new Point(Math.Clamp(position.X, plot.Left, plot.Right), Math.Clamp(position.Y, plot.Top, plot.Bottom))
            : null;
        ContinueSelection(position);
        InvalidateVisual();
    }

    internal void ContinueSelection(Point position)
    {
        if (!start.HasValue)
            return;
        var point = new Point(
            Math.Clamp(position.X, plot.Left, plot.Right),
            Math.Clamp(position.Y, plot.Top, plot.Bottom)
        );
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (!straightSelection && shift && (point - selectionPath[^1]).Length >= 6)
        {
            straightAnchor = selectionPath[^1];
            double dx = Math.Abs(point.X - straightAnchor.X), dy = Math.Abs(point.Y - straightAnchor.Y);
            horizontalSelection = dx >= dy;
            straightSelection = true;
            constrainedStroke = true;
        }
        if (straightSelection && shift)
        {
            point = horizontalSelection
                ? new Point(point.X, straightAnchor.Y)
                : new Point(straightAnchor.X, point.Y);
            selectionDistance = Math.Max(selectionDistance, (point - start.Value).Length);
            if ((selectionPath[^1] - straightAnchor).Length < 1e-6) selectionPath.Add(point);
            else selectionPath[^1] = point;
            InvalidateVisual();
            return;
        }
        straightSelection = false;
        selectionDistance = Math.Max(selectionDistance, (point - start.Value).Length);
        if ((point - selectionPath[^1]).Length < 2)
            return;
        selectionPath.Add(point);
        InvalidateVisual();
    }

    private StreamGeometry SelectionGeometry()
    {
        var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
        using (var context = geometry.Open())
        {
            context.BeginFigure(selectionPath[0], true, true);
            for (int i = 1; i < selectionPath.Count; i++)
                context.LineTo(selectionPath[i], true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private void Up(object s, MouseButtonEventArgs e)
    {
        bool selecting = start.HasValue;
        EndSelection(e.GetPosition(this));
        e.Handled = selecting;
    }

    internal Point PositionOf(int row)
    {
        if (index.TryPosition(row, out var position)) return position;
        var points = ChartPointView.Open(Data, Config);
        var point = points.Points().First(p => p.Row == row);
        var bounds = preparedScene?.Bounds ?? Dendro.Core.Regression.ChartBounds.Calculate(
            points.Points().Where(p => Config.B("show_removed") || !p.Excluded)
                .Select(p => (p.X, p.Y)), Config);
        var size = preparedScene?.Size ?? RenderSize;
        var target = preparedScene?.Plot ?? new Rect(110, 57, Math.Max(1, size.Width - 140), Math.Max(1, size.Height - 117));
        double tx = Config.B("log_x") ? Math.Log10(point.X) : point.X;
        double ty = Config.B("log_y") ? Math.Log10(point.Y) : point.Y;
        return new(target.Left + (tx - bounds.XMin) / (bounds.XMax - bounds.XMin) * target.Width,
            target.Bottom - (ty - bounds.YMin) / (bounds.YMax - bounds.YMin) * target.Height);
    }

    internal void EndSelection(Point position)
    {
        if (!start.HasValue)
            return;
        ContinueSelection(position);
        var polygon = selectionPath.ToArray();
        bool click = selectionDistance < 5;
        bool lineSelection = constrainedStroke && polygon.Length == 2;
        straightSelection = false;
        constrainedStroke = false;
        start = null;
        selectionPath.Clear();
        ReleaseMouseCapture();
        bool append = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (click) ApplySelection(index.Nearest(position), append);
        else if (lineSelection && polygon.Length >= 2)
        {
            if (index.Count < 20_000) ApplySelection(index.Stroke(polygon[0], polygon[^1], 7, CancellationToken.None), append);
            else SelectionCompletion = CompleteStroke(index, polygon[0], polygon[^1], append);
        }
        else if (index.Count < 20000 && (long)index.Count * polygon.Length < 1_000_000)
            ApplySelection(index.Lasso(polygon, CancellationToken.None), append);
        else SelectionCompletion = CompleteLasso(index, polygon, append);
        InvalidateVisual();
    }

    private async Task CompleteLasso(PlotSelectionIndex snapshot, Point[] polygon, bool append)
    {
        CancelSafely(selectionCancellation);
        var cancellation = new CancellationTokenSource(); selectionCancellation = cancellation;
        var token = cancellation.Token;
        Cursor = Cursors.AppStarting;
        try {
            var rows = await Task.Run(() => snapshot.Lasso(polygon, token), token);
            if (!token.IsCancellationRequested) ApplySelection(rows, append);
        } catch (OperationCanceledException) { }
        finally {
            if (ReferenceEquals(selectionCancellation, cancellation)) { selectionCancellation = null; Cursor = null; }
            cancellation.Dispose();
        }
    }

    private async Task CompleteStroke(PlotSelectionIndex snapshot, Point from, Point to, bool append)
    {
        CancelSafely(selectionCancellation);
        var cancellation = new CancellationTokenSource(); selectionCancellation = cancellation;
        var token = cancellation.Token;
        Cursor = Cursors.AppStarting;
        try
        {
            var rows = await Task.Run(() => snapshot.Stroke(from, to, 7, token), token);
            if (!token.IsCancellationRequested) ApplySelection(rows, append);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(selectionCancellation, cancellation)) { selectionCancellation = null; Cursor = null; }
            cancellation.Dispose();
        }
    }

    private void ApplySelection(int[] rows, bool append)
    {
        if (!append)
            selectedRows.Clear();
        selectedRows.UnionWith(rows);
        selectionDirty = true;
        SelectionChanged?.Invoke(SelectedRows);
        InvalidateVisual();
    }

    internal void SelectRows(IEnumerable<int> rows)
    {
        selectionCancellation?.Cancel();
        ApplySelection(rows.Distinct().ToArray(),false);
    }

    internal void SynchronizeSelection(IEnumerable<int> rows)
    {
        CancelSafely(selectionCancellation); Cursor = null;
        selectedRows.Clear();
        selectedRows.UnionWith(rows);
        selectionDirty = true;
        InvalidateVisual();
    }

    internal void ClearSelection()
    {
        CancelSafely(selectionCancellation); Cursor = null;
        selectedRows.Clear();
        selectionDirty = true;
        SelectionChanged?.Invoke(SelectedRows);
        InvalidateVisual();
    }

    internal void ConfirmRemoval()
    {
        if (selectedRows.Count == 0)
            return;
        var rows = SelectedRows;
        ClearSelection();
        RemoveRequested?.Invoke(rows);
    }
}
