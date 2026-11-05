using Dendro.Core.Tabular;

namespace Dendro.Desktop;

/// <summary>Reads each physical DLB catalog once while a tool enumerates project tables.</summary>
internal sealed class ProjectTableCatalogCache
{
    private readonly Dictionary<string, CompactTable[]> bankCatalogs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string[]> pathTables = new(StringComparer.OrdinalIgnoreCase);

    public string[] Tables(ProjectSession session, string path)
    {
        if (pathTables.TryGetValue(path, out var cached)) return cached;

        string resolved = session.Resolve(path);
        var location = CompactBankFile.Location(resolved);
        string[] tables;
        if (location.Dataset.Length == 0 && !CompactBankFile.IsCompactBank(location.File))
            tables = TableStore.Tables(resolved);
        else
        {
            if (!bankCatalogs.TryGetValue(location.File, out var catalog))
            {
                using var reader = new CompactBankReader(location.File);
                catalog = reader.Catalog.Tables;
                bankCatalogs.Add(location.File, catalog);
            }
            tables = catalog
                .Where(table => location.Dataset.Length == 0 || table.Dataset == location.Dataset)
                .Select(table => table.Name)
                .Where(name => !name.StartsWith(Dendro.Core.Processing.ProcessingHistory.InternalPrefix, StringComparison.Ordinal))
                .ToArray();
        }

        pathTables[path] = tables;
        return tables;
    }
}
