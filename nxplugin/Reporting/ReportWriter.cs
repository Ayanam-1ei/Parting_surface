using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PartingSurfaceReview.Reporting
{
    public class ReportWriter
    {
        public static string WriteTextReport(ReviewReport report, string outputDir)
        {
            string path = Path.Combine(outputDir,
                string.Format("PSReview_{0:yyyyMMdd_HHmmss}.txt", DateTime.Now));

            var sb = new StringBuilder();
            sb.AppendLine("========================================");
            sb.AppendLine(" 分型面尖钢审查报告");
            sb.AppendLine("========================================");
            sb.AppendLine("报告ID: " + report.ReportId);
            sb.AppendLine("时间: " + report.ReviewTime.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("部件: " + report.PartFileName);
            sb.AppendLine("结论: " + report.OverallStatus);
            sb.AppendLine("问题: " + report.TotalIssues + " (ERROR=" + report.ErrorCount + " WARN=" + report.WarnCount + " 已修复=" + report.ResolvedCount + ")");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(report.PartingSurfaceSummary))
                sb.AppendLine(report.PartingSurfaceSummary);
            sb.AppendLine();

            foreach (var issue in report.Issues)
            {
                string st = issue.IsResolved ? "[已修复]" : "[未处理]";
                string sev = issue.Severity == IssueSeverity.ERROR ? "ERROR" : "WARN";
                sb.AppendLine(st + " " + sev + " " + issue.IssueId);
                sb.AppendLine(string.Format("  坐标: X={0:F3} Y={1:F3} Z={2:F3}", issue.X, issue.Y, issue.Z));
                sb.AppendLine(string.Format("  边长={0:F6}mm 夹角={1:F1}deg 窄面={2:F6}mm", issue.EdgeLengthMm, issue.WedgeAngleDeg, issue.NarrowFaceDimMm));
                sb.AppendLine("  " + issue.Description);
                sb.AppendLine("  建议: " + issue.RepairInstruction);
                if (!string.IsNullOrEmpty(issue.ResolvedNote))
                    sb.AppendLine("  " + issue.ResolvedNote);
                sb.AppendLine();
            }

            if (report.UnavailableChecks != null && report.UnavailableChecks.Count > 0)
            {
                sb.AppendLine("不可用检查项:");
                foreach (string u in report.UnavailableChecks)
                    sb.AppendLine("  - " + u);
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return path;
        }
    }
}
