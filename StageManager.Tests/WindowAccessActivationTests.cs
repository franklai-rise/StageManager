using StageManager.Native;

// Deliberately not registered in Program.cs: this file is independently callable by main's
// runner or reflection. No module initializer and no live focus/input manipulation.
internal static class WindowAccessActivationTests
{
    public static async Task<int> RunAll()
    {
        var count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            count++;
        }

        Check(WindowAccess.Compare(null, 0x2000) == WindowIntegrityRelation.Unknown, "Target query failure must be Unknown.");
        Check(WindowAccess.Compare(0x3000, null) == WindowIntegrityRelation.Unknown, "Our query failure must be Unknown.");
        Check(WindowAccess.Compare(0x3000, 0x2000) == WindowIntegrityRelation.Higher, "High vs medium was not higher.");
        Check(WindowAccess.Compare(0x3000, 0x3000) == WindowIntegrityRelation.Equal, "Two high integrity processes are peers.");
        Check(WindowAccess.Compare(0x2000, 0x3000) == WindowIntegrityRelation.Lower, "Medium vs high was not lower.");
        Check(WindowAccess.Compare(0, 0x2000) == WindowIntegrityRelation.Lower, "Untrusted RID zero is known, not missing.");
        Check(WindowAccess.Compare(0x2100, 0x2000) == WindowIntegrityRelation.Higher, "Compare RIDs, not coarse medium/high buckets.");
        Check(WindowAccess.Describe(new(null, 0x2000), false).Badge == "ACCESS ?", "Unknown badge is missing.");
        Check(WindowAccess.Describe(new(0x3000, 0x3000), false).Badge == "", "Peer elevated processes must not get a higher badge.");
        Check(WindowAccess.Describe(new(0x3000, 0x2000), true).Description.Contains("不会自动提权"), "Safe Chinese permission guidance missing.");
        var badges = WindowAccess.GetBadges(new(0x3000, 0x2000), "Current minimized label", false);
        Check(badges.MinimizedBadge == "Current minimized label" && badges.PermissionBadge == "ADMIN",
            "Permission badges must coexist with the existing minimized label unchanged.");
        badges = WindowAccess.GetBadges(new(null, 0x2000), null, false);
        Check(badges.MinimizedBadge is null && badges.PermissionBadge == "ACCESS ?",
            "An unknown permission badge must not introduce a minimized badge.");
        Check(WindowAccess.Query(IntPtr.Zero).Relation == WindowIntegrityRelation.Unknown, "Invalid HWND must remain Unknown.");
        Check(WindowAccess.ReadIntegrity((uint)Environment.ProcessId).HasValue, "Read-only current-token native smoke test failed.");

        var native = new FakeOperation();
        var result = await WindowActivation.ActivateAsync(native, default);
        Check(result.Outcome == WindowOperationOutcome.NotConfirmed && native.Activations.SequenceEqual(new[] { true, false }),
            "Visible/non-iconic alone is not foreground; retry must not restore twice.");

        native = new() { State = FakeOperation.Initial with { Iconic = true } };
        native.OnDelay = n => n.State = n.State with { Iconic = false };
        result = await WindowActivation.ActivateAsync(native, default);
        Check(!result.Succeeded && native.Activations.Count == 2, "Clearing WS_MINIMIZE cannot confirm activation.");

        native = new();
        native.OnDelay = n => n.State = n.State with { ForegroundForTarget = true, Foreground = (IntPtr)20 };
        result = await WindowActivation.ActivateAsync(native, default);
        Check(result.Succeeded && native.Activations.Count == 1, "An owned foreground dialog should confirm activation.");

        native = new();
        native.OnDelay = n => n.State = n.State with { Foreground = (IntPtr)99 };
        var trayCalls = 0;
        result = await WindowActivation.ActivateAsync(native, default, _ => { trayCalls++; return Task.FromResult(true); });
        Check(result.Outcome == WindowOperationOutcome.Abandoned && native.Activations.Count == 1 && trayCalls == 0,
            "Human foreground switch must prevent native retry and tray fallback.");

        native = new();
        native.OnDelay = n => n.State = n.State with { LastInput = 124 };
        result = await WindowActivation.ActivateAsync(native, default);
        Check(result.Outcome == WindowOperationOutcome.Abandoned && native.Activations.Count == 1,
            "New input while the old foreground remains must stop retry.");

        native = new() { State = FakeOperation.Initial with { LastInput = null } };
        result = await WindowActivation.ActivateAsync(native, default);
        Check(result.Outcome == WindowOperationOutcome.Abandoned && native.Activations.Count == 1,
            "Unknown input timestamp permits only the immediate user request, not retries.");

