using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TradeFoundry.Desktop;

/// <summary>A small error dialog; the tray helper has no main window to host one.</summary>
internal sealed class MessageWindow : Window
{
    /// <param name="primaryAction">
    /// Optional button (label and callback) shown before a "Later" button, for questions such as
    /// "Install update?". Without it the window is a plain message with an OK button.
    /// </param>
    public MessageWindow(string message, string? details, WindowIcon? icon, (string Label, Action Invoke)? primaryAction = null)
    {
        Title = "TradeFoundry";
        Icon = icon;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Topmost = true;

        var content = new StackPanel { Margin = new Thickness(20), Spacing = 14 };
        content.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14
        });

        if (!string.IsNullOrWhiteSpace(details))
        {
            content.Children.Add(new TextBlock { Text = "Recent server output:", Opacity = 0.75 });
            content.Children.Add(new TextBox
            {
                Text = details,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"),
                FontSize = 12,
                MaxHeight = 220
            });
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };

        if (primaryAction is { } action)
        {
            var primary = new Button { Content = action.Label, MinWidth = 80, IsDefault = true };
            primary.Classes.Add("accent");
            primary.Click += (_, _) =>
            {
                Close();
                action.Invoke();
            };
            buttons.Children.Add(primary);
        }

        var close = new Button
        {
            Content = primaryAction is null ? "OK" : "Later",
            MinWidth = 80,
            IsDefault = primaryAction is null,
            IsCancel = true
        };
        close.Click += (_, _) => Close();
        buttons.Children.Add(close);
        content.Children.Add(buttons);

        Content = content;
    }
}
