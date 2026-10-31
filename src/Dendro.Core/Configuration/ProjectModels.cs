using System.Text.Json.Nodes;

namespace Dendro.Core.Configuration;

public static class ProjectModels
{
    public static JsonObject Metadata(JsonObject? project)
    {
        var growth = new JsonArray(
            ModelDefinition
                .Growth.Select(g =>
                    (JsonNode?)
                        new JsonObject
                        {
                            ["id"] = g.Id,
                            ["name"] = g.Name,
                            ["formula"] = g.Formula,
                            ["unit"] = "meses",
                        }
                )
                .ToArray()
        );
        foreach (var d in project?["curvas_crescimento"]?["modelos"] as JsonArray ?? [])
            growth.Add(
                new JsonObject
                {
                    ["id"] = d.S("id"),
                    ["name"] = d.S("nome"),
                    ["formula"] = d.S("formula"),
                    ["unit"] = d.S("unidade_idade", "meses"),
                }
            );
        return new()
        {
            ["fields"] = ProjectStore.Fields,
            ["growth"] = growth,
            ["models"] = new JsonArray(
                (project == null
                    ? Regression.RegressionEngine.Models.Select(m => (m, predictors: 1))
                    : Enumerable.Range(1, 3).SelectMany(predictors =>
                        RegressionCatalog.Families(project, predictors)
                            .Select(m => (m, predictors))))
                    .Select(item => (JsonNode?)new JsonObject
                    {
                        ["name"] = item.m.Name,
                        ["formula"] = item.m.Formula,
                        ["parameters"] = item.m.Parameters,
                        ["id"] = item.m.Id,
                        ["predictors"] = item.predictors,
                    })
                    .ToArray()
            ),
        };
    }

    public static void SetVolume(
        JsonObject project,
        int index,
        string id,
        double b0,
        double b1,
        double b2,
        string name,
        string formula,
        JsonNode? inputs,
        string? volumeType = null
    )
    {
        var models = project.Obj("volume").Arr("equacoes");
        Index(models, index, true);
        string type = volumeType ?? (index >= 0 ? models[index].S("tipo_volume", "VTCC") : "VTCC");
        if (type is not ("VTCC" or "VCCC")) throw new InvalidDataException("Tipo de volume inválido. Use VTCC ou VCCC.");
        if (models.Where((_, i) => i != index).Any(e => e.S("codigo") == id && e.S("tipo_volume", "VTCC") == type))
            throw new InvalidDataException("Já existe um modelo com esse ID e tipo de volume.");
        var model = index >= 0 ? models[index]!.DeepClone().AsObject() : new JsonObject();
        model["codigo"] = id;
        model["tipo_volume"] = type;
        model["b0"] = b0;
        model["b1"] = b1;
        model["b2"] = b2;
        if (formula.Length > 0)
        {
            model["formula"] = formula;
            model["modelo_nome"] = name;
            model["entradas"] = inputs?.DeepClone();
            if (model["entradas"] is JsonArray a)
                foreach (var input in a)
                    input?.AsObject().Remove("agregacao");
        }
        ProjectStore.ValidateCoefficients(model);
        ModelDefinition.Validate(model, false);
        if (index < 0)
            models.Add(model);
        else
            models[index] = model;
    }

