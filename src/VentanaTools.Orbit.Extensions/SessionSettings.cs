// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

namespace VentanaTools.Orbit.Extensions;

/// <summary>The companion's check of a <c>startSession</c> (contract §7.6.1 and §7.6.2).</summary>
internal static class SessionSettings
{
    /// <summary>
    /// Null when the companion accepts the session: the contribution is in the manifest and the
    /// settings are its complete set, one declared choice value per declared setting and no other
    /// key. Otherwise <c>session.unknown-contribution</c> or <c>session.settings-invalid</c>; a key is
    /// never dropped or filled in silently.
    /// </summary>
    public static ReasonCode? Check(ExtensionManifest manifest, string contributionId, IReadOnlyDictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(settings);
        var contribution = manifest.Contributions.FirstOrDefault(item => string.Equals(item.Id, contributionId, StringComparison.Ordinal));
        if (contribution is null)
        {
            return ReasonCode.SessionUnknownContribution;
        }

        if (settings.Count != contribution.Settings.Count)
        {
            return ReasonCode.SessionSettingsInvalid;
        }

        foreach (var setting in contribution.Settings)
        {
            if (!settings.TryGetValue(setting.Id, out var value)
                || !setting.Choices.Any(choice => string.Equals(choice.Value, value, StringComparison.Ordinal)))
            {
                return ReasonCode.SessionSettingsInvalid;
            }
        }

        return null;
    }
}
