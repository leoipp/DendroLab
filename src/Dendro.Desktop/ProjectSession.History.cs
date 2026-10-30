using System.IO;
using System.Text.Json.Nodes;
using Dendro.Core.Processing;

namespace Dendro.Desktop;

internal sealed partial class ProjectSession
{
    private long historyRevision = -1;
    internal bool IsHistoricalTable(string path) => Meta.Arr("processing_results").Any(r => r.S("id").Length > 0 &&
        string.Equals(Resolve(r.S("path")), Resolve(path), StringComparison.OrdinalIgnoreCase));

    internal async Task RefreshHistory(bool force = false)
    {
        if (FilePath.Length == 0) return;
        string project = FilePath;
        var seed = Meta.Arr("processing_results").DeepClone().AsArray();
        var catalog = await Task.Run(() => ProcessingCatalog.Read(project, seed));
        if (FilePath != project) return;
        if (!force && historyRevision == (long)catalog.N("revision")) return;
        MergeHistory(catalog);
    }

    private void MergeHistory(JsonObject catalog)
    {
        historyRevision = (long)catalog.N("revision");
        if (Meta.S("database_path").Length == 0 && catalog.S("database_path").Length > 0) Meta["database_path"] = catalog.S("database_path");
        var results = Meta.Arr("processing_results");
        foreach (var old in results.OfType<JsonObject>().Where(r => r.S("id").Length > 0).ToArray()) results.Remove(old);
        foreach (var entry in catalog.Arr("records").OfType<JsonObject>())
        {
            results.Add(entry.DeepClone());
            string path = entry.S("path");
            if (path.Length == 0) continue;
            if (!Files.Contains(path, StringComparer.OrdinalIgnoreCase)) Meta.Arr("files").Add(path);
            if (!Meta.Arr("file_options").Any(o => o.S("path") == path))
                Meta.Arr("file_options").Add(new JsonObject { ["path"] = path, ["sheet"] = ProcessingHistory.Trees, ["header_row"] = 1 });
        }
        Notify();
    }

    internal async Task PublishProcessing(string stage, JsonObject record, CancellationToken cancellation, Action<long, long>? progress = null)
    {
        ProjectWorkFolder();
        string dataset = "processamento_" + record.S("id") + ".dlb";
        record["path"] = Path.Combine(Meta.S("database_path"), dataset);
        var seed = Meta.Arr("processing_results").DeepClone().AsArray();
        string bank = Resolve(Meta.S("database_path"));
        var catalog = await Task.Run(() => ProcessingCatalog.Publish(FilePath, seed, bank, stage, record, cancellation, progress));
        MergeHistory(catalog);
        File.Delete(stage);
    }

    internal async Task ChangeProcessingStatus(JsonObject record, string status, string note, CancellationToken cancellation)
    {
        var seed = Meta.Arr("processing_results").DeepClone().AsArray();
        var catalog = await Task.Run(() => ProcessingCatalog.ChangeStatus(FilePath, seed, record.S("id"), (int)record.N("revision"), status, note, cancellation));
        MergeHistory(catalog);
    }

    internal void RequireApprovedExport(string path)
    {
        var run = Meta.Arr("processing_results").FirstOrDefault(r => r.S("id").Length > 0 &&
            string.Equals(Resolve(r.S("path")), Resolve(path), StringComparison.OrdinalIgnoreCase));
        if (run != null && (run.S("status") != "aprovado" || !run.B("current_approved")))
            throw new InvalidOperationException("A exportação deste processamento exige a versão aprovada vigente. Use Revisão de inventário para aprovar e selecionar as abas.");
    }
}
