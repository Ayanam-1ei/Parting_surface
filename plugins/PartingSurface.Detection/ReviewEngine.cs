using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

namespace PartingSurface.Detection
{
    /// <summary>
    /// Exception thrown by the review engine.
    /// Ported from Python review_engine.ReviewEngineError.
    /// </summary>
    public class ReviewEngineError : Exception
    {
        public ReviewEngineError(string message) : base(message) { }
    }

    /// <summary>
    /// Review engine that evaluates geometry evidence against rules.
    /// Ported from Python review_engine.ReviewEngine.
    /// All 7 rules: PL-001, PL-002, SS-GEO-001, SS-001/002/003, UC-001.
    /// Uses Dictionary&lt;string,object&gt; throughout.
    /// </summary>
    public class ReviewEngine
    {
        public const string REVIEW_SCHEMA_VERSION = "2.0";

        private static readonly Dictionary<string, string> STATUS_LABELS = new Dictionary<string, string>
        {
            { "passed", "通过" },
            { "conditional", "有条件通过，需工程确认" },
            { "not_passed", "不通过，存在确认几何风险" },
            { "stopped_incomplete", "达到最大轮次，仍未闭环" },
        };

        private Dictionary<string, object> _rulesDocument;
        private Dictionary<string, Dictionary<string, object>> _rules;
        private Dictionary<string, object> _policy;
        private string _rulesPath;

        /// <summary>
        /// Create a ReviewEngine, loading rules from the given path or the default location.
        /// Ported from ReviewEngine.__init__.
        /// </summary>
        public ReviewEngine(string rulesPath = null)
        {
            _rulesPath = rulesPath ?? FindRulesPath();
            try
            {
                string json = File.ReadAllText(_rulesPath, Encoding.UTF8);
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                _rulesDocument = serializer.Deserialize<Dictionary<string, object>>(json);
            }
            catch (Exception error)
            {
                throw new ReviewEngineError("无法读取审查规则: " + error.Message);
            }

            _rules = new Dictionary<string, Dictionary<string, object>>();
            object rulesObj;
            if (_rulesDocument.TryGetValue("rules", out rulesObj) && rulesObj is List<object>)
            {
                foreach (object ruleObj in (List<object>)rulesObj)
                {
                    Dictionary<string, object> rule = ruleObj as Dictionary<string, object>;
                    if (rule != null && rule.ContainsKey("id"))
                    {
                        _rules[rule["id"].ToString()] = rule;
                    }
                }
            }

            object policyObj;
            _policy = new Dictionary<string, object>();
            if (_rulesDocument.TryGetValue("sharp_steel_candidate_policy", out policyObj) && policyObj is Dictionary<string, object>)
            {
                _policy = (Dictionary<string, object>)policyObj;
            }
        }