        native = new();
        native.OnDelay = n => n.State = n.State with { ProcessId = 99 };
        result = await WindowActivation.ActivateAsync(native, default);
        Check(result.Outcome == WindowOperationOutcome.TargetGone && native.Activations.Count == 1,
            "Recycled HWND with a different owner must not receive a retry.");

        native = new();
        result = await WindowActivation.ActivateAsync(native, default, _ => Task.FromResult(true));
        Check(!result.Succeeded && native.Delays == 15, "Successful tray invocation must still verify actual foreground.");

        native = new();
        result = await WindowActivation.ActivateAsync(native, default, _ => Task.FromResult(true), preferTrayRestore: true);
        Check(!result.Succeeded && native.Activations.SequenceEqual(new[] { false, false }),
            "VPN/Clash tray-first activation must not race the app with another restore.");

        native = new();
        var trayToken = default(CancellationToken);
        var worker = new TaskCompletionSource<bool>();
        native.OnDelay = n => n.State = n.State with { Foreground = (IntPtr)99 };
        result = await WindowActivation.ActivateAsync(native, default, token =>
        { trayToken = token; return worker.Task; }, preferTrayRestore: true);
        Check(result.Outcome == WindowOperationOutcome.Abandoned && trayToken.IsCancellationRequested && native.Activations.Count == 0,
            "A user switch must cancel an in-flight tray request and suppress native fallback.");
        worker.SetResult(false);

        native = new();
        worker = new();
        result = await WindowActivation.ActivateAsync(native, default, token =>
        { trayToken = token; return worker.Task; }, preferTrayRestore: true);
        Check(result.Outcome == WindowOperationOutcome.TimedOut && trayToken.IsCancellationRequested && native.Delays == 225,
            "A non-cooperative tray callback must have a nine-second bounded wait.");
        worker.SetResult(false);

        Check(NativeWindowOperation.IsKnownTrayClass("Shell_TrayWnd") &&
            NativeWindowOperation.IsKnownTrayClass("Shell_SecondaryTrayWnd") &&
            NativeWindowOperation.IsKnownTrayClass("NotifyIconOverflowWindow") &&
            NativeWindowOperation.IsKnownTrayClass("TopLevelWindowForOverflowXamlIsland"),
            "Known exact tray/overflow classes must be recognized before OS owner/topology validation.");
        Check(!NativeWindowOperation.IsKnownTrayClass("CabinetWClass") &&
            !NativeWindowOperation.IsKnownTrayClass("Progman") &&
            !NativeWindowOperation.IsKnownTrayClass("WorkerW") &&
            !NativeWindowOperation.IsKnownTrayClass("shell_traywnd") &&
            !NativeWindowOperation.IsKnownTrayClass("FakeShell_TrayWnd"),
            "Explorer folder/desktop windows and approximate class names must never be exempted.");

        native = new();
        worker = new();
        native.OnDelay = n =>
        {
            n.State = n.State with { Foreground = (IntPtr)99, CooperativeTrayForeground = true };
            if (n.Delays == 201)
            {
                worker.SetResult(true);
                n.State = n.State with { Foreground = (IntPtr)20, ForegroundForTarget = true,
                    CooperativeTrayForeground = false };
            }
        };
        result = await WindowActivation.ActivateAsync(native, default, _ => worker.Task, preferTrayRestore: true);
        Check(result.Succeeded && native.Delays == 201 && native.Activations.Count == 0,
            "Verified tray transitions must survive beyond eight seconds and confirm only the target foreground.");

        native = new();
        native.OnDelay = n =>
        {
            if (n.Activations.Count > 0)
                n.State = n.State with { Foreground = (IntPtr)20, ForegroundForTarget = true,
                    CooperativeTrayForeground = false };
        };
        result = await WindowActivation.ActivateAsync(native, default, _ =>
        {
            native.State = native.State with { Foreground = (IntPtr)99, CooperativeTrayForeground = true };
            return Task.FromResult(true);
        }, preferTrayRestore: true);
        Check(result.Succeeded && native.Activations.SequenceEqual(new[] { false }),
            "Tray-first native retry must survive a verified open panel without issuing SW_RESTORE.");

        foreach (var switchKind in new[] { "input", "unrelated", "unknown-input", "input-and-target" })
        {
            native = new();
            worker = new();
            native.OnDelay = n =>
            {
                n.State = n.State with { Foreground = (IntPtr)99, CooperativeTrayForeground = true };
                if (n.Delays < 2) return;
                n.State = switchKind switch
                {
                    "input" => n.State with { LastInput = 124 },
                    "unrelated" => n.State with { Foreground = (IntPtr)98, CooperativeTrayForeground = false },
                    "unknown-input" => n.State with { LastInput = null },
                    _ => n.State with { LastInput = 124, ForegroundForTarget = true }
                };
            };
            result = await WindowActivation.ActivateAsync(native, default, token =>
            { trayToken = token; return worker.Task; }, preferTrayRestore: true);
            Check(result.Outcome == WindowOperationOutcome.Abandoned && trayToken.IsCancellationRequested &&
                native.Activations.Count == 0, $"Cooperative panel must not suppress abandonment for {switchKind}.");
            worker.SetResult(false);
        }

