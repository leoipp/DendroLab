using System.Windows;

namespace Dendro.Desktop;

public static class ShellVisuals
{
    static ShellVisuals()
    {
        EventManager.RegisterClassHandler(typeof(AvalonDock.Controls.LayoutAnchorableControl),
            FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, args) =>
            {
                if (!ReferenceEquals(sender, args.OriginalSource)) return;
                var pane = (AvalonDock.Controls.LayoutAnchorableControl)sender;
                // Auto-hide creates a new control with a local theme Style value.
                // Apply our frame to the actual content control on each creation;
                // changing the outer host's style after creation does not update it.
                if (Application.Current.TryFindResource("RoundedAnchorable") is Style style)
                    pane.Style = style;
            }));
        // Auto-hidden panes are hosted outside the docking manager's resource scope.
        // Supply the same chrome explicitly to their temporary host.
        EventManager.RegisterClassHandler(typeof(AvalonDock.Controls.LayoutAutoHideWindowControl),
            FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, args) =>
            {
                if (!ReferenceEquals(sender, args.OriginalSource)) return;
                var host = (AvalonDock.Controls.LayoutAutoHideWindowControl)sender;
                if (Application.Current.TryFindResource("RoundedAnchorable") is Style style)
                    host.AnchorableStyle = style;
                host.Background = System.Windows.Media.Brushes.Transparent;
            }));
        EventManager.RegisterClassHandler(typeof(AvalonDock.Controls.LayoutAnchorControl),
            FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, args) =>
            {
                if (!ReferenceEquals(sender, args.OriginalSource)) return;
                var anchor = (AvalonDock.Controls.LayoutAnchorControl)sender;
                if (anchor.Model is AvalonDock.Layout.LayoutAnchorable pane)
                    EnsureAutoHideSize(pane);
            }));
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

    internal static void EnsureAutoHideSize(AvalonDock.Layout.LayoutAnchorable pane)
    {
        double minimumWidth = pane.ContentId == "tools" ? 320 : 260;
        double defaultWidth = pane.ContentId == "tools" ? 380 : 320;
        pane.AutoHideMinWidth = Math.Max(pane.AutoHideMinWidth, minimumWidth);
        if (pane.AutoHideWidth < minimumWidth) pane.AutoHideWidth = defaultWidth;
        pane.AutoHideMinHeight = Math.Max(pane.AutoHideMinHeight, 150);
        if (pane.AutoHideHeight < 150) pane.AutoHideHeight = 220;
    }

    internal static void Initialize() { }
    public static readonly DependencyProperty LayerToggleProperty = DependencyProperty.RegisterAttached(
        "LayerToggle", typeof(bool), typeof(ShellVisuals), new PropertyMetadata(false));
    public static bool GetLayerToggle(DependencyObject element) => (bool)element.GetValue(LayerToggleProperty);
    public static void SetLayerToggle(DependencyObject element, bool value) => element.SetValue(LayerToggleProperty, value);
    public static readonly DependencyProperty LayerVisibilityChangedProperty = DependencyProperty.RegisterAttached(
        "LayerVisibilityChanged", typeof(Action<bool>), typeof(ShellVisuals));
    public static void SetLayerVisibilityChanged(DependencyObject element, Action<bool> value) => element.SetValue(LayerVisibilityChangedProperty, value);
    public static readonly DependencyProperty LayerVisibleProperty = DependencyProperty.RegisterAttached(
        "LayerVisible", typeof(bool), typeof(ShellVisuals), new FrameworkPropertyMetadata(true,
            (element, args) => (element.GetValue(LayerVisibilityChangedProperty) as Action<bool>)?.Invoke((bool)args.NewValue)));
    public static bool GetLayerVisible(DependencyObject element) => (bool)element.GetValue(LayerVisibleProperty);
    public static void SetLayerVisible(DependencyObject element, bool value)
    {
        element.SetValue(LayerVisibleProperty, value);
    }
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

    public static readonly DependencyProperty MultiSelectedProperty = DependencyProperty.RegisterAttached(
        "MultiSelected", typeof(bool), typeof(ShellVisuals), new PropertyMetadata(false));

    public static bool GetMultiSelected(DependencyObject element) =>
        (bool)element.GetValue(MultiSelectedProperty);

    public static void SetMultiSelected(DependencyObject element, bool value) =>
        element.SetValue(MultiSelectedProperty, value);
}
