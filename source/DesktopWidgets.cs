// SPDX-License-Identifier: GPL-2.0-or-later
// Desktop widgets: small per-pixel-alpha windows (UpdateLayeredWindow) drawn with GDI+.
// They live in the same process as the tray icon and use LumeService, so a click on any of
// them switches every RGB zone on or off directly over USB.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LumeRgb
{
    public sealed class WidgetChip
    {
        public string Label, Action;
        public Color Color, Glow;
    }

    public sealed class WidgetVisual
    {
        public bool On, Pending, Connected = true, Hover;
        public float OnAmount, Time, Ripple = -1;
        public int HoverChip = -1;
        public Color[] Colors = new Color[0];
        public string Title = "Lume RGB", Status = "";
        public List<WidgetChip> Chips = new List<WidgetChip>();
    }

    public static class WidgetRenderer
    {
        static readonly Color[] Fallback = { Color.FromArgb(124, 108, 255), Color.FromArgb(53, 217, 195), Color.FromArgb(255, 93, 145) };
        static readonly Color OffInk = Color.FromArgb(104, 110, 140);

        public static Size LogicalSize(string style)
        {
            if (style == "pill") return new Size(300, 92);
            if (style == "panel") return new Size(344, 178);
            return new Size(150, 150);
        }

        public static void Draw(Graphics g, string style, WidgetVisual v, float s)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; g.CompositingQuality = CompositingQuality.HighQuality;
            g.Clear(Color.Transparent);
            if (style == "pill") DrawPill(g, v, s);
            else if (style == "panel") DrawPanel(g, v, s);
            else DrawOrb(g, v, s, 75 * s, 75 * s, 1f, true);
        }

        // Action under a point (logical coordinates already divided by scale), or null for the drag area.
        public static string HitTest(string style, WidgetVisual v, PointF p)
        {
            if (style == "panel")
            {
                if (Dist(p, new PointF(52, 50)) <= 30) return "power";
                int i = ChipAt(v, p); return i >= 0 ? v.Chips[i].Action : null;
            }
            if (style == "pill") return p.X >= 10 && p.X <= 290 && p.Y >= 10 && p.Y <= 82 ? "power" : null;
            return Dist(p, new PointF(75, 75)) <= 58 ? "power" : null;
        }
        public static int ChipAt(WidgetVisual v, PointF p)
        {
            int count = Math.Min(5, v.Chips.Count); if (count == 0) return -1;
            float w = 300f / count;
            for (int i = 0; i < count; i++)
            {
                float cx = 22 + w * i + w / 2, cy = 124;
                if (Math.Abs(p.X - cx) <= w / 2 - 2 && p.Y >= cy - 22 && p.Y <= cy + 34) return i;
            }
            return -1;
        }
        static float Dist(PointF a, PointF b) { float dx = a.X - b.X, dy = a.Y - b.Y; return (float)Math.Sqrt(dx * dx + dy * dy); }

        static Color[] Palette(WidgetVisual v) { return v.Colors != null && v.Colors.Length > 0 ? v.Colors.Select(Vivid).ToArray() : Fallback; }
        // Hardware colours can be very dark at low brightness; widgets show the hue at full strength.
        static Color Vivid(Color c)
        {
            int max = Math.Max(c.R, Math.Max(c.G, c.B));
            if (max < 8) return Color.FromArgb(124, 108, 255);
            float k = 255f / max; return Color.FromArgb(Clamp(c.R * k), Clamp(c.G * k), Clamp(c.B * k));
        }
        static int Clamp(float x) { return x < 0 ? 0 : x > 255 ? 255 : (int)x; }
        static Color Lerp(Color a, Color b, float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb(Clamp(a.A + (b.A - a.A) * t), Clamp(a.R + (b.R - a.R) * t), Clamp(a.G + (b.G - a.G) * t), Clamp(a.B + (b.B - a.B) * t));
        }
        static Color Alpha(Color c, float a) { return Color.FromArgb(Clamp(a), c.R, c.G, c.B); }
        static Color Cycle(Color[] colors, float u)
        {
            u = u - (float)Math.Floor(u); float x = u * colors.Length; int i = (int)x;
            return Lerp(colors[i % colors.Length], colors[(i + 1) % colors.Length], x - i);
        }
        static Color Average(Color[] colors)
        {
            return Color.FromArgb((int)colors.Average(c => c.R), (int)colors.Average(c => c.G), (int)colors.Average(c => c.B));
        }
        static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var path = new GraphicsPath(); float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure(); return path;
        }
        static void SoftGlow(Graphics g, float cx, float cy, float radius, Color color, float alpha)
        {
            if (alpha <= 1) return;
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(cx - radius, cy - radius, radius * 2, radius * 2);
                using (var brush = new PathGradientBrush(path))
                {
                    brush.CenterColor = Alpha(color, alpha); brush.SurroundColors = new[] { Alpha(color, 0) };
                    var blend = new Blend(); blend.Positions = new[] { 0f, .45f, 1f }; blend.Factors = new[] { 0f, .7f, 1f }; brush.Blend = blend;
                    g.FillPath(brush, path);
                }
            }
        }
        static void RoundedGlow(Graphics g, RectangleF r, float radius, Color color, float alpha, float s)
        {
            if (alpha <= 1) return;
            for (int i = 6; i >= 1; i--)
            {
                var grown = RectangleF.Inflate(r, i * 1.6f * s, i * 1.6f * s);
                using (var path = Rounded(grown, radius + i * 1.6f * s))
                using (var brush = new SolidBrush(Alpha(color, alpha * (7 - i) / 28f))) g.FillPath(brush, path);
            }
        }
        static void PowerGlyph(Graphics g, float cx, float cy, float r, Color color, float width)
        {
            using (var pen = new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawArc(pen, cx - r, cy - r, r * 2, r * 2, -55, 290);
                g.DrawLine(pen, cx, cy - r * 1.28f, cx, cy - r * .2f);
            }
        }
        static void Text(Graphics g, string text, string family, float size, FontStyle style, Color color, RectangleF r, StringAlignment align)
        {
            using (var font = new Font(family, size, style, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat { Alignment = align, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(text, font, brush, r, format);
        }

        // The orb is reused (scaled down) as the power button of the panel widget.
        static void DrawOrb(Graphics g, WidgetVisual v, float s, float cx, float cy, float k, bool full)
        {
            var colors = Palette(v); Color avg = Average(colors); float on = v.OnAmount;
            float pulse = .82f + .18f * (float)Math.Sin(v.Time * 2.1);
            float ringR = 50 * s * k, ringW = 8.5f * s * k, diskR = 41 * s * k;
            if (full) SoftGlow(g, cx, cy, 74 * s * k, avg, 150 * on * pulse);
            // Light ring: the zone colours circulate around it while everything is on.
            int segments = 96; float rotation = v.Time * 38;
            using (var track = new Pen(Color.FromArgb(70, 40, 42, 60), ringW)) g.DrawEllipse(track, cx - ringR, cy - ringR, ringR * 2, ringR * 2);
            for (int i = 0; i < segments; i++)
            {
                float u = i / (float)segments; Color c = Cycle(colors, u + rotation / 360f);
                using (var pen = new Pen(Lerp(Color.FromArgb(255, 44, 46, 66), c, on), ringW))
                    g.DrawArc(pen, cx - ringR, cy - ringR, ringR * 2, ringR * 2, u * 360 - 90, 360f / segments + .9f);
            }
            using (var inner = new Pen(Color.FromArgb((int)(60 + 60 * on), 255, 255, 255), 1f * s)) g.DrawEllipse(inner, cx - ringR + ringW / 2, cy - ringR + ringW / 2, (ringR - ringW / 2) * 2, (ringR - ringW / 2) * 2);
            // Glass disk.
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(cx - diskR, cy - diskR, diskR * 2, diskR * 2);
                using (var brush = new PathGradientBrush(path))
                {
                    brush.CenterPoint = new PointF(cx, cy - diskR * .35f);
                    brush.CenterColor = Lerp(Color.FromArgb(250, 36, 34, 56), Lerp(Color.FromArgb(250, 36, 34, 56), avg, .35f), on);
                    brush.SurroundColors = new[] { Color.FromArgb(252, 12, 11, 22) };
                    g.FillPath(brush, path);
                }
                var top = new RectangleF(cx - diskR, cy - diskR, diskR * 2, diskR * 1.1f);
                using (var sheen = new LinearGradientBrush(top, Color.FromArgb(v.Hover ? 46 : 30, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                {
                    var state = g.Save(); g.SetClip(path); g.FillEllipse(sheen, cx - diskR * .8f, cy - diskR * .98f, diskR * 1.6f, diskR * 1.15f); g.Restore(state);
                }
            }
            float iconR = 13.5f * s * k, iconY = full ? cy - 5 * s : cy;
            Color iconColor = Lerp(OffInk, Color.White, on);
            if (on > .02f) PowerGlyph(g, cx, iconY, iconR, Alpha(avg, 90 * on), 9 * s * k);
            PowerGlyph(g, cx, iconY, iconR, iconColor, 3.4f * s * k);
            if (full)
                Text(g, v.Pending ? (v.On ? "LIGANDO" : "DESLIGANDO") : !v.Connected ? "SEM USB" : v.On ? "LIGADO" : "DESLIGADO", "Segoe UI", 8.4f * s, FontStyle.Bold,
                    Lerp(Color.FromArgb(150, 140, 146, 175), Color.FromArgb(220, 255, 255, 255), on), new RectangleF(cx - diskR, cy + 15 * s, diskR * 2, 14 * s), StringAlignment.Center);
            if (v.Pending)
                using (var spin = new Pen(Color.FromArgb(230, 255, 255, 255), 3f * s * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(spin, cx - ringR, cy - ringR, ringR * 2, ringR * 2, v.Time * 420 % 360, 70);
            if (v.Ripple >= 0 && v.Ripple < 1)
            {
                float rr = ringR + (72 * s * k - ringR) * v.Ripple;
                using (var pen = new Pen(Alpha(Lerp(Color.White, avg, .4f), 200 * (1 - v.Ripple)), 2.4f * s * k)) g.DrawEllipse(pen, cx - rr, cy - rr, rr * 2, rr * 2);
            }
        }

        static void Body(Graphics g, RectangleF r, float radius, WidgetVisual v, float s, Color[] colors, Color avg)
        {
            float on = v.OnAmount;
            RoundedGlow(g, r, radius, avg, 90 * on * (.85f + .15f * (float)Math.Sin(v.Time * 2)), s);
            using (var path = Rounded(r, radius))
            {
                using (var fill = new LinearGradientBrush(r, Color.FromArgb(240, 30, 28, 48), Color.FromArgb(244, 14, 13, 25), 90f)) g.FillPath(fill, path);
                using (var tint = new LinearGradientBrush(r, Alpha(colors[0], 34 * on), Alpha(colors[colors.Length - 1], 10 * on), 0f)) g.FillPath(tint, path);
                if (v.Hover) using (var hover = new SolidBrush(Color.FromArgb(10, 255, 255, 255))) g.FillPath(hover, path);
                // Animated gradient border.
                float shift = v.Time * .12f;
                using (var border = new LinearGradientBrush(new RectangleF(r.X - 1, r.Y, r.Width + 2, r.Height), Color.White, Color.White, 0f))
                {
                    var blend = new ColorBlend(5);
                    blend.Positions = new[] { 0f, .25f, .5f, .75f, 1f };
                    blend.Colors = blend.Positions.Select(p => Alpha(Lerp(Color.FromArgb(90, 96, 120), Cycle(colors, p + shift), on), 70 + 150 * on)).ToArray();
                    border.InterpolationColors = blend;
                    using (var pen = new Pen(border, 1.4f * s)) g.DrawPath(pen, path);
                }
            }
        }
        static void ZoneDots(Graphics g, WidgetVisual v, float s, float right, float cy, float spacing)
        {
            var colors = v.Colors != null && v.Colors.Length > 0 ? v.Colors.Select(Vivid).ToArray() : Fallback;
            for (int i = 0; i < colors.Length; i++)
            {
                float x = right - (colors.Length - 1 - i) * spacing * s, d = 9 * s;
                Color c = Lerp(Color.FromArgb(70, 74, 96), colors[i], v.OnAmount);
                if (v.OnAmount > .05f) SoftGlow(g, x, cy, 11 * s, colors[i], 120 * v.OnAmount);
                using (var b = new SolidBrush(c)) g.FillEllipse(b, x - d / 2, cy - d / 2, d, d);
            }
        }
        static void DrawPill(Graphics g, WidgetVisual v, float s)
        {
            var colors = Palette(v); Color avg = Average(colors); float on = v.OnAmount;
            var body = new RectangleF(10 * s, 10 * s, 280 * s, 72 * s);
            Body(g, body, 36 * s, v, s, colors, avg);
            // Switch.
            var track = new RectangleF(24 * s, 28 * s, 66 * s, 36 * s);
            using (var path = Rounded(track, 18 * s))
            {
                using (var off = new SolidBrush(Color.FromArgb(255, 40, 42, 60))) g.FillPath(off, path);
                if (on > .01f)
                    using (var lit = new LinearGradientBrush(track, Alpha(colors[0], 255 * on), Alpha(colors[colors.Length > 1 ? 1 : 0], 255 * on), 0f)) g.FillPath(lit, path);
                using (var edge = new Pen(Color.FromArgb(60, 255, 255, 255), 1f * s)) g.DrawPath(edge, path);
            }
            float knobX = track.X + 18 * s + (track.Width - 36 * s) * Ease(on), knobY = track.Y + 18 * s, knobR = 14 * s;
            SoftGlow(g, knobX, knobY + 2 * s, 20 * s, Color.Black, 120);
            using (var knob = new SolidBrush(Color.FromArgb(255, 248, 248, 255))) g.FillEllipse(knob, knobX - knobR, knobY - knobR, knobR * 2, knobR * 2);
            PowerGlyph(g, knobX, knobY + .5f * s, 5.6f * s, Lerp(OffInk, Darker(avg), on), 2f * s);
            if (v.Pending)
                using (var spin = new Pen(Alpha(avg, 230), 2.2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(spin, knobX - knobR - 3 * s, knobY - knobR - 3 * s, (knobR + 3 * s) * 2, (knobR + 3 * s) * 2, v.Time * 420 % 360, 80);
            Text(g, v.Title, "Segoe UI Semibold", 15 * s, FontStyle.Bold, Color.FromArgb(245, 255, 255, 255), new RectangleF(104 * s, 26 * s, 130 * s, 20 * s), StringAlignment.Near);
            Text(g, v.Status, "Segoe UI", 12 * s, FontStyle.Regular, Lerp(Color.FromArgb(150, 160, 166, 190), Color.FromArgb(200, 225, 226, 245), on), new RectangleF(104 * s, 46 * s, 112 * s, 18 * s), StringAlignment.Near);
            ZoneDots(g, v, s, 268 * s, 46 * s, 12);
            if (v.Ripple >= 0 && v.Ripple < 1)
                using (var path = Rounded(RectangleF.Inflate(body, 8 * s * v.Ripple, 8 * s * v.Ripple), 36 * s + 8 * s * v.Ripple))
                using (var pen = new Pen(Alpha(Lerp(Color.White, avg, .4f), 170 * (1 - v.Ripple)), 2f * s)) g.DrawPath(pen, path);
        }
        static float Ease(float t) { t = Math.Max(0, Math.Min(1, t)); return t * t * (3 - 2 * t); }
        static Color Darker(Color c) { return Color.FromArgb(c.R * 3 / 4, c.G * 3 / 4, c.B * 3 / 4); }

        static void DrawPanel(Graphics g, WidgetVisual v, float s)
        {
            var colors = Palette(v); Color avg = Average(colors); float on = v.OnAmount;
            var body = new RectangleF(10 * s, 10 * s, 324 * s, 158 * s);
            Body(g, body, 26 * s, v, s, colors, avg);
            DrawOrb(g, v, s, 52 * s, 50 * s, .5f, false);
            Text(g, v.Title, "Segoe UI Semibold", 16 * s, FontStyle.Bold, Color.FromArgb(245, 255, 255, 255), new RectangleF(92 * s, 28 * s, 150 * s, 22 * s), StringAlignment.Near);
            Text(g, v.Status, "Segoe UI", 12 * s, FontStyle.Regular, Lerp(Color.FromArgb(150, 160, 166, 190), Color.FromArgb(200, 225, 226, 245), on), new RectangleF(92 * s, 50 * s, 170 * s, 18 * s), StringAlignment.Near);
            ZoneDots(g, v, s, 312 * s, 40 * s, 15);
            using (var line = new Pen(Color.FromArgb(28, 255, 255, 255), 1f * s)) g.DrawLine(line, 26 * s, 86 * s, 318 * s, 86 * s);
            int count = Math.Min(5, v.Chips.Count); float w = 300f / Math.Max(1, count);
            for (int i = 0; i < count; i++)
            {
                var chip = v.Chips[i]; float cx = (22 + w * i + w / 2) * s, cy = 118 * s, r = 17 * s; bool hot = v.HoverChip == i;
                SoftGlow(g, cx, cy, (hot ? 30 : 25) * s, chip.Glow, hot ? 150 : 90);
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(cx - r, cy - r, r * 2, r * 2);
                    using (var brush = new PathGradientBrush(path))
                    {
                        brush.CenterPoint = new PointF(cx - r * .35f, cy - r * .4f);
                        brush.CenterColor = Lerp(chip.Color, Color.White, .35f); brush.SurroundColors = new[] { Lerp(chip.Color, chip.Glow, .45f) };
                        g.FillPath(brush, path);
                    }
                    using (var pen = new Pen(Color.FromArgb(hot ? 230 : 70, 255, 255, 255), (hot ? 2f : 1f) * s)) g.DrawPath(pen, path);
                }
                Text(g, chip.Label, "Segoe UI", 11 * s, hot ? FontStyle.Bold : FontStyle.Regular, Color.FromArgb(hot ? 255 : 200, 236, 236, 250),
                    new RectangleF(cx - w * s / 2 + 2 * s, cy + 20 * s, w * s - 4 * s, 16 * s), StringAlignment.Center);
            }
        }
    }

    // One widget window. All drawing goes through UpdateLayeredWindow for real per-pixel transparency.
    public sealed class DesktopWidget : Form
    {
        public readonly string Id, Style;
        public readonly WidgetVisual Visual = new WidgetVisual();
        public event Action<DesktopWidget, string> ActionInvoked;
        public event Action<DesktopWidget> Moved;
        public event Action<DesktopWidget, MouseEventArgs> MenuRequested;
        public bool Locked, Ambient = true, OnTop;
        readonly Timer timer = new Timer { Interval = 33 };
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        readonly float scale;
        float target, lastFrame;
        Point downScreen, downLocation; bool down, dragging;
        double lastRender;

        public DesktopWidget(string id, string style, Point location, float scale)
        {
            Id = id; Style = style; this.scale = scale;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            var size = WidgetRenderer.LogicalSize(style);
            Size = new Size((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale));
            Location = location; Text = "Lume RGB widget"; Cursor = Cursors.Hand;
            timer.Tick += delegate { Tick(); };
        }
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00080000 | 0x00000080 | 0x08000000; // WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
                return cp;
            }
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ApplyLayer(); Render(); timer.Start(); }
        public void ApplyLayer()
        {
            if (!IsHandleCreated) return;
            if (OnTop)
            {
                Native.SetWindowLongPtr(Handle, -8, IntPtr.Zero);
                Native.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            }
            else
            {
                // Owned by the desktop window: stays under other apps and remains on "Mostrar área de trabalho".
                Native.SetWindowPos(Handle, new IntPtr(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
                IntPtr desktop = Native.FindWindow("Progman", null);
                if (desktop != IntPtr.Zero) Native.SetWindowLongPtr(Handle, -8, desktop);
                Native.SetWindowPos(Handle, new IntPtr(1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            }
        }
        public void SetState(bool on, bool pending, bool connected, Color[] colors, string status, List<WidgetChip> chips)
        {
            Visual.On = on; Visual.Pending = pending; Visual.Connected = connected; Visual.Colors = colors; Visual.Status = status; Visual.Chips = chips;
            target = on ? 1 : 0; Render(); if (!timer.Enabled && IsHandleCreated) timer.Start();
        }
        public void Pulse() { Visual.Ripple = 0; if (IsHandleCreated) timer.Start(); }
        void Tick()
        {
            float now = (float)clock.Elapsed.TotalSeconds, dt = Math.Min(.1f, now - lastFrame); lastFrame = now;
            bool animating = false;
            if (Math.Abs(Visual.OnAmount - target) > .001f) { Visual.OnAmount += Math.Sign(target - Visual.OnAmount) * Math.Min(Math.Abs(target - Visual.OnAmount), dt * 2.6f); animating = true; }
            if (Visual.Ripple >= 0) { Visual.Ripple += dt * 1.6f; if (Visual.Ripple >= 1) Visual.Ripple = -1; animating = true; }
            if (Visual.Pending || Visual.Hover) animating = true;
            bool ambient = Ambient && Visual.OnAmount > .01f;
            if (ambient || animating) Visual.Time = now;
            // Ambient motion (rotating colours) runs at ~20 fps; transitions at ~30 fps; idle costs nothing.
            if (animating || (ambient && now - lastRender >= .048)) Render();
            if (!animating && !ambient) timer.Stop();
        }
        public void Render()
        {
            if (!IsHandleCreated || IsDisposed) return;
            lastRender = clock.Elapsed.TotalSeconds;
            using (var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bitmap)) WidgetRenderer.Draw(g, Style, Visual, scale);
                Native.Present(Handle, bitmap, Left, Top);
            }
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) { down = true; dragging = false; downScreen = Cursor.Position; downLocation = Location; }
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (down && !Locked)
            {
                var p = Cursor.Position;
                if (!dragging && (Math.Abs(p.X - downScreen.X) > 4 || Math.Abs(p.Y - downScreen.Y) > 4)) dragging = true;
                if (dragging) Location = new Point(downLocation.X + p.X - downScreen.X, downLocation.Y + p.Y - downScreen.Y);
            }
            var logical = new PointF(e.X / scale, e.Y / scale);
            int chip = Style == "panel" ? WidgetRenderer.ChipAt(Visual, logical) : -1;
            bool hover = WidgetRenderer.HitTest(Style, Visual, logical) == "power";
            if (chip != Visual.HoverChip || hover != Visual.Hover) { Visual.HoverChip = chip; Visual.Hover = hover; Render(); timer.Start(); }
            Cursor = chip >= 0 || hover ? Cursors.Hand : Locked ? Cursors.Default : Cursors.SizeAll;
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Visual.Hover = false; Visual.HoverChip = -1; Render(); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right) { if (MenuRequested != null) MenuRequested(this, e); return; }
            if (e.Button != MouseButtons.Left || !down) return;
            down = false;
            if (dragging) { dragging = false; if (Moved != null) Moved(this); return; }
            string action = WidgetRenderer.HitTest(Style, Visual, new PointF(e.X / scale, e.Y / scale));
            if (action != null) { Pulse(); if (ActionInvoked != null) ActionInvoked(this, action); }
        }
        protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
    }

    static class Native
    {
        [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; public NativePoint(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] struct NativeSize { public int Cx, Cy; public NativeSize(int x, int y) { Cx = x; Cy = y; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BlendFunction { public byte Op, Flags, Alpha, Format; }
        [DllImport("user32.dll", SetLastError = true)] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, ref NativePoint pos, ref NativeSize size, IntPtr src, ref NativePoint srcPos, int key, ref BlendFunction blend, int flags);
        [DllImport("user32.dll", SetLastError = true)] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, ref NativePoint pos, IntPtr size, IntPtr src, IntPtr srcPos, int key, IntPtr blend, int flags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")] static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong")] static extern IntPtr SetWindowLong32(IntPtr hwnd, int index, IntPtr value);
        public static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value) { return IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : SetWindowLong32(hwnd, index, value); }

        // bitmap == null only moves the window, keeping the current pixels.
        public static void Present(IntPtr hwnd, Bitmap bitmap, int left, int top)
        {
            var pos = new NativePoint(left, top);
            if (bitmap == null) { UpdateLayeredWindow(hwnd, IntPtr.Zero, ref pos, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero, 0); return; }
            IntPtr screen = GetDC(IntPtr.Zero), memory = CreateCompatibleDC(screen), hbitmap = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hbitmap = bitmap.GetHbitmap(Color.FromArgb(0)); old = SelectObject(memory, hbitmap);
                var size = new NativeSize(bitmap.Width, bitmap.Height); var source = new NativePoint(0, 0);
                var blend = new BlendFunction { Op = 0, Flags = 0, Alpha = 255, Format = 1 };
                UpdateLayeredWindow(hwnd, screen, ref pos, ref size, memory, ref source, 0, ref blend, 2);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
                if (hbitmap != IntPtr.Zero) { SelectObject(memory, old); DeleteObject(hbitmap); }
                DeleteDC(memory);
            }
        }
    }
}
