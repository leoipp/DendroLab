using System.Text.Json;
using System.Text.Json.Nodes;
using Dendro.Core.Configuration;
using Dendro.Core.Tabular;

namespace Dendro.Core.Processing;

/// <summary>Small shared catalogue. Runs publish data first and then atomically publish the catalogue.</summary>
public static class ProcessingCatalog
{
    public const string HistoryDataset = "__DENDRO_HISTORY__.json";
    public static string FilePath(string project) => Path.GetFullPath(project);
    public static string User => Environment.UserDomainName + "\\" + Environment.UserName;

    public static JsonObject Read(string project, JsonArray? seed = null)
    {
        string? text = CompactBankFile.ReadTextDataset(project, HistoryDataset);
        if (text == null) return Empty(seed);
        var catalog = JsonNode.Parse(text)?.AsObject() ?? throw new InvalidDataException("Catálogo de processamento vazio.");
        if (catalog.S("format") != "DENDRO_PROCESSING_HISTORY" || catalog.N("version") != 1 || catalog["records"] is not JsonArray)
            throw new InvalidDataException("Catálogo de processamento incompatível.");
        return catalog;
    }

    private static JsonObject Empty(JsonArray? seed) => new() { ["format"] = "DENDRO_PROCESSING_HISTORY", ["version"] = 1, ["revision"] = 0,
        ["records"] = new JsonArray((seed ?? []).OfType<JsonObject>().Where(r => r.S("id").Length > 0).Select(r => (JsonNode?)r.DeepClone()).ToArray()) };

    private static JsonObject Update(string project, JsonArray seed, Action<JsonObject> update, CancellationToken cancellation,
        IReadOnlyList<(string Source, string Dataset)>? incoming = null, IReadOnlyCollection<string>? removeDatasets = null,
        Action<long, long>? progress = null)
    {
        JsonObject? updated = null;
        CompactBankFile.UpdateTextDataset(project, HistoryDataset, text => {
            var catalog = text == null ? Empty(seed) : JsonNode.Parse(text)?.AsObject()
                ?? throw new InvalidDataException("Catálogo de processamento vazio.");
            if (catalog.S("format") != "DENDRO_PROCESSING_HISTORY" || catalog.N("version") != 1 || catalog["records"] is not JsonArray)
                throw new InvalidDataException("Catálogo de processamento incompatível.");
            update(catalog);
            catalog["revision"] = catalog.N("revision") + 1;
            updated = catalog.DeepClone().AsObject();
            return catalog.ToJsonString();
        }, incoming, removeDatasets, cancellation, progress);
        return updated!;
    }

