using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using LumeRgb;
namespace LumeRgb { static class Program { public const string Version = "audit"; } }
class AuditProbe {
 sealed class Keys : IX68HidTransport {
  public ushort VendorId { get { return 0x3151; } } public ushort ProductId { get { return 0x502D; } }
  public ushort UsagePage { get { return 0xFFFF; } } public ushort Usage { get { return 2; } }
  public int FeatureReportLength { get { return 65; } } public string ProductName { get { return "X68HE"; } }
  public byte[] State = new byte[65]; byte command=0x87; public int Modes, Frames, ZeroBrightness;
  public Keys() { State[1]=0x87; State[2]=1; State[3]=2; State[4]=4; State[5]=7; State[8]=255; }
  public byte[] GetFeature() { if(command==0x87) return (byte[])State.Clone(); var b=new byte[65]; b[1]=command; return b; }
  public void SetFeature(byte[] b) { X68WindowsHidTransport.ValidateOutgoingReport(b); command=b[1]; if(command==7) { Modes++; if(b[4]==0) ZeroBrightness++; for(int i=2;i<=8;i++)State[i]=b[i]; } if(command==15) Frames++; }
  public void Dispose() {}
 }
 static ZoneSetting Target(Color c) { return new ZoneSetting { Key="X68HE", Color=c, Effect="Static", BrightnessPercent=100, SpeedPercent=50 }; }
 static void Check(bool b,string s) { if(!b) throw new Exception(s); Console.WriteLine("CONFIRMED "+s); }
 static void Field(object o,string n,object v) { o.GetType().GetField(n,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(o,v); }
 static void Main() {
  Color midpoint=ReactiveEngine.Gradient(Color.Blue,null,Color.Red,0.5); Check(midpoint.R==128 && midpoint.B==128 && midpoint.G==0,"RGB gradient midpoint is #800080; max channel falls from 255 to 128"); var keys=new Keys(); var controller=new X68KeyboardController(); controller.Connect(keys);
  controller.Apply(Target(Color.Red));
  Check(keys.Modes==1 && keys.State[6]==255 && keys.State[8]==0,"manual blue-to-red sends one final effect configuration; no intermediate colors");
  for(int i=0;i<=10;i++) controller.ApplyFast(Target(ReactiveEngine.Gradient(Color.Blue,null,Color.Red,i/10.0)));
  Check(keys.Modes==12 && keys.ZeroBrightness==0,"classic gradient reconfigures effect for every color even with constant 100% brightness");
  controller.Stream(Color.Blue,100);
  Check(controller.IsStreaming,"fake transport starts live stream");
  controller.DynamicLighting();
  Check(!controller.IsStreaming,"reading DynamicLighting (called by Diagnostics) stops the live stream");
  controller.Dispose();
  var advanced=new AdvancedSensors(false,Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N")));
  var pipe=new NamedPipeServerStream("FreeRGB-audit-"+Guid.NewGuid().ToString("N"));
  Field(advanced,"pipe",pipe); Field(advanced,"hostRunning",true); Field(advanced,"startedAt",DateTime.UtcNow.AddMinutes(-10));
  Field(advanced,"lastData",DateTime.UtcNow.AddMinutes(-10));
  typeof(AdvancedSensors).GetMethod("Watch",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(advanced,null);
  Check(advanced.Detail.Contains("abrir"),"running helper silence is mislabeled as startup timeout after StopHost resets hostRunning");
  advanced.Dispose();
 }
}