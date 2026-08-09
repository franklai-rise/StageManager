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

if (args.Length >= 2 && args[0].Equals("--delayed-title", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[1], out var titleDelay))
{
	var delayedTitle = args.Length >= 3 ? args[2] : "StageManager Delayed Title Probe";
	var lifetime = args.Length >= 4 && int.TryParse(args[3], out var requestedLifetime) ? requestedLifetime : 5000;
	var form = new Form
	{
		Text = string.Empty,
		Width = 480,
		Height = 320,
		ShowInTaskbar = true,
		StartPosition = FormStartPosition.CenterScreen
	};
	var titleTimer = new System.Windows.Forms.Timer { Interval = Math.Max(1, titleDelay) };
	var closeTimer = new System.Windows.Forms.Timer { Interval = Math.Max(titleDelay + 1000, lifetime) };
	titleTimer.Tick += (_, _) =>
	{
		titleTimer.Stop();
		form.Text = delayedTitle;
	};
	closeTimer.Tick += (_, _) =>
	{
		closeTimer.Stop();
		form.Close();
	};
	form.Shown += (_, _) =>
	{
		titleTimer.Start();
		closeTimer.Start();
	};
	form.FormClosed += (_, _) =>
	{
		titleTimer.Dispose();
		closeTimer.Dispose();
	};
	Application.Run(form);
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
