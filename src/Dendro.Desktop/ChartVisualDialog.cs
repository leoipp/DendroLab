using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Dendro.Desktop;

internal static class ChartVisualDialog
{
    internal static JsonObject? Show(JsonObject original, JsonObject data)
    {
        var draft = original.DeepClone().AsObject();
        var readers = new List<Action>();
        var preview = new ScatterPlot { Data = data, Config = draft.DeepClone().AsObject() };
        var legend = new ContentControl();
        var previewPanel = new DockPanel { Background = Brushes.White };
        DockPanel.SetDock(legend, Dock.Top); previewPanel.Children.Add(legend); previewPanel.Children.Add(preview);
        void RefreshPreview()
        {
            preview.Config = draft.DeepClone().AsObject();
            legend.Content = PlotLegend.Create(draft, data);
            legend.Visibility = draft.B("visual_legend", true) ? Visibility.Visible : Visibility.Collapsed;
        }
        var error = Ui.Text(""); error.Foreground = Brushes.Firebrick;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        void Changed() { timer.Stop(); timer.Start(); }
        void Read()
        {
            foreach (var read in readers) read();
            foreach (string axis in new[] { "x", "y" })
            {
                if (draft[axis + "min"] != null && draft[axis + "max"] != null && draft.N(axis + "max") <= draft.N(axis + "min"))
                    throw new InvalidOperationException("O máximo do eixo deve ser maior que o mínimo.");
                if (draft.B("log_" + axis) && new[] { axis + "min", axis + "max" }.Any(k => draft[k] != null && draft.N(k) <= 0))
                    throw new InvalidOperationException("Limites logarítmicos precisam ser positivos.");
            }
        }
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try { Read(); RefreshPreview(); error.Text = ""; }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        var tabs = new TabControl();
        StackPanel Tab(string label)
        {
            var form = Ui.Form(); tabs.Items.Add(new TabItem { Header = label, Content = Ui.Scroll(form) }); return form;
        }
        void Text(Panel panel, string label, string key, string fallback = "")
        {
            var input = Ui.Input(draft.S(key, fallback)); Ui.Field(panel, label, input);
            input.TextChanged += (_, _) => Changed(); readers.Add(() => draft[key] = input.Text);
        }
        void Number(Panel panel, string label, string key, double fallback, double min, double max, bool optional = false)
        {
            var input = Ui.Input(optional && draft[key] == null ? "" : draft.N(key, fallback).ToString(System.Globalization.CultureInfo.CurrentCulture));
            Ui.Field(panel, label, input); input.TextChanged += (_, _) => Changed();
            readers.Add(() =>
            {
                if (optional && string.IsNullOrWhiteSpace(input.Text)) { draft.Remove(key); return; }
                double value = Ui.Number(input.Text);
                if (!double.IsFinite(value) || value < min || value > max) throw new InvalidOperationException($"{label}: use valores de {min} a {max}.");
                draft[key] = value;
            });
        }
        void Range(Panel panel, string label, string key, double fallback, double min, double max, double step = 1, bool percent = false)
        {
            double scale = percent ? 100 : 1;
            var slider = new Slider
            {
                Minimum = min * scale, Maximum = max * scale, Value = draft.N(key, fallback) * scale,
                TickFrequency = step * scale, IsSnapToTickEnabled = true, SmallChange = step * scale,
                LargeChange = step * scale * 5, AutoToolTipPrecision = percent || step >= 1 ? 0 : 2,
            };
            var row = new DockPanel();
            var value = Ui.Text(""); value.Width = 55; value.TextAlignment = TextAlignment.Right;
            value.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(value, Dock.Right); row.Children.Add(value); row.Children.Add(slider);
            void Update() => value.Text = slider.Value.ToString(step >= 1 || percent ? "0" : "0.##", System.Globalization.CultureInfo.CurrentCulture) + (percent ? "%" : "");
            slider.ValueChanged += (_, _) => { Update(); Changed(); }; Update();
            Ui.Field(panel, label, row);
            readers.Add(() => draft[key] = slider.Value / scale);
        }
        StackPanel[] Columns(Panel parent, int count)
        {
            var row = new Grid(); var columns = new StackPanel[count];
            for (int i = 0; i < count; i++)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition());
                columns[i] = new StackPanel { Margin = new Thickness(i == 0 ? 0 : 5, 0, i == count - 1 ? 0 : 5, 0) };
                Grid.SetColumn(columns[i], i); row.Children.Add(columns[i]);
            }
            parent.Children.Add(row); return columns;
        }
        void Toggle(Panel panel, string label, string key, bool fallback)
        {
            var check = new CheckBox { Content = label, IsChecked = draft.B(key, fallback), Margin = new Thickness(0, 5, 0, 5) };
            panel.Children.Add(check); check.Click += (_, _) => Changed(); readers.Add(() => draft[key] = check.IsChecked == true);
        }
        void Color(Panel panel, string label, JsonObject target, string key, string fallback)
        {
            string selected = target.S(key, fallback);
            var button = Ui.Button("Cor…", () => { });
            button.MinWidth = 0; button.Padding = new Thickness(8, 4, 8, 4); button.HorizontalAlignment = HorizontalAlignment.Stretch;
            void Paint()
            {
                var c = (System.Windows.Media.Color)ColorConverter.ConvertFromString(selected);
                button.Background = new SolidColorBrush(c);
                button.Foreground = .2126*c.R + .7152*c.G + .0722*c.B > 150 ? Brushes.Black : Brushes.White;
            }
            Paint(); Ui.Field(panel, label, button);
            button.Click += (_, _) => { var chosen = ColorWheelPicker.Pick(selected); if (chosen != null) { selected = chosen; Paint(); target[key] = chosen; Changed(); } };
        }
        var labels = Tab("Texto");
        Text(labels, "Título", "title"); Text(labels, "Rótulo X", "x_label", draft.S("x")); Text(labels, "Rótulo Y", "y_label", draft.S("y"));
        var font = Ui.Combo(Fonts.SystemFontFamilies.Select(f => f.Source).Append(draft.S("visual_font", "Segoe UI")).Distinct().Order(StringComparer.CurrentCultureIgnoreCase), draft.S("visual_font", "Segoe UI"));
        Ui.Field(labels, "Fonte", font); font.SelectionChanged += (_, _) => Changed();
        readers.Add(() => draft["visual_font"] = font.SelectedItem?.ToString() ?? "Segoe UI");
        Range(labels, "Tamanho do título", "visual_title_size", 19, 8, 40);
        Range(labels, "Tamanho dos rótulos", "visual_label_size", 12, 8, 26);
        Range(labels, "Tamanho das marcações", "visual_tick_size", 12, 7, 22);
        Toggle(labels, "Mostrar legenda", "visual_legend", true);
        Color(labels, "Texto", draft, "visual_text_color", "#526579");
        var appearance = Tab("Aparência");
        var colorRow = Columns(appearance, 3);
        Color(colorRow[0], "Fundo", draft, "visual_background", "#FFFFFF");
        Color(colorRow[1], "Eixos", draft, "visual_axis_color", "#708090");
        Color(colorRow[2], "Grade", draft, "visual_grid_color", "#E5EBF3");
        Toggle(appearance, "Mostrar grade", "grid", true);
        Range(appearance, "Espessura da grade", "visual_grid_width", 1, .25, 4, .25);
        Range(appearance, "Espessura dos eixos", "visual_axis_width", 1, .25, 5, .25);
        Toggle(appearance, "Mostrar cursor e rótulos X/Y", "visual_cursor", true);
        Ui.Section(appearance, "Espaço interno (pixels)");
        var margins = Columns(appearance, 2);
        Range(margins[0], "Esquerda", "visual_margin_left", 110, 50, 200);
        Range(margins[1], "Direita", "visual_margin_right", 30, 10, 150);
        var verticalMargins = Columns(appearance, 2);
        Range(verticalMargins[0], "Superior", "visual_margin_top", 57, 40, 150);
        Range(verticalMargins[1], "Inferior", "visual_margin_bottom", 60, 45, 150);
        var axes = Tab("Eixos");
        foreach (string axis in new[] { "x", "y" })
        {
            Ui.Section(axes, "Eixo " + axis.ToUpperInvariant());
            Number(axes, "Mínimo (vazio = automático)", axis + "min", 0, -1e100, 1e100, true);
            Number(axes, "Máximo (vazio = automático)", axis + "max", 1, -1e100, 1e100, true);
            Number(axes, "Intervalo das marcações (vazio = automático)", axis + "_tick_step", 1, 1e-10, 1e100, true);
            Toggle(axes, "Escala logarítmica", "log_" + axis, false);
        }
        var points = Tab("Pontos");
        Range(points, "Tamanho dos pontos", "point_size", 3, 0, 12, .5);
        Range(points, "Opacidade", "point_alpha", 1, 0, 1, .01, true);
        var shape = Ui.Combo(["Círculo", "Quadrado", "Losango"]); shape.SelectedIndex = (int)draft.N("visual_point_shape");
        Ui.Field(points, "Símbolo", shape); shape.SelectionChanged += (_, _) => Changed(); readers.Add(() => draft["visual_point_shape"] = shape.SelectedIndex);
        Toggle(points, "Mostrar pontos removidos", "show_removed", false);
        var colors = draft.Obj("group_colors");
        foreach (var group in Dendro.Core.Regression.ChartPointView.Open(data, draft).Points().Select(p => p.Group).Distinct().Order(StringComparer.Ordinal))
            Color(points, group.Length > 0 ? group : "Pontos", colors, group,
                Dendro.Core.Regression.ChartPalette.Colors[draft.S("stratum").Length > 0 ? ScatterPlot.GroupColor(group) : Math.Abs((int)draft.N("color")) % 5]);
        var lines = Tab("Curvas");
        Range(lines, "Espessura das curvas", "visual_curve_width", 2.2, .5, 8, .1);
        foreach (var fit in draft.Arr("fits").OfType<JsonObject>())
        {
            Ui.Section(lines, fit.S("group") + " · " + fit.S("model"));
            Color(lines, "Cor", fit, "curve_color", Dendro.Core.Regression.FitDisplay.Color(fit));
            var dashed = new CheckBox { Content = "Linha tracejada", IsChecked = fit.B("line_dashed") };
            lines.Children.Add(dashed); dashed.Click += (_, _) => { fit["line_dashed"] = dashed.IsChecked == true; Changed(); };
        }
        RefreshPreview();
        var layout = new Grid { Background = Brushes.White }; layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(370) }); layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.Children.Add(tabs); Grid.SetColumn(previewPanel, 1); layout.Children.Add(previewPanel);
        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10) };
        var body = new DockPanel { Background = Brushes.White }; DockPanel.SetDock(footer, Dock.Bottom); body.Children.Add(footer);
        DockPanel.SetDock(error, Dock.Bottom); body.Children.Add(error); body.Children.Add(layout);
        var window = Ui.Dialog("Configurações visuais do gráfico", body, 1180, 760);
        footer.Children.Add(Ui.Button("Cancelar", () => window.DialogResult = false));
        footer.Children.Add(Ui.Button("Aplicar", () => { try { Read(); window.DialogResult = true; } catch (Exception ex) { error.Text = ex.Message; } }));
        bool? accepted = window.ShowDialog(); timer.Stop();
        if (draft.S("title") != original.S("title")) draft.Remove("title_pattern");
        return accepted == true ? draft : null;
    }
}
