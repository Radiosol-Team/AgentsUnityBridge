using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace AgentsBridge.Local;

/// <summary>
/// Best-effort owner lookup for a loopback TCP request. This deliberately fails closed:
/// unsupported platforms and short-lived connections simply return no process name.
/// </summary>
public static class LoopbackCallerResolver
{
    private const int AddressFamilyInterNetwork = 2;
    private const int TcpTableOwnerPidAll = 5;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint ToolhelpSnapshotProcess = 0x00000002;

    public static string? TryResolve(int localPort, int remotePort)
    {
        if (!OperatingSystem.IsWindows() || localPort <= 0 || remotePort <= 0)
        {
            return null;
        }

        try
        {
            int length = 0;
            uint result = GetExtendedTcpTable(IntPtr.Zero, ref length, true, AddressFamilyInterNetwork, TcpTableOwnerPidAll, 0);
            if (result != ErrorInsufficientBuffer || length <= 0)
            {
                return null;
            }

            IntPtr table = Marshal.AllocHGlobal(length);
            try
            {
                if (GetExtendedTcpTable(table, ref length, true, AddressFamilyInterNetwork, TcpTableOwnerPidAll, 0) != 0)
                {
                    return null;
                }

                int count = Marshal.ReadInt32(table);
                IntPtr rowPointer = IntPtr.Add(table, sizeof(int));
                int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
                for (int index = 0; index < count; index++)
                {
                    MibTcpRowOwnerPid row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPointer);
                    // The row owned by the daemon has localPort == 9876. To find the caller,
                    // locate the other half of the loopback connection: its ephemeral local port
                    // is this request's remote port and its remote port is the daemon listener.
                    if (NetworkPort(row.LocalPort) == remotePort && NetworkPort(row.RemotePort) == localPort)
                    {
                        return DescribeProcess(unchecked((int)row.OwningPid));
                    }

                    rowPointer = IntPtr.Add(rowPointer, rowSize);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(table);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // A connection can disappear while the table is read, or the process may be protected.
        }

        return null;
    }

    private static string? DescribeProcess(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            string name = process.ProcessName;
            if (name.Contains("codex", StringComparison.OrdinalIgnoreCase))
            {
                return "Codex";
            }

            return AncestorIsCodex(processId) ? name + " (Codex)" : name;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool AncestorIsCodex(int processId)
    {
        Dictionary<int, int> parents = ReadParentProcessIds();
        for (int depth = 0; depth < 4 && parents.TryGetValue(processId, out int parentId); depth++)
        {
            processId = parentId;
            try
            {
                using Process parent = Process.GetProcessById(processId);
                if (parent.ProcessName.Contains("codex", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        return false;
    }

    private static Dictionary<int, int> ReadParentProcessIds()
    {
        Dictionary<int, int> result = [];
        IntPtr snapshot = CreateToolhelp32Snapshot(ToolhelpSnapshotProcess, 0);
        if (snapshot == new IntPtr(-1))
        {
            return result;
        }

        try
        {
            ProcessEntry32 entry = new() { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry))
            {
                return result;
            }

            do
            {
                result[unchecked((int)entry.ProcessId)] = unchecked((int)entry.ParentProcessId);
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return result;
    }

    private static int NetworkPort(uint port) =>
        (ushort)IPAddress.NetworkToHostOrder(unchecked((short)port));

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr table,
        ref int tableLength,
        bool sort,
        int addressFamily,
        int tableClass,
        uint reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }
}
