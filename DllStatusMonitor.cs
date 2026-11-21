using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace n1ssan_launcher
{
    internal sealed record DllStatusSnapshot(
        int ProcessId,
        string BuildId,
        string State,
        string Reason,
        string Code,
        DateTimeOffset TimestampUtc);

    // What the DLL's schema sweep found on this CS2 build. `CoreTrusted` false
    // means the runtime refused to write, because the field table is stale.
    internal sealed record ArmoryOffsetsReport(
        int Checked,
        int Applied,
        int Unresolved,
        bool CoreTrusted,
        bool SpawnTime);

    internal static class DllStatusMonitor
    {
        private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(30);

        private static string[] StatusDirectories
        {
            get
            {
                string local = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);
                string temp = Path.GetTempPath();

                var directories = new System.Collections.Generic.List<string>();
                if (!string.IsNullOrWhiteSpace(local))
                    directories.Add(Path.Combine(local, "n1ssan", "status"));

                if (!string.IsNullOrWhiteSpace(temp))
                {
                    string fallback = Path.Combine(temp, "n1ssan", "status");
                    bool alreadyAdded = false;
                    foreach (string existing in directories)
                    {
                        if (string.Equals(existing, fallback, StringComparison.OrdinalIgnoreCase))
                        {
                            alreadyAdded = true;
                            break;
                        }
                    }
                    if (!alreadyAdded)
                        directories.Add(fallback);
                }

                return directories.ToArray();
            }
        }

        private static int ReadInt(JsonElement element, string name)
        {
            return element.TryGetProperty(name, out JsonElement value) &&
                value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out int parsed)
                ? parsed
                : 0;
        }

        /// <summary>
        /// Reads the Armory status document the injected DLL publishes next to its
        /// status record. Only a fresh document is accepted: one from a closed
        /// session must never decide whether this one may write.
        /// </summary>
        public static bool TryReadArmoryOffsets(int processId, out ArmoryOffsetsReport? report)
        {
            report = null;

            string fileName = $"armory-{processId}.json";
            foreach (string directory in StatusDirectories)
            {
                string candidate = Path.Combine(directory, fileName);
                if (!File.Exists(candidate))
                    continue;

                try
                {
                    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(candidate));
                    JsonElement root = document.RootElement;

                    if (!root.TryGetProperty("generatedAt", out JsonElement generated) ||
                        generated.ValueKind != JsonValueKind.String ||
                        !DateTimeOffset.TryParse(generated.GetString(), CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                            out DateTimeOffset publishedAt) ||
                        DateTimeOffset.UtcNow - publishedAt > TimeSpan.FromMinutes(2))
                    {
                        return false;
                    }

                    if (!root.TryGetProperty("offsets", out JsonElement offsets) ||
                        offsets.ValueKind != JsonValueKind.Object)
                    {
                        // Older builds did not publish the sweep; nothing to gate on.
                        return false;
                    }

                    report = new ArmoryOffsetsReport(
                        ReadInt(offsets, "checked"),
                        ReadInt(offsets, "applied"),
                        ReadInt(offsets, "unresolved"),
                        !(offsets.TryGetProperty("coreTrusted", out JsonElement trusted) &&
                          trusted.ValueKind == JsonValueKind.False),
                        offsets.TryGetProperty("spawnTime", out JsonElement spawn) &&
                            spawn.ValueKind == JsonValueKind.True);

                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
                {
                    return false;
                }
            }

            return false;
        }

        public static bool TryRead(int processId, out DllStatusSnapshot? snapshot,
            out string message, out bool isReady, out bool isFailure)
        {
            snapshot = null;
            isReady = false;
            isFailure = false;

            string fileName = $"cs2-{processId}.status";
            string? path = null;
            foreach (string directory in StatusDirectories)
            {
                string candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                {
                    path = candidate;
                    break;
                }
            }

            if (path == null)
            {
                message = "Awaiting DLL status (LOCALAPPDATA/TEMP)";
                return false;
            }

            try
            {
                string line = File.ReadAllText(path).Trim();
                string[] fields = line.Split('|');

                // Protocol 1 records carry 7 fields; protocol 2 appends the shared
                // failure classification as code=<CODE>.
                const int ProtocolV1Fields = 7;
                const int ProtocolV2Fields = 8;

                if ((fields.Length != ProtocolV1Fields && fields.Length != ProtocolV2Fields) ||
                    fields[0] != "N1SSAN_STATUS" ||
                    (fields[1] != "1" && fields[1] != "2") ||
                    !int.TryParse(fields[3], NumberStyles.None,
                        CultureInfo.InvariantCulture, out int reportedPid) ||
                    reportedPid != processId ||
                    !DateTimeOffset.TryParse(fields[2], CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out DateTimeOffset timestamp) ||
                    !fields[6].StartsWith("reason=", StringComparison.Ordinal))
                {
                    message = "Invalid DLL status record";
                    return false;
                }

                string state = fields[5].Trim();
                string reason = fields[6].Substring("reason=".Length);

                string code = FailureCatalog.None;
                if (fields.Length == ProtocolV2Fields &&
                    fields[7].StartsWith("code=", StringComparison.Ordinal))
                {
                    code = fields[7].Substring("code=".Length).Trim();
                }

                snapshot = new DllStatusSnapshot(
                    reportedPid, fields[4], state, reason, code, timestamp);

                TimeSpan age = DateTimeOffset.UtcNow - timestamp;
                if (age < TimeSpan.FromMinutes(-5))
                {
                    message = "DLL status timestamp is in the future";
                    return true;
                }

                // FAILED is terminal for this DLL session and remains useful
                // after the worker exits. READY, by contrast, requires a live
                // heartbeat and must never be inferred from an old snapshot.
                if (state == "FAILED")
                {
                    isFailure = true;
                    message = "DLL failed · " + FailureCatalog.Describe(reason, code);
                    return true;
                }

                if (age > HeartbeatTimeout)
                {
                    long ageSeconds = Math.Max(0L, (long)age.TotalSeconds);
                    message = $"DLL status stale ({ageSeconds}s)";
                    return true;
                }

                switch (state)
                {
                    case "READY":
                        isReady = true;
                        message = $"DLL ready · {fields[4]}";
                        break;
                    case "INITIALIZING":
                        message = "DLL initializing";
                        break;
                    case "CHECKS_PASSED":
                        message = "Checks passed · awaiting heartbeat";
                        break;
                    case "DEGRADED":
                        // The DLL contract treats DEGRADED as initialized with an optional
                        // feature missing, so it satisfies the DOMINATE readiness check.
                        isReady = true;
                        message = $"DLL ready (degraded) · {FailureCatalog.Describe(reason, code)}";
                        break;
                    default:
                        message = $"DLL state: {state}";
                        break;
                }

                return true;
            }
            catch (IOException)
            {
                message = "DLL status temporarily unavailable";
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                message = "DLL status access denied";
                return false;
            }
            catch (Exception ex)
            {
                message = "DLL status read failed: " + ex.Message;
                return false;
            }
        }
    }
}
