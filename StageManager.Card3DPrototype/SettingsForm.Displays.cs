using Microsoft.Win32;
using StageManager.Settings;

namespace StageManager.Card3DPrototype;

internal sealed partial class SettingsForm
{
    private ComboBox _displayChoice = null!;
    private Label _displaySelectionHint = null!;
    private bool _displayEventsSubscribed;
    internal sealed record DisplayChoice(SidebarDisplayMode Mode, string MonitorId, string DeviceName, string Label, bool Connected = true)
    {
        public override string ToString() => Label;
    }

    private void BuildDisplaySelection(FlowLayoutPanel layout)
    {
        _displayChoice = Choice(new[] { "" }, 0);
        _displayChoice.DropDownWidth = D(500);
        Add(layout, SettingRow("Display", "显示器", _displayChoice));
        _displaySelectionHint = Note("", "");
        Add(layout, _displaySelectionHint);
        _displayChoice.SelectedIndexChanged += (_, _) => UpdateDisplaySelectionHint();
        _localized.Add(() => RefreshDisplayChoices());
        Shown += (_, _) =>
        {
            if (_displayEventsSubscribed) return;
            SystemEvents.DisplaySettingsChanged += SettingsDisplaysChanged;
            _displayEventsSubscribed = true;
        };
    }

    internal void RefreshDisplayChoices(IReadOnlyList<SidebarDisplayInfo>? displays = null)
    {
        var previous = _displayChoice.SelectedItem as DisplayChoice ??
            new DisplayChoice(Draft.SidebarDisplayMode, Draft.SidebarDisplayId, Draft.SidebarDisplayDeviceName, "");
        displays ??= SidebarDisplayCatalog.Enumerate();
        var choices = new List<DisplayChoice>
        {
            new(SidebarDisplayMode.Leftmost, "", "", T("Automatic · leftmost screen", "自动 · 最左侧屏幕")),
            new(SidebarDisplayMode.Primary, "", "", T("Windows primary display", "Windows 主显示器"))
        };
        foreach (var display in displays)
        {
            var device = display.DeviceName.Replace(@"\\.\", "");
            var primary = display.Primary ? T(" · primary", " · 主屏") : "";
            choices.Add(new(SidebarDisplayMode.Specific, display.MonitorId, display.DeviceName,
                $"{device} · {display.Name} · {display.Bounds.Width} × {display.Bounds.Height}{primary}"));
        }
        var selected = choices.FindIndex(choice => choice.Mode == previous.Mode &&
            (choice.Mode != SidebarDisplayMode.Specific ||
             (previous.MonitorId.Length > 0 ? string.Equals(choice.MonitorId, previous.MonitorId, StringComparison.OrdinalIgnoreCase) :
                string.Equals(choice.DeviceName, previous.DeviceName, StringComparison.OrdinalIgnoreCase))));
        var missing = selected < 0 && previous.Mode == SidebarDisplayMode.Specific;
        if (missing)
        {
            choices.Add(previous with { Label = T("Disconnected · ", "未连接 · ") + previous.DeviceName, Connected = false });
            selected = choices.Count - 1;
        }
        _displayChoice.BeginUpdate();
        try
        {
            _displayChoice.Items.Clear(); _displayChoice.Items.AddRange(choices.Cast<object>().ToArray());
            _displayChoice.SelectedIndex = Math.Max(0, selected);
        }
        finally { _displayChoice.EndUpdate(); }
        UpdateDisplaySelectionHint();
    }

    private void UpdateDisplaySelectionHint()
    {
        _displaySelectionHint.Text = _displayChoice.SelectedItem is DisplayChoice { Connected: false }
            ? T("Selected screen is disconnected. The dock temporarily uses the primary display; your choice is retained.", "指定屏幕未连接，卡片栏暂时使用主屏；保留选择，重新连接后自动返回。")
            : T("Choose a connected display, then Save. If it disconnects, use the primary screen temporarily. Dock edge is independent.", "选择屏幕后保存。断开时临时使用主屏，重连后自动返回；停靠方向仍可独立选择。");
    }

    private void SettingsDisplaysChanged(object? sender, EventArgs e)
    {
        if (!IsHandleCreated || IsDisposed || Disposing) return;
        try { BeginInvoke(new Action(() => { if (!IsDisposed && !Disposing) RefreshDisplayChoices(); })); }
        catch (InvalidOperationException) { }
    }

    private void SaveDisplaySelection()
    {
        if (_displayChoice.SelectedItem is not DisplayChoice choice) return;
        Draft.SidebarDisplayMode = choice.Mode;
        Draft.SidebarDisplayId = choice.MonitorId;
        Draft.SidebarDisplayDeviceName = choice.DeviceName;
    }
}
