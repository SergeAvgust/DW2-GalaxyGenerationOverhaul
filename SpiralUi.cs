using System.Reflection;
using DistantWorlds.Types;
using DistantWorlds.UI;
using HarmonyLib;
using Stride.Core.Mathematics;
using Stride.Graphics;

namespace SpiralArms;

// Adds the Spiral options as extra rows at the bottom of the galaxy settings section (section 0) of the
// new-game dialog. Rows are built the way the game builds its own (a ToggleButtonGroup handed to the dialog's
// private AddLabelledControl, which adds the striped back panel and the label with its hover tip), so they look
// and behave like the originals. They only show while the Spiral shape is selected, and a click goes through the
// dialog's own GalaxySettingsChangedPreProcess so the galaxy preview regenerates with the new values.
internal static class SpiralUi
{
    private const string NamePrefix = "SpiralArms_";
    private const int GalaxySectionIndex = 0;

    private sealed class Row
    {
        public required string Key;
        public required string Label;
        public required string Hint;
        public required SpiralSettings.Option[] Options;
        public required Func<int> Get;
        public required Action<int> Set;
        public readonly List<DWControl> Controls = new();
    }

    private static readonly Row[] Rows =
    {
        new()
        {
            Key = "Arms", Label = "Spiral Arms", Options = SpiralSettings.ArmCounts,
            Hint = "Number of spiral arms. Two arms with a large core give a barred-spiral look; more arms give a busier, more tightly packed spiral.",
            Get = () => SpiralSettings.ArmCountIndex, Set = i => SpiralSettings.ArmCountIndex = i,
        },
        new()
        {
            Key = "Winding", Label = "Arm Winding", Options = SpiralSettings.Winding,
            Hint = "How tightly the arms wind around the core. Tight arms circle the galaxy several times; loose arms sweep outward more directly.",
            Get = () => SpiralSettings.WindingIndex, Set = i => SpiralSettings.WindingIndex = i,
        },
        new()
        {
            Key = "Width", Label = "Arm Width", Options = SpiralSettings.ArmWidth,
            Hint = "How far stars scatter from the centre line of each arm. Thin arms are sharply defined; wide arms blur into the gaps between them.",
            Get = () => SpiralSettings.ArmWidthIndex, Set = i => SpiralSettings.ArmWidthIndex = i,
        },
        new()
        {
            Key = "Taper", Label = "Arm Taper", Options = SpiralSettings.ArmTaper,
            Hint = "How the arms change in width toward the rim. None lets arms widen with distance from the core; stronger settings make them narrower and sharper at the edge of the galaxy.",
            Get = () => SpiralSettings.ArmTaperIndex, Set = i => SpiralSettings.ArmTaperIndex = i,
        },
        new()
        {
            Key = "Core", Label = "Core Size", Options = SpiralSettings.CoreSize,
            Hint = "Size of the dense central blob. Its star density stays the same, so a larger core holds proportionally more of the galaxy's stars.",
            Get = () => SpiralSettings.CoreSizeIndex, Set = i => SpiralSettings.CoreSizeIndex = i,
        },
        new()
        {
            Key = "Rim", Label = "Rim Thinning", Options = SpiralSettings.RimThinning,
            Hint = "How quickly star density falls off toward the rim. None keeps the density even all the way out; stronger settings leave the outskirts sparse.",
            Get = () => SpiralSettings.RimThinningIndex, Set = i => SpiralSettings.RimThinningIndex = i,
        },
        new()
        {
            Key = "Nebulae", Label = "Nebula Placement", Options = SpiralSettings.NebulaPlacement,
            Hint = "Where nebula clouds appear: along the arms, among the stars, or in the gaps between the arms. The nebula density setting above still controls how many there are.",
            Get = () => SpiralSettings.NebulaPlacementIndex, Set = i => SpiralSettings.NebulaPlacementIndex = i,
        },
    };

