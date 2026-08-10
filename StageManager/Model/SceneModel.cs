using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;

namespace StageManager.Model;

[System.Diagnostics.DebuggerDisplay("{Title}")]
public sealed class SceneModel : INotifyPropertyChanged
{
	private Stage _stage = null!;
	private bool _isVisible = true;
	private Thickness _perspectiveCardMargin;
	private Visibility _perspectiveVisibility = Visibility.Collapsed;
	private int _perspectiveZIndex;
	private double _perspectiveBackplateOpacity;
	private double _perspectiveAngle;

	private SceneModel()
	{
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public static SceneModel FromStage(Stage stage)
	{
		var model = new SceneModel { Id = stage.Id, Stage = stage };
		model.SynchronizeWindows();
		return model;
	}

	public Guid Id { get; private set; }
	public ObservableCollection<WindowModel> Windows { get; } = new();
	public ObservableCollection<WindowModel> PreviewWindows { get; } = new();
	public string Title => Stage?.Title ?? string.Empty;
	public int WindowCount => Windows.Count;
	public int ExtraWindowCount => Math.Max(0, WindowCount - PreviewWindows.Count);
	public string ExtraWindowLabel => ExtraWindowCount > 0 ? $"+{ExtraWindowCount}" : string.Empty;
	public Visibility ExtraWindowVisibility => ExtraWindowCount > 0 ? Visibility.Visible : Visibility.Collapsed;
	public DateTime Updated { get; private set; } = DateTime.UtcNow;
	public Thickness PerspectiveCardMargin => _perspectiveCardMargin;
	public Visibility PerspectiveVisibility => _perspectiveVisibility;
	public int PerspectiveZIndex => _perspectiveZIndex;
	public double PerspectiveBackplateOpacity => _perspectiveBackplateOpacity;
	public double PerspectiveAngle => _perspectiveAngle;

	public Stage Stage
	{
		get => _stage;
		private set
		{
			if (_stage is not null)
				_stage.SelectedChanged -= OnSelectedChanged;
			_stage = value;
			_stage.SelectedChanged += OnSelectedChanged;
		}
	}

	public bool IsVisible
	{
		get => _isVisible;
		set
		{
			if (_isVisible == value)
				return;
			_isVisible = value;
			RaisePropertyChanged();
			RaisePropertyChanged(nameof(Visibility));
		}
	}

	public Visibility Visibility => IsVisible ? Visibility.Visible : Visibility.Collapsed;

	public void UpdateFromStage(Stage stage)
	{
		if (Id != stage.Id)
			throw new InvalidOperationException("Cannot update a model from another stage.");
		Stage = stage;
		SynchronizeWindows();
		Updated = DateTime.UtcNow;
		RaisePropertyChanged(nameof(Title));
		RaisePropertyChanged(nameof(Updated));
	}

	public void RebuildPreviews()
	{
		PreviewWindows.Clear();
		foreach (var model in Windows.Take(3))
			PreviewWindows.Add(model);
		UpdatePreviewMetadata();
	}

	public void SetPerspectiveIndex(int index, bool enabled)
	{
		var depth = Math.Min(Math.Max(index, 0), 4);
		var margin = enabled ? new Thickness(9 - depth * 0.9, 0, 0, 0) : new Thickness();
		SetPerspectiveValue(ref _perspectiveCardMargin, margin, nameof(PerspectiveCardMargin));
		SetPerspectiveValue(ref _perspectiveVisibility, enabled ? Visibility.Visible : Visibility.Collapsed, nameof(PerspectiveVisibility));
		SetPerspectiveValue(ref _perspectiveZIndex, enabled ? 1000 - index : 0, nameof(PerspectiveZIndex));
		SetPerspectiveValue(ref _perspectiveBackplateOpacity, enabled ? 0.88 - depth * 0.06 : 0, nameof(PerspectiveBackplateOpacity));
		SetPerspectiveValue(ref _perspectiveAngle, enabled ? -7 + depth * 0.6 : 0, nameof(PerspectiveAngle));
	}

	private void SynchronizeWindows()
	{
		var updatedWindows = Stage.Windows.ToArray();
		for (var index = 0; index < updatedWindows.Length; index++)
		{
			var existing = Windows.FirstOrDefault(model => model.Handle == updatedWindows[index].Handle);
			if (existing is null)
				Windows.Insert(index, new WindowModel(updatedWindows[index]));
			else
			{
				existing.Window = updatedWindows[index];
				var oldIndex = Windows.IndexOf(existing);
				if (oldIndex != index)
					Windows.Move(oldIndex, index);
			}
		}

		for (var index = Windows.Count - 1; index >= 0; index--)
		{
			if (!updatedWindows.Any(window => window.Handle == Windows[index].Handle))
				Windows.RemoveAt(index);
		}

		RebuildPreviews();
		RaisePropertyChanged(nameof(WindowCount));
	}

	private void UpdatePreviewMetadata()
	{
		for (var index = 0; index < PreviewWindows.Count; index++)
			PreviewWindows[index].SetPreviewIndex(index);
		RaisePropertyChanged(nameof(ExtraWindowCount));
		RaisePropertyChanged(nameof(ExtraWindowLabel));
		RaisePropertyChanged(nameof(ExtraWindowVisibility));
	}

	private void OnSelectedChanged(object? sender, EventArgs e)
	{
		Updated = DateTime.UtcNow;
		RaisePropertyChanged(nameof(Updated));
	}

	private void RaisePropertyChanged([CallerMemberName] string memberName = "") =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(memberName));

	private void SetPerspectiveValue<T>(ref T field, T value, string propertyName)
	{
		if (Equals(field, value))
			return;
		field = value;
		RaisePropertyChanged(propertyName);
	}
}
