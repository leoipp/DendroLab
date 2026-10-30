using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dendro.Core.Mathematics;
using Dendro.Core.Spreadsheets;

namespace Dendro.Core.Processing;

public static class FeaturePreparation
{
    public static Dictionary<string, int> RegistryIndex(DataTable registry, string key)
    {
        int c = registry.Require(key);
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int r = 1; r < registry.Count; r++)
        {
            string value = registry.Text(r, c);
            if (value.Length > 0 && !index.TryAdd(value, r))
                throw new InvalidDataException(
                    $"Cadastro com chave duplicada: {value}. Revise antes de processar."
                );
        }
        return index;
    }

    public static void Join(DataTable input, DataTable registry, JsonObject config)
    {
        RegistryAgeFallback.Apply(input, registry, config);
        int key = input.Require(config["arquivo_entrada"].S("chave", "Talhão"));
        var index = RegistryIndex(registry, config["cadastro"].S("chave", "Talhão"));
        var map = Enumerable
            .Range(0, registry.Width)
            .Select(c =>
                input.Column(registry.Text(0, c)) < 0 ? input.Ensure(registry.Text(0, c)) : -1
            )
            .ToArray();
        for (int r = 1; r < input.Count; r++)
        {
            if (!index.TryGetValue(input.Text(r, key), out int rr))
                throw new InvalidDataException(
                    $"Chave não encontrada no cadastro: {input.Text(r, key)}."
                );
            for (int c = 0; c < map.Length; c++)
                if (map[c] >= 0)
                    input.Set(r, map[c], registry.Value(rr, c));
        }
    }

    public static void Prepare(DataTable t, DataTable registry, JsonObject cfg)
    {
        var identification = new InventoryIdentity(t, cfg);
        Join(t, registry, cfg);
        var cols = cfg["arquivo_entrada"]?["colunas"];
        var cc = cfg["cadastro"]?["colunas"];
        var h = cfg["ajuste_altura"]!;
        int dc = t.Require(cols.S("data_medicao", "Data")),
            pc = t.Require(cc.S("data_plantio", "Data de Plantio")),
            age = t.Ensure("Idade_anos"),
            year = t.Ensure("Ano_Medicao");
        for (int r = 1; r < t.Count; r++)
        {
            double measured = InventoryMath.Date(t.Text(r, dc));
            t.Set(r, age, Math.Floor(measured - InventoryMath.Date(t.Text(r, pc))) / 365.25);
            t.Set(r, year, (double)InventoryMath.Year(measured));
        }
        int stand = t.Require(cols.S("talhao", "Talhão")),
            plot = t.Require(cols.S("parcela", "Parcela"));
        bool temporal = cfg["estatisticas"].B("chave_temporal_ativa");
        var groups = Enumerable
            .Range(1, t.Count - 1)
            .GroupBy(identification.PlotKey)
            .Select(g => g.ToArray())
            .ToArray();
        var stats = h["estatisticas_parcela"] as JsonArray ?? [];
        foreach (var entry in stats)
        {
            if (entry.S("formula").Length > 0)
                continue;
            string name = entry.S("nome");
            int from = t.Require(entry.S("coluna")),
                dest = t.Ensure(name);
            Guard(dest, name);
            foreach (var group in groups)
            {
                double value = InventoryMath.Aggregate(
                    group.Select(r => t.Number(r, from)).Where(x => Filter(x, entry.S("filtro"))),
                    entry.S("funcao")
                );
                foreach (int r in group)
                    t.Set(r, dest, value);
            }
        }
        foreach (
            var entry in stats
                .Where(s => s.S("formula").Length > 0)
                .Concat(h["variaveis_customizadas"] as JsonArray ?? [])
        )
        {
            string name = entry.S("nome"),
                expression = entry.S("formula"),
                type = entry.S("tipo");
            int existing = t.Column(name),
                dest = t.Ensure(name);
            Guard(dest, name);
            if (type.StartsWith("data_diff_", StringComparison.Ordinal) && existing >= 0)
                continue;
            if (entry.S("fonte") == "cadastro")
            {
                int source = t.Require(entry.S("coluna", name));
                for (int r = 1; r < t.Count; r++)
                    t.Set(r, dest, t.Value(r, source));
                continue;
            }
            if (type.StartsWith("data_diff_", StringComparison.Ordinal))
            {
                double divisor = type switch
                {
                    "data_diff_anos" => 365.25,
                    "data_diff_meses" => 30.4375,
                    "data_diff_dias" => 1,
                    _ => throw new InvalidDataException($"Datas inválidas para {name}."),
                };
                int a = t.Require(cols.S(entry.S("col_data1"))),
                    b = t.Require(cc.S(entry.S("col_data2")));
                for (int r = 1; r < t.Count; r++)
                    t.Set(
                        r,
                        dest,
                        Math.Floor(
                            InventoryMath.Date(t.Text(r, a)) - InventoryMath.Date(t.Text(r, b))
                        ) / divisor
                    );
                continue;
            }
            if (TryString(expression, t, 1, out _))
            {
                for (int r = 1; r < t.Count; r++)
                {
                    if (!TryString(expression, t, r, out string value))
                        throw new InvalidDataException($"Fórmula de texto inválida: {name}.");
                    t.Set(r, dest, value);
                }
                continue;
            }
            var symbols = Enumerable
                .Range(0, t.Width)
                .Where(c => c != dest)
                .ToDictionary(c => t.Text(0, c), c => c, StringComparer.Ordinal);
            var formula = new Formula(expression, symbols.Keys);
            foreach (var group in groups)
            {
                var rows = group
                    .Select(r => (Func<string, double>)(s => t.Number(r, symbols[s])))
                    .ToArray();
                foreach (int r in group)
                    t.Set(r, dest, formula.Evaluate(s => t.Number(r, symbols[s]), rows));
            }
        }
        void Guard(int dest, string name)
        {
            if (dest == stand || dest == plot || dest == year)
                throw new InvalidDataException(
                    $"A variável {name} não pode substituir uma chave de agrupamento."
                );
        }
    }

    public static bool Filter(double value, string text)
    {
        if (text.Length == 0)
            return true;
        var match = Regex.Match(
            text,
            @"^\s*(>=|<=|==|!=|>|<)\s*([+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?)\s*$"
        );
        if (!match.Success)
            throw new InvalidDataException($"Filtro inválido: {text}.");
        double rhs = double.Parse(
            match.Groups[2].Value,
            System.Globalization.CultureInfo.InvariantCulture
        );
        return match.Groups[1].Value switch
        {
            ">=" => value >= rhs,
            "<=" => value <= rhs,
            "==" => value == rhs,
            "!=" => value != rhs,
            ">" => value > rhs,
            _ => value < rhs,
        };
    }

    private static bool TryString(string text, DataTable table, int row, out string value)
    {
        value = "";
        string slice = text;
        bool where = text.StartsWith("np.where(np.isin(", StringComparison.Ordinal);
        if (where)
            slice = text[17..];
        var m = Regex.Match(slice, @"^([^\[]+)\[(\d+):(\d+)\]");
        if (!m.Success)
            return false;
        int c = table.Column(m.Groups[1].Value.Trim());
        if (c < 0)
            return false;
        var chars = table.Text(row, c).EnumerateRunes().ToArray();
        int start = int.Parse(m.Groups[2].Value),
            end = int.Parse(m.Groups[3].Value);
        if (end < start || end > 10000)
            return false;
        value = string.Concat(chars.Skip(start).Take(end - start).Select(r => r.ToString()));
        string tail = slice[m.Length..].Trim();
        if (!where)
            return tail.Length == 0;
        var condition = Regex.Match(
            tail,
            "^,\\s*\\[(.*?)\\]\\s*\\)\\s*,\\s*(['\"])(.*?)\\2\\s*,\\s*(['\"])(.*?)\\4\\s*\\)\\s*$"
        );
        if (!condition.Success)
            return false;
        string chosen = value;
        bool found = Regex
            .Matches(condition.Groups[1].Value, "(['\"])(.*?)\\1")
            .Any(x => x.Groups[2].Value == chosen);
        value = condition.Groups[found ? 3 : 5].Value;
        return true;
    }
}
