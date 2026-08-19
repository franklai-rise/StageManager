namespace StageManager.Desktop;

internal static class GraphicsDeviceRecoveryPolicy
{
	private const int DxgiErrorDeviceRemoved = unchecked((int)0x887A0005);
	private const int DxgiErrorDeviceHung = unchecked((int)0x887A0006);
	private const int DxgiErrorDeviceReset = unchecked((int)0x887A0007);

	public static bool IsDeviceLoss(Exception? exception)
	{
		for (var current = exception; current is not null; current = current.InnerException)
		{
			if (current.HResult is DxgiErrorDeviceRemoved or DxgiErrorDeviceHung or DxgiErrorDeviceReset)
				return true;
		}
		return false;
	}
}
