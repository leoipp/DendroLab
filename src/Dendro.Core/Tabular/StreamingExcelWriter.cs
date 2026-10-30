using System.Globalization;
using Dendro.Core.Spreadsheets;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Dendro.Core.Tabular;

public static class StreamingExcelWriter
{
    public static void Write(
        string path,
        string[] columns,
        IEnumerable<object?[]> rows,
        CancellationToken cancellation = default
    )
    {
        using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbook = doc.AddWorkbookPart();
        workbook.Workbook = new Workbook();
        var sheet = workbook.AddNewPart<WorksheetPart>();
        using (var writer = OpenXmlWriter.Create(sheet))
        {
            writer.WriteStartElement(new Worksheet());
            writer.WriteElement(
                new SheetViews(
                    new SheetView(
                        new Pane
                        {
                            VerticalSplit = 1,
                            TopLeftCell = "A2",
                            State = PaneStateValues.Frozen,
                        }
                    )
                    {
                        WorkbookViewId = 0,
                    }
                )
            );
            writer.WriteStartElement(new SheetData());
            uint index = 0;
            void Row(object?[] row)
            {
                if (++index > TableTools.ExcelRows)
                    throw new InvalidDataException(
                        "O resultado ultrapassa o limite de linhas do Excel."
                    );
                writer.WriteStartElement(new Row { RowIndex = index });
                for (int c = 0; c < row.Length; c++)
                {
                    if (row[c] == null)
                        continue;
                    var cell = new Cell
                    {
                        CellReference = WorkbookWriter.Reference((int)index - 1, c),
                    };
                    if (row[c] is double number && double.IsFinite(number))
                    {
                        cell.DataType = CellValues.Number;
                        cell.CellValue = new CellValue(
                            number.ToString("G17", CultureInfo.InvariantCulture)
                        );
                    }
                    else
                    {
                        cell.DataType = CellValues.InlineString;
                        cell.InlineString = new InlineString(
                            new Text(TableCursor.Text(row[c]))
                            {
                                Space = SpaceProcessingModeValues.Preserve,
                            }
                        );
                    }
                    writer.WriteElement(cell);
                }
                writer.WriteEndElement();
            }
            Row(columns.Cast<object?>().ToArray());
            foreach (var row in rows)
            {
                cancellation.ThrowIfCancellationRequested();
                Row(row);
            }
            writer.WriteEndElement();
            if (columns.Length > 0)
                writer.WriteElement(
                    new AutoFilter
                    {
                        Reference =
                            "A1:" + WorkbookWriter.Reference((int)index - 1, columns.Length - 1),
                    }
                );
            writer.WriteEndElement();
        }
        workbook.Workbook.Append(
            new Sheets(
                new Sheet
                {
                    Id = workbook.GetIdOfPart(sheet),
                    SheetId = 1,
                    Name = "Dados",
                }
            )
        );
        workbook.Workbook.Save();
    }
}
