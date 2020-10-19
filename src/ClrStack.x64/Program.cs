using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Diagnostics.Runtime;

namespace ClrStack
{
    internal static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hReservedNull, int dwFlags);
        private const int LoadLibrarySearchDllLoadDir = 0x00000100;

        private const string ThreadDumpDirEnvVar = "THREAD_DUMP_DIR";

        private static void EnsureDbgEngineIsLoaded()
        {
            var systemFolder = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var res = LoadLibraryEx(Path.Combine(systemFolder, "dbgeng.dll"), IntPtr.Zero, LoadLibrarySearchDllLoadDir);
            if (res == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        public static void Main(string[] args)
        {
            if (args.Length != 1 ||
                !int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                Console.Error.WriteLine("Usage: ClrStack.exe [PID]");
                return;
            }
            var threadDumpDir = Environment.GetEnvironmentVariable(ThreadDumpDirEnvVar);
            if (!string.IsNullOrEmpty(threadDumpDir) && !Directory.Exists(threadDumpDir))
            {
                Console.Error.WriteLine($"Path [{threadDumpDir}] in THREAD_DUMP_DIR environment variable not exists or not directory");
                return;
            }

            var output = new StringBuilder();
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    EnsureDbgEngineIsLoaded();
                using (var target = DataTarget.AttachToProcess(pid, false))
                {
                    var clrVersion = target.ClrVersions.FirstOrDefault();
                    if (clrVersion == null)
                    {
                        output.AppendLine($"CLR not found in process: {pid}");
                        return;
                    }

                    var runtime = clrVersion.CreateRuntime();

                    foreach (var clrThread in runtime.Threads)
                    {
                        if (!clrThread.IsAlive)
                            continue;
                        output.AppendLine($"Thread #{clrThread.ManagedThreadId}:");

                        foreach (var frame in clrThread.EnumerateStackTrace())
                            output.AppendLine($"\tat {frame}");
                    }
                }
            }
            catch (Exception ex)
            {
                output.AppendLine($"Cannot capture stack trace from process[{pid}]. Error: {ex.Message}");
            }

            if (string.IsNullOrEmpty(threadDumpDir))
            {
                Console.Write(output.ToString());
            }
            else
            {
                var fileName = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss.fff}.tdump";
                File.WriteAllText(Path.Combine(threadDumpDir, fileName), output.ToString(), Encoding.UTF8);
            }
        }
    }
}