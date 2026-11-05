using System.Reflection;
using DistantWorlds.Types;
using DistantWorlds.UI;
using HarmonyLib;
using Stride.Core.Mathematics;
using Stride.Graphics;

namespace SpiralArms;

// A sixth button on two vanilla rows of the colonization section:
//   - Habitable Planets (the vanilla Colony Prevalence, GameStartSettings.OrbQuality): "Scripted only", stored as 0.1 (see Habitability);
//   - Independent Colonies (GameStartSettings.AlienLifePrevalence): "None", stored as 0 (see Habitability.IndependentsPrefix).
// The dialog's own Resolve* (button index to value) and Set* (value to button index) only know five buttons, so they are patched for the
// sixth one.
internal static class VanillaExtras
{
    private const int ExtraIndex = 0;
    private const float ScriptedOnlyValue = 0.1f;

    public static void Install(Harmony harmony)
    {
        Type dialog = typeof(StartNewGameDialog);
        Patch(harmony, dialog, "ResolveColonyPrevalence", nameof(ResolvePrevalence), nameof(RecordResolved));
        Patch(harmony, dialog, "SetColonyPrevalence", null, nameof(SetPrevalence));
        Patch(harmony, dialog, "ResolveIndependentColonies", nameof(ResolveIndependents), null);
        Patch(harmony, dialog, "SetIndependentColonies", null, nameof(SetIndependents));
    }

    private static void Patch(Harmony harmony, Type type, string method, string? prefix, string? postfix)
    {
        MethodInfo? target = AccessTools.Method(type, method);
        if (target == null)
        {
            Bootstrap.Log($"VanillaExtras: StartNewGameDialog.{method} not found; the extra button is not usable.");
            return;
        }
        HarmonyMethod? Own(string? name) => name == null ? null : new HarmonyMethod(typeof(VanillaExtras).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));
        harmony.Patch(target, prefix: Own(prefix), postfix: Own(postfix));
    }

    // Parameter names must match the original's so Harmony can bind them by name.
    // Button 0 is the extra one; buttons 1..5 are the vanilla 0..4, so the original is handed the index minus one.
    private static bool ResolvePrevalence(ref int buttonIndex, ref float __result)
    {
        if (buttonIndex == ExtraIndex)
        {
            __result = ScriptedOnlyValue;
            return false;
        }
        buttonIndex--;
        return true;
    }

    // What the buttons say now, for Habitability (see Habitability.ButtonValue).
    private static void RecordResolved(float __result)
    {
        Habitability.ButtonValue = __result;
        KeeperLink.Remember("HabitablePlanets", __result.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void SetPrevalence(float value, ref int __result)
    {
        Habitability.ButtonValue = value;
        __result = value < Habitability.ScriptedOnlyBelow ? ExtraIndex : __result + 1;
    }

    // Set when the Independent Colonies row already had a None button by the time this mod looked (see Extend).
    private static volatile bool IndependentsHandledElsewhere;

    private static bool ResolveIndependents(ref int buttonIndex, ref float __result)
    {
        if (IndependentsHandledElsewhere)
        {
            return true;
        }
        if (buttonIndex == ExtraIndex)
        {
            __result = 0f;
            return false;
        }
        buttonIndex--;
        return true;
    }

    private static void SetIndependents(float value, ref int __result)
    {
        if (IndependentsHandledElsewhere)
        {
            return;
        }
        __result = value <= 0f ? ExtraIndex : __result + 1;
    }
    // Called once the dialog has built the colonization section.
    public static void AddButtons(StartNewGameDialog dialog)
    {
        Extend(dialog, "ColonizationColonyPrevalence", "Scripted only");
        Extend(dialog, "ColonizationIndependentColonyAmount", "None");
    }

    private static void Extend(StartNewGameDialog dialog, string field, string label)
    {
        try
        {
            var group = AccessTools.Field(typeof(StartNewGameDialog), field)?.GetValue(dialog) as ToggleButtonGroup;
            if (group == null)
            {
                Bootstrap.Log($"VanillaExtras: {field} not found.");
                return;
            }
            // Another mod (Independence Day) may already have put a None button in front: its own index mapping then stands, and this one
            // neither adds a second button nor shifts the indices again.
            if (group.Controls.OfType<DWButton>().Any(b => b.Name != null && b.Name.EndsWith(" None", StringComparison.Ordinal)))
            {
                if (field == "ColonizationIndependentColonyAmount")
                {
                    IndependentsHandledElsewhere = true;
                }
                Bootstrap.Log($"VanillaExtras: {field} already has a None button from another mod; left as it is.");
                return;
            }
            float buttonWidth = UserInterfaceHelper.CalculateScaledValue(95f);
            float buttonGap = UserInterfaceHelper.CalculateScaledValue(8f);
            // AddButton only appends: take the vanilla buttons out and put them back after the new one.
            var old = new List<(string Name, string Text)>();
            foreach (DWControl child in group.Controls)
            {
                if (child is DWButton button)
                {
                    old.Add((button.Name, button.Text));
                }
            }
            int toggled = group.ToggledButtonIndex;
            Vector2 size = group.Size;
            group.SetSizeAndPosition(new Vector2(size.X + buttonWidth + buttonGap, size.Y), group.Position);
            group.ClearButtons();
            group.AddButton("SpiralArms_" + field + " " + label, label, (Texture)null!);
            foreach ((string name, string text) in old)
            {
                group.AddButton(name, text, (Texture)null!);
            }
            if (toggled >= 0)
            {
                group.ToggleButton(toggled + 1);
            }
            Bootstrap.Log($"VanillaExtras: added the button \"{label}\" to {field}.");
        }
        catch (Exception ex)
        {
            Bootstrap.Log($"VanillaExtras: {field} failed: " + ex.Message);
        }
    }
}
