using StageManager.Native;
using StageManager.Native.Window;
using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StageManager.Model;

[System.Diagnostics.DebuggerDisplay("{Title}")]
public sealed class WindowModel : INotifyPropertyChanged
{
	private static readonly ConcurrentDictionary<string, ImageSource> IconCache = new(StringComparer.OrdinalIgnoreCase);
	private IWindow _window;
	private Thickness _previewMargin;
	private int _previewZIndex;

	public WindowModel(IWindow window)
	{
		_window = window ?? throw new ArgumentNullException(nameof(window));
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public string Title => _window.Title.Length > 32 ? _window.Title[..29] + "..." : _window.Title;
	public string ProcessName => _window.ProcessName;
	public IntPtr Handle => _window.Handle;
	public Thickness PreviewMargin => _previewMargin;
	public int PreviewZIndex => _previewZIndex;

	public ImageSource? Icon
	{
		get
		{
			var key = string.IsNullOrWhiteSpace(_window.ProcessExecutable) ? _window.ProcessName : _window.ProcessExecutable;
			if (IconCache.TryGetValue(key, out var cached))
				return cached;
			var extracted = ExtractIconSource();
			if (extracted is not null)
				IconCache.TryAdd(key, extracted);
			return extracted;
		}
	}

	public IWindow Window
	{
		get => _window;
		set
		{
			_window = value;
			RaisePropertyChanged();
			RaisePropertyChanged(nameof(Title));
			RaisePropertyChanged(nameof(ProcessName));
			RaisePropertyChanged(nameof(Handle));
			RaisePropertyChanged(nameof(Icon));
		}
	}

	public void SetPreviewIndex(int index)
	{
		_previewMargin = new Thickness(index * 6, index * 5, Math.Max(0, (2 - index) * 6), Math.Max(0, (2 - index) * 5));
		_previewZIndex = index;
		RaisePropertyChanged(nameof(PreviewMargin));
		RaisePropertyChanged(nameof(PreviewZIndex));
	}

	private ImageSource? ExtractIconSource()
	{
		if (_window is not WindowsWindow windowsWindow)
			return null;
		using var icon = windowsWindow.ExtractIcon();
		if (icon is null)
			return null;
		var image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
		image.Freeze();
		return image;
	}

	private void RaisePropertyChanged([CallerMemberName] string memberName = "") =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(memberName));
}
