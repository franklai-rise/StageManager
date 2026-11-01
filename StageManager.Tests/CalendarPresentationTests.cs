using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.ExceptionServices;
using StageManager.Card3DPrototype.AiUsage;
using StageManager.Card3DPrototype.Calendar;
using StageManager.Settings;

internal static class CalendarPresentationTests
{
    public static void Run()
    {
        var callerContext = SynchronizationContext.Current;
        OnSta(RunCore);
        if (!ReferenceEquals(callerContext, SynchronizationContext.Current))
            throw new InvalidOperationException("Calendar tests changed the caller synchronization context");
    }
    private static void OnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        }) { IsBackground = true, Name = "Calendar UI verification" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("Calendar UI verification exceeded 30 seconds");
        failure?.Throw();
    }
    private static void RunCore()
    {
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        var date = new DateOnly(2026, 10, 25);
        var start = CalendarDates.LocalBoundary(date);
        var now = start.AddHours(12);
        CalendarEvent Event(string id, DateTimeOffset a, DateTimeOffset b, bool allDay = false) =>
            new(id, "calendar", "Meeting " + id, a, b, allDay, date, date.AddDays(1), "Room", "Organizer", "Plain text preview", "https://outlook.office.com/calendar/item/1");
        var ended = Event("ended", start.AddHours(8), start.AddHours(9));
        var ongoing = Event("ongoing", start.AddHours(11), start.AddHours(13));
        var future = Event("future", start.AddHours(15), start.AddHours(16));
        var allDay = Event("all", start, start.AddDays(1), true);
        var snapshot = new CalendarSnapshot(CalendarStatus.Connected, new[] { ended, ongoing, future, allDay }, now);
        Check(CalendarDates.GridStart(date).DayOfWeek == DayOfWeek.Monday, "Monday grid");
        Check(CalendarDates.GridStart(new DateOnly(2026, 2, 1)) == new DateOnly(2026, 1, 26), "Previous-month grid");
        Check(CalendarDates.OnDay(allDay, date) && !CalendarDates.OnDay(allDay, date.AddDays(1)), "All-day exclusive end");
        Check(!CalendarDates.OnDay(Event("midnight", start.AddHours(-1), start), date), "Timed exclusive end");
        Check(CalendarDates.OnDay(Event("overnight", start.AddHours(-1), start.AddHours(1)), date), "Overnight event");
        Check(CalendarPresentation.IsEnded(ended, now) && !CalendarPresentation.IsEnded(ongoing, now), "Ended timed status");
        Check(!CalendarPresentation.IsEnded(allDay, now) && CalendarPresentation.IsEnded(allDay, start.AddDays(1)), "All-day ended at exclusive end date");
        Check(CalendarDates.MonthStart(new DateOnly(2024, 2, 29)).AddMonths(1) == new DateOnly(2024, 3, 1), "Leap month");
        Check(CalendarPresentation.TodayItems(snapshot, now).Select(e => e.Id).SequenceEqual(new[] { "all", "ongoing", "future" }), "Today filtering/order");
        Check(CalendarPresentation.TodayItems(snapshot with { Status = CalendarStatus.Loading }, now).Length == 0, "Loading hides old events");
        Check(CalendarPresentation.TryOutlookLink("https://outlook.live.com/calendar/", out _), "Trusted host");
        foreach (var link in new[] { "http://outlook.live.com/", "https://outlook.office.com.evil.test/", "https://outlook.office.com@evil.test/", "file:///x", "https://outlook.office.com:444/" })
            Check(!CalendarPresentation.TryOutlookLink(link, out _), "Untrusted link: " + link);
        var t = new DateTime(2026, 10, 25);
        var popup = new CalendarPopupController();
        Check(popup.Update(true, false, false, false, t) == CalendarPopupAction.None, "Hover begins");
        Check(popup.Update(true, false, false, false, t.AddMilliseconds(299)) == CalendarPopupAction.None, "Hover threshold");
        Check(popup.Update(true, false, false, false, t.AddMilliseconds(300)) == CalendarPopupAction.Open, "Hover opens");
        Check(popup.Update(false, false, true, true, t) == CalendarPopupAction.KeepOpen, "Corridor keeps open");
        Check(popup.Update(false, false, false, true, t) == CalendarPopupAction.None, "Leave begins");
        Check(popup.Update(false, false, false, true, t.AddMilliseconds(299)) == CalendarPopupAction.None, "Leave threshold");
        Check(popup.Update(false, false, false, true, t.AddMilliseconds(300)) == CalendarPopupAction.FadeOut, "Leave fades");
        popup.Dismiss();
        Check(popup.Update(true, false, false, false, t.AddSeconds(1)) == CalendarPopupAction.None, "Dismiss suppresses hover");
        popup.Click();
        Check(popup.Update(true, false, false, false, t) == CalendarPopupAction.Open, "Click opens immediately");
        var language = default(UiLanguage);
        var size = new Size(196, 36);
        var key = CalendarPresentation.RenderKey(size, language, now);
        Check(key != CalendarPresentation.RenderKey(size, language, now.AddDays(1)), "Midnight invalidation");
        Check(key == CalendarPresentation.RenderKey(size, language, now.AddMinutes(30)), "Date-only card must not repaint for the clock or agenda updates");
        Check(CalendarPresentation.CompactDate(now, UiLanguage.SimplifiedChinese) == "10月25日", "Compact local Chinese date");
        Check(CalendarPresentation.CompactDate(now, UiLanguage.English) == "Oct 25", "Compact local English date");
        foreach (var lang in Enum.GetValues<UiLanguage>())
        foreach (var dimensions in new[] { size, new Size(118, 36), new Size(147, 45), new Size(236, 72), new Size(360, 160) })
        {
            using var bitmap = new Bitmap(dimensions.Width, dimensions.Height);
            using var g = Graphics.FromImage(bitmap);
            CalendarPresentation.PaintCard(g, dimensions, lang, now);
            var pixel = bitmap.GetPixel(0, 0);
            Check(pixel.A == CalendarPresentation.CardPaper.A && pixel.R < 50 && pixel.B > pixel.R, "Shortcut-matching dark translucent card surface");
        }
        // Isolated STA; the scroll check briefly realizes the nonactivating form offscreen. No network/files.
        using var form = new CalendarDetailsForm(language, false);
        Check(form.BackColor == AiUsagePresentation.PanelBackground && form.ForeColor == AiUsagePresentation.PanelForeground, "Calendar must share the AI flyout palette");
        Check(form.ClientSize == new Size(CalendarDetailsForm.PreferredWidth, CalendarDetailsForm.PreferredHeight), "Taller preferred panel size");
        Check(form.Region is not null && !form.Region.IsVisible(0, 0) && form.Region.IsVisible(form.Width / 2, 0), "Rounded outer flyout geometry");
        var refreshButton = form.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .First(f => f.Name == "_refresh").GetValue(form) as System.Windows.Forms.Button;
        Color ButtonPixel()
        {
            using var buttonBitmap = new Bitmap(refreshButton!.Width, refreshButton.Height);
            refreshButton.DrawToBitmap(buttonBitmap, new Rectangle(Point.Empty, buttonBitmap.Size));
            return buttonBitmap.GetPixel(buttonBitmap.Width / 2, 5);
        }
        var normalButton = ButtonPixel();
        using var aiButton = new AiUsageButton { Text = refreshButton!.Text, BackColor = form.BackColor,
            ForeColor = AiUsagePresentation.ButtonForeground, Size = refreshButton.Size };
        using var aiButtonBitmap = new Bitmap(aiButton.Width, aiButton.Height);
        aiButton.DrawToBitmap(aiButtonBitmap, new Rectangle(Point.Empty, aiButtonBitmap.Size));
        Check(normalButton == aiButtonBitmap.GetPixel(aiButton.Width / 2, 5), "Calendar actions reuse actual AI button rendering");
        refreshButton!.GetType().GetMethod("OnMouseEnter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(refreshButton, new object[] { EventArgs.Empty });
        var hoverButton = ButtonPixel();
        refreshButton.GetType().GetMethod("OnMouseDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(refreshButton,
            new object[] { new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left, 1, 3, 3, 0) });
        var pressedButton = ButtonPixel();
        Check(normalButton != hoverButton && hoverButton != pressedButton, "AI-style buttons need distinct hover and press feedback");
        refreshButton.GetType().GetMethod("OnMouseLeave", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(refreshButton, new object[] { EventArgs.Empty });
        Check(ButtonPixel() == normalButton, "Button press feedback stuck after pointer leave");
        form.SetSnapshot(snapshot);
        int requests = 0; form.MonthRequested += _ => requests++;
        form.SelectDate(CalendarDates.MonthStart(form.DisplayMonth).AddDays(1));
        Check(requests == 0, "Day selection must not request a month");
        var next = form.DisplayMonth.AddMonths(1);
        form.SelectDate(next);
        Check(requests == 1 && form.DisplayMonth == next && form.SelectedDate == next, "Month navigation");
        IEnumerable<System.Windows.Forms.Control> Descendants(System.Windows.Forms.Control parent) =>
            parent.Controls.Cast<System.Windows.Forms.Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
        Check(Descendants(form).Any(c => c.Text == CalendarPresentation.StatusText(CalendarSnapshot.Empty(CalendarStatus.Loading), language)), "Month change shows loading");
        Check(!Descendants(form).Any(c => c.Text == "No events on this day"), "Pending month is not falsely empty");
        form.FitToWorkingArea(new Rectangle(-1000, 0, 900, 1000), 1.25f);
        // Form.SetBoundsCore also enforces the real session's MaxWindowTrackSize,
        // even when this test supplies a synthetic larger/negative monitor area.
        var nativeLimit = System.Windows.Forms.SystemInformation.MaxWindowTrackSize;
        var expectedSize = new Size(Math.Min(525, nativeLimit.Width), Math.Min(925, nativeLimit.Height));
        Console.WriteLine($"Calendar DPI viewport: {form.Size}; expected {expectedSize}; native limit {nativeLimit}.");
        Check(form.Size == expectedSize && form.Left >= -1000 && form.Right <= -100 && form.Top >= 0 && form.Bottom <= 1000,
            "DPI and work area must respect native window limits");
        using var panel = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(panel, new Rectangle(Point.Empty, panel.Size));
        Check(Descendants(form).Count() >= 55, "Native calendar child controls");
        foreach (var status in new[] { CalendarStatus.AuthRequired, CalendarStatus.NotConfigured, CalendarStatus.NotConnected, CalendarStatus.Disabled })
        {
            form.SetSnapshot(CalendarSnapshot.Empty(status)); form.SelectDate(form.DisplayMonth.AddMonths(1));
            Check(Descendants(form).Any(c => c.Text == CalendarPresentation.StatusText(CalendarSnapshot.Empty(status), language)), "Navigation preserves account status");
        }
        form.SelectDate(date); form.SetSnapshot(snapshot with { Status = CalendarStatus.Stale, Events = Array.Empty<CalendarEvent>() });
        Check(Descendants(form).Any(c => c.Text == CalendarPresentation.EmptyDayText(CalendarSnapshot.Empty(CalendarStatus.Stale), language)), "Stale unknown day is unavailable, not empty");
        Check(!CalendarPresentation.EmptyDayText(snapshot with { Status = CalendarStatus.Stale }, language, true).Contains("All done"), "Stale does not claim finished");
        form.SetSnapshot(snapshot);
        var completed = new HashSet<string> { CalendarCompletionStore.Key(allDay) };
        int completionChanges = 0;
        form.CompletionChanged += (item, value) => { Check(item.Id == allDay.Id, "Completion event identity"); completionChanges++; };
        form.SetCompletedEvents(completed);
        Check(completionChanges == 0, "External marks do not emit completion changes");
        var checks = Descendants(form).OfType<System.Windows.Forms.CheckBox>().ToArray();
        Check(checks.Length == 4 && checks[0].Checked, "Every agenda event has checkbox in original order");
        Check((checks[0].AccessibilityObject.State & System.Windows.Forms.AccessibleStates.Checked) != 0, "Accessible checked state");
        checks[0].Checked = false;
        Check(completionChanges == 1, "Checkbox emits local completion change");
        Check(completed.Contains(CalendarCompletionStore.Key(allDay)), "UI does not mutate supplied persisted set");
        checks[0].Checked = true;
        Check(completionChanges == 2, "Checkbox toggles both ways");
        form.SetCompletedEvents(completed);
        Check(ReferenceEquals(checks[0], Descendants(form).OfType<System.Windows.Forms.CheckBox>().First()), "Persistence echo must not replace the animated checkbox");
        var tickControl = (CompletionCheckBox)checks[0];
        Color TickPixel()
        {
            using var image = new Bitmap(tickControl.Width, tickControl.Height);
            tickControl.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            return image.GetPixel(image.Width / 2, image.Height / 2 - (int)(7 * tickControl.RenderScale));
        }
        var checkedColor = TickPixel();
        form.SetCompletedEvents(new HashSet<string>());
        var uncheckedColor = TickPixel();
        Check(checkedColor != uncheckedColor && completionChanges == 2, "Custom rounded tick reflects external state without writing back");
        tickControl.GetType().GetMethod("OnMouseEnter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(tickControl, new object[] { EventArgs.Empty });
        Check(TickPixel() != uncheckedColor, "Tick hover feedback");
        tickControl.GetType().GetMethod("OnMouseLeave", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(tickControl, new object[] { EventArgs.Empty });
        Check(TickPixel() == uncheckedColor, "Tick leave resets feedback");
        form.SetCompletedEvents(completed);
        Check(Descendants(form).OfType<System.Windows.Forms.Button>().Any(b => b.AccessibleName?.Contains("Meeting all") == true && b.ForeColor == CalendarPresentation.Muted), "Completion immediately mutes expandable row");
        form.SetSnapshot(snapshot);
        Check(Descendants(form).OfType<System.Windows.Forms.CheckBox>().First().Checked && completionChanges == 2, "Refresh retains local completion without emitting a write");
        var reallyEnded = ended with { Start = DateTimeOffset.Now.AddHours(-2), End = DateTimeOffset.Now.AddHours(-1) };
        form.SelectDate(DateOnly.FromDateTime(reallyEnded.Start.LocalDateTime));
        form.SetSnapshot(snapshot with { Events = new[] { reallyEnded } });
        Check(Descendants(form).OfType<System.Windows.Forms.Button>().Any(b => b.AccessibleName?.Contains("Meeting ended") == true && b.ForeColor == CalendarPresentation.Muted), "Ended uncompleted row is muted");
        foreach (var status in Enum.GetValues<CalendarStatus>())
        {
            form.SetSnapshot(CalendarSnapshot.Empty(status));
            Check(Descendants(form).OfType<System.Windows.Forms.Button>().Any(b => b.Text == "Open Outlook" && b.Enabled), "Outlook entry available in every state");
            if (status != CalendarStatus.Connected)
                Check(!Descendants(form).Any(c => c.Text == "No events on this day" || c.Text == "All done for today"), "Unavailable state never claims zero events");
        }
        form.SelectDate(date);
        var many = snapshot with { Events = Enumerable.Range(0, 25).Select(i => ongoing with { Id = "scroll-" + i }).ToArray() };
        form.SetSnapshot(many);
        form.Location = new Point(-32000, -32000); form.Show(); form.Update();
        var agenda = Descendants(form).OfType<System.Windows.Forms.Panel>().Single(p => p.AccessibleName == "Calendar agenda");
        agenda.AutoScrollPosition = new Point(0, 250);
        Check(agenda.AutoScrollPosition.Y < 0, "Agenda scroll test established nonzero offset");
        var position = agenda.AutoScrollPosition;
        form.SetCompletedEvents(new HashSet<string>());
        Check(agenda.AutoScrollPosition == position, "Completion preserves agenda scroll");
        form.SetSnapshot(many);
        Check(agenda.AutoScrollPosition == position, "Snapshot refresh preserves agenda scroll");
        form.SelectDate(date);
        Check(agenda.AutoScrollPosition == position, "Selecting same date preserves agenda scroll");
        form.SetSnapshot(CalendarSnapshot.Empty(CalendarStatus.Loading)); form.SetSnapshot(many);
        Check(agenda.AutoScrollPosition == position, "Temporary loading preserves agenda scroll");
        form.SelectDate(date.AddDays(1));
        Check(agenda.AutoScrollPosition == Point.Empty, "Changing selected date resets agenda scroll");
        form.Hide();
        form.FitToWorkingArea(new Rectangle(-500, 10, 320, 400), 2);
        Check(form.Width == 320 && form.Height == 400 && Math.Abs(form.Font.Size - 26) < .01, "Constrained work area preserves DPI text");
        Check(Descendants(form).OfType<System.Windows.Forms.Panel>().Any(p => p.AutoScroll && p.Controls.Cast<System.Windows.Forms.Control>().Any(c => c.Width == 836 && c.Height == 1476)), "Constrained area scrolls full-size surface");
    }

    // Explicit opt-in only. The normal Run() never writes files.
    public static void Preview(string path)
        => OnSta(() => PreviewCore(path));

    private static void PreviewCore(string path)
    {
        using var bitmap = new Bitmap(780, 780, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.FromArgb(86, 107, 131));
        var now = DateTimeOffset.Now;
        var day = DateOnly.FromDateTime(now.LocalDateTime);
        var first = new CalendarEvent("preview-one", "preview", "项目设计讨论", now.AddMinutes(-15), now.AddHours(1), false, day, day.AddDays(1), "会议室", "团队", "演示用日程，不是真实账户数据", "https://outlook.office.com/calendar/");
        var second = first with { Id = "preview-two", Subject = "整理本周笔记", Start = now.AddHours(2), End = now.AddHours(3) };
        var earlier = first with { Id = "preview-ended", Subject = "晨间计划", Start = now.AddHours(-2), End = now.AddHours(-1) };
        var snapshot = new CalendarSnapshot(CalendarStatus.Connected, new[] { earlier, first, second }, now);
        var completed = new HashSet<string> { CalendarCompletionStore.Key(first) };
        using var card = new Bitmap(196, 36, PixelFormat.Format32bppPArgb);
        using (var cardGraphics = Graphics.FromImage(card)) CalendarPresentation.PaintCard(cardGraphics, card.Size, UiLanguage.SimplifiedChinese, now);
        var saved = g.Save();
        using (var cardShape = AiUsagePresentation.Rounded(new RectangleF(28, 30, 196, 36), 8))
        { g.SetClip(cardShape); g.DrawImageUnscaled(card, 28, 30); }
        g.Restore(saved);
        using var form = new CalendarDetailsForm(UiLanguage.SimplifiedChinese, false) { ManualRefresh = true };
        form.SetSnapshot(snapshot);
        form.SetCompletedEvents(completed);
        using var panel = new Bitmap(form.Width, form.Height, PixelFormat.Format24bppRgb);
        // WinForms omits hidden native children. This explicit preview briefly shows a nonactivating form.
        form.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
        form.Location = new Point(-32000, -32000);
        try { form.Show(); form.Update(); form.DrawToBitmap(panel, new Rectangle(Point.Empty, panel.Size)); }
        finally { form.Hide(); }
        saved = g.Save();
        using (var outline = AiUsagePresentation.Rounded(new RectangleF(310, 20, panel.Width, panel.Height), AiUsagePresentation.PanelRadius))
        { g.SetClip(outline); g.DrawImageUnscaled(panel, 310, 20); }
        g.Restore(saved);
        bitmap.Save(path, ImageFormat.Png);
    }
}
