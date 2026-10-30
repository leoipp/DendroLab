using System.Globalization;

namespace Dendro.Core.Tabular;

/// <summary>Small, non-executable expression language for table columns.</summary>
public sealed class ColumnExpression
{
    private readonly Func<object?[], object?> evaluate;
    public string[] Dependencies { get; }
    public ColumnExpression(string expression, string[] columns)
    {
        if (string.IsNullOrWhiteSpace(expression) || expression.Length > 4096)
            throw new InvalidDataException("Informe uma expressão de até 4096 caracteres.");
        var parser = new Parser(expression, columns);
        evaluate = parser.Parse();
        Dependencies = parser.Dependencies.ToArray();
    }
    public object? Evaluate(object?[] row) => evaluate(row);

    private sealed class Parser(string text, string[] columns)
    {
        private int pos, depth;
        public HashSet<string> Dependencies { get; } = new(StringComparer.Ordinal);
        private InvalidDataException Bad(string message) => new($"{message} (posição {pos + 1}).");
        private void Space() { while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++; }
        private bool Take(string token)
        {
            Space();
            if (!text.AsSpan(pos).StartsWith(token, StringComparison.Ordinal)) return false;
            pos += token.Length; return true;
        }
        public Func<object?[], object?> Parse()
        {
            var result = Expression(); Space();
            if (pos != text.Length) throw Bad("Trecho inesperado na expressão");
            return result;
        }
        private Func<object?[], object?> Expression()
        {
            if (++depth > 48) throw Bad("Expressão muito aninhada");
            try
            {
                var left = Sum();
                foreach (string op in new[] { ">=", "<=", "<>", "!=", "==", "=", ">", "<" })
                    if (Take(op))
                    {
                        var right = Sum();
                        return row => {
                            object? a = left(row), b = right(row);
                            int c = a is double x && b is double y ? x.CompareTo(y)
                                : string.Compare(TableCursor.Text(a), TableCursor.Text(b), StringComparison.Ordinal);
                            return op switch { "=" or "==" => c == 0, "<>" or "!=" => c != 0,
                                ">" => c > 0, "<" => c < 0, ">=" => c >= 0, _ => c <= 0 };
                        };
                    }
                return left;
            }
            finally { depth--; }
        }
        private Func<object?[], object?> Sum()
        {
            var node = Product();
            while (true)
            {
                string? op = Take("&") ? "&" : Take("+") ? "+" : Take("-") ? "-" : null;
                if (op == null) return node;
                var a = node; var b = Product();
                node = row => op == "&" ? TableCursor.Text(a(row)) + TableCursor.Text(b(row))
                    : op == "+" ? Number(a(row)) + Number(b(row)) : Number(a(row)) - Number(b(row));
            }
        }
        private Func<object?[], object?> Product()
        {
            var node = Atom();
            while (true)
            {
                string? op = Take("*") ? "*" : Take("/") ? "/" : null;
                if (op == null) return node;
                var a = node; var b = Atom();
                node = row => op == "*" ? Number(a(row)) * Number(b(row)) : Number(a(row)) / Number(b(row));
            }
        }
        private Func<object?[], object?> Atom()
        {
            Space();
            if (Take("(")) { var n = Expression(); if (!Take(")")) throw Bad("Falta fechar parênteses"); return n; }
            if (Take("-")) { var n = AtomBounded(); return row => -Number(n(row)); }
            if (Take("["))
            {
                int start = pos; while (pos < text.Length && text[pos] != ']') pos++;
                string name = text[start..pos];
                if (!Take("]")) throw Bad("Falta fechar referência à coluna");
                int index = Array.IndexOf(columns, name);
                if (index < 0) throw Bad($"Coluna não encontrada: {name}");
                Dependencies.Add(name); return row => row[index];
            }
            if (pos < text.Length && text[pos] is '\"' or '\'')
            {
                char quote = text[pos++]; var value = new System.Text.StringBuilder();
                while (pos < text.Length)
                {
                    char c = text[pos++];
                    if (c == quote)
                    {
                        if (pos < text.Length && text[pos] == quote) { value.Append(quote); pos++; continue; }
                        string literal = value.ToString(); return _ => literal;
                    }
                    value.Append(c);
                }
                throw Bad("Texto sem aspas de fechamento");
            }
            int begin = pos;
            if (pos < text.Length && (char.IsDigit(text[pos]) || text[pos] == '.'))
            {
                while (pos < text.Length && (char.IsDigit(text[pos]) || text[pos] == '.')) pos++;
                if (!double.TryParse(text[begin..pos], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) throw Bad("Número inválido");
                return _ => value;
            }
            while (pos < text.Length && (char.IsLetter(text[pos]) || text[pos] is '_' or '.')) pos++;
            string function = text[begin..pos].ToUpperInvariant();
            if (function == "NULO") return _ => null;
            if (function == "VERDADEIRO") return _ => true;
            if (function == "FALSO") return _ => false;
            if (function.Length == 0 || !Take("(")) throw Bad("Use [Coluna], texto entre aspas ou uma função");
            var args = new List<Func<object?[], object?>>();
            if (!Take(")"))
            {
                do { args.Add(Expression()); } while (Take(";") || Take(","));
                if (!Take(")")) throw Bad("Falta fechar função");
            }
            int arity = function switch { "SE" or "EXT.TEXTO" or "SUBSTITUIR" => 3,
                "ESQUERDA" or "DIREITA" or "CONTEM" or "ANTES" or "DEPOIS" => 2,
                "MAIUSCULA" or "MINUSCULA" or "APARAR" or "TAMANHO" => 1, "MEIO" => 3, _ => -1 };
            if (arity != args.Count || arity < 0) throw Bad($"Função ou quantidade de argumentos inválida: {function}");
            return row => {
                object? A(int i) => args[i](row);
                string S(int i) => TableCursor.Text(A(i));
                int N(int i) { double n = Number(A(i)); if (!double.IsFinite(n) || n < 0 || n > int.MaxValue || n != Math.Truncate(n)) throw new InvalidDataException("Posições e comprimentos devem ser inteiros não negativos."); return (int)n; }
                string Slice(string s, int start, int length) => start >= s.Length ? "" : s.Substring(start, Math.Min(length, s.Length - start));
                string Split(bool after) { string s = S(0), separator = S(1); if (separator.Length == 0) throw new InvalidDataException("Informe um separador não vazio."); int at = s.IndexOf(separator, StringComparison.Ordinal); return at < 0 ? "" : after ? s[(at + separator.Length)..] : s[..at]; }
                return function switch {
                    "SE" => Truth(A(0)) ? A(1) : A(2),
                    "ESQUERDA" => Slice(S(0), 0, N(1)),
                    "DIREITA" => S(0)[Math.Max(0, S(0).Length - N(1))..],
                    "MEIO" or "EXT.TEXTO" => N(1) < 1 ? throw new InvalidDataException("A posição inicial começa em 1.") : Slice(S(0), N(1) - 1, N(2)),
                    "CONTEM" => S(0).Contains(S(1), StringComparison.Ordinal),
                    "ANTES" => Split(false), "DEPOIS" => Split(true),
                    "SUBSTITUIR" => S(1).Length == 0 ? throw new InvalidDataException("Texto de busca vazio.") : S(0).Replace(S(1), S(2), StringComparison.Ordinal),
                    "MAIUSCULA" => S(0).ToUpperInvariant(), "MINUSCULA" => S(0).ToLowerInvariant(),
                    "APARAR" => S(0).Trim(), "TAMANHO" => (double)S(0).Length,
                    _ => throw Bad("Função inválida") };
            };
        }
        private Func<object?[], object?> AtomBounded()
        {
            if (++depth > 48) throw Bad("Expressão muito aninhada");
            try { return Atom(); } finally { depth--; }
        }
        private static double Number(object? value) => value is double n ? n
            : double.TryParse(TableCursor.Text(value), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed
            : throw new InvalidDataException("O cálculo exige um valor numérico.");
        private static bool Truth(object? value) => value is bool b ? b : value is double n ? n != 0
            : TableCursor.Text(value) is "Falso" or "" ? false : true;
    }
}
