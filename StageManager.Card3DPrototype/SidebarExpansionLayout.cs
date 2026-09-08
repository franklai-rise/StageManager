namespace StageManager.Card3DPrototype;

internal readonly record struct SidebarExpansionLayout(float StartY, SidebarScrollRange ScrollRange)
{
    public static SidebarExpansionLayout Calculate(float collapsedHeight, float expandedHeight,
        float viewportHeight, float dpiScale, float verticalOffset)
    {
        var margin = 12f * dpiScale;
        // Only the collapsed column establishes the anchor. Extra children grow down.
        var naturalStart = collapsedHeight <= viewportHeight - 2 * margin
            ? (viewportHeight - collapsedHeight) / 2f : margin;
        var start = naturalStart + verticalOffset;
        var collapsed = SidebarScrollBehavior.Calculate(start, collapsedHeight, viewportHeight, margin, 40f * dpiScale);
        var expanded = SidebarScrollBehavior.Calculate(start, collapsedHeight + expandedHeight,
            viewportHeight, margin, 40f * dpiScale);
        // Keep the old scroll position legal when the expanded column becomes taller.
        return new SidebarExpansionLayout(start, new SidebarScrollRange(
            Math.Min(collapsed.Minimum, expanded.Minimum), Math.Max(collapsed.Maximum, expanded.Maximum)));
    }
}
