using ValthvnQuota.Controls;
using ValthvnQuota.Usage;
using ValthvnQuota.Usage.Providers;

namespace ValthvnQuota.Tests;

public class AntigravityProviderTests
{
    [Fact]
    public void ParsePoolsModelQuotasIntoGeminiAndClaudeGpt()
    {
        var result = AntigravityProvider.ParseUsage("""
            {"userStatus":{"email":"test@example.com","planStatus":{"planInfo":{"planName":"Pro"}},
            "cascadeModelConfigData":{"clientModelConfigs":[
              {"label":"Gemini Pro","quotaInfo":{"remainingFraction":0.8,"resetTime":"2026-10-06T18:00:00Z"}},
              {"label":"Claude Sonnet","quotaInfo":{"remainingFraction":0.25,"resetTime":"2026-10-06T19:00:00Z"}},
              {"label":"Internal autocomplete"}
            ]}}}
            """);
        Assert.Equal("Gemini models", result.Usage.Primary.Label);
        Assert.Equal(80, result.Usage.Primary.RemainingPercent, 6);
        Assert.Equal(DateTimeOffset.Parse("2026-10-06T18:00:00Z"), result.Usage.Primary.ResetAt);
        Assert.NotNull(result.Usage.Secondary);
        Assert.Equal("Claude & GPT", result.Usage.Secondary.Label);
        Assert.Equal(25, result.Usage.Secondary.RemainingPercent, 6);
        Assert.Equal(DateTimeOffset.Parse("2026-10-06T19:00:00Z"), result.Usage.Secondary.ResetAt);
        Assert.Equal("Pro", result.Usage.LoginMethod);
        Assert.Equal("test@example.com", result.Usage.Email);
        Assert.Empty(result.Usage.ExtraRateWindows);
        var provider = new AntigravityProvider();
        var state = UsageResult.Success(ProviderId.Antigravity, provider, result);
        Assert.Equal(new[] { "Gemini", "Claude & GPT" }, WidgetSummary.BuildRowLabelsForTesting(state, result.Usage));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"remainingFraction\":0}")]
    public void OmittedProtobufZeroStillMeansExhausted(string quota)
    {
        var result = AntigravityProvider.ParseUsage("{\"cascadeModelConfigData\":{\"clientModelConfigs\":[{\"label\":\"Gemini\",\"quotaInfo\":" + quota + "}]}}");
        Assert.Equal(0, result.Usage.Primary.RemainingPercent);
    }

    [Theory]
    [InlineData(-0.5, 0)]
    [InlineData(1.5, 100)]
    public void OutOfRangeFractionsAreClamped(double fraction, double expectedRemaining)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { cascadeModelConfigData = new { clientModelConfigs = new[] { new { label = "Gemini", quotaInfo = new { remainingFraction = fraction } } } } });
        Assert.Equal(expectedRemaining, AntigravityProvider.ParseUsage(json).Usage.Primary.RemainingPercent);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"userStatus\":{\"cascadeModelConfigData\":{\"clientModelConfigs\":[]}}}")]
    [InlineData("{\"cascadeModelConfigData\":{\"clientModelConfigs\":[{\"label\":\"Gemini\"}]}}")]
    [InlineData("{\"cascadeModelConfigData\":{\"clientModelConfigs\":[{\"label\":\"Gemini\",\"quotaInfo\":null}]}}")]
    [InlineData("{\"cascadeModelConfigData\":{\"clientModelConfigs\":[{\"label\":\"Gemini\",\"quotaInfo\":{\"remainingFraction\":\"unknown\"}}]}}")]
    public void UnknownQuotasDoNotFabricateRemainingUsage(string json)
        => Assert.Equal(ProviderErrorKind.Parse, Assert.Throws<ProviderException>(() => AntigravityProvider.ParseUsage(json)).Kind);

    [Theory]
    [InlineData("language_server_windows_x64.exe --csrf_token token-123 --app_data_dir antigravity", "token-123")]
    [InlineData("server --csrf_token=token-123", "token-123")]
    [InlineData("server --csrf_token \"token-123\"", "token-123")]
    [InlineData("server --other_token hidden", null)]
    public void CsrfDiscoveryHandlesSupportedFlagForms(string command, string? expected)
        => Assert.Equal(expected, AntigravityProvider.ReadCsrfToken(command));
}

public class QuotaProviderSettingsTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void InvalidSavedModeKeepsOriginalCodexDefault(int mode)
        => Assert.Equal(QuotaDisplayMode.Codex, QuotaProviderSettings.Normalize(mode));

    [Fact]
    public void ModesChooseTheRequestedProviders()
    {
        Assert.Equal(new[] { ProviderId.Codex }, QuotaProviderSettings.ProvidersFor(QuotaDisplayMode.Codex));
        Assert.Equal(new[] { ProviderId.Antigravity }, QuotaProviderSettings.ProvidersFor(QuotaDisplayMode.Antigravity));
        Assert.Equal(new[] { ProviderId.Codex, ProviderId.Antigravity }, QuotaProviderSettings.ProvidersFor(QuotaDisplayMode.Both));
    }
}
