using System.Diagnostics;

namespace AgentsBridge.Desktop;

/// <summary>
/// Starts the daemon as a detached child process and waits until its health endpoint responds.
/// It supports both release packages and the repository's development output layout.
/// </summary>
internal sealed class DaemonProcessLauncher
{
    internal async Task<DaemonStartResult> StartAsync(CancellationToken cancellationToken)
    {
        if (await IsHealthyAsync(cancellationToken))
        {
            return DaemonStartResult.Successful("The daemon is already running.");
        }

        DaemonCommand? command = FindCommand();
        if (command is null)
        {
            return DaemonStartResult.Failed(
                "The daemon executable was not found. Build or install the complete AgentsBridge package.");
        }

        ProcessStartInfo startInfo = new(command.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = command.WorkingDirectory
        };
        foreach (string argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process? process = Process.Start(startInfo);
        if (process is null)
        {
            return DaemonStartResult.Failed("The operating system did not start the daemon process.");
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(200, cancellationToken);
            if (await IsHealthyAsync(cancellationToken))
            {
                return DaemonStartResult.Successful("The daemon is running on localhost:9876.");
            }

            if (process.HasExited)
            {
                return DaemonStartResult.Failed($"The daemon exited with code {process.ExitCode}.");
            }
        }

        return DaemonStartResult.Failed("The daemon started but did not become healthy within 10 seconds.");
    }

    private static async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(1) };
        try
        {
            using HttpResponseMessage response = await client.GetAsync(
                "http://127.0.0.1:9876/health",
                cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private static DaemonCommand? FindCommand()
    {
        string baseDirectory = AppContext.BaseDirectory;
        string executableName = OperatingSystem.IsWindows() ? "AgentsBridge.Daemon.exe" : "AgentsBridge.Daemon";
        string[] executableCandidates =
        [
            System.IO.Path.Combine(baseDirectory, "daemon", executableName),
            System.IO.Path.Combine(baseDirectory, executableName)
        ];

        string? executable = executableCandidates.FirstOrDefault(File.Exists);
        if (executable is not null)
        {
            return new DaemonCommand(executable, [], System.IO.Path.GetDirectoryName(executable)!);
        }

        string[] dllCandidates =
        [
            System.IO.Path.Combine(baseDirectory, "daemon", "AgentsBridge.Daemon.dll"),
            System.IO.Path.Combine(baseDirectory, "AgentsBridge.Daemon.dll")
        ];

        string? packagedDll = dllCandidates.FirstOrDefault(File.Exists);
        if (packagedDll is not null)
        {
            return new DaemonCommand("dotnet", [packagedDll], System.IO.Path.GetDirectoryName(packagedDll)!);
        }

        DirectoryInfo? directory = new(baseDirectory);
        while (directory is not null)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "AgentsBridge.slnx")))
            {
                string preferredConfiguration = baseDirectory.Contains(
                    $"{System.IO.Path.DirectorySeparatorChar}Release{System.IO.Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase)
                    ? "Release"
                    : "Debug";

                foreach (string configuration in new[] { preferredConfiguration, preferredConfiguration == "Release" ? "Debug" : "Release" })
                {
                    string developmentDll = System.IO.Path.Combine(
                        directory.FullName,
                        "src",
                        "AgentsBridge.Daemon",
                        "bin",
                        configuration,
                        "net10.0",
                        "AgentsBridge.Daemon.dll");
                    if (File.Exists(developmentDll))
                    {
                        return new DaemonCommand("dotnet", [developmentDll], directory.FullName);
                    }
                }
            }

            directory = directory.Parent;
        }

        return null;
    }

    private sealed record DaemonCommand(string Executable, string[] Arguments, string WorkingDirectory);
}

internal sealed record DaemonStartResult(bool Success, string Message)
{
    internal static DaemonStartResult Successful(string message) => new(true, message);
    internal static DaemonStartResult Failed(string message) => new(false, message);
}
