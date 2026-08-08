using System.Diagnostics;
using System.Text.Json;

namespace AgentsBridge.Local;

public sealed class UnityEditorLauncher
{
    public const string ForceConnectExecuteMethod =
        "Radiosol.CodexBridge.Editor.EditorHttpBridge.ConnectToAgentsBridgeFromCommandLine";

    public LaunchResult Launch(UnityProjectInfo project, bool forceBridgeConnect = false)
    {
        if (!project.Exists)
        {
            return LaunchResult.Failed("The project directory no longer exists.");
        }

        if (string.IsNullOrWhiteSpace(project.UnityVersion))
        {
            return LaunchResult.Failed("The project's Unity version could not be determined.");
        }

        string? editorPath = FindEditor(project.UnityVersion);
        if (editorPath is null)
        {
            return LaunchResult.Failed($"Unity {project.UnityVersion} is not installed in a Unity Hub editor location.");
        }

        ProcessStartInfo startInfo = new(editorPath)
        {
            UseShellExecute = true,
            WorkingDirectory = project.Path
        };
        startInfo.ArgumentList.Add("-projectPath");
        startInfo.ArgumentList.Add(project.Path);
        if (forceBridgeConnect)
        {
            startInfo.ArgumentList.Add("-executeMethod");
            startInfo.ArgumentList.Add(ForceConnectExecuteMethod);
        }

        Process.Start(startInfo);
        return LaunchResult.Started(forceBridgeConnect
            ? $"Opening {project.Name} and asking Unity to connect the bridge."
            : $"Opening {project.Name} in Unity {project.UnityVersion}.");
    }

    public static string? FindEditor(string unityVersion)
    {
        foreach (string root in EditorRoots())
        {
            string executable = OperatingSystem.IsWindows()
                ? System.IO.Path.Combine(root, unityVersion, "Editor", "Unity.exe")
                : OperatingSystem.IsMacOS()
                    ? System.IO.Path.Combine(root, unityVersion, "Unity.app", "Contents", "MacOS", "Unity")
                    : System.IO.Path.Combine(root, unityVersion, "Editor", "Unity");

            if (File.Exists(executable))
            {
                return executable;
            }
        }

        return null;
    }

    private static IEnumerable<string> EditorRoots()
    {
        string? secondary = ReadSecondaryInstallPath();
        if (!string.IsNullOrWhiteSpace(secondary))
        {
            yield return secondary;
        }

        if (OperatingSystem.IsWindows())
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            yield return System.IO.Path.Combine(programFiles, "Unity", "Hub", "Editor");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/Applications/Unity/Hub/Editor";
        }
        else
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return System.IO.Path.Combine(userProfile, "Unity", "Hub", "Editor");
        }
    }

    private static string? ReadSecondaryInstallPath()
    {
        string applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string path = System.IO.Path.Combine(applicationData, "UnityHub", "secondaryInstallPath.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<string>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record LaunchResult(bool Success, string Message)
{
    public static LaunchResult Started(string message) => new(true, message);
    public static LaunchResult Failed(string message) => new(false, message);
}