        native = new();
        native.OnDelay = n => n.State = n.State with { Foreground = (IntPtr)99, CooperativeTrayForeground = true };
        result = await WindowActivation.ActivateAsync(native, default);
        Check(result.Outcome == WindowOperationOutcome.Abandoned && native.Activations.Count == 1,
            "Ordinary activation must not acquire the cooperative tray exemption.");

        native = new();
        native.OnDelay = n => n.State = n.State with { Foreground = (IntPtr)99, CooperativeTrayForeground = true };
        result = await WindowActivation.ActivateAsync(native, default, _ => Task.FromResult(true), preferTrayRestore: true);
        Check(result.Outcome == WindowOperationOutcome.NotConfirmed && native.Activations.SequenceEqual(new[] { false, false }),
            "An open verified tray panel is not activation success and retries must remain bounded.");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        native = new();
        result = await WindowActivation.ActivateAsync(native, cancelled.Token);
        Check(result.Outcome == WindowOperationOutcome.Cancelled && native.Activations.Count == 0,
            "A cancelled operation must not request focus.");

        native = new();
        result = await WindowActivation.MinimizeAsync(native, default);
        Check(result.Outcome == WindowOperationOutcome.NotConfirmed && native.Minimizes == 1 && native.Delays == 10,
            "PostMessage true is only a queued minimize request, not successful minimization.");

        native = new() { MinimizeAccepted = false };
        result = await WindowActivation.MinimizeAsync(native, default);
        Check(result.Outcome == WindowOperationOutcome.RequestRejected && result.NativeError == 5 && native.Delays == 0,
            "Rejected minimize must preserve its immediate native error.");

        native = new();
        native.OnDelay = n => n.State = n.State with { Iconic = true, Foreground = (IntPtr)99 };
        result = await WindowActivation.MinimizeAsync(native, default);
        Check(result.Outcome == WindowOperationOutcome.Confirmed, "Real minimization naturally changes foreground and must succeed.");

        native = new();
        native.OnDelay = n => n.State = n.State with { Visible = false };
        result = await WindowActivation.MinimizeAsync(native, default);
        Check(result.Outcome == WindowOperationOutcome.Hidden && result.Succeeded, "Minimize-to-tray must be recognized as hidden, not iconic.");

        native = new() { State = FakeOperation.Initial with { Visible = false } };
        result = await WindowActivation.MinimizeAsync(native, default);
        Check(!result.Succeeded && native.Minimizes == 0, "Already hidden is not a confirmed new minimize-to-tray transition.");

        native = new();
        native.OnDelay = n => n.State = n.State with { Exists = false, Visible = false };
        result = await WindowActivation.MinimizeAsync(native, default);
        Check(result.Outcome == WindowOperationOutcome.TargetGone, "Destroyed windows must not be reported as minimized/hidden.");

        native = new();
        result = await WindowActivation.MinimizeAsync(native, cancelled.Token);
        Check(result.Outcome == WindowOperationOutcome.Cancelled && native.Minimizes == 0,
            "Cancelled minimize must not post a message.");

