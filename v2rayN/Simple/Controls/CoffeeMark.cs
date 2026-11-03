using Avalonia.Media;
namespace WorldScience.Controls;
/// <summary>Scalable coffee cup for the support entry. No bitmap or colour assumption.</summary>
public sealed class CoffeeMark : Control
{
    public static readonly StyledProperty<IBrush?> InkProperty = AvaloniaProperty.Register<CoffeeMark, IBrush?>(nameof(Ink), Brushes.Black);
    public IBrush? Ink { get => GetValue(InkProperty); set => SetValue(InkProperty, value); }
    private static readonly Geometry Outline = Geometry.Parse("M12 22H45V38C45 47 38 51 29 51C19 51 12 47 12 38Z M45 25H49C59 25 59 39 45 39 M9 57H49 M24 14C18 10 29 7 24 3 M35 14C29 10 40 7 35 3");
    static CoffeeMark() => AffectsRender<CoffeeMark>(InkProperty);
    public override void Render(DrawingContext context)
    {
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 64;
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
            context.DrawGeometry(null, new Pen(Ink, 3.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), Outline);
    }
}
