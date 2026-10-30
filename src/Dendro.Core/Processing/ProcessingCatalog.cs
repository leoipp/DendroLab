using System.Text.Json;
using System.Text.Json.Nodes;
using Dendro.Core.Configuration;
using Dendro.Core.Tabular;

namespace Dendro.Core.Processing;

/// <summary>Small shared catalogue. Runs publish data first and then atomically publish the catalogue.</summary>
public static class ProcessingCatalog
{
    public static string FilePath(string project) => Path.GetFullPath(project) + ".history.json";
    public static string User => Environment.UserDomainName + "\\" + Environment.UserName;

    public static JsonObject Read(string project, JsonArray? seed = null)
    {
        string path = FilePath(project);
        if (!File.Exists(path)) return new JsonObject { ["format"] = "DENDRO_PROCESSING_HISTORY", ["version"] = 1, ["revision"] = 0,
            ["records"] = new JsonArray((seed ?? []).OfType<JsonObject>().Where(r => r.S("id").Length > 0).Select(r => (JsonNode?)r.DeepClone()).ToArray()) };
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var catalog = JsonNode.Parse(stream)?.AsObject() ?? throw new InvalidDataException("Catálogo de processamento vazio.");
        if (catalog.S("format") != "DENDRO_PROCESSING_HISTORY" || catalog.N("version") != 1 || catalog["records"] is not JsonArray)
            throw new InvalidDataException("Catálogo de processamento incompatível.");
        return catalog;
    }

    private static JsonObject Update(string project, JsonArray seed, Action<JsonObject> update, CancellationToken cancellation)
    {
        string path = FilePath(project);
        using var lease = SharedFileLock.Acquire(path + ".lock", cancellation);
        var catalog = Read(project, seed);
        update(catalog);
        catalog["revision"] = catalog.N("revision") + 1;
        ProjectStore.AtomicWrite(path, temp => {
            using var stream = File.Create(temp); using var writer = new Utf8JsonWriter(stream);
            catalog.WriteTo(writer); writer.Flush(); stream.Flush(true);
        }, true);
        return catalog;
    }

    public static JsonObject Publish(string project, JsonArray seed, string bank, string stage, JsonObject record,
        CancellationToken cancellation = default, Action<long, long>? progress = null)
    {
        string dataset = Path.GetFileName(record.S("path")); bool imported = false;
        try
        {
            return Update(project, seed, catalog => {
                var records = catalog.Arr("records");
                if (records.Any(r => r.S("id") == record.S("id"))) throw new InvalidDataException("Identificador de processamento já publicado.");
                string family = record.S("family_id");
                int next = records.Where(r => r.S("family_id") == family).Select(r => (int)r.N("number")).DefaultIfEmpty(0).Max() + 1;
                if (record.N("number") != next) throw new InvalidOperationException("Outro usuário publicou uma revisão desta base. Atualize o histórico e gere a nova versão novamente; os fatores preenchidos podem ser reaproveitados.");
                if (next > 1 && !records.Any(r => r.S("id") == record.S("parent_id") && r.S("family_id") == family))
                    throw new InvalidDataException("Versão de origem não encontrada.");
                cancellation.ThrowIfCancellationRequested();
                catalog["database_path"] = Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(project))!, Path.GetFullPath(bank));
                CompactBankFile.Import(bank, [(stage, dataset)], cancellation: cancellation, progress: progress); imported = true;
                records.Add(record.DeepClone());
            }, cancellation);
        }
        catch
        {
            if (imported) CompactBankFile.RemoveDatasets(bank, [dataset]);
            throw;
        }
    }

    public static JsonObject ChangeStatus(string project, JsonArray seed, string id, int expectedRevision, string status, string note = "",
        CancellationToken cancellation = default)
        => Update(project, seed, catalog => {
            if (status is not ("em_analise" or "aprovado")) throw new InvalidDataException("Estado inválido.");
            var record = catalog.Arr("records").OfType<JsonObject>().SingleOrDefault(r => r.S("id") == id)
                ?? throw new InvalidDataException("Processamento não encontrado.");
            if (record.N("revision") != expectedRevision) throw new InvalidOperationException("Este processamento foi atualizado por outro usuário. Atualize o histórico antes de continuar.");
            if (record.S("status") == "aprovado") throw new InvalidOperationException("Uma versão aprovada é preservada. Crie uma revisão para aplicar novos fatores.");
            if (status == "aprovado")
            {
                if (catalog.Arr("records").Any(r => r.S("family_id") == record.S("family_id") && r.B("current_approved") && r.N("number") > record.N("number")))
                    throw new InvalidOperationException("Já existe uma versão posterior aprovada. Crie uma nova revisão para alterar o resultado vigente.");
                foreach (var older in catalog.Arr("records").OfType<JsonObject>().Where(r => r.S("family_id") == record.S("family_id")))
                {
                    if (older.B("current_approved"))
                    {
                        older["current_approved"] = false; older["revision"] = older.N("revision") + 1;
                        older.Arr("audit").Add(new JsonObject { ["status"] = "aprovacao_substituida", ["user"] = User,
                            ["at"] = DateTimeOffset.Now.ToString("O"), ["replaced_by"] = record.S("id") });
                    }
                }
                record["current_approved"] = true; record["approved_by"] = User; record["approved_at"] = DateTimeOffset.Now.ToString("O");
            }
            record["status"] = status; record["revision"] = record.N("revision") + 1;
            record.Arr("audit").Add(new JsonObject { ["status"] = status, ["user"] = User, ["at"] = DateTimeOffset.Now.ToString("O"), ["note"] = note });
        }, cancellation);

    public static JsonObject NewRecord(string source, string inventoryType, string scope, JsonObject? parent = null, int number = 1)
    {
        if (inventoryType is not ("IFC" or "IFPC" or "MISTO") || scope is not ("talhao" or "lote")) throw new InvalidDataException("Tipo de inventário ou nível de fator inválido.");
        string id = Guid.NewGuid().ToString("N");
        return new JsonObject { ["id"] = id, ["family_id"] = parent?.S("family_id") ?? id, ["parent_id"] = parent?.S("id") ?? "",
            ["number"] = number, ["source_path"] = source, ["inventory_type"] = inventoryType, ["factor_scope"] = scope,
            ["created"] = DateTimeOffset.Now.ToString("O"), ["user"] = User, ["machine"] = Environment.MachineName,
            ["status"] = "processado", ["revision"] = 0, ["current_approved"] = false,
            ["audit"] = new JsonArray(new JsonObject { ["status"] = "processado", ["user"] = User, ["at"] = DateTimeOffset.Now.ToString("O") }) };
    }
}
