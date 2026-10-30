using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dendro.Core.Mathematics;
using Dendro.Core.Spreadsheets;

namespace Dendro.Core.Processing;

public static class RegistryAgeFallback
{
    public const string DefaultPattern = "[A-Za-z](?<ano>\\d{2})(?:-\\d+[A-Za-z]*)?$";
    public const string LegacyDefaultPattern = "[A-Za-z](?<ano>\\d{2})(?:-\\d+)?$";
    public const string FlagColumn = "Cadastro_Fallback_Idade", DateColumn = "Data_Plantio_Fallback";
    public const string DiagnosticsKey = "Dendro.PlantingIssues";
    public static void Validate(JsonNode? value)
    {
        if (value == null) return;
        if (value is not JsonObject) throw new InvalidDataException("Fallback de idade inválido.");
        if (value["ativo"] is { } active && active.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("Ativação do fallback de idade inválida.");
        foreach (string key in new[] { "coluna_codigo", "padrao_ano" })
            if (value[key] is { } text && text.GetValueKind() != JsonValueKind.String)
                throw new InvalidDataException($"Parâmetro do fallback inválido: {key}.");
        double cutoff = value.N("limite_ano_2000", 30), month = value.N("mes_plantio", 6), day = value.N("dia_plantio", 1);
        if (!double.IsFinite(cutoff) || cutoff != Math.Truncate(cutoff) || cutoff is < 0 or > 99
            || !double.IsFinite(month) || month != Math.Truncate(month) || month is < 1 or > 12
            || !double.IsFinite(day) || day != Math.Truncate(day) || day is < 1 or > 31)
            throw new InvalidDataException("Fallback de idade: corte de século de 0 a 99; informe dia e mês inteiros válidos.");
        try { _ = new DateTime(2000, (int)month, (int)day); }
        catch (ArgumentOutOfRangeException) { throw new InvalidDataException("Dia/mês de plantio presumido inválido."); }
        _ = Pattern(value);
    }
    private static Regex? Pattern(JsonNode value)
    {
        string expression = value.S("padrao_ano", DefaultPattern);
        if (expression == LegacyDefaultPattern) expression = DefaultPattern;
        if (expression == DefaultPattern) return null; // The default suffix has a deterministic parser, with no regex timeout.
        if (expression.Length is < 1 or > 1024) throw new InvalidDataException("Padrão do ano deve ter de 1 a 1.024 caracteres.");
        try {
            Regex regex;
            try { regex = new Regex(expression, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1)); }
            catch (NotSupportedException) { regex = new Regex(expression, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
            if (!regex.GetGroupNames().Contains("ano")) throw new InvalidDataException("Use o grupo nomeado (?<ano>...) no padrão do fallback.");
            return regex;
        } catch (ArgumentException ex) { throw new InvalidDataException("Padrão do ano inválido no fallback de idade.", ex); }
    }
    public static DateTime InferDate(string code, JsonNode settings) { Validate(settings); return InferDate(code, settings, Pattern(settings)); }
    private static DateTime InferDate(string code, JsonNode settings, Regex? pattern)
    {
        string text;
        if (pattern == null) text = DefaultYear(code);
        else
        {
            Match match;
            try { match = pattern.Match(code); }
            catch (RegexMatchTimeoutException ex) { throw new InvalidDataException($"O padrão personalizado de plantio excedeu o tempo limite para {code}. Revise o padrão configurado.", ex); }
            text = match.Success ? match.Groups["ano"].Value : "";
        }
        if (text.Length is not (2 or 4) || text.Any(c => c is < '0' or > '9')
            || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int year))
            throw new InvalidDataException($"Não foi possível extrair o ano de plantio do código: {code}. Informe o plantio no cadastro ou revise o padrão do fallback.");
        if (text.Length == 2) year += year <= settings.N("limite_ano_2000", 30) ? 2000 : 1900;
        if (year is < 1900 or > 2099) throw new InvalidDataException($"Ano de plantio fora de 1900–2099: {code}.");
        try { return new DateTime(year, (int)settings.N("mes_plantio", 6), (int)settings.N("dia_plantio", 1)); }
        catch (ArgumentOutOfRangeException) { throw new InvalidDataException($"Data presumida de plantio inválida: {code}."); }
    }

    private static string DefaultYear(string code)
    {
        var value = code.AsSpan().Trim();
        int dash = value.LastIndexOf('-');
        if (dash >= 0)
        {
            var suffix = value[(dash + 1)..].Trim(); int position = 0;
            while (position < suffix.Length && char.IsAsciiDigit(suffix[position])) position++;
            bool valid = position > 0;
            while (position < suffix.Length) { if (!char.IsAsciiLetter(suffix[position++])) valid = false; }
            if (valid) value = value[..dash].TrimEnd();
        }
        return value.Length >= 3 && char.IsAsciiLetter(value[^3]) && char.IsAsciiDigit(value[^2]) && char.IsAsciiDigit(value[^1])
            ? value[^2..].ToString() : "";
    }

    public static int Apply(DataTable input, DataTable registry, JsonObject config, CancellationToken cancellation = default,
        Action<int, string>? progress = null)
    {
        var settings = config["cadastro"]?["fallback_idade"];
        if (!settings.B("ativo")) return 0;
        cancellation.ThrowIfCancellationRequested();
        Validate(settings); var pattern = Pattern(settings!);
        var cadastro = config["cadastro"];
        string keyName = cadastro.S("chave", "Talhão"), plantedName = cadastro.At("colunas").S("data_plantio", "Data de Plantio");
        int registryKey = registry.Require(keyName), planted = registry.Require(plantedName);
        var index = FeaturePreparation.RegistryIndex(registry, keyName);
        var inferred = new Dictionary<string, (DateTime Date, int Row)>(StringComparer.Ordinal);
        var codes = new Dictionary<string, (DateTime Date, string? Error)>(StringComparer.Ordinal);
        var issues = new Dictionary<(string Key, string Code, string Reason), (int First, long Count)>();
        int keyColumn = input.Require(config["arquivo_entrada"].S("chave", "Talhão"));
        string codeName = settings.S("coluna_codigo");
        int codeColumn = codeName.Length > 0 ? input.Require(codeName) : keyColumn;
        int measuredColumn = input.Require(config.At("arquivo_entrada.colunas").S("data_medicao", "Data"));
        var mapping = Enumerable.Range(0, registry.Width).Select(c => input.Column(registry.Text(0, c))).ToArray();
        void Issue(int row, string key, string code, string reason)
        {
            var previous = issues.GetValueOrDefault((key, code, reason));
            issues[(key, code, reason)] = (previous.Count == 0 ? row + 1 : previous.First, previous.Count + 1);
        }
        for (int row = 1; row < input.Count; row++)
        {
            if ((row & 8191) == 0)
            {
                cancellation.ThrowIfCancellationRequested();
                progress?.Invoke(21, $"Verificando plantio: {row:N0} de {input.Count - 1:N0} linhas · {issues.Count:N0} pendências…");
            }
            string key = input.Text(row, keyColumn);
            if (index.ContainsKey(key)) continue;
            string code = input.Text(row, codeColumn);
            if (key.Length == 0) { Issue(row, key, code, "Chave vazia no inventário."); continue; }
            if (!codes.TryGetValue(code, out var parsed))
            {
                try { parsed = (InferDate(code, settings!, pattern), null); }
                catch (InvalidDataException ex) when (ex.InnerException is not RegexMatchTimeoutException) { parsed = (default, ex.Message); }
                codes.Add(code, parsed);
            }
            if (parsed.Error != null) { Issue(row, key, code, parsed.Error); continue; }
            if (inferred.TryGetValue(key, out var known) && known.Date != parsed.Date)
            { Issue(row, key, code, "Ano de plantio conflitante para a chave."); continue; }
            inferred.TryAdd(key, (parsed.Date, row));
            double measured = InventoryMath.Date(input.Text(row, measuredColumn));
            if (double.IsFinite(measured) && parsed.Date.ToOADate() > measured)
                Issue(row, key, code, $"Plantio inferido {parsed.Date:dd/MM/yyyy} posterior à medição. Revise o corte de século.");
        }
        cancellation.ThrowIfCancellationRequested();
        if (issues.Count > 0)
        {
            var details = new DataTable(["Primeira linha processada", "Chave", "Código", "Ocorrências", "Motivo"]);
            foreach (var item in issues.OrderBy(i => i.Value.First))
                details.Add(item.Value.First.ToString(CultureInfo.InvariantCulture), item.Key.Key, item.Key.Code,
                    item.Value.Count.ToString(CultureInfo.InvariantCulture), item.Key.Reason);
            string first = string.Join("; ", issues.Take(5).Select(i => $"{i.Key.Code} (linha {i.Value.First:N0}, {i.Value.Count:N0} ocorrência(s)): {i.Key.Reason}"));
            var error = new InvalidDataException($"Plantio não resolvido: {issues.Count:N0} pendência(s), {issues.Values.Sum(i => i.Count):N0} linha(s). {first}");
            error.Data[DiagnosticsKey] = details;
            throw error;
        }
        if (inferred.Count == 0) return 0;
        foreach (var entry in inferred)
        {
            var values = mapping.Select(c => c >= 0 ? input.Value(entry.Value.Row, c) : null).ToArray();
            values[registryKey] = entry.Key; values[planted] = entry.Value.Date.ToOADate();
            int row = registry.Count; registry.Add(values); index.Add(entry.Key, row);
        }
        bool newPlantedColumn = input.Column(plantedName) < 0;
        int inputPlanted = input.Ensure(plantedName), flag = input.Ensure(FlagColumn), date = input.Ensure(DateColumn);
        var formatted = inferred.ToDictionary(e => e.Key, e => e.Value.Date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), StringComparer.Ordinal);
        for (int row = 1; row < input.Count; row++)
        {
            if ((row & 8191) == 0) cancellation.ThrowIfCancellationRequested();
            string key = input.Text(row, keyColumn);
            if (inferred.TryGetValue(key, out var inferredDate))
            {
                input.Set(row, inputPlanted, inferredDate.Date.ToOADate());
                input.Set(row, flag, "Sim"); input.Set(row, date, formatted[key]);
            }
            else if (newPlantedColumn && index.TryGetValue(key, out int known)) input.Set(row, inputPlanted, registry.Value(known, planted));
        }
        return inferred.Count;
    }
}
