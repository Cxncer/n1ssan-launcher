# DOMINATE DLL status monitoring

DOMINATE reads the versioned status snapshot written by the DLL. It does not load the DLL or initiate any game-process modification.

- Snapshot location: `%LOCALAPPDATA%\\n1ssan\\status\\cs2-<PID>.status`
- Poll interval: the existing 2-second status timer.
- A snapshot is accepted only when its protocol version and PID match.
- READY is displayed as connected only while the 10-second worker heartbeat is no older than 30 seconds.
- CHECKS_PASSED is explicitly not displayed as runtime-ready.
- FAILED is surfaced in the DLL indicator and startup diagnostic log.
- Missing, malformed, inaccessible or stale snapshots are never treated as READY.

The existing Bridge and Obsidian Web startup pipeline is unchanged. The current project still has no authorized DLL-loading integration; this monitor observes a DLL session only when one is already present in the CS2 process.
