using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Dendro.Core.Configuration;
using Dendro.Core.Mathematics;
using Dendro.Core.Spreadsheets;

namespace Dendro.Core.Regression;

public static class ChartExport
{
    public static void Excel(string path, ChartResult result, DataTable source)
    {
        var chart = result.Chart;
        var fits = chart["fits"] as JsonArray ?? [];
        int cols = source.Width;
        if (cols + 4 + fits.Count * 2 > 16384)
            throw new InvalidDataException(
                "O Excel excederia 16.384 colunas. Exporte menos modelos por vez."
            );
        var names = Enumerable
            .Range(0, cols)
            .Select(c => source.Text(0, c))
            .Concat(["DENDRO_Linha", "DENDRO_X", "DENDRO_Y", "DENDRO_Estrato"])
            .ToArray();
        var clean = new DataTable(names);
        var removed = new DataTable(names);
        var summary = new DataTable([
            "Estrato",
            "Modelo",
            "Fronteira",
            "Fórmula",
            "N",
            "b0",
            "b1",
            "b2",
            "b3",
            "R2",
            "R2_ajustado",
            "RMSE",
            "AIC",
            "BIC",
            "Status",
        ]);
        var frontier = new DataTable(["Estrato", "Fronteira", "X", "Y"]);
        var info = new DataTable();
        info.Add("Configuração do gráfico e ajuste");
        info.Add(chart.ToJsonString());
        foreach (var point in result.Points)
        {
            var table = point.Excluded ? removed : clean;
            int row = table.Count;
            table.Add(
                Enumerable
                    .Range(0, cols)
                    .Select(c => (object?)source.Text(point.Row, c))
                    .Concat([point.Row + chart.N("header_row", 1), point.X, point.Y, point.Group])
                    .ToArray()
            );
            for (int m = 0; m < fits.Count; m++)
            {
                var fit = fits[m]!;
                if (!fit.B("converged") || fit.S("group") != point.Group)
                    continue;
                clean.Set(0, cols + 4 + m * 2, $"EST_M{m + 1}");
                removed.Set(0, cols + 4 + m * 2, $"EST_M{m + 1}");
                clean.Set(0, cols + 5 + m * 2, $"RES_ABS_M{m + 1}");
                removed.Set(0, cols + 5 + m * 2, $"RES_ABS_M{m + 1}");
                var formula = new Formula(
                    fit.S("formula"),
                    ["b0", "b1", "b2", "b3", "x", "x1", "t"]
                );
                double pred = formula.Evaluate(s => s is "x" or "x1" or "t" ? point.X : fit.N(s));
                table.Set(row, cols + 4 + m * 2, pred);
                table.Set(row, cols + 5 + m * 2, point.Y - pred);
            }
        }
        foreach (var fit in fits)
        {
            summary.Add(
                fit.S("group"),
                fit.S("model"),
                Side((int)fit.N("frontier")),
                fit.S("formula"),
                fit.N("n"),
                fit.B("converged") ? fit.N("b0") : double.NaN,
                fit.B("converged") && fit.N("parameters") > 1 ? fit.N("b1") : double.NaN,
                fit.B("converged") && fit.N("parameters") > 2 ? fit.N("b2") : double.NaN,
                fit.B("converged") && fit.N("parameters") > 3 ? fit.N("b3") : double.NaN,
                fit.N("r2", double.NaN),
                fit.N("adj", double.NaN),
                fit.N("rmse", double.NaN),
                fit.N("aic", double.NaN),
                fit.N("bic", double.NaN),
                fit.S("status")
            );
        }
        foreach (var p in chart["frontier_points"] as JsonArray ?? [])
            frontier.Add(p.S("group"), Side((int)p.N("side")), p.N("x"), p.N("y"));
        ProjectStore.AtomicWrite(
            path,
            temp =>
                WorkbookWriter.Write(
                    temp,
                    [
                        new("Summary", summary),
                        new("Clean_Data", clean),
                        new("Removed_Data", removed),
                        new("Fronteiras", frontier),
                        new("Configuração", info),
                    ]
                ),
            true
        );
    }

