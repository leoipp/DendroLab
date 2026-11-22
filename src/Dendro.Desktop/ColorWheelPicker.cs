using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Dendro.Desktop;

internal static class ColorWheelPicker
{
    internal static string? Pick(string initial)
    {
        var current = (Color)ColorConverter.ConvertFromString(initial);
        double max = Math.Max(current.R, Math.Max(current.G, current.B)) / 255d;
        double min = Math.Min(current.R, Math.Min(current.G, current.B)) / 255d;
        double delta = max - min;
        double hue = delta == 0 ? 0 : max * 255 == current.R ? ((current.G - current.B) / (255 * delta) + 6) % 6
            : max * 255 == current.G ? (current.B - current.R) / (255 * delta) + 2 : (current.R - current.G) / (255 * delta) + 4;
        double saturation = max == 0 ? 0 : delta / max;
        var brightness = new Slider { Minimum = 0, Maximum = 1, Value = max, Margin = new Thickness(0, 8, 0, 8) };
        var preview = new Border { Height = 34, CornerRadius = new CornerRadius(5) };
        var wheel = new Image { Width = 260, Height = 260, Cursor = Cursors.Cross };
        var canvas = new Canvas { Width = 260, Height = 260 };
        canvas.Children.Add(wheel);
        var marker = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Stroke = Brushes.Black, StrokeThickness = 2, Fill = Brushes.White, IsHitTestVisible = false };
        canvas.Children.Add(marker);
        Color Hsv(double h, double s, double v)
        {
            double c = v * s, x = c * (1 - Math.Abs(h % 2 - 1)), m = v - c;
            var (r, g, b) = h switch { < 1 => (c, x, 0d), < 2 => (x, c, 0d), < 3 => (0d, c, x), < 4 => (0d, x, c), < 5 => (x, 0d, c), _ => (c, 0d, x) };
            return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }
        void Update()
        {
            current = Hsv(hue, saturation, brightness.Value);
            preview.Background = new SolidColorBrush(current);
            Canvas.SetLeft(marker, 130 + Math.Cos(hue * Math.PI / 3) * saturation * 129 - 5);
            Canvas.SetTop(marker, 130 - Math.Sin(hue * Math.PI / 3) * saturation * 129 - 5);
        }
        var pixels = new byte[260 * 260 * 4];
        for (int y = 0; y < 260; y++) for (int x = 0; x < 260; x++)
        {
            double dx = x - 130, dy = 130 - y, s = Math.Sqrt(dx * dx + dy * dy) / 129;
            if (s > 1) continue;
            var color = Hsv((Math.Atan2(dy, dx) * 3 / Math.PI + 6) % 6, s, 1);
            int i = (y * 260 + x) * 4; pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255;
        }
        wheel.Source = BitmapSource.Create(260, 260, 96, 96, PixelFormats.Bgra32, null, pixels, 260 * 4);
        void Select(MouseEventArgs e)
        {
            var p = e.GetPosition(wheel); double dx = p.X - 130, dy = 130 - p.Y;
            hue = (Math.Atan2(dy, dx) * 3 / Math.PI + 6) % 6;
            saturation = Math.Min(1, Math.Sqrt(dx * dx + dy * dy) / 129); Update();
        }
        wheel.MouseLeftButtonDown += (_, e) => { wheel.CaptureMouse(); Select(e); };
        wheel.MouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed && wheel.IsMouseCaptured) Select(e); };
        wheel.MouseLeftButtonUp += (_, _) => wheel.ReleaseMouseCapture();
        brightness.ValueChanged += (_, _) => Update(); Update();
        var form = Ui.Form(); form.Children.Add(canvas);
        Ui.Field(form, "Luminosidade", brightness); form.Children.Add(preview);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var body = new DockPanel(); DockPanel.SetDock(actions, Dock.Bottom); body.Children.Add(actions); body.Children.Add(form);
        var dialog = Ui.Dialog("Cor da bolha", body, 480, 480);
        actions.Children.Add(Ui.Button("Cancelar", () => dialog.DialogResult = false));
        actions.Children.Add(Ui.Button("Selecionar", () => dialog.DialogResult = true));
        return dialog.ShowDialog() == true ? $"#{current.R:X2}{current.G:X2}{current.B:X2}" : null;
    }
}
