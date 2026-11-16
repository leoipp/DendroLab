using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Dendro.Desktop;

internal sealed class AppDatePicker : Grid
{
    private static readonly CultureInfo PortugueseCulture = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly string[] AcceptedFormats = ["dd/MM/yyyy", "d/M/yyyy", "d/MM/yyyy", "dd/M/yyyy"];
    private readonly TextBox input;
    private readonly System.Windows.Controls.Calendar calendar;
    private readonly Popup popup;
    private DateTime? committedDate;

    internal AppDatePicker()
    {
        Height = 35;
        Language = XmlLanguage.GetLanguage("pt-BR");

        input = Ui.Input();
        input.Margin = new Thickness(0);
        input.Padding = new Thickness(9, 5, 38, 5);
        input.VerticalContentAlignment = VerticalAlignment.Center;
        Children.Add(input);

        var toggle = new Button
        {
            Style = Application.Current.TryFindResource("CompactDateToggleButton") as Style,
            Width = 30,
            Height = 29,
            MinWidth = 30,
            MinHeight = 29,
            Padding = new Thickness(0),
            Margin = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Abrir calendário",
            Content = CalendarGlyph(),
        };
        System.Windows.Automation.AutomationProperties.SetName(toggle, "Abrir calendário");
        Children.Add(toggle);

        calendar = new System.Windows.Controls.Calendar
        {
            Style = Application.Current.TryFindResource("CompactDateCalendar") as Style,
            Language = Language,
            FirstDayOfWeek = DayOfWeek.Monday,
            SelectionMode = CalendarSelectionMode.SingleDate,
            DisplayMode = CalendarMode.Month,
            BorderThickness = new Thickness(0),
        };
        var popupFrame = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(214, 224, 236)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6),
            Child = calendar,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Color.FromRgb(35, 60, 82),
                Opacity = 0.16,
                BlurRadius = 14,
                ShadowDepth = 3,
            },
        };
        popup = new Popup
        {
            PlacementTarget = toggle,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = popupFrame,
        };
        calendar.SelectedDatesChanged += (_, _) =>
        {
            if (calendar.SelectedDate is DateTime date)
            {
                SelectedDate = date;
                popup.IsOpen = false;
            }
        };

        toggle.Click += (_, _) => ToggleCalendar();
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                UpdateCommittedDate();
                Keyboard.ClearFocus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                input.Text = committedDate?.ToString("dd/MM/yyyy", PortugueseCulture) ?? string.Empty;
                e.Handled = true;
            }
        };
    }

    internal DateTime? SelectedDate
    {
        get => TryParseDate(input.Text, out DateTime value) ? value : null;
        set
        {
            committedDate = value?.Date;
            input.Text = committedDate?.ToString("dd/MM/yyyy", PortugueseCulture) ?? string.Empty;
            calendar.SelectedDate = committedDate;
            if (committedDate is DateTime date) calendar.DisplayDate = date;
        }
    }

    private void ToggleCalendar()
    {
        if (popup.IsOpen)
        {
            popup.IsOpen = false;
            return;
        }
        DateTime? selected = SelectedDate;
        calendar.SelectedDate = selected;
        calendar.DisplayDate = selected ?? DateTime.Today;
        popup.IsOpen = true;
    }

    private void UpdateCommittedDate()
    {
        if (TryParseDate(input.Text, out DateTime value)) committedDate = value;
    }

    private static bool TryParseDate(string text, out DateTime value) =>
        DateTime.TryParseExact(text.Trim(), AcceptedFormats, PortugueseCulture, DateTimeStyles.None, out value);

    private static FrameworkElement CalendarGlyph()
    {
        var glyph = new Grid { Width = 14, Height = 14 };
        glyph.Children.Add(new Rectangle
        {
            RadiusX = 2,
            RadiusY = 2,
            Stroke = new SolidColorBrush(Color.FromRgb(72, 99, 122)),
            StrokeThickness = 1.2,
        });
        glyph.Children.Add(new Path
        {
            Data = Geometry.Parse("M2,5 L12,5 M4,1.5 L4,4 M10,1.5 L10,4 M4,8 L5.5,8 M7,8 L8.5,8 M10,8 L11,8 M4,10.5 L5.5,10.5 M7,10.5 L8.5,10.5"),
            Stroke = new SolidColorBrush(Color.FromRgb(72, 99, 122)),
            StrokeThickness = 1.1,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        });
        return glyph;
    }
}
