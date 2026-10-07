using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ValthvnQuota.Diagnostics;
using ValthvnQuota.Helpers;


namespace ValthvnQuota.Usage.Providers;

/// <summary>Reads quota from Antigravity's local Connect RPC server. Tokens stay in memory.</summary>
public sealed class AntigravityProvider : IUsageProvider
{
    public ProviderId Id => ProviderId.Antigravity;
    public string DisplayName => "Antigravity";
    public string SessionLabel => "Gemini";
    public string WeeklyLabel => "Claude & GPT";
    public BillingKind Billing => BillingKind.Subscription;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LocalEndpoint? _endpoint;

    // The server can use a self-signed HTTPS certificate. This client is restricted to literal
    // loopback addresses and never follows redirects or uses a system proxy.
    private static readonly HttpClient LocalClient = new(new HttpClientHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = (request, _, _, _) => request.RequestUri?.Host == "127.0.0.1",
    }) { Timeout = TimeSpan.FromSeconds(2) };

    internal sealed record LocalEndpoint(int ProcessId, int Port, string CsrfToken, bool Https);

    public async Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(25));
            try
            {
                if (_endpoint is { } cached)
                {
                    try { return await FetchEndpointAsync(cached, budget.Token).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ProviderException or JsonException)
                    {
                        budget.Token.ThrowIfCancellationRequested();
                        _endpoint = null; // Antigravity restart: rediscover its port and token.
                    }
                }

                var endpoints = await DiscoverAsync(budget.Token).ConfigureAwait(false);
                if (endpoints.Count == 0)
                    throw new ProviderException(ProviderErrorKind.NotRunning, AppStrings.Get("Antigravity.Start"));

                bool invalidPayload = false;
                foreach (var endpoint in endpoints)
                {
                    try
                    {
                        var result = await FetchEndpointAsync(endpoint, budget.Token).ConfigureAwait(false);
                        _endpoint = endpoint;
                        return result;
                    }
                    catch (ProviderException) { invalidPayload = true; }
                    catch (JsonException) { invalidPayload = true; }
                    catch (HttpRequestException) { }
                    catch (OperationCanceledException) { budget.Token.ThrowIfCancellationRequested(); }
                }
                throw new ProviderException(invalidPayload ? ProviderErrorKind.Parse : ProviderErrorKind.Other,
                    AppStrings.Get(invalidPayload ? "Antigravity.NoQuota" : "Antigravity.Unavailable"));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ProviderException(ProviderErrorKind.Timeout, AppStrings.Get("Antigravity.Unavailable"));
            }
        }
        finally { _gate.Release(); }
    }

    private static async Task<ProviderFetchResult> FetchEndpointAsync(LocalEndpoint endpoint, CancellationToken ct)
    {
        // Only a validated numeric port and the fixed loopback host can enter this URI.
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{(endpoint.Https ? "https" : "http")}://127.0.0.1:{endpoint.Port}/exa.language_server_pb.LanguageServerService/GetUserStatus");
        request.Headers.Add("Connect-Protocol-Version", "1");
        request.Headers.Add("x-codeium-csrf-token", endpoint.CsrfToken);
        request.Content = new StringContent("{\"metadata\":{\"ideName\":\"antigravity\",\"extensionName\":\"antigravity\",\"locale\":\"en\"}}", Encoding.UTF8, "application/json");
        using var response = await LocalClient.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return ParseUsage(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    internal static ProviderFetchResult ParseUsage(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var status = root.TryGetProperty("userStatus", out var user) ? user : root;
        if (!status.TryGetProperty("cascadeModelConfigData", out var data)
            || !data.TryGetProperty("clientModelConfigs", out var configs)
            || configs.ValueKind != JsonValueKind.Array)
            throw new ProviderException(ProviderErrorKind.Parse, AppStrings.Get("Antigravity.NoQuota"));

        var windows = new List<NamedRateWindow>();
        foreach (var model in configs.EnumerateArray())
        {
            string? label = StringProperty(model, "label");
            if (string.IsNullOrWhiteSpace(label) || !model.TryGetProperty("quotaInfo", out var quota)
                || quota.ValueKind != JsonValueKind.Object)
                continue;
            // Protobuf JSON omits default numeric values: quotaInfo {} means exhausted (0),
            // whereas an absent quotaInfo means the quota is unknown and must be skipped.
            double remaining = 0;
            if (quota.TryGetProperty("remainingFraction", out var fraction)
                && (fraction.ValueKind != JsonValueKind.Number || !fraction.TryGetDouble(out remaining) || !double.IsFinite(remaining)))
                continue;
            DateTimeOffset? reset = DateTimeOffset.TryParse(StringProperty(quota, "resetTime"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var time) ? time : null;
            var window = new RateWindow((1 - Math.Clamp(remaining, 0, 1)) * 100,
                resetAt: reset, resetDescription: reset is null ? null : CountdownFormat.Format(reset), label: label);
            windows.Add(new NamedRateWindow(label, label, window));
        }
        if (windows.Count == 0)
            throw new ProviderException(ProviderErrorKind.Parse, AppStrings.Get("Antigravity.NoQuota"));

        // Shared quota pools:
        // 1. Gemini models share one quota pool.
        // 2. Claude and GPT models share one quota pool.
        var geminiLimiting = windows.Where(w => IsGemini(w.Title)).OrderByDescending(w => w.Window.UsedPercent).FirstOrDefault();
        var claudeGptLimiting = windows.Where(w => IsClaudeOrGpt(w.Title)).OrderByDescending(w => w.Window.UsedPercent).FirstOrDefault();
        var others = windows.Where(w => !IsGemini(w.Title) && !IsClaudeOrGpt(w.Title)).ToList();

        RateWindow primaryWindow;
        RateWindow? secondaryWindow = null;

        if (geminiLimiting != null)
        {
            primaryWindow = new RateWindow(
                geminiLimiting.Window.UsedPercent,
                resetAt: geminiLimiting.Window.ResetAt,
                resetDescription: geminiLimiting.Window.ResetDescription,
                label: "Gemini models");

            if (claudeGptLimiting != null)
            {
                secondaryWindow = new RateWindow(
                    claudeGptLimiting.Window.UsedPercent,
                    resetAt: claudeGptLimiting.Window.ResetAt,
                    resetDescription: claudeGptLimiting.Window.ResetDescription,
                    label: "Claude & GPT");
            }
        }
        else if (claudeGptLimiting != null)
        {
            primaryWindow = new RateWindow(
                claudeGptLimiting.Window.UsedPercent,
                resetAt: claudeGptLimiting.Window.ResetAt,
                resetDescription: claudeGptLimiting.Window.ResetDescription,
                label: "Claude & GPT");
        }
        else
        {
            primaryWindow = windows.OrderByDescending(w => w.Window.UsedPercent).First().Window;
        }

        var usage = new UsageSnapshot(primaryWindow) { Email = StringProperty(status, "email") };
        if (secondaryWindow != null)
            usage.Secondary = secondaryWindow;
        if (status.TryGetProperty("planStatus", out var plan) && plan.TryGetProperty("planInfo", out var info))
            usage.LoginMethod = StringProperty(info, "planName");
        foreach (var other in others)
            usage.ExtraRateWindows.Add(other);
        return new ProviderFetchResult(usage, "Antigravity local");
    }

    private static bool IsGemini(string title)
        => title.Contains("Gemini", StringComparison.OrdinalIgnoreCase);

    private static bool IsClaudeOrGpt(string title)
        => title.Contains("Claude", StringComparison.OrdinalIgnoreCase)
        || title.Contains("GPT", StringComparison.OrdinalIgnoreCase)
        || title.Contains("o1", StringComparison.OrdinalIgnoreCase)
        || title.Contains("o3", StringComparison.OrdinalIgnoreCase)
        || title.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase);

    private static string? StringProperty(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    internal static string? ReadCsrfToken(string commandLine)
    {
        var match = Regex.Match(commandLine, "--csrf_token(?:=|\\s+)(?:\"([^\"]+)\"|([^\\s\"]+))", RegexOptions.CultureInvariant);
        return match.Success ? (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) : null;
    }

    private static async Task<List<LocalEndpoint>> DiscoverAsync(CancellationToken ct)
    {
        // CIM avoids adding a reflection-heavy WMI dependency to the trimmed WinUI app.
        // Return only matching processes; command lines never reach diagnostics or disk.
        const string script = "& { $ErrorActionPreference='Stop'; $servers=@(Get-CimInstance Win32_Process -Filter \"Name LIKE 'language_server%'\" | Where-Object { $_.CommandLine -match 'antigravity' -and $_.CommandLine -match '--csrf_token' }); if ($servers.Count -eq 0) { '[]'; return }; $listeners=@(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue); @($servers | ForEach-Object { $server=$_; $ports=@($listeners | Where-Object { $_.OwningProcess -eq $server.ProcessId -and $_.LocalAddress -in @('127.0.0.1','::1','0.0.0.0','::') } | Select-Object -ExpandProperty LocalPort -Unique); [pscustomobject]@{ ProcessId=$server.ProcessId; CommandLine=$server.CommandLine; Ports=$ports } }) | ConvertTo-Json -Depth 4 -Compress }";
        var start = new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var process = Process.Start(start) ?? throw new ProviderException(ProviderErrorKind.Other, AppStrings.Get("Antigravity.Unavailable"));
        using var registration = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch { } });
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var errors = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        await errors.ConfigureAwait(false); // Drain but never log process data.
        if (process.ExitCode != 0)
            throw new ProviderException(ProviderErrorKind.Other, AppStrings.Get("Antigravity.Unavailable"));
        using var document = JsonDocument.Parse(await output.ConfigureAwait(false));
        var entries = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToArray() : new[] { document.RootElement };
        var result = new List<LocalEndpoint>();
        foreach (var entry in entries)
        {
            var token = ReadCsrfToken(StringProperty(entry, "CommandLine") ?? "");
            if (token is null || !entry.TryGetProperty("Ports", out var ports)) continue;
            int pid = entry.GetProperty("ProcessId").GetInt32();
            foreach (var portValue in ports.EnumerateArray())
            {
                if (!portValue.TryGetInt32(out var port) || port is < 1 or > 65535) continue;
                result.Add(new LocalEndpoint(pid, port, token, false));
                result.Add(new LocalEndpoint(pid, port, token, true));
            }
        }
        return result;
    }

    private static readonly ConcurrentDictionary<string, (DateTime LastModified, long Tokens)> _trajectoryCache = new();
    private CodexProfileSnapshot? _lastProfileSnapshot;

    public async Task<CodexProfileSnapshot> FetchProfileAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(25));
            LocalEndpoint? endpoint = _endpoint;
            if (endpoint is null)
            {
                var endpoints = await DiscoverAsync(budget.Token).ConfigureAwait(false);
                endpoint = endpoints.FirstOrDefault();
                if (endpoint != null) _endpoint = endpoint;
            }

            if (endpoint is null)
                return _lastProfileSnapshot ?? new CodexProfileSnapshot();

            var activeCascades = new Dictionary<string, (string? Title, DateTime LastModified)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var rpcCascades = await FetchAllCascadeTrajectoriesAsync(endpoint, budget.Token).ConfigureAwait(false);
                foreach (var (cid, title, mod) in rpcCascades)
                    activeCascades[cid] = (title, mod);
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warning(ex, "Antigravity GetAllCascadeTrajectories failed");
            }

            var convDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".gemini\antigravity\conversations");
            if (Directory.Exists(convDir))
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(convDir, "*.db"))
                    {
                        var id = Path.GetFileNameWithoutExtension(file);
                        var lastWrite = File.GetLastWriteTimeUtc(file);
                        if (!activeCascades.ContainsKey(id))
                            activeCascades[id] = (null, lastWrite);
                    }
                }
                catch { }
            }

            if (activeCascades.Count == 0)
                return _lastProfileSnapshot ?? new CodexProfileSnapshot();

            var daily = new Dictionary<DateOnly, long>();
            long totalTokens = 0;
            long peakTokens = 0;

            foreach (var (cid, info) in activeCascades)
            {
                long cascadeTokens = 0;
                if (_trajectoryCache.TryGetValue(cid, out var cached) && cached.LastModified == info.LastModified)
                {
                    cascadeTokens = cached.Tokens;
                }
                else
                {
                    try
                    {
                        cascadeTokens = await FetchCascadeTokensAsync(endpoint, cid, budget.Token).ConfigureAwait(false);
                        _trajectoryCache[cid] = (info.LastModified, cascadeTokens);
                    }
                    catch
                    {
                        if (cached.Tokens > 0)
                            cascadeTokens = cached.Tokens;
                    }
                }

                if (cascadeTokens > 0)
                {
                    var day = DateOnly.FromDateTime(info.LastModified.Date);
                    daily[day] = daily.GetValueOrDefault(day) + cascadeTokens;
                    totalTokens += cascadeTokens;
                }
            }

            foreach (var val in daily.Values)
                if (val > peakTokens) peakTokens = val;

            var buckets = daily
                .OrderBy(kv => kv.Key)
                .Select(kv => new ProfileUsageBucket(kv.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), kv.Value))
                .ToList();

            var snapshot = new CodexProfileSnapshot
            {
                LifetimeTokens = totalTokens,
                PeakDailyTokens = peakTokens,
                TotalThreads = activeCascades.Count,
                DailyUsageBuckets = buckets,
                GeneratedAt = DateTimeOffset.UtcNow,
                StatsAsOf = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture),
            };
            _lastProfileSnapshot = snapshot;
            return snapshot;
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Warning(ex, "FetchProfileAsync Antigravity failed");
            return _lastProfileSnapshot ?? new CodexProfileSnapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<List<(string Id, string? Title, DateTime LastModified)>> FetchAllCascadeTrajectoriesAsync(LocalEndpoint endpoint, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{(endpoint.Https ? "https" : "http")}://127.0.0.1:{endpoint.Port}/exa.language_server_pb.LanguageServerService/GetAllCascadeTrajectories");
        request.Headers.Add("Connect-Protocol-Version", "1");
        request.Headers.Add("x-codeium-csrf-token", endpoint.CsrfToken);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await LocalClient.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var list = new List<(string Id, string? Title, DateTime LastModified)>();
        if (doc.RootElement.TryGetProperty("trajectorySummaries", out var summaries))
        {
            if (summaries.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in summaries.EnumerateObject())
                {
                    string id = prop.Name;
                    string? title = null;
                    DateTime modTime = DateTime.UtcNow;
                    if (prop.Value.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String)
                        title = s.GetString();
                    if (prop.Value.TryGetProperty("lastModifiedTime", out var lmt) && lmt.ValueKind == JsonValueKind.String
                        && DateTime.TryParse(lmt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed))
                        modTime = parsed;
                    list.Add((id, title, modTime));
                }
            }
            else if (summaries.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in summaries.EnumerateArray())
                {
                    string? id = null;
                    if (item.TryGetProperty("cascadeId", out var cid) || item.TryGetProperty("trajectoryId", out cid))
                        id = cid.GetString();
                    if (string.IsNullOrEmpty(id)) continue;
                    string? title = item.TryGetProperty("summary", out var s) ? s.GetString() : null;
                    DateTime modTime = DateTime.UtcNow;
                    if (item.TryGetProperty("lastModifiedTime", out var lmt) && lmt.ValueKind == JsonValueKind.String
                        && DateTime.TryParse(lmt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed))
                        modTime = parsed;
                    list.Add((id, title, modTime));
                }
            }
        }
        return list;
    }

    private static async Task<long> FetchCascadeTokensAsync(LocalEndpoint endpoint, string cascadeId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{(endpoint.Https ? "https" : "http")}://127.0.0.1:{endpoint.Port}/exa.language_server_pb.LanguageServerService/GetCascadeTrajectoryGeneratorMetadata");
        request.Headers.Add("Connect-Protocol-Version", "1");
        request.Headers.Add("x-codeium-csrf-token", endpoint.CsrfToken);
        request.Content = new StringContent($"{{\"cascadeId\":\"{cascadeId}\"}}", Encoding.UTF8, "application/json");

        using var response = await LocalClient.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        long total = 0;
        if (doc.RootElement.TryGetProperty("generatorMetadata", out var metaArray)
            && metaArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in metaArray.EnumerateArray())
            {
                if (item.TryGetProperty("chatModel", out var chatModel)
                    && chatModel.TryGetProperty("usage", out var usage))
                {
                    total += ReadTokenValue(usage, "inputTokens");
                    total += ReadTokenValue(usage, "outputTokens");
                }
            }
        }
        return total;
    }

    private static long ReadTokenValue(JsonElement element, string propName)
    {
        if (!element.TryGetProperty(propName, out var prop))
            return 0;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var val))
            return val;
        if (prop.ValueKind == JsonValueKind.String && long.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var strVal))
            return strVal;
        return 0;
    }
}

