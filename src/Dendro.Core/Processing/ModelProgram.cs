using System.Text.Json.Nodes;
using Dendro.Core.Configuration;
using Dendro.Core.Mathematics;
using Dendro.Core.Spreadsheets;

namespace Dendro.Core.Processing;

public sealed record ModelContext(
    DataTable Input,
    DataTable Registry,
    int Row,
    int RegistryRow,
    double Age,
    double Dap,
    double Height,
    double Area,
    double Effective,
    double VolumeHa = double.NaN,
    double StemsHa = double.NaN
);

public sealed class ModelProgram
{
    public JsonNode Definition { get; }
    public JsonArray Inputs { get; }
    private readonly Formula formula;
    private readonly Dictionary<string, int> symbols;
    private readonly int[] columns;

    public ModelProgram(JsonNode definition, DataTable input, DataTable registry, bool growth)
    {
        ModelDefinition.Validate(definition, growth);
        Definition = definition;
        Inputs = ModelDefinition.Inputs(definition, growth);
        symbols = Inputs
            .Select((x, i) => (x.S("simbolo"), i))
            .ToDictionary(x => x.Item1, x => x.i, StringComparer.Ordinal);
        formula = new(
            ModelDefinition.FormulaText(definition, growth),
            symbols.Keys.Concat(["b0", "b1", "b2"])
        );
        columns = Inputs
            .Select(x =>
                x.S("origem") switch
                {
                    "entrada" => input.Require(x.S("coluna")),
                    "cadastro" => registry.Require(x.S("coluna")),
                    _ => -1,
                }
            )
            .ToArray();
    }

    public double[] Values(ModelContext c)
    {
        double[] values = new double[Inputs.Count];
        for (int i = 0; i < values.Length; i++)
        {
            var x = Inputs[i]!;
            double v = x.S("origem") switch
            {
                "entrada" => c.Input.Number(c.Row, columns[i]),
                "cadastro" => c.Registry.Number(c.RegistryRow, columns[i]),
                _ => x.S("coluna") switch
                {
                    "idade_anos" => c.Age,
                    "idade_meses" => c.Age * 12,
                    "dap_cm" => c.Dap / 10,
                    "altura_m" => c.Height / 10,
                    "area_corr" => c.Area,
                    "area_efetiva" => c.Effective,
                    "volume_ha" => c.VolumeHa,
                    "fustes_ha" => c.StemsHa,
                    _ => double.NaN,
                },
            };
            values[i] = v * x.N("fator", 1);
            if (!double.IsFinite(values[i]))
                throw new InvalidDataException(
                    $"Modelo {Definition.S("codigo")}: entrada {x.S("simbolo")} inválida na linha {c.Row + 1} ({x.S("coluna")})."
                );
        }
        return values;
    }

    public double Evaluate(double[] values)
    {
        double value = formula.Evaluate(s =>
            s is "b0" or "b1" or "b2" ? Definition.N(s, double.NaN) : values[symbols[s]]
        );
        return double.IsFinite(value) && value >= 0 ? value : double.NaN;
    }

    public double Aggregate(IEnumerable<ModelContext> rows)
    {
        var data = rows.Select(Values).ToArray();
        double[] aggregated = Enumerable
            .Range(0, Inputs.Count)
            .Select(i =>
                InventoryMath.Aggregate(data.Select(v => v[i]), Inputs[i].S("agregacao", "media"))
            )
            .ToArray();
        return Evaluate(aggregated);
    }
}
