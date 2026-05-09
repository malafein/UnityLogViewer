# Changelog

## 0.0.3
- **Feature**: Upgraded the log viewer to use BepInEx's `ILogListener`. The viewer now captures all mod logs (`Logger.LogInfo`) instead of just Unity engine logs, perfectly mirroring `LogOutput.log`.
- **Fix**: Resolved a major scrolling bug where multi-line logs (like exceptions) and the horizontal scrollbar would cause the virtual scroll view to miscalculate its bounds and prevent scrolling to the bottom.

## 0.0.2
- Added a config entry ShowWindow as a fallback for games not compatible with current keyboard shortcut.

## 0.0.1

- Initial release.
