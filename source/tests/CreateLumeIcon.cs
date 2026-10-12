using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
class CreateLumeIcon
{
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    static void Main(string[] args)
    {
        using(var image=new Bitmap(64,64))using(var g=Graphics.FromImage(image))
        {
            g.SmoothingMode=SmoothingMode.AntiAlias;g.Clear(Color.Transparent);
            using(var shape=new GraphicsPath())
            {
                shape.AddArc(2,2,24,24,180,90);shape.AddArc(38,2,24,24,270,90);shape.AddArc(38,38,24,24,0,90);shape.AddArc(2,38,24,24,90,90);shape.CloseFigure();
                using(var fill=new SolidBrush(Color.FromArgb(23,25,40)))g.FillPath(fill,shape);
                using(var border=new Pen(Color.FromArgb(77,69,112),2))g.DrawPath(border,shape);
            }
            using(var line=new Pen(Color.FromArgb(174,157,255),7)){line.StartCap=line.EndCap=LineCap.Round;line.LineJoin=LineJoin.Round;g.DrawLines(line,new[]{new Point(22,18),new Point(22,43),new Point(43,43)});}
            using(var light=new SolidBrush(Color.FromArgb(96,224,184)))g.FillEllipse(light,39,16,10,10);
            IntPtr handle=image.GetHicon();try{using(var icon=Icon.FromHandle(handle))using(var output=File.Create(args[0]))icon.Save(output);}finally{DestroyIcon(handle);}
        }
    }
}
