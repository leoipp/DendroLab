using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Dendro.Desktop;

internal sealed record RasterShapeClipOptions(string ShapePath, int FieldIndex, string OutputFolder, bool Crop,
    bool AllTouched, string Prefix, string Suffix, float? NoData);

internal static class RasterShapeClipDialog
{
    internal static RasterShapeClipOptions? Show(Window owner, string rasterPath, IReadOnlyList<string> fields,
        string shape, string suggestedOutput)
    {
        string output = suggestedOutput;
        var outputPath = new TextBox { Text = output, IsReadOnly = true, MinWidth = 270 };
        var column = new ComboBox { ItemsSource = fields, SelectedIndex = 0, MinWidth = 270 };
        var prefix = Ui.Input(); var suffix = Ui.Input(); var nodata = Ui.Input();
        var crop = new CheckBox { Content = "Recortar a extensão de cada feição", IsChecked = true, Margin = new Thickness(0, 5, 0, 5) };
        var allTouched = new CheckBox { Content = "Incluir pixels tocados pela borda", IsChecked = false, Margin = new Thickness(0, 5, 0, 5) };
        var form = new StackPanel { Margin = new Thickness(18) };
        form.Children.Add(Ui.Text("Raster de entrada: " + Path.GetFileName(rasterPath)));
        form.Children.Add(Ui.Text("Recortar cada polígono do shapefile em um GeoTIFF separado. Aceita ASCII, GeoTIFF float32 e imagens RGB/RGBA de 8 bits, incluindo compressão LZW, Deflate ou PackBits."));
        AddField(form, "Shapefile", Ui.Text(Path.GetFullPath(shape)));
        AddFileRow(form, "Pasta de saída", outputPath, "Selecionar…", () =>
        {
            string? picked = Ui.PickFolder();
            if (picked == null) return;
            output = Path.GetFullPath(picked); outputPath.Text = output;
        });
        AddField(form, "Campo para o nome do arquivo", column);
        AddField(form, "Prefixo", prefix); AddField(form, "Sufixo", suffix);
        AddField(form, "NoData de saída (opcional)", nodata);
        form.Children.Add(crop); form.Children.Add(allTouched);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        Window? dialog = null; RasterShapeClipOptions? result = null;
        buttons.Children.Add(Ui.Button("Cancelar", () => dialog!.DialogResult = false));
        buttons.Children.Add(Ui.Button("Recortar", () =>
        {
            if (string.IsNullOrWhiteSpace(output))
            { Ui.Alert("Dados incompletos", "Selecione uma pasta de saída.", Ui.MessageTone.Warning); return; }
            float? nodataValue = null;
            if (!string.IsNullOrWhiteSpace(nodata.Text))
            {
                if (!float.TryParse(nodata.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) || !float.IsFinite(parsed))
                { Ui.Alert("NoData inválido", "Informe um valor numérico finito ou deixe o campo vazio.", Ui.MessageTone.Warning); return; }
                nodataValue = parsed;
            }
            result = new RasterShapeClipOptions(shape, column.SelectedIndex, output, crop.IsChecked == true,
                allTouched.IsChecked == true, prefix.Text, suffix.Text, nodataValue);
            dialog!.DialogResult = true;
        }, Ui.ActionEmphasis.Primary));
        form.Children.Add(buttons);
        dialog = Ui.Dialog("Recortar raster por shapefile", form, 610, 560);
        dialog.Owner = owner;
        dialog.ShowDialog();
        return result;
    }

    private static void AddFileRow(Panel form, string label, TextBox path, string action, Action browse)
    {
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 4, 0, 4) };
        var button = Ui.Button(action, browse); button.Margin = new Thickness(6, 0, 0, 0); DockPanel.SetDock(button, Dock.Right); row.Children.Add(button); row.Children.Add(path);
        AddField(form, label, row);
    }

    private static void AddField(Panel form, string label, UIElement input)
    {
        form.Children.Add(Ui.Text(label)); form.Children.Add(input);
    }
}
