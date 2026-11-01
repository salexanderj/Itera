using System.Runtime.InteropServices;

namespace Itera.Engine;

/// <summary>
/// How a background thread runs below the game loop's threads on every platform: it is made with
/// <see cref="ThreadPriority.BelowNormal"/>, which Windows and macOS take, and calls <see cref="Lower"/> first thing on
/// itself, which on Linux, where .NET's priorities change nothing, sets its own nice value to <see cref="LinuxNice"/>.
/// While every processor is busy, the scheduler then gives the main thread, the step's workers, and the physics their
/// time first, and the background thread what is left; while one is free, the background thread runs as fast as before.
/// </summary>
public static class ThreadPriorities
{
    /// <summary>
    /// The nice value a background thread takes on Linux: 10, about a tenth of a normal thread's share of a processor
    /// that both want.
    /// </summary>
    public const int LinuxNice = 10;

    // Linux's setpriority on PRIO_PROCESS with 0 sets the calling thread's nice value alone.
    private const int PrioProcess = 0;

    /// <summary>
    /// On Linux, sets the calling thread's nice value to <see cref="LinuxNice"/>; true when it did. False on another
    /// platform, whose thread priorities do the same, and where the call fails (the thread then runs as it was). On the
    /// thread itself, first.
    /// </summary>
    public static bool Lower()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            return SetPriority(PrioProcess, 0, LinuxNice) == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("libc.so.6", EntryPoint = "setpriority")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int SetPriority(int which, int who, int priority);
}