    public static JsonObject Publish(string project, JsonArray seed, string bank, string stage, JsonObject record,
        CancellationToken cancellation = default, Action<long, long>? progress = null)
    {
        if (!string.Equals(Path.GetFullPath(project), Path.GetFullPath(bank), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Os resultados e o histórico devem ser publicados no arquivo DLB único do projeto.");
        string dataset = Path.GetFileName(record.S("path"));
        return Update(project, seed, catalog => {
                var records = catalog.Arr("records");
                if (records.Any(r => r.S("id") == record.S("id"))) throw new InvalidDataException("Identificador de processamento já publicado.");
                string family = record.S("family_id");
                var familyRecords = records.OfType<JsonObject>().Where(r => r.S("family_id") == family).ToArray();
                int next = familyRecords.Select(r => (int)r.N("number")).DefaultIfEmpty(0).Max() + 1;
                if (record.N("number") != next) throw new InvalidOperationException("Outro usuário publicou uma revisão desta base. Atualize o histórico e gere a nova versão novamente; os fatores preenchidos podem ser reaproveitados.");
                if (next > 1)
                {
                    var latest = familyRecords.SingleOrDefault(r => r.N("number") == next - 1);
                    if (latest == null || latest.S("id") != record.S("parent_id"))
                        throw new InvalidOperationException("Crie a revisão a partir da versão mais recente para manter uma única linha do tempo.");
                    if (record.N("parent_revision", -1) != latest.N("revision"))
                        throw new InvalidOperationException("A versão de origem foi atualizada por outra pessoa. Atualize o histórico e prepare a revisão novamente.");
                    if (latest.S("status") is not ("em_revisao" or "ajustes_solicitados" or "em_analise"))
                        throw new InvalidOperationException("A revisão só pode ser criada durante uma revisão ativa ou após uma solicitação de ajustes.");
                }
                cancellation.ThrowIfCancellationRequested();
                catalog["database_path"] = Path.GetFileName(project);
                records.Add(record.DeepClone());
            }, cancellation, [(stage, dataset)], progress: progress);
    }

    public static JsonObject ChangeStatus(string project, JsonArray seed, string id, int expectedRevision, string status, string note = "",
        CancellationToken cancellation = default)
        => Update(project, seed, catalog => {
            if (status is not ("em_revisao" or "ajustes_solicitados" or "aprovado")) throw new InvalidDataException("Estado inválido.");
            var record = catalog.Arr("records").OfType<JsonObject>().SingleOrDefault(r => r.S("id") == id)
                ?? throw new InvalidDataException("Processamento não encontrado.");
            if (record.N("revision") != expectedRevision) throw new InvalidOperationException("Este processamento foi atualizado por outro usuário. Atualize o histórico antes de continuar.");
            if (record.S("status") == "aprovado") throw new InvalidOperationException("Uma versão aprovada é preservada. Crie uma revisão para aplicar novos fatores.");
            string currentStatus = record.S("status");
            bool legacyAwaitingReview = currentStatus is "processado" or "em_analise";
            if (status == "em_revisao" && currentStatus is not ("aguardando_revisao" or "ajustes_solicitados") && !legacyAwaitingReview)
                throw new InvalidOperationException("Somente uma versão aguardando revisão ou devolvida pode entrar em revisão.");
            if (status == "ajustes_solicitados")
            {
                if (currentStatus != "em_revisao" && currentStatus != "em_analise")
                    throw new InvalidOperationException("Inicie a revisão antes de solicitar ajustes.");
                if (string.IsNullOrWhiteSpace(note)) throw new InvalidOperationException("Informe o motivo para devolver esta versão para ajustes.");
            }
            if (status == "aprovado")
            {
                if (currentStatus != "em_revisao" && !legacyAwaitingReview)
                    throw new InvalidOperationException("Inicie a revisão antes de aprovar a versão.");
                int latestNumber = catalog.Arr("records").OfType<JsonObject>()
                    .Where(r => r.S("family_id") == record.S("family_id"))
                    .Select(r => (int)r.N("number")).DefaultIfEmpty(0).Max();
                if (record.N("number") != latestNumber)
                    throw new InvalidOperationException("Apenas a versão mais recente pode ser aprovada. Revise a versão pendente mais nova.");
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

    public static JsonObject RemoveRevision(string project, JsonArray seed, string bank, string id, CancellationToken cancellation = default)
    {
        if (!string.Equals(Path.GetFullPath(project), Path.GetFullPath(bank), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("O histórico e os dados devem estar no arquivo DLB único do projeto.");
        string dataset = Path.GetFileName(seed.OfType<JsonObject>().FirstOrDefault(r => r.S("id") == id)?.S("path") ?? "");
        var catalog = Update(project, seed, value => {
            var records = value.Arr("records").OfType<JsonObject>().ToArray();
            var record = records.SingleOrDefault(r => r.S("id") == id)
                ?? throw new InvalidDataException("Revisão de inventário não encontrada.");
            if (record.S("status") == "aprovado" || record.B("current_approved"))
                throw new InvalidOperationException("Uma versão aprovada é preservada e não pode ser removida.");
            if (records.Any(r => r.S("parent_id") == id))
                throw new InvalidOperationException("Esta versão tem revisões posteriores. Remova primeiro as revisões descendentes.");
            records = records.Where(r => r.S("id") != id).ToArray();
            value["records"] = new JsonArray(records.Select(r => (JsonNode?)r.DeepClone()).ToArray());
        }, cancellation, removeDatasets: dataset.Length > 0 ? [dataset] : []);
        return catalog;
    }

    public static JsonObject NewRecord(string source, string inventoryType, string scope, JsonObject? parent = null, int number = 1)
    {
        if (inventoryType is not ("IFC" or "IFPC" or "MISTO") || scope is not ("talhao" or "lote")) throw new InvalidDataException("Tipo de inventário ou nível de fator inválido.");
        string id = Guid.NewGuid().ToString("N");
        return new JsonObject { ["id"] = id, ["family_id"] = parent?.S("family_id") ?? id, ["parent_id"] = parent?.S("id") ?? "",
            ["number"] = number, ["parent_revision"] = parent?.N("revision") ?? -1, ["source_path"] = source, ["inventory_type"] = inventoryType, ["factor_scope"] = scope,
            ["created"] = DateTimeOffset.Now.ToString("O"), ["user"] = User, ["machine"] = Environment.MachineName,
            ["status"] = "aguardando_revisao", ["revision"] = 0, ["current_approved"] = false,
            ["factor_method"] = "manual",
            ["audit"] = new JsonArray(new JsonObject { ["status"] = "aguardando_revisao", ["user"] = User, ["at"] = DateTimeOffset.Now.ToString("O") }) };
    }
}
