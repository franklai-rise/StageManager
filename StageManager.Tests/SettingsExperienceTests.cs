using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using StageManager.Card3DPrototype;
using StageManager.Settings;

internal static class SettingsExperienceTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static IEnumerable<Control> Descendants(Control control)
    {
        foreach (Control child in control.Controls) { yield return child; foreach (var item in Descendants(child)) yield return item; }
    }
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(value)!;
    public static void RunUi(string? output = null)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                var draft = new AppSettings { SchemaVersion = 21, SettingsTheme = SettingsTheme.Light };
                using var form = new SettingsForm(draft);
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000);
                form.Show(); Application.DoEvents();
                Check(form.FormBorderStyle == FormBorderStyle.Sizable && form.MaximizeBox, "Settings stopped being resizable.");
                var language = Field<ComboBox>(form, "_interfaceLanguage");
                language.SelectedIndex = 1;
                Check(draft.UiLanguage == UiLanguage.SimplifiedChinese && form.Text.Contains("设置"), "Chinese selection is not applied.");
                Check(Descendants(form).Any(c => c.Text.EndsWith("偏好设置")) && Descendants(form).Any(c => c.Text == "Focus 增强模式"), "Localized sections/options missing.");
                form.ScrollToAiUsage(); Check(Field<string>(form, "_selectedPage") == "ai", "AI deep link did not select its page.");
                var footer = Field<Panel>(form, "_footer");
                var host = Field<Panel>(form, "_pageHost");
                form.MinimumSize = new Size(480, 400); // Simulate constrained portrait work areas, not the form's normal minimum.
                foreach (var size in new[] { new Size(1040, 820), new Size(760, 560), new Size(540, 640) })
                {
                    form.Size = size;
                    foreach (var page in new[] { "general", "cards", "ai", "windows", "shortcuts", "preferences", "updates" })
                    {
                        form.SelectPage(page); Application.DoEvents();
                        Check(footer.Bottom <= form.ClientSize.Height && footer.Top > 0, "Save bar is offscreen.");
                        var panel = Descendants(form).OfType<Panel>().Single(c => c.Name == page);
                        Check(panel.Visible, "A module was hidden behind category navigation.");
                        Check(!host.HorizontalScroll.Visible, "Continuous settings page overflows horizontally.");
                        Check(Descendants(form).OfType<Panel>().Where(c => new[] { "general", "cards", "ai", "windows", "shortcuts", "preferences", "updates" }.Contains(c.Name)).All(c => c.Visible), "Selecting a module hides another module.");
                        if (panel.HorizontalScroll.Visible && output is not null)
                        {
                            Directory.CreateDirectory(output);
                            using var debug = new Bitmap(form.Width, form.Height); form.DrawToBitmap(debug, new Rectangle(Point.Empty, debug.Size)); debug.Save(Path.Combine(output, "layout-debug.png"));
                            foreach (var c in new[] { (Control)panel }.Concat(Descendants(panel))) Console.WriteLine($"{c.GetType().Name} {c.Text} bounds={c.Bounds} preferred={c.PreferredSize} max={c.MaximumSize} padding={c.Padding} margin={c.Margin}");
                        }
                        Check(!panel.HorizontalScroll.Visible, $"Page {page} unexpectedly scrolls horizontally at {size}: client={panel.ClientRectangle}, display={panel.DisplayRectangle}.");
                    }
                }
                form.SelectPage("ai"); Application.DoEvents();
                Check(host.AutoScrollPosition.Y < 0, "AI anchor did not scroll the continuous page.");
                form.SelectPage("general"); Application.DoEvents();
                Check(host.AutoScrollPosition.Y == 0, "First module did not return to the beginning.");
                var search = Field<TextBox>(form, "_search");
                Check(search.Visible && search.Parent!.ClientRectangle.Contains(search.Bounds), "Search box is clipped.");
                search.Text = "DeepSeek";
                typeof(SettingsForm).GetMethod("FindSetting", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(form, null);
                Check(Field<string>(form, "_selectedPage") == "ai" && host.AutoScrollPosition.Y < 0, "Search did not jump to the option.");
                search.Text = "";
                var navigation = Descendants(form).OfType<Button>().Where(b => b.Name.StartsWith("navigate-")).ToArray();
                Check(navigation.Length == 7 && navigation.All(b => b.Visible), "Module rail is missing visible navigation buttons.");
                var railBounds = navigation[0].Parent!.Parent!.Bounds;
                form.SelectPage("general");
                Field<CheckBox>(form, "_animationsEnabled").Checked = true;
                navigation.Single(b=>b.Name=="navigate-windows").PerformClick();
                var deadline = DateTime.UtcNow.AddSeconds(2);
                var timer = Field<System.Windows.Forms.Timer>(form, "_navigationTimer");
                var previous = 0;
                do {
                    Application.DoEvents(); Thread.Sleep(16);
                    var position = -host.AutoScrollPosition.Y;
                    Check(position >= previous, "Anchor navigation reverses or bounces."); previous = position;
                } while(timer.Enabled && DateTime.UtcNow < deadline);
                Check(!timer.Enabled && previous > 0 && Field<string>(form,"_selectedPage")=="windows", "Navigation did not reach its module.");
                var reachedModule = Descendants(form).OfType<Panel>().Single(p=>p.Name=="windows");
                Check(Math.Abs(host.PointToClient(reachedModule.PointToScreen(Point.Empty)).Y) <= 2,
                    "Navigation selected the label but did not align the actual module heading.");
                Check(navigation[0].Parent!.Parent!.Bounds == railBounds, "The navigation rail scrolled with the document.");
                form.NavigateToModule("ai");
                form.SelectPage("general"); // explicit navigation cancels the previous destination
                Application.DoEvents(); Thread.Sleep(40); Application.DoEvents();
                Check(!timer.Enabled && host.AutoScrollPosition.Y==0, "Old animation overrode a newer navigation request.");
                if (output is not null)
                {
                    form.ClientSize = new Size(540, 640); form.SelectPage("general"); Application.DoEvents();
                    Directory.CreateDirectory(output);
                    using (var compact = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(compact, new Rectangle(Point.Empty, compact.Size)); compact.Save(Path.Combine(output, "narrow.png")); }
                    Console.WriteLine($"Settings font: {form.Font}; checkbox font: {Field<CheckBox>(form, "_focusEnhancedMode").Font}");
                    Directory.CreateDirectory(output); form.ClientSize = new Size(1040, 820);
                    foreach (var page in new[] { "general", "preferences", "ai", "windows", "updates" })
                    {
                        form.SelectPage(page); Application.DoEvents();
                        using var image = new Bitmap(form.Width, form.Height); form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Path.Combine(output, page + ".png"));
                    }
                    Field<ComboBox>(form, "_settingsTheme").SelectedIndex = 2;
                    Field<ComboBox>(form, "_settingsTextSize").SelectedIndex = 1;
                    form.SelectPage("preferences"); Application.DoEvents();
                    using var dark = new Bitmap(form.Width, form.Height); form.DrawToBitmap(dark, new Rectangle(Point.Empty, dark.Size)); dark.Save(Path.Combine(output, "preferences-dark-large.png"));
                }
                language.SelectedIndex = 0;
                Check(draft.UiLanguage == UiLanguage.English && form.Text.Contains("Settings"), "Language cannot return to English.");
                Field<CheckBox>(form, "_focusEnhancedMode").Checked = true;
                Check(!Field<CheckBox>(form, "_idleAutoHideEnabled").Enabled, "Focus permits idle hiding.");
                Field<CheckBox>(form, "_automaticUpdates").Checked = false;
                Field<ComboBox>(form, "_settingsTheme").SelectedIndex = 2;
                form.SelectPage("preferences");
                typeof(SettingsForm).GetMethod("SaveButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, new object?[] { null, EventArgs.Empty });
                Check(draft.StageMode == StageMode.Focus && !draft.AutomaticUpdateChecks && draft.SettingsTheme == SettingsTheme.Dark && draft.LastSettingsPage == "preferences", "Saved preferences not wired.");
                using var cancel = new SettingsForm(new AppSettings { SettingsTheme = SettingsTheme.Light });
                Field<ComboBox>(cancel, "_settingsTheme").SelectedIndex = 2;
                Check(cancel.Draft.SettingsTheme == SettingsTheme.Light, "Theme preview prematurely commits preferences.");
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(45)), "Settings layout smoke test timed out.");
        if (failure is not null) throw new Exception("Settings UI regression failed.", failure);
    }
}
