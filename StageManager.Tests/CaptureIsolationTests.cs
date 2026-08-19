using Microsoft.VisualStudio.TestTools.UnitTesting;
using StageManager.Desktop;
using System.IO;
using System.IO.Pipes;
using System.Text;

[TestClass]
public sealed class CaptureIsolationTests
{
	[TestMethod]
	public void OversizedSourceWindowsAreRejectedBeforeDibAllocation()
	{
		Assert.IsTrue(WindowCapturePolicy.CanCaptureSource(4000, 4000));
		Assert.IsFalse(WindowCapturePolicy.CanCaptureSource(4001, 4000));
		Assert.IsFalse(WindowCapturePolicy.CanCaptureSource(8192, 8192));
		Assert.IsFalse(WindowCapturePolicy.CanCaptureSource(1, 100));
	}

	[TestMethod]
	public void RenderSurfaceBudgetIsBounded()
	{
		Assert.IsTrue(RenderSurfacePoolPolicy.CanAllocate(0, 0, 320, 200));
		Assert.IsFalse(RenderSurfacePoolPolicy.CanAllocate(
			RenderSurfacePoolPolicy.MaximumSurfaceCount,
			0,
			64,
			64));
		Assert.IsFalse(RenderSurfacePoolPolicy.CanAllocate(
			1,
			RenderSurfacePoolPolicy.MaximumSurfaceBytes - 1,
			64,
			64));
		Assert.AreEqual(320L * 200 * 8, RenderSurfacePoolPolicy.EstimateBytes(320, 200));
	}

	[TestMethod]
	public async Task CaptureWorkerProtocolReturnsDownscaledPlaceholder()
	{
		var pipeName = $"StageManagerCaptureTest_{Guid.NewGuid():N}";
		var token = Guid.NewGuid().ToString("N");
		await using var server = new NamedPipeServerStream(
			pipeName,
			PipeDirection.InOut,
			1,
			PipeTransmissionMode.Byte,
			PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		var workerTask = Task.Run(() => CaptureWorkerEntryPoint.Run(new[]
		{
			"--capture-worker", "--pipe", pipeName, "--token", token
		}));
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		await server.WaitForConnectionAsync(timeout.Token);
		using var reader = new BinaryReader(server, Encoding.UTF8, leaveOpen: true);
		using var writer = new BinaryWriter(server, Encoding.UTF8, leaveOpen: true);
		writer.Write(CaptureWorkerProtocol.Version);
		CaptureWorkerProtocol.WriteString(writer, token);
		writer.Flush();
		Assert.AreEqual(CaptureWorkerProtocol.Version, reader.ReadByte());

		const long requestId = 17;
		writer.Write(CaptureWorkerProtocol.CaptureCommand);
		writer.Write(requestId);
		writer.Write(0L);
		writer.Write(96);
		writer.Write(60);
		writer.Write(false);
		CaptureWorkerProtocol.WriteString(writer, null);
		writer.Flush();

		using var frame = CaptureWorkerProtocol.ReadFrame(reader, IntPtr.Zero, requestId, out _);
		Assert.AreEqual(96, frame.Width);
		Assert.AreEqual(60, frame.Height);
		Assert.IsTrue(frame.IsPlaceholder);
		Assert.AreEqual(96 * 60 * 4, frame.Pixels.Length);

		writer.Write(CaptureWorkerProtocol.ShutdownCommand);
		writer.Flush();
		Assert.AreEqual(0, await workerTask.WaitAsync(TimeSpan.FromSeconds(5)));
	}
}
