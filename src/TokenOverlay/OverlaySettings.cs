using System.Text;
using System.Text.Json;

namespace TokenOverlay;

internal enum AnchorMode
{
    Auto = 0,
    InsideTopRight = 1,
    InsideBottomRight = 2,
    TitleBarTopRight = 3
}

[Flags]
internal enum DisplayField
{
    None = 0,
    Total = 1 << 0,
    Input = 1 << 1,
    Output = 1 << 2,
    CacheHit = 1 << 3,
    CacheMiss = 1 << 4,
    Context = 1 << 5,
    ContextPercent = 1 << 6,
    Reasoning = 1 << 7,
    Thread = 1 << 8,
    CacheHitRate = 1 << 9
}

internal enum CollapsedSlot { Primary, Secondary }

internal static class DisplayFieldRules
{
    public const DisplayField SupportedMask =
        DisplayField.Total | DisplayField.Input | DisplayField.Output |
        DisplayField.CacheHit | DisplayField.CacheMiss | DisplayField.Context |
        DisplayField.ContextPercent | DisplayField.Reasoning | DisplayField.Thread |
        DisplayField.CacheHitRate;

    public static readonly IReadOnlyList<DisplayField> Ordered = new[]
    {
        DisplayField.Total, DisplayField.Input, DisplayField.Output,
        DisplayField.CacheHit, DisplayField.CacheHitRate, DisplayField.CacheMiss, DisplayField.Context,
        DisplayField.ContextPercent, DisplayField.Reasoning, DisplayField.Thread
    };

    public static bool IsSingleSupported(DisplayField field)
    {
        var value = (int)field;
        return value > 0 &&
            (value & (value - 1)) == 0 &&
            (field & SupportedMask) == field;
    }

    public static DisplayField SanitizeVisible(DisplayField fields)
    {
        var sanitized = fields & SupportedMask;
        return sanitized == DisplayField.None ? DisplayField.Total : sanitized;
    }
}

internal sealed record OverlaySettingsLoadResult(
    OverlaySettings Settings,
    bool MustPersist);

