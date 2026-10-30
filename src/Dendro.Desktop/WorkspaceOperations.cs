using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media.Imaging;
using Dendro.Core.Configuration;
using Dendro.Core.Processing;
using Dendro.Core.Regression;
using Dendro.Core.Spreadsheets;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

/// <summary>Application commands operate on a detached project snapshot; only successful results reach the editor.</summary>
internal static class WorkspaceOperations
{
    public static Task<JsonObject> Run(JsonObject request, Action<int, string>? progress = null, CancellationToken cancellation = default) =>
        Task.Run(() => Execute(request, progress, cancellation), cancellation);

    private static JsonObject Execute(JsonObject request, Action<int, string>? progress, CancellationToken cancellation)
    {
        var project = request["project"]?.DeepClone().AsObject();
        JsonNode? result = null;
        int index = (int)request.N("index", -1);
        switch (request.S("op"))
        {
            case "new":
                project = ProjectStore.Create(request.S("name"));
                break;
            case "open":
                project = ProjectStore.Read(request.S("path"));
                break;
            case "save":
                ProjectStore.Save(request.S("path"), project!);
                break;
            case "validate":
                ProjectStore.Validate(project!, true);
                break;
            case "metadata":
                result = ProjectModels.Metadata(project);
                break;
            case "file_remove":
                ProjectModels.RemoveFile(project!, index);
                break;
            case "model_set":
                ProjectModels.SetVolume(
                    project!,
                    index,
                    request.S("id"),
                    request.N("b0"),
                    request.N("b1"),
                    request.N("b2"),
                    request.S("name"),
                    request.S("formula"),
                    request["inputs"],
                    request["volume_type"]?.GetValue<string>()
                );
                break;
            case "curve_set":
                ProjectModels.SetGrowth(project!, request);
                break;
            case "curve_activate":
                ProjectModels.Activate(project!, index);
                break;
            case "model_remove":
                ProjectModels.Remove(project!, index, false);
                break;
            case "curve_remove":
                ProjectModels.Remove(project!, index, true);
                break;
            case "sheets":
                result = new JsonArray(
                    TableStore
                        .Tables(request.S("path"))
                        .Select(s => (JsonNode?)JsonValue.Create(s))
                        .ToArray()
                );
                break;
            case "table":
                if (request.N("limit") > 0)
                {
                    var page = TableStore.Page(
                        new(request.S("path"), request.S("sheet"), (int)request.N("header_row", 1)),
                        (long)request.N("offset"),
                        (int)request.N("limit")
                    );
                    result = page.Data.ToJson();
                    result["total"] = page.Total;
                }
                else
                    result = Read(request).ToJson();
                break;
            case "groups":
                var table = Read(request);
                int column = table.Require(request.S("column"));
                result = new JsonArray(
                    Enumerable
                        .Range(1, table.Count - 1)
                        .Select(r => table.Text(r, column))
                        .Distinct()
                        .Order(StringComparer.Ordinal)
                        .Select(s => (JsonNode?)JsonValue.Create(s))
                        .ToArray()
                );
                break;
            case "process":
                var summary = new InventoryProcessor().Process(
                    new(
                        request.S("input"),
                        request.S("config"),
                        request.S("registry"),
                        request.S("output"),
                        request.S("sheet")
                    ),
                    progress, cancellation
                );
                result = new JsonObject
                {
                    ["trees"] = summary.Trees,
                    ["plots"] = summary.Plots,
                    ["stands"] = summary.Stands,
                    ["volume"] = summary.Volume,
                    ["removed_duplicates"] = summary.RemovedDuplicates,
                    ["inventory_types"] = new JsonArray((summary.InventoryTypes ?? []).Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
                };
                break;
            case "chart":
            case "fit":
            case "export":
                var config = request["chart"]!.AsObject();
                var source = Read(config);
                var chart = new RegressionEngine(
                    project == null ? null : RegressionCatalog.Families(project)
                ).Generate(
                    source,
                    config,
                    request.S("op") == "fit" ? (int)request.N("model") : null
                );
                result = RegressionEngine.ToJson(chart);
                if (request.S("op") == "export")
                {
                    string path = request.S("output");
                    switch ((int)request.N("format"))
                    {
                        case 1:
                            ChartExport.Svg(path, chart);
                            break;
                        case 2:
                            ChartExport.Excel(path, chart, source);
                            break;
                        default:
                            ExportPng(path, result.AsObject(), chart.Chart);
                            break;
                    }
                }
                break;
            default:
                throw new InvalidOperationException("Operação desconhecida.");
        }
        return new()
        {
            ["ok"] = true,
            ["project"] = project,
            ["result"] = result,
        };
    }

    private static DataTable Read(JsonObject request) =>
        TableStore.IsBank(request.S("path"))
            ? TableStore.Read(new(request.S("path"), request.S("sheet")))
            : SpreadsheetReader.Read(
                request.S("path"),
                request.S("sheet"),
                (int)request.N("header_row", 1)
            );

    private static void ExportPng(string path, JsonObject data, JsonObject config)
    {
        int width = (int)config.N("width", 1600),
            height = (int)config.N("height", 1000);
        if (width is < 400 or > 8000 || height is < 300 or > 8000)
            throw new InvalidDataException(
                "Informe largura de 400–8.000 e altura de 300–8.000 pixels."
            );
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var plot = new ScatterPlot
                {
                    Data = data,
                    Config = config,
                    Width = width,
                    Height = height,
                };
                plot.Measure(new Size(width, height));
                plot.Arrange(new Rect(0, 0, width, height));
                plot.UpdateLayout();
                var bitmap = new RenderTargetBitmap(
                    width,
                    height,
                    96,
                    96,
                    System.Windows.Media.PixelFormats.Pbgra32
                );
                bitmap.Render(plot);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                ProjectStore.AtomicWrite(
                    path,
                    temp =>
                    {
                        using var stream = File.Create(temp);
                        encoder.Save(stream);
                    },
                    true
                );
            }
            catch (Exception ex)
            {
                error = ex;
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw new InvalidOperationException("Falha ao exportar PNG.", error);
    }
}
