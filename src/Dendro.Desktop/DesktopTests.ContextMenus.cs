using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

internal static partial class DesktopTests
{
    internal static async Task RunContextMenuIconAudit(string root)
    {
        checks = 0;
        Directory.CreateDirectory(root);
        var window = new MainWindow
        {
            WorkspacePath = Path.Combine(root, "layout.xml"),
            CloseConfirmation = () => Task.FromResult(true),
        };
        await window.Session.New("Auditoria de menus");

        string spreadsheetPath = Path.Combine(root, "origem.xlsx");
        File.WriteAllBytes(spreadsheetPath, []);
        window.Session.AddFile(spreadsheetPath);
        string outputPath = Path.Combine(root, "resultado.dlb");
        using (var writer = new CompactBankWriter(outputPath))
        {
            string tableId = writer.AddTable(["Talhão", "Parcela", "DAP", "H"], "ESTATISTICAS INV");
            writer.Add(tableId, ["T01", "P01", 20d, 10d]);
            writer.Complete();
        }
        window.Session.Meta.Arr("processing_results").Add(new JsonObject
        {
            ["path"] = outputPath,
            ["source_path"] = spreadsheetPath,
            ["sheets"] = new JsonArray("ESTATISTICAS INV"),
        });
        window.Session.Meta.Arr("processing_results").Add(new JsonObject
        {
            ["id"] = "revision-icon-qa",
            ["inventory_type"] = "IFC",
            ["number"] = 1,
            ["status"] = "rascunho",
            ["source_path"] = spreadsheetPath,
            ["path"] = outputPath,
            ["sheets"] = new JsonArray("ESTATISTICAS INV"),
        });
        window.Session.Meta.Arr("charts").Add(new JsonObject
        {
            ["path"] = spreadsheetPath,
            ["sheet"] = "Dados",
            ["title"] = "Gráfico QA",
            ["x"] = "DAP",
            ["y"] = "H",
        });
        window.Session.Meta.Arr("analyses").Add(new JsonObject
        {
            ["id"] = "analysis-icon-qa",
            ["path"] = spreadsheetPath,
            ["title"] = "Análise QA",
            ["definition"] = new JsonObject { ["kind"] = "isolation_forest", ["sheet"] = "Dados" },
        });
        var spreadsheetGroup = new JsonObject
        {
            ["id"] = "spreadsheet-group",
            ["type"] = "spreadsheet",
            ["name"] = "Grupo de planilhas QA",
            ["paths"] = new JsonArray(spreadsheetPath),
        };
        window.Session.Meta.Arr("content_groups").Add(spreadsheetGroup);
        window.Session.Meta["gis_basemap"] = "openstreetmap";
        window.Session.Touch();
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        var nodes = LogicalControls<TreeViewItem>(window.ContentsTree).ToArray();
        var sheetsRoot = nodes.Single(node => node.Header?.ToString() == "Planilhas");
        var databasesRoot = nodes.Single(node => node.Header?.ToString() == "Bancos de dados");
        var vectorsRoot = nodes.Single(node => node.Header?.ToString() == "Dados vetoriais");
        var basemapsRoot = nodes.Single(node => node.Header?.ToString() == "Mapas base");
        var sourceNode = nodes.Single(node => node.Tag as string == spreadsheetPath);
        var groupNode = nodes.Single(node => node.Header?.ToString() == "Grupo de planilhas QA");
        var resultTableNode = nodes.First(node => node.Header?.ToString() == "ESTATISTICAS INV");
        var chartNode = nodes.Single(node => node.Header?.ToString() == "Gráfico QA");
        var analysisNode = nodes.Single(node => node.Header?.ToString() == "Análise QA");
        var revisionNode = nodes.Single(node => node.Tag is MainWindow.ProcessingVersionReference);
        var basemapNode = nodes.Single(node => node.Header?.ToString() == "OpenStreetMap");

        CheckMenuIcon(sheetsRoot, "Adicionar planilha…", CommandIconKind.Add);
        CheckMenuIcon(sheetsRoot, "Juntar tabelas…", CommandIconKind.Merge);
        CheckMenuIcon(sheetsRoot, "Criar grupo de planilhas…", CommandIconKind.Folder);
        CheckMenuIcon(databasesRoot, "Adicionar banco DendroLab…", CommandIconKind.Database);
        CheckMenuIcon(vectorsRoot, "Criar grupo de dados vetoriais…", CommandIconKind.Folder);
        CheckMenuIcon(basemapsRoot, "Adicionar raster TIFF ou shapefile…", CommandIconKind.Add);
        CheckMenuIcon(basemapNode, "Adicionar raster TIFF ou shapefile…", CommandIconKind.Add);
        Check(!ContentMenuItems(basemapsRoot.ContextMenu!).Concat(ContentMenuItems(basemapNode.ContextMenu!))
            .Any(item => item.Header?.ToString()?.Contains("Adicionar mapa base", StringComparison.OrdinalIgnoreCase) == true),
            "o mapa base mantém uma única referência e oferece adicionar camadas raster ou vetoriais");
        CheckMenuIcon(sourceNode, "Abrir tabela", CommandIconKind.Table);
        CheckMenuIcon(sourceNode, "Copiar para o projeto…", CommandIconKind.Copy);
        CheckMenuIcon(sourceNode, "Worksheet e cabeçalho…", CommandIconKind.Settings);
        CheckMenuIcon(sourceNode, "Gráfico de dispersão…", CommandIconKind.Chart);
        CheckMenuIcon(sourceNode, "Estratificar…", CommandIconKind.Stratify);
        CheckMenuIcon(sourceNode, "Gerar coluna…", CommandIconKind.CalculateColumn);
        CheckMenuIcon(sourceNode, "Isolation Forest…", CommandIconKind.Anomaly);
        CheckMenuIcon(sourceNode, "Limpeza integrada…", CommandIconKind.IntegratedCleaning);
        CheckMenuIcon(sourceNode, "Testes estatísticos…", CommandIconKind.Statistics);
        CheckMenuIcon(sourceNode, "Renomear…", CommandIconKind.Rename);
        CheckMenuIcon(sourceNode, "Remover", CommandIconKind.Remove);
        CheckMenuIcon(groupNode, "Renomear grupo…", CommandIconKind.Rename);
        CheckMenuIcon(groupNode, "Excluir grupo…", CommandIconKind.Remove);
        CheckMenuIcon(chartNode, "Abrir gráfico", CommandIconKind.Chart);
        CheckMenuIcon(chartNode, "Regressões…", CommandIconKind.Regression);
        CheckMenuIcon(chartNode, "Fronteira de eficiência…", CommandIconKind.Frontier);
        CheckMenuIcon(chartNode, "ICA e IMA…", CommandIconKind.Increments);
        CheckMenuIcon(chartNode, "Renomear…", CommandIconKind.Rename);
        CheckMenuIcon(chartNode, "Remover gráfico", CommandIconKind.Remove);
        CheckMenuIcon(analysisNode, "Abrir análise", CommandIconKind.Anomaly);
        CheckMenuIcon(analysisNode, "Abrir tabela de resultados", CommandIconKind.Table);
        CheckMenuIcon(revisionNode, "Revisar / aprovar / exportar…", CommandIconKind.ProcessingReview);
        CheckMenuIcon(revisionNode, "Remover revisão…", CommandIconKind.Remove);
        CheckMenuIcon(resultTableNode, "Abrir informações e gráficos", CommandIconKind.Chart);
        CheckMenuIcon(resultTableNode, "Remover tabela", CommandIconKind.Remove);
        var moveMenu = ContentMenuItem(sourceNode.ContextMenu!, "Mover para grupo");
        CheckMenuIcon(moveMenu, CommandIconKind.Folder);
        CheckMenuIcon(ContentMenuItem(moveMenu, "Sem grupo"), CommandIconKind.Ungroup);
        CheckMenuIcon(ContentMenuItem(moveMenu, "Grupo de planilhas QA"), CommandIconKind.Folder);
        foreach (var node in nodes)
            foreach (var item in ContentMenuItems(node.ContextMenu))
                Check(item.Icon is CommandIcon, $"menu ‘{item.Header}’ tem ícone da biblioteca do aplicativo");

        var regressionWorkspace = new ChartWorkspace(window, new Grid(), new StackPanel(), new StackPanel());
        var regressionDoc = window.Document("regression-icon-test", "Regressão de teste", regressionWorkspace);
        Check(window.RegressionModelsButton.IsEnabled && window.RegressionDataButton.IsEnabled,
            "atalhos de modelos e dados habilitados quando disponíveis na regressão ativa");
        window.Document("other-icon-test", "Outro documento", Ui.Text("Tabela"));
        Check(!window.RegressionModelsButton.IsEnabled && !window.RegressionDataButton.IsEnabled,
            "atalhos de regressão desabilitados fora do documento de regressão");
        regressionDoc.IsActive = true;
        Check(window.RegressionModelsButton.IsEnabled && window.RegressionDataButton.IsEnabled,
            "atalhos reabilitados ao retornar à regressão");
        regressionWorkspace.RemoveModels();
        Check(!window.RegressionModelsButton.IsEnabled && window.RegressionDataButton.IsEnabled,
            "atalho de modelos desabilitado ao remover sua aba");

        File.WriteAllText(Path.Combine(root, "self-test.log"), $"Menus da árvore e estados da regressão: {checks} verificações aprovadas.{Environment.NewLine}");
    }

    private static MenuItem ContentMenuItem(ItemsControl owner, string title) =>
        owner.Items.OfType<MenuItem>().SingleOrDefault(menu => menu.Header?.ToString() == title)
        ?? throw new InvalidOperationException($"Comando ‘{title}’ não encontrado. Disponíveis: {string.Join(", ", owner.Items.OfType<MenuItem>().Select(menu => menu.Header))}");

    private static void CheckMenuIcon(TreeViewItem owner, string title, CommandIconKind expected) =>
        CheckMenuIcon(ContentMenuItem(owner.ContextMenu!, title), expected);

    private static void CheckMenuIcon(MenuItem item, CommandIconKind expected) =>
        Check(item.Icon is CommandIcon icon && icon.Kind == expected,
            $"comando ‘{item.Header}’ usa ícone {expected}");

    private static IEnumerable<MenuItem> ContentMenuItems(ItemsControl? owner)
    {
        if (owner == null) yield break;
        foreach (var item in owner.Items.OfType<MenuItem>())
        {
            yield return item;
            foreach (var child in ContentMenuItems(item)) yield return child;
        }
    }
}
