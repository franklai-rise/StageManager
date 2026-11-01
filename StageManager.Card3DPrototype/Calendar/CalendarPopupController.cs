namespace StageManager.Card3DPrototype.Calendar;

internal enum CalendarPopupAction { None, Open, KeepOpen, FadeOut }

// Pure input/timing policy. The host owns windows, hit testing and Escape/outside dismissal.
internal sealed class CalendarPopupController
{
    private DateTime? _entered, _left;
    private bool _dismissed, _clicked;
    public void Click() { _clicked = true; _dismissed = false; _entered = _left = null; }
    public void Dismiss() { _dismissed = true; _clicked = false; _entered = _left = null; }
    public CalendarPopupAction Update(bool onCard, bool onPanel, bool onCorridor, bool open, DateTime now)
    {
        if (_clicked) { _clicked = false; return open ? CalendarPopupAction.KeepOpen : CalendarPopupAction.Open; }
        if (!onCard && !onPanel && !onCorridor) _dismissed = false;
        if (open)
        {
            _entered = null;
            if (onCard || onPanel || onCorridor) { _left = null; return CalendarPopupAction.KeepOpen; }
            if (_left is null || now < _left.Value) _left = now;
            return now - _left.Value >= TimeSpan.FromMilliseconds(300) ? CalendarPopupAction.FadeOut : CalendarPopupAction.None;
        }
        _left = null;
        if (!onCard || _dismissed) { _entered = null; return CalendarPopupAction.None; }
        if (_entered is null || now < _entered.Value) _entered = now;
        return now - _entered.Value >= TimeSpan.FromMilliseconds(300) ? CalendarPopupAction.Open : CalendarPopupAction.None;
    }
}
