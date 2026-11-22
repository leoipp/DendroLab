using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Controls;

namespace Dendro.Desktop;

internal static class CursorSnapPreferences
{
    private static readonly string PathName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DENDRO", "cursor-snap.json");
    internal static JsonObject Current { get; private set; } = Load();
    private static JsonObject Load()
    {
        try { return JsonNode.Parse(File.ReadAllText(PathName))!.AsObject(); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { return new(); }
    }
    internal static void Save(JsonObject settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        Dendro.Core.Configuration.ProjectStore.AtomicWrite(PathName, temp => File.WriteAllText(temp, settings.ToJsonString()), true);
        Current = settings;
    }

    internal sealed class Editor : StackPanel
    {
        private readonly CheckBox enabled = new() { Content = "Habilitar snap dos rótulos do cursor" };
        private readonly TextBox x = Ui.Input(), y = Ui.Input();
        private readonly Slider tolerance = new() { Minimum = 0, Maximum = 100, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new System.Windows.Thickness(0, 8, 0, 8) };
        private readonly Button color = Ui.Button("Selecionar cor…", () => { });
        private string selectedColor = "#239B56";
        internal Editor()
        {
            Margin = new System.Windows.Thickness(18);
            enabled.IsChecked = Current.B("cursor_snap");
            x.Text = Current["cursor_snap_x"]?.Display() ?? "";
            y.Text = Current["cursor_snap_y"]?.Display() ?? "";
            tolerance.Value = Current.N("cursor_snap_tolerance", 10);
            selectedColor = Current.S("cursor_snap_color", "#239B56");
            void UpdateColor() => color.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(selectedColor));
            UpdateColor();
            color.Click += (_, _) =>
            {
                var chosen = ColorWheelPicker.Pick(selectedColor);
                if (chosen != null) { selectedColor = chosen; UpdateColor(); }
            };
            Children.Add(enabled);
            Ui.Field(this, "Valor de X (vazio = sem snap)", x);
            Ui.Field(this, "Valor de Y (vazio = sem snap)", y);
            var sensitivity = new StackPanel();
            sensitivity.Children.Add(tolerance);
            var value = Ui.Text("");
            void UpdateValue() => value.Text = $"{tolerance.Value:0} pixels";
            tolerance.ValueChanged += (_, _) => UpdateValue(); UpdateValue();
            sensitivity.Children.Add(value);
            Ui.Field(this, "Sensibilidade (0 a 100)", sensitivity);
            Ui.Field(this, "Cor da bolha encaixada", color);
            Ui.AddHelp(this, "Preferência global deste computador, aplicada a todos os gráficos, inclusive os já abertos.");
        }
        internal JsonObject Read()
        {
            var result = new JsonObject { ["cursor_snap"] = enabled.IsChecked == true };
            foreach (var pair in new[] { ("cursor_snap_x", x), ("cursor_snap_y", y) })
                if (!string.IsNullOrWhiteSpace(pair.Item2.Text))
                {
                    double value = Ui.Number(pair.Item2.Text);
                    if (!double.IsFinite(value)) throw new InvalidOperationException("Informe um valor numérico válido para o snap.");
                    result[pair.Item1] = value;
                }
            double pixels = tolerance.Value;
            result["cursor_snap_tolerance"] = pixels;
            result["cursor_snap_color"] = selectedColor;
            return result;
        }
    }
}
