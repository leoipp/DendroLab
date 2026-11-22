using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace Dendro.Desktop;

internal static partial class Ui
{
    private static void RemoveRepeatedDialogTitle(DependencyObject node, string title)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>().ToArray())
        {
            if (child is TextBlock text && text.FontSize >= 16
                && string.Equals(text.Text.Trim(), title.Trim(), StringComparison.CurrentCultureIgnoreCase)
                && node is Panel panel)
                panel.Children.Remove(text);
            else RemoveRepeatedDialogTitle(child, title);
        }
    }

    private static void HideDialogIcon(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        // Use the native dialog frame: keep the title and close button, omit the app icon.
        SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x00000001);
        SendMessage(handle, 0x0080, IntPtr.Zero, IntPtr.Zero);
        SendMessage(handle, 0x0080, new IntPtr(1), IntPtr.Zero);
        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x0027);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
