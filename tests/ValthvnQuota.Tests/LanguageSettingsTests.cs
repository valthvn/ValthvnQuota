using System;
using Xunit;

namespace ValthvnQuota.Tests;

public class LanguageSettingsTests : IDisposable
{
    private readonly string _originalLanguage;

    public LanguageSettingsTests()
    {
        _originalLanguage = LanguageSettings.CurrentLanguage;
    }

    public void Dispose()
    {
        LanguageSettings.CurrentLanguage = _originalLanguage;
    }

    [Theory]
    [InlineData(null, "en")]
    [InlineData("", "en")]
    [InlineData("   ", "en")]
    [InlineData("en", "en")]
    [InlineData("en-US", "en")]
    [InlineData("fr", "fr")]
    [InlineData("fr-FR", "fr")]
    [InlineData("FR", "fr")]
    [InlineData("de", "en")]
    [InlineData("es", "en")]
    public void Normalize_ReturnsExpectedLanguage(string? input, string expected)
    {
        Assert.Equal(expected, LanguageSettings.Normalize(input));
    }

    [Fact]
    public void CurrentCulture_ReflectsCurrentLanguage()
    {
        LanguageSettings.CurrentLanguage = "en";
        Assert.Equal("en-US", LanguageSettings.CurrentCulture.Name);

        LanguageSettings.CurrentLanguage = "fr";
        Assert.Equal("fr-FR", LanguageSettings.CurrentCulture.Name);
    }

    [Fact]
    public void LanguageChange_RaisesChangedEvent()
    {
        LanguageSettings.CurrentLanguage = "en";
        bool changedFired = false;
        Action handler = () => changedFired = true;

        LanguageSettings.Changed += handler;
        try
        {
            LanguageSettings.CurrentLanguage = "fr";
            Assert.True(changedFired);
        }
        finally
        {
            LanguageSettings.Changed -= handler;
        }
    }

    [Fact]
    public void AppStrings_ResolvesLocalizedStringsAccordingToLanguage()
    {
        LanguageSettings.CurrentLanguage = "en";
        Assert.Equal("Language", AppStrings.Get("Ui.Language"));
        Assert.Equal("Credits", AppStrings.Get("Labels.Credits"));
        Assert.Equal("Both · compact", AppStrings.Get("Ui.BothCompact"));

        LanguageSettings.CurrentLanguage = "fr";
        Assert.Equal("Langue", AppStrings.Get("Ui.Language"));
        Assert.Equal("Crédits", AppStrings.Get("Labels.Credits"));
        Assert.Equal("Les deux · compact", AppStrings.Get("Ui.BothCompact"));
    }
}
