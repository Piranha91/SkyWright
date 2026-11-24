using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WorkbenchTests;

/// <summary>Every process this test process or any of its descendants starts, named by its image as it starts: the test
/// process puts itself in a Windows job with a completion port (JOB_OBJECT_MSG_NEW_PROCESS), once, for its life. Other
/// sessions' processes are never seen. A process that exits before it can be named is listed as unnamed.</summary>
internal static class ProcessWatch
{
    static readonly object Gate = new();
    static readonly List<(DateTime At, int Pid, string? Image)> Seen = new();
    static bool _armed;

    /// <summary>The processes started since <paramref name="mark"/> (from <see cref="Mark"/>).</summary>
    internal static IReadOnlyList<(int Pid, string? Image)> Since(long mark)
    {
        lock (Gate) return Seen.Skip((int)mark).Select(s => (s.Pid, s.Image)).ToList();
    }

    /// <summary>Arms the watch (once) and returns a mark for <see cref="Since"/>. Waits a moment first so the messages of
    /// processes started before it are in.</summary>
    internal static long Mark()
    {
        Arm();
        Thread.Sleep(200);
        lock (Gate) return Seen.Count;
    }

    /// <summary>The images started since the mark, after giving the last ones a moment to arrive.</summary>
    internal static IReadOnlyList<(int Pid, string? Image)> After(long mark)
    {
        Thread.Sleep(300);
        return Since(mark);
    }

    /// <summary>True for houseCARL's server program, by its file name.</summary>
    internal static bool IsHousecarlServer(string? image) =>
        image is not null && string.Equals(Path.GetFileName(image), "housecarl-mcp.exe", StringComparison.OrdinalIgnoreCase);

    static void Arm()
    {
        lock (Gate)
        {
            if (_armed) return;
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw new Win32Exception();
            var port = CreateIoCompletionPort(new IntPtr(-1), IntPtr.Zero, UIntPtr.Zero, 1);
            if (port == IntPtr.Zero) throw new Win32Exception();
            var info = new JOBOBJECT_ASSOCIATE_COMPLETION_PORT { CompletionKey = new IntPtr(1), CompletionPort = port };
            int size = Marshal.SizeOf(info);
            var p = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(info, p, false);
            if (!SetInformationJobObject(job, 7, p, (uint)size)) throw new Win32Exception();
            if (!AssignProcessToJobObject(job, Process.GetCurrentProcess().Handle)) throw new Win32Exception("the test process could not join its watching job");
            new Thread(() =>
            {
                while (GetQueuedCompletionStatus(port, out uint msg, out _, out IntPtr ov, uint.MaxValue))
                {
                    if (msg != 6) continue;                                    // JOB_OBJECT_MSG_NEW_PROCESS
                    int pid = ov.ToInt32();
                    string? image = null;
                    var h = OpenProcess(0x1000, false, pid);                   // PROCESS_QUERY_LIMITED_INFORMATION
                    if (h != IntPtr.Zero)
                    {
                        var sb = new StringBuilder(1024); int n = sb.Capacity;
                        if (QueryFullProcessImageName(h, 0, sb, ref n)) image = sb.ToString();
                        CloseHandle(h);
                    }
                    lock (Gate) Seen.Add((DateTime.UtcNow, pid, image));
                }
            }) { IsBackground = true, Name = "process watch" }.Start();
            _armed = true;
        }
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr a, string? n);
    [DllImport("kernel32", SetLastError = true)] static extern IntPtr CreateIoCompletionPort(IntPtr f, IntPtr p, UIntPtr k, uint t);
    [DllImport("kernel32", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr j, int c, IntPtr i, uint l);
    [DllImport("kernel32", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr j, IntPtr p);
    [DllImport("kernel32", SetLastError = true)] static extern bool GetQueuedCompletionStatus(IntPtr p, out uint n, out UIntPtr k, out IntPtr o, uint ms);
    [DllImport("kernel32", SetLastError = true)] static extern IntPtr OpenProcess(uint a, bool i, int pid);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int f, StringBuilder s, ref int n);
    [DllImport("kernel32")] static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_ASSOCIATE_COMPLETION_PORT { public IntPtr CompletionKey; public IntPtr CompletionPort; }
}

/// <summary>The watch's own tests and every test that reads it run one at a time, after the parallel ones, so the
/// processes it lists are theirs.</summary>
[Xunit.CollectionDefinition("ProcessWatch", DisableParallelization = true)]
public sealed class ProcessWatchCollection { }
