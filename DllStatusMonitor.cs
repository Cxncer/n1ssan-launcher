using System;
using System.Globalization;
using System.IO;

namespace n1ssan_launcher
{
    internal sealed record DllStatusSnapshot(
        int ProcessId,
        string BuildId,
        string State,
        string Reason,
        DateTimeOffset TimestampUtc);

    internal static class DllStatusMonitor
    {
        private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(30);

        private static string StatusDirectory
        {
            get
            {
                string local = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(local, "n1ssan", "status");
            }
        }

        public static bool TryRead(int processId, out DllStatusSnapshot? snapshot,
            out string message, out bool isReady, out bool isFailure)
        {
            snapshot = null;
            isReady = false;
            isFailure = false;

            string path = Path.Combine(StatusDirectory, $"cs2-{processId}.status");
            if (!File.Exists(path))
            {
                message = "Awaiting DLL status";
                return false;
            }

            try
            {
                string line = File.ReadAllText(path).Trim();
                string[] fields = line.Split('|');
                if (fields.Length != 7 ||
                    fields[0] != "N1SSAN_STATUS" ||
                    fields[1] != "1" ||
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
                snapshot = new DllStatusSnapshot(
                    reportedPid, fields[4], state, reason, timestamp);

                TimeSpan age = DateTimeOffset.UtcNow - timestamp;
                if (age < TimeSpan.FromMinutes(-5) || age > HeartbeatTimeout)
                {
                    message = $"DLL status stale ({Math.Max(0, (int)age.TotalSeconds)}s)";
                    return true;
                }

                switch (state)
                {
                    case "READY":
                        isReady = true;
                        message = $"DLL ready · {fields[4]}";
                        break;
                    case "FAILED":
                        isFailure = true;
                        message = $"DLL failed · {reason}";
                        break;
                    case "INITIALIZING":
                        message = "DLL initializing";
                        break;
                    case "CHECKS_PASSED":
                        message = "Checks passed · awaiting heartbeat";
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
