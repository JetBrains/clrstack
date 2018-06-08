using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Diagnostics.Runtime;

namespace ClrStack
{
    internal static class Program
    {
        private const string ThreadDumpDirEnvVar = "THREAD_DUMP_DIR";

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
                Console.Error.WriteLine($"Path [{threadDumpDir}] in THREAD_DUMP_DIR environment vaiable not exists or not directory");
                return;
            }

            var output = new StringBuilder();
            try
            {
                using (var target = DataTarget.AttachToProcess(pid, 5000, AttachFlag.NonInvasive))
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

                        foreach (var frame in clrThread.StackTrace)
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
                var fileName = $"{DateTime.Now:yyyy-MM-dd_HH_mm_ss.fff}.tdump";
                File.WriteAllText(Path.Combine(threadDumpDir, fileName), output.ToString(), Encoding.UTF8);
            }
        }
    }
}