// SPDX-License-Identifier: GPL-2.0-or-later
// Self-update from the GitHub "main" branch. The app records the commit it was built from
// (version.json); on start it asks the GitHub API whether main has changes to the app itself,
// downloads that exact commit as a zip, compiles it with the C# compiler that ships with Windows
// (same options as COMPILAR.bat), stages the complete app and swaps the files in place.
// Only api.github.com / github.com / codeload.github.com of the official repository are contacted.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LumeRgb
{
    public sealed class Updater
    {
        public const string Owner = "LksR02", Repository = "FreeRGB", Branch = "main";
        static readonly string[] IgnoredPaths = { "docs/", "tools/", "README.md", ".gitignore", ".github/" };
        static readonly string[] DefaultOptions = { "/nologo", "/target:winexe", "/platform:x64", "/optimize+", "/utf8output",
            "/reference:System.dll", "/reference:System.Core.dll", "/reference:System.Drawing.dll", "/reference:System.Windows.Forms.dll",
            "/reference:System.Runtime.Serialization.dll", "/reference:System.Web.Extensions.dll", "/reference:System.Management.dll",
            "/reference:System.IO.Compression.dll", "/reference:System.IO.Compression.FileSystem.dll",
            "/reference:Microsoft.Web.WebView2.Core.dll", "/reference:Microsoft.Web.WebView2.WinForms.dll" };

        readonly object gate = new object();
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        public string AppDirectory { get; private set; }
        public string WorkDirectory { get; private set; }
        public string State { get; private set; }      // idle, checking, uptodate, available, downloading, building, ready, error
        public string Installed { get; private set; }
        public string InstalledDate { get; private set; }
        public string Remote { get; private set; }
        public string RemoteDate { get; private set; }
        public string Error { get; private set; }
        public int Behind { get; private set; }
        public List<string> Changes { get; private set; }
        public DateTime? LastCheck { get; private set; }
        public event EventHandler Changed;
        // Overridable for tests (Linux/Mono): HTTP and compiler.
        public Func<string, byte[]> Download;
        public string CompilerPath;

        public Updater(string appDirectory, string workDirectory)
        {
            AppDirectory = appDirectory; WorkDirectory = workDirectory; State = "idle"; Changes = new List<string>();
            Download = DefaultDownload;
            ReadVersion();
        }
        public static Updater CreateDefault()
        {
            return new Updater(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/'),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumeRGBStandalone", "update"));
        }
        public static string Short(string sha) { return String.IsNullOrEmpty(sha) ? "" : sha.Substring(0, Math.Min(7, sha.Length)); }
        void Set(string state, string error)
        {
            lock (gate) { State = state; Error = error; }
            var handler = Changed; if (handler != null) try { handler(this, EventArgs.Empty); } catch { }
        }
        public bool Busy { get { lock (gate) return State == "checking" || State == "downloading" || State == "building"; } }

        // ---------- version ----------
        string VersionFile { get { return Path.Combine(AppDirectory, "version.json"); } }
        void ReadVersion()
        {
            try
            {
                if (!File.Exists(VersionFile)) return;
                var data = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(VersionFile));
                object commit, date;
                if (data.TryGetValue("commit", out commit)) Installed = Convert.ToString(commit);
                if (data.TryGetValue("date", out date)) InstalledDate = Convert.ToString(date);
            }
            catch (Exception e) { AppLog.Write("version.json: " + e.Message); }
        }
        static string VersionJson(string sha, string date)
        {
            return "{\"commit\":\"" + sha + "\",\"date\":\"" + (date ?? "") + "\",\"branch\":\"" + Branch + "\"}";
        }

        // ---------- network ----------
        static byte[] DefaultDownload(string url)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2, required by GitHub
            var uri = new Uri(url);
            if (uri.Scheme != "https" || (uri.Host != "api.github.com" && uri.Host != "github.com" && uri.Host != "codeload.github.com"))
                throw new InvalidOperationException("Endereço de atualização não permitido.");
            using (var client = new WebClient())
            {
                client.Headers[HttpRequestHeader.UserAgent] = "LumeRGB-Updater";
                if (uri.Host == "api.github.com") client.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                return client.DownloadData(uri);
            }
        }
        Dictionary<string, object> Api(string path)
        {
            string text = Encoding.UTF8.GetString(Download("https://api.github.com/repos/" + Owner + "/" + Repository + path));
            return json.Deserialize<Dictionary<string, object>>(text);
        }
        static object Walk(object node, params string[] keys)
        {
            foreach (var key in keys) { var d = node as Dictionary<string, object>; if (d == null || !d.TryGetValue(key, out node)) return null; }
            return node;
        }
        static IEnumerable<object> Items(object node) { return node is IEnumerable && !(node is string) ? ((IEnumerable)node).Cast<object>() : new object[0]; }
        static string FirstLine(object message) { var m = Convert.ToString(message) ?? ""; int n = m.IndexOf('\n'); return (n < 0 ? m : m.Substring(0, n)).Trim(); }

        // ---------- check ----------
        public Task Check() { return Task.Factory.StartNew(CheckNow); }
        public void CheckNow()
        {
            if (Busy) return;
            Set("checking", null);
            try
            {
                var head = Api("/commits/" + Branch);
                string sha = Convert.ToString(head["sha"]);
                lock (gate) { Remote = sha; RemoteDate = Convert.ToString(Walk(head, "commit", "committer", "date")); Changes = new List<string> { FirstLine(Walk(head, "commit", "message")) }; Behind = 1; LastCheck = DateTime.Now; }
                if (String.Equals(sha, Installed, StringComparison.OrdinalIgnoreCase)) { Behind = 0; Set("uptodate", null); return; }
                if (!String.IsNullOrEmpty(Installed))
                {
                    Dictionary<string, object> compare = null;
                    try { compare = Api("/compare/" + Installed + "..." + sha); }
                    catch (WebException e) { AppLog.Write("Atualização: comparação indisponível (" + e.Message + ")."); }
                    if (compare != null)
                    {
                        string status = Convert.ToString(compare["status"]);
                        if (status == "identical" || status == "behind") { Behind = 0; Set("uptodate", null); return; }
                        var files = Items(Walk(compare, "files")).Select(f => Convert.ToString(Walk(f, "filename"))).ToList();
                        var relevant = files.Where(f => !IgnoredPaths.Any(p => f.StartsWith(p, StringComparison.OrdinalIgnoreCase))).ToList();
                        var messages = Items(Walk(compare, "commits")).Select(c => FirstLine(Walk(c, "commit", "message")))
                            .Where(m => m.Length > 0 && !m.StartsWith("Merge pull request")).Reverse().Take(6).ToList();
                        lock (gate) { Behind = Convert.ToInt32(compare["ahead_by"]); if (messages.Count > 0) Changes = messages; }
                        if (relevant.Count == 0)
                        {
                            // Only docs or a merge commit changed: adopt the new commit id, nothing to install.
                            TryWriteVersion(sha, RemoteDate); Behind = 0; Set("uptodate", null); return;
                        }
                    }
                }
                Set("available", null);
            }
            catch (Exception e) { AppLog.Write("Atualização (verificar): " + e); Set("error", "Não foi possível consultar o GitHub: " + e.Message); }
        }
        void TryWriteVersion(string sha, string date)
        {
            try { File.WriteAllText(VersionFile, VersionJson(sha, date)); lock (gate) { Installed = sha; InstalledDate = date; } }
            catch (Exception e) { AppLog.Write("version.json: " + e.Message); }
        }

        // ---------- download + build ----------
        string Stage { get { return Path.Combine(WorkDirectory, "stage"); } }
        public Task Prepare() { return Task.Factory.StartNew(PrepareNow); }
        public void PrepareNow()
        {
            string sha; lock (gate) sha = Remote;
            if (String.IsNullOrEmpty(sha) || Busy) return;
            try
            {
                Set("downloading", null);
                Directory.CreateDirectory(WorkDirectory);
                string zip = Path.Combine(WorkDirectory, "source.zip"), extracted = Path.Combine(WorkDirectory, "source");
                File.WriteAllBytes(zip, Download("https://codeload.github.com/" + Owner + "/" + Repository + "/zip/" + sha));
                if (Directory.Exists(extracted)) Directory.Delete(extracted, true);
                ZipFile.ExtractToDirectory(zip, extracted);
                string root = Directory.GetDirectories(extracted).Single();
                if (!File.Exists(Path.Combine(root, "source", "LumeApp.cs"))) throw new InvalidDataException("O pacote baixado não contém o código do Lume.");

                Set("building", null);
                if (Directory.Exists(Stage)) Directory.Delete(Stage, true);
                Directory.CreateDirectory(Stage);
                Compile(root, Path.Combine(Stage, "LumeRGB.exe"));
                Layout(root, Stage);
                File.WriteAllText(Path.Combine(Stage, "version.json"), VersionJson(sha, RemoteDate));
                File.WriteAllText(Path.Combine(WorkDirectory, "ready.txt"), sha);
                Set("ready", null);
            }
            catch (Exception e) { AppLog.Write("Atualização (preparar): " + e); Set("error", e.Message); }
        }
        public static string FindCompiler()
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            foreach (var folder in new[] { "Framework64", "Framework" })
            {
                string path = Path.Combine(windows, "Microsoft.NET", folder, "v4.0.30319", "csc.exe");
                if (File.Exists(path)) return path;
            }
            throw new FileNotFoundException("Compilador do .NET Framework 4 (csc.exe) não encontrado.");
        }
        static string Quote(string value) { return "\"" + value + "\""; }
        void Compile(string root, string output)
        {
            string compiler = CompilerPath ?? FindCompiler();
            string rsp = Path.Combine(root, "source", "build.rsp");
            var args = new List<string>();
            if (File.Exists(rsp)) args.Add(Quote("@" + rsp)); else args.AddRange(DefaultOptions);
            args.Add(Quote("/out:" + output));
            args.Add(Quote("/win32icon:" + Path.Combine(root, "source", "lume.ico")));
            args.Add(Quote("/lib:" + Path.Combine(root, "lib")));
            args.AddRange(Directory.GetFiles(Path.Combine(root, "source"), "*.cs").OrderBy(f => f).Select(Quote));
            var info = new ProcessStartInfo(compiler, String.Join(" ", args.ToArray()))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root,
                StandardOutputEncoding = Encoding.UTF8
            };
            using (var process = Process.Start(info))
            {
                var stderr = process.StandardError.ReadToEndAsync();
                string stdout = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(180000)) { try { process.Kill(); } catch { } throw new TimeoutException("A compilação demorou demais."); }
                string log = stdout + stderr.Result;
                if (process.ExitCode != 0 || !File.Exists(output))
                {
                    AppLog.Write("Atualização: compilação falhou\r\n" + log);
                    string firstError = log.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("error")) ?? "código " + process.ExitCode;
                    throw new InvalidOperationException("A compilação da nova versão falhou (" + firstError + ").");
                }
            }
        }
        // Repository layout -> installed app layout (same as tools/package.sh).
        static void Layout(string root, string stage)
        {
            foreach (var dll in Directory.GetFiles(Path.Combine(root, "lib"), "*.dll")) File.Copy(dll, Path.Combine(stage, Path.GetFileName(dll)), true);
            CopyTree(Path.Combine(root, "ui"), Path.Combine(stage, "ui"));
            string source = Path.Combine(stage, "source"); Directory.CreateDirectory(source);
            foreach (var file in Directory.GetFiles(Path.Combine(root, "source")))
                if (new[] { ".cs", ".ps1", ".rsp", ".ico" }.Contains(Path.GetExtension(file).ToLowerInvariant())) File.Copy(file, Path.Combine(source, Path.GetFileName(file)), true);
            if (Directory.Exists(Path.Combine(root, "source", "tests"))) CopyTree(Path.Combine(root, "source", "tests"), Path.Combine(source, "tests"));
            foreach (var name in new[] { "LEIA-ME.txt", "ATRIBUICOES.txt", "COMPILAR.bat" })
                if (File.Exists(Path.Combine(root, name))) File.Copy(Path.Combine(root, name), Path.Combine(stage, name), true);
            if (File.Exists(Path.Combine(root, "LICENSE-GPL-2.0.txt"))) File.Copy(Path.Combine(root, "LICENSE-GPL-2.0.txt"), Path.Combine(stage, "LICENSE"), true);
            if (File.Exists(Path.Combine(root, "lib", "WebView2-LICENSE.txt"))) File.Copy(Path.Combine(root, "lib", "WebView2-LICENSE.txt"), Path.Combine(stage, "WebView2-LICENSE.txt"), true);
        }
        static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
            foreach (var dir in Directory.GetDirectories(from)) CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
        }

        // ---------- install ----------
        // Swaps the staged files in. Running files (exe, DLLs) can be renamed on Windows, so each
        // existing file becomes *.old first; any failure puts every original file back.
        public void Apply()
        {
            lock (gate) if (State != "ready") throw new InvalidOperationException("Nenhuma atualização pronta.");
            var moved = new List<KeyValuePair<string, string>>(); var added = new List<string>();
            try
            {
                foreach (var file in Directory.GetFiles(Stage, "*", SearchOption.AllDirectories))
                {
                    string relative = file.Substring(Stage.Length).TrimStart('\\', '/');
                    string target = Path.Combine(AppDirectory, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    if (File.Exists(target))
                    {
                        string old = target + ".old";
                        if (File.Exists(old)) { try { File.Delete(old); } catch { old = target + "." + DateTime.Now.Ticks + ".old"; } }
                        File.Move(target, old); moved.Add(new KeyValuePair<string, string>(target, old));
                    }
                    File.Copy(file, target); added.Add(target);
                }
            }
            catch (Exception e)
            {
                foreach (var target in added) try { File.Delete(target); } catch { }
                foreach (var pair in Enumerable.Reverse(moved)) try { File.Move(pair.Value, pair.Key); } catch { }
                AppLog.Write("Atualização (instalar): " + e);
                Set("error", e is UnauthorizedAccessException ? "Sem permissão para gravar na pasta do Lume. Mova o app para uma pasta sua (ex.: C:\\LumeRGB)." : "Não foi possível instalar: " + e.Message);
                throw;
            }
            try { File.Delete(Path.Combine(WorkDirectory, "ready.txt")); } catch { }
            ReadVersion(); Behind = 0;
            Set("installed", null);
        }
        // Leftovers of the previous swap (old exe/DLLs could not be deleted while in use).
        public void CleanupOldFiles()
        {
            try { foreach (var file in Directory.GetFiles(AppDirectory, "*.old", SearchOption.AllDirectories)) try { File.Delete(file); } catch { } }
            catch (Exception) { }
        }
    }
}
