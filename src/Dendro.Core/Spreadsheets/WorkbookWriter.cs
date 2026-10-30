using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using A = DocumentFormat.OpenXml.Drawing;
using C = DocumentFormat.OpenXml.Drawing.Charts;
using D = DocumentFormat.OpenXml.Drawing.Spreadsheet;

namespace Dendro.Core.Spreadsheets;

public sealed record WorksheetData(string Name, DataTable Table);

public static class WorkbookWriter
{
    public static string Reference(int row, int column)
    {
        string name = "";
        for (int n = column + 1; n > 0; n = (n - 1) / 26)
            name = (char)('A' + (n - 1) % 26) + name;
        return name + (row + 1).ToString(CultureInfo.InvariantCulture);
    }

    public static void Write(
        string path,
        IReadOnlyList<WorksheetData> sheets,
        bool report = false,
        double green = 5,
        double yellow = 15,
        double alert = 10
    )
    {
        if (sheets.Count == 0)
            throw new InvalidDataException("Selecione pelo menos uma planilha para exportar.");
        using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var wb = doc.AddWorkbookPart();
        wb.Workbook = new Workbook();
        var styles = wb.AddNewPart<WorkbookStylesPart>();
        styles.Stylesheet = Styles();
        var collection = wb.Workbook.AppendChild(new Sheets());
        uint id = 0;
        foreach (var entry in sheets)
        {
            var table = entry.Table;
            if (table.Count > 1048576 || table.Width > 16384)
                throw new InvalidDataException("A planilha excede os limites do Excel.");
            var part = wb.AddNewPart<WorksheetPart>();
            var worksheet = new Worksheet();
            part.Worksheet = worksheet;
            var view = new SheetView { WorkbookViewId = 0 };
            view.Append(
                new Pane
                {
                    VerticalSplit = 1,
                    TopLeftCell = "A2",
                    State = PaneStateValues.Frozen,
                }
            );
            worksheet.Append(new SheetViews(view));
            worksheet.Append(
                new Columns(
                    new Column
                    {
                        Min = 1,
                        Max = (uint)Math.Max(1, table.Width),
                        Width = 19,
                        CustomWidth = true,
                    }
                )
            );
            var data = worksheet.AppendChild(new SheetData());
            int header = 0;
            for (int r = 0; r < table.Count; r++)
            {
                bool heading =
                    r == 0
                    || (
                        report
                        && new[] { "Lot", "Lote", "Stand", "H_obs", "Métrica" }.Contains(
                            table.Text(r, 0)
                        )
                    );
                if (heading)
                    header = r;
                var row = new Row { RowIndex = (uint)r + 1 };
                data.Append(row);
                for (int c = 0; c < table.Rows[r].Count; c++)
                {
                    var value = table.Value(r, c);
                    if (value is null || (value is double d && !double.IsFinite(d)))
                        continue;
                    var cell = new Cell { CellReference = Reference(r, c) };
                    row.Append(cell);
                    if (value is double or float or int or long or decimal)
                    {
                        string name = table.Text(header, c);
                        uint style =
                            name is "VMI" or "VMI_real" or "Gini" or "Homog" or "Erro Pad" ? 3u
                            : name == "IcV" || name.StartsWith("t(") ? 4u
                            : name
                                is "n"
                                    or "N_Parcelas"
                                    or "Fustes"
                                    or "Ano"
                                    or "Curva_Ano"
                                    or "Ano_Medicao"
                                ? 5u
                            : name is "Fustes_ha" or "Fustes/ha" ? 6u
                            : 2u;
                        cell.StyleIndex = style;
                        cell.DataType = CellValues.Number;
                        cell.CellValue = new CellValue(
                            Convert.ToString(value, CultureInfo.InvariantCulture)!
                        );
                    }
                    else
                    {
                        cell.StyleIndex = heading ? 1u : 0u;
                        cell.DataType = CellValues.InlineString;
                        cell.InlineString = new InlineString(
                            new Text(value.ToString()!)
                            {
                                Space = SpaceProcessingModeValues.Preserve,
                            }
                        );
                    }
                }
            }
            if (
                table.Count > 0
                && table.Width > 0
                && (
                    !report
                    || entry.Name == "AJUSTADO"
                    || entry.Name.StartsWith("RESUMO")
                    || entry.Name == "MODELOS APLICADOS"
                )
            )
                worksheet.Append(
                    new AutoFilter
                    {
                        Reference = "A1:" + Reference(table.Count - 1, table.Width - 1),
                    }
                );
            if (report && table.Count > 1)
            {
                int diff = table.Column("Dif_IFC_pct");
                if (diff >= 0)
                {
                    string first = Reference(1, diff);
                    var cf = new ConditionalFormatting
                    {
                        SequenceOfReferences = new ListValue<StringValue>
                        {
                            InnerText = first + ":" + Reference(table.Count - 1, diff),
                        },
                    };
                    string g = green.ToString("G17", CultureInfo.InvariantCulture),
                        y = yellow.ToString("G17", CultureInfo.InvariantCulture);
                    string[] rules =
                    [
                        $"AND(ISNUMBER({first}),ABS({first})<={g})",
                        $"AND(ISNUMBER({first}),ABS({first})>{g},ABS({first})<={y})",
                        $"AND(ISNUMBER({first}),ABS({first})>{y})",
                    ];
                    for (int i = 0; i < 3; i++)
                        cf.Append(
                            new ConditionalFormattingRule(new Formula(rules[i]))
                            {
                                Type = ConditionalFormatValues.Expression,
                                FormatId = (uint)i,
                                Priority = i + 1,
                            }
                        );
                    worksheet.Append(cf);
                }
                if (entry.Name == "ESTATISTICAS INV")
                    worksheet.Append(
                        new ConditionalFormatting(
                            new ConditionalFormattingRule(
                                new Formula(
                                    $"AND(ISNUMBER(Q3),Q3>{alert.ToString("G17", CultureInfo.InvariantCulture)})"
                                )
                            )
                            {
                                Type = ConditionalFormatValues.Expression,
                                FormatId = 2,
                                Priority = 1,
                            }
                        )
                        {
                            SequenceOfReferences = new ListValue<StringValue>
                            {
                                InnerText = "Q3:" + Reference(table.Count - 1, 16),
                            },
                        }
                    );
            }
            if (report && entry.Name == "Graficos" && table.Count > 1)
            {
                AddScatter(part, entry.Name, 0, 1, 1, table.Count - 1, "Altura × DAP", 0);
                AddScatter(part, entry.Name, 0, 2, 1, table.Count - 1, "Volume × DAP", 1);
            }
            if (report && entry.Name == "H Ajuste")
            {
                int raw = table.Rows.FindIndex(r => r.Count > 0 && r[0]?.ToString() == "H_obs");
                if (raw >= 0 && raw < table.Count - 1)
                {
                    AddScatter(
                        part,
                        entry.Name,
                        0,
                        1,
                        raw + 1,
                        table.Count - 1,
                        "Altura observada × estimada",
                        0
                    );
                    AddScatter(
                        part,
                        entry.Name,
                        0,
                        2,
                        raw + 1,
                        table.Count - 1,
                        "Resíduo (%) × altura observada",
                        1
                    );
                }
            }
            collection.Append(
                new Sheet
                {
                    Name = entry.Name,
                    SheetId = ++id,
                    Id = wb.GetIdOfPart(part),
                }
            );
            worksheet.Save();
        }
        wb.Workbook.Save();
    }