    public static void SetGrowth(JsonObject project, JsonObject request)
    {
        int index = (int)request.N("index", -1);
        var section = project.Obj("curvas_crescimento");
        var models = section.Arr("equacoes");
        Index(models, index, true);
        if (request["definition"] is JsonObject definition)
        {
            string id = definition.S("id");
            if (id.Length is < 1 or > 63 || ModelDefinition.Growth.Any(g => g.Id == id))
                throw new InvalidDataException(
                    "Use outro ID; este identifica um modelo predefinido."
                );
            var d = new JsonObject
            {
                ["id"] = id,
                ["nome"] = definition.S("name"),
                ["formula"] = definition.S("formula"),
                ["unidade_idade"] = definition.S("unit"),
                ["entradas"] = definition["inputs"]?.DeepClone(),
            };
            ModelDefinition.Validate(d, true);
            var definitions = section.Arr("modelos");
            int found = definitions.ToList().FindIndex(v => v.S("id") == id);
            if (found < 0)
                definitions.Add(d);
            else
                definitions[found] = d;
        }
        string code = request.S("id"),
            family = request.S("model");
        var builtin = ModelDefinition.Growth.FirstOrDefault(g => g.Id == family);
        var custom = (section["modelos"] as JsonArray ?? []).FirstOrDefault(d =>
            d.S("id") == family
        );
        if (builtin is null && custom is null)
            throw new InvalidDataException(
                "Cadastre a definição do modelo antes de salvar seus coeficientes."
            );
        var row = new JsonObject
        {
            ["codigo"] = code,
            ["modelo"] = family,
            ["ano"] = request.N("year"),
            ["b0"] = request.N("b0"),
            ["b1"] = request.N("b1"),
            ["b2"] = request.N("b2"),
            ["vigente"] = request.B("current"),
            ["formula"] = builtin?.Formula ?? custom.S("formula"),
            ["modelo_nome"] = builtin?.Name ?? custom.S("nome"),
            ["unidade_idade"] = custom.S("unidade_idade", "meses"),
        };
        if (custom?["entradas"] is not null)
            row["entradas"] = custom["entradas"]!.DeepClone();
        if (request["fixed_inputs"] is JsonObject fixedInputs)
            row["fixed_inputs"] = fixedInputs.DeepClone();
        if (index >= 0 && models[index].S("modelo") == family)
            foreach (string key in new[] { "formula", "modelo_nome", "unidade_idade", "entradas", "fixed_inputs" })
                if (models[index]![key] is { } old)
                    row[key] = old.DeepClone();
        if (request["inputs"] is not null)
        {
            row["modelo_nome"] = request.S("name");
            row["formula"] = request.S("formula");
            row["entradas"] = request["inputs"]!.DeepClone();
        }
        if (
            models
                .Where((_, i) => i != index)
                .Any(e =>
                    e.S("codigo") == code && e.S("modelo") == family && e.N("ano") == row.N("ano")
                )
        )
            throw new InvalidDataException(
                "Já existe uma versão desse ID, modelo e ano. Edite-a ou use outro ano/modelo."
            );
        ProjectStore.ValidateCoefficients(row);
        ModelDefinition.Validate(row, true);
        if (index < 0)
            models.Add(row);
        else
            models[index] = row;
        if (row.B("vigente"))
            foreach (var other in models)
                if (!ReferenceEquals(other, row) && other.S("codigo") == code)
                    other!["vigente"] = false;
        ProjectStore.Validate(project);
    }

    public static void Activate(JsonObject project, int index)
    {
        var models = project.Obj("curvas_crescimento").Arr("equacoes");
        Index(models, index);
        string id = models[index].S("codigo");
        foreach (var model in models)
            if (model.S("codigo") == id)
                model!["vigente"] = ReferenceEquals(model, models[index]);
    }

    public static void Remove(JsonObject project, int index, bool growth)
    {
        var models = project.Obj(growth ? "curvas_crescimento" : "volume").Arr("equacoes");
        Index(models, index);
        models.RemoveAt(index);
    }

    public static string[] RemoveFile(JsonObject project, int index)
    {
        var meta = project.Obj("_dendro");
        var files = meta.Arr("files");
        Index(files, index);
        static string Key(string path) => path.Replace('/', '\\');
        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Key(files[index].Display()) };
        var children = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in new[] { "derived_tables", "processing_results" })
            foreach (var edge in (meta[key] as JsonArray ?? []).OfType<JsonObject>())
            {
                string parent = Key(edge.S("source_path")), child = Key(edge.S("path"));
                if (parent.Length == 0 || child.Length == 0) continue;
                if (!children.TryGetValue(parent, out var targets)) children[parent] = targets = [];
                targets.Add(child);
            }
        // Traverse before removing links, so every generation remains discoverable.
        // The visited set also protects against cyclic or duplicated project metadata.
        var pending = new Queue<string>(removed);
        while (pending.TryDequeue(out string? parent))
            if (children.TryGetValue(parent, out var targets))
                foreach (string child in targets)
                    if (removed.Add(child)) pending.Enqueue(child);
        bool Contains(string? path) => !string.IsNullOrEmpty(path) && removed.Contains(Key(path));
        for (int i = files.Count - 1; i >= 0; i--)
            if (Contains(files[i].Display())) files.RemoveAt(i);
        foreach (
            string key in new[] { "file_options", "charts", "derived_tables", "content_names", "processing_results", "analyses" }
        )
        {
            var array = meta[key] as JsonArray;
            if (array is null)
                continue;
            for (int i = array.Count - 1; i >= 0; i--)
                if (
                    Contains(array[i].S("path"))
                    || (key == "derived_tables" || key == "processing_results")
                        && Contains(array[i].S("source_path"))
                    || key == "analyses" && (Contains(array[i]?["definition"].S("path"))
                        || Contains(array[i]?["definition"]?["chart"].S("path")))
                )
                    array.RemoveAt(i);
        }
        if (meta["batch_inputs"] is JsonArray selected)
            for (int i = selected.Count - 1; i >= 0; i--)
                if (Contains(selected[i].Display())) selected.RemoveAt(i);
        foreach (string key in new[] { "input", "output" })
            if (Contains(meta.S(key))) meta[key] = "";
        return removed.ToArray();
    }

    private static void Index(JsonArray array, int index, bool append = false)
    {
        if (index < (append ? -1 : 0) || index >= array.Count)
            throw new InvalidDataException("Selecione um item válido.");
    }
}
