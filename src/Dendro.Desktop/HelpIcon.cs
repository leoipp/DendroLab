using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Dendro.Desktop;

public sealed class HelpIcon : Border
{
    private readonly TextBlock explanation = new()
    {
        FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 440,
        Foreground = new SolidColorBrush(Color.FromRgb(39, 60, 78)),
    };
    private readonly ToolTip popup;
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(HelpIcon),
        new PropertyMetadata("", (d, e) => ((HelpIcon)d).UpdateText(e.NewValue as string ?? "")));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    private void UpdateText(string text)
    {
        explanation.Text = text;
        AutomationProperties.SetHelpText(this, text);
        Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    public HelpIcon() : this("") { }

    public HelpIcon(string text)
    {
        Width = Height = 20;
        CornerRadius = new CornerRadius(10);
        BorderThickness = new Thickness(1);
        BorderBrush = new SolidColorBrush(Color.FromRgb(151, 174, 194));
        Background = new SolidColorBrush(Color.FromRgb(243, 247, 251));
        Margin = new Thickness(8, 3, 2, 5);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        Cursor = Cursors.Help;
        Focusable = true;
        Child = new TextBlock
        {
            Text = "?", FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(54, 94, 127)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        popup = new ToolTip { Content = explanation, Padding = new Thickness(14), PlacementTarget = this,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Right, HorizontalOffset = 6,
            Background = Brushes.White, BorderBrush = BorderBrush, BorderThickness = new Thickness(1) };
        ToolTip = popup;
        ToolTipService.SetInitialShowDelay(this, 250);
        ToolTipService.SetShowDuration(this, 60000);
        AutomationProperties.SetName(this, "Ajuda");
        GotKeyboardFocus += (_, _) => popup.IsOpen = true;
        LostKeyboardFocus += (_, _) => popup.IsOpen = false;
        MouseLeftButtonUp += (_, _) => popup.IsOpen = !popup.IsOpen;
        Text = text;
        UpdateText(text);
    }
}