    private static StartNewGameDialog? _dialog;
    private static MethodInfo? _addLabelledControl;
    private static FieldInfo? _labelWidthField;
    private static FieldInfo? _startSettingsField;
    private static FieldInfo? _previewPositionField;
    private static FieldInfo? _previewSizeField;
    // The game draws the galaxy preview right under the last vanilla row, which is where our rows go. While Spiral is
    // selected it is moved to the free space right of them.
    private static Vector2 _vanillaPreviewPosition;
    private static Vector2 _spiralPreviewPosition;
    private static bool _previewPositionsKnown;

    public static void Install(Harmony harmony)
    {
        try
        {
            Type dialogType = typeof(StartNewGameDialog);
            _addLabelledControl = AccessTools.Method(dialogType, "AddLabelledControl", new[]
            {
                typeof(Galaxy), typeof(Empire), typeof(DWPanel), typeof(string), typeof(string), typeof(DWControl),
                typeof(float), typeof(float), typeof(float), typeof(float), typeof(float).MakeByRefType(),
            });
            _labelWidthField = AccessTools.Field(dialogType, "LabelWidth");
            _startSettingsField = AccessTools.Field(dialogType, "_GameStartSettings");
            _previewPositionField = AccessTools.Field(dialogType, "GalaxyPreviewTexturePosition");
            _previewSizeField = AccessTools.Field(dialogType, "GalaxyPreviewTextureSize");
            if (_addLabelledControl == null || _labelWidthField == null || _startSettingsField == null || _previewPositionField == null || _previewSizeField == null)
            {
                Bootstrap.Log("UI: dialog internals not found, in-game Spiral options disabled.");
                return;
            }

            Patch(harmony, dialogType, "AddControlsToPanel", nameof(PostAddControls));
            Patch(harmony, dialogType, "ShowSection", nameof(PostRefreshVisibility));
            Patch(harmony, dialogType, "BindData", nameof(PostRefreshVisibility));
            Patch(harmony, dialogType, "GalaxySettingsChangedPreProcessCore", nameof(PostRefreshVisibility));
        }
        catch (Exception ex)
        {
            Bootstrap.Log("UI: install failed: " + ex);
        }
    }

    private static void Patch(Harmony harmony, Type type, string method, string postfix)
    {
        MethodInfo? target = AccessTools.Method(type, method);
        if (target == null)
        {
            Bootstrap.Log($"UI: StartNewGameDialog.{method} not found.");
            return;
        }
        harmony.Patch(target, postfix: new HarmonyMethod(typeof(SpiralUi).GetMethod(postfix, BindingFlags.Static | BindingFlags.NonPublic)));
        Bootstrap.Log($"UI: patched StartNewGameDialog.{method}");
    }

