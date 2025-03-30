using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Runtime;

namespace ClrStack
{
    internal static class Program
    {
        private const string ThreadDumpDirEnvVar = "THREAD_DUMP_DIR";
        private const string NoSuspend = "--no-suspend";
        private const string Timeout = "--timeout";
        private const string DumpProcessTo = "--dump-process-to";
        private const string CreateSnapshotAndAttach = "--create-snapshot-and-attach";

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
                StartInfo = new ProcessStartInfo(fullPath, string.Join(" ", args))
                {
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            process.OutputDataReceived += (sender, eventArgs) => Console.WriteLine(eventArgs.Data);
            process.ErrorDataReceived += (sender, eventArgs) => Console.Error.WriteLine(eventArgs.Data);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();
            process.CancelOutputRead();
            process.CancelErrorRead();
            return process.ExitCode;
        }

        private static int PrintUsageError()
        {
            Console.Error.WriteLine($"Usage: ClrStack.exe PID  [{NoSuspend}] {{ [{Timeout}=5000 (in milliseconds)] [{CreateSnapshotAndAttach} (windows only)] }} | {{ {DumpProcessTo}=\"full path\" }}");
            return 1;
        }

        public static int Main(string[] args)
        {
            if (args.Length < 1 ||
                !int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                return PrintUsageError();
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Environment.Is64BitOperatingSystem && Environment.Is64BitProcess)
            {
                var targetProcess = Process.GetProcessById(pid);
                if (IsWow64Process(targetProcess.Handle, out var targetProcessIsWow64) && targetProcessIsWow64)
                    return RerunMainAs32BitProcess(args);
            }
            
            args = args.Skip(1).ToArray(); // skip pid

            var threadDumpDir = Environment.GetEnvironmentVariable(ThreadDumpDirEnvVar);
            if (!string.IsNullOrEmpty(threadDumpDir) && !Directory.Exists(threadDumpDir))
            {
                Console.Error.WriteLine($"Path [{threadDumpDir}] in THREAD_DUMP_DIR environment variable not exists or not directory");
                return 1;
            }

            var processDumpParameters = TryParseProcessDumpParameters(args, pid);
            if (processDumpParameters != null)
            {
                return ToResult(DumpProcess(processDumpParameters));
            }

            var threadDumpParameters = TryParseThreadDumpParameters(args, pid, threadDumpDir);
            if (threadDumpParameters == null)
            {
                return PrintUsageError();
            }

            return ToResult(DumpStackTraces(threadDumpParameters));
        }

        private static bool TryParseTimeout(string arg, out int timeoutMs)
        {
            if (TryParseValue(arg, Timeout, out var value))
            {
                return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out timeoutMs);
            }

            timeoutMs = -1;
            return false;
        }

        private static bool TryParseValue(string arg, string pattern, out string value)
        {
            const char separator = '=';
            if (arg.Length > pattern.Length + 1 && arg.StartsWith(pattern, StringComparison.InvariantCultureIgnoreCase) && arg[pattern.Length] == separator)
            {
                value = arg.Split(new[] { separator }, 2)[1];
                return true;
            }

            value = null;
            return false;
        }

        private static bool TryParseFlag(string arg, string pattern)
        {
            return arg.Equals(pattern, StringComparison.InvariantCultureIgnoreCase);
        }

        private static ProcessDumpParameters TryParseProcessDumpParameters(string[] args, int pid)
        {
            var parameters = new ProcessDumpParameters(pid);
            foreach (var arg in args)
            {
                if (TryParseTimeout(arg, out var timeoutMs) && parameters.TimeoutMs == null)
                {
                    parameters.TimeoutMs = timeoutMs;
                }
                else if (TryParseValue(arg, DumpProcessTo, out var dumpPath) && parameters.DumpPath == null)
                {
                    parameters.DumpPath = dumpPath;
                }
                else return null;
            }

            return parameters.Success ? parameters : null;
        }

