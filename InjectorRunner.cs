using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace n1ssan_launcher
{
    internal sealed record InjectorResult(
        bool Launched,
        string ExecutablePath,
        int ExitCode,
        bool TimedOut,
        string ResultLine,
        string Output,
        string Error)
    {
        internal bool Ok => Launched && !TimedOut && ExitCode == 0;
    }

    /// <summary>
    /// Runs the standalone n1ssan-injector as a child process. The launcher never performs
    /// injection itself: it only starts the injector and interprets its exit code / RESULT line.
    /// </summary>
    internal static class InjectorRunner
    {
        internal const string InjectorExeName = "n1ssan-injector.exe";
        internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(330);

        /// <summary>
        /// Search order: N1SSAN_INJECTOR_PATH, next to the launcher, then the sibling repository
        /// layout walked up from the launcher directory.
        /// </summary>
        internal static string? ResolveInjectorPath()
        {
            string? configured = Environment.GetEnvironmentVariable("N1SSAN_INJECTOR_PATH");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                return Path.GetFullPath(configured);

            // Prefer the maintained v2 project build over any copied executable
            // beside the launcher. A stale adjacent injector can otherwise take
            // precedence and reopen a console even after v2 has been rebuilt.
            DirectoryInfo? directory;
            try
            {
                directory = new DirectoryInfo(AppContext.BaseDirectory);
            }
            catch (Exception)
            {
                return null;
            }

            for (int depth = 0; directory != null && depth < 8; depth++, directory = directory.Parent)
            {
                foreach (string relative in CandidateLayouts())
                {
                    string candidate = Path.Combine(directory.FullName, relative);
                    if (File.Exists(candidate))
                        return Path.GetFullPath(candidate);
                }
            }

            // A packaged v2 executable may be deployed beside the launcher.
            // Use it only after checking the maintained sibling-project build.
            string besideLauncher = Path.Combine(AppContext.BaseDirectory, InjectorExeName);
            if (File.Exists(besideLauncher))
                return Path.GetFullPath(besideLauncher);

            return null;
        }

        private static IEnumerable<string> CandidateLayouts()
        {
            // Only use the maintained v2 repository. Falling back to the legacy
            // n1ssan-injector folder can silently launch an executable that does not
            // understand the current DOMINATE arguments or status contract.
            yield return Path.Combine("n1ssan-injectorv2", "bin", "Release", "net8.0-windows", InjectorExeName);
        }

        internal static async Task<InjectorResult> RunAsync(int pid, CancellationToken ct)
        {
            string? executable = ResolveInjectorPath();

            if (executable == null)
            {
                return new InjectorResult(
                    false, string.Empty, -1, false, string.Empty, string.Empty,
                    $"Could not locate {InjectorExeName}. Build n1ssan-injectorv2 for x64 Release or set N1SSAN_INJECTOR_PATH.");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            startInfo.ArgumentList.Add("--pid");
            startInfo.ArgumentList.Add(pid.ToString());
            startInfo.ArgumentList.Add("--no-wait");
            // DOMINATE owns the status handshake so it can show the DLL's INITIALIZING/READY states;
            // the injector only has to load the module and confirm it is listed.
            startInfo.ArgumentList.Add("--no-status");
            startInfo.ArgumentList.Add("--quiet");
            startInfo.ArgumentList.Add("--settle");
            startInfo.ArgumentList.Add("45");
            startInfo.ArgumentList.Add("--inject-timeout");
            startInfo.ArgumentList.Add("60");

            using var process = new Process { StartInfo = startInfo };

            try
            {
                if (!process.Start())
                {
                    return new InjectorResult(
                        false, executable, -1, false, string.Empty, string.Empty,
                        "The injector process could not be started.");
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                return new InjectorResult(
                    false, executable, -1, false, string.Empty, string.Empty,
                    $"The injector could not start: {ex.Message}");
            }

            bool timedOut = false;

            // Start draining the pipes immediately: a full pipe would block the injector.
            Task<string> outputTask = ReadAllAsync(process.StandardOutput);
            Task<string> errorTask = ReadAllAsync(process.StandardError);

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(Timeout);

                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    Kill(process);

                    if (ct.IsCancellationRequested)
                        throw;

                    timedOut = true;

                    // Give the killed process a moment to flush and exit.
                    try
                    {
                        await process.WaitForExitAsync(CancellationToken.None)
                            .WaitAsync(TimeSpan.FromSeconds(5))
                            .ConfigureAwait(true);
                    }
                    catch (Exception)
                    {
                        // Ignore: we already have enough to report.
                    }
                }
            }

            string output = await outputTask.ConfigureAwait(true);
            string error = await errorTask.ConfigureAwait(true);

            int exitCode;
            try
            {
                exitCode = process.HasExited ? process.ExitCode : -1;
            }
            catch (InvalidOperationException)
            {
                exitCode = -1;
            }

            string resultLine = output
                .Split('\n')
                .Select(line => line.Trim())
                .LastOrDefault(line => line.StartsWith("RESULT=", StringComparison.Ordinal))
                ?? string.Empty;

            return new InjectorResult(true, executable, exitCode, timedOut, resultLine, output, error);
        }

        private static async Task<string> ReadAllAsync(StreamReader reader)
        {
            try
            {
                return await reader.ReadToEndAsync().ConfigureAwait(true);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static void Kill(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Already gone or not killable; the caller reports the timeout either way.
            }
        }

        /// <summary>Actionable message for the DOMINATE readiness line.</summary>
        internal static string Describe(InjectorResult result)
        {
            if (!result.Launched)
                return result.Error;

            if (result.TimedOut)
                return $"The injector did not finish within {Timeout.TotalSeconds:0} seconds and was stopped.";

            string detail = LastErrorLine(result);

            return result.ExitCode switch
            {
                0 => "The injector loaded the DLL.",
                2 => "The injector rejected its arguments.",
                3 => "DLL validation failed. " + detail,
                4 => "CS2 closed or did not finish loading. " + detail,
                5 => "Injection failed. " + detail,
                6 => "CS2 unloaded the DLL immediately. Start CS2 with -allow_third_party_software.",
                _ => $"The injector exited with code {result.ExitCode}. {detail}",
            };
        }

        internal static string LastErrorLine(InjectorResult result)
        {
            string? line = result.Error
                .Split('\n')
                .Select(value => value.Trim())
                .LastOrDefault(value => value.Length > 0);

            return line ?? result.ResultLine;
        }
    }
}
