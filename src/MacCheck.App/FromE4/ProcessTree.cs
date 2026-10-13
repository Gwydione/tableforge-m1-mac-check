using System.Runtime.InteropServices;

namespace E4.App;

/// <summary>Processes started (directly or indirectly) by a process: the browser engine's helpers, for lifecycle checks.</summary>
public static class ProcessTree
{
    public sealed record Proc(int Pid, int ParentPid, string Name);

    public static List<Proc> Descendants(int root)
    {
        var all = OperatingSystem.IsWindows() ? WindowsSnapshot() : OperatingSystem.IsLinux() ? LinuxSnapshot() : [];
        var result = new List<Proc>();
        var frontier = new HashSet<int> { root };
        bool added;
        do
        {
            added = false;
            foreach (var p in all)
                if (frontier.Contains(p.ParentPid) && p.Pid != root && result.All(r => r.Pid != p.Pid))
                {
                    result.Add(p);
                    frontier.Add(p.Pid);
                    added = true;
                }
        } while (added);
        return result;
    }

    private static List<Proc> LinuxSnapshot()
    {
        var list = new List<Proc>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            try
            {
                var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                var close = stat.LastIndexOf(')');
                var name = stat[(stat.IndexOf('(') + 1)..close];
                var fields = stat[(close + 2)..].Split(' ');
                if (fields[0] == "Z") continue; // zombie: already gone, waiting to be reaped
                list.Add(new Proc(pid, int.Parse(fields[1]), name));
            }
            catch (Exception) { }
        }
        return list;
    }

    private static List<Proc> WindowsSnapshot()
    {
        var list = new List<Proc>();
        var snap = CreateToolhelp32Snapshot(2, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
        try
        {
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snap, ref e)) return list;
            do list.Add(new Proc((int)e.th32ProcessID, (int)e.th32ParentProcessID, e.szExeFile));
            while (Process32Next(snap, ref e));
        }
        finally { CloseHandle(snap); }
        return list;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize; public uint cntUsage; public uint th32ProcessID; public IntPtr th32DefaultHeapID; public uint th32ModuleID;
        public uint cntThreads; public uint th32ParentProcessID; public int pcPriClassBase; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}
