using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NXOpen;
using PartingSurfaceReview.Analysis;
using PartingSurfaceReview.Repair;
using PartingSurfaceReview.Reporting;

namespace PartingSurfaceReview
{
    public class PartingSurfaceReviewCommand
    {
        private static Session _session;
        private static Part _workPart;
        private static ListingWindow _lw;

        public static void Main()
        {
            _session = Session.GetSession();
            _workPart = _session.Parts.Work;
            _lw = _session.ListingWindow;
            _lw.Open();

            if (_workPart == null)
            {
                _lw.WriteLine("ERROR: No work part open");
                return;
            }

            try { RunFullWorkflow(); }
            catch (Exception ex)
            {
                _lw.WriteLine("FATAL: " + ex.GetType().Name + ": " + ex.Message);
                _lw.WriteLine(ex.StackTrace);
            }
        }

        private static void Log(string msg) { _lw.WriteLine(msg); }

        private static void RunFullWorkflow()
        {
            Log("");
            Log("========================================");
            Log(" 分型面尖钢审查 v6.0");
            Log(" 部件: " + _workPart.FullPath);
            Log("========================================");

            var analyzer = new GeometryAnalyzer();
            Body partingSurface = analyzer.FindPartingSurface();
            Body productSolid = analyzer.FindProductSolid();

            Log("产品实体: " + (productSolid != null ? "Tag=" + productSolid.Tag + " F=" + productSolid.GetFaces().Length : "未找到"));
            Log("分型面: " + (partingSurface != null ? "Tag=" + partingSurface.Tag + " F=" + partingSurface.GetFaces().Length : "未找到"));

            if (partingSurface == null) { Log("ERROR: 未找到分型面"); return; }

            // Analyze
            Log(""); Log("--- 几何检测 ---");
            List<ReviewIssue> issues = analyzer.Analyze(partingSurface, productSolid);
            var report = analyzer.MakeReport(issues, partingSurface);
            report.SourceSha256 = analyzer.ComputeSha256(_workPart.FullPath);
            Log("源文件 SHA-256: " + report.SourceSha256);
            Log("完成: " + report.ErrorCount + " ERROR, " + report.WarnCount + " WARN");

            if (issues.Count == 0)
            {
                Log("未检测到风险。");
                UI.GetUI().NXMessageBox.Show("OK", NXMessageBox.DialogType.Information, "未检测到分型面尖钢风险。");
                return;
            }

            foreach (var issue in issues)
            {
                string pf = issue.Severity == IssueSeverity.ERROR ? "ERROR" : "WARN";
                Log(pf + " " + issue.IssueId + ": (" + issue.X.ToString("F1") + "," + issue.Y.ToString("F1") + "," + issue.Z.ToString("F1") + ") L=" + issue.EdgeLengthMm.ToString("F4") + " A=" + issue.WedgeAngleDeg.ToString("F1"));
            }

            // Report
            string srcDir = Path.GetDirectoryName(_workPart.FullPath);
            string txtPath = ReportWriter.WriteTextReport(report, srcDir);
            Log("报告: " + txtPath);

            // Ask user
            int choice = UI.GetUI().NXMessageBox.Show("分型面尖钢审查",
                NXMessageBox.DialogType.Question,
                report.ErrorCount + " ERROR, " + report.WarnCount + " WARN\n\n[是] 创建受保护工作副本并自动修复\n[否] 仅输出报告\n[取消] 结束");

            if (choice == 2 || choice == 3) { Log(choice == 2 ? "仅报告。" : "取消。"); return; }

            // Save As working copy
            string srcPath = _workPart.FullPath;
            string srcName = Path.GetFileNameWithoutExtension(srcPath);
            string workPath = Path.Combine(srcDir, srcName + "_working_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".prt");

            Log(""); Log("--- 创建工作副本 ---");
            string srcHashBefore = analyzer.ComputeSha256(srcPath);
            if (srcHashBefore != report.SourceSha256) { Log("ERROR: 源文件被修改!"); return; }

            try { _workPart.SaveAs(workPath); }
            catch (Exception ex) { Log("ERROR: SaveAs " + ex.Message); return; }

            string srcHashAfter = analyzer.ComputeSha256(srcPath);
            if (srcHashAfter != srcHashBefore) { Log("FATAL: 源文件哈希变化!"); return; }
            Log("源文件未变化 (OK)");

            // Repair
            Log(""); Log("--- 自动修复 ---");
            var repairOps = new FaceRepairOps();
            int autoFixed = 0, autoFailed = 0;

            foreach (var issue in issues)
            {
                // 修复所有可自动处理的几何问题（ERROR + WARN），仅跳过需人工判断的项
                if (issue.SuggestedAction == RepairActionType.ManualReview) continue;

                Log("修复 " + issue.IssueId + " ...");
                try
                {
                    Session.UndoMarkId mark = repairOps.CreateUndoMark();
                    RepairResult rr = null;

                    if (issue.SuggestedAction == RepairActionType.FaceDeleteAndHeal)
                        rr = repairOps.DeleteFacesAndHeal(issue.FaceTags);
                    else if (issue.SuggestedAction == RepairActionType.FaceJoin && issue.FaceTags.Count >= 2)
                        rr = repairOps.JoinFaces(new List<int>{issue.FaceTags[0]}, new List<int>{issue.FaceTags[1]});
                    else if (issue.SuggestedAction == RepairActionType.EdgeBlend)
                    {
                        // EdgeBlendBuilder unreliable on sheet bodies — redirect to delete face on adjacent faces
                        Log("  改用删面愈合 (EdgeBlend在片体上不稳定)");
                        rr = repairOps.DeleteFacesAndHeal(issue.FaceTags);
                    }

                    if (rr != null && rr.Success)
                    {
                        issue.IsResolved = true;
                        issue.ResolvedNote = "自动修复";
                        autoFixed++;
                        Log("  成功 (面数: " + rr.FacesBefore + " -> " + rr.FacesAfter + ")");
                    }
                    else
                    {
                        repairOps.UndoToMark(mark);
                        autoFailed++;
                        string errDetail = (rr != null && !string.IsNullOrEmpty(rr.ErrorMessage)) ? rr.ErrorMessage : "未知错误";
                        Log("  失败: " + errDetail + " — 已回滚");
                    }
                }
                catch (Exception ex)
                {
                    autoFailed++;
                    Log("  异常: " + ex.Message);
                }
            }

            _workPart.Save(BasePart.SaveComponents.False, BasePart.CloseAfterSave.False);

            // Re-review
            Log(""); Log("--- 复审 ---");
            var reAnalyzer = new GeometryAnalyzer();
            Body rePS = reAnalyzer.FindPartingSurface();
            if (rePS == null) { Log("WARNING: 分型面丢失"); return; }

            List<ReviewIssue> reIssues = reAnalyzer.Analyze(rePS, productSolid);
            foreach (var old in issues)
            {
                if (!old.IsResolved) continue;
                foreach (var ni in reIssues)
                {
                    double dx = ni.X-old.X, dy = ni.Y-old.Y, dz = ni.Z-old.Z;
                    if (Math.Sqrt(dx*dx+dy*dy+dz*dz) <= 3.0)
                    { old.IsResolved = false; old.ResolvedNote = "复审仍存在"; break; }
                }
            }

            var reReport = reAnalyzer.MakeReport(reIssues, rePS);
            reReport.ResolvedCount = issues.Count(i => i.IsResolved);
            ReportWriter.WriteTextReport(reReport, srcDir);

            // Deliver
            Log("");
            bool hasErr = false;
            foreach (var ni in reIssues) if (ni.Severity == IssueSeverity.ERROR) { hasErr = true; break; }

            if (!hasErr && autoFailed == 0)
            {
                string reviewedPath = Path.Combine(srcDir, srcName + "_reviewed_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".prt");
                _workPart.SaveAs(reviewedPath);
                Log("========================================");
                Log(" 审查通过!");
                Log("源文件: " + srcPath + " (未修改)");
                Log("审查后: " + reviewedPath);
                Log("修复: " + autoFixed + " 项");
                UI.GetUI().NXMessageBox.Show("完成", NXMessageBox.DialogType.Information,
                    "审查通过!\n\n" + reviewedPath + "\n修复 " + autoFixed + " 项。");
            }
            else
            {
                Log("========================================");
                Log(" 复审未通过 - 仅保存工作副本");
                Log("工作副本: " + workPath);
                Log("源文件 " + srcPath + " 未被修改。");
                UI.GetUI().NXMessageBox.Show("复审未通过", NXMessageBox.DialogType.Warning,
                    "仍有 ERROR。\n工作副本: " + workPath);
            }
        }

        /// <summary>
        /// NX Open unload option — called by NX when unloading the library.
        /// </summary>
        public static int GetUnloadOption(string arg)
        {
            return System.Convert.ToInt32(Session.LibraryUnloadOption.Immediately);
        }
    }
}