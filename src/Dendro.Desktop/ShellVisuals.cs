using System.Windows;

namespace Dendro.Desktop;

public static class ShellVisuals
{
    static ShellVisuals()
    {
        // AvalonDock assigns an explicit theme style to panes as it creates them,
        // including floating/restored panes. Apply the shell chrome at Loaded so
        // all hosts use the same single rounded frame without replacing docking templates.
        EventManager.RegisterClassHandler(typeof(AvalonDock.Controls.LayoutAnchorablePaneControl),
            FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, args) =>
            {
                if (!ReferenceEquals(sender, args.OriginalSource)) return;
                var pane = (AvalonDock.Controls.LayoutAnchorablePaneControl)sender;
                pane.Background = System.Windows.Media.Brushes.Transparent;
                pane.BorderThickness = new Thickness(0);
            }));
    }

    internal static void Initialize() { }
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon",
        typeof(CommandIconKind),
        typeof(ShellVisuals),
        new PropertyMetadata(CommandIconKind.Contents)
    );

    public static CommandIconKind GetIcon(DependencyObject element) =>
        (CommandIconKind)element.GetValue(IconProperty);

    public static void SetIcon(DependencyObject element, CommandIconKind value) =>
        element.SetValue(IconProperty, value);

    public static readonly DependencyProperty MissingProperty = DependencyProperty.RegisterAttached(
        "Missing", typeof(bool), typeof(ShellVisuals), new PropertyMetadata(false));

    public static bool GetMissing(DependencyObject element) =>
        (bool)element.GetValue(MissingProperty);

    public static void SetMissing(DependencyObject element, bool value) =>
        element.SetValue(MissingProperty, value);
}
