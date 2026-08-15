using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AgentsBridge.Desktop;

internal sealed class UpdatePromptWindow : Window
{
    internal UpdatePromptWindow(AvailableRelease release)
    {
        Title = "Unity Agents Bridge update";
        Width = 520;
        Height = 330;
        MinWidth = 420;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;

        Button install = new()
        {
            Content = "Install update",
            Padding = new Thickness(18, 9)
        };
        Button later = new()
        {
            Content = "Later",
            Padding = new Thickness(18, 9)
        };
        install.Click += (_, _) => Close(true);
        later.Click += (_, _) => Close(false);

        Content = new StackPanel
        {
            Margin = new Thickness(28),
            Spacing = 18,
            Children =
            {
                new TextBlock
                {
                    Text = $"{release.DisplayName} is available",
                    FontSize = 23,
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = "Unity Agents Bridge can download and install this update now. After you approve, no further action is needed.",
                    TextWrapping = TextWrapping.Wrap
                },
                new ScrollViewer
                {
                    MaxHeight = 125,
                    Content = new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(release.ReleaseNotes)
                            ? "No release notes were provided."
                            : release.ReleaseNotes,
                        Foreground = Brushes.Gray,
                        TextWrapping = TextWrapping.Wrap
                    }
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { later, install }
                }
            }
        };
    }
}
