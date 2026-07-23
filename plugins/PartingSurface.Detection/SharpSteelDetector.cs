using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PartingSurface.Detection
{
    /// <summary>
    /// Detects sharp steel candidates from edge/face geometry.
    /// Ported from Python geometry_pipeline._detect_sharp_steel_candidates and _cluster_candidates.
    /// </summary>
    public static class SharpSteelDetector
    {
        /// <summary>
        /// Default sharp steel candidate policy. Matches DEFAULT_SHARP_STEEL_POLICY in geometry_pipeline.py.
        /// </summary>
        public static Dictionary<string, object> DefaultPolicy()
        {
            return new Dictionary<string, object>
            {
                { "near_product_max_mm", 0.1 },
                { "valid_curve_radius_max_mm", 1000000.0 },
                { "narrow_nonplanar_face_max_mm", 1.0 },
                { "confirmed_max_edge_length_mm", 0.1 },
                { "confirmed_min_angle_deg", 1.0 },
                { "confirmed_max_angle_deg", 45.0 },
                { "candidate_max_edge_length_mm", 1.5 },
                { "candidate_min_angle_deg", 0.5 },
                { "candidate_max_angle_deg", 15.0 },
                { "cluster_radius_mm", 2.0 },
                { "max_reported_candidates", 30 },
            };
        }

        /// <summary>
        /// Detect sharp steel candidates from edges and faces.
        /// Ported from _detect_sharp_steel_candidates.
        /// Input: edges, faceByTag, policy.
        /// Output: Tuple of (reported candidates list, summary dict).
        /// </summary>
        public static Tuple<List<Dictionary<string, object>>, Dictionary<string, object>> Detect(
            List<Dictionary<string, object>> edges,
            Dictionary<int, Dictionary<string, object>> faceByTag,
            Dictionary<string, object> policy)
        {
            // Merge default policy with provided policy (equivalent to dict(DEFAULT_SHARP_STEEL_POLICY, **(policy or {})))
            Dictionary<string, object> mergedPolicy = DefaultPolicy();
            if (policy != null)
            {
                foreach (KeyValuePair<string, object> pair in policy)
                {
                    mergedPolicy[pair.Key] = pair.Value;
                }
            }

            List<Dictionary<string, object>> raw = new List<Dictionary<string, object>>();

            foreach (Dictionary<string, object> edge in edges)
            {
                double? length = GetDouble(edge, "edge_length_mm");
                double? angle = GetDouble(edge, "normal_angle_deg");
                Dictionary<string, object> midpoint = JsonHelper.GetAsDict(edge, "midpoint_mm");
                int adjacentCount = GetInt(edge, "adjacent_count", 0);

                if (length == null || angle == null || midpoint == null || adjacentCount < 2)
                {
                    continue;
                }

                int faceAId = GetInt(edge, "face_a", 0);
                int faceBId = GetInt(edge, "face_b", 0);

                Dictionary<string, object> faceA = null;
                Dictionary<string, object> faceB = null;
                faceByTag.TryGetValue(faceAId, out faceA);
                faceByTag.TryGetValue(faceBId, out faceB);

                // Collect distances to product from adjacent faces
                List<double> distances = new List<double>();
                if (faceA != null)
                {
                    double? d = GetDouble(faceA, "distance_to_product_mm");
                    if (d != null) distances.Add(d.Value);
                }
                if (faceB != null)
                {
                    double? d = GetDouble(faceB, "distance_to_product_mm");
                    if (d != null) distances.Add(d.Value);
                }

                double? nearDistance = distances.Count > 0 ? distances.Min() : (double?)null;
                double nearProductMaxMm = GetDouble(mergedPolicy, "near_product_max_mm").Value;
                bool nearProduct = nearDistance != null && nearDistance.Value <= nearProductMaxMm;

                // Check for narrow non-planar faces
                List<KeyValuePair<int, double>> narrowFaces = new List<KeyValuePair<int, double>>();
                foreach (Dictionary<string, object> face in new[] { faceA, faceB })
                {
                    if (face == null) continue;
                    string surfaceClass = JsonHelper.GetAsString(face, "surface_class");
                    if (surfaceClass != null && surfaceClass.ToLowerInvariant().Contains("plane"))
                    {
                        continue;
                    }
                    Dictionary<string, object> dimensions = JsonHelper.GetAsDict(face, "dimensions_mm");
                    if (dimensions == null) continue;
                    List<double> positiveDimensions = new List<double>();
                    foreach (object value in dimensions.Values)
                    {
                        double? d = ToDouble(value);
                        if (d != null && d.Value >= 0.0)
                        {
                            positiveDimensions.Add(d.Value);
                        }
                    }
                    if (positiveDimensions.Count > 0)
                    {
                        double minimumDimension = positiveDimensions.Min();
                        double narrowMaxMm = GetDouble(mergedPolicy, "narrow_nonplanar_face_max_mm").Value;
                        if (minimumDimension <= narrowMaxMm)
                        {
                            int faceTag = GetInt(face, "face_tag", 0);
                            narrowFaces.Add(new KeyValuePair<int, double>(faceTag, minimumDimension));
                        }
                    }
                }

                // Check confirmed and candidate thresholds
                double confirmedMaxLength = GetDouble(mergedPolicy, "confirmed_max_edge_length_mm").Value;
                double confirmedMinAngle = GetDouble(mergedPolicy, "confirmed_min_angle_deg").Value;
                double confirmedMaxAngle = GetDouble(mergedPolicy, "confirmed_max_angle_deg").Value;
                double candidateMaxLength = GetDouble(mergedPolicy, "candidate_max_edge_length_mm").Value;
                double candidateMinAngle = GetDouble(mergedPolicy, "candidate_min_angle_deg").Value;
                double candidateMaxAngle = GetDouble(mergedPolicy, "candidate_max_angle_deg").Value;

                bool confirmed = length.Value <= confirmedMaxLength
                    && confirmedMinAngle <= angle.Value && angle.Value <= confirmedMaxAngle;
                bool candidate = length.Value <= candidateMaxLength
                    && candidateMinAngle <= angle.Value && angle.Value <= candidateMaxAngle;

                if (!nearProduct || narrowFaces.Count == 0 || (!confirmed && !candidate))
                {
                    continue;
                }

                double? radius = GetDouble(edge, "curve_min_radius_mm");
                string classification = confirmed ? "confirmed_geometry_risk" : "candidate";
                int score = confirmed ? 100 : 60;
                List<object> reasons = new List<object> { "near_product", "narrow_nonplanar_face" };
                reasons.Add(length.Value <= 0.1 ? "micro_edge" : "short_edge");
                reasons.Add("measurable_face_angle");
                if (radius != null && radius.Value < 0.5)
                {
                    score += 5;
                    reasons.Add("small_curve_radius");
                }
                double confidence = confirmed ? 0.9 : 0.65;

                // Build face_tags list (non-zero tags)
                List<object> faceTags = new List<object>();
                if (faceAId != 0) faceTags.Add(faceAId);
                if (faceBId != 0) faceTags.Add(faceBId);

                // Build narrow_face_tags list
                List<object> narrowFaceTags = new List<object>();
                foreach (var nf in narrowFaces)
                {
                    narrowFaceTags.Add(nf.Key);
                }

                double minNarrowFaceDimension = narrowFaces.Min(nf => nf.Value);

                raw.Add(new Dictionary<string, object>
                {
                    { "score", score },
                    { "classification", classification },
                    { "confidence", Math.Round(confidence, 2) },
                    { "coordinate", JsonHelper.DeepClone(midpoint) },
                    { "edge_length_mm", length.Value },
                    { "normal_angle_deg", angle.Value },
                    { "curve_min_radius_mm", radius },
                    { "distance_to_product_mm", nearDistance },
                    { "edge_tags", new List<object> { GetInt(edge, "edge_tag", 0) } },
                    { "face_tags", faceTags },
                    { "narrow_face_tags", narrowFaceTags },
                    { "min_narrow_face_dimension_mm", minNarrowFaceDimension },
                    { "reasons", reasons },
                });
            }

            // Sort raw by (-score, edge_length_mm) and cluster
            raw.Sort((a, b) =>
            {
                int scoreA = (int)a["score"];
                int scoreB = (int)b["score"];
                if (scoreA != scoreB) return scoreB.CompareTo(scoreA); // descending score
                double lenA = GetDouble(a, "edge_length_mm").Value;
                double lenB = GetDouble(b, "edge_length_mm").Value;
                return lenA.CompareTo(lenB); // ascending length
            });

            double clusterRadiusMm = GetDouble(mergedPolicy, "cluster_radius_mm").Value;
            List<Dictionary<string, object>> clustered = ClusterCandidates(raw, clusterRadiusMm);

            // Build results
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> item in clustered)
            {
                Dictionary<string, object> coord = JsonHelper.GetAsDict(item, "coordinate");
                double cx = GetDouble(coord, "x").Value;
                double cy = GetDouble(coord, "y").Value;
                double cz = GetDouble(coord, "z").Value;
                string fingerprintText = string.Format(CultureInfo.InvariantCulture,
                    "{0:F1}|{1:F1}|{2:F1}", cx, cy, cz);
                string fingerprint = Sha1Hex(fingerprintText).Substring(0, 10);

                results.Add(new Dictionary<string, object>
                {
                    { "id", "SS-" + fingerprint.ToUpperInvariant() },
                    { "fingerprint", fingerprint },
                    { "classification", item["classification"] },
                    { "confidence", item["confidence"] },
                    { "severity", (string)item["classification"] == "confirmed_geometry_risk" ? "ERROR" : "WARN" },
                    { "position", "parting_surface_edge_cluster" },
                    { "coordinate_approx", JsonHelper.DeepClone(JsonHelper.GetAsDict(item, "coordinate")) },
                    { "measurement_mode", "surface_only" },
                    { "thickness_mm", null },
                    { "height_mm", null },
                    { "aspect_ratio", null },
                    { "edge_radius_mm", null },
                    { "curve_min_radius_mm", item["curve_min_radius_mm"] },
                    { "wedge_angle_deg", item["normal_angle_deg"] },
                    { "representative_edge_length_mm", item["edge_length_mm"] },
                    { "distance_to_product_mm", item["distance_to_product_mm"] },
                    { "min_narrow_face_dimension_mm", item["min_narrow_face_dimension_mm"] },
                    { "is_on_parting_line", true },
                    { "is_in_high_pressure_zone", null },
                    { "evidence", new Dictionary<string, object>
                        {
                            { "edge_tags", SortedUniqueInts(item, "edge_tags") },
                            { "face_tags", SortedUniqueInts(item, "face_tags") },
                            { "narrow_face_tags", SortedUniqueInts(item, "narrow_face_tags") },
                            { "reasons", SortedUniqueStrings(item, "reasons") },
                            { "note", "边长和曲线最小半径仅用于拓扑筛查，不作为钢厚或钢料圆角。" },
                        }
                    },
                });
            }

            // Sort results: confirmed_geometry_risk first, then by representative_edge_length_mm
            results.Sort((a, b) =>
            {
                int rankA = (string)a["classification"] == "confirmed_geometry_risk" ? 0 : 1;
                int rankB = (string)b["classification"] == "confirmed_geometry_risk" ? 0 : 1;
                if (rankA != rankB) return rankA.CompareTo(rankB);
                double lenA = GetDouble(a, "representative_edge_length_mm").Value;
                double lenB = GetDouble(b, "representative_edge_length_mm").Value;
                return lenA.CompareTo(lenB);
            });

            int maxReported = GetInt(mergedPolicy, "max_reported_candidates", 30);
            List<Dictionary<string, object>> reported = results.Count > maxReported
                ? results.GetRange(0, maxReported)
                : results;

            Dictionary<string, object> summary = new Dictionary<string, object>
            {
                { "raw_candidate_count", raw.Count },
                { "clustered_candidate_count", results.Count },
                { "reported_candidate_count", reported.Count },
                { "omitted_candidate_count", Math.Max(0, results.Count - reported.Count) },
                { "confirmed_geometry_risk_count", results.Count(r => (string)r["classification"] == "confirmed_geometry_risk") },
                { "candidate_count", results.Count(r => (string)r["classification"] == "candidate") },
                { "exact_thickness_count", 0 },
            };

            return Tuple.Create(reported, summary);
        }

        /// <summary>
        /// Cluster candidates by spatial proximity.
        /// Ported from _cluster_candidates.
        /// </summary>
        public static List<Dictionary<string, object>> ClusterCandidates(
            List<Dictionary<string, object>> candidates, double radiusMm)
        {
            List<Dictionary<string, object>> clusters = new List<Dictionary<string, object>>();

            foreach (Dictionary<string, object> candidate in candidates)
            {
                // Find first cluster within radius
                Dictionary<string, object> target = null;
                Dictionary<string, object> candidateCoord = JsonHelper.GetAsDict(candidate, "coordinate");
                foreach (Dictionary<string, object> cluster in clusters)
                {
                    Dictionary<string, object> clusterCoord = JsonHelper.GetAsDict(cluster, "coordinate");
                    if (PointDistance(clusterCoord, candidateCoord) <= radiusMm)
                    {
                        target = cluster;
                        break;
                    }
                }

                if (target == null)
                {
                    // Deep copy candidate as new cluster
                    clusters.Add((Dictionary<string, object>)JsonHelper.DeepClone(candidate));
                    continue;
                }

                // Extend lists
                ExtendList(target, candidate, "edge_tags");
                ExtendList(target, candidate, "face_tags");
                ExtendList(target, candidate, "narrow_face_tags");
                ExtendList(target, candidate, "reasons");

                // If candidate has higher score, update target fields
                int candidateScore = (int)candidate["score"];
                int targetScore = (int)target["score"];
                if (candidateScore > targetScore)
                {
                    string[] keysToUpdate = {
                        "score", "classification", "confidence", "coordinate",
                        "edge_length_mm", "normal_angle_deg", "curve_min_radius_mm",
                        "distance_to_product_mm", "min_narrow_face_dimension_mm"
                    };
                    foreach (string key in keysToUpdate)
                    {
                        target[key] = JsonHelper.DeepClone(candidate[key]);
                    }
                }
            }

            return clusters;
        }

        /// <summary>
        /// Calculate Euclidean distance between two coordinate dicts.
        /// Ported from _point_distance.
        /// </summary>
        public static double PointDistance(Dictionary<string, object> first, Dictionary<string, object> second)
        {
            double fx = GetDouble(first, "x").Value;
            double fy = GetDouble(first, "y").Value;
            double fz = GetDouble(first, "z").Value;
            double sx = GetDouble(second, "x").Value;
            double sy = GetDouble(second, "y").Value;
            double sz = GetDouble(second, "z").Value;
            double dx = fx - sx;
            double dy = fy - sy;
            double dz = fz - sz;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        // --- Helper methods ---

        private static double? GetDouble(Dictionary<string, object> dict, string key)
        {
            return JsonHelper.GetAsDouble(dict, key);
        }

        private static int GetInt(Dictionary<string, object> dict, string key, int defaultValue)
        {
            int? value = JsonHelper.GetAsInt(dict, key);
            return value ?? defaultValue;
        }

        private static double? ToDouble(object value)
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

        private static void ExtendList(Dictionary<string, object> target, Dictionary<string, object> source, string key)
        {
            List<object> targetList = JsonHelper.GetAsList(target, key);
            List<object> sourceList = JsonHelper.GetAsList(source, key);
            if (targetList != null && sourceList != null)
            {
                targetList.AddRange(sourceList);
            }
        }

        private static List<object> SortedUniqueInts(Dictionary<string, object> item, string key)
        {
            List<object> list = JsonHelper.GetAsList(item, key);
            if (list == null) return new List<object>();
            HashSet<int> seen = new HashSet<int>();
            foreach (object value in list)
            {
                int? v = ToInt(value);
                if (v != null) seen.Add(v.Value);
            }
            List<int> sorted = seen.ToList();
            sorted.Sort();
            return sorted.Cast<object>().ToList();
        }

        private static List<object> SortedUniqueStrings(Dictionary<string, object> item, string key)
        {
            List<object> list = JsonHelper.GetAsList(item, key);
            if (list == null) return new List<object>();
            HashSet<string> seen = new HashSet<string>();
            foreach (object value in list)
            {
                if (value != null) seen.Add(value.ToString());
            }
            List<string> sorted = seen.ToList();
            sorted.Sort(StringComparer.Ordinal);
            return sorted.Cast<object>().ToList();
        }

        private static int? ToInt(object value)
        {
            if (value == null) return null;
            if (value is int) return (int)value;
            if (value is long) return (int)(long)value;
            if (value is double) return (int)(double)value;
            int parsed;
            if (int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }
            return null;
        }

        private static string Sha1Hex(string text)
        {
            using (SHA1 sha1 = SHA1.Create())
            {
                byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(text));
                StringBuilder sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                {
                    sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                }
                return sb.ToString();
            }
        }
    }
}
