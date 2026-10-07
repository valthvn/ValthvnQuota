using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ValthvnQuota.Usage;
using ValthvnQuota.Usage.Providers;

namespace ValthvnQuota
{
    /// <summary>
    /// Drives the app: on a timer it fetches Codex usage and raises <see cref="StateChanged"/> so the
    /// widget and flyout can update. Single shared instance for the process.
    /// </summary>
    public sealed class UsageCoordinator
    {
        public static UsageCoordinator Instance { get; } = new();

        private readonly UsageService _service = new(UsageSnapshotStore.DefaultDirectory);
        private Timer? _timer;
        private UsageResult? _lastState;
        private readonly ConcurrentDictionary<ProviderId, UsageResult> _states = new();
        private CodexProfileSnapshot? _lastProfile;
        private int _pollInFlight;
        private volatile bool _flyoutOpen;
        private DateTimeOffset? _lastFlyoutOpenAtUtc;

        public UsageService Service => _service;

        /// <summary>The single provider this build supports.</summary>
        public ProviderId? ActiveProvider => WidgetDisplayProviders[0];

        public ProviderId? WidgetDisplayProvider => ActiveProvider;

        /// <summary>Maximum number of quota tile slots allocated by the widget.</summary>
        public const int MaxWidgetTiles = 3;

        /// <summary>Effective quota-tile cap (no activity discount — Codex-only).</summary>
        public static int MaxDisplayedWidgetTiles => MaxWidgetTiles;

        /// <summary>Every provider the taskbar widget renders as its own tile.</summary>
        public IReadOnlyList<ProviderId> WidgetDisplayProviders => QuotaProviderSettings.ProvidersFor(QuotaProviderSettings.DisplayMode);

        /// <summary>Most recently active providers (Codex is the only one).</summary>
        public IReadOnlyList<ProviderId> RecentProviders => WidgetDisplayProviders;

        private UsageCoordinator()
        {
            QuotaProviderSettings.Changed += () =>
            {
                foreach (var id in WidgetDisplayProviders)
                    if (GetState(id) is { } state) StateChanged?.Invoke(state);
                _ = FetchAndPublishAsync(force: false);
            };

            LanguageSettings.Changed += () =>
            {
                foreach (var id in WidgetDisplayProviders)
                    if (GetState(id) is { } state) StateChanged?.Invoke(state);
            };
        }

        public UsageResult? GetState(ProviderId id)
        {
            if (_states.TryGetValue(id, out var state)) return state;
            if (_service.TryGetCached(id, out var cached)) return cached;
            if (_service.TryGetLastSuccessfulLiveResult(id, out var restored)) return restored;
            return _service.Get(id) is { } provider ? UsageResult.Pending(id, provider, "Loading...") : null;
        }

        /// <summary>Last usage snapshot pushed to listeners; used to hydrate the widget if created late.</summary>
        public UsageResult? LastState => _lastState;

        /// <summary>Last Codex profile snapshot pushed to listeners; used to hydrate the flyout if created late.</summary>
        public CodexProfileSnapshot? LastProfile => _lastProfile;
        private CodexProfileSnapshot? _lastAntigravityProfile;
        public CodexProfileSnapshot? LastAntigravityProfile => _lastAntigravityProfile;


        /// <summary>Codex is always present when this app runs.</summary>
        public bool IsActiveToolPresent => true;

        /// <summary>Focus never hides the tile — Codex-only build.</summary>
        public bool IsActiveTileAllowedByFocus => true;

        /// <summary>Raised whenever a fetch completes (success or degraded failure), with the new state.</summary>
        public event Action<UsageResult>? StateChanged;

        /// <summary>Raised whenever the Codex profile fetch completes with a parsed snapshot.</summary>
        public event Action<CodexProfileSnapshot>? ProfileChanged;
        public event Action<CodexProfileSnapshot>? AntigravityProfileChanged;


