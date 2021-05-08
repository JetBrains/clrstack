using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Diagnostics.Runtime;

namespace ClrStack
{
    internal static class Program
    {
        private const string ThreadDumpDirEnvVar = "THREAD_DUMP_DIR";

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsWow64Process(IntPtr hProcess, out bool isWow64Process);

        private static int RerunMainAs32BitProcess(string[] args)
        {
            var platformSpecificExecutable = "ClrStack32.exe";
            var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var fullPath = assemblyDirectory != null
                ? Path.Combine(assemblyDirectory, platformSpecificExecutable)
                : platformSpecificExecutable;
            var process = new Process
            {
                StartInfo = new ProcessStartInfo(fullPath, args[0])
                {
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            process.OutputDataReceived += (sender, eventArgs) => Console.WriteLine(eventArgs.Data);
            process.ErrorDataReceived += (sender, eventArgs) => Console.WriteLine(eventArgs.Data);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();
            process.CancelOutputRead();
            process.CancelErrorRead();
            return process.ExitCode;
        }

        public static void Main(string[] args)
        {
            if (args.Length != 1 ||
                !int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                Console.Error.WriteLine("Usage: ClrStack.exe [PID]");
                return;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Environment.Is64BitOperatingSystem && Environment.Is64BitProcess)
            {
                var targetProcess = Process.GetProcessById(pid);
                if (IsWow64Process(targetProcess.Handle, out var targetProcessIsWow64) && targetProcessIsWow64)
                {
                    RerunMainAs32BitProcess(args);
                    return;
                }
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
                using (var target = DataTarget.AttachToProcess(pid, true))
                {
                    var clrVersion = target.ClrVersions.FirstOrDefault();
                    if (clrVersion == null)
                    {
                        output.AppendLine($"CLR not found in process: {pid}");
                        return;
                    }

                    using (var runtime = clrVersion.CreateRuntime())
                    {
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