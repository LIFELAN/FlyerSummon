using BepInEx.Configuration;

namespace FlyerSummon;

/// <summary>
/// Tag object passed to <see cref="ConfigDescription"/> so that BepInEx.ConfigurationManager
/// renders a setting with a custom editor. ConfigurationManager finds this type by its simple
/// name ("ConfigurationManagerAttributes") and copies fields by name via reflection, so the
/// field names/types below must match exactly what it expects.
/// </summary>
internal sealed class ConfigurationManagerAttributes
{
    /// <summary>
    /// Custom editor that lets the user click the setting and then press a key to rebind it.
    /// </summary>
    public CustomHotkeyDrawerFunc? CustomHotkeyDrawer;

    /// <summary>
    /// When a setting uses an <c>AcceptableValueRange</c>, ConfigurationManager shows the slider
    /// label as a percentage by default. Turn that off so counts/distances show their real value.
    /// </summary>
    public bool? ShowRangeAsPercent;

    public delegate void CustomHotkeyDrawerFunc(ConfigEntryBase setting, ref bool isCurrentlyAcceptingInput);
}
