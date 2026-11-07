using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace n1ssan_launcher
{
    /// <summary>One running cs2.exe, as seen by the launcher.</summary>
    internal sealed record Cs2Instance(int Pid, DateTime StartedAt, bool ClientLoaded);

    /// <summary>
    /// Single place that picks the cs2.exe instance the launcher will inject into and monitor.
    /// The injector applies the same preference (client.dll loaded, otherwise newest), so the
    /// launcher and injector cannot end up watching different processes.
    /// </summary>
    internal static class Cs2Process
    {
        internal const string ProcessName = "cs2";
        internal const string ClientModuleName = "client.dll";

        internal static Cs2Instance? SelectPreferred()
        {
            Process[] processes;

            try
            {
                processes = Process.GetProcessesByName(ProcessName);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return null;
            }

            try
            {
                var instances = new List<Cs2Instance>(processes.Length);

                foreach (Process process in processes)
                {
                    try
                    {
                        instances.Add(new Cs2Instance(
                            process.Id,
                            ReadStartTime(process),
                            HasClientModule(process)));
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // The process exited while we were inspecting it.
                    }
                }

                if (instances.Count == 0)
                    return null;

                Cs2Instance? withClient = instances.FirstOrDefault(instance => instance.ClientLoaded);

                if (withClient != null)
                    return withClient;

                return instances.OrderByDescending(instance => instance.StartedAt).First();
            }
            finally
            {
                foreach (Process process in processes)
                    process.Dispose();
            }
        }

        internal static Cs2Instance? TryGet(int pid)
        {
            if (pid <= 0)
                return null;

            try
            {
                using Process process = Process.GetProcessById(pid);

                if (process.HasExited)
                    return null;

                return new Cs2Instance(pid, ReadStartTime(process), HasClientModule(process));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }

        internal static bool IsAlive(int pid) => TryGet(pid) != null;

        internal static bool HasClientModule(int pid)
        {
            Cs2Instance? instance = TryGet(pid);
            return instance?.ClientLoaded == true;
        }

        private static DateTime ReadStartTime(Process process)
        {
            try
            {
                return process.StartTime;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return DateTime.MinValue;
            }
        }

        private static bool HasClientModule(Process process)
        {
            try
            {
                foreach (ProcessModule module in process.Modules)
                {
                    using (module)
                    {
                        if (string.Equals(module.ModuleName, ClientModuleName, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Access to another process's module list can be denied; treat as "not loaded yet".
            }

            return false;
        }
    }
}
