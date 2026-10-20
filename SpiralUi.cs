using System.Reflection;
using DistantWorlds.Types;
using DistantWorlds.UI;
using HarmonyLib;
using Stride.Core.Mathematics;
using Stride.Graphics;

namespace SpiralArms;

// Adds the shape options (see UiRows) as extra rows at the bottom of the galaxy settings section (section 0) of the
// new-game dialog. Rows are built the way the game builds its own (a ToggleButtonGroup handed to the dialog's
// private AddLabelledControl, which adds the striped back panel and the label with its hover tip), so they look
// and behave like the originals. Each shape shows only its own rows, and a click goes through the dialog's own
// GalaxySettingsChangedPreProcess so the galaxy preview regenerates with the new values.
internal static class SpiralUi
{
    private const string NamePrefix = "SpiralArms_";
    private const int GalaxySectionIndex = 0;

    private static readonly UiRow[] Rows = UiRows.All;

    private static StartNewGameDialog? _dialog;
    private static MethodInfo? _addLabelledControl;
    private static FieldInfo? _labelWidthField;
    private static FieldInfo? _startSettingsField;
    private static FieldInfo? _rowCounterField;
    private static FieldInfo? _previewPositionField;
    private static FieldInfo? _previewSizeField;
    // The game draws the galaxy preview right under the last vanilla row, which is where our rows go. While a shape with
    // extra rows is selected it is moved to the free space right of them.
    private static Vector2 _vanillaPreviewPosition;
    private static Vector2 _shapedPreviewPosition;
    private static bool _previewPositionsKnown;
    private static Vector2 _vanillaPreviewSize;
    private static DWPanel? _galaxyPanel;
    private static FieldInfo? _sectionContainerField;
    // The preview is never shrunk below this share of its normal size.
    private const float MinPreviewScale = 0.3f;
    private static int _loggedGeometry;

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
            _rowCounterField = AccessTools.Field(dialogType, "_RowCounter");
            _previewPositionField = AccessTools.Field(dialogType, "GalaxyPreviewTexturePosition");
            _previewSizeField = AccessTools.Field(dialogType, "GalaxyPreviewTextureSize");
            if (_addLabelledControl == null || _labelWidthField == null || _startSettingsField == null || _rowCounterField == null
                || _previewPositionField == null || _previewSizeField == null)
            {
                Bootstrap.Log("UI: dialog internals not found, in-game shape options disabled.");
                return;
            }

