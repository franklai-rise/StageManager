using System.Drawing;
using System.IO;
using StageManager.Card3DPrototype;

internal static class PreviewFitModeRegressionTests
{
    // The integration owner registers Run (or Check) in the main test runner.
    public static void Run() => Check();

    public static void Check()
    {
        Geometry();
        Protocol();
        DrawingAndPlaceholders();
        CaptureClipPreservesContainCorners();
        InvalidModesDoNotStartWorkers().GetAwaiter().GetResult();
    }

    private static void Geometry()
    {
        var target = new Rectangle(7, 11, 120, 180);
        foreach (var source in new[] { new Size(1920, 1080), new Size(1080, 1920),
            new Size(120, 180), new Size(100, 100), new Size(8192, 2), new Size(2, 8192) })
        {
            var fit = WindowFrameCapture.AspectFitBounds(source, target);
            Assert(fit.Width > 0 && fit.Height > 0, "Empty fit.");
            Assert(fit.Left >= target.Left - .001 && fit.Top >= target.Top - .001 &&
                fit.Right <= target.Right + .001 && fit.Bottom <= target.Bottom + .001, "Fit escapes target.");
            Near(fit.Left + fit.Width / 2, target.Left + target.Width / 2f, "Horizontal centering");
            Near(fit.Top + fit.Height / 2, target.Top + target.Height / 2f, "Vertical centering");
            Near(fit.Width / source.Width, fit.Height / source.Height, "Non-uniform scale/rotation");
            Assert(Math.Abs(fit.Width - target.Width) < .001 || Math.Abs(fit.Height - target.Height) < .001,
                "Fit does not use the available area.");
        }
        Assert(WindowFrameCapture.AspectFitBounds(new Size(240, 120), target) == new RectangleF(7, 71, 120, 60),
            "Landscape source was cropped, stretched or rotated into portrait target.");
        Assert(WindowFrameCapture.AspectFillCrop(new Size(240, 120), target.Size) == new Rectangle(80, 0, 80, 120),
            "Existing cover crop changed.");
        Expect<ArgumentOutOfRangeException>(() => WindowFrameCapture.AspectFitBounds(Size.Empty, target));
    }

    private static void Protocol()
    {
        var request = new CaptureRequest(7, 21,
            new CaptureWindow(new IntPtr(123), "Title / 中文", "test", "", false, 42, 100), 120, 180, false, "2/3");
        Assert(request.FitMode == PreviewFitMode.Cover, "Default must remain Cover.");
        Assert(CaptureWorkerProtocol.Version == 2, "Wire format must use protocol v2.");
        foreach (var mode in Enum.GetValues<PreviewFitMode>())
        foreach (var applicationCard in new[] { false, true })
        {
            var expected = request with { FitMode = mode, ApplicationCard = applicationCard };
            var packet = CaptureWorkerProtocol.EncodeRequest(expected);
            Assert(CaptureWorkerProtocol.DecodeRequest(packet) == expected, "Request round trip lost fields.");
            Expect<EndOfStreamException>(() => CaptureWorkerProtocol.DecodeRequest(packet[..^1]));
            Expect<InvalidDataException>(() => CaptureWorkerProtocol.DecodeRequest([.. packet, 0]));
            packet[0] = 1;
            Expect<InvalidDataException>(() => CaptureWorkerProtocol.DecodeRequest(packet));
        }
        foreach (var value in new[] { -1, 2, 255, 256 })
            Expect<InvalidDataException>(() => CaptureWorkerProtocol.EncodeRequest(request with { FitMode = (PreviewFitMode)value }));
        foreach (var value in new byte[] { 2, 255 })
        {
            var packet = CaptureWorkerProtocol.EncodeRequest(request);
            packet[^1] = value;
            Expect<InvalidDataException>(() => CaptureWorkerProtocol.DecodeRequest(packet));
        }
    }

