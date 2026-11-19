using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TradeFoundry.Desktop;

/// <summary>A small error dialog; the tray helper has no main window to host one.</summary>
internal sealed class MessageWindow : Window
{
    public MessageWindow(string message, string? details, WindowIcon? icon)
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

        var close = new Button
        {
            Content = "OK",
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 80,
            IsDefault = true,
            IsCancel = true
        };
        close.Click += (_, _) => Close();
        content.Children.Add(close);

        Content = content;
    }
}
