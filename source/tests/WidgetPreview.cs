// Renders every widget style in several states to PNG (no window, no hardware).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using LumeRgb;
class WidgetPreview
{
    static void Main(string[] args)
    {
        string folder = args.Length > 0 ? args[0] : ".";
        var colors = new[] { Color.FromArgb(124, 108, 255), Color.FromArgb(124, 108, 255), Color.FromArgb(53, 217, 195), Color.FromArgb(255, 93, 145), Color.FromArgb(255, 170, 85) };
        var chips = new List<WidgetChip>();
        foreach (var sc in LumeService.Scenes) if (chips.Count < 5) chips.Add(new WidgetChip { Label = sc.Name, Action = "scene:" + sc.Id, Color = ColorTranslator.FromHtml(sc.Color), Glow = ColorTranslator.FromHtml(sc.Glow) });
        float scale = 1.5f;
        var sheet = new Bitmap((int)(360 * scale) * 3, (int)(200 * scale) * 3);
        using (var page = Graphics.FromImage(sheet))
        {
            page.Clear(Color.FromArgb(28, 50, 80));
            using (var bg = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, sheet.Width, sheet.Height), Color.FromArgb(20, 40, 70), Color.FromArgb(90, 60, 110), 30f)) page.FillRectangle(bg, 0, 0, sheet.Width, sheet.Height);
            string[] styles = { "orb", "pill", "panel" };
            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 3; col++)
                {
                    var v = new WidgetVisual { Colors = colors, Chips = chips, Time = 1.3f + col };
                    if (col == 0) { v.On = true; v.OnAmount = 1; v.Status = "Tudo ligado · 5 zonas"; v.HoverChip = 1; v.Hover = true; }
                    if (col == 1) { v.On = false; v.OnAmount = 0; v.Status = "Tudo desligado"; }
                    if (col == 2) { v.On = true; v.OnAmount = .55f; v.Pending = true; v.Ripple = .4f; v.Status = "Ligando…"; }
                    var size = WidgetRenderer.LogicalSize(styles[row]);
                    using (var bmp = new Bitmap((int)(size.Width * scale), (int)(size.Height * scale), PixelFormat.Format32bppArgb))
                    {
                        using (var g = Graphics.FromImage(bmp)) WidgetRenderer.Draw(g, styles[row], v, scale);
                        page.DrawImage(bmp, col * 360 * scale + 4, row * 200 * scale + 4);
                    }
                }
        }
        sheet.Save(System.IO.Path.Combine(folder, "widgets-preview.png"), ImageFormat.Png);
        Console.WriteLine("ok");
    }
}
