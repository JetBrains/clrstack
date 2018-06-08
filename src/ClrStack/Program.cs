using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ClrStack
{
    public static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsWow64Process(IntPtr hProcess, out bool isWow64Process);

        public static void Main(string[] args)
        {
            if (args.Length != 1 ||
                !int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                Console.Error.WriteLine("Usage: ClrStack.exe [PID]");
                return;
            }
            var targetProcess = Process.GetProcessById(pid);
            var archSuffix = Is64BitProcess(targetProcess) ? "x64" : "x86";
            var platformSpecificExecutable = $"ClrStack.{archSuffix}.exe";
            var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var fullPath = assemblyDirectory != null
                ? Path.Combine(assemblyDirectory, platformSpecificExecutable)
                : platformSpecificExecutable;
            var process = new Process
            {
                StartInfo = new ProcessStartInfo(fullPath, pid.ToString())
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
        }

        private static bool Is64BitProcess(Process process)
        {
            if (!Environment.Is64BitOperatingSystem)
                return false;
            IsWow64Process(process.Handle, out var isWow64Process);
            return !isWow64Process;
        }
    }
}