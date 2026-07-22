using System.ComponentModel;
using System.Runtime.InteropServices;
using McPanel.Api.Services;

namespace McPanel.Api.Infrastructure;

public static class CgroupProcessLauncher
{
    public static bool TryExec(string[] arguments, out int exitCode)
    {
        exitCode = 0;
        if (arguments.Length == 0 || arguments[0] != CgroupMemoryService.LauncherArgument) return false;
        if (!OperatingSystem.IsLinux() || arguments.Length < 3)
        {
            Console.Error.WriteLine("Invalid MC Panel cgroup launcher invocation.");
            exitCode = 126;
            return true;
        }

        try
        {
            File.WriteAllText(arguments[1], Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Exec(arguments[2], arguments[2..]);
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"MC Panel could not enter the server cgroup: {exception.Message}");
            exitCode = 126;
            return true;
        }
    }

    private static void Exec(string executable, IReadOnlyList<string> arguments)
    {
        var pointers = new IntPtr[arguments.Count + 1];
        var argv = IntPtr.Zero;
        try
        {
            for (var index = 0; index < arguments.Count; index++) pointers[index] = Marshal.StringToCoTaskMemUTF8(arguments[index]);
            argv = Marshal.AllocHGlobal(IntPtr.Size * pointers.Length);
            Marshal.Copy(pointers, 0, argv, pointers.Length);
            _ = execv(executable, argv);
        }
        finally
        {
            if (argv != IntPtr.Zero) Marshal.FreeHGlobal(argv);
            foreach (var pointer in pointers) if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int execv([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr argv);
}
