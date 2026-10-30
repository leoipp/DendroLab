using System.Globalization;
using System.Text;
using System.Windows.Data;

namespace Dendro.Desktop;

internal enum ColumnFormat { Automatic, Number, Text, Date, Time, DateTime }

internal sealed class TableCellFormatting(ColumnFormat format) : IValueConverter
{
    internal static ColumnFormat Guess(string name)
    {
        string normalized = string.Concat(name.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)).ToLowerInvariant();
        var words = normalized.Split([' ', '_', '-', '/', '(', ')'], StringSplitOptions.RemoveEmptyEntries);
        bool date = words.Any(w => w is "data" or "date" or "dt"), time = words.Any(w => w is "hora" or "horario" or "time" or "hr");
        return date && time ? ColumnFormat.DateTime : date ? ColumnFormat.Date : time ? ColumnFormat.Time : ColumnFormat.Automatic;
    }

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || value == DBNull.Value) return "";
        if (format is ColumnFormat.Automatic or ColumnFormat.Number) return value;
        if (format == ColumnFormat.Text) return value is IFormattable f ? f.ToString(null, culture) : value.ToString();
        DateTime date;
        if (value is DateTime actual) date = actual;
        else if (value is double serial && double.IsFinite(serial))
        {
            if (serial == 0 && format != ColumnFormat.Time) return value;
            try { date = DateTime.FromOADate(serial); } catch (ArgumentException) { return value; }
        }
        else if (!DateTime.TryParse(value.ToString(), culture, DateTimeStyles.None, out date)) return value;
        return date.ToString(format == ColumnFormat.Time ? "HH:mm:ss" : format == ColumnFormat.DateTime ? "dd/MM/yyyy HH:mm:ss" : "dd/MM/yyyy", culture);
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
