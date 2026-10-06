using System.Reflection;
using System.Reflection.Emit;
using DistantWorlds.Types;
using DistantWorlds.UI;
using HarmonyLib;

namespace SpiralArms;

// Adds an "Alien Empire Placement" screen to the new-game dialog, after "Other Empires".
//
// The dialog builds a fixed number of section panels (12, indices 0-11) and walks through them in the order of a per-play-mode list of
// section indices (StartNewGameDialog.StepIndices*). The new screen is section 12:
//   - the loop that creates the panels runs to 13 instead of 12 (transpiler); AddControlsToPanel ignores the unknown index, so
//     SpiralUi fills the panel (UiRows.AlienPlacement);
//   - 12 is added to every step list, right after section 7 (Other Empires);
//   - ResolveStepText names it, for the header and the Previous / Next buttons;
//   - StepIndicesMaximum, which the game returns for "past the last step", moves to 13 so that panel 12 is not shown as the
//     dialog closes.
internal static class PlacementStep
{
    public const int SectionIndex = 12;
    private const int OriginalSectionCount = 12;
    private const int ColonizationSection = 3;

    public static void Install(Harmony harmony)
    {
        MethodInfo? setup = AccessTools.Method(typeof(StartNewGameDialog), "SetupUserInterfaceControls");
        MethodInfo? text = AccessTools.Method(typeof(StartNewGameDialog), "ResolveStepText");
        if (setup == null || text == null)
        {
            Bootstrap.Log("PlacementStep: SetupUserInterfaceControls / ResolveStepText not found; the placement screen is disabled.");
            return;
        }
        harmony.Patch(setup, prefix: Own(nameof(SetupPrefix)), transpiler: Own(nameof(SetupTranspiler)));
        harmony.Patch(text, postfix: Own(nameof(TextPostfix)));
        MethodInfo? background = AccessTools.Method(typeof(StartNewGameDialog), "SetupStepBackgroundImages");
        if (background == null)
        {
            Bootstrap.Log("PlacementStep: SetupStepBackgroundImages not found; the placement screen has no background.");
        }
        else
        {
            harmony.Patch(background, postfix: Own(nameof(BackgroundPostfix)));
        }
    }

    private static HarmonyMethod Own(string name) => new(typeof(PlacementStep).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));

    // Runs before the panels are created; harmless to repeat.
    private static void SetupPrefix()
    {
        try
        {
            AddStep(StartNewGameDialog.StepIndicesStandardEmpire);
            AddStep(StartNewGameDialog.StepIndicesPirateFaction);
            AddStep(StartNewGameDialog.StepIndicesLonePirate);
            AddStep(StartNewGameDialog.StepIndicesRefugeeFleet);
            StartNewGameDialog.StepIndicesMaximum = SectionIndex + 1;
        }
        catch (Exception ex)
        {
            Bootstrap.Log("PlacementStep: could not add the step: " + ex);
        }
    }

    private static void AddStep(List<int>? steps)
    {
        if (steps == null || steps.Contains(SectionIndex))
        {
            return;
        }
        int after = steps.IndexOf(7);
        steps.Insert(after >= 0 ? after + 1 : steps.Count, SectionIndex);
    }

    // The panel-creating loop is `for (i = 0; i < count; i++)` with `count` set by `ldc.i4.s 12; stloc.s 16`.
    private static IEnumerable<CodeInstruction> SetupTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        int patched = 0;
        for (int i = 0; i + 1 < code.Count; i++)
        {
            if (code[i].opcode == OpCodes.Ldc_I4_S && Convert.ToInt32(code[i].operand) == OriginalSectionCount
                && code[i + 1].opcode == OpCodes.Stloc_S && code[i + 1].operand is LocalBuilder { LocalIndex: 16 })
            {
                code[i].operand = (sbyte)(OriginalSectionCount + 1);
                patched++;
            }
        }
        Bootstrap.Log(patched == 1
            ? "PlacementStep: the dialog now builds 13 section panels."
            : $"PlacementStep: expected one section-count constant in SetupUserInterfaceControls, found {patched}; the placement screen will not work.");
        return code;
    }

    // The game gives most sections a faint picture behind the rows (same tint and fill as the colonization section).
    private static void BackgroundPostfix(StartNewGameDialog __instance)
    {
        try
        {
            var sections = (DWPanel[]?)AccessTools.Field(typeof(StartNewGameDialog), "_Sections")?.GetValue(__instance);
            if (sections == null || sections.Length <= SectionIndex)
            {
                return;
            }
            DWPanel panel = sections[SectionIndex], source = sections[ColonizationSection];
            panel.BackgroundImage = source.BackgroundImage;
            panel.BackgroundImageTintColor = source.BackgroundImageTintColor;
            panel.BackgroundImageFill = source.BackgroundImageFill;
        }
        catch (Exception ex)
        {
            Bootstrap.Log("PlacementStep: background failed: " + ex.Message);
        }
    }

    private static void TextPostfix(int index, ref string __result)
    {
        if (index == SectionIndex)
        {
            __result = "Alien Empire Placement";
        }
    }
}