        /// <summary>
        /// Review the given evidence and produce a review result.
        /// Ported from ReviewEngine.review().
        /// </summary>
        public Dictionary<string, object> Review(
            Dictionary<string, object> evidence,
            Dictionary<string, object> previousReview = null,
            int roundNumber = 1,
            int maxRounds = 5)
        {
            ValidateEvidence(evidence);
            if (roundNumber < 1 || maxRounds < 1)
            {
                throw new ReviewEngineError("round_number 和 max_rounds 必须大于 0");
            }

            List<Dictionary<string, object>> ruleResults = EvaluateRules(evidence);
            List<Dictionary<string, object>> issues = BuildGeometryIssues(evidence);

            KeyValuePair<List<Dictionary<string, object>>, Dictionary<string, object>> tracked =
                TrackChanges(issues, previousReview, GetSourceSha256(evidence));
            issues = tracked.Key;
            Dictionary<string, object> changeTracking = tracked.Value;

            Dictionary<string, object> counts = BuildCounts(ruleResults, issues);
            string status = DetermineStatus(counts);
            if (roundNumber >= maxRounds && status != "passed")
            {
                status = "stopped_incomplete";
            }

            Dictionary<string, object> source = JsonHelper.GetAsDict(evidence, "meta") ?? new Dictionary<string, object>();
            string reviewId = string.Format(CultureInfo.InvariantCulture,
                "REV-{0}-{1:00}",
                DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
                roundNumber);

            Dictionary<string, object> result = new Dictionary<string, object>
            {
                { "schema_version", REVIEW_SCHEMA_VERSION },
                { "review_id", reviewId },
                { "review_time_utc", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffK", CultureInfo.InvariantCulture) },
                { "round_number", roundNumber },
                { "max_rounds", maxRounds },
                { "status", status },
                { "conclusion", STATUS_LABELS[status] },
                { "source", new Dictionary<string, object>
                    {
                        { "file_name", JsonHelper.GetAsString(source, "file_name") },
                        { "full_path", JsonHelper.GetAsString(source, "full_path") },
                        { "source_sha256", JsonHelper.GetAsString(source, "source_sha256") },
                        { "analysis_mode", JsonHelper.GetAsString(source, "analysis_mode") },
                        { "source_modified_by_workflow", false },
                    }
                },
                { "geometry_summary", new Dictionary<string, object>
                    {
                        { "body_count", JsonHelper.GetAsList(evidence, "bodies")?.Count ?? 0 },
                        { "parting_surface", JsonHelper.GetAsDict(evidence, "parting_surface") ?? new Dictionary<string, object>() },
                        { "sharp_steel_summary", JsonHelper.GetAsDict(evidence, "sharp_steel_summary") ?? new Dictionary<string, object>() },
                    }
                },
                { "rule_results", ruleResults },
                { "issues", issues },
                { "unavailable_checks", ruleResults.Where(r => JsonHelper.GetAsString(r, "status") == "UNAVAILABLE").Cast<object>().ToList() },
                { "change_tracking", changeTracking },
                { "counts", counts },
                { "repair_plan", BuildRepairPlan(issues) },
                { "next_action", BuildNextAction(status, roundNumber, maxRounds) },
                { "limitations", JsonHelper.GetAsList(evidence, "limitations") ?? new List<object>() },
            };

            return result;
        }

        /// <summary>
        /// Render the review result as a Markdown report.
        /// Ported from ReviewEngine.render_markdown().
        /// </summary>
        public string RenderMarkdown(Dictionary<string, object> review)
        {
            List<string> lines = new List<string>
            {
                "# 分型面尖钢审查报告",
                "",
                "- Review ID：`" + JsonHelper.GetAsString(review, "review_id") + "`",
                "- 审查轮次：" + JsonHelper.GetAsInt(review, "round_number") + " / " + JsonHelper.GetAsInt(review, "max_rounds"),
                "- 总体结论：**" + JsonHelper.GetAsString(review, "conclusion") + "**",
                "- 源文件哈希：`" + (JsonHelper.GetAsString(JsonHelper.GetAsDict(review, "source"), "source_sha256") ?? "unavailable") + "`",
                "- 源文件状态：未被工作流修改",
                "",
                "## 确定性几何摘要",
                "",
            };

            Dictionary<string, object> summary = JsonHelper.GetAsDict(
                JsonHelper.GetAsDict(review, "geometry_summary"), "sharp_steel_summary") ?? new Dictionary<string, object>();

            lines.AddRange(new[]
            {
                "- 原始候选：" + (JsonHelper.GetAsInt(summary, "raw_candidate_count") ?? 0),
                "- 聚类候选：" + (JsonHelper.GetAsInt(summary, "clustered_candidate_count") ?? 0),
                "- 确认几何风险：" + (JsonHelper.GetAsInt(summary, "confirmed_geometry_risk_count") ?? 0),
                "- 报告候选：" + (JsonHelper.GetAsInt(summary, "reported_candidate_count") ?? 0) +
                    "，省略：" + (JsonHelper.GetAsInt(summary, "omitted_candidate_count") ?? 0),
                "",
                "> 边长与曲线最小半径只用于拓扑筛查，不等同于钢厚或真实钢料圆角。",
                "",
                "## 规则审查",
                "",
                "| 规则 | 状态 | 量测/依据 | 结论 |",
                "|---|---|---|---|",
            });

            List<object> ruleResults = JsonHelper.GetAsList(review, "rule_results") ?? new List<object>();
            foreach (object ruleObj in ruleResults)
            {
                Dictionary<string, object> item = ruleObj as Dictionary<string, object>;
                if (item == null) continue;
                lines.Add(string.Format(CultureInfo.InvariantCulture,
                    "| {0} {1} | {2} | {3} | {4} |",
                    JsonHelper.GetAsString(item, "rule_id"),
                    MarkdownCell(JsonHelper.GetAsString(item, "name")),
                    JsonHelper.GetAsString(item, "status"),
                    MarkdownCell(JsonHelper.GetAsString(item, "evidence")),
                    MarkdownCell(JsonHelper.GetAsString(item, "message"))));
            }

            lines.AddRange(new[] { "", "## 问题与建议", "" });
            List<object> issuesList = JsonHelper.GetAsList(review, "issues") ?? new List<object>();
            if (issuesList.Count == 0)
            {
                lines.Add("未发现达到当前确定性门槛的尖钢几何候选。");
            }
            foreach (object issueObj in issuesList)
            {
                Dictionary<string, object> issue = issueObj as Dictionary<string, object>;
                if (issue == null) continue;
                Dictionary<string, object> coordinate = JsonHelper.GetAsDict(issue, "coordinate_approx");
                Dictionary<string, object> measurements = JsonHelper.GetAsDict(issue, "measurements");
                List<object> recommendations = JsonHelper.GetAsList(issue, "recommendations");
                List<object> verificationCriteria = JsonHelper.GetAsList(issue, "verification_criteria");

                lines.AddRange(new[]
                {
                    string.Format(CultureInfo.InvariantCulture,
                        "### {0} [{1}] {2}",
                        JsonHelper.GetAsString(issue, "issue_id"),
                        JsonHelper.GetAsString(issue, "severity"),
                        JsonHelper.GetAsString(issue, "title")),
                    "",
                    string.Format(CultureInfo.InvariantCulture,
                        "- 坐标：X={0:F3}, Y={1:F3}, Z={2:F3} mm",
                        GetCoord(coordinate, "x"), GetCoord(coordinate, "y"), GetCoord(coordinate, "z")),
                    string.Format(CultureInfo.InvariantCulture,
                        "- 几何量测：边长 {0:F6} mm，面夹角 {1:F3}°，窄面最小包围盒尺寸 {2:F6} mm",
                        GetMeasurement(measurements, "representative_edge_length_mm"),
                        GetMeasurement(measurements, "wedge_angle_deg"),
                        GetMeasurement(measurements, "min_narrow_face_dimension_mm")),
                    "- 产品距离：" + Number(GetMeasurementNullable(measurements, "distance_to_product_mm"), 6) + " mm",
                    "- 工程判断：" + JsonHelper.GetAsString(issue, "engineering_assessment"),
                    "- 方案 A：" + GetRecommendationInstruction(recommendations, 0),
                    "- 方案 B：" + GetRecommendationInstruction(recommendations, 1),
                    "- 验证标准：" + string.Join("；", verificationCriteria.Select(v => v.ToString())),
                    "",
                });
            }

            lines.AddRange(new[] { "## Loop 追踪", "" });
            Dictionary<string, object> tracking = JsonHelper.GetAsDict(review, "change_tracking") ?? new Dictionary<string, object>();
            lines.AddRange(new[]
            {
                "- 新增：" + JoinIds(JsonHelper.GetAsList(tracking, "new_issue_ids")),
                "- 改善：" + JoinIds(JsonHelper.GetAsList(tracking, "improved_issue_ids")),
                "- 未关闭：" + JoinIds(JsonHelper.GetAsList(tracking, "remaining_issue_ids")),
                "- 已关闭：" + JoinIds(JsonHelper.GetAsList(tracking, "closed_issue_ids")),
                "- 几何是否变化：" + (JsonHelper.GetAsBool(tracking, "same_source_hash") == true ? "否" : "是或首轮"),
                "",
                "## 数据边界",
                "",
            });

            List<object> unavailable = JsonHelper.GetAsList(review, "unavailable_checks") ?? new List<object>();
            if (unavailable.Count > 0)
            {
                foreach (object itemObj in unavailable)
                {
                    Dictionary<string, object> item = itemObj as Dictionary<string, object>;
                    if (item == null) continue;
                    lines.Add("- " + JsonHelper.GetAsString(item, "rule_id") + "：" + JsonHelper.GetAsString(item, "message"));
                }
            }
            else
            {
                lines.Add("- 本轮规则所需量测均可用。");
            }
            List<object> limitations = JsonHelper.GetAsList(review, "limitations") ?? new List<object>();
            foreach (object limitation in limitations)
            {
                lines.Add("- " + limitation);
            }

            lines.AddRange(new[]
            {
                "",
                "## 下一步",
                "",
                JsonHelper.GetAsString(JsonHelper.GetAsDict(review, "next_action"), "message"),
                "",
            });

            return string.Join("\n", lines);
        }

        // --- Private methods ---

        private void ValidateEvidence(Dictionary<string, object> evidence)
        {
            if (JsonHelper.GetAsString(evidence, "schema_version") != "2.0")
            {
                throw new ReviewEngineError("仅支持 geometry evidence schema_version 2.0");
            }
            Dictionary<string, object> metadata = JsonHelper.GetAsDict(evidence, "meta");
            if (metadata == null || string.IsNullOrEmpty(JsonHelper.GetAsString(metadata, "source_sha256")))
            {
                throw new ReviewEngineError("几何证据缺少源文件 SHA-256");
            }
        }

        private List<Dictionary<string, object>> EvaluateRules(Dictionary<string, object> evidence)
        {
            return new List<Dictionary<string, object>>
            {
                PartingSurfaceComplexity(evidence),
                MaximumContour(evidence),
                GeometryRiskRule(evidence),
                NumericPerItemRule(evidence, "SS-001", "thickness_mm"),
                NumericPerItemRule(evidence, "SS-002", "aspect_ratio"),
                NumericPerItemRule(evidence, "SS-003", "edge_radius_mm"),
                UndercutRule(evidence),
            };
        }

        private Dictionary<string, object> PartingSurfaceComplexity(Dictionary<string, object> evidence)
        {
            Dictionary<string, object> rule = Rule("PL-001", "分型面复杂度");
            Dictionary<string, object> surface = JsonHelper.GetAsDict(evidence, "parting_surface") ?? new Dictionary<string, object>();
            if (JsonHelper.GetAsString(surface, "measurement_status") == "unavailable")
            {
                return Result(rule, "UNAVAILABLE", "未识别到独立分型面 Sheet Body", "无法审查");
            }
            double? flatness = JsonHelper.GetAsDouble(surface, "flatness_score");
            if (JsonHelper.GetAsBool(surface, "is_planar") == true)
            {
                return Result(rule, "PASS", "全部分型面均为平面", "满足平面规则");
            }
            return Result(rule, "WARN",
                string.Format(CultureInfo.InvariantCulture, "平面占比评分 {0}/10，面数 {1}",
                    Number(flatness, 1), JsonHelper.GetAsInt(surface, "face_count")),
                "分型面为复杂曲面，需确认必要性、加工与配模风险");
        }

        private Dictionary<string, object> MaximumContour(Dictionary<string, object> evidence)
        {
            Dictionary<string, object> rule = Rule("PL-002", "最大轮廓分型位置");
            Dictionary<string, object> partingLine = JsonHelper.GetAsDict(evidence, "parting_line") ?? new Dictionary<string, object>();
            object valueObj;
            bool? value = null;
            if (partingLine.TryGetValue("is_at_max_contour", out valueObj))
            {
                if (valueObj is bool) value = (bool)valueObj;
                else if (valueObj == null) value = null;
            }

            if (value == null)
            {
                return Result(rule, "UNAVAILABLE", "缺少开模方向与可见性分析", "不得推断分型面位于最大轮廓");
            }
            if (value.Value)
            {
                return Result(rule, "PASS", "is_at_max_contour=true", "满足规则");
            }
            return Result(rule, "ERROR", "is_at_max_contour=false", "存在倒扣或尖钢形成风险");
        }

        private Dictionary<string, object> GeometryRiskRule(Dictionary<string, object> evidence)
        {
            Dictionary<string, object> rule = Rule("SS-GEO-001", "尖钢几何候选");
            Dictionary<string, object> summary = JsonHelper.GetAsDict(evidence, "sharp_steel_summary") ?? new Dictionary<string, object>();
            int confirmed = JsonHelper.GetAsInt(summary, "confirmed_geometry_risk_count") ?? 0;
            int candidates = JsonHelper.GetAsInt(summary, "candidate_count") ?? 0;
            string detail = string.Format(CultureInfo.InvariantCulture, "确认风险 {0}，待确认候选 {1}", confirmed, candidates);
            if (confirmed > 0)
            {
                return Result(rule, "ERROR", detail, "存在满足三重几何门槛的尖钢风险");
            }
            if (candidates > 0)
            {
                return Result(rule, "WARN", detail, "需由模具工程师确认局部钢料关系");
            }
            return Result(rule, "PASS", detail, "未命中当前尖钢候选门槛");
        }

        private Dictionary<string, object> NumericPerItemRule(Dictionary<string, object> evidence, string ruleId, string field)
        {
            Dictionary<string, object> rule = Rule(ruleId, ruleId);
            List<Dictionary<string, object>> sharpSteels = JsonHelper.GetAsDictList(evidence, "sharp_steels") ?? new List<Dictionary<string, object>>();
            List<double> values = new List<double>();
            foreach (Dictionary<string, object> item in sharpSteels)
            {
                double? value = JsonHelper.GetAsDouble(item, field);
                if (value != null) values.Add(value.Value);
            }

            if (values.Count == 0)
            {
                return Result(rule, "UNAVAILABLE",
                    field + " 无可信量测",
                    "缺少独立型腔/型芯钢料实体，不得以边长代替");
            }

            string op = JsonHelper.GetAsString(rule, "operator");
            double? thresholdNullable = JsonHelper.GetAsDouble(rule, "threshold");
            double threshold = thresholdNullable ?? 0;
            List<double> failures = values.Where(v => !Compare(v, op, threshold)).ToList();

            if (failures.Count == 0)
            {
                return Result(rule, "PASS",
                    string.Format(CultureInfo.InvariantCulture, "{0} 个量测均满足阈值 {1}", values.Count, FormatThreshold(thresholdNullable)),
                    "满足规则");
            }

            string status = JsonHelper.GetAsString(rule, "severity") == "ERROR" ? "ERROR" : "WARN";
            return Result(rule, status,
                string.Format(CultureInfo.InvariantCulture, "{0} 个不合格，最不利值 {1}", failures.Count, Number(Worst(failures, op), 6)),
                JsonHelper.GetAsString(rule, "consequence") ?? "不满足规则");
        }

        private Dictionary<string, object> UndercutRule(Dictionary<string, object> evidence)
        {
            Dictionary<string, object> rule = Rule("UC-001", "倒扣检测");
            Dictionary<string, object> analysis = JsonHelper.GetAsDict(evidence, "undercut_analysis") ?? new Dictionary<string, object>();
            if (JsonHelper.GetAsString(analysis, "status") == "unavailable")
            {
                return Result(rule, "UNAVAILABLE",
                    JsonHelper.GetAsString(analysis, "reason") ?? "缺少开模方向",
                    "不得声明无倒扣");
            }
            List<object> undercuts = JsonHelper.GetAsList(evidence, "undercuts") ?? new List<object>();
            if (undercuts.Count > 0)
            {
                return Result(rule, "ERROR", undercuts.Count + " 个倒扣", "阻碍脱模");
            }
            return Result(rule, "PASS", "0 个倒扣", "满足规则");
        }

        private List<Dictionary<string, object>> BuildGeometryIssues(Dictionary<string, object> evidence)
        {
            List<Dictionary<string, object>> issues = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> sharpSteels = JsonHelper.GetAsDictList(evidence, "sharp_steels") ?? new List<Dictionary<string, object>>();

            foreach (Dictionary<string, object> risk in sharpSteels)
            {
                string classification = JsonHelper.GetAsString(risk, "classification");
                if (classification != "confirmed_geometry_risk" && classification != "candidate")
                {
                    continue;
                }

                string severity = classification == "confirmed_geometry_risk" ? "ERROR" : "WARN";
                Dictionary<string, object> coordinate = JsonHelper.GetAsDict(risk, "coordinate_approx");
                string issueId = "ISS-" + (JsonHelper.GetAsString(risk, "fingerprint") ?? "").ToUpperInvariant();

                string assessment = severity == "ERROR"
                    ? "确定性算法已确认微小边、可测夹角、窄非平面面且贴近产品，必须修改或提供反证。"
                    : "确定性算法命中候选门槛；当前没有钢料实体，需工程师确认该处是否形成真实尖钢。";

                Dictionary<string, object> issue = new Dictionary<string, object>
                {
                    { "issue_id", issueId },
                    { "fingerprint", JsonHelper.GetAsString(risk, "fingerprint") },
                    { "severity", severity },
                    { "classification", classification },
                    { "title", "分型面局部尖钢几何风险" },
                    { "coordinate_approx", JsonHelper.DeepClone(coordinate) },
                    { "measurements", new Dictionary<string, object>
                        {
                            { "representative_edge_length_mm", risk.ContainsKey("representative_edge_length_mm") ? risk["representative_edge_length_mm"] : null },
                            { "wedge_angle_deg", risk.ContainsKey("wedge_angle_deg") ? risk["wedge_angle_deg"] : null },
                            { "min_narrow_face_dimension_mm", risk.ContainsKey("min_narrow_face_dimension_mm") ? risk["min_narrow_face_dimension_mm"] : null },
                            { "distance_to_product_mm", risk.ContainsKey("distance_to_product_mm") ? risk["distance_to_product_mm"] : null },
                            { "thickness_mm", risk.ContainsKey("thickness_mm") ? risk["thickness_mm"] : null },
                            { "height_mm", risk.ContainsKey("height_mm") ? risk["height_mm"] : null },
                            { "aspect_ratio", risk.ContainsKey("aspect_ratio") ? risk["aspect_ratio"] : null },
                            { "true_edge_radius_mm", risk.ContainsKey("edge_radius_mm") ? risk["edge_radius_mm"] : null },
                            { "curve_min_radius_mm", risk.ContainsKey("curve_min_radius_mm") ? risk["curve_min_radius_mm"] : null },
                        }
                    },
                    { "geometry_evidence", JsonHelper.DeepClone(JsonHelper.GetAsDict(risk, "evidence") ?? new Dictionary<string, object>()) },
                    { "engineering_assessment", assessment },
                    { "recommendations", Recommendations(risk) },
                    { "verification_criteria", VerificationCriteria(risk) },
                };
                issues.Add(issue);
            }
            return issues;
        }

        private List<object> Recommendations(Dictionary<string, object> risk)
        {
            Dictionary<string, object> coordinate = JsonHelper.GetAsDict(risk, "coordinate_approx");
            string location = string.Format(CultureInfo.InvariantCulture,
                "X={0:F3}, Y={1:F3}, Z={2:F3} mm",
                GetCoord(coordinate, "x"), GetCoord(coordinate, "y"), GetCoord(coordinate, "z"));

            double clusterRadius = JsonHelper.GetAsDouble(_policy, "cluster_radius_mm") ?? 2.0;

            return new List<object>
            {
                new Dictionary<string, object>
                {
                    { "priority", "A" },
                    { "type", "parting_surface_local_rework" },
                    { "instruction", string.Format(CultureInfo.InvariantCulture,
                        "在 {0} 周围优先重构或平顺分型面，消除微小边和狭窄碎面；" +
                        "修改量必须由相邻产品面与钢料实体重新量测后确定，禁止凭当前边长推算。", location) },
                    { "parameters", new Dictionary<string, object>
                        {
                            { "inspection_radius_mm", clusterRadius },
                            { "target_min_steel_thickness_mm", 2.0 },
                            { "target_max_aspect_ratio", 3.0 },
                            { "target_min_true_radius_mm", 0.5 },
                        }
                    },
                },
                new Dictionary<string, object>
                {
                    { "priority", "B" },
                    { "type", "local_insert" },
                    { "instruction",
                        "若产品功能不允许移动分型面，在该坐标建立独立镶件；" +
                        "镶件外形、锁固和材料必须结合真实钢料实体、寿命和冷却重新设计。" },
                    { "parameters", new Dictionary<string, object>
                        {
                            { "target_min_insert_ligament_mm", 2.5 },
                            { "target_min_true_radius_mm", 0.5 },
                            { "fit_and_material", "需模具工程师按寿命与加工能力确认" },
                        }
                    },
                },
            };
        }

        private List<object> VerificationCriteria(Dictionary<string, object> risk)
        {
            double clusterRadius = JsonHelper.GetAsDouble(_policy, "cluster_radius_mm") ?? 2.0;
            List<object> criteria = new List<object>
            {
                string.Format(CultureInfo.InvariantCulture,
                    "复跑后该坐标 {0} mm 聚类范围内不再出现 confirmed_geometry_risk", clusterRadius),
                "有型腔/型芯钢料实体时，真实最小钢厚 ≥ 2.0 mm",
                "真实钢料高度/厚度 ≤ 3.0",
                "真实应力集中圆角 ≥ 0.5 mm",
            };
            if (JsonHelper.GetAsString(risk, "classification") == "candidate")
            {
                criteria.Insert(0, "人工确认该局部是否构成承压尖钢，并记录依据");
            }
            return criteria;
        }

        private KeyValuePair<List<Dictionary<string, object>>, Dictionary<string, object>> TrackChanges(
            List<Dictionary<string, object>> currentIssues,
            Dictionary<string, object> previousReview,
            string currentSourceHash)
        {
            if (previousReview == null)
            {
                List<object> identifiers = currentIssues.Select(i => (object)JsonHelper.GetAsString(i, "issue_id")).ToList();
                return new KeyValuePair<List<Dictionary<string, object>>, Dictionary<string, object>>(
                    currentIssues,
                    new Dictionary<string, object>
                    {
                        { "same_source_hash", false },
                        { "new_issue_ids", identifiers },
                        { "improved_issue_ids", new List<object>() },
                        { "remaining_issue_ids", new List<object>(identifiers) },
                        { "closed_issue_ids", new List<object>() },
                        { "regressed_issue_ids", new List<object>() },
                        { "comparisons", new List<object>() },
                    });
            }

            List<Dictionary<string, object>> previousIssues =
                JsonHelper.GetAsDictList(previousReview, "issues") ?? new List<Dictionary<string, object>>();
            HashSet<int> unmatched = new HashSet<int>();
            for (int i = 0; i < currentIssues.Count; i++) unmatched.Add(i);

            List<object> comparisons = new List<object>();
            List<object> newIds = new List<object>();
            List<object> improvedIds = new List<object>();
            List<object> remainingIds = new List<object>();
            List<object> closedIds = new List<object>();
            List<object> regressedIds = new List<object>();

            foreach (Dictionary<string, object> previous in previousIssues)
            {
                int? matchIndex = FindIssueMatch(previous, currentIssues, unmatched);
                if (matchIndex == null)
                {
                    closedIds.Add(JsonHelper.GetAsString(previous, "issue_id"));
                    comparisons.Add(new Dictionary<string, object>
                    {
                        { "issue_id", JsonHelper.GetAsString(previous, "issue_id") },
                        { "previous", JsonHelper.GetAsString(previous, "severity") },
                        { "current", "CLOSED" },
                        { "change", "closed" },
                    });
                    continue;
                }

                int idx = matchIndex.Value;
                unmatched.Remove(idx);
                Dictionary<string, object> current = currentIssues[idx];
                current["issue_id"] = JsonHelper.GetAsString(previous, "issue_id");

                int currentRank = SeverityRank(JsonHelper.GetAsString(current, "severity"));
                int previousRank = SeverityRank(JsonHelper.GetAsString(previous, "severity"));

                string change;
                if (currentRank < previousRank)
                {
                    change = "improved";
                    improvedIds.Add(JsonHelper.GetAsString(current, "issue_id"));
                }
                else if (currentRank > previousRank)
                {
                    change = "regressed";
                    regressedIds.Add(JsonHelper.GetAsString(current, "issue_id"));
                }
                else
                {
                    change = "remaining";
                }
                remainingIds.Add(JsonHelper.GetAsString(current, "issue_id"));

                Dictionary<string, object> comparison = new Dictionary<string, object>
                {
                    { "issue_id", JsonHelper.GetAsString(current, "issue_id") },
                    { "previous", JsonHelper.GetAsString(previous, "severity") },
                    { "current", JsonHelper.GetAsString(current, "severity") },
                    { "change", change },
                    { "coordinate_shift_mm", CoordinateDistance(
                        JsonHelper.GetAsDict(previous, "coordinate_approx"),
                        JsonHelper.GetAsDict(current, "coordinate_approx")) },
                };
                comparisons.Add(comparison);
            }

            // Process unmatched current issues as new
            foreach (int index in unmatched.OrderBy(i => i))
            {
                Dictionary<string, object> issue = currentIssues[index];
                newIds.Add(JsonHelper.GetAsString(issue, "issue_id"));
                remainingIds.Add(JsonHelper.GetAsString(issue, "issue_id"));
                comparisons.Add(new Dictionary<string, object>
                {
                    { "issue_id", JsonHelper.GetAsString(issue, "issue_id") },
                    { "previous", null },
                    { "current", JsonHelper.GetAsString(issue, "severity") },
                    { "change", "new" },
                });
            }

            string previousHash = JsonHelper.GetAsString(JsonHelper.GetAsDict(previousReview, "source"), "source_sha256");
            bool sameHash = previousHash == currentSourceHash;

            return new KeyValuePair<List<Dictionary<string, object>>, Dictionary<string, object>>(
                currentIssues,
                new Dictionary<string, object>
                {
                    { "same_source_hash", sameHash },
                    { "new_issue_ids", newIds },
                    { "improved_issue_ids", improvedIds },
                    { "remaining_issue_ids", remainingIds },
                    { "closed_issue_ids", closedIds },
                    { "regressed_issue_ids", regressedIds },
                    { "comparisons", comparisons },
                });
        }

        private Dictionary<string, object> BuildCounts(List<Dictionary<string, object>> ruleResults, List<Dictionary<string, object>> issues)
        {
            return new Dictionary<string, object>
            {
                { "rule_error", ruleResults.Count(r => JsonHelper.GetAsString(r, "status") == "ERROR") },
                { "rule_warn", ruleResults.Count(r => JsonHelper.GetAsString(r, "status") == "WARN") },
                { "rule_unavailable", ruleResults.Count(r => JsonHelper.GetAsString(r, "status") == "UNAVAILABLE") },
                { "issue_error", issues.Count(i => JsonHelper.GetAsString(i, "severity") == "ERROR") },
                { "issue_warn", issues.Count(i => JsonHelper.GetAsString(i, "severity") == "WARN") },
            };
        }

        private string DetermineStatus(Dictionary<string, object> counts)
        {
            int issueError = JsonHelper.GetAsInt(counts, "issue_error") ?? 0;
            int ruleError = JsonHelper.GetAsInt(counts, "rule_error") ?? 0;
            int issueWarn = JsonHelper.GetAsInt(counts, "issue_warn") ?? 0;
            int ruleWarn = JsonHelper.GetAsInt(counts, "rule_warn") ?? 0;
            int ruleUnavailable = JsonHelper.GetAsInt(counts, "rule_unavailable") ?? 0;

            if (issueError > 0 || ruleError > 0) return "not_passed";
            if (issueWarn > 0 || ruleWarn > 0 || ruleUnavailable > 0) return "conditional";
            return "passed";
        }

        private Dictionary<string, object> BuildRepairPlan(List<Dictionary<string, object>> issues)
        {
            List<object> operations = new List<object>();
            foreach (Dictionary<string, object> issue in issues)
            {
                List<object> recommendations = JsonHelper.GetAsList(issue, "recommendations") ?? new List<object>();
                operations.Add(new Dictionary<string, object>
                {
                    { "issue_id", JsonHelper.GetAsString(issue, "issue_id") },
                    { "coordinate_approx", JsonHelper.DeepClone(JsonHelper.GetAsDict(issue, "coordinate_approx")) },
                    { "preferred_action", recommendations.Count > 0 ? recommendations[0] : null },
                    { "fallback_action", recommendations.Count > 1 ? recommendations[1] : null },
                });
            }

            bool hasIssues = issues.Count > 0;
            return new Dictionary<string, object>
            {
                { "status", hasIssues ? "manual_or_licensed_nx_modifier_required" : "not_required" },
                { "source_file_must_remain_unchanged", true },
                { "may_prepare_working_copy", hasIssues },
                { "may_label_output_as_modified", false },
                { "reason", hasIssues
                    ? "当前工作流只生成量测证据和修改计划；合法 NX Headless 修改成功并复审后，才可交付修改版 .prt。"
                    : "未生成修改任务。" },
                { "operations", operations },
            };
        }

        private Dictionary<string, object> BuildNextAction(string status, int roundNumber, int maxRounds)
        {
            if (status == "passed")
            {
                return new Dictionary<string, object>
                {
                    { "action", "finish" },
                    { "message", "审查闭环，可输出最终报告。" },
                };
            }
            if (status == "stopped_incomplete")
            {
                return new Dictionary<string, object>
                {
                    { "action", "human_review" },
                    { "message", "已达到最大轮次，停止自动 Loop，遗留项转人工模具评审。" },
                };
            }
            return new Dictionary<string, object>
            {
                { "action", "modify_copy_and_recheck" },
                { "message", "仅修改工作副本；完成后把新 .prt 作为同一 Session 的下一轮输入，工作流会自动匹配、关闭或升级问题。" },
            };
        }

        private Dictionary<string, object> Rule(string ruleId, string fallbackName)
        {
            Dictionary<string, object> rule;
            if (_rules.TryGetValue(ruleId, out rule) && rule != null)
            {
                rule = (Dictionary<string, object>)JsonHelper.DeepClone(rule);
            }
            else
            {
                rule = new Dictionary<string, object>();
            }
            if (!rule.ContainsKey("id")) rule["id"] = ruleId;
            if (!rule.ContainsKey("name")) rule["name"] = fallbackName;
            return rule;
        }

        private static Dictionary<string, object> Result(Dictionary<string, object> rule, string status, string evidence, string message)
        {
            return new Dictionary<string, object>
            {
                { "rule_id", rule["id"] },
                { "name", rule["name"] },
                { "status", status },
                { "configured_severity", rule.ContainsKey("severity") ? rule["severity"] : null },
                { "evidence", evidence },
                { "message", message },
                { "consequence", rule.ContainsKey("consequence") ? rule["consequence"] : null },
            };
        }

        // --- Module-level helper functions ---

        private static bool Compare(double value, string op, double threshold)
        {
            switch (op)
            {
                case ">=": return value >= threshold;
                case "<=": return value <= threshold;
                case "==": return Math.Abs(value - threshold) < 1e-10;
                default:
                    throw new ReviewEngineError("不支持的规则操作符: " + op);
            }
        }

        private static double Worst(List<double> values, string op)
        {
            return op == ">=" ? values.Min() : values.Max();
        }

        private static int? FindIssueMatch(
            Dictionary<string, object> previous,
            List<Dictionary<string, object>> currentIssues,
            HashSet<int> unmatched)
        {
            string previousFingerprint = JsonHelper.GetAsString(previous, "fingerprint");
            foreach (int index in unmatched)
            {
                if (!string.IsNullOrEmpty(previousFingerprint) &&
                    JsonHelper.GetAsString(currentIssues[index], "fingerprint") == previousFingerprint)
                {
                    return index;
                }
            }

            Dictionary<string, object> previousCoordinate = JsonHelper.GetAsDict(previous, "coordinate_approx");
            int? nearestIndex = null;
            double nearestDistance = double.MaxValue;
            foreach (int index in unmatched)
            {
                double? distance = CoordinateDistance(
                    previousCoordinate,
                    JsonHelper.GetAsDict(currentIssues[index], "coordinate_approx"));
                if (distance != null && distance.Value <= 3.0 && distance.Value < nearestDistance)
                {
                    nearestIndex = index;
                    nearestDistance = distance.Value;
                }
            }
            return nearestIndex;
        }

        private static double? CoordinateDistance(Dictionary<string, object> first, Dictionary<string, object> second)
        {
            if (first == null || second == null) return null;
            double? fx = JsonHelper.GetAsDouble(first, "x");
            double? fy = JsonHelper.GetAsDouble(first, "y");
            double? fz = JsonHelper.GetAsDouble(first, "z");
            double? sx = JsonHelper.GetAsDouble(second, "x");
            double? sy = JsonHelper.GetAsDouble(second, "y");
            double? sz = JsonHelper.GetAsDouble(second, "z");
            if (fx == null || fy == null || fz == null || sx == null || sy == null || sz == null) return null;
            double dx = fx.Value - sx.Value;
            double dy = fy.Value - sy.Value;
            double dz = fz.Value - sz.Value;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static int SeverityRank(string severity)
        {
            if (string.IsNullOrEmpty(severity)) return 0;
            if (severity == "ERROR") return 2;
            if (severity == "WARN") return 1;
            return 0;
        }

        private static string Number(double? value, int digits)
        {
            if (value == null) return "unavailable";
            return value.Value.ToString("F" + digits, CultureInfo.InvariantCulture);
        }

        private static string MarkdownCell(string value)
        {
            return (value ?? "").Replace("|", "\\|").Replace("\n", " ");
        }

        private static string JoinIds(List<object> values)
        {
            if (values == null || values.Count == 0) return "无";
            return string.Join(", ", values.Select(v => v.ToString()));
        }

        private static string GetSourceSha256(Dictionary<string, object> evidence)
        {
            return JsonHelper.GetAsString(JsonHelper.GetAsDict(evidence, "meta"), "source_sha256");
        }

        private static double GetCoord(Dictionary<string, object> coordinate, string axis)
        {
            return JsonHelper.GetAsDouble(coordinate, axis) ?? 0.0;
        }

        private static double GetMeasurement(Dictionary<string, object> measurements, string key)
        {
            return JsonHelper.GetAsDouble(measurements, key) ?? 0.0;
        }

        private static double? GetMeasurementNullable(Dictionary<string, object> measurements, string key)
        {
            return JsonHelper.GetAsDouble(measurements, key);
        }

        private static string GetRecommendationInstruction(List<object> recommendations, int index)
        {
            if (recommendations == null || index >= recommendations.Count) return "";
            Dictionary<string, object> rec = recommendations[index] as Dictionary<string, object>;
            if (rec == null) return "";
            return JsonHelper.GetAsString(rec, "instruction") ?? "";
        }

        private static string FormatThreshold(double? threshold)
        {
            if (threshold == null) return "null";
            return threshold.Value.ToString(CultureInfo.InvariantCulture);
        }

        private static string FindRulesPath()
        {
            List<string> candidates = new List<string>();
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
            candidates.Add(Path.Combine(Environment.CurrentDirectory, "review_rules.json"));
            candidates.Add(Path.Combine(Environment.CurrentDirectory, "rules", "review_rules.json"));
            candidates.Add(Path.Combine(Environment.CurrentDirectory, "skills", "parting-surface-review", "rules", "review_rules.json"));

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
    }
}
