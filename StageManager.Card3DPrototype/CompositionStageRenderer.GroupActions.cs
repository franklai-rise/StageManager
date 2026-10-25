using System.Numerics;

namespace StageManager.Card3DPrototype;

internal sealed partial class CompositionStageRenderer
{
    private string? _openGroupRequest;
    public string? ConsumeOpenGroupRequest() { var key = _openGroupRequest; _openGroupRequest = null; return key; }
    public void SetOpenGroupPressed(string key, bool pressed)
    { if (_stages.TryGetValue(key, out var stage)) stage.OpenButton.SetPressed(pressed); }
    public void SetOpenGroupBusy(string key, bool busy)
    { if (_stages.TryGetValue(key, out var stage)) stage.OpenButton.SetBusy(busy); }
    private void SetOpenGroupHovered(string? key)
    { foreach (var stage in _stages.Values) stage.OpenButton.SetHovered(stage.Key == key); }

    private void LayoutGroupActions(StageCardVisual stage, Vector3 origin, float scale,
        CardHoverTransform transform, Vector2 camera, bool showPin)
    {
        var card = stage.GroupCard!;
        var fit = Math.Min(1f, (CardSize.X - 12 * _dpiScale) / (104 * _dpiScale));
        stage.PinButton.SetLayout(_dpiScale * fit, 0, IsStagePinned(stage.Key));
        stage.OpenButton.SetLayout(_dpiScale, Math.Min(104 * _dpiScale, CardSize.X - 12 * _dpiScale));
        var gap = 4 * _dpiScale;
        var total = stage.OpenButton.Size.Y + (showPin ? stage.PinButton.Size.Y + gap : 0);
        var y = (CardSize.Y - total) / 2;
        if (showPin)
        {
            var offset = new Vector3((CardSize.X - stage.PinButton.Size.X) / 2, y, 2 * _dpiScale);
            stage.PinButton.SetOffset(offset); stage.PinButton.SetVisible(true);
            stage.PinButton.SetHovered(_pinButtonHovered && stage.Key == _pinHoveredStageKey);
            AddAction(offset, stage.PinButton.Size, pin: true);
            y += stage.PinButton.Size.Y + gap;
        }
        else stage.PinButton.SetVisible(false);
        var openOffset = new Vector3((CardSize.X - stage.OpenButton.Size.X) / 2, y, 2 * _dpiScale);
        stage.OpenButton.Root.Offset = openOffset; stage.OpenButton.SetVisible(true);
        AddAction(openOffset, stage.OpenButton.Size, pin: false);

        void AddAction(Vector3 offset, Vector2 size, bool pin)
        {
            // Half-gap padding keeps FIX and OPEN hit regions disjoint.
            var padding = 2 * _dpiScale;
            var hitOffset = offset - new Vector3(padding, padding, 0);
            var hitSize = size + new Vector2(padding * 2);
            var polygon = Card3DGeometry.ProjectCardOverlay(origin, scale, transform.Offset, transform.Scale,
                transform.Angle, CardSize, card.Pivot, hitOffset, hitSize, camera, PerspectiveDistance * _dpiScale);
            _hitTargets.Add(new(stage.Key, null, polygon, 25000,
                IsPinButton: pin, IsOpenGroupButton: !pin, Projection: ProjectLive(stage, card, hitOffset, hitSize)));
        }
    }
}
