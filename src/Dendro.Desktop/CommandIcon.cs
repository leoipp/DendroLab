using System.Windows;
using System.Windows.Media;

namespace Dendro.Desktop;

public enum CommandIconKind
{
    New,
    Open,
    Save,
    SaveAs,
    Settings,
    Volume,
    Growth,
    Contents,
    Tools,
    History,
    Layout,
    Table,
    Database,
    Shape,
    Chart,
    Regression,
    Frontier,
    Increments,
    Identity,
    Statistics,
    Merge,
    Stratify,
    Processor,
    Rename,
    Remove,
    Search,
    Add,
    CalculateColumn,
    CurveComparison,
    Anomaly,
    ProcessingReview,
    InventoryAudit,
    BiologicalAsset,
    IntegratedCleaning,
}

public sealed class CommandIcon : FrameworkElement
{
    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(Brush), typeof(CommandIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush? Tint
    {
        get => (Brush?)GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind),
        typeof(CommandIconKind),
        typeof(CommandIcon),
        new FrameworkPropertyMetadata(
            CommandIconKind.New,
            FrameworkPropertyMetadataOptions.AffectsRender
        )
    );
    public CommandIconKind Kind
    {
        get => (CommandIconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }
    private static Brush Frozen(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
    private static readonly Brush Ink = Frozen("#344257"), Accent = Frozen("#4F64D9");
    private static readonly Dictionary<CommandIconKind, Brush> Accents = new()
    {
        [CommandIconKind.Processor] = Frozen("#5B5CE2"), [CommandIconKind.CalculateColumn] = Frozen("#7C4DCE"),
        [CommandIconKind.CurveComparison] = Frozen("#0891A2"), [CommandIconKind.Anomaly] = Frozen("#D64D73"),
        [CommandIconKind.Frontier] = Frozen("#169B8A"), [CommandIconKind.Statistics] = Frozen("#8262CE"),
        [CommandIconKind.Increments] = Frozen("#167DAD"), [CommandIconKind.Remove] = Frozen("#D64D73"),
    };
    private static readonly Dictionary<CommandIconKind, string[]> Paths = new()
    {
        [CommandIconKind.IntegratedCleaning] = ["M2 4 H10 V14 H2 Z M14 10 H22 V20 H14 Z M4 11 L8 7 M16 17 L20 13", "M10 8 H13 Q16 8 16 5 V3 M13 5 L16 2 19 5 M8 18 H11 M9.5 16.5 V19.5"],
        [CommandIconKind.New] = ["M6 3.5 Q6 2.5 7 2.5 H14 L19 7.5 V20 Q19 21.5 17.5 21.5 H7.5 Q6 21.5 6 20 Z M14 2.5 V7.5 H19", "M9 14 H16 M12.5 10.5 V17.5"],
        [CommandIconKind.Open] = ["M3 7.5 Q3 6 4.5 6 H9 L11 8 H19.5 Q21 8 21 9.5 V10 M3 9 V18.5 Q3 20 4.5 20 H18.5 Q20 20 20.3 18.5 L22 11 H7 L5 18", "M12 4 H19 M16 1.5 L19 4 16 6.5"],
        [CommandIconKind.Save] = ["M4 4.5 Q4 3 5.5 3 H17 L21 7 V19.5 Q21 21 19.5 21 H5.5 Q4 21 4 19.5 Z M8 3 V9 H16 V3 M8 21 V14 H17 V21", "M13.5 5.5 V7"],
        [CommandIconKind.SaveAs] = ["M4 19.5 V4.5 Q4 3 5.5 3 H17 L20 6 V10 M8 3 V9 H16 V3 M4 20 H9 M8 20 V14 H12", "M13 17 L19 11 22 14 16 20 12 21 Z M17.5 12.5 L20.5 15.5"],
        [CommandIconKind.Settings] = ["M5 3 V21 M12 3 V21 M19 3 V21", "M3 7 H7 V11 H3 Z M10 14 H14 V18 H10 Z M17 6 H21 V10 H17 Z"],
        [CommandIconKind.Volume] = ["M4 8 L12 4 20 8 V17 L12 21 4 17 Z M4 8 L12 12 20 8 M12 12 V21", "M8 6 L16 10 M16 10 V14"],
        [CommandIconKind.Growth] = ["M4 20 H21 M7 20 V13 M12 20 V8 M17 20 V4", "M4 11 L10 6 14 7 20 2 M16.5 2 H20 V5.5"],
        [CommandIconKind.Contents] = ["M4 3.5 H20 V20.5 H4 Z M4 8.5 H20", "M7.5 12 H9 M12 12 H17 M7.5 16.5 H9 M12 16.5 H17"],
        [CommandIconKind.Tools] = ["M14 4 Q17 1.5 21 3 L17 7 18 10 21 11 Q20 16 15 15 L8 22 3 17 10 10 Q8 5 11 3", "M5.5 17 L8 19.5"],
        [CommandIconKind.History] = ["M4 8 A9 9 0 1 1 4 17 M3 3 V9 H9", "M12 7 V12 L16 14"],
        [CommandIconKind.Layout] = ["M3 4 H21 V20 H3 Z M3 9 H21 M9 9 V20", "M13 12 H18 M13 16 H18"],
        [CommandIconKind.Table] = ["M4.5 4 H19.5 Q21 4 21 5.5 V18.5 Q21 20 19.5 20 H4.5 Q3 20 3 18.5 V5.5 Q3 4 4.5 4 Z M3 9 H21 M9 9 V20", "M9 14.5 H21 M15 9 V20"],
        [CommandIconKind.Database] = ["M4 6 C4 2 20 2 20 6 V18 C20 22 4 22 4 18 Z M4 6 C4 10 20 10 20 6", "M4 12 C4 16 20 16 20 12"],
        [CommandIconKind.Shape] = ["M4 6 L10 3 19 6 21 16 14 21 4 17 Z", "M4 6 L13 11 19 6 M13 11 L14 21 M4 17 L13 11 21 16"],
        [CommandIconKind.Chart] = ["M4 3 V20 H21", "M7 14 A1.2 1.2 0 1 0 9.4 14 A1.2 1.2 0 1 0 7 14 M12 10 A1.2 1.2 0 1 0 14.4 10 A1.2 1.2 0 1 0 12 10 M17 6 A1.2 1.2 0 1 0 19.4 6 A1.2 1.2 0 1 0 17 6 M15.5 15 A1.2 1.2 0 1 0 17.9 15 A1.2 1.2 0 1 0 15.5 15"],
        [CommandIconKind.Regression] = ["M4 3 V20 H21 M7 17 L20 5", "M7 11 A1 1 0 1 0 9 11 A1 1 0 1 0 7 11 M12 14 A1 1 0 1 0 14 14 A1 1 0 1 0 12 14 M17 8 A1 1 0 1 0 19 8 A1 1 0 1 0 17 8"],
        [CommandIconKind.Frontier] = ["M4 3 V20 H21", "M6 12 L10 6 16 4 21 4 M8 16 L8.1 16 M12 12 L12.1 12 M16 15 L16.1 15 M19 10 L19.1 10"],
        [CommandIconKind.Increments] = ["M4 3 V20 H21 M6 17 C10 17 12 7 20 6", "M6 7 C13 6 16 11 20 17 M14 11 V15"],
        [CommandIconKind.Identity] = ["M7 3 H4 V21 H7 M17 3 H20 V21 H17", "M8 9 H16 M8 15 H16"],
        [CommandIconKind.Statistics] = ["M3 20 H21 M5 17 V20 M9 11 V20 M13 6 V20 M17 14 V20", "M3 16 C7 16 7 4 12 4 C17 4 17 16 21 16"],
        [CommandIconKind.Merge] = ["M3 4 H8 V9 H3 Z M3 15 H8 V20 H3 Z M17 9 H22 V16 H17 Z", "M10 6.5 H12 V12.5 H16 M10 17.5 H12 V12.5 M13.5 10 L16 12.5 13.5 15"],
        [CommandIconKind.Stratify] = ["M3 3 H21 L14 10 V13 L10 15 V10 Z M4 19 H8 M16 19 H20", "M12 15 V17 M6 17 H18 M6 17 V21 M18 17 V21"],
        [CommandIconKind.Processor] = ["M7 4 H17 Q20 4 20 7 V17 Q20 20 17 20 H7 Q4 20 4 17 V7 Q4 4 7 4 Z M8 1.5 V4 M16 1.5 V4 M8 20 V22.5 M16 20 V22.5 M1.5 8 H4 M1.5 16 H4 M20 8 H22.5 M20 16 H22.5", "M10 8 L16 12 10 16 Z"],
        [CommandIconKind.Rename] = ["M4 17 L16.5 4.5 Q18 3 19.5 4.5 L20.5 5.5 Q22 7 20.5 8.5 L8 21 H3 Z M15 6 L19 10", "M11 21 H21"],
        [CommandIconKind.Remove] = ["M6 8 L7 20 H17 L18 8 M4 5 H20 M9 5 V3 H15 V5", "M10 10 V17 M14 10 V17"],
        [CommandIconKind.Search] = ["M17.5 10 A7 7 0 1 1 3.5 10 A7 7 0 1 1 17.5 10", "M16 16 L21 21"],
        [CommandIconKind.Add] = ["M5 12 H19", "M12 5 V19"],
        [CommandIconKind.CalculateColumn] = ["M3 4 H14 V20 H3 Z M3 9 H14 M8 9 V20 M3 14 H8", "M17 8 H22 M19.5 5.5 V10.5 M16 16 H22 M16 19 H20"],
        [CommandIconKind.CurveComparison] = ["M3 3 V21 H22 M5 17 C10 17 10 5 21 5", "M5 19 C13 19 13 10 21 9 M18 13 V18 M16 15 L18 13 20 15 M16 16 L18 18 20 16"],
        [CommandIconKind.Anomaly] = ["M5 12 A1.5 1.5 0 1 0 8 12 A1.5 1.5 0 1 0 5 12 M8 18 A1.5 1.5 0 1 0 11 18 A1.5 1.5 0 1 0 8 18 M12 13 A1.5 1.5 0 1 0 15 13 A1.5 1.5 0 1 0 12 13", "M15 2 H13 V8 H19 V6 M16 5 A1.5 1.5 0 1 0 19 5 A1.5 1.5 0 1 0 16 5 M19 11 L21 13 M19 18 L21 16"],
        [CommandIconKind.ProcessingReview] = ["M8 4 H5 V21 H19 V4 H16 M9 2 H15 V6 H9 Z M8 10 H15 M8 14 H11", "M12 17 L15 20 21 13"],
        [CommandIconKind.InventoryAudit] = ["M5 3 H16 V8 M5 3 V21 H12 M8 7 H12 M8 11 H11 M8 15 H10", "M21 15 A5 5 0 1 1 11 15 A5 5 0 1 1 21 15 M20 19 L23 22 M14 15 L16 17 19 13"],
        [CommandIconKind.BiologicalAsset] = ["M12 21 V11 M12 14 C4 14 3 8 5 4 C11 4 14 8 12 14 M12 11 C12 5 17 3 21 3 C22 9 19 13 12 11", "M3 20 H7 M5 18 V22 M17 18 H22 M17 21 H22"],
    };
    private static readonly Dictionary<CommandIconKind, Geometry[]> Shapes = Paths.ToDictionary(
        pair => pair.Key,
        pair =>
            pair.Value.Select(path =>
                {
                    var geometry = Geometry.Parse(path);
                    geometry.Freeze();
                    return geometry;
                })
                .ToArray()
    );

    static CommandIcon()
    {
        WidthProperty.OverrideMetadata(typeof(CommandIcon), new FrameworkPropertyMetadata(24d));
        HeightProperty.OverrideMetadata(typeof(CommandIcon), new FrameworkPropertyMetadata(24d));
    }

    public CommandIcon()
    {
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double side = Math.Min(ActualWidth, ActualHeight);
        if (side <= 0) return;
        double scale = side / 24;
        dc.PushTransform(new TranslateTransform((ActualWidth - side) / 2, (ActualHeight - side) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        var shapes = Shapes[Kind];
        for (int i = 0; i < shapes.Length; i++)
        {
            var pen = new Pen(Tint ?? (i == 0 ? Ink : Accents.GetValueOrDefault(Kind, Accent)), Math.Max(1.65, 1.15 / scale))
                { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            dc.DrawGeometry(null, pen, shapes[i]);
        }
        dc.Pop(); dc.Pop();
    }
}
