using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace PartingSurface.Repair
{
    /// <summary>
    /// 脚本执行结果
    /// </summary>
    public class ExecutionResult
    {
        public bool AllSuccess;
        public List<string> Outputs = new List<string>();
        public List<string> Errors = new List<string>();
        public List<string> ScriptPaths = new List<string>();
        public double TotalSeconds;
    }

    /// <summary>
    /// 执行 NX Journal Python 脚本
    /// </summary>
    public static class ScriptExecutor
    {
        /// <summary>
        /// 将脚本写入磁盘并执行
        /// </summary>
        public static ExecutionResult Execute(List<GeneratedScript> scripts, string prtPath, string nxRoot, string outputDir)
        {
            var result = new ExecutionResult();
            var watch = System.Diagnostics.Stopwatch.StartNew();

            Directory.CreateDirectory(outputDir);

            // 写入所有脚本文件
            foreach (var script in scripts)
            {
                string scriptPath = Path.Combine(outputDir, script.FileName);
                File.WriteAllText(scriptPath, script.Content, new System.Text.UTF8Encoding(false));
                result.ScriptPaths.Add(scriptPath);
            }

            // 查找 run_journal.exe
            string runJournal = FindRunJournal(nxRoot);
            if (runJournal == null)
            {
                result.AllSuccess = false;
                result.Errors.Add("未找到 run_journal.exe。NX Root: " + nxRoot);
                watch.Stop();
                result.TotalSeconds = watch.Elapsed.TotalSeconds;
                return result;
            }

            // 查找主脚本（如果有）
            string masterScript = null;
            foreach (var script in scripts)
            {
                if (script.OperationType == "master")
                {
                    masterScript = Path.Combine(outputDir, script.FileName);
                    break;
                }
            }

            // 如果没有主脚本，按顺序执行每个脚本
            if (masterScript != null)
            {
                ExecuteSingleScript(runJournal, masterScript, prtPath, result);
            }
            else
            {
                foreach (var scriptPath in result.ScriptPaths)
                {
                    string fileName = Path.GetFileName(scriptPath);
                    if (fileName.StartsWith("repair_master")) continue;

                    bool success = ExecuteSingleScript(runJournal, scriptPath, prtPath, result);
                    if (!success)
                    {
                        result.AllSuccess = false;
                        result.Errors.Add("脚本执行失败: " + fileName);
                        break;
                    }
                }
                result.AllSuccess = result.Errors.Count == 0;
            }

            watch.Stop();
            result.TotalSeconds = watch.Elapsed.TotalSeconds;
            return result;
        }

        /// <summary>
        /// 仅写入脚本文件，不执行（用于预览）
        /// </summary>
        public static ExecutionResult WriteOnly(List<GeneratedScript> scripts, string outputDir)
        {
            var result = new ExecutionResult { AllSuccess = true };
            Directory.CreateDirectory(outputDir);

            foreach (var script in scripts)
            {
                string scriptPath = Path.Combine(outputDir, script.FileName);
                File.WriteAllText(scriptPath, script.Content, new System.Text.UTF8Encoding(false));
                result.ScriptPaths.Add(scriptPath);
                result.Outputs.Add("已写入: " + scriptPath);
            }

            return result;
        }

        private static bool ExecuteSingleScript(string runJournal, string scriptPath, string prtPath, ExecutionResult result)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = runJournal,
                    Arguments = "\"" + scriptPath + "\" -auto",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(runJournal)
                };

                var proc = Process.Start(psi);
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(120000); // 2分钟超时

                if (!string.IsNullOrEmpty(stdout))
                    result.Outputs.Add("[" + Path.GetFileName(scriptPath) + "] " + stdout.Trim());

                if (proc.ExitCode != 0)
                {
                    if (!string.IsNullOrEmpty(stderr))
                        result.Errors.Add("[" + Path.GetFileName(scriptPath) + "] " + stderr.Trim());
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                result.Errors.Add("[" + Path.GetFileName(scriptPath) + "] " + ex.Message);
                return false;
            }
        }

        private static string FindRunJournal(string nxRoot)
        {
            string[] candidates =
            {
                Path.Combine(nxRoot, "NXBIN", "run_journal.exe"),
                Path.Combine(nxRoot, "UGII", "run_journal.exe"),
            };

            foreach (var c in candidates)
                if (File.Exists(c)) return c;

            return null;
        }
    }
}
