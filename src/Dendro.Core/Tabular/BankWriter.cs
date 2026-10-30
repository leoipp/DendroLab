using LiteDB;

namespace Dendro.Core.Tabular;

/// <summary>Batched disk-backed staging; no worksheet-sized row list is retained.</summary>
internal sealed class BankWriter : IDisposable
{
    private readonly LiteDatabase db;
    private readonly Dictionary<string, (string[] Columns, long Count)> tables = new();
    private int pending;
    private bool committed;

    public BankWriter(string path)
    {
        db = new LiteDatabase(new ConnectionString { Filename = path });
        db.GetCollection<BsonDocument>("dendro_info")
            .Insert(new BsonDocument { ["_id"] = "format", ["value"] = "DENDRO_TABLES_1" });
        db.BeginTrans();
    }

    public string AddTable(string[] columns)
    {
        string id = "t" + tables.Count;
        tables.Add(id, (columns, 0));
        return id;
    }

    public void Add(string id, object?[] row)
    {
        var state = tables[id];
        long count = state.Count + 1;
        var doc = new BsonDocument { ["_id"] = count };
        for (int i = 0; i < state.Columns.Length; i++)
            doc["c" + i] = row[i] switch
            {
                null => BsonValue.Null,
                double n when double.IsFinite(n) => new BsonValue(n),
                double => BsonValue.Null,
                var v => new BsonValue(v),
            };
        db.GetCollection<BsonDocument>(id).Insert(doc);
        tables[id] = (state.Columns, count);
        if (++pending >= 2000)
        {
            db.Commit();
            db.BeginTrans();
            pending = 0;
        }
    }

    public long Count(string id) => tables[id].Count;

    public void Complete()
    {
        var catalog = db.GetCollection<BsonDocument>("dendro_tables");
        foreach (var (id, state) in tables)
            catalog.Insert(
                new BsonDocument
                {
                    ["_id"] = id,
                    ["name"] = id == "t0" ? "Dados" : id,
                    ["collection"] = id,
                    ["columns"] = new BsonArray(state.Columns.Select(c => new BsonValue(c))),
                    ["rows"] = state.Count,
                }
            );
        db.Commit();
        db.Checkpoint();
        committed = true;
    }

    public void Dispose()
    {
        if (!committed)
            db.Rollback();
        db.Dispose();
    }
}
