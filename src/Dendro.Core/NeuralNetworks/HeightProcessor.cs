using System.Text.Json.Nodes;
using Dendro.Core.Mathematics;
using Dendro.Core.Processing;
using Dendro.Core.Spreadsheets;

namespace Dendro.Core.NeuralNetworks;

public static class HeightProcessor
{
    private sealed record Calibration(
        int Method = 0,
        double Intercept = 0,
        double Slope = 1,
        double[]? Weights = null
    )
    {
        public double Predict(double prediction, float[] hidden) =>
            !double.IsFinite(prediction) ? double.NaN
            : Method == 3 ? Intercept + Weights!.Select((w, i) => w * hidden[i]).Sum()
            : Slope * prediction + Intercept;
    }

    private static Calibration Fit(
        int[] rows,
        double[] observed,
        double[] predictions,
        float[][] hidden,
        JsonNode? config,
        bool global
    )
    {
        rows = rows.Where(r => observed[r] > 0 && double.IsFinite(predictions[r])).ToArray();
        if (rows.Length == 0)
            return new();
        int n = rows.Length,
            dim = hidden[rows[0]].Length;
        double xmean = rows.Average(r => predictions[r]),
            ymean = rows.Average(r => observed[r]);
        if (n >= config.N("min_amostras_finetune", 30) && dim is > 0 and <= 512)
        {
            var means = new double[dim];
            foreach (int r in rows)
                for (int j = 0; j < dim; j++)
                    means[j] += hidden[r][j] / (double)n;
            var matrix = new double[dim, dim];
            var rhs = new double[dim];
            foreach (int r in rows)
                for (int j = 0; j < dim; j++)
                {
                    double x = hidden[r][j] - means[j];
                    rhs[j] += x * (observed[r] - ymean);
                    for (int k = 0; k < dim; k++)
                        matrix[j, k] += x * (hidden[r][k] - means[k]);
                }
            for (int j = 0; j < dim; j++)
                matrix[j, j] += config.N("ridge_alpha", 10);
            if (InventoryMath.Solve(matrix, rhs, out var weights))
            {
                var fit = new Calibration(
                    3,
                    ymean - means.Select((v, i) => v * weights[i]).Sum(),
                    1,
                    weights
                );
                double residual = rows.Sum(r =>
                        Math.Pow(observed[r] - fit.Predict(predictions[r], hidden[r]), 2)
                    ),
                    ss = rows.Sum(r => Math.Pow(observed[r] - ymean, 2)),
                    r2 = ss > 0 ? 1 - residual / ss
                    : residual < 1e-12 ? 1
                    : 0;
                if (n <= 1 || r2 >= (global ? 0 : .5))
                    return fit;
            }
        }
        if (n >= config.N("min_amostras_linear", 10))
        {
            double xx = rows.Sum(r => Math.Pow(predictions[r] - xmean, 2)),
                xy = rows.Sum(r => (predictions[r] - xmean) * (observed[r] - ymean));
            if (xx > 1e-15)
                return new(2, ymean - xy / xx * xmean, xy / xx);
        }
        return n >= config.N("min_amostras_bias", 1) ? new(1, ymean - xmean) : new();
    }