    // Parameter names must match the original's so Harmony can bind them by name.
    private static void PostAddControls(StartNewGameDialog __instance, Galaxy galaxy, Empire empire, DWPanel panel, int index)
    {
        if (index != GalaxySectionIndex)
        {
            return;
        }

        try
        {
            _dialog = __instance;

            // Same sizes the game uses for its option rows (see AddControlsToPanel).
            float buttonWidth = UserInterfaceHelper.CalculateScaledValue(95f);
            float buttonGap = UserInterfaceHelper.CalculateScaledValue(8f);
            // Slightly shorter than the game's 60px-scaled rows so all rows fit under the existing ones.
            float buttonHeight = UserInterfaceHelper.CalculateScaledValue(35f);
            float lineHeight = UserInterfaceHelper.CalculateScaledValue(25f);
            float gap = __instance.HalfLineHeight * 1.5f;
            float labelWidth = (float)_labelWidthField!.GetValue(__instance)!;

            // The existing rows are stacked back to back; continue below the last one.
            float bottom = 0f;
            foreach (DWControl c in panel.Controls)
            {
                bottom = Math.Max(bottom, c.Position.Y + c.Size.Y);
            }
            float y = bottom + gap * 0.5f;

            foreach (Row row in Rows)
            {
                row.Controls.Clear();

                int count = row.Options.Length;
                var group = new ToggleButtonGroup { Name = NamePrefix + row.Key };
                group.Initialize((FontSize)1, (FontSize)2, null!);
                group.ClickEvent = OnChoice;
                group.Visible = true;
                group.SetSizeAndPosition(new Vector2(RowWidth(count, buttonWidth, buttonGap), buttonHeight), Vector2.Zero);
                for (int i = 0; i < count; i++)
                {
                    group.AddButton($"{NamePrefix}{row.Key} {row.Options[i].Label}", row.Options[i].Label, (Texture)null!);
                }

                int before = panel.Controls.Count;
                object[] args = { galaxy, empire, panel, row.Label, row.Hint, group, labelWidth, lineHeight, gap, 0f, y };
                _addLabelledControl!.Invoke(__instance, args);
                y = (float)args[10];

                for (int i = before; i < panel.Controls.Count; i++)
                {
                    row.Controls.Add(panel.Controls[i]);
                }
                group.ToggleButton(Math.Clamp(row.Get(), 0, count - 1));
            }

            // Move the preview to the right of the rows: left edge past the widest row, right edge at the panel border.
            var previewSize = (Vector2)_previewSizeField!.GetValue(__instance)!;
            _vanillaPreviewPosition = (Vector2)_previewPositionField!.GetValue(__instance)!;
            float widestRow = labelWidth + gap + Rows.Max(r => RowWidth(r.Options.Length, buttonWidth, buttonGap));
            _spiralPreviewPosition = new Vector2(Math.Max(panel.Size.X - previewSize.X - gap, widestRow + gap), bottom + gap * 0.5f);
            _previewPositionsKnown = true;

            Bootstrap.Log($"UI: added {Rows.Length} rows below y={bottom:F0}, ending at y={y:F0}.");
            ApplyVisibility(__instance);
        }
        catch (Exception ex)
        {
            Bootstrap.Log("UI: building rows failed: " + ex);
        }
    }

    // Width of a row of buttons. Rows of up to five use the game's button width; longer rows (the arm count) shrink
    // their buttons so the whole row is as wide as a five-button row.
    private static float RowWidth(int count, float buttonWidth, float buttonGap)
    {
        const int MaxFullWidthButtons = 5;
        if (count <= MaxFullWidthButtons)
        {
            return count * buttonWidth + (count - 1) * buttonGap;
        }
        return MaxFullWidthButtons * buttonWidth + (MaxFullWidthButtons - 1) * buttonGap;
    }

    private static void PostRefreshVisibility(StartNewGameDialog __instance)
    {
        ApplyVisibility(__instance);
    }

    private static void ApplyVisibility(StartNewGameDialog dialog)
    {
        try
        {
            var settings = _startSettingsField?.GetValue(dialog) as GameStartSettings;
            // Before the first bind there are no settings yet: keep the rows hidden.
            bool show = settings?.Shape == GalaxyShape.Spiral;
            if (_previewPositionsKnown)
            {
                _previewPositionField!.SetValue(dialog, show ? _spiralPreviewPosition : _vanillaPreviewPosition);
            }
            foreach (Row row in Rows)
            {
                foreach (DWControl c in row.Controls)
                {
                    c.Visible = show;
                }
            }
        }
        catch (Exception ex)
        {
            Bootstrap.Log("UI: visibility update failed: " + ex.Message);
        }
    }

    // Click handler of every row: remember the choice, then let the dialog treat it as a galaxy setting change
    // so the preview galaxy is regenerated.
    private static void OnChoice(object? sender, DWEventArgs args)
    {
        try
        {
            if (sender is ToggleButtonGroup group && group.Name != null && group.Name.StartsWith(NamePrefix, StringComparison.Ordinal))
            {
                Row? row = Array.Find(Rows, r => NamePrefix + r.Key == group.Name);
                if (row != null && group.ToggledButtonIndex >= 0)
                {
                    row.Set(group.ToggledButtonIndex);
                }
            }
        }
        catch (Exception ex)
        {
            Bootstrap.Log("UI: click handling failed: " + ex.Message);
        }
        _dialog?.GalaxySettingsChangedPreProcess(sender!, args);
    }
}
