using System.Globalization;
using System.Text;

namespace AgentsBridge.Local;

public sealed class UnityCrashDetector
{
    private readonly IUnityCrashLogSource _logSource;
    private readonly object _sync = new();
    private bool _wasRunning;
    private DateTimeOffset? _lastRunningAtUtc;
    private UnityCrashReport? _latestCrash;

    public UnityCrashDetector()
        : this(new UnityCrashLogSource())
    {
    }

    public UnityCrashDetector(IUnityCrashLogSource logSource)
    {
        _logSource = logSource;
    }

    public UnityCrashReport? Read(UnityProcessSnapshot snapshot, DateTimeOffset nowUtc)
    {
        lock (_sync)
        {
            if (snapshot.IsRunning)
            {
                _wasRunning = true;
                _lastRunningAtUtc = nowUtc;
                return _latestCrash;
            }

            if (_wasRunning)
            {
                UnityCrashLog? log = _logSource.FindLatestCrashLog(nowUtc, _lastRunningAtUtc);
                if (log is not null)
                {
                    _latestCrash = UnityCrashReport.FromLog(log, nowUtc);
                }
            }

            _wasRunning = false;
            return _latestCrash;
        }
    }

    public void Discard()
    {
        lock (_sync)
        {
            _latestCrash = null;
            _wasRunning = false;
            _lastRunningAtUtc = null;
        }
    }
}

public interface IUnityCrashLogSource
{
    UnityCrashLog? FindLatestCrashLog(DateTimeOffset nowUtc, DateTimeOffset? lastRunningAtUtc);
}

public sealed class UnityCrashLogSource : IUnityCrashLogSource
{
    private static readonly TimeSpan RecentCrashWindow = TimeSpan.FromMinutes(15);

