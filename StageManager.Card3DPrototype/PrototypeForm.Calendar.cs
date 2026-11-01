using Microsoft.Win32;
using StageManager.Card3DPrototype.Calendar;
using StageManager.Native.PInvoke;
using StageManager.Settings;
using System.Net.NetworkInformation;

namespace StageManager.Card3DPrototype;

internal sealed partial class PrototypeForm
{
    private readonly CalendarService _calendarService = new();
    private readonly CalendarCompletionStore _calendarCompletion = new();
    private readonly System.Windows.Forms.Timer _calendarTimer = new() { Interval = 30000 };
    private readonly CalendarPopupController _calendarPopup = new();
    private CancellationTokenSource _calendarCancellation = new();
    private CancellationTokenSource? _calendarPanelCancellation;
    private CalendarDetailsForm? _calendarDetails;
    private Rectangle _calendarCorridor;
    private long _calendarMonthRequest;
    private bool _calendarRefreshRunning;
    private bool CalendarPanelOpen => _calendarDetails is { IsDisposed: false };
    private bool CalendarPointerInside(Point screen) => _calendarDetails is { IsDisposed: false } panel && panel.IsPointerInside(screen);

    private void InitializeCalendarHost()
    {
        _calendarService.Updated += CalendarServiceUpdated;
        _calendarTimer.Tick += CalendarTimerTick;
        SystemEvents.PowerModeChanged += CalendarPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged += CalendarNetworkChanged;
        SystemEvents.TimeChanged += CalendarTimeChanged;
    }

    private bool ApplyCalendarSettings()
    {
        if (_closing || _catalog is null || _renderer is null) return false;
        var settings = _catalog.Settings.Current;
        // Changing language/account/placement must not leave a popup with stale content or coordinates.
        CloseCalendarDetails();
        var changed = _calendarService.Configure(settings.ShowCalendarCard,
            settings.CalendarUseCodex ? "codex:" + settings.CodexCliPath : settings.OutlookCalendarClientId, settings.OutlookCalendarIds);
        var layoutChanged = _renderer.SetCalendarCardEnabled(settings.ShowCalendarCard);
        _renderer.SetCalendarLanguage(settings.UiLanguage);
        _renderer.RefreshCalendarDate();
        if (settings.ShowCalendarCard)
        {
            if (_calendarCancellation.IsCancellationRequested)
            {
                _calendarCancellation.Dispose();
                _calendarCancellation = new CancellationTokenSource();
            }
            _calendarTimer.Start();
            if (changed) _ = RefreshCalendarAsync();
        }
        else
        {
            _calendarTimer.Stop();
            _calendarCancellation.Cancel();
        }
        if (layoutChanged) UpdateWindowRegion(true);
        return layoutChanged;
    }

    private async void CalendarTimerTick(object? sender, EventArgs e)
    {
        // CalendarService owns periodic today refresh. This UI timer updates date/card state
        // and only refreshes a visible month (the service applies its own due/backoff policy).
        if (_closing || _catalog?.Settings.Current.ShowCalendarCard != true) return;
        _renderer?.RefreshCalendarDate();
        if (_calendarDetails is { IsDisposed: false } panel)
            await LoadCalendarMonthAsync(panel, panel.DisplayMonth);
    }

