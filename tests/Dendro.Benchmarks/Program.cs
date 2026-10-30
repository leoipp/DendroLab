using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Dendro.Core.Mathematics;

string destination = Path.GetFullPath(args.Length > 0 ? args[0] : ".build/benchmarks/csharp.json");
Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
var cases = new List<object>();
foreach (int count in new[] { 1, 10_000, 1_000_000 })
{
    var dap = new double[count];
    var height = new double[count];
    var output = new double[count];
    for (int i = 0; i < count; i++)
    {
        dap[i] = 50 + i * 37L % 950;
        height[i] = 50 + i * 19L % 450;
    }
    void Run() => InventoryMath.VolumeBatch(dap, height, -7.4, 1.93, 1.01, .83, output);
    var warm = Stopwatch.StartNew();
    while (warm.ElapsedMilliseconds < 800)
        Run();
    int repeats =
        count == 1 ? 20000
        : count == 10000 ? 10
        : 1;
    var samples = new double[17];
    long allocated = 0;
    for (int sample = 0; sample < samples.Length; sample++)
    {
        long before = GC.GetAllocatedBytesForCurrentThread(),
            start = Stopwatch.GetTimestamp();
        for (int repeat = 0; repeat < repeats; repeat++)
            Run();
        samples[sample] = Stopwatch.GetElapsedTime(start).TotalMilliseconds / repeats;
        allocated = Math.Max(
            allocated,
            (GC.GetAllocatedBytesForCurrentThread() - before) / repeats
        );
    }
    double median = samples.Order().ElementAt(samples.Length / 2);
    cases.Add(
        new
        {
            Rows = count,
            MedianMs = median,
            MinMs = samples.Min(),
            MaxMs = samples.Max(),
            ManagedAllocatedBytesPerBatch = allocated,
            Checksum = output.Sum(),
        }
    );
    Console.WriteLine($"{count:N0} árvores: {median:F6} ms; {allocated} bytes alocados/lote");
}
File.WriteAllText(
    destination,
    JsonSerializer.Serialize(
        new
        {
            Date = DateTimeOffset.Now,
            Runtime = RuntimeInformation.FrameworkDescription,
            Scope = "Núcleo C# atual: Schumacher–Hall e fator, sem IO ou RNA. A comparação histórica com C está documentada separadamente.",
            Cases = cases,
        },
        new JsonSerializerOptions { WriteIndented = true }
    )
);
