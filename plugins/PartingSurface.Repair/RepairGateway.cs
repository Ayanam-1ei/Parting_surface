using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PartingSurface.Repair
{
    /// <summary>
    /// 修复结果
    /// </summary>
    public class RepairResult
    {
        public bool Success;
        public string OutputPrtPath;
        public string ReDetectionJson;
        public string Summary;
        public List<string> ScriptsGenerated = new List<string>();
        public List<string> Errors = new List<string>();
        public int OperationsPlanned;
        public double ExecutionSeconds;
    }

    /// <summary>
    /// 修复网关 - 公共 API 入口
    /// 对应图片中的"指挥修复 DLL → 读报告 → 修面 → 保存新 PRT → 再检测"
    /// </summary>
    public static class RepairGateway
    {
        /// <summary>
        /// 完整修复流程：解析报告 → 规划修复 → 生成脚本 → 执行 → 再检测
        /// </summary>
        public static RepairResult Repair(string prtPath, string jsonReport, string nxRoot = null)
        {
            var result = new RepairResult();

            // Step 1: 定位 NX 运行时
            if (string.IsNullOrEmpty(nxRoot))
                nxRoot = FindNxRoot();

            if (string.IsNullOrEmpty(nxRoot))
            {
                result.Success = false;
                result.Errors.Add("无法定位 NX 安装目录");
                return result;
            }

            // Step 2: 规划修复操作
            var operations = RepairPlanner.PlanRepairs(jsonReport);
            result.OperationsPlanned = operations.Count;

            if (operations.Count == 0)
            {
                result.Success = true;
                result.Summary = "检测报告中无需要修复的问题。";
                return result;
            }

            // Step 3: 生成 NX Journal Python 脚本
            var scripts = ScriptGenerator.Generate(operations, prtPath);
            var masterScript = ScriptGenerator.GenerateMasterScript(scripts, prtPath);
            scripts.Add(masterScript);

            string outputDir = Path.Combine(Path.GetDirectoryName(prtPath), "repair_scripts");
            foreach (var s in scripts)
                result.ScriptsGenerated.Add(s.FileName);

            // Step 4: 执行脚本
            var execResult = ScriptExecutor.Execute(scripts, prtPath, nxRoot, outputDir);

            result.ExecutionSeconds = execResult.TotalSeconds;

            if (!execResult.AllSuccess)
            {
                result.Success = false;
                result.Errors.AddRange(execResult.Errors);
                result.Summary = string.Format("修复执行失败，{0} 个错误。", execResult.Errors.Count);
                return result;
            }

            // Step 5: 查找修复后的 PRT 文件
            string outputPrt = FindRepairedPrt(prtPath, outputDir);
            result.OutputPrtPath = outputPrt;
            result.Success = true;

            // Step 6: 再检测（闭环验证）
            if (!string.IsNullOrEmpty(outputPrt) && File.Exists(outputPrt))
            {
                try
                {
                    result.ReDetectionJson = PartingSurface.Detection.DetectionGateway.Detect(outputPrt, nxRoot);
                    result.Summary = BuildSummary(operations, execResult, result.ReDetectionJson);
                }
                catch (Exception ex)
                {
                    result.Summary = "修复完成，但再检测失败: " + ex.Message;
                    result.Errors.Add("再检测失败: " + ex.Message);
                }
            }
            else
            {
                result.Summary = "修复脚本已执行，但未找到输出 PRT 文件。";
            }

            return result;
        }

        /// <summary>
        /// 仅生成修复脚本，不执行（用于预览或人工审核）
        /// </summary>
        public static RepairResult GenerateScripts(string jsonReport, string prtPath, string outputDir = null)
        {
            var result = new RepairResult();

            var operations = RepairPlanner.PlanRepairs(jsonReport);
            result.OperationsPlanned = operations.Count;

            if (operations.Count == 0)
            {
                result.Success = true;
                result.Summary = "检测报告中无需要修复的问题。";
                return result;
            }

            var scripts = ScriptGenerator.Generate(operations, prtPath);
            var masterScript = ScriptGenerator.GenerateMasterScript(scripts, prtPath);
            scripts.Add(masterScript);

            if (string.IsNullOrEmpty(outputDir))
                outputDir = Path.Combine(Path.GetDirectoryName(prtPath), "repair_scripts");

            var writeResult = ScriptExecutor.WriteOnly(scripts, outputDir);
            result.ScriptsGenerated.AddRange(writeResult.Outputs);
            result.Success = true;
            result.Summary = string.Format("已生成 {0} 个修复脚本到 {1}", scripts.Count, outputDir);

            return result;
        }

        private static string FindRepairedPrt(string originalPrt, string searchDir)
        {
            string stem = Path.GetFileNameWithoutExtension(originalPrt);
            var candidates = Directory.GetFiles(Path.GetDirectoryName(originalPrt), stem + "_repaired_*.prt")
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .ToList();

            if (candidates.Count > 0)
                return candidates[0];

            var dirCandidates = Directory.GetFiles(searchDir, "*_repaired_*.prt")
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .ToList();

            return dirCandidates.Count > 0 ? dirCandidates[0] : null;
        }

        private static string BuildSummary(List<RepairOperation> operations, ExecutionResult execResult, string reDetectionJson)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Format("修复完成：{0} 个操作，耗时 {1:F1}s", operations.Count, execResult.TotalSeconds));

            int errorCount = operations.Count(o => o.Severity == "ERROR");
            int warnCount = operations.Count(o => o.Severity == "WARN");
            sb.AppendLine(string.Format("  - ERROR 级: {0} 个", errorCount));
            sb.AppendLine(string.Format("  - WARN 级: {0} 个", warnCount));

            var byType = operations.GroupBy(o => o.Type);
            foreach (var g in byType)
            {
                sb.AppendLine(string.Format("  - {0}: {1} 个", g.Key, g.Count()));
            }

            if (!string.IsNullOrEmpty(reDetectionJson))
            {
                sb.AppendLine("  - 闭环再检测已执行");
            }

            return sb.ToString();
        }

        private static string FindNxRoot()
        {
            string[] envVars = { "PARTING_SURFACE_NX_ROOT", "UGII_BASE_DIR", "NX_ROOT" };
            foreach (var v in envVars)
            {
                string val = Environment.GetEnvironmentVariable(v);
                if (!string.IsNullOrEmpty(val) && Directory.Exists(val))
                    return val;
            }

            string[] paths = { @"S:\nx", @"C:\Program Files\Siemens\NX2306", @"C:\Program Files\Siemens\NX" };
            foreach (var p in paths)
            {
                if (Directory.Exists(p) && File.Exists(Path.Combine(p, "NXBIN", "ug_inspect.exe")))
                    return p;
            }

            return null;
        }
    }
}
