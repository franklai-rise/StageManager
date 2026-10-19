using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.UI.Composition;

namespace StageManager.Card3DPrototype;

internal sealed class TopPanelLabel : IDisposable
{
    private readonly Compositor _compositor;
    private readonly D3DCompositionDevice _graphics;
    private CardSwapChain? _surface;
    private CompositionSurfaceBrush? _brush;
    private string _key = "";
    public SpriteVisual Root { get; }
    public TopPanelLabel(Compositor compositor,D3DCompositionDevice graphics)
    { _compositor=compositor; _graphics=graphics; Root=compositor.CreateSpriteVisual(); }
    internal bool LightSurface { get; private set; }
    internal static Color BackgroundColor(bool backdrop, bool lightSurface) =>
        backdrop && !lightSurface ? Color.FromArgb(215,25,30,39) : Color.Transparent;
    internal static Color TextColor(bool lightSurface) =>
        lightSurface ? Color.FromArgb(235,49,61,76) : Color.FromArgb(240,240,244,250);
    public void Set(string text,Vector2 size,float dpi,bool centered=false, bool backdrop=false, bool lightSurface=false)
    {
        var key=$"{text}|{size}|{dpi}|{centered}|{backdrop}|{lightSurface}";
        if (_key==key) return;
        LightSurface=lightSurface;
        Root.Brush=null; _brush?.Dispose(); _surface?.Dispose();
        var width=Math.Max(1,(int)Math.Ceiling(size.X)); var height=Math.Max(1,(int)Math.Ceiling(size.Y));
        using var bitmap=new Bitmap(width,height,PixelFormat.Format32bppPArgb);
        using (var g=Graphics.FromImage(bitmap))
        {
            g.Clear(BackgroundColor(backdrop,lightSurface));
            g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
            using var font=new Font("Microsoft YaHei UI",11*dpi,FontStyle.Regular,GraphicsUnit.Pixel);
            using var brush=new SolidBrush(TextColor(lightSurface));
            using var format=new StringFormat { Alignment=centered?StringAlignment.Center:StringAlignment.Near,LineAlignment=StringAlignment.Center,Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap };
            g.DrawString(text,font,brush,new RectangleF(6*dpi,0,Math.Max(1,width-12*dpi),height),format);
            using var pen=new Pen(Color.FromArgb(60,225,235,250),Math.Max(1,dpi));
            if (!centered) g.DrawLine(pen,0,height-1,width,height-1);
        }
        var bits=bitmap.LockBits(new Rectangle(0,0,width,height),ImageLockMode.ReadOnly,PixelFormat.Format32bppPArgb);
        var pixels=new byte[width*height*4];
        try { for(var y=0;y<height;y++) Marshal.Copy(bits.Scan0+y*bits.Stride,pixels,y*width*4,width*4); }
        finally { bitmap.UnlockBits(bits); }
        _surface=_graphics.CreateSurface(_compositor,width,height); _surface.Upload(pixels);
        _brush=_compositor.CreateSurfaceBrush(_surface.CompositionSurface); Root.Brush=_brush;Root.Size=size;_key=key;
    }
    public void Dispose() { Root.Dispose();_brush?.Dispose();_surface?.Dispose(); }
}

internal sealed class TopCardChrome : IDisposable
{
    private readonly ContainerVisual _parent;
    private readonly ShapeVisual _outline;
    private readonly CompositionRoundedRectangleGeometry _geometry;
    private readonly CompositionColorBrush _stroke;
    private readonly CompositionColorBrush _fill;
    private readonly CompositionSpriteShape _shape;
    private readonly SpriteVisual? _background;
    private readonly CompositionShadow? _previousShadow;
    private readonly DropShadow _shadow;
    public TopCardChrome(Compositor compositor,ContainerVisual parent)
    {
        _parent=parent;
        _geometry=compositor.CreateRoundedRectangleGeometry();
        _stroke=compositor.CreateColorBrush(Windows.UI.Color.FromArgb(45,225,235,250));
        _fill=compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0,255,255,255));
        _shape=compositor.CreateSpriteShape(_geometry);_shape.StrokeBrush=_stroke;_shape.FillBrush=_fill;
        _outline=compositor.CreateShapeVisual();_outline.Shapes.Add(_shape);
        parent.Children.InsertAtTop(_outline);
        _background=parent.Children.OfType<SpriteVisual>().FirstOrDefault(v=>v.Brush is CompositionColorBrush && v.Size==parent.Size);
        _shadow=compositor.CreateDropShadow();_shadow.Opacity=.18f;
        if (_background is not null) { _previousShadow=_background.Shadow; _background.Shadow=_shadow; }
    }
    public void Layout(float dpi)
    {
        _outline.Size=_parent.Size;_geometry.Offset=new Vector2(.5f*dpi);
        _geometry.Size=_parent.Size-new Vector2(dpi);_geometry.CornerRadius=new Vector2(8*dpi);
        _shape.StrokeThickness=Math.Max(1,dpi);_shadow.BlurRadius=12*dpi;_shadow.Offset=new(0,3*dpi,0);
    }
    public void Dispose()
    {
        if(_background is not null) _background.Shadow=_previousShadow;
        _parent.Children.Remove(_outline);_outline.Dispose();_shape.Dispose();_geometry.Dispose();_stroke.Dispose();_fill.Dispose();_shadow.Dispose();
    }
    public void SetHovered(bool hovered)
    { _stroke.Color=Windows.UI.Color.FromArgb((byte)(hovered?135:45),225,235,250);_fill.Color=Windows.UI.Color.FromArgb((byte)(hovered?18:0),255,255,255); }
}
