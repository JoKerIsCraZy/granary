using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels.Admin;

/// <summary>
/// Where an account's field takes its value while it does not override it, as the web's <c>PolicyDefaultSource</c>
/// (<c>components/UserPolicyFields.tsx</c>): a grouped account's group, an admin's built-in defaults, or the server's for an
/// account without a group.
/// </summary>
public enum AdminPolicySource
{
    Group,
    Admin,
    Server,
}

/// <summary>The texts each <see cref="AdminPolicySource"/> names its default with, as the web's <c>DEFAULT_SOURCE_TEXT</c>.</summary>
internal static class AdminPolicyText
{
    public static string Hint(AdminPolicySource source, string? effective) => effective is null
        ? AppText.Get(Key(source, "groupUnknown", "adminDefault", "serverDefault"))
        : AppText.Get(Key(source, "groupValue", "adminValue", "serverValue"), ("value", effective));

    public static string AllLibraries(AdminPolicySource source) => AppText.Get(Key(source, "groupAllLibraries", "adminDefault", "serverDefault"));

    public static string LimitHelp(AdminPolicySource source, int min) => min > 0
        ? AppText.Get(Key(source, "groupLimitHelpMin", "adminLimitHelpMin", "serverLimitHelpMin"), ("min", min))
        : AppText.Get(Key(source, "groupLimitHelp", "adminLimitHelp", "serverLimitHelp"));

    public static string Quality(AdminPolicySource source) => AppText.Get(Key(source, "groupQuality", "adminQuality", "serverQuality"));

    private static string Key(AdminPolicySource source, string group, string admin, string server) => "web.profiles.userPolicy." + source switch
    {
        AdminPolicySource.Admin => admin,
        AdminPolicySource.Server => server,
        _ => group,
    };
}

/// <summary>One choice of a policy menu whose text follows the field's default (the first, "Inherited: Allowed").</summary>
public sealed partial class AdminPolicyOption(string text) : ObservableObject
{
    [ObservableProperty]
    public partial string Text { get; set; } = text;
}

/// <summary>
/// A yes-or-no policy as the web's <c>BooleanPolicyRow</c>: a card with its name, an optional line, and a menu of the
/// default ("Inherited: Allowed"), Allowed and Not allowed. The default sends nothing.
/// </summary>
public sealed partial class AdminPolicySwitchField : ObservableObject
{
    public AdminPolicySwitchField(string labelKey, string? descriptionKey = null)
    {
        Label = AppText.Get(labelKey);
        Description = descriptionKey is null ? "" : AppText.Get(descriptionKey);
        Options = [new(AdminPolicyText.Hint(AdminPolicySource.Group, null)), new(AppText.Get("web.profiles.userPolicy.allowed")),
            new(AppText.Get("web.profiles.userPolicy.notAllowed"))];
    }

    public string Label { get; }

    public string Description { get; }

    public bool HasDescription => Description.Length > 0;

    public IReadOnlyList<AdminPolicyOption> Options { get; }

    /// <summary>0 the default, 1 Allowed, 2 Not allowed.</summary>
    [ObservableProperty]
    public partial int SelectedIndex { get; set; }

    /// <summary>The override sent: null keeps the default.</summary>
    public bool? Value => SelectedIndex switch
    {
        1 => true,
        2 => false,
        _ => null,
    };

    internal void Hint(AdminPolicySource source, bool? effective) =>
        Options[0].Text = AdminPolicyText.Hint(source, effective is not { } allowed ? null
            : AppText.Get(allowed ? "web.profiles.userPolicy.allowed" : "web.profiles.userPolicy.notAllowed"));

    internal void Reset() => SelectedIndex = 0;

    // A menu clears its choice when its items change; the field keeps its own.
    partial void OnSelectedIndexChanged(int oldValue, int newValue)
    {
        if (newValue < 0)
        {
            SelectedIndex = oldValue;
        }
    }
}

/// <summary>
/// A whole-number limit as the web's <c>LimitPolicyField</c>: Override off shows the default in a dashed box; on, a box
/// seeded with the value the field resolves to. Its text stays as typed, so a cleared or half-typed box is an unsaved edit
/// that holds the dialog back, not 0 (which is unlimited) or the last valid number.
/// </summary>
public sealed partial class AdminPolicyLimitField(string labelKey, int min = 0) : ObservableObject
{
    private AdminPolicySource _source;
    private int? _effective;

    public string Label { get; } = AppText.Get(labelKey);

    /// <summary>The lowest value; with 0 allowed it means unlimited.</summary>
    public int Min { get; } = min;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Value), nameof(IsInvalid), nameof(IsInherited))]
    public partial bool IsOverridden { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Value), nameof(IsInvalid), nameof(Help), nameof(HasHelp))]
    public partial string Draft { get; set; } = "";

    public bool IsInherited => !IsOverridden;

    /// <summary>The override sent: null while not overridden.</summary>
    public int? Value => IsOverridden ? Parse(Draft) : null;

    /// <summary>Overridden without a valid number: the dialog is not saved.</summary>
    public bool IsInvalid => IsOverridden && Parse(Draft) is null;

    /// <summary>The dashed box: "Inherited: Unlimited".</summary>
    public string DefaultText => AdminPolicyText.Hint(_source, _effective is not { } value ? null
        : value == 0 ? AppText.Get("web.profiles.userPolicy.unlimited") : value.ToString(CultureInfo.CurrentCulture));

    /// <summary>Under the box: how to fix it while it holds no valid number, else "0 = unlimited" where 0 is allowed.</summary>
    public string Help => Parse(Draft) is null ? AdminPolicyText.LimitHelp(_source, Min) : Min == 0 ? AppText.Get("web.profiles.userPolicy.zeroUnlimited") : "";

    public bool HasHelp => Help.Length > 0;

    internal void Hint(AdminPolicySource source, int? effective)
    {
        (_source, _effective) = (source, effective);
        OnPropertyChanged(nameof(DefaultText));
        OnPropertyChanged(nameof(Help));
        OnPropertyChanged(nameof(HasHelp));
    }

    internal void Reset()
    {
        IsOverridden = false;
        Draft = "";
    }

    // On, the box starts from what the field resolves to; with that unknown it starts empty, and saving waits for a number.
    partial void OnIsOverriddenChanged(bool value) =>
        Draft = value && _effective is { } effective ? effective.ToString(CultureInfo.InvariantCulture) : "";

    private int? Parse(string text) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed >= Min ? parsed : null;
}

