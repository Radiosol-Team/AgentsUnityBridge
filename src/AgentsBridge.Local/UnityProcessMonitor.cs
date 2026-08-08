using System.Diagnostics;

namespace AgentsBridge.Local;

/// <summary>
/// Reads local Unity editor processes without depending on the bridge connection.
/// </summary>
public sealed class UnityProcessMonitor
{
    public UnityProcessSnapshot Read()
    {
        UnityEditorProcess[] processes = Process.GetProcessesByName("Unity")
            .Select(ToEditorProcess)
            .OrderBy(process => process.StartedAtUtc ?? DateTimeOffset.MaxValue)
            .ToArray();

        return new UnityProcessSnapshot(processes);
    }

    private static UnityEditorProcess ToEditorProcess(Process process)
    {
        using (process)
        {
            return new UnityEditorProcess(
                process.Id,
                ReadMainWindowTitle(process),
                ReadStartTime(process));
        }
    }

    private static string? ReadMainWindowTitle(Process process)
    {
        try
        {
            return string.IsNullOrWhiteSpace(process.MainWindowTitle)
                ? null
                : process.MainWindowTitle;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static DateTimeOffset? ReadStartTime(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

public sealed record UnityProcessSnapshot(IReadOnlyList<UnityEditorProcess> Processes)
{
    public bool IsRunning => Processes.Count > 0;

    public string Summary => Processes.Count switch
    {
        0 => "No Unity editor process is running.",
        1 => "Unity editor process is running.",
        _ => $"{Processes.Count} Unity editor processes are running."
    };

    public bool LooksLikeProject(string projectName)
    {
        return Processes.Any(process =>
            process.WindowTitle?.Contains(projectName, StringComparison.OrdinalIgnoreCase) == true);
    }
}

public sealed record UnityEditorProcess(
    int ProcessId,
    string? WindowTitle,
    DateTimeOffset? StartedAtUtc);
