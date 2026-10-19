using System.Drawing;
using System.Numerics;
using System.Reflection;
using StageManager.Card3DPrototype;
using StageManager.Settings;
using Windows.UI.Composition;

internal static class CardPolishRegressionTests
{
    internal static void Check(Compositor compositor)
    {
        using var arrow = new SidebarCollapseButtonVisual(compositor);
        foreach (var dpi in new[] { 1f,1.25f,1.5f,2f })
        foreach (var edge in Enum.GetValues<SidebarEdge>())
        foreach (var height in new[] { 36f,42f,90f,180f })
        {
            arrow.SetDirection(edge);
            arrow.SetLayout(dpi,118*dpi,height*dpi);
            CheckDirection(arrow,edge,dpi);
            arrow.SetLayout(dpi,150*dpi,(height+8)*dpi);
            CheckDirection(arrow,edge,dpi);
            arrow.SetHovered(true); arrow.SetPressed(true);
            CheckDirection(arrow,edge,dpi);
            arrow.SetHovered(false); arrow.SetPressed(false);
        }
        Assert(TopPanelLabel.BackgroundColor(false,true).A==0 && TopPanelLabel.BackgroundColor(true,true).A==0,
            "Light card captions must never paint a rectangular backdrop");
        var foreground=TopPanelLabel.TextColor(true);
        Assert(foreground.A>=220 && foreground.R<80 && foreground.G<80 && foreground.B<90,"Light captions need readable dark text");
        Assert(TopPanelLabel.TextColor(false).R>200,"Utility labels lost their light foreground");
    }
    private static void CheckDirection(SidebarCollapseButtonVisual arrow,SidebarEdge edge,float dpi)
    {
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var upper=(SpriteVisual)typeof(SidebarCollapseButtonVisual).GetField("_upperStroke",flags)!.GetValue(arrow)!;
        var lower=(SpriteVisual)typeof(SidebarCollapseButtonVisual).GetField("_lowerStroke",flags)!.GetValue(arrow)!;
        static Vector2[] Ends(SpriteVisual stroke)
        {
            var angle=stroke.RotationAngleInDegrees*MathF.PI/180;
            var center=new Vector2(stroke.Offset.X,stroke.Offset.Y)+stroke.Size/2;
            var half=new Vector2(-MathF.Sin(angle),MathF.Cos(angle))*stroke.Size.Y/2;
            return [center-half,center+half];
        }
        var joint=(from a in Ends(upper) from b in Ends(lower) orderby Vector2.DistanceSquared(a,b) select (a,b)).First();
        Assert(Vector2.Distance(joint.a,joint.b)<.1f*dpi,"Arrow strokes disconnected after resizing");
        var tip=(joint.a+joint.b)/2-arrow.Size/2;
        var expected=edge switch { SidebarEdge.Top=>new Vector2(0,-1),SidebarEdge.Bottom=>new Vector2(0,1),SidebarEdge.Right=>new Vector2(1,0),_=>new Vector2(-1,0) };
        Assert(Vector2.Dot(Vector2.Normalize(tip),expected)>.999f,"Wrong collapse direction: "+edge);
    }
    private static void Assert(bool value,string message) { if(!value) throw new Exception(message); }
}
