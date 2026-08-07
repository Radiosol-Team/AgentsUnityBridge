using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AgentsBridge.Desktop;

internal sealed class MainWindow : Window
{
    private static readonly IBrush HealthyBrush = new SolidColorBrush(Color.Parse("#38C172"));
    private static readonly IBrush WaitingBrush = new SolidColorBrush(Color.Parse("#F5A623"));
    private static readonly IBrush OfflineBrush = new SolidColorBrush(Color.Parse("#E55353"));
    private static readonly IBrush SecondaryBrush = new SolidColorBrush(Color.Parse("#9AA4B2"));

    private readonly BridgeStatusClient _client;
    private readonly DispatcherTimer _timer;
    private readonly TextBlock _daemonState = StatusText();
    private readonly TextBlock _unityState = StatusText();
    private readonly TextBlock _summary = new() { Foreground = SecondaryBrush };
    private readonly TextBlock _projectPath = ValueText();
    private readonly TextBlock _unityVersion = ValueText();
    private readonly TextBlock _connectedAt = ValueText();
    private bool _refreshing;

    public MainWindow(BridgeStatusClient client)
    {
        _client = client;
        Title = "AgentsBridge";
        Width = 760;
        Height = 480;
        MinWidth = 620;
        MinHeight = 420;
        Background = new SolidColorBrush(Color.Parse("#11151C"));

        Content = BuildContent();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        Opened += async (_, _) =>
        {
            _timer.Start();
            await RefreshAsync();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _client.Dispose();
        };
    }

    private Control BuildContent()
    {
        Button refreshButton = new()
        {
            Content = "Refresh",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(18, 8)
        };
        refreshButton.Click += async (_, _) => await RefreshAsync();

        Grid header = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children =
            {
                new StackPanel
                {
                    Spacing = 5,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "AgentsBridge",
                            FontSize = 30,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = Brushes.White
                        },
                        _summary
                    }
                },
                refreshButton
            }
        };
        Grid.SetColumn(refreshButton, 1);

        Grid states = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,16,*"),
            Children =
            {
                StateCard("Daemon", "Stable API on localhost:9876", _daemonState),
                StateCard("Unity editor", "Outbound editor connector", _unityState)
            }
        };
        Grid.SetColumn(states.Children[1], 2);

        Border details = new()
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.Parse("#1A202A")),
            Padding = new Thickness(20),
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    DetailRow("Project", _projectPath),
                    DetailRow("Unity version", _unityVersion),
                    DetailRow("Connected at", _connectedAt)
                }
            }
        };

        return new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(28),
                Spacing = 24,
                Children = { header, states, details }
            }
        };
    }

    private async Task RefreshAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            BridgeHealth health = await _client.ReadAsync(CancellationToken.None);
            SetStatus(_daemonState, health.DaemonConnected ? "Running" : "Offline", health.DaemonConnected ? HealthyBrush : OfflineBrush);
            SetStatus(
                _unityState,
                health.UnityConnected ? "Connected" : "Disconnected",
                health.UnityConnected ? HealthyBrush : WaitingBrush);

            _summary.Text = health.Summary;
            _projectPath.Text = health.ProjectPath ?? "—";
            _unityVersion.Text = health.UnityVersion ?? "—";
            _connectedAt.Text = FormatTimestamp(health.ConnectedAtUtc);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private static Border StateCard(string title, string caption, TextBlock status)
    {
        return new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.Parse("#1A202A")),
            Padding = new Thickness(20),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = title, FontSize = 17, Foreground = Brushes.White },
                    new TextBlock { Text = caption, Foreground = SecondaryBrush },
                    status
                }
            }
        };
    }

    private static Grid DetailRow(string label, TextBlock value)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("140,*") };
        row.Children.Add(new TextBlock { Text = label, Foreground = SecondaryBrush });
        row.Children.Add(value);
        Grid.SetColumn(value, 1);
        return row;
    }

    private static TextBlock StatusText() => new()
    {
        Text = "Checking…",
        FontSize = 21,
        FontWeight = FontWeight.SemiBold
    };

    private static TextBlock ValueText() => new()
    {
        Foreground = Brushes.White,
        TextWrapping = TextWrapping.Wrap
    };

    private static void SetStatus(TextBlock text, string value, IBrush brush)
    {
        text.Text = value;
        text.Foreground = brush;
    }

    private static string FormatTimestamp(string? raw)
    {
        return DateTimeOffset.TryParse(raw, out DateTimeOffset timestamp)
            ? timestamp.ToLocalTime().ToString("g")
            : "—";
    }
}
