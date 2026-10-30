using System.Globalization;

namespace Dendro.Core.Mathematics;

/// <summary>Compiles a bounded arithmetic grammar. Expressions never execute application or script code.</summary>
public sealed class Formula
{
    private readonly Func<
        Func<string, double>,
        IReadOnlyList<Func<string, double>>?,
        double
    > evaluate;

    public Formula(string expression, IEnumerable<string> symbols)
    {
        if (string.IsNullOrWhiteSpace(expression) || expression.Length > 4096)
            throw new InvalidDataException("Fórmula vazia ou acima do limite.");
        var parser = new Parser(expression, symbols);
        evaluate = parser.Parse();
    }

    public double Evaluate(
        Func<string, double> values,
        IReadOnlyList<Func<string, double>>? group = null
    ) => evaluate(values, group);

    private delegate double Node(
        Func<string, double> values,
        IReadOnlyList<Func<string, double>>? group
    );

    private sealed class Parser(string text, IEnumerable<string> symbols)
    {
        private int pos,
            depth;
        private readonly string[] names = symbols
            .Distinct()
            .OrderByDescending(s => s.Length)
            .ToArray();

        private void Space()
        {
            while (pos < text.Length && char.IsWhiteSpace(text[pos]))
                pos++;
        }

        private bool Take(string token)
        {
            Space();
            if (!text.AsSpan(pos).StartsWith(token, StringComparison.Ordinal))
                return false;
            pos += token.Length;
            return true;
        }

        public Func<Func<string, double>, IReadOnlyList<Func<string, double>>?, double> Parse()
        {
            var n = Sum();
            Space();
            if (pos != text.Length)
                throw Bad();
            return (v, g) => n(v, g);
        }

        private InvalidDataException Bad() =>
            new($"Fórmula inválida na posição {pos + 1}: {text}.");

        private Node Sum()
        {
            var n = Product();
            while (true)
            {
                if (Take("+"))
                {
                    var a = n;
                    var b = Product();
                    n = (v, g) => a(v, g) + b(v, g);
                }
                else if (Take("-"))
                {
                    var a = n;
                    var b = Product();
                    n = (v, g) => a(v, g) - b(v, g);
                }
                else
                    return n;
            }
        }

        private Node Product()
        {
            var n = Unary();
            while (true)
            {
                Space();
                if (text.AsSpan(pos).StartsWith("**"))
                    return n;
                if (Take("*"))
                {
                    var a = n;
                    var b = Unary();
                    n = (v, g) => a(v, g) * b(v, g);
                }
                else if (Take("/"))
                {
                    var a = n;
                    var b = Unary();
                    n = (v, g) => a(v, g) / b(v, g);
                }
                else
                    return n;
            }
        }

        private Node Unary()
        {
            if (++depth > 64)
                throw Bad();
            try
            {
                if (Take("+"))
                    return Unary();
                if (Take("-"))
                {
                    var n = Unary();
                    return (v, g) => -n(v, g);
                }
                var a = Atom();
                if (Take("**") || Take("^"))
                {
                    var b = Unary();
                    return (v, g) => Math.Pow(a(v, g), b(v, g));
                }
                return a;
            }
            finally
            {
                depth--;
            }
        }

        private Node Atom()
        {
            Space();
            if (Take("("))
            {
                var n = Sum();
                if (!Take(")"))
                    throw Bad();
                return n;
            }
            if (pos < text.Length && (char.IsDigit(text[pos]) || text[pos] == '.'))
            {
                int start = pos;
                while (pos < text.Length && (char.IsDigit(text[pos]) || text[pos] == '.'))
                    pos++;
                if (pos < text.Length && text[pos] is 'e' or 'E')
                {
                    pos++;
                    if (pos < text.Length && text[pos] is '+' or '-')
                        pos++;
                    while (pos < text.Length && char.IsDigit(text[pos]))
                        pos++;
                }
                if (
                    !double.TryParse(
                        text[start..pos],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var n
                    )
                )
                    throw Bad();
                return (_, _) => n;
            }
            int begin = pos;
            if (text.AsSpan(pos).StartsWith("np."))
                pos += 3;
            int fn = pos;
            while (pos < text.Length && (char.IsLetterOrDigit(text[pos]) || text[pos] == '_'))
                pos++;
            string name = text[fn..pos];
            if (name.Length > 0 && Take("("))
            {
                if (name == "pi" && Take(")"))
                    return (_, _) => Math.PI;
                var a = Sum();
                Node? b = Take(",") ? Sum() : null;
                if (!Take(")"))
                    throw Bad();
                if (b is not null)
                    return name switch
                    {
                        "pow" => (v, g) => Math.Pow(a(v, g), b(v, g)),
                        "min" => (v, g) => Math.Min(a(v, g), b(v, g)),
                        "max" => (v, g) => Math.Max(a(v, g), b(v, g)),
                        _ => throw Bad(),
                    };
                if (name is "sum" or "soma" or "mean")
                    return (v, g) =>
                    {
                        var values = g is null
                            ? new[] { a(v, null) }
                            : g.Select(row => a(row, null)).Where(double.IsFinite).ToArray();
                        return name == "mean"
                            ? (values.Length > 0 ? values.Average() : double.NaN)
                            : values.Sum();
                    };
                Func<double, double> f = name switch
                {
                    "ln" or "log" => Math.Log,
                    "log10" => Math.Log10,
                    "log2" => Math.Log2,
                    "exp" => Math.Exp,
                    "sqrt" => Math.Sqrt,
                    "abs" => Math.Abs,
                    "sin" => Math.Sin,
                    "cos" => Math.Cos,
                    "tan" => Math.Tan,
                    _ => throw Bad(),
                };
                return (v, g) => f(a(v, g));
            }
            pos = begin;
            foreach (var symbol in names)
                if (
                    text.AsSpan(pos).StartsWith(symbol, StringComparison.Ordinal)
                    && (
                        pos + symbol.Length == text.Length
                        || !(
                            char.IsLetterOrDigit(text[pos + symbol.Length])
                            || text[pos + symbol.Length] == '_'
                        )
                    )
                )
                {
                    pos += symbol.Length;
                    return (v, _) => v(symbol);
                }
            if (Take("pi"))
                return (_, _) => Math.PI;
            throw Bad();
        }
    }
}
