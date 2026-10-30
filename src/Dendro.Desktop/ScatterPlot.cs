using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Dendro.Desktop;

internal sealed class ScatterPlot : FrameworkElement
{
    private JsonObject data = new(), config = new();
    internal JsonObject Data { get => data; set { data = value; InvalidateScene(); } }
    internal JsonObject Config { get => config; set { config = value; InvalidateScene(); } }
    private readonly DrawingVisual scene = new(), highlights = new(), overlay = new();
    private bool sceneDirty = true, selectionDirty = true, renderedRemoved;
    private Size renderedSize;
    private PlotSelectionIndex index = new([]);
    private CancellationTokenSource? selectionCancellation;
    internal Task SelectionCompletion { get; private set; } = Task.CompletedTask;
    internal int SceneBuilds { get; private set; }
    private void InvalidateScene() { sceneDirty = true; selectionCancellation?.Cancel(); InvalidateVisual(); }
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
    private double xmin,
        xmax,
        ymin,
        ymax;
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
        Unloaded += (_, _) => selectionCancellation?.Cancel();
        MouseLeftButtonDown += Down;
        MouseMove += Move;
        MouseLeftButtonUp += Up;
        LostMouseCapture += (_, _) =>
        {
            start = null;
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
        if (sceneDirty || renderedSize != RenderSize || renderedRemoved != Config.B("show_removed")) {
            selectionCancellation?.Cancel();
            sceneDirty = false; renderedSize = RenderSize; renderedRemoved = Config.B("show_removed");
            index = new([]); SceneBuilds++;
            selectionDirty = true;
            using var canvas = scene.RenderOpen(); DrawScene(canvas);
        }
        if (selectionDirty) {
            selectionDirty = false;
            using var marked = highlights.RenderOpen();
            marked.PushClip(new RectangleGeometry(plot));
            double radius = Config.N("point_size", 3) + 3;
            var selectedPen = new Pen(Brushes.DarkOrange, 2); selectedPen.Freeze();
            foreach (int row in selectedRows)
                if (index.Positions.TryGetValue(row, out var p)) marked.DrawEllipse(null, selectedPen, p, radius, radius);
            marked.Pop();
        }
        using var selection = overlay.RenderOpen();
        selection.PushClip(new RectangleGeometry(plot));
        if (start.HasValue && selectionPath.Count > 1)
            selection.DrawGeometry(new SolidColorBrush(Color.FromArgb(30, 33, 102, 157)), new Pen(Brushes.SteelBlue, 1), SelectionGeometry());
        selection.Pop();
    }

