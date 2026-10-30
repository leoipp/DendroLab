using System.Text;
using ExcelDataReader;

namespace Dendro.Core.Spreadsheets;

public static class SpreadsheetReader
{
    static SpreadsheetReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static IExcelDataReader Open(string path) =>
        ExcelReaderFactory.CreateReader(
            File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite),
            new ExcelReaderConfiguration { LeaveOpen = false }
        );

    public static string[] Sheets(string path)
    {
        using var reader = Open(path);
        var result = new List<string>();
        do
        {
            result.Add(reader.Name);
        } while (reader.NextResult());
        return [.. result];
    }

    public static DataTable Read(string path, string sheet = "", int headerRow = 1)
    {
        using var reader = Open(path);
        do
        {
            if (sheet.Length > 0 && reader.Name != sheet)
                continue;
            var result = new DataTable();
            while (reader.Read())
            {
                var row = new object?[reader.FieldCount];
                for (int c = 0; c < row.Length; c++)
                    row[c] = reader.GetValue(c) switch
                    {
                        DateTime date => date.ToOADate(),
                        int n => (double)n,
                        bool b => b ? "TRUE" : "FALSE",
                        var value => value,
                    };
                result.Add(row);
            }
            while (result.Count > 0 && result.Rows[^1].All(v => v is null))
                result.Rows.RemoveAt(result.Count - 1);
            result.UseHeader(headerRow);
            return result;
        } while (reader.NextResult());
        throw new InvalidDataException($"Worksheet não encontrada: {sheet}.");
    }
}
