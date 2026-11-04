using System;
using System.IO;

namespace n1ssan_launcher
{
    internal static class StartupDiagnostics
    {
        private static readonly object Sync = new();

        public static string LogPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "n1ssan", "logs", "dominate-startup.log");

        public static void Write(string message)
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
            System.Diagnostics.Debug.WriteLine(line);
            try
            {
                lock (Sync)
                {
                    string path = LogPath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[DOMINATE Diagnostics] Log write failed: " + ex.Message);
            }
        }
    }
}