internal sealed class OverlaySettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly string DefaultSettingsPath = Path.Combine(AppPaths.DataFolder, "settings.json");

    private const DisplayField DefaultVisibleFields =
        DisplayField.Total
        | DisplayField.Input
        | DisplayField.Output
        | DisplayField.CacheHit
        | DisplayField.CacheHitRate
        | DisplayField.CacheMiss
        | DisplayField.Context
        | DisplayField.ContextPercent;

    private sealed class PersistedSettings
    {
        public int? SettingsVersion { get; set; }
        // Still written for older versions (Claude's choice); read when the per-app ones are missing.
        public int? AnchorMode { get; set; }
        public int? ClaudeAnchorMode { get; set; }
        public int? CodexAnchorMode { get; set; }
        public bool? ContextRingEnabled { get; set; }
        public bool? CpuMeterEnabled { get; set; }
        public bool? CacheHitLineEnabled { get; set; }
        public bool? ProviderLineEnabled { get; set; }
        public bool? CacheWarningEnabled { get; set; }
        public int? Theme { get; set; }
        public int? VisibleFields { get; set; }
        public int? CollapsedPrimaryField { get; set; }
        public int? CollapsedSecondaryField { get; set; }
        public bool? ManualPlacementEnabled { get; set; }
        public PersistedWindowAttachment? MainAttachment { get; set; }
        public int? OverlayScalePercent { get; set; }
        public bool? DriveShareEnabled { get; set; }
        public string? DriveShareFolder { get; set; }
    }

    private sealed class PersistedWindowAttachment
    {
        public int? ReferencePoint { get; set; }
        public double? OffsetXDip { get; set; }
        public double? OffsetYDip { get; set; }
    }

    public const int CurrentSettingsVersion = 1;
    public int SettingsVersion { get; private set; }
    // Placement, chosen for each app on its own.
    public AnchorMode ClaudeAnchorMode { get; set; }
    public AnchorMode CodexAnchorMode { get; set; }
    // The context-window ring on top of the capsule.
    public bool ContextRingEnabled { get; set; } = true;
    // A CPU meter (like Task Manager's tray icon) in the empty space left of the context ring, which makes the overlay
    // one full rectangle; off keeps the capsule with the ring module standing on its right end.
    public bool CpuMeterEnabled { get; set; }
    // The DeepSeek worker's cache hit line under its rate lines.
    public bool CacheHitLineEnabled { get; set; } = true;
    // The DeepSeek worker's API provider under its name.
    public bool ProviderLineEnabled { get; set; } = true;
    // A tray notice when an uncompacted Claude session with a large context is about to lose its cache (see CacheWatch).
    public bool CacheWarningEnabled { get; set; } = true;
    // Light or dark: as Windows' app mode (the default), or fixed.
    public ThemePreference Theme { get; set; }
    public DisplayField VisibleFields { get; set; }
    public DisplayField CollapsedPrimaryField { get; private set; }
    public DisplayField CollapsedSecondaryField { get; private set; }
    public bool ManualPlacementEnabled { get; set; }
    public WindowAttachment MainAttachment { get; set; } = ManualAttachmentRules.DefaultMainAttachment;
    public int OverlayScalePercent { get; set; }
    // Share busy times with the user's other devices through Google Drive (see DriveShare).
    public bool DriveShareEnabled { get; set; } = true;
    // A folder the user picked to share through instead of the Google Drive root; null = found on its own.
    public string? DriveShareFolder { get; set; }

    public static OverlaySettings CreateDefault()
    {
        return new OverlaySettings
        {
            SettingsVersion = CurrentSettingsVersion,
            ClaudeAnchorMode = AnchorMode.InsideBottomRight,
            CodexAnchorMode = AnchorMode.InsideBottomRight,
            VisibleFields = DefaultVisibleFields,
            CollapsedPrimaryField = DisplayField.Total,
            CollapsedSecondaryField = DisplayField.ContextPercent,
            ManualPlacementEnabled = false,
            MainAttachment = ManualAttachmentRules.DefaultMainAttachment,
            OverlayScalePercent = ManualAttachmentRules.DefaultScalePercent
        };
    }

    public static OverlaySettings Load(string? settingsPath = null)
    {
        var path = settingsPath ?? DefaultSettingsPath;
        var result = LoadFromFile(path);
        if (result.MustPersist)
        {
            result.Settings.Save(path);
        }

        return result.Settings;
    }

    internal static OverlaySettingsLoadResult LoadFromFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new OverlaySettingsLoadResult(CreateDefault(), false);
            }

            return ParseJson(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new OverlaySettingsLoadResult(CreateDefault(), false);
        }
    }

    internal static OverlaySettingsLoadResult ParseJson(string json)
    {
        try
        {
            var persisted = JsonSerializer.Deserialize<PersistedSettings>(json, JsonOptions);
            if (persisted is null)
            {
                return new OverlaySettingsLoadResult(CreateDefault(), false);
            }

            if (!persisted.SettingsVersion.HasValue)
            {
                var migrated = CreateDefault();
                migrated.VisibleFields = DisplayFieldRules.SanitizeVisible(
                    (DisplayField)(persisted.VisibleFields ?? (int)DefaultVisibleFields));
                migrated.ManualPlacementEnabled = persisted.ManualPlacementEnabled ?? false;
                return new OverlaySettingsLoadResult(migrated, true);
            }

            var settings = CreateDefault();
            // Before the per-app choice there was one for both apps.
            settings.ClaudeAnchorMode = SanitizeAnchorMode(persisted.ClaudeAnchorMode ?? persisted.AnchorMode);
            settings.CodexAnchorMode = SanitizeAnchorMode(persisted.CodexAnchorMode ?? persisted.AnchorMode);
            settings.ContextRingEnabled = persisted.ContextRingEnabled ?? true;
            settings.CpuMeterEnabled = persisted.CpuMeterEnabled ?? false;
            settings.CacheHitLineEnabled = persisted.CacheHitLineEnabled ?? true;
            settings.ProviderLineEnabled = persisted.ProviderLineEnabled ?? true;
            settings.CacheWarningEnabled = persisted.CacheWarningEnabled ?? true;
            settings.Theme = persisted.Theme is int theme && Enum.IsDefined((ThemePreference)theme) ? (ThemePreference)theme : ThemePreference.Auto;
            settings.VisibleFields = DisplayFieldRules.SanitizeVisible(
                (DisplayField)(persisted.VisibleFields ?? (int)DefaultVisibleFields));
            settings.SetCollapsedFields(
                (DisplayField)(persisted.CollapsedPrimaryField ?? (int)DisplayField.Total),
                (DisplayField)(persisted.CollapsedSecondaryField ?? (int)DisplayField.ContextPercent));
            settings.ManualPlacementEnabled = persisted.ManualPlacementEnabled ?? false;
            settings.MainAttachment = ManualAttachmentRules.SanitizeMain(
                DeserializeAttachment(persisted.MainAttachment));

            settings.OverlayScalePercent = ManualAttachmentRules.SanitizeScale(
                persisted.OverlayScalePercent);
            settings.DriveShareEnabled = persisted.DriveShareEnabled ?? true;
            settings.DriveShareFolder = string.IsNullOrWhiteSpace(persisted.DriveShareFolder) ? null : persisted.DriveShareFolder;
            return new OverlaySettingsLoadResult(settings, false);
        }
        catch (JsonException)
        {
            return new OverlaySettingsLoadResult(CreateDefault(), false);
        }
    }

    internal string Serialize()
    {
        var persisted = new PersistedSettings
        {
            SettingsVersion = CurrentSettingsVersion,
            AnchorMode = (int)SanitizeAnchorMode((int)ClaudeAnchorMode),
            ClaudeAnchorMode = (int)SanitizeAnchorMode((int)ClaudeAnchorMode),
            CodexAnchorMode = (int)SanitizeAnchorMode((int)CodexAnchorMode),
            ContextRingEnabled = ContextRingEnabled,
            CpuMeterEnabled = CpuMeterEnabled,
            CacheHitLineEnabled = CacheHitLineEnabled,
            ProviderLineEnabled = ProviderLineEnabled,
            CacheWarningEnabled = CacheWarningEnabled,
            Theme = (int)Theme,
            VisibleFields = (int)DisplayFieldRules.SanitizeVisible(VisibleFields),
            CollapsedPrimaryField = (int)CollapsedPrimaryField,
            CollapsedSecondaryField = (int)CollapsedSecondaryField,
            ManualPlacementEnabled = ManualPlacementEnabled,
            MainAttachment = SerializeAttachment(ManualAttachmentRules.SanitizeMain(MainAttachment)),
            OverlayScalePercent = ManualAttachmentRules.SanitizeScale(OverlayScalePercent),
            DriveShareEnabled = DriveShareEnabled,
            DriveShareFolder = DriveShareFolder
        };
        return JsonSerializer.Serialize(persisted, JsonOptions);
    }

    public bool TrySave(string? settingsPath = null)
    {
        var path = settingsPath ?? DefaultSettingsPath;
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Whole or not at all: a half-written file would bring back every default on the next start.
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, Serialize(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Save(string? settingsPath = null) => TrySave(settingsPath);

    public AnchorMode AnchorFor(HostApp app) => app == HostApp.Codex ? CodexAnchorMode : ClaudeAnchorMode;

    public void SetAnchor(HostApp app, AnchorMode mode)
    {
        if (app == HostApp.Codex) CodexAnchorMode = mode;
        else ClaudeAnchorMode = mode;
    }

    public bool SelectCollapsedField(CollapsedSlot slot, DisplayField field)
    {
        if (!DisplayFieldRules.IsSingleSupported(field))
        {
            return false;
        }

        switch (slot)
        {
            case CollapsedSlot.Primary:
                if (CollapsedPrimaryField == field)
                {
                    return false;
                }

                if (CollapsedSecondaryField == field)
                {
                    (CollapsedPrimaryField, CollapsedSecondaryField) =
                        (CollapsedSecondaryField, CollapsedPrimaryField);
                    return true;
                }

                CollapsedPrimaryField = field;
                return true;

            case CollapsedSlot.Secondary:
                if (CollapsedSecondaryField == field)
                {
                    return false;
                }

                if (CollapsedPrimaryField == field)
                {
                    (CollapsedPrimaryField, CollapsedSecondaryField) =
                        (CollapsedSecondaryField, CollapsedPrimaryField);
                    return true;
                }

                CollapsedSecondaryField = field;
                return true;

            default:
                return false;
        }
    }

    public static string? ResolveSettingsOverride(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (!args[index].Equals("--settings", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]))
            {
                throw new ArgumentException("--settings must be followed by an absolute settings file path.", nameof(args));
            }

            var candidate = args[index + 1];
            if (!Path.IsPathFullyQualified(candidate))
            {
                throw new ArgumentException("--settings only takes an absolute settings file path.", nameof(args));
            }

            return Path.GetFullPath(candidate);
        }

        return null;
    }

    private static AnchorMode SanitizeAnchorMode(int? value)
    {
        return value switch
        {
            null => AnchorMode.InsideBottomRight,
            (int)AnchorMode.Auto => AnchorMode.Auto,
            (int)AnchorMode.InsideTopRight => AnchorMode.InsideTopRight,
            (int)AnchorMode.InsideBottomRight => AnchorMode.InsideBottomRight,
            (int)AnchorMode.TitleBarTopRight => AnchorMode.TitleBarTopRight,
            _ => AnchorMode.TitleBarTopRight
        };
    }

    private static WindowAttachment? DeserializeAttachment(PersistedWindowAttachment? value)
    {
        if (value?.ReferencePoint is not int referencePoint
            || value.OffsetXDip is not double offsetXDip
            || value.OffsetYDip is not double offsetYDip)
        {
            return null;
        }

        return ManualAttachmentRules.TrySanitize(
            new WindowAttachment((AttachmentReferencePoint)referencePoint, offsetXDip, offsetYDip),
            out var attachment)
            ? attachment
            : null;
    }

    private static PersistedWindowAttachment? SerializeAttachment(WindowAttachment? value)
    {
        return value is null
            ? null
            : new PersistedWindowAttachment
            {
                ReferencePoint = (int)value.ReferencePoint,
                OffsetXDip = value.OffsetXDip,
                OffsetYDip = value.OffsetYDip
            };
    }

    private void SetCollapsedFields(DisplayField primary, DisplayField secondary)
    {
        CollapsedPrimaryField = DisplayFieldRules.IsSingleSupported(primary)
            ? primary
            : DisplayField.Total;
        CollapsedSecondaryField = DisplayFieldRules.IsSingleSupported(secondary)
            ? secondary
            : DisplayField.ContextPercent;

        if (CollapsedPrimaryField == CollapsedSecondaryField)
        {
            CollapsedSecondaryField = CollapsedPrimaryField == DisplayField.ContextPercent
                ? DisplayField.Total
                : DisplayField.ContextPercent;
        }
    }
}

