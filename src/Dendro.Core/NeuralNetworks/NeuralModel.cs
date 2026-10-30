using System.IO.Compression;
using System.Text.Json.Nodes;
using PureHDF;

namespace Dendro.Core.NeuralNetworks;

public sealed class NeuralModel
{
    public sealed record DenseLayer(
        int Inputs,
        int Outputs,
        float[] Weights,
        float[] Bias,
        string Activation
    );

    public string[] Numerical { get; private set; } = [];
    public string[] Categorical { get; private set; } = [];
    public string[][] Categories { get; private set; } = [];
    public List<DenseLayer> Layers { get; } = [];
    public double[] Mean { get; private set; } = [];
    public double[] Scale { get; private set; } = [];
    public double OutputMean { get; private set; }
    public double OutputScale { get; private set; }
    public int Inputs { get; private set; }
    public int HiddenSize => Layers[^1].Inputs;

    private static byte[] Entry(ZipArchive archive, string name, int limit)
    {
        var matches = archive.Entries.Where(e => e.FullName == name).ToArray();
        if (matches.Length != 1 || matches[0].Length < 1 || matches[0].Length > limit)
            throw new InvalidDataException(
                $"Arquivo do modelo ausente, duplicado ou acima do limite: {name}."
            );
        using var input = matches[0].Open();
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    public static NeuralModel Load(string path)
    {
        using var archive = path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? ZipFile.OpenRead(path)
            : null;
        string prefix = path;
        if (archive is not null)
        {
            if (archive.Entries.Count > 10000)
                throw new InvalidDataException("O ZIP excede o limite de arquivos.");
            var metadata = archive
                .Entries.Where(e => e.FullName.EndsWith("_metadata.json", StringComparison.Ordinal))
                .ToArray();
            if (metadata.Length != 1)
                throw new InvalidDataException("Use um ZIP com um único conjunto de modelo geral.");
            prefix = metadata[0].FullName[..^14];
        }
        byte[] Part(string suffix, int limit)
        {
            if (archive is not null)
                return Entry(archive, prefix + suffix, limit);
            var info = new FileInfo(prefix + suffix);
            if (info.Length < 1 || info.Length > limit)
                throw new InvalidDataException("Arquivo de modelo acima do limite.");
            return File.ReadAllBytes(info.FullName);
        }
        var meta = JsonNode.Parse(Part("_metadata.json", 1048576))!;
        if (meta.S("model_type") != "neural_net")
            throw new InvalidDataException("Metadados inválidos: esperado neural_net.");
        var m = new NeuralModel
        {
            Numerical = (meta["cols_numericas"] ?? meta["features"])!
                .AsArray()
                .Select(n => n!.GetValue<string>())
                .ToArray(),
            Categorical = (meta["cols_categoricas"] as JsonArray ?? [])
                .Select(n => n!.GetValue<string>())
                .ToArray(),
        };
        using var keras = new ZipArchive(
            new MemoryStream(Part("_model.keras", 128 * 1024 * 1024)),
            ZipArchiveMode.Read
        );
        var architecture = JsonNode.Parse(Entry(keras, "config.json", 1048576))!;
        if (architecture.S("class_name") != "Sequential")
            throw new InvalidDataException("Somente redes Keras Sequential são suportadas.");
        using var weights = new MemoryStream(Entry(keras, "model.weights.h5", 128 * 1024 * 1024));
        using var h5 = H5File.Open(weights);
        int previous = 0,
            denseIndex = 0;
        foreach (var layer in architecture["config"]!["layers"]!.AsArray())
        {
            string type = layer.S("class_name");
            var cfg = layer!["config"]!;
            string dtype =
                cfg["dtype"] is JsonValue
                    ? cfg.S("dtype")
                    : cfg["dtype"]?["config"].S("name", "float32") ?? "float32";
            if (dtype != "float32")
                throw new InvalidDataException("Use pesos Keras float32.");
            if (type == "InputLayer")
            {
                var shape = cfg["batch_shape"]!.AsArray();
                previous = m.Inputs = checked((int)JsonExtensions.Number(shape[1]));
                if (shape.Count != 2 || previous is < 1 or > 4096)
                    throw new InvalidDataException("Forma de entrada não suportada.");
                continue;
            }
            if (type == "Dropout")
                continue;
            if (type != "Dense" || m.Layers.Count >= 32 || previous < 1)
                throw new InvalidDataException($"Camada não suportada: {type}.");
            string activation = cfg.S("activation", "linear");
            _ = Activate(0, activation);
            string group = denseIndex == 0 ? "dense" : "dense_" + denseIndex;
            denseIndex++;
            var dataset = h5.Dataset($"/layers/{group}/vars/0");
            var dims = dataset.Space.Dimensions;
            int outputs = (int)cfg.N("units");
            if (
                dims.Length != 2
                || dims[0] != (ulong)previous
                || dims[1] != (ulong)outputs
                || outputs is < 1 or > 4096
                || (long)previous * outputs > 16777216
            )
                throw new InvalidDataException("Dimensão dos pesos Dense incompatível.");
            float[] kernel = dataset.Read<float[]>();
            float[] bias = cfg.B("use_bias", true)
                ? h5.Dataset($"/layers/{group}/vars/1").Read<float[]>()
                : new float[outputs];
            if (
                bias.Length != outputs
                || kernel.Length != previous * outputs
                || kernel.Any(n => !float.IsFinite(n))
                || bias.Any(n => !float.IsFinite(n))
            )
                throw new InvalidDataException("Pesos Dense inválidos.");
            m.Layers.Add(new(previous, outputs, kernel, bias, activation));
            previous = outputs;
        }
        if (m.Layers.Count == 0 || previous != 1)
            throw new InvalidDataException("A RNA precisa de uma única saída de altura.");
        var scalers = PickleData.Read(Part("_scalers.pkl", 16 * 1024 * 1024));
        var x = scalers.At(0);
        var y = scalers.At(1);
        if (x?.ClassName != "StandardScaler" || y?.ClassName != "StandardScaler")
            throw new InvalidDataException(
                "Pré-processamento incompatível: StandardScaler esperado."
            );
        m.Mean = x.Get("mean_")!.Doubles(m.Inputs);
        m.Scale = x.Get("scale_")!.Doubles(m.Inputs);
        m.OutputMean = y.Get("mean_")!.Doubles(1)[0];
        m.OutputScale = y.Get("scale_")!.Doubles(1)[0];
        if (x.Get("with_mean")?.Number == 0)
            Array.Fill(m.Mean, 0);
        if (x.Get("with_std")?.Number == 0)
            Array.Fill(m.Scale, 1);
        if (y.Get("with_mean")?.Number == 0)
            m.OutputMean = 0;
        if (y.Get("with_std")?.Number == 0)
            m.OutputScale = 1;
        if (m.Scale.Any(n => n <= 0) || m.OutputScale <= 0)
            throw new InvalidDataException("Escalas de normalização inválidas.");
        int expected = m.Numerical.Length;
        if (m.Categorical.Length > 0)
        {
            var root = PickleData.Read(Part("_encoder.pkl", 16 * 1024 * 1024));
            var enc = root.Get("encoder");
            if (
                enc?.ClassName != "OneHotEncoder"
                || enc.Get("drop_idx_")?.Kind != 0
                || enc.Get("handle_unknown")?.Text != "ignore"
                || !root.Get("cols_numericas")!.Strings().SequenceEqual(m.Numerical)
                || !root.Get("cols_categoricas")!.Strings().SequenceEqual(m.Categorical)
            )
                throw new InvalidDataException("OneHotEncoder incompatível.");
            m.Categories = enc.Get("categories_")!
                .Items.Select(v => v.ArrayData!.Strings())
                .ToArray();
            expected += m.Categories.Sum(v => v.Length);
            if (m.Categories.Length != m.Categorical.Length)
                throw new InvalidDataException("Categorias incompatíveis.");
        }
        if (expected != m.Inputs)
            throw new InvalidDataException("As variáveis não correspondem às dimensões da rede.");
        return m;
    }

    public double Predict(double[] numerical, string[] categorical, float[]? hidden = null)
    {
        if (
            numerical.Length != Numerical.Length
            || categorical.Length != Categorical.Length
            || numerical.Any(n => !double.IsFinite(n))
            || categorical.Any(string.IsNullOrEmpty)
        )
            return double.NaN;
        var x = new double[Inputs];
        numerical.CopyTo(x, 0);
        int offset = numerical.Length;
        for (int c = 0; c < Categories.Length; c++)
            foreach (var category in Categories[c])
                x[offset++] = category == categorical[c] ? 1 : 0;
        var a = new float[Math.Max(Inputs, Layers.Max(l => l.Outputs))];
        var b = new float[a.Length];
        for (int i = 0; i < Inputs; i++)
            a[i] = (float)((x[i] - Mean[i]) / Scale[i]);
        for (int k = 0; k < Layers.Count; k++)
        {
            var layer = Layers[k];
            if (hidden is not null && k == Layers.Count - 1)
                Array.Copy(a, hidden, layer.Inputs);
            for (int j = 0; j < layer.Outputs; j++)
            {
                float sum = 0;
                for (int i = 0; i < layer.Inputs; i++)
                    sum += a[i] * layer.Weights[i * layer.Outputs + j];
                b[j] = Activate(sum + layer.Bias[j], layer.Activation);
                if (!float.IsFinite(b[j]))
                    return double.NaN;
            }
            (a, b) = (b, a);
        }
        float y = (float)(a[0] * OutputScale);
        y = (float)(y + OutputMean);
        return y;
    }

    private static float Activate(float x, string kind) =>
        kind switch
        {
            "linear" => x,
            "relu" => MathF.Max(x, 0),
            "sigmoid" => x >= 0 ? 1 / (1 + MathF.Exp(-x)) : MathF.Exp(x) / (1 + MathF.Exp(x)),
            "tanh" => MathF.Tanh(x),
            "elu" => x >= 0 ? x : MathF.Exp(x) - 1,
            "selu" => 1.050700987f * (x >= 0 ? x : 1.673263242f * (MathF.Exp(x) - 1)),
            "softplus" => MathF.Max(x, 0) + MathF.Log(1 + MathF.Exp(-MathF.Abs(x))),
            "softsign" => x / (1 + MathF.Abs(x)),
            "swish" or "silu" => x * Activate(x, "sigmoid"),
            "exponential" => MathF.Exp(x),
            _ => throw new InvalidDataException($"Ativação não suportada: {kind}."),
        };
}