        count += await CheckAccessCache();
        Console.WriteLine($"WindowAccessActivationTests: {count} checks passed.");
        return count;
    }

    private static async Task<int> CheckAccessCache()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        long now = 100;
        uint owner = 10;
        var queries = 0;
        var answer = new WindowAccessInfo(0x3000, 0x2000);
        var cache = new WindowAccessCache(_ => owner, _ => { queries++; return answer; }, () => now);
        var hwnd = (IntPtr)1;
        Check(cache.GetCached(hwnd).Relation == WindowIntegrityRelation.Unknown && queries == 0,
            "Cache miss must not open process tokens.");
        Check(cache.Refresh(hwnd) == answer && queries == 1 && cache.Count == 1, "Refresh must populate the cache.");
        now = 5099;
        Check(cache.GetCached(hwnd) == answer && cache.Refresh(hwnd) == answer && queries == 1,
            "Fresh entries must avoid redundant token queries.");
        now = 5100;
        Check(cache.GetCached(hwnd).Relation == WindowIntegrityRelation.Unknown && queries == 1,
            "Expiry at exactly five seconds must return Unknown without a token query.");
        Check(cache.Refresh(hwnd) == answer && queries == 2, "Expired entries must refresh.");
        owner = 11;
        Check(cache.GetCached(hwnd).Relation == WindowIntegrityRelation.Unknown, "Recycled HWND must not reuse another PID's access.");
        answer = new(0x2000, 0x2000);
        Check(cache.Refresh(hwnd) == answer && queries == 3 && cache.Count == 1,
            "A new owner must replace the obsolete HWND/PID entry.");
        owner = 0;
        Check(cache.GetCached(hwnd).Relation == WindowIntegrityRelation.Unknown &&
            cache.Refresh(hwnd).Relation == WindowIntegrityRelation.Unknown && queries == 3,
            "Failed owner lookup must not reuse a cached privilege classification.");
        Check(cache.Refresh(IntPtr.Zero).Relation == WindowIntegrityRelation.Unknown && queries == 3,
            "Null HWND must not query tokens.");

        owner = 10;
        queries = 0;
        cache = new(_ => owner, _ => { queries++; return default; }, () => now);
        cache.Refresh(hwnd);
        cache.Refresh(hwnd);
        Check(queries == 1 && cache.GetCached(hwnd).Relation == WindowIntegrityRelation.Unknown,
            "Unknown query results must be cached to avoid hammering inaccessible processes.");

        cache = new(_ => owner, _ => { owner++; return new(0x3000, 0x2000); }, () => now);
        Check(cache.Refresh(hwnd).Relation == WindowIntegrityRelation.Unknown && cache.Count == 0,
            "Owner changes during token inspection must discard the result.");

        cache = new(_ => 10, _ => { now += 5000; return new(0x3000, 0x2000); }, () => now);
        Check(cache.Refresh(hwnd).Relation == WindowIntegrityRelation.Unknown && cache.Count == 0,
            "Slow token inspection must not extend freshness beyond five seconds from query start.");

        now = 0;
        cache = new(_ => 10, _ => new(0x3000, 0x2000), () => now);
        for (var i = 1; i <= 513; i++) { cache.Refresh((IntPtr)i); now++; }
        Check(cache.Count == 512 && cache.GetCached((IntPtr)1).Relation == WindowIntegrityRelation.Unknown &&
            cache.GetCached((IntPtr)513).Relation == WindowIntegrityRelation.Higher,
            "Capacity must evict the oldest entry without exceeding 512.");
        now = 10000;
        cache.Refresh((IntPtr)600);
        Check(cache.Count == 1, "Refresh must reclaim expired entries.");

        cache = new(_ => 10, _ => new(0x3000, 0x2000), () => 0);
        Parallel.For(1, 1025, i => cache.Refresh((IntPtr)i));
        Check(cache.Count == 512, "Concurrent refreshes must retain the strict 512-entry bound.");

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        cache = new(_ => 10, _ =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Cache test query was not released.");
            return new(0x3000, 0x2000);
        }, () => 0);
        var refresh = Task.Run(() => cache.Refresh(hwnd));
        try
        {
            if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Cache test query did not start.");
            var cached = await Task.Run(() => cache.GetCached(hwnd)).WaitAsync(TimeSpan.FromSeconds(1));
            Check(cached.Relation == WindowIntegrityRelation.Unknown,
                "GetCached must not wait for a token query running on another thread.");
        }
        finally { release.Set(); }
        await refresh;
        Check(cache.GetCached(hwnd).Relation == WindowIntegrityRelation.Higher, "Completed background refresh must publish its result.");
        Check(WindowAccess.Describe(new(0x3000, 0x2000), true).Badge == "ADMIN" &&
            WindowAccess.Describe(new(null, 0x2000), true).Badge == "ACCESS ?",
            "Both languages must use compact permission badge labels.");
        return checks;
    }

    private sealed class FakeOperation : IWindowOperation
    {
        internal static WindowOperationSnapshot Initial => new(true, 10, 11, true, false, (IntPtr)1, false, 123);
        internal WindowOperationSnapshot State = Initial;
        internal List<bool> Activations = [];
        internal int Minimizes;
        internal int Delays;
        internal bool MinimizeAccepted = true;
        internal Action<FakeOperation>? OnDelay;
        public WindowOperationSnapshot Read() => State;
        public void RequestActivation(bool restoreIfMinimized) => Activations.Add(restoreIfMinimized);
        public (bool Accepted, int Error) RequestMinimize()
        {
            Minimizes++;
            return (MinimizeAccepted, MinimizeAccepted ? 0 : 5);
        }
        public Task Delay(int milliseconds, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Delays++;
            OnDelay?.Invoke(this);
            return Task.CompletedTask;
        }
    }
}