internal sealed class SettingsProbeRequest
{
    public List<SettingsProbeCase> Cases { get; set; } = new();
}

internal sealed class SettingsProbeCase
{
    public string Name { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string? Json { get; set; }
    public string? Slot { get; set; }
    public int? Field { get; set; }
    public string? SettingsPath { get; set; }
}

internal sealed record SettingsProbeCaseResult(
    string Name,
    OverlaySettings Settings,
    bool MustPersist);

internal sealed record SettingsProbeResult(IReadOnlyList<SettingsProbeCaseResult> Cases);

internal static class SettingsProbe
{
    public static SettingsProbeResult Execute(SettingsProbeRequest request)
    {
        var results = new List<SettingsProbeCaseResult>();
        foreach (var probeCase in request.Cases)
        {
            var result = probeCase.Operation switch
            {
                "Parse" => OverlaySettings.ParseJson(RequireJson(probeCase)),
                "Select" => Select(probeCase),
                "Load" => Load(probeCase),
                "SaveReload" => SaveReload(probeCase),
                _ => throw new ArgumentException($"Unsupported settings probe operation: {probeCase.Operation}")
            };
            results.Add(new SettingsProbeCaseResult(probeCase.Name, result.Settings, result.MustPersist));
        }

        return new SettingsProbeResult(results);
    }

