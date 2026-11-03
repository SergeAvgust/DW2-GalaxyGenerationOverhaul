using System.Globalization;
using DW2Mod;

namespace SpiralArms;

// Keeps this mod's choices between sessions through DW2 Settings Keeper, if it is installed (its live channels, contract version 1).
// The Keeper holds every remembered value in memory at once and writes its file a moment later, so a choice is safe from the click on.
// Without the Keeper nothing is kept and the options start at their defaults every session, as before.
//
// Nothing is read at start-up: the screen asks for the values every time it is bound (the new-game dialog opens), because the Keeper may not
// have loaded yet at start-up and because the options shown must be the ones kept now.
internal static class KeeperLink
{
    private const int Version = 1;
    private const string RememberChannel = "settings-keeper.remember";
    private const string RecallChannel = "settings-keeper.recall";
    private const string Prefix = "galaxy-generation-overhaul.";
    private static bool _contractReported;

    // Each distinct setting once (the ring shapes and the two pages that show Galaxy Knowledge share theirs).
    private static IEnumerable<UiRow> Settings()
        => UiRows.All.Concat(UiRows.CommonBySection.Values.SelectMany(r => r))
            .GroupBy(r => r.SettingName).Select(g => g.First());

    public static void Install()
    {
        LiveChannel.Expect<Action<string, string>>(RememberChannel, Version);
        LiveChannel.Expect<Func<string, string>>(RecallChannel, Version);
    }

    private static T? Channel<T>(string channel) where T : Delegate
    {
        T? handler = LiveChannel.Get<T>(channel, Version);
        string? provider = handler != null ? null : LiveChannel.ProviderOf(channel);
        if (provider != null && !_contractReported)
        {
            _contractReported = true;
            Bootstrap.Log($"KeeperLink: {provider} provides {channel} but not at the version or type asked for; choices are not kept.");
        }
        return handler;
    }

    public static void Remember(string name, string value)
    {
        try
        {
            Channel<Action<string, string>>(RememberChannel)?.Invoke(Prefix + name, value);
        }
        catch (Exception ex)
        {
            Bootstrap.Log($"KeeperLink: remembering {name} failed: " + ex.Message);
        }
    }

    public static void Remember(UiRow row, int index) => Remember(row.SettingName, index.ToString(CultureInfo.InvariantCulture));

    public static string? Recall(string name)
    {
        try
        {
            return Channel<Func<string, string>>(RecallChannel)?.Invoke(Prefix + name);
        }
        catch (Exception ex)
        {
            Bootstrap.Log($"KeeperLink: recalling {name} failed: " + ex.Message);
            return null;
        }
    }

    // Puts the kept choices into the in-memory options. A value that is not a valid button of the row is ignored (the option list may have changed).
    public static void RestoreAll()
    {
        int restored = 0;
        foreach (UiRow row in Settings())
        {
            if (int.TryParse(Recall(row.SettingName), NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
                && index >= 0 && index < row.Options.Length)
            {
                row.Set(index);
                restored++;
            }
        }
        if (restored > 0)
        {
            Bootstrap.Log($"KeeperLink: restored {restored} option(s) from Settings Keeper.");
        }
    }
}
