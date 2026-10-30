using System.Globalization;

namespace Dendro.Core.Regression;

public sealed record AxisTick(double Position, double Value, string Label);
public static class ChartTicks
{
    public static AxisTick[] Create(double min,double max,double interval=0,bool logarithmic=false)
    {
        if(!double.IsFinite(min)||!double.IsFinite(max)||max<=min)return [];
        if(!double.IsFinite(interval)||interval<0)throw new InvalidDataException("O intervalo do eixo deve ser positivo ou zero para automático.");
        double step=interval;
        if(step==0)
        {
            double raw=(max-min)/5,power=Math.Pow(10,Math.Floor(Math.Log10(raw))),ratio=raw/power;
            step=(ratio<=1?1:ratio<=2?2:ratio<=5?5:10)*power;
            if(max-min>=1)step=Math.Max(1,step);
        }
        if(!double.IsFinite(step)||step<=0)return [];
        if((max-min)/step>200)throw new InvalidDataException("O intervalo do eixo gera mais de 200 divisões. Aumente o intervalo.");
        var ticks=new List<AxisTick>();double start=Math.Ceiling(min/step-1e-10)*step;
        for(int i=0;i<=201;i++)
        {
            double position=start+i*step;if(position>max+step*1e-8)break;
            if(Math.Abs(position)<step*1e-9)position=0;
            double value=logarithmic?Math.Pow(10,position):position;
            if(!double.IsFinite(value))continue;
            string label=Math.Abs(value-Math.Round(value))<Math.Max(1,Math.Abs(value))*1e-10
                ? Math.Round(value).ToString("0",CultureInfo.CurrentCulture):value.ToString("G6",CultureInfo.CurrentCulture);
            ticks.Add(new(position,value,label));
        }
        return ticks.ToArray();
    }
}
