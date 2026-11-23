using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Dendro.Desktop;

internal static partial class DesktopTests
{
    private static async Task TestVectorSymbology(MainWindow shell, string root)
    {
        string path = Path.Combine(root, "simbologia.shp");
        WriteSamplingSource(path, "ID", "PROJETO");
        var factory = new NetTopologySuite.Geometries.GeometryFactory();
        var features = Enumerable.Range(0, 4).Select(i => new NetTopologySuite.Features.Feature(
            factory.CreatePolygon(new[] {
                new NetTopologySuite.Geometries.Coordinate(500000 + i * 250, 7500000),
                new NetTopologySuite.Geometries.Coordinate(500200 + i * 250, 7500000),
                new NetTopologySuite.Geometries.Coordinate(500200 + i * 250, 7500200),
                new NetTopologySuite.Geometries.Coordinate(500000 + i * 250, 7500200),
                new NetTopologySuite.Geometries.Coordinate(500000 + i * 250, 7500000) }),
            new NetTopologySuite.Features.AttributesTable { { "CLASSE", new[] { "A", "B", "", "A" }[i] } })).ToArray();
        NetTopologySuite.IO.Esri.Shapefile.WriteAllFeatures(features, path);
        shell.Session.AddFile(path);
        await shell.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var node = LogicalControls<TreeViewItem>(shell.ContentsTree).Single(item => item.Tag as string == path);
        Check(node.ContextMenu!.Items.OfType<MenuItem>().Any(item => item.Header as string == "Simbologia…"), "simbologia no menu vetorial");
        for (var parent = ItemsControl.ItemsControlFromItemContainer(node) as TreeViewItem; parent != null;
            parent = ItemsControl.ItemsControlFromItemContainer(parent) as TreeViewItem) parent.IsExpanded = true;
        node.IsSelected = true; node.Focus(); node.BringIntoView(); shell.UpdateLayout();
        node.ApplyTemplate();
        await shell.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var label = Controls<TextBlock>(node).Single(text => text.Name == "ContentLabel");
        var selection = Controls<Border>(node).Single(border => border.Name == "SelectionBackground");
        Check(((SolidColorBrush)selection.Background).Color == Color.FromRgb(33, 102, 157)
            && ((SolidColorBrush)label.Foreground).Color == Colors.White,
            $"seleção ativa usa azul escuro com texto branco (fundo {selection.Background}, texto {label.Foreground})");
        Capture(shell, Path.Combine(root, "conteudo-selecionado.png"));
        shell.ContentSearch.Focus();
        await shell.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(((SolidColorBrush)selection.Background).Color == Color.FromRgb(33, 102, 157)
            && ((SolidColorBrush)label.Foreground).Color == Colors.White, "seleção sem foco mantém contraste");
        node.IsSelected = false; ShellVisuals.SetMultiSelected(node, true);
        Check(((SolidColorBrush)selection.Background).Color == Color.FromRgb(33, 102, 157)
            && ((SolidColorBrush)label.Foreground).Color == Colors.White, "seleção múltipla mantém contraste");
        ShellVisuals.SetMultiSelected(node, false);
        static T Id<T>(DependencyObject parent, string id) where T : DependencyObject => LogicalControls<T>(parent)
            .Single(control => System.Windows.Automation.AutomationProperties.GetAutomationId(control) == id);
        static Button Action(DependencyObject parent, string text) => LogicalControls<Button>(parent).Single(b => b.Content as string == text);
        async Task<bool> Editor(Func<Window, Task> inspect)
        {
            _ = shell.Dispatcher.BeginInvoke(new Action(async () =>
            {
                var dialog = Application.Current.Windows.OfType<Window>().Single(w => w.Title == "Simbologia");
                await Until(() => Id<ComboBox>(dialog, "Symbology.Field").Items.Count > 0);
                await inspect(dialog);
            }), DispatcherPriority.ApplicationIdle);
            bool accepted = VectorSymbology.Edit(shell, shell.Session, path);
            await Task.CompletedTask;
            return accepted;
        }
        Check(await Editor(async dialog =>
        {
            var fill = Id<ColorSwatch>(dialog, "Symbology.Fill");
            _ = dialog.Dispatcher.BeginInvoke(new Action(() =>
            {
                var picker = Application.Current.Windows.OfType<Window>().Single(w => w.Title == "Selecionar cor");
                Check(Controls<HsvWheel>(picker).Any(), "seletor oferece roda de cores");
                Id<TextBox>(picker, "Color.Hex").Text = "#1E9161";
                Id<Slider>(picker, "Color.Transparency").Value = 35;
                Capture(picker, Path.Combine(root, "roda-cores.png"));
                Action(picker, "Usar cor").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }), DispatcherPriority.ApplicationIdle);
            fill.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(fill.SelectedColor.G == 145 && Math.Abs(fill.SelectedColor.A / 255d - .65) < .005,
                "cor e transparência são transferidas pelo seletor");
            var combos = LogicalControls<ComboBox>(dialog).ToArray();
            combos.Single(c => c.Items.Contains("Cruzada")).SelectedItem = "Cruzada";
            await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Capture(dialog, Path.Combine(root, "simbologia-editor.png"));
            Action(dialog, "Aplicar").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }), "aplicar confirma símbolo único");
        var single = VectorSymbology.Load(shell.Session, path);
        Check(single.FillColor == "#1E9161" && single.Pattern == "Cruzada" && Math.Abs(single.FillOpacity - .65) < .005,
            "símbolo único mantém transparência e hachura");
        Check((single with { FillEnabled = false }).Fill(1) == null && (single with { LineEnabled = false }).Pen(1) == null,
            "preenchimento e contorno podem ser desligados independentemente");
        Check(!await Editor(dialog =>
        {
            Id<ColorSwatch>(dialog, "Symbology.Fill").SetColor(Colors.Black);
            LogicalControls<CheckBox>(dialog).Single(c => c.Content as string == "Habilitar contorno").IsChecked = false;
            Action(dialog, "Cancelar").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return Task.CompletedTask;
        }) && JsonSerializer.Serialize(VectorSymbology.Load(shell.Session, path)) == JsonSerializer.Serialize(single), "cancelar descarta as alterações");
        Check(await Editor(async dialog =>
        {
            Id<ComboBox>(dialog, "Symbology.Renderer").SelectedIndex = 1;
            Id<ComboBox>(dialog, "Symbology.Field").SelectedItem = "CLASSE";
            Action(dialog, "Classificar valores").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => LogicalControls<ColorSwatch>(dialog).Count() == 5);
            Check(LogicalControls<TextBlock>(dialog).Any(t => t.Text == "(Vazio / nulo)"), "valores vazios têm categoria própria");
            var colors = LogicalControls<ColorSwatch>(dialog).Where(c => System.Windows.Automation.AutomationProperties.GetAutomationId(c).Length == 0).ToArray();
            colors[0].SetColor(Colors.Gray);
            colors[1].SetColor(Color.FromArgb(160, 255, 0, 0));
            colors[2].SetColor(Colors.Blue);
            await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Capture(dialog, Path.Combine(root, "simbologia-categorias.png"));
            Action(dialog, "Aplicar").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }), "valores únicos podem ser classificados e editados");
        var symbol = VectorSymbology.Load(shell.Session, path);
        Check(symbol.CategoryField == "CLASSE" && symbol.Categories!.Count == 3, "classificação usa valores distintos do DBF");
        Check(symbol.ResolveCategory("A")!.FillColor != symbol.ResolveCategory("B")!.FillColor, "categorias diferentes têm cores diferentes");
        Check((symbol with { ShowOtherValues = false }).ResolveCategory("NOVO") == null, "outros valores podem ser ocultos");
        Check(symbol.ResolveCategory("NOVO") != null, "outros valores usam o símbolo padrão");
        Check((symbol with { Categories = [new("A", "#FF0000", false)] }).ResolveCategory("A") == null,
            "desmarcar uma categoria oculta suas feições");
        var keys = VectorSymbology.ReadCategoryKeys(path, "CLASSE");
        Check(keys.SequenceEqual(new[] { "A", "B", "", "A" }), "atributos mantêm a ordem física dos registros");
        var layer = ShapefileReader.Read(path, "Talhões"); layer.Symbology = symbol; layer.CategoryKeys = keys;
        Check(layer.Features.Select(f => f.RecordIndex).SequenceEqual(new[] { 0, 1, 2, 3 }), "geometrias guardam o índice do DBF");
        using (var canvas = new GisMapCanvas { Width = 650, Height = 280, ShowBasemap = false })
        {
            canvas.SetLayers([layer], []); canvas.Measure(new Size(650, 280)); canvas.Arrange(new Rect(0, 0, 650, 280));
            canvas.Fit([layer.Extent]); Capture(canvas, Path.Combine(root, "simbologia-mapa.png"));
        }
        using (var gis = new GisView(shell.Session))
        {
            await gis.Initialize(path);
            var live = gis.VectorLayers.Single(l => l.Path == path);
            Check(live.CategoryKeys!.SequenceEqual(keys), "mapa carrega atributos da classificação salva");
            shell.Session.Meta.Obj("gis_symbology")[path] = JsonSerializer.SerializeToNode(symbol with { LineEnabled = false });
            await gis.RefreshSymbology(path);
            Check(!live.Symbology!.LineEnabled, "aplicar altera a simbologia da camada já carregada");
            shell.Session.Meta.Obj("gis_symbology")[path] = JsonSerializer.SerializeToNode(symbol);
        }
        string project = Path.Combine(root, "simbologia.dlb"); await shell.Session.Save(project);
        var restored = new ProjectSession(); await restored.Open(project);
        Check(JsonSerializer.Serialize(VectorSymbology.Load(restored, path)) == JsonSerializer.Serialize(symbol), "categorias persistem ao salvar e reabrir");
        foreach (string pattern in new[] { "Sólido", "Sem preenchimento", "Diagonal", "Cruzada", "Horizontal", "Vertical" })
            Check(((single with { Pattern = pattern }).Fill(.5) == null) == (pattern == "Sem preenchimento"), "padrão válido: " + pattern);
        var legacy = JsonSerializer.Deserialize<VectorSymbology>("{\"FillColor\":\"#123456\",\"FillOpacity\":0.4}")!;
        Check(legacy.FillEnabled && legacy.LineEnabled && legacy.LineOpacity == 1, "estilos antigos preservam padrões dos novos componentes");
        foreach (var color in new[] { Colors.Red, Colors.Green, Colors.Blue, Colors.White, Colors.Black, Colors.Gray })
        {
            var wheel = new HsvWheel(); wheel.SetColor(color);
            Check(wheel.Rgb == Color.FromRgb(color.R, color.G, color.B), "conversão HSV preserva " + color);
        }
        // Null shapes must not shift the physical attribute index of following features.
        byte[] original = File.ReadAllBytes(path);
        int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(original.AsSpan(104, 4)) * 2;
        var withNull = original.Take(100).Concat(new byte[] { 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0, 0 })
            .Concat(original.Skip(108 + length)).ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(withNull.AsSpan(24, 4), withNull.Length / 2);
        string nullShape = Path.Combine(root, "nulo.shp"); File.WriteAllBytes(nullShape, withNull);
        File.Copy(Path.ChangeExtension(path, ".prj"), Path.ChangeExtension(nullShape, ".prj"));
        Check(ShapefileReader.Read(nullShape, "Nulo").Features.Select(f => f.RecordIndex).SequenceEqual(new[] { 1, 2, 3 }),
            "registro nulo não desloca a classificação das geometrias seguintes");
    }
}
