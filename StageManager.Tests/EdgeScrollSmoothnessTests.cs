using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using StageManager.Card3DPrototype;

internal static class EdgeScrollSmoothnessTests
{
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    public static void Run()
    {
        var range = new SidebarScrollRange(0, 10000);
        foreach (var dpi in new[] { .75f, 1f, 1.25f, 1.5f, 2f })
        {
            var motion = new EdgeScrollMotion();
            var delta = motion.Advance(480*dpi, 1d/60, 0, range, dpi);
            Check(delta > 0 && delta < 2*dpi && motion.Velocity > 0 && motion.Velocity < 120*dpi, "Edge start jumps straight to cruising speed.");
            var previous = motion.Velocity;
            for (var i=0;i<40;i++) {
                motion.Advance(480*dpi, 1d/60, 0, range, dpi);
                Check(motion.Velocity >= previous && motion.Velocity <= 480*dpi, "Acceleration overshoots or oscillates."); previous=motion.Velocity;
            }
            delta=motion.Advance(-480*dpi,1d/60,5000,range,dpi);
            Check(delta<0 && motion.Velocity<0, "Direction reversal briefly moves the wrong way.");
            motion.Stop(); Check(motion.Velocity==0, "Leave/click did not stop immediately.");
            motion.Advance(480*dpi,1,0,range,dpi);
            Check(motion.Velocity<120*dpi, "A stalled frame retained fast catch-up velocity.");

            var distances=new List<double>();
            foreach(var rate in new[]{30,60,120,144}) {
                var paced=new EdgeScrollMotion(); float position=0;
                for(var i=0;i<rate*2;i++) position+=paced.Advance(480*dpi,1d/rate,position,range,dpi);
                distances.Add(position/dpi);
            }
            Check(distances.Max()-distances.Min()<.02, "Travel depends on the refresh rate.");
            var irregular = new EdgeScrollMotion(); var time=0d; var jitterPosition=0f; var frame=0;
            var schedule=new[]{.009,.024,.012,.031,.016};
            while(time<2) { var dt=Math.Min(schedule[frame++%schedule.Length],2-time); jitterPosition+=irregular.Advance(480*dpi,dt,jitterPosition,range,dpi); time+=dt; }
            Check(Math.Abs(jitterPosition/dpi-distances[0])<.02,"Irregular frame intervals create extra travel or stutter.");
            var end=new EdgeScrollMotion(); var at=480f*dpi; var smallRange=new SidebarScrollRange(0,500*dpi);
            for(var i=0;i<240;i++) {
                var step=end.Advance(480*dpi,1d/60,at,smallRange,dpi); at+=step;
                Check(step>=0 && at<=smallRange.Maximum+.001, "End brake overshot or bounced.");
            }
            Check(Math.Abs(at-smallRange.Maximum)<.02*dpi, "End brake never reaches the last card.");
        }
        var spring=new SidebarScrollMotion(); spring.SetRange(new(-100,500),1); spring.AddWheel(-120,500,false);
        Check(spring.Position==500,"Wheel setup failed.");
        spring.MoveEdgeDelta(-2,new(0,200));
        Check(spring.Position==498,"Edge takeover jumped from wheel extra travel to a content bound.");
        spring.MoveEdgeDelta(2,new(0,200));
        Check(spring.Position==498,"Edge travel moved farther beyond the content range.");
        for(var i=0;i<300;i++) spring.MoveEdgeDelta(-2,new(0,200));
        Check(spring.Position==0,"Inward travel did not reach the content start.");

        foreach(var horizontal in new[]{false,true})
        foreach(var direction in new[]{-1,1})
        foreach(var dpi in new[]{1f,1.5f,2f}) {
            var intent=new EdgeScrollIntent(); var point=new PointF(100*dpi,100*dpi);
            Check(!intent.Allows(point,horizontal,240*direction,dpi,true),"Initial button hover lost priority.");
            Check(intent.Allows(point,horizontal,240*direction,dpi,false),"Blank edge did not begin.");
            Check(intent.Allows(point,horizontal,240*direction,dpi,true),"Moving card stopped a stationary pointer session.");
            var outward=horizontal?new PointF(point.X+2*direction*dpi,point.Y):new PointF(point.X,point.Y+2*direction*dpi);
            Check(intent.Allows(outward,horizontal,240*direction,dpi,true),"Outward pointer motion was mistaken for button targeting.");
            var inward=horizontal?new PointF(point.X-8*direction*dpi,point.Y):new PointF(point.X,point.Y-8*direction*dpi);
            Check(!intent.Allows(inward,horizontal,240*direction,dpi,true),"Moving inward to a button did not release scrolling.");
            intent.Allows(point,horizontal,240*direction,dpi,false); intent.Reset();
            Check(!intent.Allows(point,horizontal,240*direction,dpi,true),"Hidden/menu state retained an old scroll session.");
        }
    }

    public static void ProbeTimers()
    {
        Exception? error = null;
        var thread = new Thread(() => { try {
            foreach (var interval in new[] {16, 8})
            {
                using var context = new ApplicationContext();
                using var timer = new System.Windows.Forms.Timer { Interval = interval };
                var samples = new List<double>(); var previous = Stopwatch.GetTimestamp();
                timer.Tick += (_, _) => {
                    var now = Stopwatch.GetTimestamp();
                    samples.Add(Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds); previous = now;
                    if(samples.Count >= 100) {timer.Stop(); context.ExitThread();}
                };
                timer.Start(); Application.Run(context);
                samples.RemoveAt(0); samples.Sort();
                Console.WriteLine($"Active timer {interval}ms: mean={samples.Average():F2}ms median={samples[49]:F2}ms p95={samples[94]:F2}ms max={samples[^1]:F2}ms");
            }
        } catch(Exception e) {error=e;} });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(30))) throw new Exception("Frame timing probe timed out.");
        if(error is not null) throw error;
    }
}