/// <summary>
/// A per-stream bitrate cap as the web's <c>StreamBitratePolicyField</c> and <c>StreamBitrateLimitInput</c>: Override off
/// shows the default; on, a menu of Unlimited, the presets and Custom, which takes Mbps in a box beside it. Stored in kbps.
/// </summary>
public sealed partial class AdminPolicyBitrateField : ObservableObject
{
    /// <summary>The web's <c>STREAM_BITRATE_LIMIT_PRESETS_KBPS</c>, after Unlimited (0).</summary>
    private static readonly int[] Presets = [0, 40000, 20000, 10000, 8000, 5000, 3000, 2000, 1000];

    /// <summary>Below this a cap forces very low quality, so the field warns.</summary>
    private const int LowKbps = 1000;

    private AdminPolicySource _source;
    private int? _effective;

    public AdminPolicyBitrateField(string labelKey)
    {
        Label = AppText.Get(labelKey);
        Options = [.. Presets.Select(Format), AppText.Get("web.player.bitrateLimit.custom")];
    }

    public string Label { get; }

    /// <summary>Unlimited, the presets, then Custom.</summary>
    public IReadOnlyList<string> Options { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInherited), nameof(Value), nameof(IsInvalid), nameof(InvalidText), nameof(LowWarning))]
    public partial bool IsOverridden { get; set; }

    /// <summary>-1 while nothing is picked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustom), nameof(Value), nameof(IsInvalid), nameof(InvalidText), nameof(LowWarning))]
    public partial int SelectedIndex { get; set; } = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Value), nameof(IsInvalid), nameof(InvalidText), nameof(LowWarning))]
    public partial string CustomDraft { get; set; } = "";

    public bool IsInherited => !IsOverridden;

    public bool IsCustom => SelectedIndex == Presets.Length;

    /// <summary>The override sent, in kbps: null while not overridden.</summary>
    public int? Value => !IsOverridden || SelectedIndex < 0 ? null : IsCustom ? ParseMbps(CustomDraft) : Presets[SelectedIndex];

    public bool IsInvalid => IsOverridden && Value is null;

    /// <summary>A Custom box without a number the server can store.</summary>
    public string InvalidText => IsOverridden && IsCustom && ParseMbps(CustomDraft) is null
        ? AppText.Get("web.player.bitrateLimit.invalid", ("places", 3), ("min", 0))
        : "";

    public string LowWarning => IsOverridden && IsCustom && ParseMbps(CustomDraft) is > 0 and < LowKbps
        ? AppText.Get("web.player.bitrateLimit.lowWarning", ("mbps", LowKbps / 1000))
        : "";

    public string DefaultText => AdminPolicyText.Hint(_source, _effective is { } value ? Format(value) : null);

    public string CustomName => AppText.Get("web.player.bitrateLimit.customAria", ("label", Label));

    internal void Hint(AdminPolicySource source, int? effective)
    {
        (_source, _effective) = (source, effective);
        OnPropertyChanged(nameof(DefaultText));
    }

    internal void Reset()
    {
        IsOverridden = false;
        SelectedIndex = -1;
        CustomDraft = "";
    }

    /// <summary>The web's <c>formatStreamBitrateLimit</c>: "Unlimited" or "8 Mbps".</summary>
    public static string Format(int kbps) => kbps == 0
        ? AppText.Get("web.player.bitrateLimit.unlimited")
        : AppText.Get("web.player.bitrateLimit.value", ("mbps", NumberText.Decimal(kbps / 1000.0, 3, 0)));

    /// <summary>The web's <c>parseStreamBitrateMbps</c>: a positive Mbps number with up to three decimals, "." or ",".</summary>
    public static int? ParseMbps(string text)
    {
        string trimmed = text.Trim().Replace(',', '.');
        if (!System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"\A(?:[0-9]+(?:\.[0-9]{0,3})?|\.[0-9]{1,3})\z"))
        {
            return null;
        }
        double kbps = Math.Round(double.Parse(trimmed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture) * 1000);
        return kbps is > 0 and <= int.MaxValue ? (int)kbps : null;
    }

    // On, the menu starts on the cap the field resolves to; with that unknown nothing is picked, and saving waits for one.
    partial void OnIsOverriddenChanged(bool value)
    {
        CustomDraft = "";
        if (!value || _effective is not { } effective)
        {
            SelectedIndex = -1;
            return;
        }
        int preset = Array.IndexOf(Presets, effective);
        SelectedIndex = preset >= 0 ? preset : Presets.Length;
        if (preset < 0)
        {
            CustomDraft = Mbps(effective);
        }
    }

    // Custom starts from the current cap, so picking it alone changes nothing.
    partial void OnSelectedIndexChanged(int oldValue, int newValue)
    {
        if (newValue == Presets.Length && oldValue >= 1 && oldValue < Presets.Length)
        {
            CustomDraft = Mbps(Presets[oldValue]);
        }
    }

    private static string Mbps(int kbps) => (kbps / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
}
