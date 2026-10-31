using System.IO;
using System.Text.Json.Nodes;
using Dendro.Core.Configuration;
using Dendro.Core.Processing;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal sealed partial class ProjectSession
{
    private sealed class PendingBankRemoval
    {
        public HashSet<string> Tables { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Datasets { get; } = new(StringComparer.Ordinal);
    }

    private string? workFolder;
    private readonly Dictionary<string, PendingBankRemoval> pendingBankRemovals = new(StringComparer.OrdinalIgnoreCase);

    private void DeferBankRemoval(string path, IEnumerable<string>? tableIds = null, IEnumerable<string>? datasets = null)
    {
        path = Path.GetFullPath(path);
        if (!pendingBankRemovals.TryGetValue(path, out var pending))
            pendingBankRemovals[path] = pending = new PendingBankRemoval();
        if (tableIds != null) pending.Tables.UnionWith(tableIds);
        if (datasets != null) pending.Datasets.UnionWith(datasets);
    }

    private async Task ApplyPendingBankRemovals(string originalProject, string savedProject)
    {
        foreach (var pair in pendingBankRemovals.ToArray())
        {
            // Save As copies the project's bank to the new path; keep the source project intact.
            string bank = string.Equals(pair.Key, originalProject, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(originalProject, savedProject, StringComparison.OrdinalIgnoreCase)
                ? savedProject
                : pair.Key;
            await Task.Run(() => CompactBankFile.RemoveTables(bank, pair.Value.Tables, pair.Value.Datasets));
            pendingBankRemovals.Remove(pair.Key);
        }
    }

    public bool IsManagedTable(string path) => Meta.S("database_path").Length > 0
        && Resolve(path).StartsWith(Resolve(Meta.S("database_path")) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    // Work files live outside the project and are removed after being committed to its single bank.
    public string ProjectWorkFolder()
    {
        if (FilePath.Length == 0) throw new InvalidOperationException("Salve o projeto antes de gerar tabelas no arquivo DLB.");
        string relative = Meta.S("database_path", Path.GetFileNameWithoutExtension(FilePath) + ".dlb");
        if (Meta.S("database_path") != relative) { Meta["database_path"] = relative; Touch(); }
        workFolder ??= Path.Combine(Path.GetTempPath(), "DendroLab-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workFolder);
        return workFolder;
    }

    internal async Task<string> CommitBankFile(string source, string? replaceReference = null)
    {
        ProjectWorkFolder();
        string reference = replaceReference ?? ProjectReference(source);
        if (replaceReference == null) {
            string stem = Path.GetFileNameWithoutExtension(reference);
            for (int n = 2; CompactBankFile.Exists(Resolve(reference)); n++)
                reference = Path.Combine(Meta.S("database_path"), stem + "_" + n + ".dlb");
        }
        string dataset = Path.GetFileName(reference);
        await Task.Run(() => CompactBankFile.Import(Resolve(Meta.S("database_path")), [(source, dataset)], replaceReference != null));
        File.Delete(source);
        return reference;
    }

    internal string[] PreviewRemoveFile(int index) => PrepareFileRemoval(index).Removed;

    private (JsonObject Updated, string[] Removed) PrepareFileRemoval(int index)
    {
        string selected = Files.ElementAt(index);
        if (Meta.Arr("processing_results").Any(r => r.S("id").Length > 0 && (r.S("path") == selected || r.S("source_path") == selected)))
            throw new InvalidOperationException("Esta tabela possui histórico de processamento preservado e não pode ser removida.");
        var updated = Data!.DeepClone().AsObject();
        string[] removed = ProjectModels.RemoveFile(updated, index);
        if (Meta.Arr("processing_results").Any(r => r.S("id").Length > 0 && removed.Any(p => string.Equals(p.Replace('/', '\\'), r.S("path").Replace('/', '\\'), StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("A remoção atingiria versões históricas de processamento. O histórico precisa ser preservado.");
        return (updated, removed);
    }

    public Task RemoveFile(int index)
    {
        var (updated, removed) = PrepareFileRemoval(index);
        string database = Meta.S("database_path");
        if (database.Length > 0)
        {
            string bank = Path.GetFullPath(Resolve(database));
            string[] datasets = removed
                .Select(path => CompactBankFile.Location(Resolve(path)))
                .Where(location => location.Dataset.Length > 0
                    && string.Equals(location.File, bank, StringComparison.OrdinalIgnoreCase))
                .Select(location => location.Dataset)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (datasets.Length > 0)
                DeferBankRemoval(bank, datasets: datasets);
        }
        Replace(updated);
        return Task.CompletedTask;
    }

    internal string[] PreviewRemoveResultTable(string path, string sheet)
    {
        if (IsHistoricalTable(path)) throw new InvalidOperationException("As tabelas de uma versão histórica são preservadas. Crie uma revisão para aplicar fatores.");
        string resolved = Resolve(path);
        string[] remaining;
        using (var reader = new CompactBankReader(resolved))
        {
            var selected = reader.Table(sheet);
            remaining = reader.Catalog.Tables.Where(t => t.Id != selected.Id).Select(t => t.Name).ToArray();
        }
        var updated = Data!.DeepClone().AsObject();
        string firstSheet = updated.Obj("_dendro").Arr("processing_results").OfType<JsonObject>().Reverse()
            .Where(r => r.S("path") == path).SelectMany(r => r.Arr("sheets")).FirstOrDefault().Display();
        string[] descendants = DescendantPathsToRemove(updated, path, sheet, remaining, firstSheet);
        if (Meta.Arr("processing_results").Any(r => r.S("id").Length > 0 && descendants.Contains(r.S("path"), StringComparer.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A remoção atingiria versões históricas de processamento.");
        return descendants;
    }

    public Task RemoveResultTable(string path, string sheet)
    {
        if (IsHistoricalTable(path)) throw new InvalidOperationException("As tabelas de uma versão histórica são preservadas. Crie uma revisão para aplicar fatores.");
        string resolved = Resolve(path);
        string[] remaining;
        CompactTable selected;
        using (var reader = new CompactBankReader(resolved))
        {
            selected = reader.Table(sheet);
            remaining = reader.Catalog.Tables.Where(t => t.Id != selected.Id).Select(t => t.Name).ToArray();
        }
        var updated = Data!.DeepClone().AsObject();
        var meta = updated.Obj("_dendro");
        string firstSheet = meta.Arr("processing_results").OfType<JsonObject>().Reverse()
            .Where(r => r.S("path") == path).SelectMany(r => r.Arr("sheets")).FirstOrDefault().Display();
        var removedPaths = DescendantPathsToRemove(updated, path, sheet, remaining, firstSheet).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string bank = CompactBankFile.Location(resolved).File;
        if (Meta.Arr("processing_results").Any(r => r.S("id").Length > 0 && removedPaths.Contains(r.S("path"))))
            throw new InvalidOperationException("A remoção atingiria versões históricas de processamento.");
        string[] descendantDatasets = removedPaths.Select(p => CompactBankFile.Location(Resolve(p)))
            .Where(p => p.Dataset.Length > 0 && string.Equals(p.File, bank, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Dataset).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var result in meta.Arr("processing_results").OfType<JsonObject>().Where(r => r.S("path") == path).ToArray())
        {
            foreach (var value in result.Arr("sheets").Where(s => s.Display() == sheet).ToArray()) result.Arr("sheets").Remove(value);
            if (result.Arr("sheets").Count == 0) meta.Arr("processing_results").Remove(result);
        }
        foreach (string section in new[] { "charts", "content_names", "analyses" })
            foreach (var entry in meta.Arr(section).OfType<JsonObject>().Where(e =>
                e.S("path") == path && (remaining.Length == 0 || e.S("sheet") == sheet || e.At("definition.chart").S("sheet") == sheet)).ToArray())
                meta.Arr(section).Remove(entry);
        foreach (var option in meta.Arr("file_options").OfType<JsonObject>().Where(o => o.S("path") == path).ToArray())
        {
            if (remaining.Length == 0) meta.Arr("file_options").Remove(option);
            else if (option.S("sheet") == sheet) { option["sheet"] = remaining[0]; option["header_row"] = 1; }
        }
        if (remaining.Length == 0)
        {
            foreach (string section in new[] { "files", "batch_inputs" })
                foreach (var value in meta.Arr(section).Where(v => v.Display() == path).ToArray()) meta.Arr(section).Remove(value);
            foreach (var entry in meta.Arr("derived_tables").OfType<JsonObject>().Where(e => e.S("path") == path).ToArray())
                meta.Arr("derived_tables").Remove(entry);
        }
        DeferBankRemoval(bank, [selected.Id], descendantDatasets);
        Replace(updated);
        return Task.CompletedTask;
    }

    private static string[] DescendantPathsToRemove(JsonObject updated, string path, string sheet, string[] remaining, string firstSheet)
    {
        var meta = updated.Obj("_dendro");
        string[] childPaths = new[] { "derived_tables", "processing_results" }
            .SelectMany(section => meta.Arr(section).OfType<JsonObject>())
            .Where(entry => entry.S("source_path") == path && (remaining.Length == 0 ||
                (string.IsNullOrWhiteSpace(entry.S("source_sheet")) ? firstSheet : entry.S("source_sheet")) == sheet))
            .Select(entry => entry.S("path")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var removedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string childPath in childPaths)
        {
            int index = meta.Arr("files").ToList().FindIndex(value =>
                string.Equals(value.Display(), childPath, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                foreach (string removed in ProjectModels.RemoveFile(updated, index)) removedPaths.Add(removed);
        }
        return removedPaths.ToArray();
    }

    public async Task Save(string path)
    {
        await RefreshHistory(true);
        path = Path.ChangeExtension(Path.GetFullPath(path), ".dlb");
        var copy = Data!.DeepClone().AsObject();
        string original = FilePath;
        if (File.Exists(path) && !string.Equals(path, original, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Já existe um arquivo DLB no destino. Escolha outro nome para salvar o projeto.");
        string oldBank = Meta.S("database_path");
        string replacement = Path.GetFileName(path);
        if (original.Length > 0 && !string.Equals(path, original, StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(original)) throw new FileNotFoundException("O arquivo do projeto atual não foi encontrado.", original);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (SharedFileLock.Acquire(original + ".write.lock")) File.Copy(original, temporary);
                File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            void Remap(JsonNode? node)
            {
                string RemapText(string text)
                {
                    string[] roots = [oldBank, Path.GetFileName(original)];
                    string? root = roots.FirstOrDefault(candidate => candidate.Length > 0 && string.Equals(text, candidate, StringComparison.OrdinalIgnoreCase));
                    if (root != null) return replacement;
                    root = roots.FirstOrDefault(candidate => candidate.Length > 0 && (
                        text.StartsWith(candidate + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || text.StartsWith(candidate + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    ));
                    if (root != null) return replacement + text[root.Length..];
                    if (!Path.IsPathRooted(text))
                    {
                        // DLB table references are stored as "container.dlb\\dataset.dlb".
                        // Projects saved through several locations can retain an older
                        // container name, so rebase any relative embedded DLB reference.
                        int separator = text.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
                        if (separator > 0 && text[..separator].EndsWith(".dlb", StringComparison.OrdinalIgnoreCase))
                            return replacement + text[separator..];
                    }
                    return text;
                }
                if (node is JsonObject obj)
                    foreach (var pair in obj.ToArray())
                    {
                        if (pair.Value is JsonValue v && v.TryGetValue<string>(out string? text))
                            obj[pair.Key] = RemapText(text);
                        else Remap(pair.Value);
                    }
                else if (node is JsonArray array)
                    for (int i = 0; i < array.Count; i++)
                        if (array[i] is JsonValue v && v.TryGetValue<string>(out string? text))
                            array[i] = RemapText(text);
                        else Remap(array[i]);
            }
            Remap(copy);
            string? history = CompactBankFile.ReadTextDataset(path, ProcessingCatalog.HistoryDataset);
            if (history != null)
            {
                try { CompactBankFile.UpdateTextDataset(path, ProcessingCatalog.HistoryDataset, current =>
                {
                    var catalog = JsonNode.Parse(current ?? history)!.AsObject();
                    Remap(catalog);
                    catalog["database_path"] = replacement;
                    return catalog.ToJsonString();
                }); }
                catch { File.Delete(path); throw; }
            }
        }
        copy.Obj("_dendro")["database_path"] = replacement;
        try
        {
            var r = await WorkspaceOperations.Run(new() { ["op"] = "save", ["path"] = path, ["project"] = copy });
            FilePath = path; Replace(r["project"]!.AsObject(), false);
        }
        catch { if (original.Length > 0 && path != original && File.Exists(path)) File.Delete(path); throw; }
        await ApplyPendingBankRemovals(original, path);
    }

    public string ProjectReference(string path)
    {
        string root = Meta.S("database_path");
        if (root.Length > 0) {
            if (workFolder != null && Path.GetDirectoryName(Path.GetFullPath(path)) == workFolder)
                return Path.Combine(root, Path.GetFileName(path));
            if (Path.GetFullPath(path).StartsWith(Resolve(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return Path.GetRelativePath(Path.GetDirectoryName(FilePath)!, path);
        }
        return path;
    }
}
