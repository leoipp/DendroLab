using System.IO;
using System.Data;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Dendro.Core.Tabular;

namespace Dendro.Desktop;

/// <summary>Editable DBF attribute table for a vector shapefile.</summary>
internal sealed class VectorAttributeTable : DockPanel
{
    private readonly MainWindow owner;
    private readonly DbfTable dbf;
    private readonly DataTable table = new("Atributos");
    private readonly DataGrid grid = new() { AutoGenerateColumns = false, CanUserAddRows = false, IsReadOnly = false, SelectionUnit = DataGridSelectionUnit.FullRow, SelectionMode = DataGridSelectionMode.Extended, GridLinesVisibility = DataGridGridLinesVisibility.All, RowHeight = 26, HeadersVisibility = DataGridHeadersVisibility.All };
    private readonly TextBlock editStatus = Ui.Text("");
    private readonly TextBlock selectionStatus = Ui.Text("");
    private bool dirty;
    internal bool HasUnsavedChanges => dirty;

    internal VectorAttributeTable(MainWindow owner, string shapefile)
    {
        this.owner = owner;
        dbf = DbfTable.Read(Path.ChangeExtension(shapefile, ".dbf"));
        Background = Brushes.White;
        foreach (var field in dbf.Fields)
        {
            table.Columns.Add(field.Name, typeof(string));
            grid.Columns.Add(new DataGridTextColumn { Header = field.Name, Binding = new Binding("[" + field.Name + "]") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }, IsReadOnly = !field.Editable, MinWidth = 100, Width = DataGridLength.SizeToHeader });
        }
        foreach (var row in dbf.Rows)
        {
            var values = table.NewRow();
            for (int c = 0; c < dbf.Fields.Count; c++) values[c] = dbf.ReadValue(row, c);
            table.Rows.Add(values);
        }
        grid.ItemsSource = table.DefaultView;
        grid.CellEditEnding += (_, e) => { if (e.EditAction == DataGridEditAction.Commit) MarkDirty(); };
        grid.SelectionChanged += (_, _) => UpdateSelectionStatus();
        grid.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { grid.CommitEdit(); } };
        var toolbar = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(247, 249, 251)), LastChildFill = true, Margin = new Thickness(0, 0, 0, 1) };
        editStatus.Margin = new Thickness(8, 0, 12, 0); editStatus.VerticalAlignment = VerticalAlignment.Center; editStatus.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(editStatus, Dock.Right); toolbar.Children.Add(editStatus);
        var commandGroups = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2, 4, 2) };
        commandGroups.Children.Add(CommandGroup("Campo", CommandButton("Adicionar", CommandIconKind.Add, AddColumn), CommandButton("Calcular", CommandIconKind.CalculateColumn, Calculate)));
        commandGroups.Children.Add(new Border { Width = 1, Background = new SolidColorBrush(Color.FromRgb(212, 219, 226)), Margin = new Thickness(6, 2, 6, 2) });
        commandGroups.Children.Add(CommandGroup("Seleção", CommandButton("Selecionar por atributos", CommandIconKind.Filter, SelectByAttributes), CommandButton("Limpar", CommandIconKind.ClearSelection, () => grid.SelectedItems.Clear())));
        commandGroups.Children.Add(new Border { Width = 1, Background = new SolidColorBrush(Color.FromRgb(212, 219, 226)), Margin = new Thickness(6, 2, 6, 2) });
        commandGroups.Children.Add(CommandGroup("Edição", CommandButton("Salvar", CommandIconKind.Save, Save)));
        toolbar.Children.Add(commandGroups);
        var footer = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(247, 249, 251)), MinHeight = 30 };
        selectionStatus.Margin = new Thickness(10, 4, 10, 4); selectionStatus.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(selectionStatus, Dock.Left); footer.Children.Add(selectionStatus);
        var count = Ui.Text($"{dbf.Rows.Count:N0} registros · {dbf.Fields.Count} campos"); count.Margin = new Thickness(10, 4, 10, 4); count.VerticalAlignment = VerticalAlignment.Center; count.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(count, Dock.Right); footer.Children.Add(count);
        SetDock(toolbar, Dock.Top); SetDock(footer, Dock.Bottom); Children.Add(toolbar); Children.Add(footer); Children.Add(grid);
        UpdateSelectionStatus(); editStatus.Text = "Edição do DBF";
    }

    private static UIElement CommandGroup(string label, params UIElement[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var items = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var button in buttons) items.Children.Add(button);
        panel.Children.Add(items);
        panel.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(100, 115, 129)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0) });
        return panel;
    }

    private static Button CommandButton(string label, CommandIconKind icon, Action action)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new CommandIcon { Kind = icon, Width = 15, Height = 15, Margin = new Thickness(0, 0, 5, 0) });
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = content, Padding = new Thickness(7, 4, 7, 4), Margin = new Thickness(2), MinHeight = 29, ToolTip = label };
        button.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception ex) { Ui.Error(ex); }
        };
        return button;
    }

    private void UpdateSelectionStatus() => selectionStatus.Text = $"{grid.SelectedItems.Count:N0} de {dbf.Rows.Count:N0} selecionados";
    private void MarkDirty() { dirty = true; editStatus.Text = "Alterações não salvas"; }

    private void Save()
    {
        grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true);
        try
        {
            dbf.Write(table);
            dirty = false; owner.Session.Touch();
            editStatus.Text = "Edições salvas no arquivo DBF";
            owner.Log("Tabela de atributos salva no shapefile.");
        }
        catch (Exception ex) { Ui.Alert("Não foi possível salvar a tabela de atributos", ex.Message, Ui.MessageTone.Error); }
    }

    private void AddColumn()
    {
        var name = new TextBox { Text = "NovoCampo", MinWidth = 220 };
        var type = new ComboBox { ItemsSource = new[] { "Texto", "Número", "Data" }, SelectedIndex = 0, MinWidth = 220 };
        var form = new StackPanel { Margin = new Thickness(14) };
        form.Children.Add(Ui.Text("Nome (até 10 caracteres)")); form.Children.Add(name);
        form.Children.Add(Ui.Text("Tipo")); form.Children.Add(type);
        var accept = Ui.Button("Adicionar", () => { });
        var dialog = Ui.Dialog("Adicionar campo", Ui.WithToolbar(form, accept), 440, 300);
        accept.Click += (_, _) =>
        {
            try { char fieldType = type.SelectedIndex switch { 1 => 'N', 2 => 'D', _ => 'C' }; dbf.AddField(name.Text.Trim(), fieldType);
                table.Columns.Add(name.Text.Trim(), typeof(string));
                foreach (DataRow row in table.Rows) row[name.Text.Trim()] = "";
                grid.Columns.Add(new DataGridTextColumn { Header = name.Text.Trim(), Binding = new Binding("[" + name.Text.Trim() + "]") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }, MinWidth = 100, Width = DataGridLength.SizeToHeader });
                MarkDirty(); dialog.DialogResult = true;
            } catch (Exception ex) { Ui.Alert("Não foi possível adicionar o campo", ex.Message, Ui.MessageTone.Error); }
        };
        dialog.ShowDialog();
    }

    private void Calculate()
    {
        var name = new TextBox { Text = "Calculado", MinWidth = 340 };
        var expression = new TextBox { MinWidth = 440, MinHeight = 70, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        var type = new ComboBox { ItemsSource = new[] { "Texto", "Número", "Data" }, SelectedIndex = 0, MinWidth = 340 };
        var form = new StackPanel { Margin = new Thickness(14) };
        form.Children.Add(Ui.Text("Nome da nova coluna")); form.Children.Add(name);
        form.Children.Add(Ui.Text("Tipo do resultado")); form.Children.Add(type);
        form.Children.Add(Ui.Text("Expressão · use [Nome da coluna], +, -, *, / e &")); form.Children.Add(expression);
        form.Children.Add(Ui.Text("Exemplo: [Altura] * [DAP] / 100 ou [Gênero] & \"-\" & [Espécie]"));
        var accept = Ui.Button("Calcular", () => { });
        var dialog = Ui.Dialog("Calcular nova coluna", Ui.WithToolbar(Ui.Scroll(form), accept), 650, 430);
        accept.Click += (_, _) =>
        {
            try
            {
                string key = name.Text.Trim();
                if (key.Length == 0 || key.Length > 10 || key.Any(ch => !(char.IsLetterOrDigit(ch) || ch == '_'))) throw new InvalidDataException("O nome deve ter até 10 letras, números ou sublinhados.");
                if (table.Columns.Contains(key)) throw new InvalidDataException("Já existe uma coluna com esse nome. Informe outro nome.");
                var calc = new ColumnExpression(expression.Text, table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray());
                var output = new string[table.Rows.Count];
                for (int i = 0; i < table.Rows.Count; i++)
                {
                    try
                    {
                        object? value = calc.Evaluate(table.Rows[i].ItemArray);
                        output[i] = value switch { null => "", bool b => b ? "1" : "0", double n when double.IsFinite(n) => n.ToString("G17", CultureInfo.InvariantCulture), _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" };
                    }
                    catch (Exception ex) { throw new InvalidDataException($"Erro na linha {i + 1}: {ex.Message}", ex); }
                }
                char fieldType = type.SelectedIndex switch { 1 => 'N', 2 => 'D', _ => 'C' };
                if (fieldType == 'D')
                    for (int i = 0; i < output.Length; i++) output[i] = output[i].Length == 0 ? "" : DbfTable.DisplayDate(output[i]);
                int width = fieldType == 'D' ? 8 : fieldType == 'N' ? 20 : output.Select(v => dbf.EncodedLength(v)).DefaultIfEmpty(1).Max();
                if (width > 254) throw new InvalidDataException("Há resultados com mais de 254 bytes; divida o cálculo em colunas menores.");
                width = Math.Max(1, width);
                dbf.AddField(key, fieldType, width); table.Columns.Add(key, typeof(string));
                grid.Columns.Add(new DataGridTextColumn { Header = key, Binding = new Binding("[" + key + "]") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }, MinWidth = 100, Width = DataGridLength.SizeToHeader });
                for (int i = 0; i < output.Length; i++) table.Rows[i][key] = fieldType == 'N' && double.TryParse(output[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n.ToString("G17", CultureInfo.InvariantCulture) : output[i];
                MarkDirty(); dialog.DialogResult = true;
            }
            catch (Exception ex) { Ui.Alert("Não foi possível calcular a coluna", ex.Message, Ui.MessageTone.Error); }
        };
        dialog.ShowDialog();
    }

    private void SelectByAttributes()
    {
        var expression = new TextBox { MinWidth = 460, MinHeight = 55, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        var form = new StackPanel { Margin = new Thickness(14) };
        form.Children.Add(Ui.Text("Expressão que retorna verdadeiro/falso. Exemplo: [DAP] > 20")); form.Children.Add(expression);
        var accept = Ui.Button("Selecionar", () => { });
        var dialog = Ui.Dialog("Selecionar por atributos", Ui.WithToolbar(form, accept), 620, 260);
        accept.Click += (_, _) =>
        {
            try
            {
                var calc = new ColumnExpression(expression.Text, table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray());
                var selected = new List<object>();
                for (int i = 0; i < table.Rows.Count; i++)
                {
                    object? value;
                    try { value = calc.Evaluate(table.Rows[i].ItemArray); }
                    catch (Exception ex) { throw new InvalidDataException($"Erro na linha {i + 1}: {ex.Message}", ex); }
                    bool match = value is bool b ? b : value is double number ? number != 0 : string.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), "verdadeiro", StringComparison.OrdinalIgnoreCase);
                    if (match) selected.Add(grid.Items[i]);
                }
                grid.SelectedItems.Clear(); foreach (var row in selected) grid.SelectedItems.Add(row);
                dialog.DialogResult = true;
            }
            catch (Exception ex) { Ui.Alert("Não foi possível selecionar registros", ex.Message, Ui.MessageTone.Error); }
        };
        dialog.ShowDialog();
    }
}

/// <summary>Small DBF editor that retains untouched field bytes and deleted records.</summary>
internal sealed class DbfTable
{
    internal sealed record Field(string Name, char Type, byte Width, byte Decimals) { internal bool Editable => Type is 'C' or 'N' or 'F' or 'D' or 'L'; }
    private readonly string path;
    private readonly Encoding encoding;
    private readonly byte[] original;
    private int initialFieldCount;
    private int[] originalWidths = [];
    private byte[][] originalDescriptors = [];
    internal List<Field> Fields { get; } = [];
    internal List<byte[]> Rows { get; } = [];

    private DbfTable(string path, byte[] data, Encoding encoding, int headerLength, int recordLength)
    { this.path = path; original = data; this.encoding = encoding; _ = headerLength; _ = recordLength; }

    internal static DbfTable Read(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("O shapefile não possui um arquivo DBF ao lado do SHP.", path);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var data = File.ReadAllBytes(path);
        if (data.Length < 33) throw new InvalidDataException("Arquivo DBF incompleto.");
        int count = BitConverter.ToInt32(data, 4), header = BitConverter.ToUInt16(data, 8), recordLength = BitConverter.ToUInt16(data, 10);
        if (count < 0 || header < 33 || recordLength < 1 || header + (long)count * recordLength > data.Length) throw new InvalidDataException("Cabeçalho DBF inválido ou truncado.");
        Encoding enc = Encoding.GetEncoding(1252);
        string cpg = Path.ChangeExtension(path, ".cpg");
        if (File.Exists(cpg)) { string label = File.ReadAllText(cpg).Trim(); try { enc = int.TryParse(label, out int cp) ? Encoding.GetEncoding(cp) : Encoding.GetEncoding(label); } catch { } }
        var dbf = new DbfTable(path, data, enc, header, recordLength);
        int offset = 32, fieldOffset = 1;
        while (offset < header && data[offset] != 0x0D)
        {
            if (offset + 32 > header) throw new InvalidDataException("Descritor de campo DBF inválido.");
            string name = Encoding.ASCII.GetString(data, offset, 11).TrimEnd('\0', ' ');
            char type = (char)data[offset + 11]; byte width = data[offset + 16], decimals = data[offset + 17];
            if (string.IsNullOrWhiteSpace(name) || fieldOffset + width > recordLength) throw new InvalidDataException("Campo DBF inválido.");
            dbf.Fields.Add(new Field(name, type, width, decimals)); offset += 32; fieldOffset += width;
        }
        if (offset >= header || data[offset] != 0x0D || fieldOffset != recordLength) throw new InvalidDataException("Estrutura de campos DBF inconsistente.");
        dbf.initialFieldCount = dbf.Fields.Count;
        dbf.originalWidths = dbf.Fields.Select(f => (int)f.Width).ToArray();
        dbf.originalDescriptors = Enumerable.Range(0, dbf.initialFieldCount).Select(i => data.AsSpan(32 + i * 32, 32).ToArray()).ToArray();
        for (int i = 0; i < count; i++) dbf.Rows.Add(data.AsSpan(header + i * recordLength, recordLength).ToArray());
        return dbf;
    }

    internal string ReadValue(byte[] row, int column)
    {
        var f = Fields[column]; int offset = FieldOffset(column);
        string value = encoding.GetString(row, offset, f.Width).Trim();
        return f.Type == 'D' && value.Length == 8 ? DisplayDate(value) : value;
    }
    internal static string DisplayDate(string value)
    {
        if (DateTime.TryParseExact(value, ["yyyyMMdd", "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy"], CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out var date)) return date.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("pt-BR"));
        throw new InvalidDataException($"Data inválida: {value}.");
    }
    internal int EncodedLength(string value) => encoding.GetByteCount(value);
    internal void AddField(string name, char type, int? requestedWidth = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 10 || name.Any(ch => !(ch <= 127 && (char.IsLetterOrDigit(ch) || ch == '_')))) throw new InvalidDataException("Nome de campo DBF inválido: use até 10 letras, números ou sublinhados ASCII.");
        if (Fields.Count >= 255 || Fields.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("O campo já existe ou o limite de campos foi atingido.");
        int width = requestedWidth ?? (type switch { 'N' => 20, 'D' => 8, _ => 80 });
        if (width < 1 || width > 254) throw new InvalidDataException("O campo calculado ultrapassa o limite de 254 bytes do formato DBF.");
        int newLength = Fields.Sum(f => f.Width) + width + 1;
        if (newLength > ushort.MaxValue || Fields.Count * 32 + 33 > ushort.MaxValue) throw new InvalidDataException("O DBF não pode armazenar outro campo: limite de estrutura atingido.");
        Fields.Add(new Field(name, type, (byte)width, (byte)(type == 'N' ? 6 : 0)));
    }
    private int FieldOffset(int column) => 1 + Fields.Take(column).Sum(f => f.Width);

    internal void Write(DataTable table)
    {
        if (table.Rows.Count != Rows.Count || table.Columns.Count != Fields.Count) throw new InvalidDataException("A estrutura da tabela mudou durante a edição.");
        int headerLength = 33 + Fields.Count * 32, recordLength = 1 + Fields.Sum(f => f.Width);
        using var stream = new MemoryStream(headerLength + recordLength * Rows.Count + 1);
        byte[] header = original.AsSpan(0, 32).ToArray();
        header[1] = (byte)(DateTime.Now.Year - 1900); header[2] = (byte)DateTime.Now.Month; header[3] = (byte)DateTime.Now.Day;
        BitConverter.GetBytes(Rows.Count).CopyTo(header, 4); BitConverter.GetBytes((ushort)headerLength).CopyTo(header, 8); BitConverter.GetBytes((ushort)recordLength).CopyTo(header, 10);
        stream.Write(header);
        for (int i = 0; i < Fields.Count; i++)
        {
            var field = Fields[i];
            if (i < initialFieldCount) { stream.Write(originalDescriptors[i]); continue; }
            byte[] descriptor = new byte[32]; Encoding.ASCII.GetBytes(field.Name).CopyTo(descriptor, 0); descriptor[11] = (byte)field.Type; descriptor[16] = field.Width; descriptor[17] = field.Decimals; stream.Write(descriptor);
        }
        stream.WriteByte(0x0D);
        for (int r = 0; r < Rows.Count; r++)
        {
            byte[] record = new byte[recordLength]; record[0] = Rows[r][0]; int dst = 1;
            for (int c = 0; c < Fields.Count; c++)
            {
                Field f = Fields[c]; byte[] cell;
                string value = Convert.ToString(table.Rows[r][c], CultureInfo.CurrentCulture) ?? "";
                bool existed = c < initialFieldCount;
                string old = existed ? ReadOriginalValue(Rows[r], c) : "";
                if (existed && value == old) cell = Rows[r].AsSpan(FieldOffsetOriginal(c), originalWidths[c]).ToArray();
                else cell = Format(value, f);
                cell.CopyTo(record, dst); dst += f.Width;
            }
            stream.Write(record);
        }
        stream.WriteByte(0x1A);
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, stream.ToArray()); File.Move(temp, path, true);
    }
    private int FieldOffsetOriginal(int column) => 1 + originalWidths.Take(column).Sum();
    private string ReadOriginalValue(byte[] row, int col) => encoding.GetString(row, FieldOffsetOriginal(col), originalWidths[col]).Trim();
    private byte[] Format(string value, Field f)
    {
        if (f.Type == 'C')
        {
            byte[] raw = encoding.GetBytes(value); if (raw.Length > f.Width) throw new InvalidDataException($"O texto '{value}' excede {f.Width} bytes no campo {f.Name}.");
            return raw.Concat(Enumerable.Repeat((byte)' ', f.Width - raw.Length)).ToArray();
        }
        if (f.Type is 'N' or 'F')
        {
            if (value.Length == 0) return Enumerable.Repeat((byte)' ', f.Width).ToArray();
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out double n) && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out n)) throw new InvalidDataException($"Valor numérico inválido em {f.Name}: {value}.");
            string formatted = n.ToString(f.Decimals > 0 ? "F" + f.Decimals : "0", CultureInfo.InvariantCulture);
            if (formatted.Length > f.Width) throw new InvalidDataException($"Valor excede a largura do campo {f.Name}.");
            return encoding.GetBytes(formatted.PadLeft(f.Width));
        }
        if (f.Type == 'D')
        {
            string date = value.Length == 0 ? "        " : DateTime.TryParseExact(value, ["dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd", "yyyyMMdd"], CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out var parsed) ? parsed.ToString("yyyyMMdd") : throw new InvalidDataException($"Data inválida em {f.Name}. Use DD/MM/AAAA.");
            return Encoding.ASCII.GetBytes(date);
        }
        if (f.Type == 'L') return Encoding.ASCII.GetBytes(value.Length == 0 ? "?" : value.Equals("1") || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("T", StringComparison.OrdinalIgnoreCase) ? "T" : "F");
        throw new InvalidDataException($"O tipo de campo {f.Name} não permite edição.");
    }
}
