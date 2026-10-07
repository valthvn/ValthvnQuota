using System;
using System.Collections.Generic;
using ValthvnQuota.Usage;

namespace ValthvnQuota;

public enum QuotaDisplayMode { Codex, Antigravity, Both }

public static class QuotaProviderSettings
{
    private const string KeyPath = @"Software\ValthvnQuota";
    public static event Action? Changed;

    public static QuotaDisplayMode DisplayMode
    {
        get => Normalize(RegistrySettings.ReadInt(KeyPath, "QuotaDisplayMode", 0));
        set
        {
            RegistrySettings.WriteInt(KeyPath, "QuotaDisplayMode", (int)Normalize((int)value));
            Changed?.Invoke();
        }
    }

    internal static QuotaDisplayMode Normalize(int value)
        => Enum.IsDefined(typeof(QuotaDisplayMode), value) ? (QuotaDisplayMode)value : QuotaDisplayMode.Codex;

    public static IReadOnlyList<ProviderId> ProvidersFor(QuotaDisplayMode mode) => mode switch
    {
        QuotaDisplayMode.Antigravity => new[] { ProviderId.Antigravity },
        QuotaDisplayMode.Both => new[] { ProviderId.Codex, ProviderId.Antigravity },
        _ => new[] { ProviderId.Codex },
    };
}
