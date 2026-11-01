# v4.8.2 validation addendum

The display selection implementation and acceptance scope are recorded in [v4.8.1 validation](VALIDATION-v4.8.1.md). v4.8.1 remains an unmodified, unpublished tag; it did not pass CI and no release assets were published.

The failed cloud check was CalendarPresentationTests' hard-coded 525 × 925 native form size. WinForms Form.SetBoundsCore also clamps normal windows to SystemInformation.MaxWindowTrackSize, even when a test supplies an imaginary 900 × 1000 work area. Reference: [.NET 8 Form implementation](https://github.com/dotnet/winforms/blob/v8.0.0/src/System.Windows.Forms/src/System/Windows/Forms/Form.cs#L4690-L4702).

The corrected assertion computes exact expected dimensions including that real session limit and checks all four work-area bounds. Existing large-DPI full-surface/independent-scroll assertions remain enabled. No production calendar geometry change and no skipped tests.

Local isolated updater fixture passed success, crash, timeout, absent settings and interrupted-transaction scenarios; it uses only temporary fixtures, not the user's installed EXE/settings.

This release remains expedited at the user's request; physical monitor hotplug/mixed-DPI and exact-CI-binary 24-hour soak are uncompleted. Do not replace the user's installed application or shortcuts.