    public UnityCrashLog? FindLatestCrashLog(DateTimeOffset nowUtc, DateTimeOffset? lastRunningAtUtc)
    {
        string? localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string tempPath = Path.GetTempPath();
        string? userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        IEnumerable<string> candidates = CandidatePaths(localAppData, tempPath, userProfile)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return candidates
            .Select(TryReadLog)
            .Where(log => log is not null)
            .Select(log => log!)
            .Where(log => IsRecentEnough(log, nowUtc, lastRunningAtUtc))
            .OrderByDescending(log => log.LastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static IEnumerable<string> CandidatePaths(string? localAppData, string tempPath, string? userProfile)
    {
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Unity", "Editor", "Editor.log");
            yield return Path.Combine(localAppData, "Unity", "Editor", "Editor-prev.log");

            foreach (string path in EnumerateCrashFiles(Path.Combine(localAppData, "Temp", "Unity", "Editor", "Crashes")))
            {
                yield return path;
            }

        }

        foreach (string path in EnumerateCrashFiles(Path.Combine(tempPath, "Unity", "Editor", "Crashes")))
        {
            yield return path;
        }

        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            foreach (string path in EnumerateCrashFiles(Path.Combine(userProfile, "AppData", "Local", "Temp", "Unity", "Editor", "Crashes")))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> EnumerateCrashFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                .Take(50)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static UnityCrashLog? TryReadLog(string path)
    {
        try
        {
            FileInfo file = new(path);
            if (!file.Exists || file.Length == 0)
            {
                return null;
            }

            string text = ReadTail(path, maxBytes: 256 * 1024);

            if (!LooksLikeCrashLog(text))
            {
                return null;
            }

            return new UnityCrashLog(path, file.LastWriteTimeUtc, text);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ReadTail(string path, int maxBytes)
    {
        using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        int bytesToRead = (int)Math.Min(maxBytes, stream.Length);
        stream.Seek(-bytesToRead, SeekOrigin.End);
        byte[] buffer = new byte[bytesToRead];
        int read = stream.Read(buffer, 0, buffer.Length);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static bool LooksLikeCrashLog(string text)
    {
        string[] markers =
        [
            "Crash!!!",
            "crash report",
            "fatal error",
            "Received signal",
            "Stacktrace:"
        ];

        return markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRecentEnough(UnityCrashLog log, DateTimeOffset nowUtc, DateTimeOffset? lastRunningAtUtc)
    {
        DateTimeOffset writeTime = log.LastWriteTimeUtc;
        if (nowUtc - writeTime > RecentCrashWindow)
        {
            return false;
        }

        return lastRunningAtUtc is null || writeTime >= lastRunningAtUtc.Value - TimeSpan.FromMinutes(2);
    }
}

public sealed record UnityCrashLog(
    string Path,
    DateTimeOffset LastWriteTimeUtc,
    string Text);

public sealed record UnityCrashReport(
    string LogPath,
    DateTimeOffset DetectedAtUtc,
    DateTimeOffset LogLastWriteTimeUtc,
    string Summary)
{
    public static UnityCrashReport FromLog(UnityCrashLog log, DateTimeOffset detectedAtUtc)
    {
        return new UnityCrashReport(
            log.Path,
            detectedAtUtc,
            log.LastWriteTimeUtc,
            Summarize(log.Text));
    }

    public static string Summarize(string text)
    {
        string[] lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (lines.Length == 0)
        {
            return "Crash log was found, but it did not contain readable text.";
        }

        List<string> selected = [];
        AddFirstMatch(lines, selected, "Unity version", "Initialize engine version");
        AddFirstMatch(lines, selected, "Fatal", "fatal");
        AddFirstMatch(lines, selected, "Signal", "Received signal");
        AddFirstMatch(lines, selected, "Exception", "exception");
        AddFirstMatch(lines, selected, "Crash", "Crash!!!");

        int stackIndex = Array.FindIndex(lines, line =>
            line.Contains("Stacktrace:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Native stacktrace:", StringComparison.OrdinalIgnoreCase));
        if (stackIndex >= 0)
        {
            selected.Add("Stack: " + FirstUsefulStackLine(lines.Skip(stackIndex + 1)));
        }

        if (selected.Count == 0)
        {
            selected.Add("Last log line: " + lines[^1]);
        }

        return string.Join(Environment.NewLine, selected.Select(TrimForSummary).Where(line => line.Length > 0).Take(6));
    }

    private static void AddFirstMatch(string[] lines, List<string> selected, string label, string marker)
    {
        string? match = lines.FirstOrDefault(line => line.Contains(marker, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(match))
        {
            selected.Add(label + ": " + match);
        }
    }

    private static string FirstUsefulStackLine(IEnumerable<string> lines)
    {
        return lines.FirstOrDefault(line =>
                   line.Length > 0 &&
                   !line.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                   !line.Equals("Stacktrace:", StringComparison.OrdinalIgnoreCase)) ??
               "No readable stack frame found in the log tail.";
    }

    private static string TrimForSummary(string line)
    {
        const int maxLength = 180;
        string normalized = string.Join(
            " ",
            line.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength].TrimEnd() + "...";
    }
}

public static class UnityCrashTimeFormatter
{
    public static string FormatAge(DateTimeOffset timestampUtc, DateTimeOffset nowUtc)
    {
        TimeSpan age = nowUtc - timestampUtc;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age.TotalSeconds < 60)
        {
            int seconds = Math.Max(1, (int)Math.Round(age.TotalSeconds));
            return seconds.ToString(CultureInfo.InvariantCulture) + " seconds ago";
        }

        if (age.TotalMinutes < 60)
        {
            int minutes = Math.Max(1, (int)Math.Round(age.TotalMinutes));
            return minutes.ToString(CultureInfo.InvariantCulture) + " minutes ago";
        }

        int hours = Math.Max(1, (int)Math.Round(age.TotalHours));
        return hours.ToString(CultureInfo.InvariantCulture) + " hours ago";
    }
}