            Patch(harmony, dialogType, "AddControlsToPanel", nameof(PostAddControls));
            Patch(harmony, dialogType, "ShowSection", nameof(PostRefreshVisibility));
            MethodInfo? render = dialogType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "Render" && m.GetParameters().Length == 4 && m.DeclaringType == dialogType);
            if (render != null)
            {
                harmony.Patch(render, prefix: new HarmonyMethod(typeof(SpiralUi).GetMethod(nameof(RenderPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                Bootstrap.Log("UI: patched StartNewGameDialog.Render (preview only on the first page)");
            }
            else
            {
                Bootstrap.Log("UI: StartNewGameDialog.Render not found; the preview strip on the next page stays.");
            }
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
        if (UiRows.CommonBySection.TryGetValue(index, out UiRow[]? common))
        {
            if (index == 3)
            {
                VanillaExtras.AddButtons(__instance);
            }
            AddCommonRows(__instance, galaxy, empire, panel, index, common);
            return;
        }
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
            // Tighter than the game's own row spacing (HalfLineHeight * 1.5): the biggest stacks have eight rows.
            float gap = UserInterfaceHelper.CalculateScaledValue(10f);
            float labelWidth = (float)_labelWidthField!.GetValue(__instance)!;

            // The existing rows are stacked back to back; continue below the last one.
            float bottom = 0f;
            foreach (DWControl c in panel.Controls)
            {
                bottom = Math.Max(bottom, c.Position.Y + c.Size.Y);
            }
            float firstY = bottom + gap * 0.5f;
            object stripeStart = _rowCounterField!.GetValue(__instance)!;

            // One stack of rows per group, all starting at the same place (only one group is visible at a time).
            foreach (IGrouping<string, UiRow> group in Rows.GroupBy(r => r.Group))
            {
                _rowCounterField.SetValue(__instance, stripeStart);
                float y = firstY;
                foreach (UiRow row in group)
                {
                    BuildRow(__instance, galaxy, empire, panel, row, labelWidth, lineHeight, gap, buttonWidth, buttonGap, buttonHeight, ref y);
                }
            }
            _rowCounterField.SetValue(__instance, stripeStart);

            // Move the preview to the right of the rows: left edge past the widest row, right edge at the panel border.
            var previewSize = (Vector2)_previewSizeField!.GetValue(__instance)!;
            _vanillaPreviewSize = previewSize;
            _galaxyPanel = panel;
            _sectionContainerField ??= AccessTools.Field(typeof(StartNewGameDialog), "_SectionContainer");
            _vanillaPreviewPosition = (Vector2)_previewPositionField!.GetValue(__instance)!;
            float widestRow = labelWidth + gap + Rows.Max(r => RowWidth(r.Options.Length, buttonWidth, buttonGap));
            _shapedPreviewPosition = new Vector2(Math.Max(panel.Size.X - previewSize.X - gap, widestRow + gap), firstY);
            _previewPositionsKnown = true;

            Bootstrap.Log($"UI: added {Rows.Length} rows below y={bottom:F0}.");
            ApplyVisibility(__instance);
        }
        catch (Exception ex)
        {
            Bootstrap.Log("UI: building rows failed: " + ex);
        }
    }

    // Builds one row (label, hover tip and buttons) at y and advances y past it.
    private static void BuildRow(StartNewGameDialog dialog, Galaxy galaxy, Empire empire, DWPanel panel, UiRow row, float labelWidth, float lineHeight,
        float gap, float buttonWidth, float buttonGap, float buttonHeight, ref float y)
    {
        row.Controls.Clear();

        int count = row.Options.Length;
        var buttons = new ToggleButtonGroup { Name = NamePrefix + row.Key };
        buttons.Initialize((FontSize)1, (FontSize)2, null!);
        buttons.ClickEvent = OnChoice;
        buttons.Visible = true;
        buttons.SetSizeAndPosition(new Vector2(RowWidth(count, buttonWidth, buttonGap), buttonHeight), Vector2.Zero);
        for (int i = 0; i < count; i++)
        {
            buttons.AddButton($"{NamePrefix}{row.Key} {row.Options[i].Label}", row.Options[i].Label, (Texture)null!);
        }

        int before = panel.Controls.Count;
        object[] args = { galaxy, empire, panel, row.Label, row.Hint, buttons, labelWidth, lineHeight, gap, 0f, y };
        _addLabelledControl!.Invoke(dialog, args);
        y = (float)args[10];

        for (int i = before; i < panel.Controls.Count; i++)
        {
            row.Controls.Add(panel.Controls[i]);
        }
        row.Buttons = buttons;
        buttons.ToggleButton(Math.Clamp(row.Get(), 0, count - 1));
    }

    // Rows that apply to every galaxy shape: appended below the vanilla rows of their section, always visible.
    private static void AddCommonRows(StartNewGameDialog dialog, Galaxy galaxy, Empire empire, DWPanel panel, int section, UiRow[] rows)
    {
        try
        {
            _dialog = dialog;
            float buttonWidth = UserInterfaceHelper.CalculateScaledValue(95f);
            float buttonGap = UserInterfaceHelper.CalculateScaledValue(8f);
            float buttonHeight = UserInterfaceHelper.CalculateScaledValue(35f);
            float lineHeight = UserInterfaceHelper.CalculateScaledValue(25f);
            float gap = UserInterfaceHelper.CalculateScaledValue(10f);
            float labelWidth = (float)_labelWidthField!.GetValue(dialog)!;

            float bottom = 0f;
            foreach (DWControl c in panel.Controls)
            {
                bottom = Math.Max(bottom, c.Position.Y + c.Size.Y);
            }
            float y = bottom + gap * 0.5f;
            float startY = y;
            foreach (UiRow row in rows)
            {
                BuildRow(dialog, galaxy, empire, panel, row, labelWidth, lineHeight, gap, buttonWidth, buttonGap, buttonHeight, ref y);
            }
            // The starting-location picture is drawn right below the last vanilla row, where the new rows now are: push it down.
            // The refugee-fleet picture goes to the same place as the ordinary one.
            if (section == 6)
            {
                FieldInfo? ordinary = AccessTools.Field(dialog.GetType(), "StartingLocationTexturePosition");
                FieldInfo? refugee = AccessTools.Field(dialog.GetType(), "StartingLocationTexturePositionRefugeeFleet");
                if (ordinary?.GetValue(dialog) is Vector2 position && position != Vector2.Zero)
                {
                    var moved = new Vector2(position.X, position.Y + (y - startY));
                    ordinary.SetValue(dialog, moved);
                    refugee?.SetValue(dialog, moved);
                    Bootstrap.Log($"UI: starting-location picture moved from y {position.Y:F0} to {moved.Y:F0} (refugee fleet: same place).");
                }
            }
            // The pirate empire step has a picture of its own, in the same place relative to its rows.
            if (section == 10)
            {
                FieldInfo? pirate = AccessTools.Field(dialog.GetType(), "StartingLocationTexturePositionPirate");
                if (pirate?.GetValue(dialog) is Vector2 position && position != Vector2.Zero)
                {
                    var moved = new Vector2(position.X, position.Y + (y - startY));
                    pirate.SetValue(dialog, moved);
                    Bootstrap.Log($"UI: pirate starting-location picture moved from y {position.Y:F0} to {moved.Y:F0}.");
                }
            }
            Bootstrap.Log($"UI: added {rows.Length} row(s) to section {section} below y={bottom:F0} (panel height {panel.Size.Y:F0}).");
        }
        catch (Exception ex)
        {
            Bootstrap.Log("UI: building common rows failed: " + ex);
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

    // The size the preview has on the first page (what ApplyVisibility worked out); null until then.
    private static Vector2? _fittedPreviewSize;
    private static int _lastRenderStep = -1;

    // Runs before every frame of the dialog. The dialog draws the preview on the first page and also on the next one, where the first
    // page's panel (moved aside) still leaves a strip of the preview in view; so it has no size there. Step changes are not reported
    // through any hook that fires on the Next and Previous buttons, so this is decided here.
    private static void RenderPrefix(StartNewGameDialog __instance)
    {
        try
        {
            if (_fittedPreviewSize is not Vector2 fitted || _previewSizeField == null)
            {
                return;
            }
            int step = __instance.ResolveStepIndex(__instance.StartNewGame_Index);
            if (step != _lastRenderStep)
            {
                _lastRenderStep = step;
                Bootstrap.Log($"UI: dialog step is now {step}; the preview is {(step == 0 ? "shown" : "hidden")}.");
            }
            _previewSizeField.SetValue(__instance, step == 0 ? fitted : new Vector2(0f, 0f));
        }
        catch (Exception ex)
        {
            Bootstrap.Log("UI: preview render hook failed: " + ex.Message);
        }
    }

    private static void ApplyVisibility(StartNewGameDialog dialog)
    {
        try
        {
            var settings = _startSettingsField?.GetValue(dialog) as GameStartSettings;
            // Before the first bind there are no settings yet: keep the rows hidden.
            bool anyRows = false;
            foreach (UiRow row in Rows)
            {
                bool show = settings != null && Array.IndexOf(row.Shapes, settings.Shape) >= 0;
                anyRows |= show;
                foreach (DWControl c in row.Controls)
                {
                    c.Visible = show;
                }

                // The same setting can appear in several stacks (the ring shapes), so bring the shown buttons up to date.
                if (show && row.Buttons != null)
                {
                    int wanted = Math.Clamp(row.Get(), 0, row.Options.Length - 1);
                    if (row.Buttons.ToggledButtonIndex != wanted)
                    {
                        row.Buttons.ToggleButton(wanted);
                    }
                }
            }
            // Rows of other sections share a setting with their twin on another step: bring every toggle up to date.
            foreach (UiRow row in UiRows.CommonBySection.Values.SelectMany(r => r))
            {
                if (row.Buttons != null)
                {
                    int wanted = Math.Clamp(row.Get(), 0, row.Options.Length - 1);
                    if (row.Buttons.ToggledButtonIndex != wanted)
                    {
                        row.Buttons.ToggleButton(wanted);
                    }
                }
            }
            if (_previewPositionsKnown)
            {
                Vector2 position = anyRows ? _shapedPreviewPosition : _vanillaPreviewPosition;
                Vector2 size = _vanillaPreviewSize;
                // The preview keeps its shape but shrinks to the room that is left: below the last row of the screen's panel, or right of the extra rows.
                var panel = _galaxyPanel;
                var container = _sectionContainerField?.GetValue(dialog) as DWPanel;
                if (panel != null && container != null)
                {
                    float gap = UserInterfaceHelper.CalculateScaledValue(10f);
                    // The preview is drawn at (panel's screen position + position), so the room is what is left of the visible container
                    // (in screen coordinates) from that point.
                    RectangleF panelRect = panel.CalculateAbsoluteRectangle();
                    RectangleF containerRect = container.CalculateAbsoluteRectangle();
                    // The section panels slide sideways as pages change, so the panel's current screen position says nothing about the room:
                    // the galaxy page's panel sits at the container's corner while it is shown, and the container is what the preview must stay inside.
                    float width = containerRect.Width;
                    float height = containerRect.Height;
                    // The pages sit side by side and the neighbouring page begins a little before this panel's right edge (a strip of the preview
                    // showed at the left of the next page when it came closer than that), so keep well clear of the right edge.
                    float rightMargin = UserInterfaceHelper.CalculateScaledValue(90f);
                    float room = Math.Min((width - rightMargin - position.X) / size.X, (height - position.Y - gap) / size.Y);
                    if (_loggedGeometry++ < 40)
                    {
                        Bootstrap.Log($"UI: preview geometry: ui scale {UserInterfaceHelper.CalculateScaledValue(100f):0}%, panel {panelRect.X:0},{panelRect.Y:0} {panelRect.Width:0}x{panelRect.Height:0}, "
                            + $"container {containerRect.X:0},{containerRect.Y:0} {containerRect.Width:0}x{containerRect.Height:0}, preview at {position.X:0},{position.Y:0} size {size.X:0}x{size.Y:0}, "
                            + $"extra rows {anyRows}, room factor {room:0.00}.");
                    }
                    float scale = Math.Clamp(room, MinPreviewScale, 1f);
                    size = new Vector2(size.X * scale, size.Y * scale);
                    if (anyRows)
                    {
                        // right-aligned to the panel edge, but never over the rows
                        position = new Vector2(Math.Max(_shapedPreviewPosition.X, width - rightMargin - size.X), position.Y);
                    }
                }
                // The dialog also draws the preview on the next page (galaxy settings), at the place the first page now holds, which is
                // shifted aside but still overlaps the visible area by a strip: that strip is the preview's right end. Nothing is drawn there.
                int step = dialog.ResolveStepIndex(dialog.StartNewGame_Index);
                if (step != 0)
                {
                    size = new Vector2(0f, 0f);
                }
                if (_loggedGeometry < 40)
                {
                    Bootstrap.Log($"UI: preview set: step {step}, position {position.X:0},{position.Y:0}, size {size.X:0}x{size.Y:0}.");
                }
                _previewPositionField!.SetValue(dialog, position);
                _previewSizeField!.SetValue(dialog, size);
                _fittedPreviewSize = step == 0 ? size : _fittedPreviewSize;
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
                UiRow? row = Array.Find(Rows, r => NamePrefix + r.Key == group.Name) ?? UiRows.CommonBySection.Values.SelectMany(r => r).FirstOrDefault(r => NamePrefix + r.Key == group.Name);
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
