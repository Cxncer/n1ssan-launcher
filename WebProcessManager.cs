using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace n1ssan_launcher
{
    internal sealed class WebProcessManager
    {
        private const string WebUrl =
            "http://127.0.0.1:5173/obsidian.html";

        private static readonly HttpClient Client = new()
        {
            Timeout = TimeSpan.FromSeconds(2)
        };

        private Process? _process;

        public async Task EnsureRunningAsync(string projectRoot)
        {
            string viteEntry = Path.Combine(
                projectRoot,
                "node_modules",
                "vite",
                "bin",
                "vite.js");

            if (!File.Exists(viteEntry))
            {
                throw new FileNotFoundException(
                    "Vite is not installed in n1ssan-web-bridge. " +
                    "Run npm install in the project directory.",
                    viteEntry);
            }

            // Reuse an already-running Vite instance instead of creating
            // another server when DOMINATE is restarted.
            if (await IsHealthyAsync())
                return;

            var startInfo = new ProcessStartInfo
            {
                FileName = "node",
                WorkingDirectory = projectRoot,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add(viteEntry);
            startInfo.ArgumentList.Add("--host");
            startInfo.ArgumentList.Add("127.0.0.1");
            startInfo.ArgumentList.Add("--port");
            startInfo.ArgumentList.Add("5173");
            startInfo.ArgumentList.Add("--strictPort");

            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;

            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "Could not start n1ssan Obsidian Web.");

            _process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    Debug.WriteLine("[Obsidian Web] " + e.Data);
            };

            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    Debug.WriteLine("[Obsidian Web Error] " + e.Data);
            };

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            DateTime deadline =
                DateTime.UtcNow.AddSeconds(20);

            while (DateTime.UtcNow < deadline)
            {
                if (_process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"Obsidian Web exited with code {_process.ExitCode}. " +
                        "Check that dependencies are installed and port 5173 is free.");
                }

                if (await IsHealthyAsync())
                {
                    Debug.WriteLine(
                        "[Obsidian Web] Ready at " + WebUrl);

                    return;
                }

                await Task.Delay(300);
            }

            throw new TimeoutException(
                "n1ssan Obsidian Web did not become ready within 20 seconds.");
        }

        private static async Task<bool> IsHealthyAsync()
        {
            try
            {
                using var response =
                    await Client.GetAsync(WebUrl);

                if (!response.IsSuccessStatusCode)
                    return false;

                string html =
                    await response.Content.ReadAsStringAsync();

                // Vite injects this client script into transformed HTML.
                // This prevents an unrelated service on port 5173 from
                // being mistaken for the n1ssan development server.
                return html.Contains(
                    "/@vite/client",
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}