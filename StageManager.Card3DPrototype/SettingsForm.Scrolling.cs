using System.Runtime.InteropServices;

namespace StageManager.Card3DPrototype;

internal sealed partial class SettingsForm
{
    private SettingsWheelFilter? _settingsWheelFilter;
    private double _wheelRemainder;

    internal bool RouteSettingsWheel(Control target, int delta)
    {
        if (IsDisposed || target.FindForm() != this || !_uiReady || delta == 0) return false;
        // An open dropdown owns its list. Otherwise wheel input never changes a
        // numeric setting, slider or selection simply because the pointer is over it.
        if (Descendants(this).OfType<ComboBox>().Any(c => c.DroppedDown)) return false;
        StopNavigation();
        var lines = SystemInformation.MouseWheelScrollLines;
        var distance = lines < 0 ? _pageHost.ClientSize.Height : D(Math.Max(1, lines) * 18);
        _wheelRemainder += delta / 120d * distance;
        var step = (int)_wheelRemainder;
        _wheelRemainder -= step;
        var current = -_pageHost.AutoScrollPosition.Y;
        var limit = Math.Max(0, _allModules.Height - _pageHost.ClientSize.Height);
        _pageHost.AutoScrollPosition = new Point(0, Math.Clamp(current - step, 0, limit));
        RememberVisibleModule();
        return true;
    }

    private sealed class SettingsWheelFilter(SettingsForm owner) : IMessageFilter
    {
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != 0x020A || Form.ActiveForm != owner || owner.IsDisposed) return false;
            var packed = m.LParam.ToInt64();
            var point = new Point(unchecked((short)packed), unchecked((short)(packed >> 16)));
            var target = Control.FromChildHandle(WindowFromPoint(point));
            return target is not null && owner.RouteSettingsWheel(target, unchecked((short)(m.WParam.ToInt64() >> 16)));
        }
    }
}
