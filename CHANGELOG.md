# Changelog

All notable changes to **ValthvnQuota** are documented in this file.

## [1.7.0] - 2026-10-07

### Added
- **Google Antigravity Provider**: Local RPC discovery and monitoring directly from Antigravity's language server.
- **Quota Pooling Architecture**: Automatically aggregates Antigravity models into two intuitive pools: `Gemini models` and `Claude & GPT`.
- **2-Row Taskbar Presentation**: Tailored taskbar widget showing dual-row quotas for Antigravity matching Codex layout.
- **Provider Selector**: Switch seamlessly between Codex, Antigravity, or Both (compact dual-engine view).

### Changed
- **Dynamic Flyout Resizing**: Window height automatically recalculates and animates to hug content tightly across all providers.
- **Renamed to ValthvnQuota**: Unified project identity and repository structure.

### Fixed
- Fixed unconstrained layout stretch bug in flyout ScrollViewer that caused excessive vertical padding.
- Prevented Codex auto-send triggers from firing on external provider updates.

---

## [1.6.0] - 2026-08-30

- Initial upstream release based on CodexQuota.
- Added session/weekly quota tracking, token heatmap, and auto-send capability for OpenAI Codex.
