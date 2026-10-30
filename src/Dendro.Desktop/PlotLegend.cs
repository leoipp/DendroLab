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
            foreach(string group in (data["points"] as JsonArray??[]).Where(p=>!p.B("excluded")||config.B("show_removed")).Select(p=>p.S("group")).Distinct().Order(StringComparer.Ordinal))
            {
                string hex=config["group_colors"].S(group,ChartPalette.Colors[ScatterPlot.GroupColor(group)]);
                var entry=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,14,3)};
                entry.Children.Add(new System.Windows.Shapes.Ellipse {Width=9,Height=9,Margin=new Thickness(0,0,6,0),VerticalAlignment=VerticalAlignment.Center,Fill=(Brush)new BrushConverter().ConvertFromString(hex)!});
                entry.Children.Add(Ui.Text(group,12));panel.Children.Add(entry);
            }
        return new ScrollViewer {Content=panel,MaxHeight=90,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    }
}