        private static ThreadDumpParameters TryParseThreadDumpParameters(string[] args, int pid, string threadDumpDir)
        {
            var parameters = new ThreadDumpParameters(pid, threadDumpDir);
            foreach (var arg in args)
            {
                if (TryParseFlag(arg, NoSuspend) && parameters.Suspend)
                {
                    parameters.Suspend = false;
                }
                else if (TryParseTimeout(arg, out var timeoutMs) && parameters.TimeoutMs == null)
                {
                    parameters.TimeoutMs = timeoutMs;
                }
                else if (TryParseFlag(arg, CreateSnapshotAndAttach) && !parameters.CreateSnapshotAndAttach && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    parameters.CreateSnapshotAndAttach = true;
                }
                else return null;
            }

            return parameters;
        }

        private static int ToResult(bool success) => success ? 0 : 3;

        private static bool DumpProcess(ProcessDumpParameters parameters)
        {
            try
            {
                var directory = new FileInfo(parameters.DumpPath).Directory;
                if (directory == null) throw new DirectoryNotFoundException($"Parent of {parameters.DumpPath} is null");

                if (!directory.Exists)
                    directory.Create();

                var path = parameters.DumpPath;
                using (WithTimeoutCookie(parameters.TimeoutMs, () => { }))
                {
                    new DiagnosticsClient(parameters.Pid).WriteDump(DumpType.Full, path);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Cannot dump from process[{parameters.Pid}]. Error: {ex.Message}");
                return false;
            }
        }

        private static bool DumpStackTraces(ThreadDumpParameters parameters)
        {
            DataTarget target = null;
            var success = true;
            var output = new StringBuilder();

            using (WithTimeoutCookie(parameters.TimeoutMs, () => target?.Dispose()))
            {
                try
                {
                    using (target = AttachToProcess(parameters))
                    {
                        var clrVersion = target.ClrVersions.FirstOrDefault() ?? throw new Exception("CLR not found in process");

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
                    success = false;
                    output.AppendLine($"Cannot capture stack trace from process[{parameters.Pid}]. Error: {ex.Message}");
                }
            }

            if (string.IsNullOrEmpty(parameters.ThreadDumpDir))
            {
                Console.Write(output.ToString());
            }
            else
            {
                var fileName = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss.fff}.tdump";
                var path = Path.Combine(parameters.ThreadDumpDir, fileName);
                File.WriteAllText(path, output.ToString(), Encoding.UTF8);
            }

            return success;
        }

        private static TimeoutCookie WithTimeoutCookie(int? timeoutMs, Action action = null)
        {
            if (timeoutMs == null) return default;

            var source = new CancellationTokenSource();
            var token = source.Token;
            Task.Factory.StartNew(async () =>
            {
                await Task.Delay(timeoutMs.Value, token);

                action?.Invoke();

                await Console.Error.WriteLineAsync("Cannot capture stack trace from process: Timeout expired.");
                Environment.Exit(2);
            }, token);

            return new TimeoutCookie(source);
        }

        private static DataTarget AttachToProcess(ThreadDumpParameters parameters)
        {
            return parameters.CreateSnapshotAndAttach
                ? DataTarget.CreateSnapshotAndAttach(parameters.Pid)
                : DataTarget.AttachToProcess(parameters.Pid, parameters.Suspend);
        }
    }

    public class ProcessDumpParameters
    {
        public int Pid { get;  }
        public string DumpPath { get; set; }
        public int? TimeoutMs { get; set; } = null;

        public bool Success => DumpPath != null;

        public ProcessDumpParameters(int pid)
        {
            Pid = pid;
        }
    }

    public class ThreadDumpParameters
    {
        public ThreadDumpParameters(int pid, string threadDumpDir)
        {
            Pid = pid;
            ThreadDumpDir = threadDumpDir;
        }

        public int? TimeoutMs { get; set; } = null;
        public int Pid { get;  }
        public string ThreadDumpDir { get; }
        public bool Suspend { get; set; } = true;
        public bool CreateSnapshotAndAttach { get; set; } = false;
    }

    public readonly struct TimeoutCookie : IDisposable
    {
        private readonly CancellationTokenSource _source;

        public TimeoutCookie(CancellationTokenSource source)
        {
            _source = source;
        }

        public void Dispose()
        {
            _source?.Dispose();
        }
    }
}