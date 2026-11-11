using System;
using System.IO;
using System.Text.Json;

namespace n1ssan_launcher
{
    internal sealed record LoadoutPreflightResult(bool HasEntries, int EntryCount, string Path, string Message);

    /// <summary>
    /// Inspects the configuration file the native DLL reads at injection time (n1ssan-data.json).
    /// This is informational only: the DLL remains the authority on whether a loadout is usable,
    /// and blocking injection here would also lock the destination screen that a user needs to
    /// fix the loadout. The result is surfaced in the readiness text and the startup log.
    /// </summary>
    internal static class LoadoutPreflight
    {
        internal static LoadoutPreflightResult Check(string bridgeRoot)
        {
            string? configured = Environment.GetEnvironmentVariable("N1SSAN_DATA_FILE");

            string path = string.IsNullOrWhiteSpace(configured)
                ? System.IO.Path.Combine(bridgeRoot, "configs", "n1ssan-data.json")
                : System.IO.Path.GetFullPath(configured);

            if (!File.Exists(path))
            {
                return new LoadoutPreflightResult(
                    false, 0, path,
                    "No loadout file found; the DLL will report loadout_empty.");
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
                int count = 0;

                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("skins", out JsonElement skins) &&
                    skins.ValueKind == JsonValueKind.Array)
                {
                    count = skins.GetArrayLength();
                }
                else if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    count = document.RootElement.GetArrayLength();
                }

                return new LoadoutPreflightResult(
                    count > 0,
                    count,
                    path,
                    count > 0
                        ? $"Loadout ready: {count} item{(count == 1 ? string.Empty : "s")}."
                        : "The loadout file has no entries; the DLL will report loadout_empty.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return new LoadoutPreflightResult(
                    false, 0, path,
                    "The loadout file could not be read: " + ex.Message);
            }
        }
    }
}
