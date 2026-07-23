using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PartingSurface.Detection
{
    /// <summary>
    /// Exception thrown when a required runtime component cannot be found.
    /// </summary>
    public class RuntimeNotFoundError : Exception
    {
        public RuntimeNotFoundError(string message) : base(message) { }
    }

    /// <summary>
    /// Holds paths to all required NX/Parasolid runtime components.
    /// Ported from Python runtime_locator.RuntimePaths.
    /// </summary>
    public class RuntimePaths
    {
        public string NxRoot { get; set; }
        public string UgInspect { get; set; }
        public string Nxbin { get; set; }
        public string PskernelNet { get; set; }
        public string Pskernel { get; set; }
        public string Csc { get; set; }
        public string RunJournal { get; set; }
    }

    /// <summary>
    /// Locates the NX/Parasolid runtime and C# compiler.
    /// Ported from Python runtime_locator.locate_runtime.
    /// </summary>
    public static class RuntimeLocator
    {
        /// <summary>
        /// Find all required runtime paths. Explicit nxRoot and csc parameters take priority.
        /// </summary>
        public static RuntimePaths Locate(string nxRoot = null, string csc = null)
        {
            string root = FindNxRoot(nxRoot);
            string nxbin = Path.Combine(root, "NXBIN");
            string ugInspect = FirstExisting(
                Path.Combine(nxbin, "ug_inspect.exe"),
                Path.Combine(root, "UGII", "ug_inspect.exe")
            );
            string pskernelNet = FirstExisting(
                Path.Combine(nxbin, "managed", "pskernel_net.dll")
            );
            string pskernel = FirstExisting(
                Path.Combine(nxbin, "pskernel.dll")
            );
            string compiler = FindCsc(csc);
            string runJournal = Path.Combine(nxbin, "run_journal.exe");
            return new RuntimePaths
            {
                NxRoot = root,
                UgInspect = ugInspect,
                Nxbin = nxbin,
                PskernelNet = pskernelNet,
                Pskernel = pskernel,
                Csc = compiler,
                RunJournal = File.Exists(runJournal) ? runJournal : null,
            };
        }

        private static string FindNxRoot(string explicitRoot)
        {
            List<string> candidates = new List<string>();
            if (!string.IsNullOrEmpty(explicitRoot))
            {
                candidates.Add(explicitRoot);
            }
            string[] envVars = { "PARTING_SURFACE_NX_ROOT", "UGII_BASE_DIR", "NX_ROOT" };
            foreach (string varName in envVars)
            {
                string value = Environment.GetEnvironmentVariable(varName);
                if (!string.IsNullOrEmpty(value))
                {
                    candidates.Add(value);
                }
            }
            string[] drives = { "C", "D", "E", "F", "G", "S" };
            foreach (string drive in drives)
            {
                candidates.Add(drive + ":\\nx");
                candidates.Add(drive + ":\\Program Files\\Siemens\\NX2306");
                candidates.Add(drive + ":\\Program Files\\Siemens\\NX");
            }
            foreach (string candidate in Deduplicate(candidates))
            {
                if (File.Exists(Path.Combine(candidate, "NXBIN", "ug_inspect.exe")) &&
                    File.Exists(Path.Combine(candidate, "NXBIN", "managed", "pskernel_net.dll")))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            throw new RuntimeNotFoundError(
                "未找到兼容 NX/Parasolid 运行时。请设置 PARTING_SURFACE_NX_ROOT 指向 NX 安装目录。"
            );
        }

        private static string FindCsc(string explicitCsc)
        {
            List<string> candidates = new List<string>();
            if (!string.IsNullOrEmpty(explicitCsc))
            {
                candidates.Add(explicitCsc);
            }
            string envValue = Environment.GetEnvironmentVariable("PARTING_SURFACE_CSC");
            if (!string.IsNullOrEmpty(envValue))
            {
                candidates.Add(envValue);
            }
            // Search PATH
            string pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (string dir in pathEnv.Split(Path.PathSeparator))
                {
                    if (!string.IsNullOrEmpty(dir))
                    {
                        string cscInDir = Path.Combine(dir, "csc.exe");
                        if (File.Exists(cscInDir))
                        {
                            candidates.Add(cscInDir);
                        }
                    }
                }
            }
            candidates.Add(@"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe");
            candidates.Add(@"C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe");
            string programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
            if (string.IsNullOrEmpty(programFiles))
            {
                programFiles = @"C:\Program Files";
            }
            string vsRoot = Path.Combine(programFiles, "Microsoft Visual Studio", "2022");
            string[] editions = { "BuildTools", "Community", "Professional", "Enterprise" };
            foreach (string edition in editions)
            {
                candidates.Add(Path.Combine(vsRoot, edition, "MSBuild", "Current", "Bin", "Roslyn", "csc.exe"));
            }
            foreach (string candidate in Deduplicate(candidates))
            {
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            throw new RuntimeNotFoundError(
                "未找到 C# 编译器。请安装 .NET Framework/Visual Studio，或设置 PARTING_SURFACE_CSC。"
            );
        }

        private static string FirstExisting(params string[] paths)
        {
            foreach (string path in paths)
            {
                if (File.Exists(path))
                {
                    return Path.GetFullPath(path);
                }
            }
            throw new RuntimeNotFoundError(
                "缺少运行时文件: " + string.Join(", ", paths)
            );
        }

        private static IEnumerable<string> Deduplicate(IEnumerable<string> paths)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                if (seen.Add(path))
                {
                    yield return path;
                }
            }
        }
    }
}