    private static Stylesheet Styles()
    {
        var s = new Stylesheet();
        s.Append(
            new NumberingFormats(
                new NumberingFormat { NumberFormatId = 164, FormatCode = "0.0000" },
                new NumberingFormat { NumberFormatId = 165, FormatCode = "0.000" },
                new NumberingFormat { NumberFormatId = 166, FormatCode = "0.0" }
            )
            {
                Count = 3,
            }
        );
        s.Append(
            new Fonts(
                new Font(new FontSize { Val = 11 }, new FontName { Val = "Calibri" }),
                new Font
                {
                    Bold = new Bold(),
                    Color = new Color { Rgb = "FFFFFFFF" },
                    FontSize = new FontSize { Val = 11 },
                    FontName = new FontName { Val = "Calibri" },
                }
            )
            {
                Count = 2,
            }
        );
        s.Append(
            new Fills(
                new Fill(new PatternFill { PatternType = PatternValues.None }),
                new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
                new Fill(
                    new PatternFill(
                        new ForegroundColor { Rgb = "FF2E7D32" },
                        new BackgroundColor { Indexed = 64 }
                    )
                    {
                        PatternType = PatternValues.Solid,
                    }
                )
            )
            {
                Count = 3,
            }
        );
        s.Append(new Borders(new Border()) { Count = 1 });
        s.Append(new CellStyleFormats(new CellFormat()) { Count = 1 });
        s.Append(
            new CellFormats(
                new CellFormat(),
                new CellFormat
                {
                    FontId = 1,
                    FillId = 2,
                    ApplyFill = true,
                    ApplyFont = true,
                },
                new CellFormat { NumberFormatId = 4, ApplyNumberFormat = true },
                new CellFormat { NumberFormatId = 164, ApplyNumberFormat = true },
                new CellFormat { NumberFormatId = 165, ApplyNumberFormat = true },
                new CellFormat { NumberFormatId = 1, ApplyNumberFormat = true },
                new CellFormat { NumberFormatId = 166, ApplyNumberFormat = true }
            )
            {
                Count = 7,
            }
        );
        s.Append(
            new CellStyles(
                new CellStyle
                {
                    Name = "Normal",
                    FormatId = 0,
                    BuiltinId = 0,
                }
            )
            {
                Count = 1,
            }
        );
        var diffs = new DifferentialFormats { Count = 3 };
        foreach (string color in new[] { "FFC6EFCE", "FFFFEB9C", "FFFFC7CE" })
            diffs.Append(
                new DifferentialFormat(
                    new Fill(
                        new PatternFill(
                            new ForegroundColor { Rgb = color },
                            new BackgroundColor { Indexed = 64 }
                        )
                        {
                            PatternType = PatternValues.Solid,
                        }
                    )
                )
            );
        s.Append(diffs);
        return s;
    }

