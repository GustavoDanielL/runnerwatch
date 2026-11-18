using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace RunnerWatch;

/// <summary>A small yes/no window for actions that change something on GitHub (e.g. cancelling a run).</summary>
public static class ConfirmDialog
{
    public static Task<bool> AskAsync(string title, string message, string yes, string no)
    {
        var tcs = new TaskCompletionSource<bool>();
        var ok = new Button { Content = yes, MinWidth = 90 };
        var cancel = new Button { Content = no, IsDefault = true, IsCancel = true, MinWidth = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(16), Spacing = 14 };
        root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(buttons);

        var win = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            Content = root,
        };
        ok.Click += (_, _) => { tcs.TrySetResult(true); win.Close(); };
        cancel.Click += (_, _) => win.Close();
        win.Closed += (_, _) => tcs.TrySetResult(false);
        win.Opened += (_, _) => cancel.Focus(); // the safe choice is the default
        win.Show();
        return tcs.Task;
    }
}
