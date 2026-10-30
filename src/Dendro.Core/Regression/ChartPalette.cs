namespace Dendro.Core.Regression;

public static class ChartPalette
{
    public static readonly string[] Colors=["#21669D","#1C8965","#D56D2C","#8155B4","#C33E5E"];
    public static string At(int index)
    {
        if(index<Colors.Length)return Colors[Math.Max(0,index)];
        double h=(index*137.508)%360/60,c=.65,x=c*(1-Math.Abs(h%2-1)),m=.16;
        var (r,g,b)=h<1?(c,x,0d):h<2?(x,c,0d):h<3?(0d,c,x):h<4?(0d,x,c):h<5?(x,0d,c):(c,0d,x);
        return $"#{(int)((r+m)*255):X2}{(int)((g+m)*255):X2}{(int)((b+m)*255):X2}";
    }
}
