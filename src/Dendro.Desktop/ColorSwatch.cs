using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Dendro.Desktop;

internal sealed class ColorSwatch : Button
{
    internal Color SelectedColor { get; private set; }
    internal event Action? ColorChanged;
    private readonly bool compact;
    internal ColorSwatch(Color color, bool compact = false)
    {
        this.compact = compact;
        Style = (Style)Application.Current.FindResource(typeof(Button));
        HorizontalContentAlignment = HorizontalAlignment.Left;
        SetColor(color);
        Click += (_, _) => EditColor();
    }

    internal void SetColor(Color color)
    {
        SelectedColor = color;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var sample = new Grid { Width = 38, Height = 20, Margin = new Thickness(0, 0, 8, 0), Background = Checker() };
        sample.Children.Add(new Border { Background = new SolidColorBrush(color) });
        row.Children.Add(sample);
        if (!compact) row.Children.Add(new TextBlock { Text = $"#{color.R:X2}{color.G:X2}{color.B:X2} · {100 - color.A * 100 / 255d:0}% transparente", VerticalAlignment = VerticalAlignment.Center });
        Content = row;
        System.Windows.Automation.AutomationProperties.SetName(this, "Selecionar cor e transparência");
        ColorChanged?.Invoke();
    }

    internal static Brush Checker()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 12, 12))));
        group.Children.Add(new GeometryDrawing(Brushes.LightGray, null, new RectangleGeometry(new Rect(0, 0, 6, 6))));
        group.Children.Add(new GeometryDrawing(Brushes.LightGray, null, new RectangleGeometry(new Rect(6, 6, 6, 6))));
        var brush = new DrawingBrush(group) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 12, 12) };
        brush.Freeze(); return brush;
    }

    private void EditColor()
    {
        var wheel = new HsvWheel(); wheel.SetColor(SelectedColor);
        var brightness = new Slider { Minimum = 0, Maximum = 100, Value = wheel.Brightness * 100, TickFrequency = 1 };
        var alpha = new Slider { Minimum = 0, Maximum = 100, Value = 100 - SelectedColor.A * 100 / 255d, TickFrequency = 1 };
        var hex = Ui.Input($"#{SelectedColor.R:X2}{SelectedColor.G:X2}{SelectedColor.B:X2}");
        var sample = new Border { Height = 44, Background = Checker(), Margin = new Thickness(0, 10, 0, 10) };
        var overlay = new Border(); sample.Child = overlay;
        System.Windows.Automation.AutomationProperties.SetAutomationId(hex, "Color.Hex");
        System.Windows.Automation.AutomationProperties.SetAutomationId(alpha, "Color.Transparency");
        var error = Ui.Text(""); error.Foreground = Brushes.Firebrick;
        var form = Ui.Form(); form.Children.Add(wheel); form.Children.Add(sample);
        Ui.Field(form, "Luminosidade", brightness);
        var lightValue = Ui.Text(""); form.Children.Add(lightValue);
        Ui.Field(form, "Transparência (0% = opaco · 100% = transparente)", alpha);
        var alphaValue = Ui.Text(""); form.Children.Add(alphaValue);
        Ui.Field(form, "Cor hexadecimal (#RRGGBB)", hex); form.Children.Add(error);
        var dialog = Ui.Dialog("Selecionar cor", null!, 440, 690);
        dialog.MinWidth = 360; dialog.MaxHeight = SystemParameters.WorkArea.Height * .9;
        dialog.Owner = Window.GetWindow(this);
        bool syncing = false;
        Color Current() { var c = wheel.Rgb; c.A = (byte)Math.Round((100 - alpha.Value) * 255 / 100); return c; }
        void Update(bool setHex = true)
        {
            var c = Current(); overlay.Background = new SolidColorBrush(c);
            lightValue.Text = $"{brightness.Value:0}%"; alphaValue.Text = $"{alpha.Value:0}%";
            if (setHex) { syncing = true; hex.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}"; syncing = false; }
            error.Text = "";
        }
        wheel.Changed += () => Update();
        brightness.ValueChanged += (_, _) => { wheel.Brightness = brightness.Value / 100; Update(); };
        alpha.ValueChanged += (_, _) => Update();
        hex.TextChanged += (_, _) =>
        {
            if (syncing) return;
            if (hex.Text.Length != 7 || hex.Text[0] != '#') { error.Text = "Informe uma cor no formato #RRGGBB."; return; }
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(hex.Text);
                wheel.SetColor(c); brightness.Value = wheel.Brightness * 100; Update(false);
            }
            catch (FormatException) { error.Text = "Cor hexadecimal inválida."; }
        };
        var cancel = Ui.Button("Cancelar", () => dialog.DialogResult = false, Ui.ActionEmphasis.Secondary); cancel.IsCancel = true;
        var apply = Ui.Button("Usar cor", () => { if (error.Text.Length == 0) { SetColor(Current()); dialog.DialogResult = true; } }, Ui.ActionEmphasis.Primary);
        var body = new DockPanel { Background = Brushes.White };
        var footer = Ui.ActionFooter(cancel, apply); DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(footer); body.Children.Add(Ui.Scroll(form)); dialog.Content = body;
        Update(); dialog.ShowDialog();
    }
}

