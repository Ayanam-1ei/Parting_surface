using System;
using System.IO;
using PartingSurface.Detection;
using PartingSurface.Repair;

namespace PartingSurface.PluginHost
{
    /// <summary>
    /// 测试宿主程序
    /// 模拟图片中的三角色流程：用户 → AI → DLL
    /// 用法:
    ///   detect  <prtPath> [outputJson]     执行检测
    ///   repair  <prtPath> <jsonReport>     执行修复（含再检测）
    ///   scripts <prtPath> <jsonReport>     仅生成修复脚本
    ///   full    <prtPath>                  完整流程：检测→修复→再检测
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                PrintUsage();
                return 2;
            }

            string command = args[0].ToLowerInvariant();

            try
            {
                switch (command)
                {
                    case "detect":
                        return RunDetect(args);
                    case "repair":
                        return RunRepair(args);
                    case "scripts":
                        return RunScripts(args);
                    case "full":
                        return RunFull(args);
                    default:
                        Console.Error.WriteLine("未知命令: " + command);
                        PrintUsage();
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ERROR: " + ex.GetType().Name + ": " + ex.Message);
                Console.Error.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        private static int RunDetect(string[] args)
        {
            string prtPath = args[1];
            string outputPath = args.Length > 2 ? args[2] : null;

            Console.WriteLine("[Host] === 检测 DLL 启动 ===");
            Console.WriteLine("[Host] PRT: " + prtPath);

            string json;
            if (outputPath != null)
            {
                string savedPath = DetectionGateway.DetectToFile(prtPath, outputPath);
                Console.WriteLine("[Host] JSON 已保存: " + savedPath);
                json = File.ReadAllText(savedPath);
            }
            else
            {
                json = DetectionGateway.Detect(prtPath);
            }

            Console.WriteLine("[Host] === 检测完成 ===");
            Console.WriteLine(json);
            return 0;
        }

        private static int RunRepair(string[] args)
        {
            string prtPath = args[1];
            string jsonReport = args[2];

            Console.WriteLine("[Host] === 修复 DLL 启动 ===");
            Console.WriteLine("[Host] PRT: " + prtPath);
            Console.WriteLine("[Host] 报告来源: " + (File.Exists(jsonReport) ? "文件" : "内联"));

            string reportJson = File.Exists(jsonReport) ? File.ReadAllText(jsonReport) : jsonReport;

            var result = RepairGateway.Repair(prtPath, reportJson);

            Console.WriteLine("[Host] === 修复完成 ===");
            Console.WriteLine("[Host] 成功: " + result.Success);
            Console.WriteLine("[Host] 操作数: " + result.OperationsPlanned);
            Console.WriteLine("[Host] 耗时: " + result.ExecutionSeconds.ToString("F1") + "s");
            Console.WriteLine("[Host] 输出PRT: " + (result.OutputPrtPath ?? "无"));
            Console.WriteLine("[Host] 摘要: " + result.Summary);

            if (result.Errors.Count > 0)
            {
                Console.Error.WriteLine("[Host] 错误:");
                foreach (var err in result.Errors)
                    Console.Error.WriteLine("  - " + err);
            }

            if (!string.IsNullOrEmpty(result.ReDetectionJson))
            {
                Console.WriteLine("[Host] === 再检测 JSON ===");
                Console.WriteLine(result.ReDetectionJson);
            }

            return result.Success ? 0 : 1;
        }

        private static int RunScripts(string[] args)
        {
            string prtPath = args[1];
            string jsonReport = args[2];

            Console.WriteLine("[Host] === 生成修复脚本 ===");

            string reportJson = File.Exists(jsonReport) ? File.ReadAllText(jsonReport) : jsonReport;
            var result = RepairGateway.GenerateScripts(reportJson, prtPath);

            Console.WriteLine("[Host] 成功: " + result.Success);
            Console.WriteLine("[Host] 摘要: " + result.Summary);
            foreach (var s in result.ScriptsGenerated)
                Console.WriteLine("  " + s);

            return result.Success ? 0 : 1;
        }

        private static int RunFull(string[] args)
        {
            string prtPath = args[1];
            string nxRoot = args.Length > 2 ? args[2] : null;

            Console.WriteLine("[Host] ======== 完整流程启动 ========");
            Console.WriteLine("[Host] PRT: " + prtPath);
            Console.WriteLine();

            // Step 1: 检测
            Console.WriteLine("[Host] >>> Step 1: 检测 DLL");
            string detectionJson = DetectionGateway.Detect(prtPath, nxRoot);
            Console.WriteLine("[Host] 检测完成");
            Console.WriteLine();

            // Step 2: 修复
            Console.WriteLine("[Host] >>> Step 2: 修复 DLL");
            var repairResult = RepairGateway.Repair(prtPath, detectionJson, nxRoot);
            Console.WriteLine("[Host] 修复完成: " + repairResult.Success);
            Console.WriteLine("[Host] 摘要: " + repairResult.Summary);
            Console.WriteLine();

            // Step 3: 再检测（已内置在 Repair 中）
            Console.WriteLine("[Host] >>> Step 3: 闭环再检测");
            if (!string.IsNullOrEmpty(repairResult.ReDetectionJson))
            {
                Console.WriteLine("[Host] 再检测完成");
            }
            else
            {
                Console.WriteLine("[Host] 再检测未执行（可能修复未生成新 PRT）");
            }
            Console.WriteLine();

            Console.WriteLine("[Host] ======== 完整流程结束 ========");
            Console.WriteLine("[Host] 最终状态: " + (repairResult.Success ? "成功" : "失败"));
            return repairResult.Success ? 0 : 1;
        }

        private static void PrintUsage()
        {
            Console.Error.WriteLine("用法:");
            Console.Error.WriteLine("  PartingSurface.PluginHost detect  <prtPath> [outputJson]");
            Console.Error.WriteLine("  PartingSurface.PluginHost repair  <prtPath> <jsonReport>");
            Console.Error.WriteLine("  PartingSurface.PluginHost scripts <prtPath> <jsonReport>");
            Console.Error.WriteLine("  PartingSurface.PluginHost full    <prtPath> [nxRoot]");
        }
    }
}
