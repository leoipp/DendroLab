using System.IO.Compression;
using System.Numerics;
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

    private sealed record Tree(int[] Left, int[] Right, int[] Feature, double[] Threshold, double[] Value);

    public string[] Numerical { get; private set; } = [];
    public string[] Categorical { get; private set; } = [];
    public string[][] Categories { get; private set; } = [];
    private Dictionary<string, int>[] CategoryIndexes { get; set; } = [];
    public List<DenseLayer> Layers { get; } = [];
    public double[] Mean { get; private set; } = [];
    public double[] Scale { get; private set; } = [];
    public double OutputMean { get; private set; }
    public double OutputScale { get; private set; }
    public int Inputs { get; private set; }
    public int HiddenSize => Layers.Count == 0 ? 0 : Layers[^1].Inputs;
    public string ModelType { get; private set; } = "neural_net";
    private List<Tree> Trees { get; } = [];
    private string Kernel { get; set; } = "";
    private double[][] SupportVectors { get; set; } = [];
    private double[] DualCoefficients { get; set; } = [];
    private double Intercept { get; set; }
    private double Gamma { get; set; }
    private double Coef0 { get; set; }
    private int Degree { get; set; }
    private double LearningRate { get; set; } = 1;

    public PredictionWorkspace CreatePredictionWorkspace() => new(
        Inputs,
        Math.Max(Inputs, Layers.Count == 0 ? 0 : Layers.Max(layer => layer.Outputs))
    );

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
            ModelType = meta.S("model_type", "neural_net"),
            Numerical = (meta["cols_numericas"] ?? meta["features"])!
                .AsArray()
                .Select(n => n!.GetValue<string>())
                .ToArray(),
            Categorical = (meta["cols_categoricas"] as JsonArray ?? [])
                .Select(n => n!.GetValue<string>())
                .ToArray(),
        };
        if (m.ModelType != "neural_net")
        {
            var payload = JsonNode.Parse(Part("_model.json", 128 * 1024 * 1024))!;
            m.LoadEstimator(payload);
            var portableScalers = PickleData.Read(Part("_scalers.pkl", 16 * 1024 * 1024));
            var px = portableScalers.At(0);
            var py = portableScalers.At(1);
            if (px?.ClassName != "StandardScaler")
                throw new InvalidDataException("Pré-processamento incompatível: StandardScaler esperado.");
            int meanBytes = px.Get("mean_")?.ArrayData?.Bytes.Length ?? 0;
            if (meanBytes == 0 || meanBytes % 8 != 0)
                throw new InvalidDataException("Dimensões do pré-processamento inválidas.");
            m.Inputs = meanBytes / 8;
            m.Mean = px.Get("mean_")!.Doubles(m.Inputs);
            m.Scale = px.Get("scale_")!.Doubles(m.Inputs);
            if (px.Get("with_mean")?.Number == 0) Array.Fill(m.Mean, 0);
            if (px.Get("with_std")?.Number == 0) Array.Fill(m.Scale, 1);
            if (py?.ClassName == "StandardScaler")
            {
                m.OutputMean = py.Get("mean_")!.Doubles(1)[0];
                m.OutputScale = py.Get("scale_")!.Doubles(1)[0];
                if (py.Get("with_mean")?.Number == 0) m.OutputMean = 0;
                if (py.Get("with_std")?.Number == 0) m.OutputScale = 1;
            }
            else { m.OutputMean = 0; m.OutputScale = 1; }
            if (m.Scale.Any(n => n <= 0) || m.OutputScale <= 0)
                throw new InvalidDataException("Escalas de normalização inválidas.");
            int portableExpected = m.Numerical.Length;
            if (m.Categorical.Length > 0)
            {
                var root = PickleData.Read(Part("_encoder.pkl", 16 * 1024 * 1024));
                var enc = root.Get("encoder");
                if (enc?.ClassName != "OneHotEncoder" || enc.Get("drop_idx_")?.Kind != 0
                    || enc.Get("handle_unknown")?.Text != "ignore"
                    || !root.Get("cols_numericas")!.Strings().SequenceEqual(m.Numerical)
                    || !root.Get("cols_categoricas")!.Strings().SequenceEqual(m.Categorical))
                    throw new InvalidDataException("OneHotEncoder incompatível.");
                m.Categories = enc.Get("categories_")!.Items.Select(v => v.ArrayData!.Strings()).ToArray();
                portableExpected += m.Categories.Sum(v => v.Length);
                if (m.Categories.Length != m.Categorical.Length)
                    throw new InvalidDataException("Categorias incompatíveis.");
            }
            m.BuildCategoryIndexes();
            if (portableExpected != m.Inputs)
                throw new InvalidDataException("As variáveis não correspondem às dimensões do modelo.");
            if (m.ModelType == "svr" && m.SupportVectors.Any(row => row.Length != m.Inputs))
                throw new InvalidDataException("Dimensão dos vetores de suporte incompatível com as variáveis.");
            return m;
        }
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
        m.BuildCategoryIndexes();
        if (expected != m.Inputs)
            throw new InvalidDataException("As variáveis não correspondem às dimensões da rede.");
        return m;
    }

    public double Predict(double[] numerical, string[] categorical, float[]? hidden = null)
        => Predict(numerical, categorical, CreatePredictionWorkspace(), hidden);

    /// <summary>Predicts with caller-owned reusable scratch buffers to avoid per-row allocations.</summary>
    public double Predict(double[] numerical, string[] categorical, PredictionWorkspace workspace, float[]? hidden = null)
    {
        if (numerical.Length != Numerical.Length || categorical.Length != Categorical.Length
            || workspace.Input.Length < Inputs || workspace.LayerA.Length < Inputs
            || workspace.LayerB.Length < Inputs)
            return double.NaN;
        for (int i = 0; i < numerical.Length; i++)
            if (!double.IsFinite(numerical[i])) return double.NaN;
        for (int i = 0; i < categorical.Length; i++)
            if (string.IsNullOrEmpty(categorical[i])) return double.NaN;
        if (ModelType != "neural_net")
        {
            double[] vector = workspace.Input;
            numerical.CopyTo(vector, 0);
            EncodeCategories(vector, numerical.Length, categorical);
            for (int i = 0; i < vector.Length; i++) vector[i] = (vector[i] - Mean[i]) / Scale[i];
            double prediction;
            if (ModelType == "svr")
            {
                prediction = Intercept;
                for (int i = 0; i < SupportVectors.Length; i++)
                    prediction += DualCoefficients[i] * KernelValue(vector, SupportVectors[i]);
                return prediction * OutputScale + OutputMean;
            }
            if (ModelType == "random_forest")
            {
                double sum = 0;
                foreach (var tree in Trees) sum += PredictTree(tree, vector);
                prediction = sum / Trees.Count;
            }
            else if (ModelType == "gradient_boosting")
            {
                double sum = 0;
                foreach (var tree in Trees) sum += PredictTree(tree, vector);
                prediction = Intercept + LearningRate * sum;
            }
            else return double.NaN;
            return prediction;
        }
        double[] x = workspace.Input;
        numerical.CopyTo(x, 0);
        EncodeCategories(x, numerical.Length, categorical);
        float[] a = workspace.LayerA;
        float[] b = workspace.LayerB;
        for (int i = 0; i < Inputs; i++)
            a[i] = (float)((x[i] - Mean[i]) / Scale[i]);
        for (int k = 0; k < Layers.Count; k++)
        {
            var layer = Layers[k];
            if (hidden is not null && k == Layers.Count - 1)
                Array.Copy(a, hidden, layer.Inputs);
            if (Vector.IsHardwareAccelerated && layer.Outputs >= Vector<float>.Count)
            {
                Array.Clear(b, 0, layer.Outputs);
                int vectorWidth = Vector<float>.Count;
                for (int i = 0; i < layer.Inputs; i++)
                {
                    int weights = i * layer.Outputs, j = 0;
                    var inputValue = new Vector<float>(a[i]);
                    for (; j <= layer.Outputs - vectorWidth; j += vectorWidth)
                    {
                        var sum = new Vector<float>(b, j)
                            + new Vector<float>(layer.Weights, weights + j) * inputValue;
                        sum.CopyTo(b, j);
                    }
                    for (; j < layer.Outputs; j++)
                        b[j] += a[i] * layer.Weights[weights + j];
                }
                for (int j = 0; j < layer.Outputs; j++)
                {
                    b[j] = Activate(b[j] + layer.Bias[j], layer.Activation);
                    if (!float.IsFinite(b[j])) return double.NaN;
                }
            }
            else
            {
                for (int j = 0; j < layer.Outputs; j++)
                {
                    float sum = 0;
                    for (int i = 0; i < layer.Inputs; i++)
                        sum += a[i] * layer.Weights[i * layer.Outputs + j];
                    b[j] = Activate(sum + layer.Bias[j], layer.Activation);
                    if (!float.IsFinite(b[j])) return double.NaN;
                }
            }
            (a, b) = (b, a);
        }
        float y = (float)(a[0] * OutputScale);
        y = (float)(y + OutputMean);
        return y;
    }

    private void BuildCategoryIndexes()
    {
        CategoryIndexes = Categories.Select(categories =>
        {
            var indexes = new Dictionary<string, int>(categories.Length, StringComparer.Ordinal);
            for (int i = 0; i < categories.Length; i++) indexes.TryAdd(categories[i], i);
            return indexes;
        }).ToArray();
    }

    private void EncodeCategories(double[] vector, int offset, string[] values)
    {
        int count = 0;
        for (int column = 0; column < Categories.Length; column++) count += Categories[column].Length;
        Array.Clear(vector, offset, count);
        for (int column = 0; column < Categories.Length; column++)
        {
            if (CategoryIndexes[column].TryGetValue(values[column], out int index))
                vector[offset + index] = 1;
            offset += Categories[column].Length;
        }
    }

    private void LoadEstimator(JsonNode payload)
    {
        if (payload.S("kind") != ModelType)
            throw new InvalidDataException("Tipo do modelo ZIP e dos metadados divergentes.");
        if (ModelType is "random_forest" or "gradient_boosting")
        {
            var nodes = payload["trees"]?.AsArray() ?? throw new InvalidDataException("ZIP sem árvores do modelo.");
            if (nodes.Count is < 1 or > 10000) throw new InvalidDataException("Quantidade de árvores inválida.");
            foreach (var node in nodes) Trees.Add(ParseTree(node!));
            if (ModelType == "gradient_boosting")
            {
                Intercept = payload.N("initial");
                LearningRate = payload.N("learning_rate", 1);
            }
            return;
        }
        if (ModelType == "svr")
        {
            Kernel = payload.S("kernel");
            if (Kernel is not ("linear" or "rbf" or "poly" or "sigmoid"))
                throw new InvalidDataException("Kernel SVR não suportado.");
            SupportVectors = payload["support_vectors"]?.AsArray()
                .Select(row => row!.AsArray().Select(value => value!.GetValue<double>()).ToArray()).ToArray() ?? [];
            DualCoefficients = payload["dual_coef"]?.AsArray().Select(value => value!.GetValue<double>()).ToArray() ?? [];
            Intercept = payload.N("intercept"); Gamma = payload.N("gamma"); Coef0 = payload.N("coef0"); Degree = (int)payload.N("degree", 3);
            if (SupportVectors.Length == 0 || SupportVectors.Length != DualCoefficients.Length
                || SupportVectors.Any(row => row.Length == 0 || row.Any(value => !double.IsFinite(value)))
                || DualCoefficients.Any(value => !double.IsFinite(value)))
                throw new InvalidDataException("Vetores de suporte SVR inválidos.");
            return;
        }
        throw new InvalidDataException($"Tipo de modelo ainda não suportado pelo Processador: {ModelType}.");
    }

    private static Tree ParseTree(JsonNode node)
    {
        int[] left = node["left"]!.AsArray().Select(v => v!.GetValue<int>()).ToArray();
        int[] right = node["right"]!.AsArray().Select(v => v!.GetValue<int>()).ToArray();
        int[] feature = node["feature"]!.AsArray().Select(v => v!.GetValue<int>()).ToArray();
        double[] threshold = node["threshold"]!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
        double[] value = node["value"]!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
        int count = left.Length;
        if (count is < 1 or > 10000000 || right.Length != count || feature.Length != count || threshold.Length != count || value.Length != count
            || threshold.Any(v => !double.IsFinite(v)) || value.Any(v => !double.IsFinite(v)))
            throw new InvalidDataException("Estrutura de árvore inválida.");
        return new(left, right, feature, threshold, value);
    }

    private static double PredictTree(Tree tree, double[] vector)
    {
        int node = 0, steps = 0;
        while (tree.Feature[node] >= 0)
        {
            int feature = tree.Feature[node];
            if (feature >= vector.Length || ++steps > tree.Left.Length) return double.NaN;
            node = vector[feature] <= tree.Threshold[node] ? tree.Left[node] : tree.Right[node];
            if (node < 0 || node >= tree.Left.Length) return double.NaN;
        }
        return tree.Value[node];
    }

    private double KernelValue(double[] vector, double[] support)
    {
        double dot = 0, squared = 0;
        for (int i = 0; i < vector.Length; i++)
        {
            dot += vector[i] * support[i];
            double delta = vector[i] - support[i]; squared += delta * delta;
        }
        return Kernel switch
        {
            "linear" => dot,
            "rbf" => Math.Exp(-Gamma * squared),
            "poly" => Math.Pow(Gamma * dot + Coef0, Degree),
            "sigmoid" => Math.Tanh(Gamma * dot + Coef0),
            _ => double.NaN,
        };
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

/// <summary>Scratch arrays owned by one sequential prediction worker.</summary>
public sealed class PredictionWorkspace
{
    internal double[] Input { get; }
    internal float[] LayerA { get; }
    internal float[] LayerB { get; }

    internal PredictionWorkspace(int inputs, int layerBufferSize)
    {
        Input = new double[inputs];
        LayerA = new float[layerBufferSize];
        LayerB = new float[layerBufferSize];
    }
}
