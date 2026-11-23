using Dendro.Core.NeuralNetworks;
using Dendro.Core.Tabular;

namespace Dendro.Core.Tests;

public sealed class NativeModelTrainerTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "dendro-native-model-" + Guid.NewGuid().ToString("N"));
    public NativeModelTrainerTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);

    [Theory]
    [InlineData("neural_net")]
    [InlineData("random_forest")]
    [InlineData("gradient_boosting")]
    [InlineData("svr")]
    public void NativeTrainerProducesBundleLoadableByProcessor(string modelType)
    {
        string database = Path.Combine(folder, "treino.dlb");
        using (var writer = new CompactBankWriter(database))
        {
            string table = writer.AddTable(["X", "Grupo", "Y"], "Amostras");
            for (int row = 0; row < 80; row++)
            {
                double x = row / 10d;
                string group = row % 2 == 0 ? "A" : "B";
                writer.Add(table, [x, group, 2.5 * x + (group == "A" ? 3 : -2) + Math.Sin(x)]);
            }
            writer.Complete();
        }
        var options = new NativeModelTrainingOptions(modelType, "full", Seed: 17,
            HiddenLayers: [8], Epochs: 30, BatchSize: 16, Patience: 8,
            Estimators: 5, MaxDepth: 4, MinSamplesSplit: 2, MaxFeatures: "1.0",
            Subsample: .8, Kernel: "rbf", C: 1, Epsilon: .1);

        NativeModelBundle bundle = Assert.Single(NativeModelTrainer.Train(
            new TableSource(database, "Amostras"), "Y", ["X", "Grupo"], [], folder, options));
        Assert.True(File.Exists(bundle.Path));
        Assert.Equal(80, bundle.Rows);
        Assert.True(double.IsFinite(bundle.R2));
        Assert.True(double.IsFinite(bundle.Rmse));
        var model = NeuralModel.Load(bundle.Path);
        Assert.Equal(modelType, model.ModelType);
        double prediction = model.Predict([2.3], ["A"]);
        Assert.True(double.IsFinite(prediction));
        Assert.InRange(prediction, -100, 100);
    }
}
