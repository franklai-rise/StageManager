using System.Drawing;
using System.Numerics;
using System.Reflection;
using StageManager.Card3DPrototype;
using StageManager.Settings;

internal static class TopPanelRegressionTests
{
    public static void Preview(bool polish=false)
    {
        var thread=new Thread(() =>
        {
            System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
            using var dispatcher=new DispatcherQueueHelper(); dispatcher.EnsureDispatcherQueue();
            using var form=new System.Windows.Forms.Form { Text="Stage Manager - Card preview (B bottom / T top)", Width=1050,Height=650,BackColor=Color.FromArgb(30,42,55), KeyPreview=true };
            using var compositor=new Windows.UI.Composition.Compositor();
            using var target=CompositionInterop.CreateDesktopWindowTarget(compositor,form.Handle,false);
            using var root=compositor.CreateContainerVisual();target.Root=root;
            using var renderer=new CompositionStageRenderer(form,compositor,root,.6,true,true);
            renderer.SetDockEdge(SidebarEdge.Top);renderer.SetSidebarVerticalOffset(0);renderer.SetPinButtonEnabled(true);
            renderer.SetAiUsageCardEnabled(!polish);
            renderer.SetQuickLaunchApps(QuickLaunchDefinition.LegacyDefaults());
            renderer.SetAiUsageSnapshots([StageManager.Card3DPrototype.AiUsage.AiUsageService.Empty("antigravity","Waiting")]);
            renderer.Resize(form.ClientSize.Width,form.ClientSize.Height,form.DeviceDpi/96f);
            renderer.Synchronize(Enumerable.Range(0,polish?1:3).Select(i=>new PrototypeStageSnapshot("App"+i,"Application "+i,
                Enumerable.Range(0,polish?3:i+2).Select(j=>(StageManager.Native.Window.IWindow)new FakeWindow(61000+i*100+j,"Application "+i,"test.exe")).ToArray(),DateTime.UtcNow)).ToArray());
            var hits=(List<CardHitTarget>)typeof(CompositionStageRenderer).GetField("_hitTargets",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(renderer)!;
            form.KeyDown+=(_,e)=> { if(e.KeyCode==System.Windows.Forms.Keys.B) renderer.SetDockEdge(SidebarEdge.Bottom); if(e.KeyCode==System.Windows.Forms.Keys.T) renderer.SetDockEdge(SidebarEdge.Top); };
            form.Resize+=(_,_)=>renderer.Resize(form.ClientSize.Width,form.ClientSize.Height,form.DeviceDpi/96f);
            form.MouseMove+=(_,e)=> { renderer.UpdatePointer(e.Location);var h=renderer.HitTest(e.Location);if(h is not null && renderer.CanExpandOnHover(h)) renderer.TryExpandHoveredPrimaryCard(e.Location,h.StageKey); };
            form.MouseClick+=(_,e)=>renderer.ActivateAt(e.Location);
            form.MouseWheel+=(_,e)=>renderer.Scroll(e.Delta,e.Location);
            using var timer=new System.Windows.Forms.Timer { Interval=50 };timer.Tick+=(_,_)=> { var point=form.PointToClient(System.Windows.Forms.Cursor.Position); renderer.UpdateEdgeScroll(point,System.Windows.Forms.Control.MouseButtons!=System.Windows.Forms.MouseButtons.None); renderer.PollPointer(point); };timer.Start();
            System.Windows.Forms.Application.Run(form);
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
    }
    public static void Check(CompositionStageRenderer renderer,List<CardHitTarget> hits)
    {
        static void Assert(bool value,string message) { if(!value) throw new Exception("Top panel: "+message); }
        static PrototypeStageSnapshot Group(string key,int count,int start) => new(key,key,
            Enumerable.Range(start,count).Select(i => (StageManager.Native.Window.IWindow)new FakeWindow(i,key,key+".exe")).ToArray(),DateTime.UtcNow);
        foreach(var dpi in new[]{1f,1.25f,1.5f,2f})
        foreach(var screen in new[]{new Vector2(1080,1920),new Vector2(1920,1080)})
        foreach(var count in new[]{2,6,10,20})
        {
            var card=new Vector2(118,180)*dpi;
            var layout=TopPanelLayout.Calculate(screen*dpi,card,dpi,10000);
            Assert(layout.Left>=12*dpi && layout.Left+layout.Width<=screen.X*dpi-12*dpi+.01f,"side margins");
            Assert(layout.Height<=screen.Y*dpi*.45f+.01f,"height limit");
            var cells=Enumerable.Range(0,count).Select(i=>layout.Child(i,layout.Top,dpi)).ToArray();
            for(var i=0;i<count;i++) for(var j=i+1;j<count;j++)
                Assert(Math.Abs(cells[i].X-cells[j].X)>=card.X+25.99f*dpi || Math.Abs(cells[i].Y-cells[j].Y)>=card.Y+25.99f*dpi,"grid overlap");
        }
        renderer.SetAiUsageCardEnabled(false);
        renderer.SetNotificationAreaCardEnabled(false);
        renderer.SetPinButtonEnabled(true);
        renderer.SetDockEdge(SidebarEdge.Top);
        renderer.Resize(1080,1920,1);
        renderer.CollapseExpandedStage(true);
        renderer.Synchronize([Group("A",20,51000),Group("B",6,52000),Group("C",2,53000)]);
        var original=hits.Where(h=>h.IsPrimaryCard).ToDictionary(h=>h.StageKey,h=>h.Polygon.ToArray());
        var reserved=renderer.SidebarThickness;
        void Expand(string key) => renderer.Activate(hits.Single(h=>h.StageKey==key && h.IsPrimaryCard));
        void Pin(string key) => renderer.Activate(hits.Single(h=>h.StageKey==key && h.IsPinButton));
        Expand("A"); Pin("A"); Expand("B"); Pin("B"); Expand("C");
        Assert(renderer.IsStagePinned("A") && renderer.IsStagePinned("B"),"independent pins");
        Assert(hits.Count(h=>h.IsTopPanel && h.Window is not null)==28,"all children present");
        Assert(renderer.SidebarThickness==reserved,"expansion changed Focus reservation");
        foreach(var main in hits.Where(h=>h.IsPrimaryCard)) Assert(main.Polygon.SequenceEqual(original[main.StageKey]),"main row moved");
        var child=hits.First(h=>h.IsTopPanel && h.StageKey=="A" && h.Window is not null);
        Assert(renderer.Activate(child)==child.Window && renderer.IsStagePinned("A"),"child activation collapsed pin");
        var stableOrder=hits.Where(h=>h.IsTopPanel && h.Window is not null).Select(h=>h.Window!.Handle).ToArray();
        renderer.Synchronize([Group("A",20,51000),Group("B",6,52000),Group("C",2,53000)]);
        Assert(hits.Where(h=>h.IsTopPanel && h.Window is not null).Select(h=>h.Window!.Handle).SequenceEqual(stableOrder),"snapshot reordered children");
        var mainScroll=renderer.ScrollTranslationX;
        var panelPoint=new Point(renderer.TopPanelBounds.Left+8,renderer.TopPanelBounds.Top+8);
        renderer.Scroll(-2400,panelPoint);
        Assert(renderer.ScrollTranslationX==mainScroll,"panel wheel changed main row");
        var visible=hits.Where(h=>h.IsTopPanel && h.Window is not null).Select(h=>(Hit:h,Center:new Point((int)h.Polygon.Average(p=>p.X),(int)(h.Polygon.Average(p=>p.Y)+renderer.TargetTranslation(h).Y))))
            .Where(p=>renderer.TopPanelBounds.Contains(p.Center)).ToArray();
        Assert(visible.Length>0,"panel scrolling hid all children");
        foreach(var item in visible) Assert(renderer.HitTest(item.Center)?.Window?.Handle==item.Hit.Window!.Handle,"scrolled hit target");
        foreach(var polygon in renderer.GetInteractivePolygons(true)) foreach(var p in polygon)
            Assert(p.Y>=renderer.TopPanelBounds.Top-.1 && p.Y<=renderer.TopPanelBounds.Bottom+.1,"unclipped native region");
        var panelTranslation=renderer.TargetTranslation(child).Y;
        renderer.Scroll(-120);
        Assert(renderer.TargetTranslation(child).Y==panelTranslation,"main wheel moved panel");
        typeof(CompositionStageRenderer).GetField("_topLastInside",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(renderer,DateTime.UtcNow.AddSeconds(-2));
        renderer.PollPointer(new Point(1070,1910));
        Assert(!renderer.IsStageExpanded("C") && renderer.IsStagePinned("A") && renderer.IsStagePinned("B"),"temporary timeout affected pins");
        renderer.Activate(hits.Single(h=>h.IsExpandAllButton));
        Assert(hits.Any(h=>h.IsTopPanel && h.StageKey=="C"),"expand all missing group");
        renderer.Activate(hits.Single(h=>h.IsExpandAllButton));
        Assert(renderer.IsStagePinned("A") && renderer.IsStagePinned("B") && !renderer.IsStageExpanded("C"),"expand all off lost pins");
        renderer.Synchronize([Group("A",1,51000),Group("B",6,52000)]);
        Assert(renderer.IsStagePinned("A") && hits.Any(h=>h.IsTopPanel && h.StageKey=="A"),"pin lost when one window remains");
        renderer.SetSidebarVisible(false,false); renderer.SetSidebarVisible(true,false);
        Assert(renderer.IsStagePinned("A") && renderer.IsStagePinned("B"),"hide lost pins");
        renderer.Synchronize([Group("B",6,52000)]);
        Assert(!renderer.IsStagePinned("A") && renderer.IsStagePinned("B"),"closing one application affected another");
        renderer.SetDockEdge(SidebarEdge.Bottom);
        Assert(renderer.TopPanelBounds.IsEmpty && renderer.GetInteractivePolygons(true).Count==0,"top panel leaked to bottom mode");
        Console.WriteLine("PASS top shared panel: DPI/grid, independent pins, child order, scroll/hits, close/hide, Focus thickness");
    }
}