    private async Task RefreshCalendarAsync(bool manual = false, bool resumed = false)
    {
        if (_closing || _catalog?.Settings.Current.ShowCalendarCard != true || _calendarRefreshRunning) return;
        _calendarRefreshRunning = true;
        try
        {
            if (resumed) await _calendarService.ResumeAsync();
            else await _calendarService.RefreshAsync(manual, _calendarCancellation.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Provider failures are represented by snapshots, never logged with personal content. */ }
        finally { _calendarRefreshRunning = false; }
    }

    private void CalendarServiceUpdated(object? sender, EventArgs e) => PostCalendarUi(() =>
    {
        _renderer?.RefreshCalendarDate();
    });

    private void PostCalendarUi(Action action)
    {
        if (_closing || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke((Action)(() => { if (!_closing && !IsDisposed) action(); })); }
        catch (InvalidOperationException) { }
    }

    private void CalendarPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) PostCalendarUi(() => _ = RefreshCalendarAsync(resumed: true));
    }

    private void CalendarNetworkChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable) PostCalendarUi(() => _ = RefreshCalendarAsync(resumed: true));
    }
    private void CalendarTimeChanged(object? sender, EventArgs e) => PostCalendarUi(() => _ = RefreshCalendarAsync(resumed: true));

    private void OpenCalendarDetails(CardHitTarget? target = null, bool fromClick = false)
    {
        if (_closing || _settingsOpen || !_sidebarVisible || _renderer is null ||
            _catalog?.Settings.Current.ShowCalendarCard != true) return;
        if (fromClick)
        {
            _contextMenu.Close(ToolStripDropDownCloseReason.AppClicked);
            _cardContextMenu.Close(ToolStripDropDownCloseReason.AppClicked);
            _calendarPopup.Click();
        }
        else if (_contextMenu.Visible || _cardContextMenu.Visible) return;
        _aiUsagePopup.Dismiss();
        CloseAiUsageDetails(immediate: true);
        _renderer.StopEdgeScroll();
        HideHoverHint();
        if (_calendarDetails is { IsDisposed: false } existing) { existing.KeepOpen(); return; }
        target = _renderer.GetCalendarTarget() ?? target;
        var settings = _catalog.Settings.Current;
        var panel = new CalendarDetailsForm(settings.UiLanguage, settings.AnimationsEnabled);
        panel.ManualRefresh = settings.CalendarUseCodex;
        _calendarDetails = panel;
        _calendarPanelCancellation = CancellationTokenSource.CreateLinkedTokenSource(_calendarCancellation.Token);
        panel.SetCompletedEvents(_calendarCompletion.Keys);
        var todaySnapshot = _calendarService.Current;
        panel.SetSnapshot(todaySnapshot.Status is CalendarStatus.Connected or CalendarStatus.Stale
            ? CalendarSnapshot.Empty(CalendarStatus.Loading) : todaySnapshot);
        panel.CompletionChanged += (item, completed) =>
        {
            if (_closing || !ReferenceEquals(_calendarDetails, panel)) return;
            SetCalendarCompletion(item, completed);
        };
        panel.MonthRequested += month => _ = LoadCalendarMonthAsync(panel, month);
        panel.RefreshRequested += async (_, _) =>
        {
            if (!_calendarService.UsesCodex) await RefreshCalendarAsync(manual: true);
            if (ReferenceEquals(_calendarDetails, panel) && !panel.IsDisposed)
                await LoadCalendarMonthAsync(panel, panel.DisplayMonth, manual: true);
        };
        panel.SettingsRequested += (_, _) => ShowSettings(aiUsage: false, calendar: true);
        panel.CloseRequested += (_, _) => CloseCalendarDetails();
        panel.FormClosed += (_, _) =>
        {
            if (!ReferenceEquals(_calendarDetails, panel)) return;
            _calendarDetails = null;
            CancelCalendarPanelRequests();
            _calendarCorridor = Rectangle.Empty;
            _calendarPopup.Dismiss();
            PublishWheelRegion();
        };
        panel.VisibleChanged += (_, _) =>
        {
            // The detail form finishes its fade by hiding, rather than closing itself.
            if (!panel.Visible && ReferenceEquals(_calendarDetails, panel))
                PostCalendarUi(() => { if (!panel.Visible && ReferenceEquals(_calendarDetails, panel)) CloseCalendarDetails(); });
        };
        PositionCalendarPanel(panel, target);
        _ = NativeMethods.GetAsyncKeyState(0x1B);
        PrimePointerButtonState();
        panel.Show(this); // CalendarDetailsForm uses ShowWithoutActivation / WS_EX_NOACTIVATE.
        _lastSidebarInteractionUtc = DateTime.UtcNow;
        PublishWheelRegion();
        _ = LoadCalendarMonthAsync(panel, panel.DisplayMonth);
    }

    private void SetCalendarCompletion(CalendarEvent item, bool completed)
    {
        if (_closing) return;
        var saved = false;
        try { saved = _calendarCompletion.Set(item, completed); }
        catch (Exception) { /* Do not disclose event content or local storage paths. */ }
        var keys = _calendarCompletion.Keys;
        if (_calendarDetails is { IsDisposed: false } panel) panel.SetCompletedEvents(keys);
        if (!saved)
            _trayIcon?.ShowBalloonTip(3000, L("Calendar", "日历"),
                L("Could not save the local completion state. It may be lost after restart. Please try again.",
                    "无法保存本机完成状态，重启后可能丢失。请重试。"), ToolTipIcon.Warning);
    }

    private void PositionCalendarPanel(CalendarDetailsForm panel, CardHitTarget? target)
    {
        var dpi = DeviceDpi / 96f;
        var gap = Math.Max(1, (int)Math.Ceiling(12 * dpi));
        var bounds = target?.IsCalendarCard == true && target.Polygon.Count > 0
            ? RectangleF.FromLTRB(target.Polygon.Min(p => p.X), target.Polygon.Min(p => p.Y),
                target.Polygon.Max(p => p.X), target.Polygon.Max(p => p.Y))
            : new RectangleF(12, 12, 196 * dpi, 196 * dpi);
        bounds.Offset(_renderer!.ScrollTranslationX, _renderer.ScrollTranslationY);
        var card = RectangleToScreen(Rectangle.Ceiling(bounds));
        var work = Screen.FromRectangle(card).WorkingArea;
        work.Inflate(-Math.Min(gap, Math.Max(0, (work.Width - 1) / 2)),
            -Math.Min(gap, Math.Max(0, (work.Height - 1) / 2)));
        panel.FitToWorkingArea(work, dpi);
        panel.Size = new Size(Math.Min(panel.Width, work.Width), Math.Min(panel.Height, work.Height));
        var x = DockEdge == SidebarEdge.Right ? card.Left - gap - panel.Width : card.Right + gap;
        var y = card.Top;
        if (SidebarDockLayout.IsHorizontal(DockEdge))
        {
            x = card.Left;
            y = DockEdge == SidebarEdge.Top ? card.Bottom + gap : card.Top - gap - panel.Height;
        }
        panel.Location = new Point(Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - panel.Width)),
            Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - panel.Height)));
        // Only the gap between facing edges is a corridor, not the entire surrounding desktop.
        if (panel.Left >= card.Right)
            _calendarCorridor = Rectangle.FromLTRB(card.Right, Math.Min(card.Top, panel.Top), panel.Left, Math.Max(card.Bottom, panel.Bottom));
        else if (panel.Right <= card.Left)
            _calendarCorridor = Rectangle.FromLTRB(panel.Right, Math.Min(card.Top, panel.Top), card.Left, Math.Max(card.Bottom, panel.Bottom));
        else if (panel.Top >= card.Bottom)
            _calendarCorridor = Rectangle.FromLTRB(Math.Min(card.Left, panel.Left), card.Bottom, Math.Max(card.Right, panel.Right), panel.Top);
        else if (panel.Bottom <= card.Top)
            _calendarCorridor = Rectangle.FromLTRB(Math.Min(card.Left, panel.Left), panel.Bottom, Math.Max(card.Right, panel.Right), card.Top);
        else _calendarCorridor = Rectangle.Empty;
    }

    private async Task LoadCalendarMonthAsync(CalendarDetailsForm panel, DateOnly month, bool manual = false)
    {
        if (_closing || !ReferenceEquals(panel, _calendarDetails) || panel.IsDisposed || _calendarPanelCancellation is null) return;
        var request = ++_calendarMonthRequest;
        var token = _calendarPanelCancellation.Token;
        panel.SetRefreshing(true);
        try
        {
            var snapshot = await _calendarService.GetMonthAsync(month, manual, token);
            if (!token.IsCancellationRequested && !_closing && request == _calendarMonthRequest &&
                ReferenceEquals(panel, _calendarDetails) && !panel.IsDisposed && panel.DisplayMonth == CalendarDates.MonthStart(month))
                panel.SetSnapshot(snapshot);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Keep the last displayed snapshot; never disclose response bodies. */ }
        finally
        {
            if (!_closing && request == _calendarMonthRequest && ReferenceEquals(panel, _calendarDetails) && !panel.IsDisposed)
                panel.SetRefreshing(false);
        }
    }

    private void UpdateCalendarPointer(CardHitTarget? hit, Point screenPoint, DateTime now)
    {
        if (_catalog?.Settings.Current.ShowCalendarCard != true || !_sidebarVisible) { CloseCalendarDetails(); return; }
        if (CalendarPanelOpen && (NativeMethods.GetAsyncKeyState(0x1B) & 0x8001) != 0) { CloseCalendarDetails(); return; }
        switch (_calendarPopup.Update(hit?.IsCalendarCard == true, CalendarPointerInside(screenPoint),
            CalendarPanelOpen && _calendarCorridor.Contains(screenPoint), CalendarPanelOpen, now))
        {
            case CalendarPopupAction.Open: OpenCalendarDetails(hit); break;
            case CalendarPopupAction.KeepOpen: _calendarDetails?.KeepOpen(); break;
            case CalendarPopupAction.FadeOut: _calendarDetails?.FadeOut(); break;
        }
    }

    private void DismissCalendarOnOutsideClick(Point screenPoint)
    {
        if (CalendarPanelOpen && !CalendarPointerInside(screenPoint) &&
            _renderer?.HitTest(PointToClient(screenPoint))?.IsCalendarCard != true) CloseCalendarDetails();
    }

    private void CancelCalendarPanelRequests()
    {
        ++_calendarMonthRequest;
        _calendarPanelCancellation?.Cancel();
        _calendarPanelCancellation?.Dispose();
        _calendarPanelCancellation = null;
    }

    private void CloseCalendarDetails()
    {
        _calendarPopup.Dismiss();
        CancelCalendarPanelRequests();
        _calendarCorridor = Rectangle.Empty;
        var panel = _calendarDetails;
        _calendarDetails = null;
        if (panel is not null) { panel.Close(); panel.Dispose(); }
    }

    private void DisposeCalendarHost()
    {
        SystemEvents.TimeChanged -= CalendarTimeChanged;
        _calendarTimer.Stop();
        _calendarTimer.Tick -= CalendarTimerTick;
        _calendarTimer.Dispose();
        SystemEvents.PowerModeChanged -= CalendarPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged -= CalendarNetworkChanged;
        _calendarService.Updated -= CalendarServiceUpdated;
        CloseCalendarDetails();
        _calendarCancellation.Cancel();
        _calendarService.Dispose();
        _calendarCancellation.Dispose();
    }
}