    private static void AddScatter(
        WorksheetPart sheet,
        string name,
        int xc,
        int yc,
        int first,
        int last,
        string title,
        int index
    )
    {
        var drawings = sheet.DrawingsPart ?? sheet.AddNewPart<DrawingsPart>();
        if (drawings.WorksheetDrawing is null)
        {
            drawings.WorksheetDrawing = new D.WorksheetDrawing();
            sheet.Worksheet!.Append(new Drawing { Id = sheet.GetIdOfPart(drawings) });
        }
        var chart = drawings.AddNewPart<ChartPart>();
        var space = new C.ChartSpace();
        chart.ChartSpace = space;
        var content = space.AppendChild(new C.Chart());
        content.Append(
            new C.Title(
                new C.ChartText(
                    new C.RichText(
                        new A.BodyProperties(),
                        new A.ListStyle(),
                        new A.Paragraph(new A.Run(new A.Text(title)))
                    )
                )
            ),
            new C.AutoTitleDeleted { Val = false }
        );
        var plot = content.AppendChild(new C.PlotArea(new C.Layout()));
        var scatter = plot.AppendChild(
            new C.ScatterChart(new C.ScatterStyle { Val = C.ScatterStyleValues.Marker })
        );
        string escaped = "'" + name.Replace("'", "''") + "'!";
        var series = new C.ScatterChartSeries(
            new C.Index { Val = 0 },
            new C.Order { Val = 0 },
            new C.SeriesText(new C.NumericValue(title)),
            new C.Marker(new C.Symbol { Val = C.MarkerStyleValues.Circle }, new C.Size { Val = 3 }),
            new C.XValues(
                new C.NumberReference(
                    new C.Formula(escaped + Reference(first, xc) + ":" + Reference(last, xc))
                )
            ),
            new C.YValues(
                new C.NumberReference(
                    new C.Formula(escaped + Reference(first, yc) + ":" + Reference(last, yc))
                )
            ),
            new C.Smooth { Val = false }
        );
        scatter.Append(series, new C.AxisId { Val = 1 }, new C.AxisId { Val = 2 });
        for (uint axis = 1; axis <= 2; axis++)
            plot.Append(
                new C.ValueAxis(
                    new C.AxisId { Val = axis },
                    new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }),
                    new C.Delete { Val = false },
                    new C.AxisPosition
                    {
                        Val = axis == 1 ? C.AxisPositionValues.Bottom : C.AxisPositionValues.Left,
                    },
                    new C.NumberingFormat { FormatCode = "General", SourceLinked = true },
                    new C.TickLabelPosition { Val = C.TickLabelPositionValues.NextTo },
                    new C.CrossingAxis { Val = axis == 1 ? 2u : 1u },
                    new C.Crosses { Val = C.CrossesValues.AutoZero }
                )
            );
        content.Append(new C.PlotVisibleOnly { Val = true });
        space.Save();
        var frame = new D.GraphicFrame(
            new D.NonVisualGraphicFrameProperties(
                new D.NonVisualDrawingProperties { Id = (uint)index + 1, Name = title },
                new D.NonVisualGraphicFrameDrawingProperties()
            ),
            new D.Transform(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = 0, Cy = 0 }),
            new A.Graphic(
                new A.GraphicData(new C.ChartReference { Id = drawings.GetIdOfPart(chart) })
                {
                    Uri = "http://schemas.openxmlformats.org/drawingml/2006/chart",
                }
            )
        );
        drawings.WorksheetDrawing.Append(
            new D.OneCellAnchor(
                new D.FromMarker(
                    new D.ColumnId((4 + index * 11).ToString()),
                    new D.ColumnOffset("0"),
                    new D.RowId("1"),
                    new D.RowOffset("0")
                ),
                new D.Extent { Cx = 6500000, Cy = 4200000 },
                frame,
                new D.ClientData()
            )
        );
        drawings.WorksheetDrawing.Save();
    }
}
