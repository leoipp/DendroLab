using System.Text.Json;
using LiteDB;

namespace Dendro.Core.Auditing;

/// <summary>
/// Local append-only evidence ledger. It never opens, edits, or embeds records in a .dlb project.
/// This is persistence and approval tracking only; it does not claim to run AI agents.
/// </summary>
public sealed class AuditLedger : IDisposable
{
    private readonly LiteDatabase database;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public AuditLedger(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        database = new LiteDatabase(new ConnectionString { Filename = fullPath });
        database.GetCollection<BsonDocument>("audit_runs").EnsureIndex("startedAt");
        database.GetCollection<BsonDocument>("audit_findings").EnsureIndex("identifiedAt");
        database.GetCollection<BsonDocument>("audit_recommendations").EnsureIndex("updatedAt");
        database.GetCollection<BsonDocument>("audit_events").EnsureIndex("at");
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DENDRO", "audit-ledger.db");

    public void AddRun(AuditRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        Insert("audit_runs", run.Id, run);
    }

    public void AddFinding(AuditFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (finding.Confidence is < 0 or > 1 || !double.IsFinite(finding.Confidence))
            throw new ArgumentOutOfRangeException(nameof(finding), "A confiança deve estar entre 0 e 1.");
        Insert("audit_findings", finding.Id, finding);
    }

    public void AddRecommendation(AuditRecommendation recommendation, string actor = "Administrador")
    {
        ArgumentNullException.ThrowIfNull(recommendation);
        ValidateState(recommendation.Status);
        using var transaction = BeginTransaction();
        Insert("audit_recommendations", recommendation.Id, recommendation);
        AddEvent(new AuditEvent(Guid.NewGuid().ToString("N"), "Recomendação", recommendation.Id,
            "Criada", "", recommendation.Status, actor, recommendation.Title, recommendation.CreatedAt));
        transaction.Commit();
    }

    public IReadOnlyList<AuditRun> GetRuns(int limit = 100) => Read<AuditRun>("audit_runs", limit, "startedAt");
    public IReadOnlyList<AuditFinding> GetFindings(int limit = 500) => Read<AuditFinding>("audit_findings", limit, "identifiedAt");
    public IReadOnlyList<AuditRecommendation> GetRecommendations(int limit = 500) => Read<AuditRecommendation>("audit_recommendations", limit, "updatedAt");
    public IReadOnlyList<AuditEvent> GetEvents(int limit = 1000) => Read<AuditEvent>("audit_events", limit, "at");

    public AuditRecommendation SetRecommendationState(string id, string newState, string actor, string details = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ValidateState(newState);
        using var transaction = BeginTransaction();
        var collection = database.GetCollection<BsonDocument>("audit_recommendations");
        var document = collection.FindById(id) ?? throw new KeyNotFoundException($"Recomendação '{id}' não encontrada.");
        var previous = Deserialize<AuditRecommendation>(document["json"].AsString);
        var updated = previous with { Status = newState, UpdatedAt = DateTimeOffset.UtcNow };
        document["json"] = Serialize(updated);
        document["updatedAt"] = updated.UpdatedAt.UtcDateTime;
        collection.Update(document);
        AddEvent(new AuditEvent(Guid.NewGuid().ToString("N"), "Recomendação", id, "Estado alterado",
            previous.Status, newState, actor, details, updated.UpdatedAt));
        transaction.Commit();
        return updated;
    }

    private LiteTransaction BeginTransaction()
    {
        if (!database.BeginTrans()) throw new InvalidOperationException("Não foi possível iniciar a transação da auditoria.");
        return new LiteTransaction(database);
    }

    private void Insert<T>(string collectionName, string id, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var doc = new BsonDocument { ["_id"] = id, ["json"] = Serialize(value) };
        switch (value)
        {
            case AuditRun run: doc["startedAt"] = run.StartedAt.UtcDateTime; break;
            case AuditFinding finding: doc["identifiedAt"] = finding.IdentifiedAt.UtcDateTime; break;
            case AuditRecommendation recommendation: doc["updatedAt"] = recommendation.UpdatedAt.UtcDateTime; break;
        }
        database.GetCollection<BsonDocument>(collectionName).Insert(doc);
    }

    private void AddEvent(AuditEvent auditEvent)
    {
        database.GetCollection<BsonDocument>("audit_events").Insert(new BsonDocument
        {
            ["_id"] = auditEvent.Id,
            ["at"] = auditEvent.At.UtcDateTime,
            ["json"] = Serialize(auditEvent)
        });
    }

    private IReadOnlyList<T> Read<T>(string collectionName, int limit, string dateField)
    {
        if (limit is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(limit));
        return database.GetCollection<BsonDocument>(collectionName)
            .FindAll().OrderByDescending(document => document[dateField].AsDateTime)
            .Take(limit).Select(document => Deserialize<T>(document["json"].AsString)).ToArray();
    }

    private static string Serialize<T>(T value) => System.Text.Json.JsonSerializer.Serialize(value, JsonOptions);
    private static T Deserialize<T>(string value) => System.Text.Json.JsonSerializer.Deserialize<T>(value, JsonOptions)
        ?? throw new InvalidDataException("Registro de auditoria inválido.");
    private static void ValidateState(string state)
    {
        if (!AuditStates.All.Contains(state)) throw new ArgumentException("Estado de auditoria desconhecido.", nameof(state));
    }

    public void Dispose() => database.Dispose();

    private sealed class LiteTransaction(LiteDatabase database) : IDisposable
    {
        private bool committed;
        public void Commit()
        {
            database.Commit();
            committed = true;
        }
        public void Dispose()
        {
            if (!committed) database.Rollback();
        }
    }
}
