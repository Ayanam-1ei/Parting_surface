using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace PartingSurface.Detection
{
    /// <summary>
    /// Exception thrown by the geometry pipeline.
    /// Ported from Python geometry_pipeline.GeometryPipelineError.
    /// </summary>
    public class GeometryPipelineError : Exception
    {
        public GeometryPipelineError(string message) : base(message) { }
    }

    /// <summary>
    /// Represents an extracted Parasolid partition.
    /// Ported from Python geometry_pipeline.Partition.
    /// </summary>
    public class Partition
    {
        public int PartitionId;
        public string Label;
        public string ExtractedPath;

        public Partition(int partitionId, string label, string extractedPath)
        {
            PartitionId = partitionId;
            Label = label;
            ExtractedPath = extractedPath;
        }
    }

    /// <summary>
    /// Orchestrates the full geometry analysis pipeline:
    /// ug_inspect -ps, ug_inspect -extract, compile+run ParasolidAnalyzer, read TSV, assemble evidence JSON.
    /// Ported from Python geometry_pipeline.analyze_prt and all helper functions.
    /// </summary>
    public static class GeometryPipeline
    {
        public const string SCHEMA_VERSION = "2.0";
        public const double NX_PARASOLID_TO_MM = 1000.0;

        /// <summary>
        /// Analyze a .prt file and produce geometry evidence.
        /// Ported from analyze_prt().
        /// </summary>
        public static Dictionary<string, object> AnalyzePrt(
            string sourcePath,
            string outputDirectory,
            string nxRoot = null,
            string csc = null,
            bool keepWork = false)
        {
            sourcePath = Path.GetFullPath(sourcePath);
            outputDirectory = Path.GetFullPath(outputDirectory);

            if (!string.Equals(Path.GetExtension(sourcePath), ".prt", StringComparison.OrdinalIgnoreCase))
            {
                throw new GeometryPipelineError("只接受 Siemens NX .prt 文件: " + sourcePath);
            }
            if (!File.Exists(sourcePath))
            {
                throw new GeometryPipelineError("文件不存在: " + sourcePath);
            }

            RuntimePaths runtime = RuntimeLocator.Locate(nxRoot, csc);
            Directory.CreateDirectory(outputDirectory);

            string workDirectory = Path.Combine(outputDirectory, ".work");
            if (Directory.Exists(workDirectory))
            {
                RemoveTree(workDirectory);
            }
            string partitionsDirectory = Path.Combine(workDirectory, "partitions");
            string rawDirectory = Path.Combine(workDirectory, "raw");
            string binDirectory = Path.Combine(workDirectory, "bin");
            Directory.CreateDirectory(partitionsDirectory);
            Directory.CreateDirectory(rawDirectory);
            Directory.CreateDirectory(binDirectory);

            string inspectText = RunText(runtime.UgInspect, "-ps", sourcePath);
            KeyValuePair<Dictionary<string, object>, List<KeyValuePair<int, string>>> parsed = ParseUgInspect(inspectText);
            Dictionary<string, object> partMetadata = parsed.Key;
            List<KeyValuePair<int, string>> partitionDescriptors = parsed.Value;

            if (partitionDescriptors.Count == 0)
            {
                throw new GeometryPipelineError(".prt 中没有可读取的 Parasolid 分区");
            }

            List<Partition> partitions = ExtractPartitions(runtime, sourcePath, partitionDescriptors, partitionsDirectory);
            string analyzerExecutable = BuildAnalyzer(runtime, binDirectory);
            RunAnalyzer(runtime, analyzerExecutable, rawDirectory, partitions);

            Dictionary<string, object> evidence = AssembleEvidence(
                sourcePath,
                Sha256File(sourcePath),
                runtime,
                partMetadata,
                rawDirectory);

            string evidencePath = Path.Combine(outputDirectory, "geometry_evidence.json");
            File.WriteAllText(evidencePath, JsonHelper.Serialize(evidence), new UTF8Encoding(false));

            if (!keepWork)
            {
                RemoveTree(workDirectory);
            }
            return evidence;
        }

        /// <summary>
        /// Compute SHA-256 hash of a file. Ported from sha256_file().
        /// </summary>
        public static string Sha256File(string path)
        {
            using (SHA256 sha256 = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                byte[] hash = sha256.ComputeHash(stream);
                StringBuilder sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                {
                    sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// Run a command and return its stdout. Ported from _run_text().
        /// </summary>
        private static string RunText(params string[] command)
        {
            return RunTextWithEnv(command, null, new int[] { 0 });
        }

        /// <summary>
        /// Run a command with allowed return codes. Ported from _run_text() with allowed_returncodes.
        /// </summary>
        private static string RunTextAllowed(int[] allowedReturnCodes, params string[] command)
        {
            return RunTextWithEnv(command, null, allowedReturnCodes);
        }

        /// <summary>
        /// Build a properly quoted argument string from a command array.
        /// </summary>
        private static string BuildArguments(string[] command)
        {
            if (command.Length <= 1) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 1; i < command.Length; i++)
            {
                if (i > 1) sb.Append(' ');
                string arg = command[i];
                if (arg == null)
                {
                    sb.Append("\"\"");
                }
                else if (arg.Length == 0)
                {
                    sb.Append("\"\"");
                }
                else if (arg.IndexOfAny(new[] { ' ', '\t', '"', '\\' }) >= 0)
                {
                    sb.Append('"');
                    int backslashes = 0;
                    foreach (char c in arg)
                    {
                        if (c == '\\')
                        {
                            backslashes++;
                        }
                        else if (c == '"')
                        {
                            sb.Append(new string('\\', backslashes * 2 + 1));
                            sb.Append('"');
                            backslashes = 0;
                        }
                        else
                        {
                            if (backslashes > 0)
                            {
                                sb.Append(new string('\\', backslashes));
                                backslashes = 0;
                            }
                            sb.Append(c);
                        }
                    }
                    if (backslashes > 0)
                    {
                        sb.Append(new string('\\', backslashes * 2));
                    }
                    sb.Append('"');
                }
                else
                {
                    sb.Append(arg);
                }
            }
            return sb.ToString();
        }

        private static string RunTextWithEnv(string[] command, Dictionary<string, string> env, int[] allowedReturnCodes)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = command[0];
            psi.Arguments = BuildArguments(command);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.CreateNoWindow = true;

            if (env != null)
            {
                foreach (KeyValuePair<string, string> pair in env)
                {
                    psi.Environment[pair.Key] = pair.Value;
                }
            }

            Process process = new Process();
            process.StartInfo = psi;
            process.Start();

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            string text = stdout;
            if (!string.IsNullOrEmpty(stderr))
            {
                text = stdout + stderr;
            }

            bool allowed = false;
            foreach (int code in allowedReturnCodes)
            {
                if (process.ExitCode == code)
                {
                    allowed = true;
                    break;
                }
            }

            if (!allowed)
            {
                throw new GeometryPipelineError(
                    string.Format("命令执行失败 ({0}):\n{1}", process.ExitCode, text.Trim()));
            }

            return text;
        }

        /// <summary>
        /// Parse ug_inspect output to extract partition metadata and descriptors.
        /// Ported from _parse_ug_inspect().
        /// </summary>
        private static KeyValuePair<Dictionary<string, object>, List<KeyValuePair<int, string>>> ParseUgInspect(string text)
        {
            // Extract partition IDs
            MatchCollection idMatches = Regex.Matches(text, @"Partition id:\s*(\d+)");
            List<int> partitionIds = new List<int>();
            foreach (Match match in idMatches)
            {
                partitionIds.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
            }

            // Extract labels from directory entries
            Dictionary<int, string> labels = new Dictionary<int, string>();
            Regex directoryPattern = new Regex(
                @"^\s*(Parasolid|PS\s+Tool|PS\s+Sheet)\s+\d+\s+(\d+)\s+\(",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);
            foreach (Match match in directoryPattern.Matches(text))
            {
                int id = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                string label = Regex.Replace(match.Groups[1].Value, @"\s+", " ").Trim();
                labels[id] = label;
            }

            // Build descriptors, preserving order and deduplicating
            List<KeyValuePair<int, string>> descriptors = new List<KeyValuePair<int, string>>();
            HashSet<int> seen = new HashSet<int>();
            foreach (int partitionId in partitionIds)
            {
                if (seen.Add(partitionId))
                {
                    string label;
                    labels.TryGetValue(partitionId, out label);
                    if (label == null) label = "Parasolid";
                    descriptors.Add(new KeyValuePair<int, string>(partitionId, label));
                }
            }

            // Extract metadata
            string nxRelease = MatchValue(text, @"^Release:\s*(.+)$") ?? MatchValue(text, @"last saved in:\s*(NX[^\r\n]+)");
            string partUnits = MatchValue(text, @"^Part units:\s*(.+)$");
            string parasolidVersion = MatchValue(text, @"^Parasolid Version:\s*(.+)$");
            bool nativeMode = text.ToLowerInvariant().Contains("last saved in native mode");

            Dictionary<string, object> metadata = new Dictionary<string, object>
            {
                { "nx_release", nxRelease },
                { "part_units", partUnits },
                { "parasolid_version", parasolidVersion },
                { "native_mode", nativeMode },
                { "partition_count", descriptors.Count },
            };

            return new KeyValuePair<Dictionary<string, object>, List<KeyValuePair<int, string>>>(metadata, descriptors);
        }

        private static string MatchValue(string text, string pattern)
        {
            Match match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (match.Success)
            {
                return match.Groups[1].Value.Trim();
            }
            return null;
        }

        /// <summary>
        /// Extract Parasolid partitions from .prt file using ug_inspect -extract.
        /// Ported from _extract_partitions().
        /// </summary>
        private static List<Partition> ExtractPartitions(
            RuntimePaths runtime,
            string sourcePath,
            List<KeyValuePair<int, string>> descriptors,
            string outputDirectory)
        {
            List<Partition> extracted = new List<Partition>();
            foreach (KeyValuePair<int, string> descriptor in descriptors)
            {
                int partitionId = descriptor.Key;
                string label = descriptor.Value;
                string baseName = "partition_" + partitionId.ToString(CultureInfo.InvariantCulture);
                string basePath = Path.Combine(outputDirectory, baseName);

                // Track files before extraction
                HashSet<string> before = new HashSet<string>(Directory.GetFiles(outputDirectory));

                RunTextAllowed(new int[] { 0, 1 },
                    runtime.UgInspect, "-extract",
                    partitionId.ToString(CultureInfo.InvariantCulture),
                    sourcePath, basePath);

                // Find newly created files
                List<string> created = Directory.GetFiles(outputDirectory)
                    .Where(p => !before.Contains(p))
                    .ToList();

                // Filter candidates by stem starting with base name
                List<string> candidates = created
                    .Where(p => Path.GetFileNameWithoutExtension(p).StartsWith(baseName, StringComparison.Ordinal))
                    .ToList();

                if (candidates.Count == 0)
                {
                    // Fallback: glob by base name pattern
                    candidates = Directory.GetFiles(outputDirectory, baseName + "*").ToList();
                }

                if (candidates.Count == 0)
                {
                    throw new GeometryPipelineError("Parasolid 分区提取失败: " + partitionId);
                }

                // Pick the largest file
                string extractedPath = candidates
                    .OrderByDescending(p => new FileInfo(p).Length)
                    .First();
                extractedPath = Path.GetFullPath(extractedPath);

                extracted.Add(new Partition(partitionId, label, extractedPath));
            }
            return extracted;
        }

        /// <summary>
        /// Compile the ParasolidAnalyzer C# source to an executable.
        /// Ported from _build_analyzer().
        /// </summary>
        private static string BuildAnalyzer(RuntimePaths runtime, string binDirectory)
        {
            string sourcePath = FindAnalyzerSource();
            if (!File.Exists(sourcePath))
            {
                throw new GeometryPipelineError("缺少几何分析器源码: " + sourcePath);
            }

            string executable = Path.Combine(binDirectory, "PartingSurface.ParasolidAnalyzer.exe");
            string managedCopy = Path.Combine(binDirectory, "pskernel_net.dll");
            File.Copy(runtime.PskernelNet, managedCopy, true);

            RunText(
                runtime.Csc,
                "/nologo",
                "/unsafe",
                "/platform:x64",
                "/reference:" + runtime.PskernelNet,
                "/out:" + executable,
                sourcePath);

            if (!File.Exists(executable))
            {
                throw new GeometryPipelineError("C# 几何分析器编译后未生成可执行文件");
            }
            return executable;
        }

        /// <summary>
        /// Find the ParasolidAnalyzer.cs source file.
        /// Searches relative to the assembly location and in candidate paths.
        /// </summary>
        private static string FindAnalyzerSource()
        {
            List<string> candidates = new List<string>();

            // Search relative to assembly location
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (!string.IsNullOrEmpty(assemblyDir))
            {
                candidates.Add(Path.Combine(assemblyDir, "ParasolidAnalyzer.cs"));
                // Go up a few levels looking for plugins/PartingSurface.Detection/
                string dir = assemblyDir;
                for (int i = 0; i < 6; i++)
                {
                    dir = Path.GetDirectoryName(dir);
                    if (string.IsNullOrEmpty(dir)) break;
                    candidates.Add(Path.Combine(dir, "plugins", "PartingSurface.Detection", "ParasolidAnalyzer.cs"));
                    candidates.Add(Path.Combine(dir, "analyzer", "ParasolidAnalyzer.cs"));
                    candidates.Add(Path.Combine(dir, "ParasolidAnalyzer.cs"));
                }
            }

            // Check current working directory
            candidates.Add(Path.Combine(Environment.CurrentDirectory, "ParasolidAnalyzer.cs"));
            candidates.Add(Path.Combine(Environment.CurrentDirectory, "plugins", "PartingSurface.Detection", "ParasolidAnalyzer.cs"));
            candidates.Add(Path.Combine(Environment.CurrentDirectory, "analyzer", "ParasolidAnalyzer.cs"));

            // Check environment variable
            string envPath = Environment.GetEnvironmentVariable("PARTING_SURFACE_ANALYZER_SOURCE");
            if (!string.IsNullOrEmpty(envPath))
            {
                candidates.Add(envPath);
            }

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }

            return candidates.FirstOrDefault() ?? "ParasolidAnalyzer.cs";
        }

        /// <summary>
        /// Run the compiled ParasolidAnalyzer executable.
        /// Ported from _run_analyzer().
        /// </summary>
        private static void RunAnalyzer(
            RuntimePaths runtime,
            string executable,
            string outputDirectory,
            List<Partition> partitions)
        {
            List<string> command = new List<string> { executable, outputDirectory };
            foreach (Partition partition in partitions)
            {
                command.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0}|{1}|{2}", partition.PartitionId, partition.Label, partition.ExtractedPath));
            }

            // Build environment with NXBIN in PATH
            Dictionary<string, string> env = new Dictionary<string, string>();
            string currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            env["PATH"] = runtime.Nxbin + Path.PathSeparator + currentPath;

            RunTextWithEnv(command.ToArray(), env, new int[] { 0 });

            // Verify required outputs
            string[] required = { "bodies.tsv", "faces.tsv", "edges.tsv" };
            foreach (string file in required)
            {
                if (!File.Exists(Path.Combine(outputDirectory, file)))
                {
                    throw new GeometryPipelineError("几何分析器缺少输出: " + file);
                }
            }
        }

        /// <summary>
        /// Assemble the full evidence dictionary from TSV output and metadata.
        /// Ported from _assemble_evidence().
        /// </summary>
        private static Dictionary<string, object> AssembleEvidence(
            string sourcePath,
            string sourceSha256,
            RuntimePaths runtime,
            Dictionary<string, object> partMetadata,
            string rawDirectory)
        {
            List<Dictionary<string, object>> bodies = ReadTsv(Path.Combine(rawDirectory, "bodies.tsv"))
                .Select(row => NormalizeBody(row)).ToList();
            List<Dictionary<string, object>> faces = ReadTsv(Path.Combine(rawDirectory, "faces.tsv"))
                .Select(row => NormalizeFace(row)).ToList();
            List<Dictionary<string, object>> edges = ReadTsv(Path.Combine(rawDirectory, "edges.tsv"))
                .Select(row => NormalizeEdge(row)).ToList();

            MarkDuplicateBodies(bodies);

            Dictionary<string, object> referenceBody = bodies.FirstOrDefault(b => JsonHelper.GetAsBool(b, "is_reference_solid") == true);
            List<Dictionary<string, object>> sheetBodies = bodies
                .Where(b => JsonHelper.GetAsString(b, "body_type") == "sheet_c")
                .ToList();

            Dictionary<int, Dictionary<string, object>> faceByTag = new Dictionary<int, Dictionary<string, object>>();
            foreach (Dictionary<string, object> face in faces)
            {
                int faceTag = JsonHelper.GetAsInt(face, "face_tag") ?? 0;
                faceByTag[faceTag] = face;
            }

            Dictionary<string, object> sharpSteelPolicy = LoadCandidatePolicy();

            // Convert edges and faceByTag to the types expected by SharpSteelDetector
            Tuple<List<Dictionary<string, object>>, Dictionary<string, object>> sharpSteelResult =
                SharpSteelDetector.Detect(edges, faceByTag, sharpSteelPolicy);
            List<Dictionary<string, object>> sharpSteels = sharpSteelResult.Item1;
            Dictionary<string, object> sharpSteelSummary = sharpSteelResult.Item2;

            // Surface type counts
            Dictionary<string, object> surfaceCounts = new Dictionary<string, object>();
            foreach (Dictionary<string, object> face in faces)
            {
                string surfaceClass = JsonHelper.GetAsString(face, "surface_class") ?? "unknown";
                if (surfaceCounts.ContainsKey(surfaceClass))
                {
                    surfaceCounts[surfaceClass] = (int)surfaceCounts[surfaceClass] + 1;
                }
                else
                {
                    surfaceCounts[surfaceClass] = 1;
                }
            }

            Dictionary<string, object> product = BuildProduct(referenceBody);
            Dictionary<string, object> partingSurface = BuildPartingSurface(sheetBodies, faces, edges, surfaceCounts);
            Dictionary<string, object> partingLine = BuildPartingLine(partingSurface, sharpSteels);

            List<object> limitations = new List<object>();
            List<Dictionary<string, object>> solidBodies = bodies
                .Where(b => JsonHelper.GetAsString(b, "body_type") == "solid_c" && JsonHelper.GetAsBool(b, "is_duplicate") != true)
                .ToList();
            bool exactSteelAvailable = solidBodies.Count >= 3;
            if (!exactSteelAvailable)
            {
                limitations.Add("未识别到独立型腔/型芯钢料实体；尖钢厚度、有效高度和细长比保持 unavailable。");
            }
            limitations.Add("未提供开模方向；倒扣和最大轮廓分型位置不作通过判断。");

            Dictionary<string, object> evidence = new Dictionary<string, object>
            {
                { "schema_version", SCHEMA_VERSION },
                { "meta", new Dictionary<string, object>
                    {
                        { "file_name", Path.GetFileName(sourcePath) },
                        { "full_path", sourcePath },
                        { "source_sha256", sourceSha256 },
                        { "extract_time_utc", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffK", CultureInfo.InvariantCulture) },
                        { "nx_release", JsonHelper.GetAsString(partMetadata, "nx_release") },
                        { "part_units", JsonHelper.GetAsString(partMetadata, "part_units") },
                        { "parasolid_version", JsonHelper.GetAsString(partMetadata, "parasolid_version") },
                        { "analysis_mode", "local_headless_parasolid" },
                        { "coordinate_scale_to_mm", NX_PARASOLID_TO_MM },
                        { "nx_root", runtime.NxRoot },
                    }
                },
                { "measurement_policy", new Dictionary<string, object>
                    {
                        { "missing_numeric_values", null },
                        { "edge_length_is_not_steel_thickness", true },
                        { "classification_levels", new List<object> { "confirmed_geometry_risk", "candidate", "unavailable" } },
                        { "sharp_steel_candidate_policy", sharpSteelPolicy },
                    }
                },
                { "bodies", bodies },
                { "role_assignment", new Dictionary<string, object>
                    {
                        { "reference_product_body_tag", referenceBody != null ? JsonHelper.GetAsInt(referenceBody, "body_tag") : null },
                        { "parting_surface_body_tags", sheetBodies.Select(b => (object)JsonHelper.GetAsInt(b, "body_tag")).ToList() },
                        { "exact_steel_measurement_available", exactSteelAvailable },
                    }
                },
                { "product", product },
                { "parting_surface", partingSurface },
                { "parting_line", partingLine },
                { "sharp_steels", sharpSteels },
                { "sharp_steel_summary", sharpSteelSummary },
                { "undercuts", new List<object>() },
                { "undercut_analysis", new Dictionary<string, object>
                    {
                        { "status", "unavailable" },
                        { "reason", "需要开模方向和产品外表面可见性分析。" },
                        { "pull_direction", null },
                    }
                },
                { "mold", new Dictionary<string, object>
                    {
                        { "cavity_material", null },
                        { "core_material", null },
                        { "expected_shot_life_k", null },
                    }
                },
                { "limitations", limitations },
            };

            return evidence;
        }

        /// <summary>
        /// Read a TSV file into a list of row dictionaries.
        /// Ported from _read_tsv().
        /// </summary>
        private static List<Dictionary<string, string>> ReadTsv(string path)
        {
            List<Dictionary<string, string>> rows = new List<Dictionary<string, string>>();
            using (StreamReader reader = new StreamReader(path, new UTF8Encoding(false)))
            {
                string headerLine = reader.ReadLine();
                if (headerLine == null) return rows;
                string[] headers = headerLine.Split('\t');

                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    string[] fields = line.Split('\t');
                    Dictionary<string, string> row = new Dictionary<string, string>(headers.Length);
                    for (int i = 0; i < headers.Length; i++)
                    {
                        row[headers[i]] = i < fields.Length ? fields[i] : "";
                    }
                    rows.Add(row);
                }
            }
            return rows;
        }

        /// <summary>
        /// Normalize a body TSV row. Ported from _normalize_body().
        /// </summary>
        private static Dictionary<string, object> NormalizeBody(Dictionary<string, string> row)
        {
            Dictionary<string, object> box = BoxMm(row);
            return new Dictionary<string, object>
            {
                { "partition_id", ParseInt(row, "partition_id") },
                { "partition_label", GetRow(row, "partition_label") },
                { "body_tag", ParseInt(row, "body_tag") },
                { "body_type", GetRow(row, "body_type") },
                { "face_count", ParseInt(row, "face_count") },
                { "edge_count", ParseInt(row, "edge_count") },
                { "vertex_count", ParseInt(row, "vertex_count") },
                { "bbox_mm", box },
                { "dimensions_mm", Dimensions(box) },
                { "is_reference_solid", GetRow(row, "is_reference_solid") == "true" },
                { "is_duplicate", false },
                { "duplicate_of_body_tag", null },
            };
        }

        /// <summary>
        /// Normalize a face TSV row. Ported from _normalize_face().
        /// </summary>
        private static Dictionary<string, object> NormalizeFace(Dictionary<string, string> row)
        {
            Dictionary<string, object> box = BoxMm(row);
            return new Dictionary<string, object>
            {
                { "partition_id", ParseInt(row, "partition_id") },
                { "body_tag", ParseInt(row, "body_tag") },
                { "face_tag", ParseInt(row, "face_tag") },
                { "surface_class", GetRow(row, "surface_class") },
                { "bbox_mm", box },
                { "dimensions_mm", Dimensions(box) },
                { "range_status", GetRow(row, "range_status") },
                { "distance_to_product_mm", ScaledFloat(row, "distance") },
                { "closest_to_product_mm", VectorMm(row, "closest") },
                { "normal_status", GetRow(row, "normal_status") },
                { "normal", Vector(row, "normal") },
                { "interior_point_mm", VectorMm(row, "interior") },
            };
        }

        /// <summary>
        /// Normalize an edge TSV row. Ported from _normalize_edge().
        /// </summary>
        private static Dictionary<string, object> NormalizeEdge(Dictionary<string, string> row)
        {
            double? radius = ScaledFloat(row, "min_radius");
            double validMax = SharpSteelDetector.DefaultPolicy()["valid_curve_radius_max_mm"] is double d ? d : 1000000.0;
            if (GetRow(row, "radius_status") != "no_errors" || radius == null || radius.Value <= 0.0 || radius.Value > validMax)
            {
                radius = null;
            }

            return new Dictionary<string, object>
            {
                { "partition_id", ParseInt(row, "partition_id") },
                { "body_tag", ParseInt(row, "body_tag") },
                { "edge_tag", ParseInt(row, "edge_tag") },
                { "curve_status", GetRow(row, "curve_status") },
                { "curve_class", GetRow(row, "curve_class") },
                { "interval_status", GetRow(row, "interval_status") },
                { "length_status", GetRow(row, "length_status") },
                { "edge_length_mm", ScaledFloat(row, "length") },
                { "radius_status", GetRow(row, "radius_status") },
                { "curve_min_radius_mm", radius },
                { "adjacent_count", ParseInt(row, "adjacent_count") },
                { "face_a", ParseInt(row, "face_a") },
                { "face_b", ParseInt(row, "face_b") },
                { "normal_status", GetRow(row, "normal_status") },
                { "normal_angle_deg", ParseFloat(row, "normal_angle_deg") },
                { "midpoint_status", GetRow(row, "midpoint_status") },
                { "midpoint_mm", VectorMm(row, "mid") },
                { "is_boundary", GetRow(row, "is_boundary") == "true" },
            };
        }

        /// <summary>
        /// Mark duplicate bodies by signature. Ported from _mark_duplicate_bodies().
        /// </summary>
        private static void MarkDuplicateBodies(List<Dictionary<string, object>> bodies)
        {
            Dictionary<string, Dictionary<string, object>> firstBySignature = new Dictionary<string, Dictionary<string, object>>();
            foreach (Dictionary<string, object> body in bodies)
            {
                Dictionary<string, object> box = JsonHelper.GetAsDict(body, "bbox_mm");
                List<object> minList = JsonHelper.GetAsList(box, "min");
                List<object> maxList = JsonHelper.GetAsList(box, "max");

                List<double> coords = new List<double>();
                if (minList != null) coords.AddRange(minList.Select(v => ToDoubleVal(v)));
                if (maxList != null) coords.AddRange(maxList.Select(v => ToDoubleVal(v)));

                StringBuilder sigBuilder = new StringBuilder();
                sigBuilder.Append(JsonHelper.GetAsString(body, "body_type")).Append("|");
                sigBuilder.Append(JsonHelper.GetAsInt(body, "face_count")).Append("|");
                sigBuilder.Append(JsonHelper.GetAsInt(body, "edge_count")).Append("|");
                sigBuilder.Append(JsonHelper.GetAsInt(body, "vertex_count")).Append("|");
                foreach (double coord in coords)
                {
                    sigBuilder.Append(Math.Round(coord, 3).ToString(CultureInfo.InvariantCulture)).Append(",");
                }
                string signature = sigBuilder.ToString();

                Dictionary<string, object> original;
                if (firstBySignature.TryGetValue(signature, out original))
                {
                    body["is_duplicate"] = true;
                    body["duplicate_of_body_tag"] = JsonHelper.GetAsInt(original, "body_tag");
                }
                else
                {
                    firstBySignature[signature] = body;
                }
            }
        }

        /// <summary>
        /// Build the product section of evidence. Ported from _build_product().
        /// </summary>
        private static Dictionary<string, object> BuildProduct(Dictionary<string, object> referenceBody)
        {
            if (referenceBody == null)
            {
                return new Dictionary<string, object>
                {
                    { "measurement_status", "unavailable" },
                    { "material", null },
                    { "bbox_mm", null },
                    { "dimensions_mm", null },
                    { "max_span_mm", null },
                    { "nominal_wall_thickness_mm", null },
                    { "max_projected_area_cm2", null },
                };
            }

            Dictionary<string, object> dimensions = JsonHelper.GetAsDict(referenceBody, "dimensions_mm");
            double maxSpan = 0;
            if (dimensions != null)
            {
                foreach (object value in dimensions.Values)
                {
                    double? d = ToDoubleNullable(value);
                    if (d != null && d.Value > maxSpan) maxSpan = d.Value;
                }
            }

            return new Dictionary<string, object>
            {
                { "measurement_status", "measured_bbox_only" },
                { "body_tag", JsonHelper.GetAsInt(referenceBody, "body_tag") },
                { "material", null },
                { "bbox_mm", JsonHelper.DeepClone(JsonHelper.GetAsDict(referenceBody, "bbox_mm")) },
                { "dimensions_mm", JsonHelper.DeepClone(dimensions) },
                { "max_span_mm", maxSpan },
                { "nominal_wall_thickness_mm", null },
                { "max_projected_area_cm2", null },
            };
        }

        /// <summary>
        /// Build the parting surface section of evidence. Ported from _build_parting_surface().
        /// </summary>
        private static Dictionary<string, object> BuildPartingSurface(
            List<Dictionary<string, object>> sheetBodies,
            List<Dictionary<string, object>> faces,
            List<Dictionary<string, object>> edges,
            Dictionary<string, object> surfaceCounts)
        {
            int faceCount = faces.Count;
            int planeCount = 0;
            object planeObj;
            if (surfaceCounts.TryGetValue("plane", out planeObj))
            {
                planeCount = planeObj is int ? (int)planeObj : 0;
            }

            double? flatnessScore = faceCount > 0
                ? Math.Round(10.0 * planeCount / faceCount, 1)
                : (double?)null;

            List<double> measuredDistances = new List<double>();
            foreach (Dictionary<string, object> face in faces)
            {
                double? distance = JsonHelper.GetAsDouble(face, "distance_to_product_mm");
                string rangeStatus = JsonHelper.GetAsString(face, "range_status");
                if (distance != null && rangeStatus == "found_c")
                {
                    measuredDistances.Add(distance.Value);
                }
            }

            Dictionary<string, object> result = new Dictionary<string, object>
            {
                { "measurement_status", sheetBodies.Count > 0 ? "measured" : "unavailable" },
                { "body_tags", sheetBodies.Select(b => (object)JsonHelper.GetAsInt(b, "body_tag")).ToList() },
                { "face_count", faceCount },
                { "edge_count", edges.Count },
                { "surface_type_counts", surfaceCounts },
                { "flatness_score", flatnessScore },
                { "is_planar", faceCount > 0 && planeCount == faceCount },
                { "bbox_mm", sheetBodies.Count == 1 ? JsonHelper.DeepClone(JsonHelper.GetAsDict(sheetBodies[0], "bbox_mm")) : null },
                { "min_face_distance_to_product_mm", measuredDistances.Count > 0 ? measuredDistances.Min() : (double?)null },
                { "faces_within_0_1_mm", measuredDistances.Count(v => v <= 0.1) },
                { "faces_within_5_mm", measuredDistances.Count(v => v <= 5.0) },
            };
            return result;
        }

        /// <summary>
        /// Build the parting line section of evidence. Ported from _build_parting_line().
        /// </summary>
        private static Dictionary<string, object> BuildPartingLine(
            Dictionary<string, object> partingSurface,
            List<Dictionary<string, object>> sharpSteels)
        {
            if (JsonHelper.GetAsString(partingSurface, "measurement_status") == "unavailable")
            {
                return new Dictionary<string, object>
                {
                    { "measurement_status", "unavailable" },
                    { "shape_type", null },
                    { "flatness_score", null },
                    { "coordinate_y_mm", null },
                    { "is_at_max_contour", null },
                };
            }

            bool isPlanar = JsonHelper.GetAsBool(partingSurface, "is_planar") ?? false;
            return new Dictionary<string, object>
            {
                { "measurement_status", "surface_inferred" },
                { "location_relative_to_product", null },
                { "shape_type", isPlanar ? "flat" : "complex_surface" },
                { "flatness_score", partingSurface.ContainsKey("flatness_score") ? partingSurface["flatness_score"] : null },
                { "coordinate_y_mm", null },
                { "max_product_diameter_at_pl_mm", null },
                { "is_at_max_contour", null },
                { "risk_candidate_count", sharpSteels.Count },
            };
        }

        /// <summary>
        /// Load the sharp steel candidate policy from review_rules.json.
        /// Ported from _load_candidate_policy().
        /// </summary>
        private static Dictionary<string, object> LoadCandidatePolicy()
        {
            string rulesPath = FindRulesPath();
            try
            {
                string json = File.ReadAllText(rulesPath, Encoding.UTF8);
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                Dictionary<string, object> payload = serializer.Deserialize<Dictionary<string, object>>(json);

                object configuredObj;
                if (payload == null || !payload.TryGetValue("sharp_steel_candidate_policy", out configuredObj) || !(configuredObj is Dictionary<string, object>))
                {
                    throw new GeometryPipelineError("review_rules.json 缺少 sharp_steel_candidate_policy");
                }

                Dictionary<string, object> configured = (Dictionary<string, object>)configuredObj;
                return JsonHelper.MergeDict(SharpSteelDetector.DefaultPolicy(), configured);
            }
            catch (GeometryPipelineError)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new GeometryPipelineError("无法读取尖钢候选规则: " + error.Message);
            }
        }

        /// <summary>
        /// Find the review_rules.json file.
        /// </summary>
        private static string FindRulesPath()
        {
            List<string> candidates = new List<string>();

            // Search relative to assembly location
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (!string.IsNullOrEmpty(assemblyDir))
            {
                candidates.Add(Path.Combine(assemblyDir, "review_rules.json"));
                candidates.Add(Path.Combine(assemblyDir, "rules", "review_rules.json"));
                string dir = assemblyDir;
                for (int i = 0; i < 6; i++)
                {
                    dir = Path.GetDirectoryName(dir);
                    if (string.IsNullOrEmpty(dir)) break;
                    candidates.Add(Path.Combine(dir, "skills", "parting-surface-review", "rules", "review_rules.json"));
                    candidates.Add(Path.Combine(dir, "rules", "review_rules.json"));
                }
            }

            // Check current working directory
            candidates.Add(Path.Combine(Environment.CurrentDirectory, "review_rules.json"));
            candidates.Add(Path.Combine(Environment.CurrentDirectory, "rules", "review_rules.json"));
            candidates.Add(Path.Combine(Environment.CurrentDirectory, "skills", "parting-surface-review", "rules", "review_rules.json"));

            // Check environment variable
            string envPath = Environment.GetEnvironmentVariable("PARTING_SURFACE_RULES_PATH");
            if (!string.IsNullOrEmpty(envPath))
            {
                candidates.Add(envPath);
            }

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }

            return candidates.FirstOrDefault() ?? "review_rules.json";
        }

        // --- TSV parsing helpers (ported from _box_mm, _dimensions, _vector_mm, etc.) ---

        /// <summary>
        /// Build a bounding box dict from a TSV row, scaling to mm.
        /// Ported from _box_mm().
        /// </summary>
        private static Dictionary<string, object> BoxMm(Dictionary<string, string> row)
        {
            return new Dictionary<string, object>
            {
                { "min", new List<object>
                    {
                        RequiredFloat(row, "min_x") * NX_PARASOLID_TO_MM,
                        RequiredFloat(row, "min_y") * NX_PARASOLID_TO_MM,
                        RequiredFloat(row, "min_z") * NX_PARASOLID_TO_MM,
                    }
                },
                { "max", new List<object>
                    {
                        RequiredFloat(row, "max_x") * NX_PARASOLID_TO_MM,
                        RequiredFloat(row, "max_y") * NX_PARASOLID_TO_MM,
                        RequiredFloat(row, "max_z") * NX_PARASOLID_TO_MM,
                    }
                },
            };
        }

        /// <summary>
        /// Compute dimensions from a bounding box. Ported from _dimensions().
        /// </summary>
        private static Dictionary<string, object> Dimensions(Dictionary<string, object> box)
        {
            List<object> minList = JsonHelper.GetAsList(box, "min");
            List<object> maxList = JsonHelper.GetAsList(box, "max");
            return new Dictionary<string, object>
            {
                { "x", ToDoubleVal(maxList[0]) - ToDoubleVal(minList[0]) },
                { "y", ToDoubleVal(maxList[1]) - ToDoubleVal(minList[1]) },
                { "z", ToDoubleVal(maxList[2]) - ToDoubleVal(minList[2]) },
            };
        }

        /// <summary>
        /// Build a vector dict from a TSV row, scaling to mm.
        /// Ported from _vector_mm().
        /// </summary>
        private static Dictionary<string, object> VectorMm(Dictionary<string, string> row, string prefix)
        {
            Dictionary<string, object> vector = Vector(row, prefix);
            if (vector == null) return null;
            return new Dictionary<string, object>
            {
                { "x", ToDoubleVal(vector["x"]) * NX_PARASOLID_TO_MM },
                { "y", ToDoubleVal(vector["y"]) * NX_PARASOLID_TO_MM },
                { "z", ToDoubleVal(vector["z"]) * NX_PARASOLID_TO_MM },
            };
        }

        /// <summary>
        /// Build a vector dict from a TSV row (no scaling).
        /// Ported from _vector().
        /// </summary>
        private static Dictionary<string, object> Vector(Dictionary<string, string> row, string prefix)
        {
            double? x = ParseFloat(row, prefix + "_x");
            double? y = ParseFloat(row, prefix + "_y");
            double? z = ParseFloat(row, prefix + "_z");
            if (x == null || y == null || z == null) return null;
            return new Dictionary<string, object>
            {
                { "x", x.Value },
                { "y", y.Value },
                { "z", z.Value },
            };
        }

        /// <summary>
        /// Parse a float and scale to mm. Ported from _scaled_float().
        /// </summary>
        private static double? ScaledFloat(Dictionary<string, string> row, string key)
        {
            double? parsed = ParseFloat(row, key);
            return parsed != null ? parsed.Value * NX_PARASOLID_TO_MM : (double?)null;
        }

        /// <summary>
        /// Parse a float, returning null for empty or non-finite values.
        /// Ported from _float().
        /// </summary>
        private static double? ParseFloat(Dictionary<string, string> row, string key)
        {
            string value = GetRow(row, key);
            if (string.IsNullOrEmpty(value)) return null;
            double parsed;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                return null;
            }
            return !double.IsNaN(parsed) && !double.IsInfinity(parsed) ? parsed : (double?)null;
        }

        /// <summary>
        /// Parse a required float, raising on missing values.
        /// Ported from _required_float().
        /// </summary>
        private static double RequiredFloat(Dictionary<string, string> row, string key)
        {
            double? parsed = ParseFloat(row, key);
            if (parsed == null)
            {
                throw new GeometryPipelineError("几何输出包含缺失的必需数值");
            }
            return parsed.Value;
        }

        /// <summary>
        /// Parse an int, defaulting to 0 for empty values.
        /// Ported from _int().
        /// </summary>
        private static int ParseInt(Dictionary<string, string> row, string key)
        {
            string value = GetRow(row, key);
            if (string.IsNullOrEmpty(value)) return 0;
            int parsed;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }
            return 0;
        }

        private static string GetRow(Dictionary<string, string> row, string key)
        {
            string value;
            return row.TryGetValue(key, out value) ? (value ?? "") : "";
        }

        private static double ToDoubleVal(object value)
        {
            if (value is double) return (double)value;
            if (value is int) return (double)(int)value;
            if (value is long) return (double)(long)value;
            if (value is float) return (double)(float)value;
            if (value is decimal) return (double)(decimal)value;
            double parsed;
            if (value != null && double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }
            return 0.0;
        }

        private static double? ToDoubleNullable(object value)
        {
            if (value == null) return null;
            if (value is double) return (double)value;
            if (value is int) return (double)(int)value;
            if (value is long) return (double)(long)value;
            if (value is float) return (double)(float)value;
            if (value is decimal) return (double)(decimal)value;
            double parsed;
            if (double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }
            return null;
        }

        /// <summary>
        /// Recursively delete a directory tree. Ported from _remove_tree().
        /// </summary>
        private static void RemoveTree(string path)
        {
            if (!Directory.Exists(path)) return;
            try
            {
                Directory.Delete(path, true);
            }
            catch
            {
                // Best effort, try again after clearing attributes
                try
                {
                    foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                    {
                        try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                    }
                    Directory.Delete(path, true);
                }
                catch
                {
                    // If still failing, ignore (best effort cleanup)
                }
            }
        }
    }
}
