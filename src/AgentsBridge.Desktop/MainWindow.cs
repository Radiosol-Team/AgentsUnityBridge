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
    private static readonly IBrush CardBrush = new SolidColorBrush(Color.Parse("#1A202A"));
    private static readonly IBrush WarningCardBrush = new SolidColorBrush(Color.Parse("#3A2C16"));

    private readonly BridgeStatusClient _client;
    private readonly DaemonProcessLauncher _daemonLauncher = new();
    private readonly UnityHubProjectDiscovery _projectDiscovery = new();
    private readonly UnityEditorLauncher _unityLauncher = new();
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _lifetime = new();

    private readonly TextBlock _daemonState = StatusText();
    private readonly TextBlock _unityState = StatusText();
    private readonly TextBlock _summary = new() { Foreground = SecondaryBrush, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _projectPath = ValueText();
    private readonly TextBlock _unityVersion = ValueText();
    private readonly TextBlock _editorState = ValueText();
    private readonly TextBlock _connectedAt = ValueText();
    private readonly TextBlock _consoleState = ValueText();
    private readonly TextBlock _dirtyScenes = ValueText();
    private readonly TextBlock _testState = ValueText();
    private readonly TextBlock _testElapsed = ValueText();
    private readonly TextBlock _latestTest = ValueText();
    private readonly TextBlock _failedTests = ValueText();
    private readonly TextBlock _alertText = ValueText();
    private readonly Border _alert = new() { IsVisible = false };
    private readonly StackPanel _projectsPanel = new() { Spacing = 10 };
    private readonly Button _startDaemonButton = ActionButton("Start daemon");
    private readonly Button _runTestsButton = ActionButton("Run EditMode tests");
    private readonly Button _discardAndRunButton = ActionButton("Discard scene changes and run");
    private readonly Button _cancelTestButton = ActionButton("Cancel");

    private BridgeDashboard _dashboard = BridgeDashboard.Offline("Checking daemon status…");
    private bool _refreshing;
    private bool _statusLoaded;
    private bool _testRequestActive;
    private bool _awaitingSceneDecision;
    private DateTimeOffset? _testStartedAt;

    public MainWindow(BridgeStatusClient client)
    {
        _client = client;
        Title = "AgentsBridge";
        Width = 920;
        Height = 780;
        MinWidth = 720;
        MinHeight = 600;
        Background = new SolidColorBrush(Color.Parse("#11151C"));

        _discardAndRunButton.IsVisible = false;
        _cancelTestButton.IsVisible = false;
        WireActions();
        Content = BuildContent();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) =>
        {
            UpdateTestElapsed();
            await RefreshAsync();
        };

        Opened += async (_, _) =>
        {
            RefreshProjects();
            _timer.Start();
            await RefreshAsync();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _lifetime.Cancel();
            _lifetime.Dispose();
            _client.Dispose();
        };
    }

    private void WireActions()
    {
        _startDaemonButton.Click += async (_, _) => await StartDaemonAsync();
        _runTestsButton.Click += async (_, _) => await RunTestsAsync("cancel");
        _discardAndRunButton.Click += async (_, _) => await RunTestsAsync("discard");
        _cancelTestButton.Click += (_, _) => ClearSceneDecision();
    }

    private Control BuildContent()
    {
        Button refreshButton = ActionButton("Refresh");
        refreshButton.Click += async (_, _) =>
        {
            RefreshProjects();
            await RefreshAsync();
        };

        StackPanel headerActions = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _startDaemonButton, refreshButton }
        };

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
                headerActions
            }
        };
        Grid.SetColumn(headerActions, 1);

        Grid states = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,16,*"),
            Children =
            {
                StateCard("Daemon", "Stable API on localhost:9876", _daemonState),
                StateCard("Unity editor", "Live editor connector", _unityState)
            }
        };
        Grid.SetColumn(states.Children[1], 2);

        _alert.CornerRadius = new CornerRadius(10);
        _alert.Background = WarningCardBrush;
        _alert.Padding = new Thickness(18);
        _alert.Child = _alertText;

        Border editorDetails = Card(
            "Unity diagnostics",
            DetailRow("Project", _projectPath),
            DetailRow("Unity version", _unityVersion),
            DetailRow("Editor state", _editorState),
            DetailRow("Connected at", _connectedAt),
            DetailRow("Console", _consoleState),
            DetailRow("Dirty scenes", _dirtyScenes));

        StackPanel testButtons = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { _runTestsButton, _discardAndRunButton, _cancelTestButton }
        };
        Border tests = Card(
            "Unity tests",
            DetailRow("Activity", _testState),
            DetailRow("Elapsed", _testElapsed),
            DetailRow("Latest result", _latestTest),
            DetailRow("Failures", _failedTests),
            testButtons);

        Button refreshProjects = ActionButton("Refresh projects");
        refreshProjects.Click += (_, _) => RefreshProjects();
        Grid projectHeader = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { SectionTitle("Unity Hub projects"), refreshProjects }
        };
        Grid.SetColumn(refreshProjects, 1);

        Border projects = new()
        {
            CornerRadius = new CornerRadius(10),
            Background = CardBrush,
            Padding = new Thickness(20),
            Child = new StackPanel
            {
                Spacing = 14,
                Children = { projectHeader, _projectsPanel }
            }
        };

        return new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(28),
                Spacing = 20,
                Children = { header, states, _alert, editorDetails, tests, projects }
            }
        };
    }

    private async Task StartDaemonAsync()
    {
        _startDaemonButton.IsEnabled = false;
        _summary.Text = "Starting the daemon…";
        DaemonStartResult result = await _daemonLauncher.StartAsync(_lifetime.Token);
        _summary.Text = result.Message;
        await RefreshAsync();
    }

    private async Task RunTestsAsync(string sceneChanges)
    {
        if (_testRequestActive || !_dashboard.UnityConnected)
        {
            return;
        }

        _testRequestActive = true;
        _awaitingSceneDecision = false;
        _testStartedAt = DateTimeOffset.UtcNow;
        _runTestsButton.IsEnabled = false;
        _discardAndRunButton.IsVisible = false;
        _cancelTestButton.IsVisible = false;
        _testState.Text = sceneChanges == "discard"
            ? "Running EditMode tests after discarding scene changes"
            : "Running EditMode tests";
        _failedTests.Text = "—";

        TestRunResult result = await _client.RunEditModeTestsAsync(sceneChanges, _lifetime.Token);
        _testRequestActive = false;

        switch (result.Kind)
        {
            case TestRunResultKind.AwaitingSceneDecision:
                _awaitingSceneDecision = true;
                _testState.Text = "Waiting for a dirty-scene decision";
                _dirtyScenes.Text = FormatDirtyScenes(result.DirtyScenes);
                _discardAndRunButton.IsVisible = true;
                _cancelTestButton.IsVisible = true;
                ShowAlert("Unity has unsaved scenes. Discard reloads them from disk and permanently loses their in-memory changes.");
                break;
            case TestRunResultKind.Completed:
                _testState.Text = result.Summary!.Passed ? "Completed successfully" : "Completed with failures";
                RenderLatestRun(result.Summary);
                HideAlertIfSafe();
                break;
            default:
                _testState.Text = "Test request failed";
                _failedTests.Text = result.Error ?? "Unknown error";
                break;
        }

        _runTestsButton.IsEnabled = _dashboard.UnityConnected;
        await RefreshAsync();
    }

    private void ClearSceneDecision()
    {
        _awaitingSceneDecision = false;
        _testStartedAt = null;
        _testState.Text = "Cancelled before running";
        _discardAndRunButton.IsVisible = false;
        _cancelTestButton.IsVisible = false;
        HideAlertIfSafe();
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
            _dashboard = await _client.ReadAsync(_lifetime.Token);
            RenderDashboard(_dashboard);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Window shutdown cancels outstanding health checks.
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RenderDashboard(BridgeDashboard dashboard)
    {
        _statusLoaded = true;
        SetStatus(
            _daemonState,
            dashboard.DaemonConnected ? "Running" : "Offline",
            dashboard.DaemonConnected ? HealthyBrush : OfflineBrush);
        SetStatus(
            _unityState,
            dashboard.UnityConnected ? dashboard.EditorState : "Disconnected",
            dashboard.UnityConnected && dashboard.MainThreadResponsive ? HealthyBrush : WaitingBrush);

        _summary.Text = dashboard.Summary;
        _startDaemonButton.IsEnabled = !dashboard.DaemonConnected;
        _startDaemonButton.Content = dashboard.DaemonConnected ? "Daemon running" : "Start daemon";
        _projectPath.Text = dashboard.ProjectPath ?? "—";
        _unityVersion.Text = dashboard.UnityVersion ?? "—";
        _editorState.Text = dashboard.EditorState;
        _connectedAt.Text = FormatTimestamp(dashboard.ConnectedAtUtc);
        _consoleState.Text = dashboard.UnityConnected
            ? $"{dashboard.ErrorCount} errors, {dashboard.WarningCount} warnings"
            : "—";
        _dirtyScenes.Text = FormatDirtyScenes(dashboard.DirtyScenes);

        if (!_testRequestActive && !_awaitingSceneDecision)
        {
            _testState.Text = dashboard.TestRunActive ? "Running tests" : "Idle";
            _testStartedAt = dashboard.TestRunActive ? _testStartedAt ?? DateTimeOffset.UtcNow : null;
        }

        _runTestsButton.IsEnabled = dashboard.UnityConnected &&
                                    dashboard.MainThreadResponsive &&
                                    !_testRequestActive &&
                                    !_awaitingSceneDecision;

        if (dashboard.LatestRun is not null && !_testRequestActive)
        {
            RenderLatestRun(dashboard.LatestRun);
        }

        if (dashboard.PossibleModalDialog)
        {
            ShowAlert("Unity's main thread is not responding. Focus Unity and check for a popup or modal dialog.");
        }
        else if (dashboard.DirtyScenes.Count > 0 && !_awaitingSceneDecision)
        {
            ShowAlert("Unity has unsaved scene changes. Starting tests will ask whether to discard them.");
        }
        else
        {
            HideAlertIfSafe();
        }

        RenderProjects();
        UpdateTestElapsed();
    }

    private void RefreshProjects()
    {
        try
        {
            IReadOnlyList<UnityProjectInfo> projects = _projectDiscovery.Discover();
            _projectsPanel.Tag = projects;
            RenderProjects();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _projectsPanel.Children.Clear();
            _projectsPanel.Children.Add(new TextBlock
            {
                Text = "Could not read Unity Hub projects: " + exception.Message,
                Foreground = OfflineBrush,
                TextWrapping = TextWrapping.Wrap
            });
        }
    }

    private void RenderProjects()
    {
        if (_projectsPanel.Tag is not IReadOnlyList<UnityProjectInfo> projects)
        {
            return;
        }

        _projectsPanel.Children.Clear();
        if (projects.Count == 0)
        {
            _projectsPanel.Children.Add(new TextBlock
            {
                Text = "No projects were found in Unity Hub.",
                Foreground = SecondaryBrush
            });
            return;
        }

        foreach (UnityProjectInfo project in projects)
        {
            TextBlock title = new()
            {
                Text = project.Name,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.White
            };
            TextBlock details = new()
            {
                Text = $"Unity {project.UnityVersion ?? "unknown"}  •  {project.Path}",
                Foreground = project.Exists ? SecondaryBrush : OfflineBrush,
                TextWrapping = TextWrapping.Wrap
            };

            Button open = ActionButton("Open in Unity");
            open.IsEnabled = _statusLoaded && !_dashboard.UnityConnected && project.Exists;
            open.Click += (_, _) =>
            {
                LaunchResult result = _unityLauncher.Launch(project);
                _summary.Text = result.Message;
            };

            Grid row = new()
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children =
                {
                    new StackPanel { Spacing = 3, Children = { title, details } },
                    open
                }
            };
            Grid.SetColumn(open, 1);
            _projectsPanel.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.Parse("#222A36")),
                Padding = new Thickness(14),
                Child = row
            });
        }
    }

    private void RenderLatestRun(TestRunSummary run)
    {
        _latestTest.Text = run.Passed
            ? $"Passed — {run.PassCount} passed, {run.SkipCount} skipped"
            : $"Failed — {run.PassCount} passed, {run.FailCount} failed, {run.SkipCount} skipped";
        _latestTest.Foreground = run.Passed ? HealthyBrush : OfflineBrush;
        _failedTests.Text = run.FailedTestNames.Count == 0
            ? "—"
            : string.Join(Environment.NewLine, run.FailedTestNames);
    }

    private void UpdateTestElapsed()
    {
        _testElapsed.Text = _testStartedAt is null
            ? "—"
            : (DateTimeOffset.UtcNow - _testStartedAt.Value).ToString(@"hh\:mm\:ss");
    }

    private void ShowAlert(string message)
    {
        _alertText.Text = message;
        _alert.IsVisible = true;
    }

    private void HideAlertIfSafe()
    {
        if (!_awaitingSceneDecision && !_dashboard.PossibleModalDialog && _dashboard.DirtyScenes.Count == 0)
        {
            _alert.IsVisible = false;
        }
    }

    private static Border StateCard(string title, string caption, TextBlock status)
    {
        return new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = CardBrush,
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

    private static Border Card(string title, params Control[] controls)
    {
        StackPanel content = new() { Spacing = 14 };
        content.Children.Add(SectionTitle(title));
        foreach (Control control in controls)
        {
            content.Children.Add(control);
        }

        return new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = CardBrush,
            Padding = new Thickness(20),
            Child = content
        };
    }

    private static Grid DetailRow(string label, TextBlock value)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("150,*") };
        row.Children.Add(new TextBlock { Text = label, Foreground = SecondaryBrush });
        row.Children.Add(value);
        Grid.SetColumn(value, 1);
        return row;
    }

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 18,
        FontWeight = FontWeight.SemiBold,
        Foreground = Brushes.White
    };

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

    private static Button ActionButton(string text) => new()
    {
        Content = text,
        Padding = new Thickness(16, 8),
        VerticalAlignment = VerticalAlignment.Center
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

    private static string FormatDirtyScenes(IReadOnlyList<DirtySceneInfo> scenes)
    {
        return scenes.Count == 0
            ? "None"
            : string.Join(", ", scenes.Select(scene => scene.Path ?? scene.Name));
    }
}