    public static void Apply(DataTable input, DataTable registry, JsonObject cfg)
    {
        var h = cfg["ajuste_altura"]!;
        var model = NeuralModel.Load(h["modelo_geral"].S("caminho"));
        var identification = new InventoryIdentity(input, cfg);
        FeaturePreparation.Prepare(input, registry, cfg);
        int[] numeric = model.Numerical.Select(input.Require).ToArray(),
            categorical = model.Categorical.Select(input.Require).ToArray();
        var cols = cfg["arquivo_entrada"]?["colunas"];
        int hc = input.Require(cols.S("altura", "H")),
            dc = input.Require(cols.S("dap", "DAP")),
            lc = input.Require(cols.S("lote", "Lote"));
        int obs = input.Ensure("H_obs"),
            est = input.Ensure("H_est"),
            final = input.Ensure("H_final"),
            method = input.Ensure("H_ajuste");
        bool fine = h.S("h_mode") == "FINETUNE_MODEL";
        if (
            fine
            && (
                model.HiddenSize > 512
                || (long)input.Count * model.HiddenSize * 4 > 256 * 1024 * 1024
            )
        )
            throw new InvalidDataException(
                "Representação oculta excede o limite do finetune (512 variáveis, 256 MB)."
            );
        int n = input.Count;
        var observed = new double[n];
        var predictions = new double[n];
        var estimated = new double[n];
        var hidden = new float[n][];
        for (int r = 1; r < n; r++)
        {
            observed[r] = input.Number(r, hc);
            hidden[r] = fine ? new float[model.HiddenSize] : [];
            predictions[r] = estimated[r] = model.Predict(
                numeric.Select(c => input.Number(r, c)).ToArray(),
                categorical.Select(c => input.Text(r, c)).ToArray(),
                fine ? hidden[r] : null
            );
        }
        if (!predictions.Skip(1).Any(double.IsFinite))
            throw new InvalidDataException(
                "Nenhuma linha possui variáveis completas para executar a RNA. Revise as estatísticas e as variáveis do modelo."
            );
        if (fine)
        {
            var rows = Enumerable.Range(1, n - 1).ToArray();
            foreach (var scope in rows.GroupBy(r => identification.Context(r).Id))
            {
                var global = Fit(scope.ToArray(), observed, predictions, hidden, h["finetune"], true);
                foreach (var group in scope.GroupBy(r => input.Text(r, lc)))
                {
                    var local = Fit(group.ToArray(), observed, predictions, hidden, h["finetune"], false);
                    var fit = local.Method != 0 ? local : global;
                    string name = (local.Method != 0 ? "" : "FALLBACK_") + new[] { "RAW", "BIAS", "LINEAR", "FINETUNE" }[fit.Method];
                    foreach (int r in group)
                    {
                        estimated[r] = fit.Predict(predictions[r], hidden[r]);
                        input.Set(r, method, name);
                    }
                }
            }
        }
        for (int r = 1; r < n; r++)
        {
            double measured = observed[r],
                prediction = estimated[r],
                dap = input.Number(r, dc),
                height = measured;
            if (!(dap > 0))
                height = 0;
            if (dap > 0 && !(measured > 0) && double.IsFinite(prediction))
                height = prediction;
            if (!fine && measured > 0)
                height = measured;
            input.Set(r, obs, measured);
            input.Set(r, est, prediction);
            input.Set(r, final, height);
            input.Set(r, hc, height);
            if (!fine)
                input.Set(r, method, "MODEL_GENERAL");
        }
        var valid = Enumerable
            .Range(1, n - 1)
            .Where(r => input.Number(r, dc) > 0 && input.Number(r, hc) > 0)
            .ToArray();
        if (valid.Length == 0)
            return;
        var means = valid.GroupBy(r => identification.Context(r).Id).ToDictionary(g => g.Key, g => g.Average(r => input.Number(r, hc)));
        var lots = valid
            .GroupBy(r => (Scope: identification.Context(r).Id, Lot: input.Text(r, lc)))
            .ToDictionary(
                g => g.Key,
                g => g.Average(r => input.Number(r, hc))
            );
        for (int r = 1; r < n; r++)
            if (input.Number(r, dc) > 0 && !(input.Number(r, hc) > 0))
            {
                string scope = identification.Context(r).Id;
                bool local = lots.TryGetValue((scope, input.Text(r, lc)), out double height);
                if (!local && !means.TryGetValue(scope, out height)) continue;
                input.Set(r, hc, height);
                input.Set(r, final, height);
                input.Set(r, method, local ? "MEDIA_LOTE" : "MEDIA_GLOBAL");
            }
    }
}
