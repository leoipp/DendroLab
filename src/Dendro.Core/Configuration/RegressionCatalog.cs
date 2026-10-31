using System.Text.Json.Nodes;
using Dendro.Core.Mathematics;
using Dendro.Core.Regression;

namespace Dendro.Core.Configuration;

public static class RegressionCatalog
{
    public static void Ensure(JsonObject project)
    {
        if (project.ContainsKey("regressoes"))
            return;
        var models = new JsonArray();
        for (int i = 0; i < RegressionEngine.Models.Length; i++)
        {
            var m = RegressionEngine.Models[i];
            models.Add(
                new JsonObject
                {
                    ["id"] = "modelo_" + (i + 1),
                    ["nome"] = m.Name,
                    ["preditores"] = 1,
                    ["formula"] = m.Formula,
                    ["coeficientes"] = m.Parameters,
                }
            );
        }
        project["regressoes"] = new JsonObject { ["modelos"] = models };
    }

    public static void Validate(JsonObject project)
    {
        if (!project.ContainsKey("regressoes"))
            return;
        if (
            project["regressoes"] is not JsonObject section
            || section["modelos"] is not JsonArray models
            || models.Count > 200
        )
            throw new InvalidDataException(
                "Cadastro de regressões inválido (máximo de 200 modelos por projeto)."
            );
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in models)
        {
            if (node is not JsonObject model)
                throw new InvalidDataException("Modelo de regressão inválido.");
            string id = model.S("id").Trim(),
                name = model.S("nome").Trim();
            if (
                id.Length is < 1 or > 64
                || name.Length is < 1 or > 120
                || id.Any(char.IsControl)
                || name.Any(char.IsControl)
                || !ids.Add(id)
            )
                throw new InvalidDataException(
                    "Informe ID único (até 64 caracteres) e nome (até 120 caracteres) para cada regressão."
                );
            double p = model.N("preditores"),
                k = model.N("coeficientes");
            if (
                p is < 1 or > 3
                || p != Math.Truncate(p)
                || k is < 1 or > 4
                || k != Math.Truncate(k)
            )
                throw new InvalidDataException(
                    $"{name}: use 1 a 3 preditores e 1 a 4 coeficientes."
                );
            var symbols = Enumerable
                .Range(0, (int)k)
                .Select(i => "b" + i)
                .Concat(Enumerable.Range(1, (int)p).Select(i => "x" + i));
            if (p == 1)
                symbols = symbols.Concat(["x", "t"]);
            try
            {
                _ = new Formula(model.S("formula"), symbols);
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException($"{name}: {ex.Message}", ex);
            }
        }
    }

    public static RegressionFamily[] Families(JsonObject project, int predictors = 1)
    {
        Ensure(project);
        Validate(project);
        return project
            .Obj("regressoes")
            .Arr("modelos")
            .OfType<JsonObject>()
            .Where(m => predictors <= 0 || m.N("preditores") == predictors)
            .Select(m => new RegressionFamily(
                $"{m.S("nome")} · {(int)m.N("preditores")}P",
                m.S("formula"),
                (int)m.N("coeficientes"),
                m.S("id"),
                (int)m.N("preditores")
            ))
            .ToArray();
    }
}