        /// <summary>Starts the poll loop. Idempotent; fires the first fetch immediately.</summary>
        public void Start()
        {
            if (_timer != null)
                return;

            _timer = new Timer(
                static _ => _ = Instance.RunPollTickAsync(),
                null,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            ScheduleNextPoll(TimeSpan.Zero);
        }

        /// <summary>Re-arms the one-shot poll timer with the policy-chosen delay.</summary>
        private void ScheduleNextPoll(TimeSpan delay)
        {
            _timer?.Change(delay, Timeout.InfiniteTimeSpan);
        }

        private async Task RunPollTickAsync()
        {
            try
            {
                // A tick that outlives its delay must not overlap the next: the usage + profile
                // fetches would double-pump the API. The user-driven refresh path (force: true) and
                // the taskbar-sync alias stay unguarded so they always run.
                if (Interlocked.Exchange(ref _pollInFlight, 1) != 0)
                {
                    ScheduleNextPoll(ComputeNextDelay(DateTimeOffset.UtcNow));
                    return;
                }
                try
                {
                    // Missed-reset catch-up: a quota window whose reset passed since the last
                    // successful fetch means the cached values are pre-reset. Force a live fetch —
                    // a plain tick would otherwise be served from the 60s success TTL.
                    bool resetCrossed = _states.Values.Any(state => state.Fetch is { } prior
                        && AdaptiveRefreshPolicy.HasCrossedReset(prior.Usage, prior.FetchedAt, DateTimeOffset.UtcNow));
                    await FetchAndPublishAsync(force: resetCrossed).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Exchange(ref _pollInFlight, 0);
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warning(ex, "Poll tick failed");
            }
        }

        /// <summary>Cheap probe for an actively running Codex CLI; refreshes speed up while it runs.</summary>
        internal static bool IsCodexProcessRunning()
        {
            try
            {
                var processes = System.Diagnostics.Process.GetProcessesByName("codex");
                try
                {
                    return processes.Length > 0;
                }
                finally
                {
                    foreach (var process in processes)
                        process.Dispose();
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The flyout became visible; the poll stays on the fast cadence while it is open.</summary>
        public void NotifyFlyoutOpen()
        {
            _flyoutOpen = true;
            _lastFlyoutOpenAtUtc = DateTimeOffset.UtcNow;
        }

        /// <summary>The flyout hid; the poll may back off on the next re-arm.</summary>
        public void NotifyFlyoutClosed() => _flyoutOpen = false;

/// <summary>Fetches Codex usage and publishes the result. Failures degrade to a cached or failure
        /// <see cref="UsageResult"/> and are logged, never thrown.</summary>
        public async Task FetchAndPublishAsync(bool force)
        {
            // Previous fetch time, captured before this fetch overwrites _lastState: if a reset
            // crossed during this interval but the fresh snapshot still reports the old (past)
            // reset, the server hasn't flipped yet and we retry soon. Reading the fresh snapshot
            // against the previous timestamp is self-limiting — once the previous timestamp moves
            // past the stale reset, the re-arm falls back to the normal reset-aware delay.
            DateTimeOffset? previousFetchedAt = _lastState?.Fetch?.FetchedAt;
            try
            {
                // Auto-send still needs Codex updates even when only Antigravity is displayed.
                var ids = WidgetDisplayProviders.Append(ProviderId.Codex).Distinct();
                await Task.WhenAll(ids.Select(id => FetchProviderAsync(id, force))).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warning(ex, "Codex usage fetch failed");
            }

            // The profile feed updates the heatmap; keep it fresh on the same cadence. Independent of
            // the usage fetch so one failing never blocks the other.
            await FetchProfileAsync().ConfigureAwait(false);

            // Every completed fetch re-arms the next poll at the reset-aware delay, so a manual
            // refresh or a fetch-on-open also re-bases the cadence (CodexBar-style adaptive refresh).
            DateTimeOffset now = DateTimeOffset.UtcNow;
            TimeSpan delay = ComputeNextDelay(now);
            if (previousFetchedAt is { } prev
                && _lastState?.Fetch is { } fresh
                && AdaptiveRefreshPolicy.HasCrossedReset(fresh.Usage, prev, now)
                && delay > AdaptiveRefreshPolicy.ResetCatchUpDelay)
            {
                delay = AdaptiveRefreshPolicy.ResetCatchUpDelay;
            }
            ScheduleNextPoll(delay);
        }

        private async Task FetchProviderAsync(ProviderId id, bool force)
        {
            var result = await _service.FetchAsync(id, force).ConfigureAwait(false);
            _states[id] = result;
            if (id == ProviderId.Codex) _lastState = result;
            StateChanged?.Invoke(result);
        }

        /// <summary>Policy delay shortened to land just after the next quota reset, so a reset
        /// sooner than the adaptive cadence is never missed while the poll loop idles.</summary>
        private TimeSpan ComputeNextDelay(DateTimeOffset now)
        {
            TimeSpan policyDelay = AdaptiveRefreshPolicy.NextDelay(
                _flyoutOpen,
                _lastFlyoutOpenAtUtc,
                now,
                IsCodexProcessRunning());
            if (WidgetDisplayProviders.Contains(ProviderId.Antigravity))
                policyDelay = TimeSpan.FromSeconds(Math.Min(policyDelay.TotalSeconds, 60));
            foreach (var state in _states.Values)
                policyDelay = AdaptiveRefreshPolicy.NextDelayWithReset(policyDelay, state.Fetch?.Usage, now);
            return policyDelay;
        }

        /// <summary>Fetches the Codex profile (lifetime tokens, daily activity buckets) and raises
        /// <see cref="ProfileChanged"/> on success. Errors are logged, never thrown.</summary>
        public async Task FetchProfileAsync()
        {
            try
            {
                if (_service.Get(ProviderId.Codex) is CodexProvider provider)
                {
                    var profile = await provider.FetchProfileAsync().ConfigureAwait(false);
                    _lastProfile = profile;
                    ProfileChanged?.Invoke(profile);
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warning(ex, "Codex profile fetch failed");
            }

            if (WidgetDisplayProviders.Contains(ProviderId.Antigravity))
            {
                try
                {
                    if (_service.Get(ProviderId.Antigravity) is ValthvnQuota.Usage.Providers.AntigravityProvider agProvider)
                    {
                        var agProfile = await agProvider.FetchProfileAsync().ConfigureAwait(false);
                        _lastAntigravityProfile = agProfile;
                        AntigravityProfileChanged?.Invoke(agProfile);
                    }
                }
                catch (Exception ex)
                {
                    Diagnostics.Log.Warning(ex, "Antigravity profile fetch failed");
                }
            }
        }

        /// <summary>Thin alias for <see cref="FetchAndPublishAsync"/>, kept for the taskbar sync path.</summary>
        public Task TickAsync(bool force = false) => FetchAndPublishAsync(force);
    }
}
