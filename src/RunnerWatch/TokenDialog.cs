using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using RunnerWatch.Core;

namespace RunnerWatch;

/// <summary>Only shown when no credential on the machine works (e.g. Git configured with SSH only).</summary>
public static class TokenDialog
{
    const string NewTokenUrl = "https://github.com/settings/tokens/new?scopes=repo&description=RunnerWatch";

    public static Task<string?> AskAsync(string reason, Action<string> openUrl)
    {
        var tcs = new TaskCompletionSource<string?>();
        var box = new TextBox { PasswordChar = '•', Margin = new Thickness(0, 6, 0, 10), PlaceholderText = "ghp_… / github_pat_…" };

        var link = new HyperlinkButton { Content = L.T("token.create"), Padding = new Thickness(0) };
        link.Click += (_, _) => openUrl(NewTokenUrl);

        var info = new StackPanel { Spacing = 8 };
        info.Children.Add(Text(reason));
        info.Children.Add(Text(L.T("token.easiest")));
        info.Children.Add(Text(L.T("token.orPaste")));

        var ok = new Button { Content = L.T("token.save"), IsDefault = true, MinWidth = 90 };
        var cancel = new Button { Content = L.T("token.cancel"), IsCancel = true, MinWidth = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(16), Spacing = 4 };
        root.Children.Add(info);
        root.Children.Add(box);
        root.Children.Add(link);
        root.Children.Add(Text(L.T("token.fineGrained"), 11));
        root.Children.Add(Text(L.T("token.storage"), 11));
        root.Children.Add(new Border { Height = 8 });
        root.Children.Add(buttons);

        var win = new Window
        {
            Title = L.T("token.title"),
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            Content = root,
        };
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(box.Text)) return;
            tcs.TrySetResult(box.Text.Trim());
            win.Close();
        };
        cancel.Click += (_, _) => win.Close();
        win.Closed += (_, _) => tcs.TrySetResult(null);
        win.Opened += (_, _) => box.Focus();
        win.Show();
        return tcs.Task;
    }

    static TextBlock Text(string s, double size = 12) =>
        new() { Text = s, TextWrapping = TextWrapping.Wrap, FontSize = size, Opacity = size < 12 ? 0.75 : 1 };
}
