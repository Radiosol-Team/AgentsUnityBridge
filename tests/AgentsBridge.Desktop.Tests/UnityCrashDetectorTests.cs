using AgentsBridge.Local;
using Xunit;

namespace AgentsBridge.Desktop.Tests;

public sealed class UnityCrashDetectorTests
{
    [Fact]
    public void Read_WhenUnityStopsAndRecentCrashLogExists_ReturnsCrashReport()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-08T12:00:00Z");
        FakeCrashLogSource source = new(new UnityCrashLog(
            "C:\\Users\\tester\\AppData\\Local\\Unity\\Editor\\Editor.log",
            now.AddSeconds(-4),
            """
            Initialize engine version: 6000.3.11f1
            Crash!!!
            Fatal error in Mono
            Stacktrace:
            Managed frame line
            """));
        UnityCrashDetector detector = new(source);

        detector.Read(new UnityProcessSnapshot([new UnityEditorProcess(12, "radiosol - Unity", now.AddMinutes(-1))]), now);
        UnityCrashReport? report = detector.Read(new UnityProcessSnapshot([]), now.AddSeconds(2));

        Assert.NotNull(report);
        Assert.Contains("Fatal", report.Summary);
        Assert.Contains("Managed frame line", report.Summary);
        Assert.Equal(source.Log!.Path, report.LogPath);
    }

    [Fact]
    public void Summarize_WhenCrashHasSignalAndStack_ReturnsCompactEssentials()
    {
        string summary = UnityCrashReport.Summarize(
            """
            Some ordinary editor line
            Received signal SIGSEGV
            Stacktrace:
            Native frame UnityEditor.CoreModule
            Another frame
            """);

        Assert.Contains("Signal", summary);
        Assert.Contains("Native frame UnityEditor.CoreModule", summary);
    }

    [Fact]
    public void Discard_ClearsLatchedCrashAndResetsTrackingState()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-08T12:00:00Z");
        FakeCrashLogSource source = new(new UnityCrashLog(
            "C:\\Users\\tester\\AppData\\Local\\Unity\\Editor\\Editor.log",
            now,
            "Crash!!!"));
        UnityCrashDetector detector = new(source);

        detector.Read(new UnityProcessSnapshot([new UnityEditorProcess(12, "radiosol - Unity", now)]), now);
        Assert.NotNull(detector.Read(new UnityProcessSnapshot([]), now.AddSeconds(1)));

        detector.Discard();

        Assert.Null(detector.Read(new UnityProcessSnapshot([]), now.AddSeconds(2)));
    }

    private sealed class FakeCrashLogSource(UnityCrashLog? log) : IUnityCrashLogSource
    {
        public UnityCrashLog? Log { get; } = log;

        public UnityCrashLog? FindLatestCrashLog(DateTimeOffset nowUtc, DateTimeOffset? lastRunningAtUtc)
        {
            return Log;
        }
    }
}
