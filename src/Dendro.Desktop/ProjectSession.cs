using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Dendro.Desktop;

internal sealed partial class ProjectSession : INotifyPropertyChanged
{
    public JsonObject? Data { get; private set; }
    public string FilePath { get; private set; } = "";
    public bool IsDirty { get; private set; }
    public bool IsOpen => Data != null;
    public string Caption =>
        IsOpen
            ? $"{Data!.Obj("_dendro").S("name", "Projeto")}{(IsDirty ? " *" : "")} — DendroLab"
            : "DendroLab";
    public JsonObject Meta =>
        Data?.Obj("_dendro") ?? throw new InvalidOperationException("Abra ou crie um projeto.");
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? Changed;

    public void Replace(JsonObject data, bool dirty = true)
    {
        Dendro.Core.Configuration.RegressionCatalog.Ensure(data);
        Dendro.Core.Configuration.ProjectStore.EnsureInputUnits(data);
        Dendro.Core.Configuration.ProjectStore.EnsureRegistryFallback(data);
        Dendro.Core.Processing.DirectInventorySummary.MigrateConfiguration(data);
        Dendro.Core.Configuration.ProjectStore.MergeDefaults(data.Obj("processador"), Dendro.Core.Processing.DirectInventorySummary.Defaults());
        foreach (string key in new[] { "resultado_arvore", "resumo_bylote" })
            if (data.At("saida.sheets." + key) is null)
                data.Obj("saida").Obj("sheets")[key] = true;
        var registry = data.Obj("cadastro");
        var meta = data.Obj("_dendro");
        if (string.IsNullOrWhiteSpace(registry.S("caminho")))
            registry["caminho"] = meta.S("registry");
        if (meta.ContainsKey("registry") && meta["file_options"] is JsonArray options)
        {
            var selected = options.FirstOrDefault(o =>
                string.Equals(
                    o.S("path"),
                    registry.S("caminho"),
                    StringComparison.OrdinalIgnoreCase
                )
            );
            if (selected != null)
                registry["worksheet"] = selected.S("sheet", registry.S("worksheet", "Data"));
        }
        meta.Remove("registry");
        Data = data;
        IsDirty = dirty;
        Notify();
    }

    public void Touch()
    {
        IsDirty = true;
        Notify();
    }