    private static OverlaySettingsLoadResult Select(SettingsProbeCase probeCase)
    {
        var result = OverlaySettings.ParseJson(RequireJson(probeCase));
        if (!Enum.TryParse<CollapsedSlot>(probeCase.Slot, ignoreCase: true, out var slot)
            || !probeCase.Field.HasValue)
        {
            throw new ArgumentException("A Select settings probe needs a valid Slot and Field.", nameof(probeCase));
        }

        result.Settings.SelectCollapsedField(slot, (DisplayField)probeCase.Field.Value);
        return result;
    }

    private static OverlaySettingsLoadResult Load(SettingsProbeCase probeCase)
    {
        var path = RequireTemporaryPath(probeCase);
        if (probeCase.Json is not null)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, probeCase.Json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        var result = OverlaySettings.LoadFromFile(path);
        if (result.MustPersist)
        {
            result.Settings.Save(path);
        }

        return result;
    }

    private static OverlaySettingsLoadResult SaveReload(SettingsProbeCase probeCase)
    {
        var path = RequireTemporaryPath(probeCase);
        var parsed = OverlaySettings.ParseJson(RequireJson(probeCase));
        parsed.Settings.Save(path);
        return new OverlaySettingsLoadResult(OverlaySettings.Load(path), false);
    }

    private static string RequireJson(SettingsProbeCase probeCase)
    {
        return probeCase.Json ?? throw new ArgumentException(
            "This settings probe operation needs Json.",
            nameof(probeCase));
    }

    private static string RequireTemporaryPath(SettingsProbeCase probeCase)
    {
        if (string.IsNullOrWhiteSpace(probeCase.SettingsPath)
            || !Path.IsPathFullyQualified(probeCase.SettingsPath))
        {
            throw new ArgumentException(
                "A settings probe needs an absolute temporary settings path.",
                nameof(probeCase));
        }

        var path = Path.GetFullPath(probeCase.SettingsPath);
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        if (!path.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "A settings probe may only use a temporary settings path.",
                nameof(probeCase));
        }

        return path;
    }
}
