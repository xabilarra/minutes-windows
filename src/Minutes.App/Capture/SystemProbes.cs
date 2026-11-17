using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Minutes.Core;

namespace Minutes.App.Capture;

/// <summary>
/// Which apps are using the microphone right now, from the registry Windows keeps for the privacy
/// indicator: an entry whose LastUsedTimeStop is 0 is in use. Desktop apps are keyed by their exe path
/// ("C:#Program Files#Zoom#bin#Zoom.exe" → "zoom.exe"), Store apps by their package family.
/// </summary>
internal static class MicUsageMonitor
{
    private const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    public static HashSet<string> ActiveMicKeys()
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(Store);
            if (root is null)
            {
                return keys;
            }
            foreach (var name in root.GetSubKeyNames())
            {
                if (name.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
                {
                    using var nonPackaged = root.OpenSubKey(name);
                    foreach (var path in nonPackaged?.GetSubKeyNames() ?? [])
                    {
                        using var entry = nonPackaged!.OpenSubKey(path);
                        if (IsInUse(entry))
                        {
                            keys.Add(path[(path.LastIndexOf('#') + 1)..].ToLowerInvariant());
                        }
                    }
                }
                else
                {
                    using var entry = root.OpenSubKey(name);
                    if (IsInUse(entry))
                    {
                        keys.Add(name.ToLowerInvariant());
                    }
                }
            }
        }
        catch (Exception error)
        {
            MinutesLog.Error($"Reading microphone users failed: {error.GetType().Name}");
        }
        return keys;
    }

    private static bool IsInUse(RegistryKey? entry) =>
        entry?.GetValue("LastUsedTimeStop") is long stop && stop == 0
        && entry.GetValue("LastUsedTimeStart") is long start && start > 0;
}

/// <summary>Visible top-level windows and the foreground app.</summary>
internal static class WindowCatalog
{
    public static List<WindowInfo> VisibleWindows()
    {
        var windows = new List<WindowInfo>();
        var names = new Dictionary<uint, string>();
        Native.EnumWindows((handle, _) =>
        {
            try
            {
                if (!Native.IsWindowVisible(handle) || IsCloaked(handle))
                {
                    return true;
                }
                var length = Native.GetWindowTextLength(handle);
                if (length == 0)
                {
                    return true;
                }
                var text = new StringBuilder(length + 1);
                Native.GetWindowText(handle, text, text.Capacity);
                if (!Native.GetWindowRect(handle, out var rect))
                {
                    return true;
                }
                Native.GetWindowThreadProcessId(handle, out var pid);
                if (!names.TryGetValue(pid, out var process))
                {
                    process = ProcessName(pid);
                    names[pid] = process;
                }
                windows.Add(new WindowInfo(handle, process, text.ToString(), rect.Right - rect.Left, rect.Bottom - rect.Top));
            }
            catch (Exception)
            {
            }
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    /// <summary>Exe name and window title of the foreground window, or null.</summary>
    public static (string Process, string Title, uint Pid)? Foreground()
    {
        var handle = Native.GetForegroundWindow();
        if (handle == IntPtr.Zero)
        {
            return null;
        }
        Native.GetWindowThreadProcessId(handle, out var pid);
        var text = new StringBuilder(512);
        Native.GetWindowText(handle, text, text.Capacity);
        return (ProcessName(pid), text.ToString(), pid);
    }

    /// <summary>Product name of the process's executable ("Zoom Workplace"), falling back to its file name.</summary>
    public static string ProductName(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var file = process.MainModule?.FileName;
            if (file is not null)
            {
                var info = FileVersionInfo.GetVersionInfo(file);
                if (!string.IsNullOrWhiteSpace(info.FileDescription))
                {
                    return info.FileDescription.Trim();
                }
                if (!string.IsNullOrWhiteSpace(info.ProductName))
                {
                    return info.ProductName.Trim();
                }
            }
            return process.ProcessName;
        }
        catch (Exception)
        {
            return "Manual";
        }
    }

    private static string ProcessName(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName.ToLowerInvariant() + ".exe";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static bool IsCloaked(IntPtr handle) =>
        Native.DwmGetWindowAttribute(handle, Native.DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static class Native
    {
        public const int DwmwaCloaked = 14;

        public delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr handle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLength(IntPtr handle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr handle, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr handle, out Rect rect);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
    }
}

/// <summary>Process ids of an app and every process it started (Teams' WebView2 children, Chrome's audio service).</summary>
internal static class ProcessTree
{
    public static HashSet<uint> Of(IReadOnlyCollection<string> executableNames)
    {
        var result = new HashSet<uint>();
        if (executableNames.Count == 0)
        {
            return result;
        }
        var processes = Snapshot();
        foreach (var (pid, (_, name)) in processes)
        {
            if (executableNames.Contains(name))
            {
                result.Add(pid);
            }
        }
        // Children may appear in any order: repeat until nothing new joins.
        bool added;
        do
        {
            added = false;
            foreach (var (pid, (parent, _)) in processes)
            {
                if (!result.Contains(pid) && result.Contains(parent) && pid != parent)
                {
                    result.Add(pid);
                    added = true;
                }
            }
        } while (added);
        return result;
    }

    private static Dictionary<uint, (uint Parent, string Name)> Snapshot()
    {
        var processes = new Dictionary<uint, (uint, string)>();
        var snapshot = Native.CreateToolhelp32Snapshot(Native.SnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return processes;
        }
        try
        {
            var entry = new Native.ProcessEntry { Size = (uint)Marshal.SizeOf<Native.ProcessEntry>() };
            for (var ok = Native.Process32FirstW(snapshot, ref entry); ok; ok = Native.Process32NextW(snapshot, ref entry))
            {
                processes[entry.ProcessId] = (entry.ParentProcessId, entry.ExeFile.ToLowerInvariant());
            }
        }
        finally
        {
            Native.CloseHandle(snapshot);
        }
        return processes;
    }

    private static class Native
    {
        public const uint SnapProcess = 0x00000002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct ProcessEntry
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
            public string ExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
