using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace Harness
{
    public static class Program
    {
        public static int Failures;

        public static int Main(string[] args)
        {
            string vs = Environment.GetEnvironmentVariable("VSDir");
            if (string.IsNullOrEmpty(vs)) vs = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA"), "Programs", "Optimum");
            string[] dirs = { vs, Path.Combine(vs, "Lib"), Path.Combine(vs, "Mods") };
            AssemblyLoadContext.Default.Resolving += (ctx, name) =>
            {
                foreach (var d in dirs)
                {
                    string p = Path.Combine(d, name.Name + ".dll");
                    if (File.Exists(p)) return ctx.LoadFromAssemblyPath(p);
                }
                return null;
            };
            if (args.Length > 1 && args[0] == "verifyzip") { VerifyZip.Run(args[1], vs); return Failures == 0 ? 0 : 1; }
            int cases = args.Length > 0 ? int.Parse(args[0]) : 30000;
            int allocCases = args.Length > 1 ? int.Parse(args[1]) : 3000;
            Console.WriteLine("runtime " + Environment.Version + ", game dir resolved via VSDir/LOCALAPPDATA");
            DecayTest.Run(cases, allocCases);
            IlTest.Run();
            Console.WriteLine(Failures == 0 ? "ALL CHECKS PASSED" : "FAILURES: " + Failures);
            return Failures == 0 ? 0 : 1;
        }
    }
}