    private static void DrawingAndPlaceholders()
    {
        using var source = new Bitmap(240, 120);
        using (var graphics = Graphics.FromImage(source))
        {
            graphics.Clear(Color.Red);
            graphics.FillRectangle(Brushes.Blue, 120, 0, 120, 120);
            graphics.FillRectangle(Brushes.Lime, 0, 0, 12, 120);
            graphics.FillRectangle(Brushes.Yellow, 228, 0, 12, 120);
        }
        using var card = new Bitmap(120, 180);
        using (var graphics = Graphics.FromImage(card))
            WindowFrameCapture.DrawPreview(graphics, source, new Rectangle(0, 0, 120, 180), PreviewFitMode.Contain);
        Assert(card.GetPixel(60, 20).ToArgb() == Color.LightGray.ToArgb() &&
            card.GetPixel(60, 160).ToArgb() == Color.LightGray.ToArgb(), "Letterbox is not light gray.");
        Assert(card.GetPixel(2, 90).ToArgb() == Color.Lime.ToArgb() &&
            card.GetPixel(117, 90).ToArgb() == Color.Yellow.ToArgb(), "Source edges were cropped.");
        Assert(card.GetPixel(30, 90).ToArgb() == Color.Red.ToArgb() &&
            card.GetPixel(90, 90).ToArgb() == Color.Blue.ToArgb(), "Source orientation changed.");

        Assert(!CaptureWorkerEntryPoint.IsWorkerProcess, "Tests must not enable native capture.");
        using var capture = new WindowFrameCapture();
        foreach (var minimized in new[] { false, true })
        {
            var window = new CaptureWindow(IntPtr.Zero, "Placeholder", "test", "", minimized);
            using var cover = capture.Capture(window, 120, 180, "2/3");
            using var contain = capture.Capture(window, 120, 180, "2/3", PreviewFitMode.Contain);
            Assert(cover.IsPlaceholder && contain.IsPlaceholder && cover.Pixels.SequenceEqual(contain.Pixels),
                "Fit mode changed placeholder behavior or bypassed main-process isolation.");
        }
    }

    private static void CaptureClipPreservesContainCorners()
    {
        // Use the exact clip factory installed by Capture, and the same draw helper
        // reached by TryDrawWindow, without enabling native capture in this process.
        const int width = 122, height = 182;
        var target = new Rectangle(1, 1, width - 2, height - 2);
        foreach (var sourceSize in new[] { new Size(120, 180), new Size(120, 178) })
        {
            using var source = new Bitmap(sourceSize.Width, sourceSize.Height);
            using (var graphics = Graphics.FromImage(source))
            {
                graphics.Clear(Color.White);
                graphics.FillRectangle(Brushes.Red, 0, 0, 20, 20);
                graphics.FillRectangle(Brushes.Blue, source.Width - 20, 0, 20, 20);
                graphics.FillRectangle(Brushes.Lime, 0, source.Height - 20, 20, 20);
                graphics.FillRectangle(Brushes.Yellow, source.Width - 20, source.Height - 20, 20, 20);
            }
            foreach (var mode in Enum.GetValues<PreviewFitMode>())
            {
                using var card = new Bitmap(width, height);
                using var graphics = Graphics.FromImage(card);
                graphics.Clear(Color.Transparent);
                graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var clip = WindowFrameCapture.CreateCaptureClipPath(width, height);
                graphics.SetClip(clip);
                Assert(!graphics.IsVisible(3, 4), "Probe must lie outside the real rounded capture clip.");
                WindowFrameCapture.DrawPreview(graphics, source, target, mode);
                Assert(!graphics.IsVisible(3, 4), "Live draw did not restore the capture clip.");
                var corners = new[] { (3, 4, Color.Red), (118, 4, Color.Blue),
                    (3, 177, Color.Lime), (118, 177, Color.Yellow) };
                foreach (var (x, y, color) in corners)
                {
                    var actual = card.GetPixel(x, y);
                    Assert(mode == PreviewFitMode.Contain ? actual.ToArgb() == color.ToArgb() : actual.A == 0,
                        "Contain lost a source corner, or Cover's rounded clipping changed.");
                }
                Assert(card.GetPixel(0, height / 2).A == 0, "Contain escaped the capture target.");
            }
        }
    }

    private static async Task InvalidModesDoNotStartWorkers()
    {
        var window = new CaptureWindow(IntPtr.Zero, "test", "test", "", false);
        using var client = new CaptureWorkerClient();
        try
        {
            using var unexpected = await client.CaptureAsync(window, 120, 180, false, null, fitMode: (PreviewFitMode)256);
            throw new InvalidOperationException("Invalid client mode accepted.");
        }
        catch (InvalidDataException) { }
        Assert(client.WorkerProcessId is null, "Invalid mode launched a worker.");
        using var service = new PreviewCaptureService();
        try
        {
            using var unexpected = await service.CaptureAsync(window, 120, 180, fitMode: (PreviewFitMode)2);
            throw new InvalidOperationException("Invalid service mode became a placeholder.");
        }
        catch (InvalidDataException) { }
    }

    private static void Near(float actual, float expected, string message) =>
        Assert(Math.Abs(actual - expected) < .001, message);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Preview fit: " + message);
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
