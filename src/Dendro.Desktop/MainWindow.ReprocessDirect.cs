using System.IO;
using System.Text.Json.Nodes;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

public partial class MainWindow
{
    private async Task ReprocessDirect(JsonObject record)
    {
        if (record.S("id").Length > 0 || !record.Arr("sheets").Any(s => s.Display().StartsWith("DIRETO -", StringComparison.Ordinal)))
            throw new InvalidOperationException("Selecione um resultado de resumo direto.");
        string source = record.S("source_path"), destination = record.S("path");
        if (!CompactBankFile.Exists(Session.Resolve(source)))
            throw new FileNotFoundException("A base de origem do processamento não foi encontrada.", Session.Resolve(source));
        string registry = Session.Resolve(Session.RegistryPath);
        if (!CompactBankFile.Exists(registry))
            throw new InvalidOperationException("Configure o cadastro florestal em Projeto > Configurações.");

        var config = Session.ProcessingSnapshot();
        if (record["direct_options"] is JsonObject options)
            config["processador"] = options.DeepClone();
        var processing = config.Obj("processador");
        processing["modo"] = "resumo_direto";
        processing.Remove("execucao");
        var sheets = record.Arr("sheets").Select(s => s.Display()).ToHashSet(StringComparer.Ordinal);
        foreach (var pair in new[] { ("arvore", "DIRETO - ARVORES"), ("parcela", "DIRETO - PARCELAS"),
            ("talhao", "DIRETO - TALHOES"), ("lote", "DIRETO - LOTES"), ("projeto", "DIRETO - PROJETO") })
            processing.Obj("saidas")[pair.Item1] = sheets.Contains(pair.Item2);
        string token = Guid.NewGuid().ToString("N");
        string snapshot = Path.Combine(Path.GetTempPath(), "DENDRO-reprocess-" + token + ".dlb");
        string stage = Path.Combine(Session.ProjectWorkFolder(), "reprocess_" + token + ".dlb");
        var cancellation = OperationCancellation;
        var progress = StartTableOperation("Reprocessando " + Session.ContentName(source) + "…");
        try
        {
            Log($"Reprocessando a base {Session.ContentName(source)} · aba {record.S("source_sheet", Session.ExcelOptions(source).S("sheet"))} · altura: {config["ajuste_altura"].S("h_mode", "KEEP_FILE")}. Todos os volumes e resumos serão calculados novamente.");
            await WorkspaceOperations.Run(new() { ["op"] = "save", ["project"] = config, ["path"] = snapshot });
            var result = await WorkspaceOperations.Run(new()
            {
                ["op"] = "process", ["input"] = Session.Resolve(source), ["config"] = snapshot,
                ["registry"] = registry, ["output"] = stage,
                ["sheet"] = record.S("source_sheet", Session.ExcelOptions(source).S("sheet")),
            }, (percent, message) => progress.Report(new TableProgress(percent, message)), cancellation);
            var updatedSheets = (await WorkspaceOperations.Run(new() { ["op"] = "sheets", ["path"] = stage }))["result"]!.AsArray();
            var missingSheets = sheets.Except(updatedSheets.Select(s => s.Display()), StringComparer.Ordinal).ToArray();
            if (missingSheets.Length > 0)
                throw new InvalidOperationException("O reprocessamento não recriou todas as saídas anteriores: " + string.Join(", ", missingSheets) + ". O resultado anterior foi preservado.");
            cancellation.ThrowIfCancellationRequested();
            string pathKey = destination;
            foreach (string key in documents.Keys.Where(k => k.Equals("table:" + pathKey, StringComparison.OrdinalIgnoreCase)
                || k.StartsWith("table:" + pathKey + ":", StringComparison.OrdinalIgnoreCase)).ToArray())
                CloseDocument(key);
            await Session.CommitBankFile(stage, destination);
            record["sheets"] = updatedSheets.DeepClone();
            record["totals"] = result["result"]!.DeepClone();
            record["removed_duplicates"] = result["result"]!.N("removed_duplicates");
            record["reprocessed"] = DateTimeOffset.Now.ToString("O");
            record["direct_options"] = processing.DeepClone();
            Session.Touch();
            Log($"Resumo direto atualizado: {destination} · {result["result"]!.N("trees"):N0} árvores · {result["result"]!.N("plots"):N0} parcelas · {result["result"]!.N("stands"):N0} talhões. Saídas recriadas: {string.Join(", ", updatedSheets.Select(s => s.Display()))}.");
            StatusText.Text = $"Reprocessamento concluído: {result["result"]!.N("trees"):N0} árvores; {updatedSheets.Count} tabelas atualizadas.";
        }
        finally
        {
            if (File.Exists(snapshot)) File.Delete(snapshot);
            if (File.Exists(stage)) File.Delete(stage);
        }
    }
}
