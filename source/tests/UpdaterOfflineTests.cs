// SPDX-License-Identifier: GPL-2.0-or-later
// Offline updater test: fake GitHub responses and a fake repository zip. Usage: UpdaterOfflineTests.exe <compiler>
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using LumeRgb;

class UpdaterOfflineTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception("FAIL " + message); Console.WriteLine("PASS " + message); }
    static byte[] RepoZip(string sha)
    {
        using (var buffer = new MemoryStream())
        {
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            {
                Action<string, string> add = (path, text) => { using (var w = new StreamWriter(zip.CreateEntry("FreeRGB-" + sha + "/" + path).Open())) w.Write(text); };
                add("source/LumeApp.cs", "class P { static void Main() { System.Console.WriteLine(\"new\"); } }");
                add("source/build.rsp", "/nologo\n/target:exe\n");
                using (var icon = zip.CreateEntry("FreeRGB-" + sha + "/source/lume.ico").Open()) { var bytes = File.ReadAllBytes("lume.ico"); icon.Write(bytes, 0, bytes.Length); }
                add("ui/index.html", "<!doctype html>new");
                add("lib/readme.txt", "");
                add("LEIA-ME.txt", "novo");
            }
            return buffer.ToArray();
        }
    }
    static void Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "Lume-updater-offline-" + Guid.NewGuid().ToString("N"));
        string app = Path.Combine(root, "app"); Directory.CreateDirectory(Path.Combine(app, "source")); Directory.CreateDirectory(Path.Combine(app, "ui", "old"));
        File.WriteAllText(Path.Combine(app, "version.json"), "{\"commit\":\"aaaa\"}");
        File.WriteAllText(Path.Combine(app, "LumeRGB.exe"), "old");
        File.WriteAllText(Path.Combine(app, "source", "Removed.cs"), "class Removed {}");
        File.WriteAllText(Path.Combine(app, "ui", "old", "gone.js"), "x");
        File.WriteAllText(Path.Combine(app, "minhas-notas.txt"), "do usuário");
        string sha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var updater = new Updater(app, Path.Combine(root, "work")) { CompilerPath = args.Length > 0 ? args[0] : null };
        updater.Download = url =>
        {
            if (url.Contains("/commits/")) return Encoding.UTF8.GetBytes("{\"sha\":\"" + sha + "\",\"commit\":{\"message\":\"Nova versão\",\"committer\":{\"date\":\"2026-10-12T00:00:00Z\"}}}");
            if (url.Contains("/compare/")) return Encoding.UTF8.GetBytes("{\"status\":\"ahead\",\"ahead_by\":1,\"files\":[{\"filename\":\"source/LumeApp.cs\"}],\"commits\":[{\"commit\":{\"message\":\"Nova versão\"}}]}");
            if (url.Contains("codeload")) return RepoZip(sha);
            throw new Exception("unexpected " + url);
        };
        updater.CheckNow(); Check(updater.State == "available", "fake main is newer");
        updater.PrepareNow(); Check(updater.State == "ready", "fake repository downloaded and compiled (" + updater.Error + ")");
        updater.Apply();
        Check(!File.Exists(Path.Combine(app, "source", "Removed.cs")), "source file deleted upstream is removed (COMPILAR.bat compiles every .cs)");
        Check(!File.Exists(Path.Combine(app, "ui", "old", "gone.js")), "obsolete UI file removed");
        Check(File.Exists(Path.Combine(app, "minhas-notas.txt")), "files outside the app's managed folders are kept");
        Check(File.ReadAllText(Path.Combine(app, "ui", "index.html")).EndsWith("new") && File.ReadAllText(Path.Combine(app, "LEIA-ME.txt")) == "novo", "new files installed");
        Check(updater.Installed == sha, "version.json updated");
        Console.WriteLine("ALL OFFLINE UPDATER TESTS PASSED");
    }
}
