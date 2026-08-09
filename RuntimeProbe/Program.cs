using System.Windows.Forms;

ApplicationConfiguration.Initialize();

if (args.Length >= 2 && args[0].Equals("--stress", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[1], out var count))
{
	for (var index = 0; index < count; index++)
	{
		using var transient = new Form
		{
			Text = $"StageManager Stress {index + 1}",
			Width = 320,
			Height = 180,
			ShowInTaskbar = true,
			StartPosition = FormStartPosition.Manual,
			Left = 260 + index % 10,
			Top = 180 + index % 10
		};
		transient.Show();
		Application.DoEvents();
		transient.Close();
		Application.DoEvents();
	}
	return;
}

var title = args.Length > 0 ? string.Join(" ", args) : "StageManager Runtime Probe";
Application.Run(new Form
{
	Text = title,
	Width = 480,
	Height = 320,
	StartPosition = FormStartPosition.CenterScreen
});
