using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AgentsBridge.Local;
using System.Diagnostics;

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
    private readonly UnityProcessMonitor _unityProcessMonitor = new();
    private readonly UnityCrashDetector _unityCrashDetector = new();
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _lifetime = new();

    private readonly TextBlock _daemonState = StatusText();
    private readonly TextBlock _unityState = StatusText();
    private readonly TextBlock _summary = new() { Foreground = SecondaryBrush, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _projectPath = ValueText();
    private readonly TextBlock _unityVersion = ValueText();
    private readonly TextBlock _editorState = ValueText();
    private readonly TextBlock _unityProcess = ValueText();
    private readonly TextBlock _crashSummary = ValueText();
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
    private readonly Button _forceBridgeButton = ActionButton("Force activate bridge");
    private readonly Button _runTestsButton = ActionButton("Run EditMode tests");
    private readonly Button _discardAndRunButton = ActionButton("Discard scene changes and run");
    private readonly Button _cancelTestButton = ActionButton("Cancel");
    private readonly Button _inspectCrashButton = ActionButton("Inspect");
    private readonly Button _discardCrashButton = ActionButton("Discard");
    private Grid _crashReportRow = null!;
    private StackPanel _crashActions = null!;

    private BridgeDashboard _dashboard = BridgeDashboard.Offline("Checking daemon status…");
    private UnityProcessSnapshot _unityProcesses = new([]);
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
        _forceBridgeButton.IsVisible = false;
        _inspectCrashButton.IsVisible = false;
        _discardCrashButton.IsVisible = false;
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
        _forceBridgeButton.Click += async (_, _) => await ForceActivateBridgeAsync();
        _runTestsButton.Click += async (_, _) => await RunTestsAsync("cancel");
        _discardAndRunButton.Click += async (_, _) => await RunTestsAsync("discard");
        _cancelTestButton.Click += (_, _) => ClearSceneDecision();
        _inspectCrashButton.Click += (_, _) => OpenCrashLog();
        _discardCrashButton.Click += async (_, _) => await DiscardCrashAsync();
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
            Children = { _startDaemonButton, _forceBridgeButton, refreshButton }
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

        _crashReportRow = DetailRow("Crash report", _crashSummary);
        _crashReportRow.IsVisible = false;
        _crashActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            IsVisible = false,
            Children = { _inspectCrashButton, _discardCrashButton }
        };

        Border editorDetails = Card(
            "Unity diagnostics",
            DetailRow("Project", _projectPath),
            DetailRow("Unity version", _unityVersion),
            DetailRow("Editor state", _editorState),
            DetailRow("Unity process", _unityProcess),
            _crashReportRow,
            _crashActions,
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

    private async Task ForceActivateBridgeAsync()
    {
        _forceBridgeButton.IsEnabled = false;
        _summary.Text = "Preparing Unity bridge activation...";

        DaemonStartResult daemon = await _daemonLauncher.StartAsync(_lifetime.Token);
        if (!daemon.Success)
        {
            _summary.Text = daemon.Message;
            _forceBridgeButton.IsEnabled = true;
            return;
        }

        UnityProjectInfo? project = FindBestActivationProject();
        if (project is null)
        {
            _summary.Text = "Unity is running, but I could not infer the project. In Unity, use Tools > Codex Bridge > Connect to AgentsBridge.";
            _forceBridgeButton.IsEnabled = true;
            await RefreshAsync();
            return;
        }

        LaunchResult launch = _unityLauncher.Launch(project, forceBridgeConnect: true);
        _summary.Text = launch.Message;
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
            _unityProcesses = _unityProcessMonitor.Read();
            UnityCrashReport? localCrashReport = _unityCrashDetector.Read(_unityProcesses, DateTimeOffset.UtcNow);
            if (_dashboard.CrashReport is null && localCrashReport is not null)
            {
                _dashboard = _dashboard.WithCrashReport(new UnityCrashReportInfo(
                    localCrashReport.LogPath,
                    localCrashReport.DetectedAtUtc.ToString("O"),
                    localCrashReport.LogLastWriteTimeUtc.ToString("O"),
                    localCrashReport.Summary));
            }

            if (!_dashboard.UnityConnected && _unityProcesses.IsRunning)
            {
                _dashboard = _dashboard.WithDisconnectedEditorState(
                    "Loading",
                    _dashboard.DaemonConnected
                        ? "Unity is running; it may still be loading or the bridge is not active yet."
                        : "Unity is running; start the daemon so the bridge can connect.");
            }

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
        bool crashedRecently = dashboard.CrashReport is not null && !dashboard.UnityConnected && !_unityProcesses.IsRunning;
        SetStatus(
            _daemonState,
            dashboard.DaemonConnected ? "Running" : "Offline",
            dashboard.DaemonConnected ? HealthyBrush : OfflineBrush);
        SetStatus(
            _unityState,
            crashedRecently
                ? "Crashed"
                : dashboard.UnityConnected
                ? dashboard.EditorState
                : _unityProcesses.IsRunning
                    ? "Unity online. Bridge disconnected"
                    : "Disconnected",
            crashedRecently
                ? OfflineBrush
                : dashboard.UnityConnected && dashboard.MainThreadResponsive
                ? HealthyBrush
                : _unityProcesses.IsRunning
                    ? WaitingBrush
                    : OfflineBrush);

        _summary.Text = dashboard.Summary;
        _startDaemonButton.IsEnabled = !dashboard.DaemonConnected;
        _startDaemonButton.Content = dashboard.DaemonConnected ? "Daemon running" : "Start daemon";
        _projectPath.Text = dashboard.ProjectPath ?? "—";
        _unityVersion.Text = dashboard.UnityVersion ?? "—";
        _editorState.Text = crashedRecently ? "Crashed" : dashboard.EditorState;
        _unityProcess.Text = FormatUnityProcesses(_unityProcesses);
        RenderCrashReport(crashedRecently ? dashboard.CrashReport : null);
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
        _forceBridgeButton.IsVisible = !dashboard.UnityConnected && _unityProcesses.IsRunning;
        _forceBridgeButton.IsEnabled = !_testRequestActive;

        if (dashboard.LatestRun is not null && !_testRequestActive)
        {
            RenderLatestRun(dashboard.LatestRun);
        }

        if (crashedRecently)
        {
            ShowAlert("Unity appears to have crashed recently. Inspect the crash log for the full report.");
        }
        else if (dashboard.PossibleModalDialog)
        {
            ShowAlert("Unity's main thread is not responding. Focus Unity and check for a popup or modal dialog.");
        }
        else if (!dashboard.UnityConnected && _unityProcesses.IsRunning)
        {
            ShowAlert(dashboard.DaemonConnected
                ? "Unity is running, but the bridge is disconnected. Force activation will reopen or focus a matching Hub project and ask Unity to connect."
                : "Unity is running, but the daemon is offline. Force activation will start the daemon first.");
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
                Text = ProjectDetails(project),
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

    private void RenderCrashReport(UnityCrashReportInfo? report)
    {
        if (report is null)
        {
            _crashSummary.Text = string.Empty;
            _crashSummary.Foreground = Brushes.White;
            _crashReportRow.IsVisible = false;
            _crashActions.IsVisible = false;
            _inspectCrashButton.IsVisible = false;
            _discardCrashButton.IsVisible = false;
            _inspectCrashButton.Tag = null;
            return;
        }

        _crashSummary.Text = $"Detected {FormatCrashAge(report)}{Environment.NewLine}{report.Summary}";
        _crashSummary.Foreground = OfflineBrush;
        _crashReportRow.IsVisible = true;
        _crashActions.IsVisible = true;
        _inspectCrashButton.Tag = report.LogPath;
        _inspectCrashButton.IsVisible = true;
        _discardCrashButton.IsVisible = true;
    }

    private void OpenCrashLog()
    {
        if (_inspectCrashButton.Tag is not string path || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            ShowAlert("Could not open the crash log: " + exception.Message);
        }
    }

    private async Task DiscardCrashAsync()
    {
        _discardCrashButton.IsEnabled = false;
        bool daemonDiscarded = !_dashboard.DaemonConnected ||
                               await _client.DiscardCrashAsync(_lifetime.Token);

        _unityCrashDetector.Discard();
        _dashboard = _dashboard.WithoutCrashReport();
        RenderDashboard(_dashboard);

        if (!daemonDiscarded)
        {
            ShowAlert("The local crash state was discarded, but the daemon could not be reached to reset its state.");
        }

        _discardCrashButton.IsEnabled = true;
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

    private static string FormatCrashAge(UnityCrashReportInfo report)
    {
        return DateTimeOffset.TryParse(report.DetectedAtUtc, out DateTimeOffset detectedAt)
            ? UnityCrashTimeFormatter.FormatAge(detectedAt, DateTimeOffset.UtcNow)
            : "recently";
    }

    private static string FormatDirtyScenes(IReadOnlyList<DirtySceneInfo> scenes)
    {
        return scenes.Count == 0
            ? "None"
            : string.Join(", ", scenes.Select(scene => scene.Path ?? scene.Name));
    }

    private UnityProjectInfo? FindBestActivationProject()
    {
        if (_projectsPanel.Tag is not IReadOnlyList<UnityProjectInfo> projects)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(_dashboard.ProjectPath))
        {
            UnityProjectInfo? connectedProject = projects.FirstOrDefault(project =>
                string.Equals(project.Path, _dashboard.ProjectPath, StringComparison.OrdinalIgnoreCase));
            if (connectedProject is not null)
            {
                return connectedProject;
            }
        }

        UnityProjectInfo? titledProject = projects.FirstOrDefault(project =>
            project.Exists && _unityProcesses.LooksLikeProject(project.Name));
        if (titledProject is not null)
        {
            return titledProject;
        }

        UnityProjectInfo[] existingProjects = projects.Where(project => project.Exists).Take(2).ToArray();
        return existingProjects.Length == 1 ? existingProjects[0] : null;
    }

    private string ProjectDetails(UnityProjectInfo project)
    {
        string details = $"Unity {project.UnityVersion ?? "unknown"}  -  {project.Path}";
        return !_dashboard.UnityConnected && _unityProcesses.LooksLikeProject(project.Name)
            ? details + "  -  running"
            : details;
    }

    private static string FormatUnityProcesses(UnityProcessSnapshot snapshot)
    {
        if (!snapshot.IsRunning)
        {
            return "Not running";
        }

        string titles = string.Join(
            Environment.NewLine,
            snapshot.Processes
                .Select(process => process.WindowTitle)
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .Take(3));

        return string.IsNullOrWhiteSpace(titles)
            ? snapshot.Summary
            : $"{snapshot.Summary}{Environment.NewLine}{titles}";
    }
}