    private void DrawScene(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
        if (RenderSize.Width < 180 || RenderSize.Height < 160)
            return;
        plot = new Rect(
            78,
            57,
            Math.Max(1, RenderSize.Width - 108),
            Math.Max(1, RenderSize.Height - 117)
        );
        var points = Data["points"] as JsonArray;
        if (points == null || points.Count == 0)
        {
            Text(dc, "Selecione X e Y e gere o gráfico.", 25, 30, 17);
            return;
        }
        var visible = points.Where(p => Config.B("show_removed") || !p!.B("excluded")).ToArray();
        if (visible.Length == 0)
        {
            Text(
                dc,
                "Todos os pontos foram removidos. Use Desfazer ou Mostrar removidos.",
                25,
                60,
                15
            );
            return;
        }
        var bounds = Dendro.Core.Regression.ChartBounds.Calculate(
            visible.Select(p => (p!.N("x"), p.N("y"))),
            Config
        );
        (xmin, xmax, ymin, ymax) = (bounds.XMin, bounds.XMax, bounds.YMin, bounds.YMax);
        Text(dc, Config.S("title"), plot.Left, 15, 19, Brushes.DarkSlateGray);
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(229, 235, 243)), 1);
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
            Text(dc,tick.Label,5,y-8);
        }
        dc.DrawLine(new Pen(Brushes.SlateGray, 1), plot.BottomLeft, plot.BottomRight);
        dc.DrawLine(new Pen(Brushes.SlateGray, 1), plot.BottomLeft, plot.TopLeft);
        Text(
            dc,
            Config.S("x_label", Config.S("x")),
            plot.Left + plot.Width / 2 - 30,
            plot.Bottom + 34
        );
        Text(dc, Config.S("y_label", Config.S("y")), 8, 34);
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
        var selectable = new List<PlotScreenPoint>(visible.Length);
        double pointAlpha = Math.Clamp(Config.N("point_alpha", 1), 0, 1);
        var pointBrushes = Colors.Select(c => { var b = new SolidColorBrush(c) { Opacity = pointAlpha }; b.Freeze(); return b; }).ToArray();
        var groupBrushes = new Dictionary<string, SolidColorBrush>(StringComparer.Ordinal);
        foreach (var pair in Config["group_colors"] as JsonObject ?? new JsonObject())
        {
            var custom = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Value!.GetValue<string>())) { Opacity = pointAlpha };
            custom.Freeze(); groupBrushes[pair.Key] = custom;
        }
        double radius = Config.N("point_size", 3);
        foreach (var p in visible)
        {
            Point pt = Screen(p!.N("x"), p.N("y"));
            if (!double.IsFinite(pt.X) || !double.IsFinite(pt.Y))
                continue;
            bool excluded = p.B("excluded");
            int color = string.IsNullOrEmpty(Config.S("stratum"))
                ? (int)Config.N("color", 0)
                : GroupColor(p.S("group"));
            var brush = excluded
                ? Brushes.LightSlateGray
                : groupBrushes.GetValueOrDefault(p.S("group")) ?? pointBrushes[Math.Abs(color) % Colors.Length];
            if (excluded)
            {
                var pen = new Pen(brush, 1.3);
                dc.DrawLine(pen, new Point(pt.X - 4, pt.Y - 4), new Point(pt.X + 4, pt.Y + 4));
                dc.DrawLine(pen, new Point(pt.X - 4, pt.Y + 4), new Point(pt.X + 4, pt.Y - 4));
            }
            else
            {
                dc.DrawEllipse(brush, null, pt, radius, radius);
                if (plot.Contains(pt)) selectable.Add(new((int)p.N("row"), pt.X, pt.Y));
            }
        }
        index = new(selectable);
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
        dc.Pop();
    }

    private void Down(object s, MouseButtonEventArgs e)
    {
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
        selectionCancellation?.Cancel();
        Cursor = null;
        start = position;
        selectionDistance = 0;
        selectionPath.Clear();
        selectionPath.Add(position);
        InvalidateVisual();
    }

    private void Move(object s, MouseEventArgs e)
    {
        ContinueSelection(e.GetPosition(this));
    }

    internal void ContinueSelection(Point position)
    {
        if (!start.HasValue)
            return;
        var point = new Point(
            Math.Clamp(position.X, plot.Left, plot.Right),
            Math.Clamp(position.Y, plot.Top, plot.Bottom)
        );
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
        var point = Data["points"]!.AsArray().First(p => p!.N("row") == row)!;
        return Screen(point.N("x"), point.N("y"));
    }

    internal void EndSelection(Point position)
    {
        if (!start.HasValue)
            return;
        ContinueSelection(position);
        var polygon = selectionPath.ToArray();
        bool click = selectionDistance < 5;
        start = null;
        selectionPath.Clear();
        ReleaseMouseCapture();
        bool append = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (click) ApplySelection(index.Nearest(position), append);
        else if (index.Count < 20000 && (long)index.Count * polygon.Length < 1_000_000)
            ApplySelection(index.Lasso(polygon, CancellationToken.None), append);
        else SelectionCompletion = CompleteLasso(index, polygon, append);
        InvalidateVisual();
    }

    private async Task CompleteLasso(PlotSelectionIndex snapshot, Point[] polygon, bool append)
    {
        selectionCancellation?.Cancel();
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

    internal void ClearSelection()
    {
        selectionCancellation?.Cancel(); Cursor = null;
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
