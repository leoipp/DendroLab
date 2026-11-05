using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dendro.Core.Regression;

namespace Dendro.Desktop;

internal static class PlotLegend
{
    internal static FrameworkElement Create(JsonObject config,JsonObject data)
    {
        var panel=new WrapPanel { Margin=new Thickness(10,3,10,3) };
        if(config.S("stratum").Length>0)
            foreach(string group in (ChartPointView.IsPacked(data)
                ? (data[config.B("show_removed") ? "point_groups" : "point_groups_visible"] as JsonArray ?? []).Select(value=>value?.GetValue<string>()??"")
                : ChartPointView.Open(data,config).Points().Where(point=>!point.Excluded||config.B("show_removed")).Select(point=>point.Group).Distinct().Order(StringComparer.Ordinal)))
            {
                string hex=config["group_colors"].S(group,ChartPalette.Colors[ScatterPlot.GroupColor(group)]);
                var entry=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,14,3)};
                entry.Children.Add(new System.Windows.Shapes.Ellipse {Width=9,Height=9,Margin=new Thickness(0,0,6,0),VerticalAlignment=VerticalAlignment.Center,Fill=(Brush)new BrushConverter().ConvertFromString(hex)!});
                entry.Children.Add(Ui.Text(group,12));panel.Children.Add(entry);
            }
        foreach (var reference in data["reference_curves"] as JsonArray ?? [])
        {
            var entry = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 3) };
            entry.Children.Add(new System.Windows.Shapes.Line
            {
                Width = 22, Height = 12,
                X1 = 0, Y1 = 6, X2 = 22, Y2 = 6,
                Stroke = (Brush)new BrushConverter().ConvertFromString(reference!.S("color", "#8E44AD"))!,
                StrokeThickness = 2.8,
                StrokeDashArray = [4, 2],
                VerticalAlignment = VerticalAlignment.Center,
            });
            entry.Children.Add(Ui.Text(reference.S("label"), 12));
            panel.Children.Add(entry);
        }
        return new ScrollViewer {Content=panel,MaxHeight=90,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    }
}
