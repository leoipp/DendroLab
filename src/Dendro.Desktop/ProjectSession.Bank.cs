using System.IO;
using System.Text.Json.Nodes;
using Dendro.Core.Configuration;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal sealed partial class ProjectSession
{
    private string? workFolder;
    public bool IsManagedTable(string path) => Meta.S("database_path").Length > 0
        && Resolve(path).StartsWith(Resolve(Meta.S("database_path")) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    // Work files live outside the project and are removed after being committed to its single bank.
    public string ProjectWorkFolder()
    {
        if (FilePath.Length == 0) throw new InvalidOperationException("Salve o projeto antes de gerar tabelas no banco do projeto.");
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

    public async Task RemoveFile(int index)
    {
        string selected = Files.ElementAt(index);
        if (Meta.Arr("processing_results").Any(r => r.S("id").Length > 0 && (r.S("path") == selected || r.S("source_path") == selected)))
            throw new InvalidOperationException("Esta tabela possui histórico de processamento preservado e não pode ser removida.");
        var updated = Data!.DeepClone().AsObject();
        string[] removed = ProjectModels.RemoveFile(updated, index);
        if (Meta.Arr("processing_results").Any(r => r.S("id").Length > 0 && removed.Any(p => string.Equals(p.Replace('/', '\\'), r.S("path").Replace('/', '\\'), StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("A remoção atingiria versões históricas de processamento. O histórico precisa ser preservado.");
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
                await Task.Run(() => CompactBankFile.RemoveDatasets(bank, datasets));
        }
        Replace(updated);
    }

    public async Task RemoveResultTable(string path, string sheet)
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
        var meta = updated.Obj("_dendro");
        string firstSheet = meta.Arr("processing_results").OfType<JsonObject>().Reverse()
            .Where(r => r.S("path") == path).SelectMany(r => r.Arr("sheets")).FirstOrDefault().Display();
        string[] childPaths = new[] { "derived_tables", "processing_results" }
            .SelectMany(section => meta.Arr(section).OfType<JsonObject>())
            .Where(e => e.S("source_path") == path && (remaining.Length == 0 ||
                (string.IsNullOrWhiteSpace(e.S("source_sheet")) ? firstSheet : e.S("source_sheet")) == sheet))
            .Select(e => e.S("path")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var removedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string childPath in childPaths)
        {
            int index = meta.Arr("files").ToList().FindIndex(value =>
                string.Equals(value.Display(), childPath, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                foreach (string removed in ProjectModels.RemoveFile(updated, index)) removedPaths.Add(removed);
        }
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
        await Task.Run(() => CompactBankFile.RemoveTable(new(resolved, sheet), descendantDatasets));
        Replace(updated);
    }

    private async Task MigrateBank()
    {
        string relative = Meta.S("database_path");
        if (relative.Length == 0 || !Directory.Exists(Resolve(relative))) return;
        string original = Resolve(relative);
        string marker = Path.Combine(original, "bank.json");
        if (!File.Exists(marker) || JsonNode.Parse(File.ReadAllText(marker)).S("format") != "DENDROLAB_GEODATABASE")
            throw new InvalidDataException("A pasta não contém um banco de projeto DendroLab válido.");
        if (Directory.EnumerateDirectories(original).Any()) throw new InvalidDataException("Estrutura inesperada no banco do projeto.");
        var inputs = Directory.GetFiles(original, "*.dlb").Select(p => (Source: p, Dataset: Path.GetFileName(p))).ToArray();
        string temp = original + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string backup = original + ".legacy-backup-" + Guid.NewGuid().ToString("N");
        try {
            if (inputs.Length > 0) await Task.Run(() => CompactBankFile.Import(temp, inputs));
            if (!Path.GetFullPath(original).StartsWith(Path.GetDirectoryName(FilePath)! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Banco antigo fora da pasta do projeto; copie-o para junto do projeto antes de converter.");
            Directory.Move(original, backup);
            try { if (inputs.Length > 0) File.Move(temp, original); }
            catch { Directory.Move(backup, original); throw; }
        } finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public async Task Save(string path)
    {
        await RefreshHistory(true);
        path = Path.GetFullPath(path);
        var copy = Data!.DeepClone().AsObject();
        string oldBank = Meta.S("database_path"), original = oldBank.Length > 0 ? Resolve(oldBank) : "";
        string? copiedBank = null;
        if (!File.Exists(original) && !Directory.Exists(original) && oldBank.Length > 0) copy.Obj("_dendro")["database_path"] = Path.GetFileNameWithoutExtension(path) + ".dlb";
        if (File.Exists(original) || Directory.Exists(original)) {
            string target = Path.ChangeExtension(path, ".dlb");
            if (!string.Equals(target, original, StringComparison.OrdinalIgnoreCase)) {
                if (File.Exists(target) || Directory.Exists(target)) throw new IOException("O banco do projeto de destino já existe. Escolha outro nome para Salvar como.");
                await Task.Run(() => {
                    if (File.Exists(original)) {
                        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        try { File.Copy(original, temporary); File.Move(temporary, target); }
                        finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    }
                    else {
                        string marker = Path.Combine(original, "bank.json");
                        if (!File.Exists(marker) || JsonNode.Parse(File.ReadAllText(marker)).S("format") != "DENDROLAB_GEODATABASE")
                            throw new InvalidDataException("Banco antigo inválido.");
                        CompactBankFile.Import(target, Directory.GetFiles(original, "*.dlb").Select(p => (p, Path.GetFileName(p))).ToArray());
                    }
                });
                copiedBank = target;
                string replacement = Path.GetFileName(target);
                string RemapText(string text) {
                    foreach (string prefix in new[] { oldBank, original }) {
                        if (string.Equals(text, prefix, StringComparison.OrdinalIgnoreCase)) return replacement;
                        if (text.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                            || text.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) return replacement + text[prefix.Length..];
                    }
                    return text;
                }
                void Remap(JsonNode? node) {
                    if (node is JsonObject obj)
                        foreach (var pair in obj.ToArray()) {
                            if (pair.Value is JsonValue v && v.TryGetValue<string>(out var text)) obj[pair.Key] = RemapText(text);
                            else Remap(pair.Value);
                        }
                    else if (node is JsonArray a)
                        for (int i = 0; i < a.Count; i++) {
                            if (a[i] is JsonValue v && v.TryGetValue<string>(out var text)) a[i] = RemapText(text);
                            else Remap(a[i]);
                        }
                }
                Remap(copy);
            }
        }
        try {
            var r = await WorkspaceOperations.Run(new() { ["op"] = "save", ["path"] = path, ["project"] = copy });
            FilePath = path; Replace(r["project"]!.AsObject(), false);
        } catch { if (copiedBank != null) File.Delete(copiedBank); throw; }
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
