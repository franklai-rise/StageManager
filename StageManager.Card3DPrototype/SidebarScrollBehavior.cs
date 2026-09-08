namespace StageManager.Card3DPrototype;

internal readonly record struct SidebarScrollRange(float Minimum, float Maximum);

internal static class SidebarScrollBehavior
{
	public static SidebarScrollRange Calculate(float contentStart, float contentHeight, float viewportHeight, float margin, float extraTravel = 0f)
	{
		var safeMargin = Math.Max(0f, margin);
		var viewportBottom = Math.Max(safeMargin, viewportHeight - safeMargin);
		var contentBottom = contentStart + Math.Max(0f, contentHeight);
		var minimum = Math.Min(0f, contentBottom - viewportBottom);
		var maximum = Math.Max(0f, Math.Max(contentStart - safeMargin, contentBottom - viewportBottom));
		var extension = Math.Clamp(extraTravel, 0f, Math.Max(0f, Math.Min(viewportHeight * 0.12f, contentHeight * 0.25f)));
		return new SidebarScrollRange(minimum - extension, maximum + extension);
	}
}

// Time-based spring motion. Input keeps the full wheel delta, including fractional
// notches from precision wheels; ticks are needed only until the spring settles.
internal sealed class SidebarScrollMotion
{
	private SidebarScrollRange _range;
	private double _velocity;
	private double _idleSeconds = 1;
	private float _scale = 1f;
	private const double ReturnDelay = 0.09;
	public float Position { get; private set; }
	public float Target { get; private set; }
	public float OverscrollLimit => 28f * _scale;
	public bool IsMoving => Math.Abs(Position - Target) > 0.08f * _scale ||
		Math.Abs(_velocity) > 0.5f * _scale || Target < _range.Minimum || Target > _range.Maximum;

	public void SetRange(SidebarScrollRange range, float dpiScale)
	{
		var sameScale = _scale == Math.Max(0.75f, dpiScale);
		_scale = Math.Max(0.75f, dpiScale);
		if (_range == range)
			return;
		var grew = range.Minimum <= _range.Minimum && range.Maximum >= _range.Maximum;
		_range = range;
		// Expanding while a wheel spring is in flight must not snap its position.
		if (sameScale && grew)
			return;
		Target = Math.Clamp(Target, range.Minimum, range.Maximum);
		var boundedPosition = Math.Clamp(Position, range.Minimum, range.Maximum);
		if (Position != boundedPosition)
			_velocity = 0;
		Position = boundedPosition;
	}

	public void AddWheel(int delta, float distancePerNotch, bool animated)
	{
		if (delta == 0)
			return;
		var distance = -(delta / 120f) * distancePerNotch;
		var proposed = Target + distance;
		var bounded = Math.Clamp(proposed, _range.Minimum, _range.Maximum);
		if (!animated)
		{
			Target = bounded;
			SnapToTarget();
			return;
		}

		var excess = proposed - bounded;
		Target = bounded + excess / (1f + Math.Abs(excess) / OverscrollLimit);
		// Retain a little inertia on reversal, but never fight the user's wheel.
		if (Math.Sign(distance) != Math.Sign(_velocity))
			_velocity *= 0.4;
		_velocity += distance * 3.0;
		_velocity = Math.Clamp(_velocity, -1800 * _scale, 1800 * _scale);
		_idleSeconds = 0;
	}

	public void Advance(double elapsedSeconds)
	{
		if (elapsedSeconds <= 0 || !IsMoving)
			return;
		if (elapsedSeconds > 0.25)
		{
			SnapToTarget();
			return;
		}

		var remaining = elapsedSeconds;
		while (remaining > 0)
		{
			var dt = Math.Min(remaining, 1.0 / 240);
			_idleSeconds += dt;
			if (_idleSeconds >= ReturnDelay)
				Target = Math.Clamp(Target, _range.Minimum, _range.Maximum);
			const double frequency = 20;
			const double damping = 0.72;
			var acceleration = frequency * frequency * (Target - Position) - 2 * damping * frequency * _velocity;
			_velocity += acceleration * dt;
			var next = Position + (float)(_velocity * dt);
			Position = Math.Clamp(next, _range.Minimum - OverscrollLimit, _range.Maximum + OverscrollLimit);
			if (next != Position)
				_velocity = 0;
			remaining -= dt;
		}
		if (!IsMoving && _idleSeconds >= ReturnDelay)
			SnapToTarget();
	}

	public void SnapToTarget()
	{
		Target = Math.Clamp(Target, _range.Minimum, _range.Maximum);
		Position = Target;
		_velocity = 0;
		_idleSeconds = 1;
	}
}