    private static string Side(int side) =>
        side == 1 ? "Superior"
        : side == 2 ? "Inferior"
        : "Observações";

    public static void Svg(string path, ChartResult result)
    {
        var c = result.Chart;
        int w = (int)c.N("width", 1600),
            h = (int)c.N("height", 1000);
        if (w is < 400 or > 8000 || h is < 300 or > 8000)
            throw new InvalidDataException("Dimensões de exportação inválidas.");
        XNamespace ns = "http://www.w3.org/2000/svg";
        var root = new XElement(
            ns + "svg",
            new XAttribute("width", w),
            new XAttribute("height", h),
            new XAttribute("viewBox", $"0 0 {w} {h}"),
            new XElement(
                ns + "rect",
                new XAttribute("width", "100%"),
                new XAttribute("height", "100%"),
                new XAttribute("fill", "white")
            )
        );
        var group = new XElement(
            ns + "g",
            new XAttribute("font-family", "Segoe UI, sans-serif"),
            new XAttribute("font-size", 14),
            new XAttribute("fill", "#223344")
        );
        root.Add(group);
        string F(double n) => n.ToString("0.###", CultureInfo.InvariantCulture);
        void Text(double x, double y, string text, string anchor = "middle") =>
            group.Add(
                new XElement(
                    ns + "text",
                    new XAttribute("x", F(x)),
                    new XAttribute("y", F(y)),
                    new XAttribute("text-anchor", anchor),
                    text
                )
            );
        double Transform(double v, bool x) => c.B(x ? "log_x" : "log_y") ? Math.Log10(v) : v;
        var points = result.Points.Where(p => !p.Excluded || c.B("show_removed")).ToArray();
        var bounds = ChartBounds.Calculate(points.Select(p => (p.X, p.Y)), c);
        double xmin = bounds.XMin,
            xmax = bounds.XMax,
            ymin = bounds.YMin,
            ymax = bounds.YMax;
        double left = 86,
            right = w - 40,
            top = 72,
            bottom = h - 70;
        double X(double x) => left + (Transform(x, true) - xmin) / (xmax - xmin) * (right - left);
        double Y(double y) =>
            bottom - (Transform(y, false) - ymin) / (ymax - ymin) * (bottom - top);
        Text(w / 2d, 32, c.S("title"));
        Text((left + right) / 2, h - 18, c.S("x_label", c.S("x")));
        Text(15, 58, c.S("y_label", c.S("y")), "start");
        if(c.S("stratum").Length>0)
        {
            var labels=points.Select(p=>p.Group).Distinct().Order(StringComparer.Ordinal).ToArray();
            double legendX=left;
            foreach(string label in labels)
            {
                if(legendX+120>right)break;
                string hex=c["group_colors"].S(label,ChartPalette.At(Array.IndexOf(labels,label)));
                group.Add(new XElement(ns+"circle",new XAttribute("cx",F(legendX)),new XAttribute("cy","51"),new XAttribute("r","4"),new XAttribute("fill",hex)));
                Text(legendX+9,56,label,"start");legendX+=Math.Max(95,label.Length*8+25);
            }
        }
        foreach(var tick in ChartTicks.Create(xmin,xmax,c.N("x_tick_step"),c.B("log_x")))
        {
            double coordinate=left+(tick.Position-xmin)/(xmax-xmin)*(right-left);
            if(c.B("grid",true))group.Add(new XElement(ns+"path",new XAttribute("d",$"M {F(coordinate)} {F(top)} V {F(bottom)}"),new XAttribute("stroke","#e5ebf1")));
            Text(coordinate,bottom+25,tick.Label);
        }
        foreach(var tick in ChartTicks.Create(ymin,ymax,c.N("y_tick_step"),c.B("log_y")))
        {
            double coordinate=bottom-(tick.Position-ymin)/(ymax-ymin)*(bottom-top);
            if(c.B("grid",true))group.Add(new XElement(ns+"path",new XAttribute("d",$"M {F(left)} {F(coordinate)} H {F(right)}"),new XAttribute("stroke","#e5ebf1")));
            Text(left-10,coordinate+5,tick.Label,"end");
        }
        group.Add(
            new XElement(
                ns + "path",
                new XAttribute("d", $"M {left} {top} V {bottom} H {right}"),
                new XAttribute("stroke", "#808f9c"),
                new XAttribute("fill", "none")
            )
        );
        string[] colors = ChartPalette.Colors;
        var groups = points.Select(p => p.Group).Distinct().Order(StringComparer.Ordinal).ToArray();
        foreach (var reference in c["reference_lines"] as JsonArray ?? [])
        {
            double coordinate = X(reference.N("x"));
            if (!double.IsFinite(coordinate) || coordinate < left || coordinate > right)
                continue;
            group.Add(
                new XElement(
                    ns + "path",
                    new XAttribute("d", $"M {F(coordinate)} {F(top)} V {F(bottom)}"),
                    new XAttribute("stroke", "#718092"),
                    new XAttribute("stroke-dasharray", "2 3"),
                    new XAttribute("fill", "none")
                )
            );
            Text(coordinate + 4, top + 15, reference.S("label"), "start");
        }
        foreach (var p in points)
        {
            double x = X(p.X),
                y = Y(p.Y);
            if (x < left || x > right || y < top || y > bottom)
                continue;
            if (p.Excluded)
            {
                group.Add(
                    new XElement(
                        ns + "path",
                        new XAttribute(
                            "d",
                            $"M {F(x - 4)} {F(y - 4)} L {F(x + 4)} {F(y + 4)} M {F(x - 4)} {F(y + 4)} L {F(x + 4)} {F(y - 4)}"
                        ),
                        new XAttribute("stroke", "#b8bfc7"),
                        new XAttribute("fill", "none")
                    )
                );
                continue;
            }
            group.Add(
                new XElement(
                    ns + "circle",
                    new XAttribute("cx", F(x)),
                    new XAttribute("cy", F(y)),
                    new XAttribute("r", c.N("point_size", 3)),
                    new XAttribute(
                        "fill",
                        p.Excluded
                            ? "#b8bfc7"
                            : c["group_colors"].S(p.Group, colors[Array.IndexOf(groups, p.Group) % colors.Length])
                    ),
                    new XAttribute("opacity", Math.Clamp(c.N("point_alpha", 1), 0, 1))
                )
            );
        }
        var fits = c["fits"] as JsonArray ?? [];
        foreach (int i in FitDisplay.Visible(c).Where(i => i < result.Curves.Length))
        {
            var f = fits[i]!;
            string d = "";
            bool connected = false;
            foreach (var p in result.Curves[i])
            {
                double x = X(p[0]),
                    y = Y(p[1]);
                if (
                    !double.IsFinite(x)
                    || !double.IsFinite(y)
                    || x < left
                    || x > right
                    || y < top
                    || y > bottom
                )
                {
                    connected = false;
                    continue;
                }
                d += (connected ? "L" : "M") + F(x) + "," + F(y) + " ";
                connected = true;
            }
            group.Add(
                new XElement(
                    ns + "path",
                    new XAttribute("d", d),
                    new XAttribute("stroke", FitDisplay.Color(f)),
                    new XAttribute("stroke-width", 2),
                    new XAttribute(
                        "stroke-dasharray",
                        f.N("frontier") == 2 || f.B("line_dashed") ? "6 3" : "none"
                    ),
                    new XAttribute("fill", "none")
                )
            );
        }
        foreach (var p in c["frontier_points"] as JsonArray ?? [])
            group.Add(
                new XElement(
                    ns + "rect",
                    new XAttribute("x", F(X(p.N("x")) - 4)),
                    new XAttribute("y", F(Y(p.N("y")) - 4)),
                    new XAttribute("width", 8),
                    new XAttribute("height", 8),
                    new XAttribute("fill", "none"),
                    new XAttribute("stroke", p.N("side") == 1 ? colors[1] : colors[2])
                )
            );
        ProjectStore.AtomicWrite(path, temp => new XDocument(root).Save(temp), true);
    }
}
