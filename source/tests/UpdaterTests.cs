// SPDX-License-Identifier: GPL-2.0-or-later
// Network test of the GitHub updater (real api.github.com). Usage: UpdaterTests.exe <compiler>
// On Windows pass nothing (uses the .NET Framework csc); on Linux pass "mcs".
using System;
using System.IO;
using System.Linq;
using LumeRgb;

class UpdaterTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception("FAIL " + message); Console.WriteLine("PASS " + message); }
    static Updater Make(string app, string work, string commit, string[] args)
    {
        Directory.CreateDirectory(app);
        if (commit != null) File.WriteAllText(Path.Combine(app, "version.json"), "{\"commit\":\"" + commit + "\"}");
        var u = new Updater(app, work); if (args.Length > 0) u.CompilerPath = args[0]; return u;
    }
    static void Main(string[] args)
    {
        string proxy = Environment.GetEnvironmentVariable("https_proxy") ?? Environment.GetEnvironmentVariable("HTTPS_PROXY");
        if (!String.IsNullOrEmpty(proxy)) System.Net.WebRequest.DefaultWebProxy = new System.Net.WebProxy(proxy); // sandboxed CI only
        string root = Path.Combine(Path.GetTempPath(), "Lume-updater-" + Guid.NewGuid().ToString("N"));
        // 1. Installed commit was merged into main without app changes -> up to date, commit adopted.
        var merged = Make(Path.Combine(root, "a"), Path.Combine(root, "wa"), "9627ee98d79d033808ce71d1a13ca0715cd03377", args);
        merged.CheckNow();
        Check(merged.State == "uptodate", "merge-only difference counts as up to date (" + merged.State + " " + merged.Error + ")");
        Check(merged.Installed == merged.Remote, "version.json adopts the main commit");
        // 2. Older build -> update available, download, compile, install.
        var old = Make(Path.Combine(root, "b"), Path.Combine(root, "wb"), "47ffbd941977544c431c07bd89281393ef4539a6", args);
        File.WriteAllText(Path.Combine(old.AppDirectory, "LumeRGB.exe"), "old exe");
        old.CheckNow();
        Check(old.State == "available", "older commit -> update available (" + old.State + " " + old.Error + ")");
        Console.WriteLine("  changes: " + String.Join(" | ", old.Changes.ToArray()));
        // 3. Unknown build (no version.json) -> available.
        var unknown = Make(Path.Combine(root, "c"), Path.Combine(root, "wc"), null, args); unknown.CheckNow();
        Check(unknown.State == "available", "build without version.json -> update offered");
        old.PrepareNow();
        Check(old.State == "ready", "downloaded and compiled main (" + old.Error + ")");
        old.Apply();
        string app = old.AppDirectory;
        Check(new FileInfo(Path.Combine(app, "LumeRGB.exe")).Length > 50000, "new exe installed");
        Check(File.Exists(Path.Combine(app, "LumeRGB.exe.old")), "running exe moved aside, not overwritten");
        Check(File.Exists(Path.Combine(app, "ui", "index.html")) && File.Exists(Path.Combine(app, "Microsoft.Web.WebView2.Core.dll")) && File.Exists(Path.Combine(app, "source", "LumeApp.cs")), "ui, DLLs and source installed");
        Check(old.Installed == old.Remote, "version.json now points at main");
        old.CleanupOldFiles(); Check(!Directory.GetFiles(app, "*.old", SearchOption.AllDirectories).Any(), "leftover .old files cleaned");
        Console.WriteLine("ALL UPDATER TESTS PASSED");
    }
}
