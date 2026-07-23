using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace PartingSurface.Repair
{
    /// <summary>
    /// 单个修复操作
    /// </summary>
    public class RepairOperation
    {
        public string IssueId;
        public string Type;
        public string Severity;
        public string Instruction;
        public string Priority;
        public double CoordX;
        public double CoordY;
        public double CoordZ;
        public double RepresentativeEdgeLengthMm;
        public double WedgeAngleDeg;
        public double MinNarrowFaceDimensionMm;
        public double DistanceToProductMm;
        public string Classification;
        public List<string> NarrowFaceTags = new List<string>();
        public Dictionary<string, object> Parameters = new Dictionary<string, object>();
    }

    /// <summary>
    /// 解析检测报告 JSON，规划修复操作
    /// </summary>
    public static class RepairPlanner
    {
        /// <summary>
        /// 从 JSON 检测报告规划修复操作
        /// </summary>
        public static List<RepairOperation> PlanRepairs(string jsonReport)
        {
            var serializer = new JavaScriptSerializer();
            var report = serializer.Deserialize<Dictionary<string, object>>(jsonReport);
            var operations = new List<RepairOperation>();

            if (report == null)
                return operations;

            // 支持两种 JSON 结构：
            // 1. 顶层 issues（直接是 review 对象）
            // 2. 嵌套 review.issues（DetectionGateway 的完整输出）
            object issuesObj = null;
            object reviewObj;
            if (report.TryGetValue("review", out reviewObj) && reviewObj is Dictionary<string, object>)
            {
                (reviewObj as Dictionary<string, object>).TryGetValue("issues", out issuesObj);
            }
            if (issuesObj == null)
            {
                report.TryGetValue("issues", out issuesObj);
            }
            if (issuesObj == null)
                return operations;

            var issues = ToObjectList(issuesObj);
            if (issues == null) return operations;

            foreach (var issueObj in issues)
            {
                var issue = issueObj as Dictionary<string, object>;
                if (issue == null) continue;

                var op = ParseIssue(issue);
                if (op != null)
                    operations.Add(op);
            }

            operations.Sort((a, b) =>
            {
                int ra = a.Priority == "A" ? 0 : a.Priority == "B" ? 1 : 2;
                int rb = b.Priority == "A" ? 0 : b.Priority == "B" ? 1 : 2;
                return ra.CompareTo(rb);
            });

            return operations;
        }

        private static RepairOperation ParseIssue(Dictionary<string, object> issue)
        {
            var op = new RepairOperation();
            op.IssueId = GetStr(issue, "issue_id");
            op.Severity = GetStr(issue, "severity");
            op.Classification = GetStr(issue, "classification");

            var coord = issue["coordinate_approx"] as Dictionary<string, object>;
            if (coord != null)
            {
                op.CoordX = GetDouble(coord, "x");
                op.CoordY = GetDouble(coord, "y");
                op.CoordZ = GetDouble(coord, "z");
            }

            var measurements = issue["measurements"] as Dictionary<string, object>;
            if (measurements != null)
            {
                op.RepresentativeEdgeLengthMm = GetDouble(measurements, "representative_edge_length_mm");
                op.WedgeAngleDeg = GetDouble(measurements, "wedge_angle_deg");
                op.MinNarrowFaceDimensionMm = GetDouble(measurements, "min_narrow_face_dimension_mm");
                op.DistanceToProductMm = GetDouble(measurements, "distance_to_product_mm");
            }

            var evidence = issue["geometry_evidence"] as Dictionary<string, object>;
            if (evidence != null && evidence.ContainsKey("narrow_face_tags"))
            {
                var tags = ToObjectList(evidence["narrow_face_tags"]);
                if (tags != null)
                    foreach (var t in tags) op.NarrowFaceTags.Add(Convert.ToString(t));
            }

            var recommendations = ToObjectList(issue["recommendations"]);
            if (recommendations != null && recommendations.Count > 0)
            {
                var rec0 = recommendations[0] as Dictionary<string, object>;
                if (rec0 != null)
                {
                    op.Priority = GetStr(rec0, "priority");
                    op.Type = GetStr(rec0, "type");
                    op.Instruction = GetStr(rec0, "instruction");
                    op.Parameters = rec0["parameters"] as Dictionary<string, object> ?? new Dictionary<string, object>();
                }
            }

            if (op.Severity == "WARN" && op.Classification == "candidate")
            {
                op.Type = "manual_review";
                op.Priority = "M";
                op.Instruction = "候选级尖钢风险，需工程师确认该处是否形成真实尖钢后决定修复策略。";
            }

            return op;
        }

        private static string GetStr(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";
        }

        private static double GetDouble(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return 0.0;
            return Convert.ToDouble(v, CultureInfo.InvariantCulture);
        }

        private static List<object> ToObjectList(object obj)
        {
            if (obj is System.Collections.ArrayList al)
            {
                var list = new List<object>(al.Count);
                foreach (var item in al) list.Add(item);
                return list;
            }
            if (obj is List<object> lo) return lo;
            return null;
        }
    }
}