internal sealed class HsvWheel : FrameworkElement
{
    private double hue, saturation, brightness = 1;
    private BitmapSource? bitmap;
    internal event Action? Changed;
    internal double Brightness { get => brightness; set { brightness = Math.Clamp(value, 0, 1); bitmap = null; InvalidateVisual(); } }
    internal Color Rgb => FromHsv(hue, saturation, brightness);
    internal HsvWheel()
    {
        Width = Height = 210; Focusable = true; HorizontalAlignment = HorizontalAlignment.Center;
        Cursor = Cursors.Cross;
        System.Windows.Automation.AutomationProperties.SetName(this, "Roda de cores. Setas esquerda e direita mudam o matiz; acima e abaixo mudam a saturação.");
        MouseLeftButtonDown += (_, e) => { Focus(); CaptureMouse(); Pick(e.GetPosition(this)); };
        MouseMove += (_, e) => { if (IsMouseCaptured) Pick(e.GetPosition(this)); };
        MouseLeftButtonUp += (_, _) => ReleaseMouseCapture();
        KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Left: hue = (hue + 358) % 360; break;
                case Key.Right: hue = (hue + 2) % 360; break;
                case Key.Up: saturation = Math.Min(1, saturation + .02); break;
                case Key.Down: saturation = Math.Max(0, saturation - .02); break;
                default: return;
            }
            e.Handled = true; InvalidateVisual(); Changed?.Invoke();
        };
    }
    internal void SetColor(Color color)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
        hue = delta == 0 ? hue : max == r ? (60 * ((g - b) / delta) + 360) % 360 : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        saturation = max == 0 ? 0 : delta / max; Brightness = max;
    }
    private void Pick(Point point)
    {
        double x = point.X - 105, y = point.Y - 105;
        hue = (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
        saturation = Math.Min(1, Math.Sqrt(x * x + y * y) / 100);
        InvalidateVisual(); Changed?.Invoke();
    }
    protected override void OnRender(DrawingContext dc)
    {
        if (bitmap == null)
        {
            byte[] pixels = new byte[210 * 210 * 4];
            for (int y = 0; y < 210; y++) for (int x = 0; x < 210; x++)
            {
                double dx = x - 105, dy = y - 105, radius = Math.Sqrt(dx * dx + dy * dy);
                if (radius > 100) continue;
                var c = FromHsv((Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360, radius / 100, brightness);
                int i = (y * 210 + x) * 4;
                pixels[i] = c.B; pixels[i + 1] = c.G; pixels[i + 2] = c.R; pixels[i + 3] = 255;
            }
            bitmap = BitmapSource.Create(210, 210, 96, 96, PixelFormats.Bgra32, null, pixels, 840); bitmap.Freeze();
        }
        dc.DrawImage(bitmap, new Rect(0, 0, 210, 210));
        var center = new Point(105 + 100 * saturation * Math.Cos(hue * Math.PI / 180), 105 + 100 * saturation * Math.Sin(hue * Math.PI / 180));
        dc.DrawEllipse(null, new Pen(Brushes.Black, 3), center, 5, 5);
        dc.DrawEllipse(null, new Pen(Brushes.White, 1.5), center, 5, 5);
        if (IsKeyboardFocused) dc.DrawEllipse(null, new Pen(Brushes.SteelBlue, 1), new Point(105, 105), 103, 103);
    }
    internal static Color FromHsv(double hue, double saturation, double value)
    {
        double c = value * saturation, x = c * (1 - Math.Abs(hue / 60 % 2 - 1)), m = value - c;
        (double r, double g, double b) = hue switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
