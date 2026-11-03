using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AppManager
{
    /// <summary>
    /// Rotellina di caricamento. La rotazione è creata dal codice (quindi modificabile) e gira
    /// solo mentre il controllo è visibile: niente animazioni nascoste che consumano CPU.
    /// </summary>
    public sealed class Spinner : FrameworkElement
    {
        private const double Thickness = 4;
        private readonly RotateTransform rotation = new RotateTransform();

        public Spinner()
        {
            Width = 36;
            Height = 36;
            RenderTransformOrigin = new Point(0.5, 0.5);
            RenderTransform = rotation;
            IsVisibleChanged += (s, e) => UpdateAnimation();
        }

        /// <summary>Colori del tema in uso: un cambio di tema ricrea i controlli, quindi basta leggerli qui.</summary>
        private Pen MakePen(string brushKey, Color fallback)
        {
            var brush = TryFindResource(brushKey) as Brush ?? new SolidColorBrush(fallback);
            var pen = new Pen(brush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            return pen;
        }

        private void UpdateAnimation()
        {
            if (IsVisible)
                rotation.BeginAnimation(RotateTransform.AngleProperty,
                    new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
            else
                rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double r = (Math.Min(ActualWidth, ActualHeight) - Thickness) / 2;
            if (r <= 0) return;
            var c = new Point(ActualWidth / 2, ActualHeight / 2);
            dc.DrawEllipse(null, MakePen("Surface3", Color.FromRgb(0x26, 0x2C, 0x37)), c, r, r);
            var arc = new StreamGeometry();
            using (var ctx = arc.Open())
            {
                ctx.BeginFigure(new Point(c.X, c.Y - r), false, false);
                ctx.ArcTo(new Point(c.X + r, c.Y), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            }
            arc.Freeze();
            dc.DrawGeometry(null, MakePen("Accent", Color.FromRgb(0x5B, 0x8C, 0xFF)), arc);
        }
    }
}
