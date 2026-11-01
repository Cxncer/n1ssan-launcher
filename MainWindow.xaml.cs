
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace n1ssan_launcher
{
    public partial class MainWindow : Window
    {
        private readonly DispatcherTimer _statusTimer =
            new DispatcherTimer();

        private static readonly HttpClient _httpClient =
            new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(2)
            };

        private bool _bridgeCheckInProgress;
        private bool _preflightPassed;
        private bool _dominateInProgress;

        private JsonElement? _validatedLoadoutSnapshot;

        private const string HealthUrl =
            "http://127.0.0.1:3210/health";

        private const string SkinsUrl =
            "http://127.0.0.1:3210/skins";

        private const string ConsumerExecutablePath =
            @"E:\Vibe Coding\CS2 Lore\CS2 Lore\n1ssan-loadout-consumer\x64\Debug\n1ssan-loadout-consumer.exe";

        private static string ConsumerLogPath =>
            Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    @"..\..\..\loadout-consumer-output.txt"));

        public MainWindow()
        {
            InitializeComponent();

            Background = new SolidColorBrush(
                Color.FromRgb(8, 9, 9));

            _statusTimer.Interval = TimeSpan.FromSeconds(2);

            _statusTimer.Tick += async (_, _) =>
            {
                UpdateCS2Status();
                await UpdateBridgeStatusAsync();
            };

            UpdateCS2Status();
            _ = UpdateBridgeStatusAsync();

            _statusTimer.Start();
        }

        // CHECK 1: Detect whether CS2 is running.
        private bool IsCS2Running()
        {
            Process[] processes =
                Process.GetProcessesByName("cs2");

            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (Process process in processes)
                    process.Dispose();
            }
        }

        private void UpdateCS2Status()
        {
            bool isRunning = IsCS2Running();

            Cs2StatusCircle.Stroke = isRunning
                ? new SolidColorBrush(Color.FromRgb(30, 215, 96))
                : new SolidColorBrush(Color.FromRgb(146, 151, 144));

            Cs2StatusCircle.Fill = isRunning
                ? new SolidColorBrush(Color.FromArgb(70, 30, 215, 96))
                : new SolidColorBrush(Color.FromArgb(51, 23, 24, 25));

            Cs2StatusText.Text = isRunning
                ? "CS2 is running"
                : "Waiting for game";

            Cs2StatusText.Foreground = isRunning
                ? new SolidColorBrush(Color.FromRgb(30, 215, 96))
                : new SolidColorBrush(Color.FromRgb(146, 151, 144));
        }

        // CHECKS 2 AND 3: Read local n1ssan status.
        private async Task UpdateBridgeStatusAsync()
        {
            if (_bridgeCheckInProgress)
                return;

            _bridgeCheckInProgress = true;

            try
            {
                using HttpResponseMessage response =
                    await _httpClient.GetAsync(HealthUrl);

                response.EnsureSuccessStatusCode();

                string json =
                    await response.Content.ReadAsStringAsync();

                using JsonDocument document =
                    JsonDocument.Parse(json);

                JsonElement root = document.RootElement;

                bool hasLoadout =
                    root.TryGetProperty(
                        "hasLoadout",
                        out JsonElement loadoutValue)
                    && loadoutValue.ValueKind == JsonValueKind.True;

                bool hasLoadoutName =
                    root.TryGetProperty(
                        "loadoutName",
                        out JsonElement nameValue)
                    && nameValue.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(nameValue.GetString());

                string loadoutName = hasLoadoutName
                    ? nameValue.GetString()!
                    : "Unknown Loadout";

                SetStatus(
                    BridgeStatusCircle,
                    BridgeStatusText,
                    true,
                    "n1ssan Online");

                SetStatus(
                    DataStatusCircle,
                    DataStatusText,
                    hasLoadout && hasLoadoutName,
                    hasLoadout && hasLoadoutName
                        ? $"Selected Loadout: {loadoutName}"
                        : "Selected Loadout: None");

                if (!_preflightPassed)
                {
                    SystemReadinessText.Text =
                        hasLoadout && hasLoadoutName
                            ? "System checks complete — loadout available"
                            : "Bridge online — awaiting a saved loadout";

                    SystemReadinessText.Foreground =
                        new SolidColorBrush(
                            hasLoadout && hasLoadoutName
                                ? Color.FromRgb(30, 215, 96)
                                : Color.FromRgb(230, 170, 60));
                }
            }
            catch (Exception)
            {
                _preflightPassed = false;
                _validatedLoadoutSnapshot = null;

                SetStatus(
                    BridgeStatusCircle,
                    BridgeStatusText,
                    false,
                    "n1ssan Offline");

                SetStatus(
                    DataStatusCircle,
                    DataStatusText,
                    false,
                    "Data unavailable");

                SystemReadinessText.Text =
                    "System check failed — bridge unavailable";

                SystemReadinessText.Foreground =
                    new SolidColorBrush(Color.FromRgb(240, 100, 100));
            }
            finally
            {
                _bridgeCheckInProgress = false;
            }
        }

        private void SetStatus(
            System.Windows.Shapes.Ellipse circle,
            System.Windows.Controls.TextBlock label,
            bool isReady,
            string message)
        {
            circle.Stroke = isReady
                ? new SolidColorBrush(Color.FromRgb(30, 215, 96))
                : new SolidColorBrush(Color.FromRgb(146, 151, 144));

            circle.Fill = isReady
                ? new SolidColorBrush(Color.FromArgb(70, 30, 215, 96))
                : new SolidColorBrush(Color.FromArgb(51, 23, 24, 25));

            label.Text = message;

            label.Foreground = isReady
                ? new SolidColorBrush(Color.FromRgb(30, 215, 96))
                : new SolidColorBrush(Color.FromRgb(146, 151, 144));
        }

        private bool TryGetValidatedLoadoutSnapshot(
            out JsonElement snapshot)
        {
            snapshot = default;

            if (!_preflightPassed ||
                !_validatedLoadoutSnapshot.HasValue)
            {
                return false;
            }

            snapshot = _validatedLoadoutSnapshot.Value;
            return true;
        }

        private bool VerifyValidatedLoadoutSnapshot()
        {
            if (!_validatedLoadoutSnapshot.HasValue)
                return false;

            JsonElement snapshot =
                _validatedLoadoutSnapshot.Value;

            if (!snapshot.TryGetProperty(
                    "loadout", out JsonElement loadout)
                || loadout.ValueKind != JsonValueKind.Object
                || !snapshot.TryGetProperty(
                    "skins", out JsonElement skins)
                || skins.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            if (!loadout.TryGetProperty(
                    "id", out JsonElement id)
                || !id.TryGetInt32(out int loadoutId)
                || loadoutId <= 0)
            {
                return false;
            }

            if (!loadout.TryGetProperty(
                    "itemCount", out JsonElement itemCount)
                || !itemCount.TryGetInt32(
                    out int declaredItemCount)
                || declaredItemCount < 0)
            {
                return false;
            }

            return declaredItemCount == skins.GetArrayLength();
        }

        private string ExportValidatedLoadoutSnapshot()
        {
            if (!TryGetValidatedLoadoutSnapshot(
                    out JsonElement snapshot))
            {
                throw new InvalidOperationException(
                    "No validated loadout snapshot is available.");
            }

            string outputDirectory = Path.Combine(
                AppContext.BaseDirectory,
                "loadout-output");

            Directory.CreateDirectory(outputDirectory);

            string outputPath = Path.Combine(
                outputDirectory,
                "loadout-snapshot.json");

            string temporaryPath = outputPath + ".tmp";

            string json = JsonSerializer.Serialize(
                snapshot,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });

            File.WriteAllText(temporaryPath, json);

            File.Move(temporaryPath, outputPath, true);

            return outputDirectory;
        }

        // Run the standalone C++ consumer and capture its output.
        private async Task<(int ExitCode, string Output)>
            RunLoadoutConsumerAsync(string snapshotDirectory)
        {
            if (!File.Exists(ConsumerExecutablePath))
            {
                throw new FileNotFoundException(
                    "The standalone C++ loadout consumer was not found.",
                    ConsumerExecutablePath);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = ConsumerExecutablePath,
                WorkingDirectory = snapshotDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process
            {
                StartInfo = startInfo
            };

            if (!process.Start())
            {
                throw new InvalidOperationException(
                    "Failed to start the C++ loadout consumer.");
            }

            Task<string> stdoutTask =
                process.StandardOutput.ReadToEndAsync();

            Task<string> stderrTask =
                process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            string stdout = await stdoutTask;
            string stderr = await stderrTask;

            var output = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(stdout))
                output.AppendLine(stdout.TrimEnd());

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                if (output.Length > 0)
                    output.AppendLine();

                output.AppendLine("[STDERR]");
                output.AppendLine(stderr.TrimEnd());
            }

            return (process.ExitCode, output.ToString());
        }

        // FINAL HANDOFF:
        // Confirm that the C++ consumer's JSON exists and matches
        // the loadout snapshot that was supplied to the consumer.
        private bool VerifyConsumedLoadout(
            string snapshotDirectory,
            JsonElement expectedSnapshot,
            out string consumedJsonPath,
            out string error)
        {
            consumedJsonPath = Path.Combine(
                snapshotDirectory,
                "loadout-consumed.json");

            error = "";

            try
            {
                if (!File.Exists(consumedJsonPath))
                {
                    error =
                        "C++ consumer exited successfully but " +
                        "loadout-consumed.json was not created.";

                    return false;
                }

                using JsonDocument document =
                    JsonDocument.Parse(
                        File.ReadAllText(consumedJsonPath));

                JsonElement consumed = document.RootElement;

                if (consumed.ValueKind != JsonValueKind.Object)
                {
                    error = "Consumer output must be a JSON object.";
                    return false;
                }

                if (!consumed.TryGetProperty(
                        "schemaVersion", out JsonElement schemaVersion)
                    || !schemaVersion.TryGetInt32(out int version)
                    || version != 1)
                {
                    error =
                        "Consumer output has a missing or unsupported schemaVersion.";

                    return false;
                }

                if (!consumed.TryGetProperty(
                        "result", out JsonElement result)
                    || result.ValueKind != JsonValueKind.String
                    || result.GetString() != "PASS")
                {
                    error =
                        "Consumer JSON does not declare result PASS.";

                    return false;
                }

                if (!expectedSnapshot.TryGetProperty(
                        "loadout", out JsonElement expectedLoadout)
                    || !expectedLoadout.TryGetProperty(
                        "id", out JsonElement expectedId)
                    || !expectedId.TryGetInt32(out int expectedLoadoutId))
                {
                    error =
                        "The expected snapshot does not contain a valid loadout ID.";

                    return false;
                }

                if (!consumed.TryGetProperty(
                        "loadout", out JsonElement consumedLoadout)
                    || consumedLoadout.ValueKind != JsonValueKind.Object
                    || !consumedLoadout.TryGetProperty(
                        "id", out JsonElement consumedId)
                    || !consumedId.TryGetInt32(out int consumedLoadoutId))
                {
                    error =
                        "Consumer JSON is missing a valid loadout ID.";

                    return false;
                }

                if (consumedLoadoutId != expectedLoadoutId)
                {
                    error =
                        $"Loadout ID mismatch. Expected {expectedLoadoutId}, " +
                        $"received {consumedLoadoutId}.";

                    return false;
                }

                if (!expectedSnapshot.TryGetProperty(
                        "skins", out JsonElement expectedItems)
                    || expectedItems.ValueKind != JsonValueKind.Array)
                {
                    error = "The expected snapshot has no valid skins array.";
                    return false;
                }

                int expectedCount = expectedItems.GetArrayLength();

                if (!consumed.TryGetProperty(
                        "itemCount", out JsonElement outputCount)
                    || !outputCount.TryGetInt32(out int parsedOutputCount)
                    || parsedOutputCount != expectedCount)
                {
                    error =
                        $"Consumer itemCount mismatch. Expected {expectedCount}.";

                    return false;
                }

                if (!consumedLoadout.TryGetProperty(
                        "itemCount", out JsonElement declaredCount)
                    || !declaredCount.TryGetInt32(out int parsedDeclaredCount)
                    || parsedDeclaredCount != expectedCount)
                {
                    error =
                        "The consumed loadout's declared itemCount is inconsistent.";

                    return false;
                }

                if (!consumed.TryGetProperty(
                        "items", out JsonElement items)
                    || items.ValueKind != JsonValueKind.Array
                    || items.GetArrayLength() != expectedCount)
                {
                    error =
                        $"Consumer items array mismatch. Expected {expectedCount} items.";

                    return false;
                }

                // Ensure each exported item corresponds to the snapshot item
                // at the same position, including the fields the consumer
                // uses for its validation.
                for (int i = 0; i < expectedCount; i++)
                {
                    JsonElement expectedItem = expectedItems[i];
                    JsonElement consumedItem = items[i];

                    if (expectedItem.ValueKind != JsonValueKind.Object ||
                        consumedItem.ValueKind != JsonValueKind.Object)
                    {
                        error = $"Item {i + 1} is not a JSON object.";
                        return false;
                    }

                    string[] requiredFields =
                    {
                        "weapon", "name", "team", "slot",
                        "paintKit", "minFloat", "maxFloat", "float"
                    };

                    foreach (string field in requiredFields)
                    {
                        bool expectedHas =
                            expectedItem.TryGetProperty(
                                field, out JsonElement expectedValue);

                        bool consumedHas =
                            consumedItem.TryGetProperty(
                                field, out JsonElement consumedValue);

                        if (expectedHas != consumedHas)
                        {
                            error =
                                $"Item {i + 1} field '{field}' differs " +
                                "between snapshot and consumer output.";

                            return false;
                        }

                        if (expectedHas &&
                            !JsonValuesEqual(expectedValue, consumedValue))
                        {
                            error =
                                $"Item {i + 1} field '{field}' does not " +
                                "match the original snapshot.";

                            return false;
                        }
                    }
                }

                return true;
            }
            catch (JsonException ex)
            {
                error =
                    "Consumer output is not valid JSON: " + ex.Message;

                return false;
            }
            catch (IOException ex)
            {
                error =
                    "Could not read consumer JSON: " + ex.Message;

                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                error =
                    "Access denied reading consumer JSON: " + ex.Message;

                return false;
            }
        }

        // Save the latest run's results to the launcher project directory.
        private void WriteConsumerLog(string content)
        {
            string? directory =
                Path.GetDirectoryName(ConsumerLogPath);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(
                ConsumerLogPath,
                content,
                new UTF8Encoding(false));
        }

        private string BuildLog(
            string result,
            int? exitCode = null,
            string details = "")
        {
            var log = new StringBuilder();

            log.AppendLine("n1ssan Loadout Consumer");
            log.AppendLine("=======================");
            log.AppendLine(
                $"Timestamp: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
            log.AppendLine($"Result: {result}");

            if (exitCode.HasValue)
                log.AppendLine($"Exit code: {exitCode.Value}");

            log.AppendLine(
                $"Snapshot: {Path.Combine(
                    AppContext.BaseDirectory,
                    @"loadout-output\loadout-snapshot.json")}");

            log.AppendLine(
                $"Consumer JSON: {Path.Combine(
                    AppContext.BaseDirectory,
                    @"loadout-output\loadout-consumed.json")}");

            log.AppendLine();

            if (!string.IsNullOrWhiteSpace(details))
            {
                log.AppendLine("Output / Details:");
                log.AppendLine("-----------------");
                log.AppendLine(details.TrimEnd());
            }

            return log.ToString();
        }

        private async void Dominate_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (_dominateInProgress)
                return;

            _dominateInProgress = true;
            _preflightPassed = false;
            _validatedLoadoutSnapshot = null;

            try
            {
                SystemReadinessText.Text =
                    "Checking CS2 status...";

                SystemReadinessText.Foreground =
                    new SolidColorBrush(Color.FromRgb(230, 170, 60));

                if (!IsCS2Running())
                {
                    FailPreflight(
                        "Preflight failed — CS2 is not running",
                        "Counter-Strike 2 is not running. Launch CS2 and try again.");

                    return;
                }

                SystemReadinessText.Text =
                    "Running preflight checks...";

                // 1. Verify the bridge is online.
                using HttpResponseMessage healthResponse =
                    await _httpClient.GetAsync(HealthUrl);

                healthResponse.EnsureSuccessStatusCode();

                using JsonDocument healthDocument =
                    JsonDocument.Parse(
                        await healthResponse.Content.ReadAsStringAsync());

                JsonElement health = healthDocument.RootElement;

                bool bridgeOnline =
                    health.TryGetProperty(
                        "status", out JsonElement statusValue)
                    && statusValue.ValueKind == JsonValueKind.String
                    && statusValue.GetString() == "online";

                if (!bridgeOnline)
                {
                    FailPreflight(
                        "Preflight failed — bridge offline",
                        "The n1ssan bridge is offline. Start the bridge and try again.");

                    return;
                }

                // 2. Retrieve the saved loadout.
                using HttpResponseMessage skinsResponse =
                    await _httpClient.GetAsync(SkinsUrl);

                skinsResponse.EnsureSuccessStatusCode();

                using JsonDocument skinsDocument =
                    JsonDocument.Parse(
                        await skinsResponse.Content.ReadAsStringAsync());

                JsonElement root = skinsDocument.RootElement;

                if (!root.TryGetProperty(
                        "loadout", out JsonElement loadout)
                    || loadout.ValueKind != JsonValueKind.Object)
                {
                    FailPreflight(
                        "Preflight failed — no valid loadout",
                        "No saved loadout was found. Save a loadout in n1ssan and try again.");

                    return;
                }

                if (!root.TryGetProperty(
                        "skins", out JsonElement skins)
                    || skins.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException(
                        "The skins field is missing or is not an array.");
                }

                // 3. Validate loadout metadata.
                int parsedLoadoutId = 0;
                string validationError = "";

                bool valid =
                    loadout.TryGetProperty(
                        "id", out JsonElement loadoutId)
                    && loadoutId.TryGetInt32(out parsedLoadoutId)
                    && parsedLoadoutId > 0;

                if (!valid)
                    validationError = "Loadout ID is missing or invalid.";

                if (valid && !HasNonEmptyString(loadout, "profile"))
                {
                    valid = false;
                    validationError = "Loadout profile is missing or empty.";
                }

                if (valid &&
                    (!loadout.TryGetProperty(
                        "team", out JsonElement primaryTeam)
                    || !IsValidTeam(primaryTeam)))
                {
                    valid = false;
                    validationError = "Loadout team must be T or CT.";
                }

                JsonElement teams = default;

                if (valid &&
                    (!loadout.TryGetProperty("teams", out teams)
                    || teams.ValueKind != JsonValueKind.Array
                    || teams.GetArrayLength() == 0))
                {
                    valid = false;
                    validationError =
                        "Loadout teams must be a non-empty array.";
                }

                if (valid)
                {
                    foreach (JsonElement team in teams.EnumerateArray())
                    {
                        if (!IsValidTeam(team))
                        {
                            valid = false;
                            validationError =
                                "Loadout teams contains an invalid team.";
                            break;
                        }
                    }
                }

                if (valid &&
                    (!loadout.TryGetProperty(
                        "receivedAt", out JsonElement receivedAt)
                    || receivedAt.ValueKind != JsonValueKind.String
                    || !DateTimeOffset.TryParse(
                        receivedAt.GetString(), out _)))
                {
                    valid = false;
                    validationError =
                        "Loadout receivedAt timestamp is missing or invalid.";
                }

                if (valid &&
                    (!loadout.TryGetProperty(
                        "itemCount", out JsonElement itemCount)
                    || !itemCount.TryGetInt32(out int parsedItemCount)
                    || parsedItemCount != skins.GetArrayLength()))
                {
                    valid = false;
                    validationError =
                        "Loadout itemCount does not match the skins array.";
                }

                // 4. Validate individual skin records.
                var assignedSlots = new HashSet<string>();
                int index = 0;

                const long MaxSafeInteger = 9007199254740991L;

                foreach (JsonElement skin in skins.EnumerateArray())
                {
                    index++;

                    if (skin.ValueKind != JsonValueKind.Object)
                    {
                        valid = false;
                        validationError =
                            $"Skin record {index} is not an object.";
                        break;
                    }

                    bool validSkin =
                        skin.TryGetProperty(
                            "id", out JsonElement skinId)
                        && skinId.TryGetInt64(out long parsedSkinId)
                        && parsedSkinId > 0
                        && parsedSkinId <= MaxSafeInteger;

                    validSkin = validSkin
                        && HasNonEmptyString(skin, "weapon");

                    validSkin = validSkin
                        && HasNonEmptyString(skin, "name");

                    validSkin = validSkin
                        && skin.TryGetProperty(
                            "team", out JsonElement skinTeam)
                        && IsValidTeam(skinTeam);

                    validSkin = validSkin
                        && skin.TryGetProperty(
                            "slot", out JsonElement slot)
                        && IsValidSlot(slot);

                    if (validSkin &&
                        skin.GetProperty("slot").GetString() == "weapon")
                    {
                        validSkin =
                            HasNonEmptyString(skin, "equippedFor");

                        validSkin = validSkin
                            && skin.TryGetProperty(
                                "float", out JsonElement floatValue)
                            && floatValue.TryGetDouble(out double wear)
                            && double.IsFinite(wear)
                            && wear >= 0
                            && wear <= 1;

                        validSkin = validSkin
                            && skin.TryGetProperty(
                                "seed", out JsonElement seedValue)
                            && seedValue.TryGetInt64(out long seed)
                            && seed >= 0
                            && seed <= MaxSafeInteger;
                    }

                    if (!validSkin)
                    {
                        valid = false;
                        validationError =
                            $"Skin record {index} has invalid or missing required fields.";
                        break;
                    }

                    string teamName =
                        skin.GetProperty("team").GetString()!;

                    string slotName =
                        skin.GetProperty("slot").GetString()!;

                    string weaponName =
                        skin.GetProperty("weapon").GetString()!;

                    string assignmentKey = slotName == "weapon"
                        ? $"{teamName}|weapon|{weaponName}"
                        : $"{teamName}|{slotName}";

                    if (!assignedSlots.Add(assignmentKey))
                    {
                        valid = false;
                        validationError =
                            $"Duplicate equipment assignment at record {index}: " +
                            $"{teamName} / {slotName}" +
                            (slotName == "weapon"
                                ? $" / {weaponName}"
                                : "") + ".";

                        break;
                    }
                }

                // 5. Reject invalid records before taking a snapshot.
                if (!valid)
                {
                    FailPreflight(
                        "Preflight failed — invalid loadout data",
                        $"Skin records inspected: {index}\n" +
                        $"{validationError}\n" +
                        $"Expected skin count: {skins.GetArrayLength()}");

                    return;
                }

                // 6. Preserve and verify the validated response.
                _validatedLoadoutSnapshot = root.Clone();

                if (!VerifyValidatedLoadoutSnapshot())
                {
                    FailPreflight(
                        "Preflight failed — snapshot integrity check failed",
                        "The validated loadout snapshot failed its integrity check.");

                    return;
                }

                _preflightPassed = true;

                // 7. Export the snapshot.
                string snapshotDirectory =
                    ExportValidatedLoadoutSnapshot();

                // 8. Run the standalone C++ consumer.
                SystemReadinessText.Text =
                    "Snapshot exported — running C++ consumer...";

                SystemReadinessText.Foreground =
                    new SolidColorBrush(Color.FromRgb(230, 170, 60));

                (int ExitCode, string Output) consumerResult =
                    await RunLoadoutConsumerAsync(snapshotDirectory);

                string resultName = consumerResult.ExitCode == 0
                    ? "SUCCESS"
                    : "FAILED";

                // Log the consumer's process output.
                WriteConsumerLog(
                    BuildLog(
                        resultName,
                        consumerResult.ExitCode,
                        string.IsNullOrWhiteSpace(consumerResult.Output)
                            ? "(The consumer returned no output.)"
                            : consumerResult.Output));

                if (consumerResult.ExitCode != 0)
                {
                    _preflightPassed = false;

                    SystemReadinessText.Text =
                        $"C++ consumer failed — exit code {consumerResult.ExitCode}";

                    SystemReadinessText.Foreground =
                        new SolidColorBrush(Color.FromRgb(240, 80, 80));

                    return;
                }

                // 9. Verify the machine-readable output.
                if (!_validatedLoadoutSnapshot.HasValue)
                {
                    _preflightPassed = false;

                    FailPreflight(
                        "Handoff failed — validated snapshot unavailable",
                        "The consumer completed, but the expected snapshot is no longer available.");

                    return;
                }

                bool consumedOutputValid = VerifyConsumedLoadout(
                    snapshotDirectory,
                    _validatedLoadoutSnapshot.Value,
                    out string consumedJsonPath,
                    out string consumedOutputError);

                if (!consumedOutputValid)
                {
                    _preflightPassed = false;

                    string failureDetails =
                        $"C++ consumer exit code: {consumerResult.ExitCode}\n" +
                        $"Consumer JSON: {consumedJsonPath}\n" +
                        $"Handoff validation: FAILED\n" +
                        $"Reason: {consumedOutputError}\n\n" +
                        consumerResult.Output;

                    WriteConsumerLog(
                        BuildLog(
                            "HANDOFF FAILED",
                            consumerResult.ExitCode,
                            failureDetails));

                    SystemReadinessText.Text =
                        "Handoff failed — consumer JSON invalid";

                    SystemReadinessText.Foreground =
                        new SolidColorBrush(Color.FromRgb(240, 80, 80));

                    return;
                }

                // 10. Final success: process and JSON handoff both passed.
                WriteConsumerLog(
                    BuildLog(
                        "SUCCESS — CONSUMER AND JSON HANDOFF VERIFIED",
                        consumerResult.ExitCode,
                        consumerResult.Output +
                        "\n\nHandoff verification: PASS\n" +
                        "Consumer JSON: " + consumedJsonPath +
                        "\nLoadout ID and item count match the snapshot.\n" +
                        "All exported item fields checked by the launcher match the snapshot."));

                SystemReadinessText.Text =
                    "DOMINATE passed — C++ consumer and JSON handoff verified.";

                SystemReadinessText.Foreground =
                    new SolidColorBrush(Color.FromRgb(30, 215, 96));
            }
            catch (Exception ex)
            {
                _preflightPassed = false;
                _validatedLoadoutSnapshot = null;

                SystemReadinessText.Text =
                    "Preflight failed — check output log";

                SystemReadinessText.Foreground =
                    new SolidColorBrush(Color.FromRgb(240, 80, 80));

                try
                {
                    WriteConsumerLog(
                        BuildLog(
                            "ERROR",
                            null,
                            ex.ToString()));
                }
                catch (Exception logException)
                {
                    SystemReadinessText.Text =
                        "Preflight failed — could not write output log";

                    Debug.WriteLine(logException);
                }

                Debug.WriteLine(ex);
            }
            finally
            {
                _dominateInProgress = false;
            }
        }

        private void FailPreflight(
            string statusMessage,
            string details)
        {
            _preflightPassed = false;
            _validatedLoadoutSnapshot = null;

            SystemReadinessText.Text = statusMessage;

            SystemReadinessText.Foreground =
                new SolidColorBrush(Color.FromRgb(240, 80, 80));

            try
            {
                WriteConsumerLog(
                    BuildLog("PREFLIGHT FAILED", null, details));
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "Could not write consumer log: " + ex);
            }
        }


        private static bool JsonValuesEqual(
            JsonElement left,
            JsonElement right)
        {
            if (left.ValueKind != right.ValueKind)
            {
                // JSON numbers can have different textual forms
                // while representing the same numeric value.
                if (left.ValueKind == JsonValueKind.Number &&
                    right.ValueKind == JsonValueKind.Number)
                {
                    return left.TryGetDecimal(out decimal leftDecimal)
                        && right.TryGetDecimal(out decimal rightDecimal)
                        && leftDecimal == rightDecimal;
                }

                return false;
            }

            switch (left.ValueKind)
            {
                case JsonValueKind.Object:
                    {
                        var leftProperties =
                            new Dictionary<string, JsonElement>(
                                StringComparer.Ordinal);

                        foreach (JsonProperty property in left.EnumerateObject())
                        {
                            leftProperties[property.Name] = property.Value;
                        }

                        int rightCount = 0;

                        foreach (JsonProperty property in right.EnumerateObject())
                        {
                            rightCount++;

                            if (!leftProperties.TryGetValue(
                                    property.Name, out JsonElement leftValue)
                                || !JsonValuesEqual(leftValue, property.Value))
                            {
                                return false;
                            }
                        }

                        return leftProperties.Count == rightCount;
                    }

                case JsonValueKind.Array:
                    {
                        if (left.GetArrayLength() != right.GetArrayLength())
                            return false;

                        for (int i = 0; i < left.GetArrayLength(); i++)
                        {
                            if (!JsonValuesEqual(left[i], right[i]))
                                return false;
                        }

                        return true;
                    }

                case JsonValueKind.String:
                    return left.GetString() == right.GetString();

                case JsonValueKind.Number:
                    if (left.TryGetDecimal(out decimal leftNumber) &&
                        right.TryGetDecimal(out decimal rightNumber))
                    {
                        return leftNumber == rightNumber;
                    }

                    return left.TryGetDouble(out double leftDouble)
                        && right.TryGetDouble(out double rightDouble)
                        && double.IsFinite(leftDouble)
                        && double.IsFinite(rightDouble)
                        && leftDouble == rightDouble;

                case JsonValueKind.True:
                case JsonValueKind.False:
                    return left.GetBoolean() == right.GetBoolean();

                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return true;

                default:
                    return false;
            }
        }

        private static bool HasNonEmptyString(
            JsonElement element,
            string property)
        {
            return element.TryGetProperty(
                    property, out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString());
        }

        private static bool IsValidTeam(JsonElement value)
        {
            return value.ValueKind == JsonValueKind.String
                && (value.GetString() == "T"
                    || value.GetString() == "CT");
        }

        private static bool IsValidSlot(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.String)
                return false;

            string? slot = value.GetString();

            return slot == "weapon"
                || slot == "agent"
                || slot == "knife"
                || slot == "gloves"
                || slot == "zeus";
        }

        // Window controls.
        private void Minimize_Click(
            object sender,
            RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void Close_Click(
            object sender,
            RoutedEventArgs e)
        {
            Close();
        }

        private void Header_MouseLeftButtonDown(
            object sender,
            System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.LeftButton ==
                System.Windows.Input.MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _statusTimer.Stop();
            base.OnClosed(e);
        }
    }
}
