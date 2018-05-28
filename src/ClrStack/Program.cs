using System;
using System.Globalization;
using System.Linq;
using Microsoft.Diagnostics.Runtime;

namespace ClrStack
{
  internal static class Program
  {
    public static void Main(string[] args)
    {
      if (args.Length != 1 || !int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture,out var pid))
      {
        Console.Error.WriteLine("Usage: ClrStack.exe [PID]");
        return;
      }
      using (var target = DataTarget.AttachToProcess(pid, 1000, AttachFlag.NonInvasive))
      {
        var clrVersion = target.ClrVersions.FirstOrDefault();
        if (clrVersion == null)
        {
          Console.WriteLine($"CLR not found in process: {pid}");
          return;
        }
        var runtime = clrVersion.CreateRuntime();

        foreach (var clrThread in runtime.Threads)
        {
          if (!clrThread.IsAlive)
            continue;
          Console.WriteLine($"Thread #{clrThread.ManagedThreadId}:");

          foreach (var frame in clrThread.StackTrace)
            Console.WriteLine($"\tat {frame}");
        }
      }
    }
  }
}