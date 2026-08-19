using StageManager.Native.Window;
using System.Drawing;

namespace StageManager.Model;

public enum WindowRole
{
	Primary,
	ModalDialog,
	TransientPopup,
	Overlay,
	Shell,
	Unknown
}

public sealed record WindowSnapshot(
	WindowInstanceId InstanceId,
	string Title,
	string WindowClass,
	string? AppUserModelId,
	Guid VirtualDesktopId,
	string DisplayDeviceName,
	WindowState State,
	Rectangle Bounds);

public sealed record WindowClassification(
	bool CreatesCard,
	string CanonicalApplicationId,
	WindowRole Role,
	IntPtr ActivationTarget,
	string RejectionReason)
{
	public static WindowClassification Rejected(
		string applicationId,
		WindowRole role,
		IntPtr activationTarget,
		string reason) => new(false, applicationId, role, activationTarget, reason);
}
