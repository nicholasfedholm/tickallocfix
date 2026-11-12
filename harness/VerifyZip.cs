using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace Harness
{
    public static class VerifyZip
    {
        static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) Program.Failures++; }

        public static void Run(string zipPath, string vs)
        {
            Console.WriteLine("[zip] " + Path.GetFileName(zipPath));
            using var zip = ZipFile.OpenRead(zipPath);
            var names = zip.Entries.Select(e => e.FullName).OrderBy(x => x).ToList();
            Console.WriteLine("  entries: " + string.Join(", ", names));
            Check(names.Count == 2 && names.Contains("modinfo.json") && names.Contains("TickAllocFix.dll"), "root-level modinfo.json + TickAllocFix.dll only (no pdb, no subfolders)");
            using (var s = zip.GetEntry("modinfo.json").Open())
            {
                var doc = JsonDocument.Parse(s).RootElement;
                string str(string k) => doc.GetProperty(k).GetString();
                Check(str("type") == "code" && str("modid") == "tickallocfix" && str("name") == "Tick Alloc Fix" && str("version") == "1.0.0" && str("side") == "Universal", "modinfo type/modid/name/version/side");
                Check(doc.GetProperty("authors")[0].GetString() == "Nick", "author Nick");
                Check(!doc.GetProperty("requiredOnClient").GetBoolean() && !doc.GetProperty("requiredOnServer").GetBoolean(), "requiredOnClient/Server false");
                Check(System.Text.RegularExpressions.Regex.IsMatch(str("modid"), "^[a-z0-9]+$"), "modid is lowercase alnum");
            }
            var ms = new MemoryStream();
            using (var s = zip.GetEntry("TickAllocFix.dll").Open()) s.CopyTo(ms);
            ms.Position = 0;
            using var pe = new PEReader(ms);
            var md = pe.GetMetadataReader();
            string asmName = md.GetString(md.GetAssemblyDefinition().Name);
            Console.WriteLine("  assembly " + asmName + " " + md.GetAssemblyDefinition().Version);
            var dirs = new[] { vs, Path.Combine(vs, "Lib"), Path.Combine(vs, "Mods") };
            bool allOk = true;
            foreach (var h in md.AssemblyReferences)
            {
                var r = md.GetAssemblyReference(h);
                string n = md.GetString(r.Name);
                string where = null;
                foreach (var d in dirs) if (File.Exists(Path.Combine(d, n + ".dll"))) { where = Path.GetRelativePath(vs, Path.Combine(d, n + ".dll")); break; }
                if (where == null)
                {
                    // framework assemblies live in the shared runtime
                    string fw = Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location), n + ".dll");
                    if (File.Exists(fw)) where = "<runtime>/" + n + ".dll";
                }
                Console.WriteLine($"  ref {n} {r.Version} -> {where ?? "UNRESOLVED"}");
                if (where == null) allOk = false;
            }
            Check(allOk, "every assembly reference resolves to a game or runtime assembly");
            Check(md.AssemblyReferences.Count > 0 && !md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).Any(n => n == "VSSurvivalMod"), "no VSSurvivalMod reference (only API/Essentials/Harmony)");
        }
    }
}