    private void Notify()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        Changed?.Invoke();
    }

    public async Task New(string name)
    {
        var r = await WorkspaceOperations.Run(new() { ["op"] = "new", ["name"] = name });
        pendingBankRemovals.Clear();
        FilePath = "";
        historyRevision = -1;
        Replace(r["project"]!.AsObject(), true);
    }

    public async Task Open(string path)
    {
        var r = await WorkspaceOperations.Run(new() { ["op"] = "open", ["path"] = path });
        pendingBankRemovals.Clear();
        FilePath = Path.GetFullPath(path);
        Replace(r["project"]!.AsObject(), false);
        historyRevision = -1;
        await RefreshHistory(true);
    }

    public async Task Mutate(JsonObject request)
    {
        request["project"] = Data?.DeepClone();
        var r = await WorkspaceOperations.Run(request);
        Replace(r["project"]!.AsObject());
    }

    public string Resolve(string path) =>
        Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(
                path,
                string.IsNullOrEmpty(FilePath)
                    ? Environment.CurrentDirectory
                    : Path.GetDirectoryName(FilePath)!
            );

    public IEnumerable<string> Files => Meta.Arr("files").Select(f => f!.GetValue<string>());

    public bool IsSpreadsheet(string path) =>
        new[] { ".xls", ".xlsx" }.Contains(
            Path.GetExtension(path),
            StringComparer.OrdinalIgnoreCase
        );

    public JsonObject ExcelOptions(string path)
    {
        var option = Meta.Arr("file_options")
            .OfType<JsonObject>()
            .FirstOrDefault(v =>
                string.Equals(v.S("path"), path, StringComparison.OrdinalIgnoreCase)
            );
        return option
            ?? new()
            {
                ["path"] = path,
                ["sheet"] = "",
                ["header_row"] = 1,
            };
    }

    public void SetExcel(string path, string sheet, int header)
    {
        var a = Meta.Arr("file_options");
        var old = a.OfType<JsonObject>()
            .FirstOrDefault(v =>
                string.Equals(v.S("path"), path, StringComparison.OrdinalIgnoreCase)
            );
        if (old != null)
            a.Remove(old);
        a.Add(
            new JsonObject
            {
                ["path"] = path,
                ["sheet"] = sheet,
                ["header_row"] = header,
            }
        );
        Touch();
    }

    public void AddFile(string path)
    {
        if (!Files.Contains(path, StringComparer.OrdinalIgnoreCase))
            Meta.Arr("files").Add(path);
        Touch();
    }

    public void RelinkFile(string oldPath, string newPath)
    {
        if (!File.Exists(newPath))
            throw new FileNotFoundException("O arquivo selecionado não existe.", newPath);
        if (IsSpreadsheet(oldPath) != IsSpreadsheet(newPath))
            throw new InvalidOperationException("Selecione uma planilha Excel para substituir esta referência.");
        static string Key(string path) => path.Replace('/', '\\');
        string oldKey = Key(oldPath);
        bool Matches(string value) => string.Equals(Key(value), oldKey, StringComparison.OrdinalIgnoreCase);
        if (!Files.Any(Matches))
            throw new InvalidOperationException("A referência ausente não pertence mais a este projeto.");
        void ReplaceReferences(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                foreach (var pair in obj.ToArray())
                    if (pair.Value is JsonValue value && value.TryGetValue<string>(out string? text) && Matches(text))
                        obj[pair.Key] = newPath;
                    else
                        ReplaceReferences(pair.Value);
            }
            else if (node is JsonArray array)
            {
                for (int index = 0; index < array.Count; index++)
                    if (array[index] is JsonValue value && value.TryGetValue<string>(out string? text) && Matches(text))
                        array[index] = newPath;
                    else
                        ReplaceReferences(array[index]);
            }
        }
        ReplaceReferences(Data);
        Touch();
    }

    public bool IsTabular(string path) =>
        IsSpreadsheet(path) || Dendro.Core.Tabular.TableStore.IsBank(path);

    public string ContentName(string path, JsonObject? chart = null)
    {
        var names = Meta["content_names"] as JsonArray;
        var found = names?.OfType<JsonObject>().FirstOrDefault(n => NameMatches(n, path, chart));
        return found?.S("name") ?? (chart == null ? (IsManagedTable(path) ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path)) : chart.S("title"));
    }

    private static bool NameMatches(JsonObject entry, string path, JsonObject? chart) =>
        string.Equals(entry.S("path"), path, StringComparison.OrdinalIgnoreCase)
        && entry.S("kind") == (chart == null ? "file" : "chart")
        && (
            chart == null
            || entry.S("sheet") == chart.S("sheet") && entry.S("title") == chart.S("title")
        );

    public void RenameContent(string path, string name, JsonObject? chart = null)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 120 || name.Any(char.IsControl))
            throw new InvalidOperationException(
                "Informe um nome de 1 a 120 caracteres, sem quebras de linha."
            );
        var names = Meta.Arr("content_names");
        var found = names.OfType<JsonObject>().FirstOrDefault(n => NameMatches(n, path, chart));
        if (found == null)
        {
            found = new() { ["path"] = path, ["kind"] = chart == null ? "file" : "chart" };
            if (chart != null)
            {
                found["sheet"] = chart.S("sheet");
                found["title"] = chart.S("title");
            }
            names.Add(found);
        }
        found["name"] = name;
        Touch();
    }

    public Task AddTableResult(Dendro.Core.Tabular.TableResult result, string? parent = null)
        => AddTableResults([result], parent);

    public async Task AddTableResults(IEnumerable<Dendro.Core.Tabular.TableResult> results, string? parent = null, string? sourceSheet = null)
    {
        ProjectWorkFolder();
        var batch = results.ToArray();
        await Task.Run(() => Dendro.Core.Tabular.CompactBankFile.Import(Resolve(Meta.S("database_path")),
            batch.Select(r => (r.Path, Path.GetFileName(r.Path))).ToArray()));
        var files = Meta.Arr("files");
        var known = Files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var options = Meta.Arr("file_options");
        foreach (var result in batch)
        {
            string path = Path.Combine(Meta.S("database_path"), Path.GetFileName(result.Path));
            if (known.Add(path)) files.Add(path);
            var previous = options.OfType<JsonObject>().FirstOrDefault(o => string.Equals(o.S("path"), path, StringComparison.OrdinalIgnoreCase));
            if (previous != null) options.Remove(previous);
            options.Add(new JsonObject { ["path"] = path, ["sheet"] = result.Table, ["header_row"] = 1 });
            if (parent != null)
                Meta.Arr("derived_tables").Add(new JsonObject { ["path"] = path, ["source_path"] = parent, ["source_sheet"] = sourceSheet });
            if (Path.GetDirectoryName(Path.GetFullPath(result.Path)) == workFolder) File.Delete(result.Path);
        }
        Touch();
    }

    public Dendro.Core.Tabular.TableSource[] TableSources(IEnumerable<string> paths)
    {
        var options = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Meta.Arr("file_options").OfType<JsonObject>()) options.TryAdd(entry.S("path"), entry);
        return paths.Select(path => {
            options.TryGetValue(path, out var option);
            return new Dendro.Core.Tabular.TableSource(Resolve(path), option.S("sheet"), (int)option.N("header_row", 1));
        }).ToArray();
    }

    public JsonObject ProcessingSnapshot()
    {
        var copy = Dendro.Core.Processing.ProcessingHistory.Freeze(Data!);
        var meta = copy.Obj("_dendro");
        foreach (var option in meta.Arr("file_options").OfType<JsonObject>())
            option["path"] = Resolve(option.S("path"));
        foreach (var key in new[] { "input" })
            if (!string.IsNullOrWhiteSpace(meta.S(key)))
                meta[key] = Resolve(meta.S(key));
        meta.Remove("registry");
        if (!string.IsNullOrWhiteSpace(RegistryPath))
            copy.Obj("cadastro")["caminho"] = Resolve(RegistryPath);
        void ResolveModels(JsonNode? node)
        {
            if (node is JsonObject obj)
                foreach (var pair in obj.ToArray())
                {
                    if (
                        pair.Key == "caminho"
                        && pair.Value is JsonValue v
                        && v.TryGetValue<string>(out string? path)
                        && !string.IsNullOrWhiteSpace(path)
                    )
                        obj[pair.Key] = Resolve(path);
                    else
                        ResolveModels(pair.Value);
                }
            else if (node is JsonArray a)
                foreach (var child in a)
                    ResolveModels(child);
        }
        ResolveModels(copy["ajuste_altura"]);
        return copy;
    }

    public string RegistryPath => Data?["cadastro"].S("caminho") ?? "";

    public JsonObject ChartKey(JsonObject chart) =>
        new()
        {
            ["path"] = chart.S("path"),
            ["sheet"] = chart.S("sheet"),
            ["title"] = chart.S("title"),
        };

    public int StoreChart(JsonObject chart)
    {
        var a = Meta.Arr("charts");
        int index = -1;
        for (int i = 0; i < a.Count; i++)
            if (
                a[i]!.S("title") == chart.S("title")
                && a[i]!.S("sheet") == chart.S("sheet")
                && string.Equals(
                    a[i]!.S("path"),
                    chart.S("path"),
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                index = i;
                break;
            }
        if (index < 0)
        {
            index = a.Count;
            a.Add(chart.DeepClone());
        }
        else
            a[index] = chart.DeepClone();
        Touch();
        return index;
    }
}
