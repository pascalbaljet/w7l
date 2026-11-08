using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace KidLauncher
{
    // One drawing layer, not a control or timer per sparkle. Nothing here takes input.
    public sealed class MagicLayer : FrameworkElement
    {
        private static readonly Geometry Star = CreateStar();
        private static readonly Brush[] Inks = {
            Frozen(new SolidColorBrush(Color.FromRgb(255, 222, 145))),
            Frozen(new SolidColorBrush(Color.FromRgb(255, 249, 222))),
            Frozen(new SolidColorBrush(Color.FromRgb(132, 231, 224))),
            Frozen(new SolidColorBrush(Color.FromRgb(241, 179, 255)))
        };
        private static readonly Brush Halo = Frozen(new RadialGradientBrush(Color.FromArgb(80, 255, 221, 144), Colors.Transparent));
        private readonly List<Particle> particles = new List<Particle>();
        private readonly Random random = new Random();
        private readonly Stopwatch clock = new Stopwatch();
        private bool animate = true, ambient, rendering;
        private double lastFrame = -1, lastTrail = -1;
        private Point lastPoint;

        public bool Animate
        {
            get { return animate; }
            set { animate = value; RefreshMotion(); }
        }
        public bool Ambient
        {
            get { return ambient; }
            set { ambient = value; RefreshMotion(); }
        }
        private bool Motion => animate && IsLoaded && IsVisible && IsEnabled && SystemParameters.ClientAreaAnimation;

        public MagicLayer()
        {
            IsHitTestVisible = false;
            Focusable = false;
            ClipToBounds = true;
            Loaded += (sender, args) => { SystemParameters.StaticPropertyChanged += SettingsChanged; RefreshMotion(); };
            Unloaded += (sender, args) => { SystemParameters.StaticPropertyChanged -= SettingsChanged; Stop(); };
            IsVisibleChanged += (sender, args) => RefreshMotion();
            IsEnabledChanged += (sender, args) => RefreshMotion();
        }

        private void SettingsChanged(object sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) RefreshMotion();
        }

        public void Trail(Point point)
        {
            if (!Motion) return;
            double now = clock.Elapsed.TotalSeconds;
            if (lastTrail >= 0 && (now - lastTrail < 0.035 || (point - lastPoint).Length < 9)) return;
            lastTrail = now;
            lastPoint = point;
            Add(new Point(point.X - 8, point.Y + 16), new Vector(random.NextDouble() * 16 - 8, 12), 4 + random.NextDouble() * 4, 0.8);
            Add(new Point(point.X + 8, point.Y + 20), new Vector(random.NextDouble() * 24 - 12, 18), 2 + random.NextDouble() * 2, 0.55);
            Start();
        }

        public void Burst(Point point, int count)
        {
            if (!Motion) return;
            for (int i = 0; i < count; i++)
            {
                double angle = Math.PI * 2 * i / count + random.NextDouble() * 0.4;
                double speed = 28 + random.NextDouble() * 68;
                Add(point, new Vector(Math.Cos(angle) * speed, Math.Sin(angle) * speed - 18),
                    3 + random.NextDouble() * 5, 0.65 + random.NextDouble() * 0.65);
            }
            Start();
        }

        private void Add(Point origin, Vector velocity, double size, double life)
        {
            // Rapid pointer movement or repeated clicks cannot grow an unbounded scene.
            if (particles.Count == 120) particles.RemoveAt(0);
            particles.Add(new Particle { Origin = origin, Velocity = velocity, Size = size, Life = life,
                Born = clock.Elapsed.TotalSeconds, Angle = random.NextDouble() * Math.PI,
                Spin = random.NextDouble() * 2 - 1, Ink = Inks[random.Next(Inks.Length)] });
        }

        private void RefreshMotion()
        {
            if (!Motion) Stop();
            else if (ambient || particles.Count > 0) Start();
            InvalidateVisual();
        }

        private void Start()
        {
            if (rendering || !Motion) return;
            clock.Start();
            rendering = true;
            CompositionTarget.Rendering += Frame;
        }

        private void Stop()
        {
            if (rendering) CompositionTarget.Rendering -= Frame;
            rendering = false;
            particles.Clear();
            clock.Reset();
            lastFrame = lastTrail = -1;
        }

        private void Frame(object sender, EventArgs args)
        {
            double now = clock.Elapsed.TotalSeconds;
            // Cap drawing at 30 fps for the older machines this launcher serves.
            if (now - lastFrame < 1.0 / 30) return;
            lastFrame = now;
            particles.RemoveAll(p => now - p.Born >= p.Life);
            InvalidateVisual();
            if (!ambient && particles.Count == 0) Stop();
        }

        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);
            double now = clock.Elapsed.TotalSeconds;
            if (ambient)
            {
                for (int i = 0; i < 64; i++)
                {
                    double phase = i * 1.7;
                    double wave = Motion ? Math.Sin(now * 1.15 + phase) : Math.Sin(phase);
                    var point = new Point((0.04 + (i * 0.61803398875 % 1) * 0.92) * ActualWidth,
                        (0.04 + (i * 0.38196601125 % 1) * 0.92) * ActualHeight);
                    if (Motion) point.Y += Math.Sin(now * 0.35 + phase) * 7;
                    DrawStar(drawing, point, 3 + i % 5, phase, 0.35 + wave * 0.22, Inks[i % Inks.Length]);
                }
            }
            foreach (var particle in particles)
            {
                double age = now - particle.Born;
                double progress = Math.Max(0, Math.Min(1, age / particle.Life));
                var point = particle.Origin + particle.Velocity * age + new Vector(0, 22 * age * age);
                DrawStar(drawing, point, particle.Size * (0.45 + 0.55 * Math.Sin(progress * Math.PI)),
                    particle.Angle + age * particle.Spin, (1 - progress) * (1 - progress), particle.Ink);
            }
        }

        private static void DrawStar(DrawingContext drawing, Point point, double size, double angle, double opacity, Brush ink)
        {
            drawing.PushOpacity(opacity);
            drawing.DrawEllipse(Halo, null, point, size * 3, size * 3);
            double cosine = Math.Cos(angle) * size, sine = Math.Sin(angle) * size;
            drawing.PushTransform(new MatrixTransform(new Matrix(cosine, sine, -sine, cosine, point.X, point.Y)));
            drawing.DrawGeometry(ink, null, Star);
            drawing.Pop();
            drawing.Pop();
        }

        private static Brush Frozen(Brush brush) { brush.Freeze(); return brush; }
        private static Geometry CreateStar()
        {
            var star = new StreamGeometry();
            using (var path = star.Open())
            {
                path.BeginFigure(new Point(0, -1), true, true);
                path.PolyLineTo(new[] { new Point(0.2, -0.2), new Point(1, 0), new Point(0.2, 0.2),
                    new Point(0, 1), new Point(-0.2, 0.2), new Point(-1, 0), new Point(-0.2, -0.2) }, true, false);
            }
            star.Freeze();
            return star;
        }

        private sealed class Particle
        {
            public Point Origin;
            public Vector Velocity;
            public double Size, Life, Born, Angle, Spin;
            public Brush Ink;
        }
    }
}
