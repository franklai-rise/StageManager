namespace StageManager.Card3DPrototype;

internal sealed class CardSizeSettingsForm : Form
{
	private readonly TrackBar _sizeSlider;
	private readonly Label _valueLabel;

	public CardSizeSettingsForm(double currentScale)
	{
		Text = "Stage_Manager_Lai Settings";
		FormBorderStyle = FormBorderStyle.FixedDialog;
		StartPosition = FormStartPosition.CenterScreen;
		ShowInTaskbar = false;
		MaximizeBox = false;
		MinimizeBox = false;
		ClientSize = new Size(430, 220);
		BackColor = Color.FromArgb(24, 26, 31);
		ForeColor = Color.FromArgb(244, 246, 250);
		Font = new Font("Segoe UI", 10f);

		var heading = new Label
		{
			Text = "Card size",
			Font = new Font(Font, FontStyle.Bold),
			AutoSize = true,
			Location = new Point(24, 22)
		};
		var description = new Label
		{
			Text = "Adjust the sidebar cards. The setting is saved automatically when you select Save.",
			AutoSize = false,
			Size = new Size(380, 42),
			Location = new Point(24, 50),
			ForeColor = Color.FromArgb(180, 187, 200)
		};
		_sizeSlider = new TrackBar
		{
			Minimum = 55,
			Maximum = 125,
			TickFrequency = 5,
			SmallChange = 5,
			LargeChange = 10,
			Value = Math.Clamp((int)Math.Round(currentScale * 100), 55, 125),
			Location = new Point(20, 96),
			Size = new Size(320, 48)
		};
		_valueLabel = new Label
		{
			AutoSize = false,
			TextAlign = ContentAlignment.MiddleCenter,
			Location = new Point(345, 99),
			Size = new Size(60, 32),
			Font = new Font("Segoe UI", 11f, FontStyle.Bold)
		};
		_sizeSlider.ValueChanged += (_, _) => UpdateValueLabel();

		var cancelButton = new Button
		{
			Text = "Cancel",
			DialogResult = DialogResult.Cancel,
			Location = new Point(224, 165),
			Size = new Size(86, 34)
		};
		var saveButton = new Button
		{
			Text = "Save",
			DialogResult = DialogResult.OK,
			Location = new Point(320, 165),
			Size = new Size(86, 34)
		};

		Controls.AddRange(new Control[] { heading, description, _sizeSlider, _valueLabel, cancelButton, saveButton });
		AcceptButton = saveButton;
		CancelButton = cancelButton;
		UpdateValueLabel();
	}

	public double CardScale => _sizeSlider.Value / 100d;

	private void UpdateValueLabel() => _valueLabel.Text = $"{_sizeSlider.Value}%";
}
