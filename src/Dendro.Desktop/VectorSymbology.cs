using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Dendro.Desktop;

internal sealed record VectorCategory(string Value, string Color, bool Visible = true);

internal sealed record VectorSymbology(string FillColor = "#DC3946", string LineColor = "#DC3946",
    double FillOpacity = 0.18, double LineWidth = 1.2, string Pattern = "Sólido",
    string LineStyle = "Contínuo", double PointSize = 8, double HatchSpacing = 8, bool SamplingColors = true,
    bool FillEnabled = true, bool LineEnabled = true, double LineOpacity = 1,
    string CategoryField = "", List<VectorCategory>? Categories = null, bool ShowOtherValues = true)
{
    internal static VectorSymbology Default(int index)
    {
        string[] colors = ["#DC3946", "#1A7DB9", "#1E9161", "#8C4EAC", "#E28424"];
        return new(colors[index % colors.Length], colors[index % colors.Length]);
    }

    internal static VectorSymbology Load(ProjectSession session, string path)
    {
        int index = Array.FindIndex(session.Files.Where(p => System.IO.Path.GetExtension(p).Equals(".shp", StringComparison.OrdinalIgnoreCase)).ToArray(), p => p == path);
        var fallback = Default(Math.Max(0, index));
        try
        {
            var style = session.Meta.Obj("gis_symbology")[path]?.Deserialize<VectorSymbology>();
            if (style == null) return fallback;
            style.Validate();
            return style;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or InvalidOperationException) { return fallback; }
    }

    internal void Validate()
    {
        _ = ColorConverter.ConvertFromString(FillColor);
        _ = ColorConverter.ConvertFromString(LineColor);
        if (Categories != null)
        {
            if (Categories.Count > 500 || Categories.Select(c => c.Value).Distinct(StringComparer.Ordinal).Count() != Categories.Count)
                throw new InvalidOperationException("A classificação deve ter até 500 valores distintos.");
            foreach (var category in Categories) _ = ColorConverter.ConvertFromString(category.Color);
        }
        if (!double.IsFinite(LineOpacity) || LineOpacity is < 0 or > 1)
            throw new InvalidOperationException("Transparência do contorno inválida.");
        if (!double.IsFinite(FillOpacity) || FillOpacity is < 0 or > 1
            || !double.IsFinite(LineWidth) || LineWidth is < 0 or > 12
            || !double.IsFinite(PointSize) || PointSize is < 2 or > 40
            || !double.IsFinite(HatchSpacing) || HatchSpacing is < 3 or > 40)
            throw new InvalidOperationException("Confira opacidade (0–100%), contorno (0–12), ponto (2–40) e espaçamento (3–40).");
    }

    internal Pen? Pen(double scale)
    {
        if (!LineEnabled || LineWidth == 0) return null;
        var pen = new Pen(new SolidColorBrush((Color)ColorConverter.ConvertFromString(LineColor)) { Opacity = LineOpacity }, LineWidth / scale);
        pen.DashStyle = LineStyle switch { "Tracejado" => DashStyles.Dash, "Pontilhado" => DashStyles.Dot, _ => DashStyles.Solid };
        pen.Freeze();
        return pen;
    }

    internal Brush? Fill(double scale)
    {
        if (!FillEnabled || Pattern == "Sem preenchimento") return null;
        var color = (Color)ColorConverter.ConvertFromString(FillColor);
        var fill = new SolidColorBrush(color) { Opacity = FillOpacity };
        if (Pattern == "Sólido") { fill.Freeze(); return fill; }
        double size = HatchSpacing / scale;
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(fill, null, new RectangleGeometry(new Rect(0, 0, size, size))));
        var pen = new Pen(new SolidColorBrush(color) { Opacity = FillOpacity }, 1 / scale);
        void Line(double x1, double y1, double x2, double y2) => group.Children.Add(new GeometryDrawing(null, pen,
            new LineGeometry(new Point(x1, y1), new Point(x2, y2))));
        if (Pattern is "Diagonal" or "Cruzada") Line(0, 0, size, size);
        if (Pattern == "Cruzada") Line(0, size, size, 0);
        if (Pattern == "Horizontal") Line(0, size / 2, size, size / 2);
        if (Pattern == "Vertical") Line(size / 2, 0, size / 2, size);
        var brush = new DrawingBrush(group) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, size, size), Stretch = Stretch.Fill };
        brush.Freeze();
        return brush;
    }

    internal static string?[] ReadCategoryKeys(string shapefile, string field)
    {
        var table = DbfTable.Read(System.IO.Path.ChangeExtension(shapefile, ".dbf"));
        int column = table.Fields.FindIndex(f => f.Name.Equals(field, StringComparison.OrdinalIgnoreCase));
        if (column < 0) throw new InvalidOperationException($"O campo de simbologia '{field}' não existe no DBF.");
        return table.Rows.Select(row => row[0] == (byte)'*' ? null : table.ReadValue(row, column)).ToArray();
    }

    internal VectorSymbology? ResolveCategory(string? key, bool lineGeometry = false)
    {
        if (CategoryField.Length == 0) return this;
        var category = Categories?.FirstOrDefault(c => string.Equals(c.Value, key, StringComparison.Ordinal));
        if (category == null) return ShowOtherValues ? this with { SamplingColors = false } : null;
        if (!category.Visible) return null;
        return this with { FillColor = category.Color, FillOpacity = 1,
            LineColor = lineGeometry ? category.Color : LineColor, LineOpacity = lineGeometry ? 1 : LineOpacity, SamplingColors = false };
    }

    internal static bool Edit(Window owner, ProjectSession session, string path) => new VectorSymbologyEditor(owner, session, path).ShowDialog() == true;
}
