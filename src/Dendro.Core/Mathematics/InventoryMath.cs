using System.Globalization;

namespace Dendro.Core.Mathematics;

public static class InventoryMath
{
    public static double Volume(double dap, double height, double b0, double b1, double b2) =>
        dap > 0 && height > 0 && double.IsFinite(dap) && double.IsFinite(height)
            ? Math.Exp(b0 + b1 * Math.Log(dap / 10) + b2 * Math.Log(height / 10))
            : double.NaN;

    public static void VolumeBatch(
        ReadOnlySpan<double> dap,
        ReadOnlySpan<double> height,
        double b0,
        double b1,
        double b2,
        double factor,
        Span<double> output
    )
    {
        if (
            dap.Length != height.Length
            || output.Length < dap.Length
            || !double.IsFinite(factor)
            || factor < 0
            || factor > 1
        )
            throw new ArgumentException("Lote ou fator volumétrico inválido.");
        for (int i = 0; i < dap.Length; i++)
            output[i] = Volume(dap[i], height[i], b0, b1, b2) * factor;
    }

    public static double Area(double length, double width, double slopeLength, double slopeWidth) =>
        length
        / 10
        * Math.Cos(slopeLength * Math.PI / 180)
        * width
        / 10
        * Math.Cos(slopeWidth * Math.PI / 180);

    public static string EquationCode(string lot, double age, double limit = 15) =>
        age > limit
            ? "> 15 anos"
            : (lot.Length > 4 ? lot[4..Math.Min(lot.Length, 6)] : "")
                + (lot.Length > 11 && "RJKLMNO".Contains(lot[11]) ? "2R" : "1R");

    public static double Gini(IEnumerable<double> source)
    {
        var a = source.Where(double.IsFinite).Order().ToArray();
        if (a.Length == 0 || a.Any(v => v < 0))
            return double.NaN;
        double sum = a.Sum();
        return sum > 0
            ? 2 * a.Select((v, i) => (i + 1) * v).Sum() / (a.Length * sum)
                - (a.Length + 1d) / a.Length
            : 0;
    }

    public static double Date(string text)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
            return serial;
        return DateTime.TryParseExact(
            text,
            ["yyyy-MM-dd", "dd/MM/yyyy", "yyyy-MM-dd HH:mm:ss"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var date
        )
            ? date.ToOADate()
            : double.NaN;
    }

    public static int Year(double serial) =>
        double.IsFinite(serial) && serial >= 1 && serial <= 2958465
            ? DateTime.FromOADate(Math.Floor(serial)).Year
            : 0;

    public static double Mean(IEnumerable<double> source)
    {
        double sum = 0;
        int n = 0;
        foreach (double v in source)
            if (!double.IsNaN(v))
            {
                sum += v;
                n++;
            }
        return n > 0 ? sum / n : double.NaN;
    }

    public static double Aggregate(IEnumerable<double> source, string function)
    {
        var a = source.Where(double.IsFinite).ToArray();
        if (function == "count")
            return a.Length;
        if (a.Length == 0)
            return function == "sum" ? 0 : double.NaN;
        return function switch
        {
            "mean" or "media" => a.Average(),
            "sum" or "soma" => a.Sum(),
            "min" => a.Min(),
            "max" => a.Max(),
            "first" or "primeiro" => a[0],
            "last" => a[^1],
            "gini" => Gini(a),
            "std" => a.Length > 1
                ? Math.Sqrt(a.Sum(v => Math.Pow(v - a.Average(), 2)) / (a.Length - 1))
                : double.NaN,
            "median" => Median(a),
            _ => throw new InvalidDataException(
                $"Função de estatística não suportada: {function}."
            ),
        };
    }

    private static double Median(double[] a)
    {
        Array.Sort(a);
        return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
    }

    public static bool Solve(double[,] matrix, double[] rhs, out double[] solution)
    {
        int n = rhs.Length;
        solution = (double[])rhs.Clone();
        var a = (double[,])matrix.Clone();
        for (int i = 0; i < n; i++)
        {
            int best = i;
            for (int k = i + 1; k < n; k++)
                if (Math.Abs(a[k, i]) > Math.Abs(a[best, i]))
                    best = k;
            if (!double.IsFinite(a[best, i]) || Math.Abs(a[best, i]) < 1e-14)
                return false;
            if (best != i)
            {
                for (int j = 0; j < n; j++)
                    (a[i, j], a[best, j]) = (a[best, j], a[i, j]);
                (solution[i], solution[best]) = (solution[best], solution[i]);
            }
            for (int k = i + 1; k < n; k++)
            {
                double factor = a[k, i] / a[i, i];
                for (int j = i; j < n; j++)
                    a[k, j] -= factor * a[i, j];
                solution[k] -= factor * solution[i];
            }
        }
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = i + 1; j < n; j++)
                solution[i] -= a[i, j] * solution[j];
            solution[i] /= a[i, i];
            if (!double.IsFinite(solution[i]))
                return false;
        }
        return true;
    }
}
