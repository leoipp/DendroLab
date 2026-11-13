using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Dendro.Desktop;

internal static partial class DesktopTests
{
    internal static async Task RunGisContents(string root)
    {
        checks = 0;
        Directory.CreateDirectory(root);
        var window = new MainWindow { WorkspacePath = Path.Combine(root, "layout.xml"), CloseConfirmation = () => Task.FromResult(true) };
        await window.Session.New("Camadas em conteúdos");
        window.Show();
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        foreach (var node in window.ContentsTree.Items.OfType<TreeViewItem>()) node.IsExpanded = false;
        window.ContentSearch.Text = "filtro que não corresponde às camadas";
        string shape = Path.Combine(root, "talhoes.shp"), raster = Path.Combine(root, "imagem.tif");
        window.Session.AddFile(shape);
        window.Session.AddFile(raster);
        window.RevealAddedContents([shape, raster]);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        foreach (var (title, path) in new[] { ("Dados vetoriais", shape), ("Mapas base", raster) })
        {
            var parent = window.ContentsTree.Items.OfType<TreeViewItem>().Single(node => node.Header?.ToString() == title);
            Check(parent.IsExpanded, title + " expandido após adicionar camada");
            Check(parent.Items.OfType<TreeViewItem>().Any(node => node.Tag is string value && value == path), path + " registrado na árvore");
        }
        Check(window.ContentSearch.Text.Length == 0, "filtro removido para exibir novos conteúdos");
        string second = Path.Combine(root, "outra_origem.shp"), external = Path.Combine(root, "externo.shp");
        WriteSamplingSource(shape, "REF_ID", "PROJETO");
        WriteSamplingSource(second, "TALHAO", "FAZENDA");
        WriteSamplingSource(external, "CODIGO", "REGIONAL");
        var sampling = new ForestSamplingView(window, [shape, second], _ => Task.CompletedTask);
        window.SetTool(sampling);
        await sampling.Initialize();
        ComboBox Combo(string id) => LogicalControls<ComboBox>(sampling).Single(control =>
            System.Windows.Automation.AutomationProperties.GetAutomationId(control) == id);
        var source = Combo("SamplingSource");
        var identifier = Combo("SamplingIdField");
        var project = Combo("SamplingProjectField");
        Check(!source.IsEditable && (string?)source.SelectedItem == shape, "combo mantém o shapefile inicial selecionado");
        Check((string?)identifier.SelectedItem == "REF_ID" && (string?)project.SelectedItem == "PROJETO", "campos iniciais carregados");
        source.SelectedIndex = 1;
        for (int attempt = 0; attempt < 100 && (!identifier.IsEnabled || !identifier.Items.Contains("TALHAO")); attempt++) await Task.Delay(50);
        Check((string?)source.SelectedItem == second, "escolha do segundo shapefile permanece selecionada");
        Check(identifier.IsEnabled && identifier.Items.Contains("TALHAO") && !identifier.Items.Contains("REF_ID"), "troca do combo carrega os campos da nova origem");
        await sampling.SelectSourcePath(external);
        Check((string?)source.SelectedItem == external && source.Items.Contains(external), "arquivo externo entra e fica selecionado no combo");
        Check(identifier.Items.Contains("CODIGO") && project.Items.Contains("REGIONAL"), "arquivo externo carrega seus campos");
        var method = Combo("SamplingMethod");
        method.SelectedIndex = 3;
        var grtsPanel = LogicalControls<GrtsSettingsPanel>(sampling).Single();
        Check(grtsPanel.Visibility == Visibility.Visible, "GRTS exibe parâmetros próprios");
        var exclusionRadius = LogicalControls<TextBox>(sampling).Single(control => System.Windows.Automation.AutomationProperties.GetAutomationId(control) == "SamplingExclusionRadius");
        Check(!exclusionRadius.IsEnabled, "raio do método antigo desabilitado no GRTS");
        var stratification = Combo("GrtsStratum");
        Check(stratification.Items.Contains("CODIGO") && stratification.Items.Contains("REGIONAL"), "estratificação usa campos do shapefile atual");
        stratification.SelectedIndex = 1;
        Combo("GrtsAllocation").SelectedIndex = 3;
        var configured = grtsPanel.Apply(new Dendro.Core.Sampling.ForestSamplingOptions());
        Check(configured.GrtsStratumField == "*" && configured.GrtsSampleSizeMode == Dendro.Core.Sampling.GrtsSampleSizeMode.Precision,
            "controles transferem desenho sem estratos e dimensionamento por precisão");
        Check(Dendro.Core.Sampling.ForestSampler.PlanGrtsSampleSize(configured.GrtsConfidencePercent, configured.GrtsRelativeErrorPercent, configured.GrtsCvPercent) == 35,
            "confiança, erro e CV da tela produzem dimensão esperada");
        Combo("GrtsAllocation").SelectedIndex = 2;
        Combo("GrtsProbability").SelectedIndex = 2;
        configured = grtsPanel.Apply(new Dendro.Core.Sampling.ForestSamplingOptions());
        Check(configured.GrtsStratumSampleSizes.ContainsKey("Todos") && configured.GrtsCategorySampleSizes.ContainsKey("Todos"), "tabelas carregam quantidades por estrato e categoria");
        method.SelectedIndex = 0;
        Check(grtsPanel.Visibility == Visibility.Collapsed && exclusionRadius.IsEnabled, "troca de método restaura parâmetros sistemáticos");
        window.Session.Meta.Arr("files").Clear();
        window.Session.AddFile(shape);
        var samples = new Dendro.Core.Sampling.ForestSamplingResult(
            new[]
            {
                new Dendro.Core.Sampling.ForestSample("T01", 1, 1, 500050, 7500050, new Dictionary<string, object?>()),
                new Dendro.Core.Sampling.ForestSample("T01", 2, 2, 500150, 7500150, new Dictionary<string, object?>()),
            }, 1, 0, 4, 2, shape, Dendro.Core.Sampling.ForestSamplingMethod.ExistingSystematicRounds, null);
        string generated = await window.RegisterSamplingContent(samples);
        Check(File.Exists(Path.ChangeExtension(generated, ".design.json")), "camada gerada guarda o desenho para exportação");
        var sampleNode = LogicalControls<TreeViewItem>(window.ContentsTree).Single(node => node.Tag is string value && value == generated);
        Check(File.Exists(generated) && sampleNode != null, "amostra salva e incluída em Conteúdos");
        Check(sampleNode!.ContextMenu!.Items.OfType<MenuItem>().Any(item => item.Header?.ToString() == "Exportar amostragem…"), "exportação no botão direito da amostra");
        Check(ShellVisuals.GetLayerToggle(sampleNode), "camada de amostra oferece checkbox");
        using (var gis = new GisView(window.Session))
        {
            await gis.Initialize(generated);
            var layer = gis.VectorLayers.Single(item => item.Path == generated);
            Check(layer.Features.Select(feature => feature.Round).SequenceEqual(new[] { 1, 2 }), "cores por rodada recuperadas da camada salva");
            Check(GisMapCanvas.SamplingPointBrush(1) != GisMapCanvas.SamplingPointBrush(2), "rodadas usam cores diferentes");
            window.UpdateLayout();
            static IEnumerable<DependencyObject> Visuals(DependencyObject parent)
            {
                for (int index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
                {
                    var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
                    yield return child;
                    foreach (var nested in Visuals(child)) yield return nested;
                }
            }
            var toggle = Visuals(sampleNode).OfType<CheckBox>().First(box => box.Name == "LayerVisibilityToggle");
            Check(toggle.Visibility == Visibility.Visible && toggle.IsChecked == true, "checkbox aparece marcado na árvore");
            toggle.IsChecked = false;
            Check(window.Session.Meta.Obj("gis_layer_visibility")[generated]!.GetValue<bool>() == false, "checkbox salva visibilidade");
            gis.SetLayerVisible(generated, false);
            Check(!layer.Visible, "camada desabilitada no mapa");
            await gis.Initialize(generated);
            Check(!gis.VectorLayers.Single(item => item.Path == generated).Visible, "visibilidade restaurada ao recarregar mapa");
        }
        await AuditManual(window, root);
        window.Close();
        File.WriteAllText(Path.Combine(root, "self-test.log"), $"{checks} verificações aprovadas.");
    }

    private static void WriteSamplingSource(string path, string id, string project)
    {
        var attributes = new NetTopologySuite.Features.AttributesTable { { id, "T01" }, { project, "P01" } };
        var shape = new NetTopologySuite.Geometries.GeometryFactory().CreatePolygon(new[]
        {
            new NetTopologySuite.Geometries.Coordinate(500000, 7500000),
            new NetTopologySuite.Geometries.Coordinate(500200, 7500000),
            new NetTopologySuite.Geometries.Coordinate(500200, 7500200),
            new NetTopologySuite.Geometries.Coordinate(500000, 7500200),
            new NetTopologySuite.Geometries.Coordinate(500000, 7500000),
        });
        NetTopologySuite.IO.Esri.Shapefile.WriteAllFeatures(
            new[] { new NetTopologySuite.Features.Feature(shape, attributes) }, path);
        File.WriteAllText(Path.ChangeExtension(path, ".prj"), "PROJCS[\"WGS 84 UTM 23S\",GEOGCS[\"WGS 84\",DATUM[\"WGS_1984\",SPHEROID[\"WGS 84\",6378137,298.257223563]],PRIMEM[\"Greenwich\",0],UNIT[\"degree\",0.0174532925199433]],PROJECTION[\"Transverse_Mercator\"],PARAMETER[\"latitude_of_origin\",0],PARAMETER[\"central_meridian\",-45],PARAMETER[\"scale_factor\",0.9996],PARAMETER[\"false_easting\",500000],PARAMETER[\"false_northing\",10000000],UNIT[\"metre\",1]]");
    }
}
