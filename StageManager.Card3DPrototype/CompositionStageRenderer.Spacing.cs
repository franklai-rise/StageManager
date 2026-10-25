using System.Numerics;

namespace StageManager.Card3DPrototype;

internal sealed partial class CompositionStageRenderer
{
    private float PlaceVerticalTool(ref float projectedTop, Vector2 size, Vector2 pivot, float angle, float gap)
    {
        var envelope = new VerticalCardEnvelope(_viewportHeight / 2, PerspectiveDistance * _dpiScale);
        envelope.Add(size, pivot, new CardHoverTransform(Vector3.Zero, Vector3.One, angle));
        var origin = envelope.OriginForTop(projectedTop);
        projectedTop = envelope.Bottom(origin) + gap;
        return origin;
    }

    private VerticalCardEnvelope MeasureVerticalStage(StageCardVisual stage, bool expanded)
    {
        var result = new VerticalCardEnvelope(_viewportHeight / 2, PerspectiveDistance * _dpiScale);
        var pivot = (stage.GroupCard ?? stage.Windows[0]).Pivot;
        result.Add(CardSize, pivot, Card3DGeometry.CreateCollapsedStackTransform(0, false, _dpiScale));
        if (stage.Windows.Count <= 1) return result;
        if (!expanded)
        {
            for (var i = 1; i <= 2; i++) result.Add(CardSize, pivot, Card3DGeometry.CreateCollapsedStackTransform(i, false, _dpiScale));
            return result;
        }
        var count = _expandAllStages ? stage.Windows.Count : MultiWindowCardInteraction.CreateExpandedChildPage(
            stage.Windows, _stagePages.GetValueOrDefault(stage.Key), PageSize - 1).VisibleChildren.Count;
        var stride = Card3DGeometry.CalculateExpandedListStride(CardSize.Y, _dpiScale);
        for (var i = 1; i <= count; i++)
            result.Add(CardSize, pivot, Card3DGeometry.CreateExpandedListTransform(i, -1, _dpiScale, stride, 18 * _dpiScale));
        if (!_expandAllStages && stage.Windows.Count > PageSize - 1)
            result.Add(stage.NextButton.Size, Vector2.Zero, new CardHoverTransform(
                new Vector3(46 * _dpiScale, CardSize.Y + count * stride + 7 * _dpiScale, 80 * _dpiScale), Vector3.One, 0));
        return result;
    }
}
