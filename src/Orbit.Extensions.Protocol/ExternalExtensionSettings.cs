// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Seth Cottle

using System.Collections.ObjectModel;

namespace Orbit.Extensions.Protocol;

public sealed record ExternalExtensionChoice(string Value, string Name);
public sealed record ExternalExtensionSetting(string Id, string Name, string DefaultValue,
    IReadOnlyList<ExternalExtensionChoice> Choices);

/// <summary>Only declared bounded choices cross to a companion.</summary>
public static class ExternalExtensionSettings
{
    public const int MaxSettings = 16;
    public const int MaxChoiceLength = 64;

    public static IReadOnlyDictionary<string, string>? Resolve(ExternalExtensionAction action,
        IReadOnlyDictionary<string, string>? settings)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.Settings.Count > MaxSettings || settings?.Count > MaxSettings) return null;
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var rule in action.Settings)
        {
            if (!ExtensionText.IsSettingKey(rule.Id) || result.ContainsKey(rule.Id)
                || rule.Choices.Count is < 2 or > 32) return null;
            var value = settings is not null && settings.TryGetValue(rule.Id, out var chosen) ? chosen : rule.DefaultValue;
            if (value is not { Length: > 0 and <= MaxChoiceLength }
                || !rule.Choices.Any(choice => string.Equals(choice.Value, value, StringComparison.Ordinal))) return null;
            result.Add(rule.Id, value);
        }
        if (settings is not null && settings.Keys.Any(key => !result.ContainsKey(key))) return null;
        return new ReadOnlyDictionary<string, string>(result);
    }
}
