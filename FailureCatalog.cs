using System;

namespace n1ssan_launcher
{
    /// <summary>
    /// The shared failure classification (see failure-taxonomy.md). The DLL writes
    /// the code into its status record, the injector prints it in RESULT, and the
    /// launcher translates the same code into one actionable sentence here.
    /// </summary>
    internal static class FailureCatalog
    {
        internal const string None = "E_NONE";

        internal static string Action(string? code)
        {
            return (code ?? string.Empty).ToUpperInvariant() switch
            {
                "E_MINHOOK_LOAD" =>
                    "Place MinHook.dll next to n1ssan-dll.dll, then inject again.",
                "E_CLIENT_MODULE" =>
                    "Wait for Counter-Strike 2 to finish loading before injecting.",
                "E_BRIDGE_DIRECTORY" =>
                    "Start the Bridge from this launcher so the DLL can find its runtime directory.",
                "E_FUNCTION_RESOLUTION" =>
                    "This build predates the installed CS2 update; refresh signatures before injecting.",
                "E_INVENTORY_RUNTIME" =>
                    "Restart CS2 with -allow_third_party_software, then inject again.",
                "E_FSN_HOOK" =>
                    "Another module already hooks FrameStageNotify. Close overlay software and retry.",
                "E_MODE_WORKER" =>
                    "Restart Counter-Strike 2; the runtime-mode worker could not be created.",
                "E_INJECTION_FAILED" =>
                    "Run the launcher as administrator and retry the injection.",
                "E_MODULE_REJECTED" =>
                    "CS2 unloaded the DLL (Trusted Mode). Launch CS2 with -allow_third_party_software.",
                "E_DLL_FAILED" =>
                    "The DLL loaded but never initialised. Read the session log in %LOCALAPPDATA%\\n1ssan\\logs.",
                "E_STATUS_TIMEOUT" =>
                    "No status record appeared. Confirm the DLL file matches the installed CS2 build.",
                "E_GAME_NOT_FOUND" =>
                    "Start Counter-Strike 2 first, then apply from the launcher.",
                "E_DLL_PROBLEM" =>
                    "The DLL is missing or built for another architecture; rebuild the x64 Release target.",
                "E_BRIDGE_UNAVAILABLE" =>
                    "Start the Bridge from this launcher before opening the web UI.",
                "W_OPTIONAL_FEATURE_UNAVAILABLE" =>
                    "Skins and attachments still apply; glove schema support needs a signature refresh.",
                "W_STATUS_STALE" =>
                    "The heartbeat stopped. Confirm CS2 is running, then inject again.",
                "W_STATUS_MISSING" =>
                    "No runtime status yet. Inject the DLL to start publishing one.",
                _ => string.Empty,
            };
        }

        internal static string Describe(string reason, string? code)
        {
            string action = Action(code);
            string suffix = string.IsNullOrEmpty(action) ? string.Empty : " — " + action;
            return string.IsNullOrEmpty(code) || code == None
                ? reason
                : $"{reason} ({code}){suffix}";
        }
    }
}
