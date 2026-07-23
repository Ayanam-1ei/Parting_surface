using System;
using System.Collections.Generic;
using System.Linq;
using NXOpen;
using NXOpen.UF;

namespace PartingSurfaceReview.Analysis
{
    public class GeometryAnalyzer
    {
        private readonly Session _session;
        private readonly Part _workPart;
        private readonly UFSession _uf;

        // ===== Aligned with Python Parasolid pipeline thresholds =====
        private const double NearProductMaxMm = 0.1;
        private const double NarrowFaceMaxMm = 1.0;
        private const double ConfirmedMaxEdgeMm = 0.1;
        private const double ConfirmedMinAngle = 1.0;
        private const double ConfirmedMaxAngle = 45.0;
        private const double CandidateMaxEdgeMm = 1.5;
        private const double CandidateMinAngle = 0.5;
        private const double CandidateMaxAngle = 15.0;
        private const double ClusterRadiusMm = 2.0;
        private const int MaxReported = 30;

        public GeometryAnalyzer()
        {
            _session = Session.GetSession();
            _workPart = _session.Parts.Work;
            _uf = UFSession.GetUFSession();
        }

        public Body FindPartingSurface()
        {
            Body largestSheet = null;
            int maxFaces = 0;
            Body[] bodies = _workPart.Bodies.ToArray();
            foreach (Body body in bodies)
            {
                if (body.IsSolidBody) continue;
                int n = body.GetFaces().Length;
                if (n > maxFaces) { maxFaces = n; largestSheet = body; }
            }
            return largestSheet;
        }

        public Body FindProductSolid()
        {
            Body largest = null;
            int maxFaces = 0;
            Body[] bodies = _workPart.Bodies.ToArray();
            foreach (Body body in bodies)
            {
                if (!body.IsSolidBody) continue;
                int n = body.GetFaces().Length;
                if (n > maxFaces) { maxFaces = n; largest = body; }
            }
            return largest;
        }

        /// <summary>
        /// Analyze parting surface edges for sharp-steel risks.
        /// Now requires product solid for distance-to-product check (matching Python pipeline).
        /// </summary>
        public List<ReviewIssue> Analyze(Body partingSurface, Body productSolid)
        {
            var issues = new List<ReviewIssue>();
            if (partingSurface == null) return issues;

            Edge[] edges = partingSurface.GetEdges();
            var candidates = new List<Candidate>();

            foreach (Edge edge in edges)
            {
                try { Candidate? c = AnalyzeEdge(edge, productSolid); if (c.HasValue) candidates.Add(c.Value); }
                catch { }
            }

            var clustered = Cluster(candidates);
            int count = Math.Min(clustered.Count, MaxReported);
            for (int i = 0; i < count; i++)
                issues.Add(MakeIssue(clustered[i]));

            return issues;
        }

        private Candidate? AnalyzeEdge(Edge edge, Body productSolid)
        {
            double len = edge.GetLength();
            if (len > CandidateMaxEdgeMm) return null;

            Face[] adj = edge.GetFaces();
            if (adj.Length < 2) return null;
            Face fa = adj[0], fb = adj[1];

            // Edge midpoint
            Point3d v1, v2;
            edge.GetVertices(out v1, out v2);
            double[] pt = new double[] {
                (v1.X + v2.X) / 2.0,
                (v1.Y + v2.Y) / 2.0,
                (v1.Z + v2.Z) / 2.0
            };

            // Face normals and wedge angle
            double[] na = FaceNormal(fa, pt);
            double[] nb = FaceNormal(fb, pt);
            if (na == null || nb == null) return null;

            double dot = na[0]*nb[0] + na[1]*nb[1] + na[2]*nb[2];
            dot = Math.Max(-1.0, Math.Min(1.0, dot));
            double angle = Math.Acos(dot) * 180.0 / Math.PI;
            if (angle < CandidateMinAngle || angle > CandidateMaxAngle) return null;

            // === NEW: Physical narrow face dimension (not UV minmax) ===
            double ndim = Math.Min(FacePhysicalMinDim(fa), FacePhysicalMinDim(fb));
            if (ndim < 0 || ndim > NarrowFaceMaxMm) return null;

            // === NEW: Distance to product body (matching Python pipeline) ===
            double distA = FaceDistanceToBody(fa, productSolid);
            double distB = FaceDistanceToBody(fb, productSolid);
            double nearDist = Math.Min(distA, distB);
            if (nearDist > NearProductMaxMm) return null;

            // Classification matching Python pipeline
            bool conf = len <= ConfirmedMaxEdgeMm
                && angle >= ConfirmedMinAngle && angle <= ConfirmedMaxAngle;

            var faceTags = new List<int> { (int)fa.Tag, (int)fb.Tag };
            var edgeTags = new List<int> { (int)edge.Tag };

            return new Candidate
            {
                EdgeLen = len, Angle = angle, NarrowDim = ndim,
                IsConfirmed = conf,
                X = pt[0], Y = pt[1], Z = pt[2],
                FaceTags = faceTags, EdgeTags = edgeTags
            };
        }

        private double[] FaceNormal(Face face, double[] point)
        {
            try
            {
                double[] fp = new double[3], u1 = new double[3], v1 = new double[3];
                double[] u2 = new double[3], v2 = new double[3];
                double[] unitNorm = new double[3], radii = new double[2];
                _uf.Modl.AskFaceProps(face.Tag, point, fp, u1, v1, u2, v2, unitNorm, radii);
                double l = Math.Sqrt(unitNorm[0]*unitNorm[0]+unitNorm[1]*unitNorm[1]+unitNorm[2]*unitNorm[2]);
                if (l < 1e-10) return null;
                return new double[] { unitNorm[0]/l, unitNorm[1]/l, unitNorm[2]/l };
            }
            catch { return null; }
        }

        /// <summary>
        /// Physical bounding-box minimum dimension of a face (not UV parameter range).
        /// </summary>
        private double FacePhysicalMinDim(Face face)
        {
            try
            {
                // Use face edge vertices for bounding box (works across all NX versions)
                double minX=1e12, minY=1e12, minZ=1e12, maxX=-1e12, maxY=-1e12, maxZ=-1e12;
                bool hasVerts = false;
                foreach (Edge e in face.GetEdges())
                {
                    Point3d a, b;
                    e.GetVertices(out a, out b);
                    minX = Math.Min(minX, Math.Min(a.X, b.X));
                    minY = Math.Min(minY, Math.Min(a.Y, b.Y));
                    minZ = Math.Min(minZ, Math.Min(a.Z, b.Z));
                    maxX = Math.Max(maxX, Math.Max(a.X, b.X));
                    maxY = Math.Max(maxY, Math.Max(a.Y, b.Y));
                    maxZ = Math.Max(maxZ, Math.Max(a.Z, b.Z));
                    hasVerts = true;
                }
                if (!hasVerts) return -1;
                double dx = maxX - minX;
                double dy = maxY - minY;
                double dz = maxZ - minZ;
                return Math.Min(dx, Math.Min(dy, dz));
            }
            catch { return -1; }
        }

        /// <summary>
        /// Minimum distance from a face to the product solid body.
        /// Uses UF_MODL_ask_minimum_dist for accuracy.
        /// </summary>
        private double FaceDistanceToBody(Face face, Body productBody)
        {
            if (productBody == null) return 999;
            try
            {
                double[] guess1 = new double[3], guess2 = new double[3];
                double[] minPt1 = new double[3], minPt2 = new double[3];
                double minDist;
                _uf.Modl.AskMinimumDist(face.Tag, productBody.Tag, 0, guess1, 0, guess2,
                    out minDist, minPt1, minPt2);
                return minDist;
            }
            catch { return 999; }
        }

        private List<Candidate> Cluster(List<Candidate> raw)
        {
            var r = new List<Candidate>();
            foreach (var c in raw)
            {
                bool found = false;
                for (int i = 0; i < r.Count; i++)
                {
                    double dx = c.X - r[i].X, dy = c.Y - r[i].Y, dz = c.Z - r[i].Z;
                    if (Math.Sqrt(dx*dx+dy*dy+dz*dz) <= ClusterRadiusMm)
                    { if (c.EdgeLen < r[i].EdgeLen) r[i] = c; found = true; break; }
                }
                if (!found) r.Add(c);
            }
            r.Sort((a, b) => a.EdgeLen.CompareTo(b.EdgeLen));
            return r;
        }

        private ReviewIssue MakeIssue(Candidate c)
        {
            int hash = Math.Abs(BitConverter.ToInt32(BitConverter.GetBytes(c.X),0)
                ^ BitConverter.ToInt32(BitConverter.GetBytes(c.Y),0)
                ^ BitConverter.ToInt32(BitConverter.GetBytes(c.Z),0));

            var issue = new ReviewIssue
            {
                IssueId = "ISS-" + hash.ToString("X8"),
                X = c.X, Y = c.Y, Z = c.Z,
                EdgeLengthMm = c.EdgeLen,
                WedgeAngleDeg = c.Angle,
                NarrowFaceDimMm = c.NarrowDim,
                FaceTags = c.FaceTags,
                EdgeTags = c.EdgeTags,
            };

            if (c.IsConfirmed)
            {
                issue.Severity = IssueSeverity.ERROR;
                issue.Title = "分型面尖钢 - 确定几何风险";
                issue.Description = string.Format("边长={0:F6}mm 面夹角={1:F1}deg 窄面={2:F6}mm - 必须修改", c.EdgeLen, c.Angle, c.NarrowDim);
                issue.SuggestedAction = RepairActionType.FaceDeleteAndHeal;
                issue.RepairInstruction = string.Format("在 X={0:F2} Y={1:F2} Z={2:F2} 删除碎面后用N边面重建", c.X, c.Y, c.Z);
            }
            else
            {
                issue.Severity = IssueSeverity.WARN;
                issue.Title = "分型面尖钢 - 候选几何风险";
                issue.Description = string.Format("边长={0:F3}mm 面夹角={1:F1}deg 窄面={2:F3}mm - 待确认", c.EdgeLen, c.Angle, c.NarrowDim);
                issue.RepairInstruction = string.Format("在 X={0:F2} Y={1:F2} Z={2:F2} 确认是否构成真实尖钢", c.X, c.Y, c.Z);
                issue.SuggestedAction = RepairActionType.FaceJoin;  // All WARN → delete + heal
            }
            return issue;
        }

        public ReviewReport MakeReport(List<ReviewIssue> issues, Body ps)
        {
            var r = new ReviewReport
            {
                ReportId = "REV-" + DateTime.Now.ToString("yyyyMMddHHmmss"),
                ReviewTime = DateTime.Now,
                PartFileName = _workPart.Name,
                PartFullPath = _workPart.FullPath,
                Issues = issues,
                TotalIssues = issues.Count,
                ErrorCount = issues.Count(i => i.Severity == IssueSeverity.ERROR),
                WarnCount = issues.Count(i => i.Severity == IssueSeverity.WARN),
            };
            r.OverallStatus = issues.Any(i => i.Severity == IssueSeverity.ERROR) ? "不通过" : issues.Count > 0 ? "待确认" : "通过";

            if (ps != null)
            {
                Face[] fs = ps.GetFaces();
                int pl = 0, bs = 0;
                foreach (Face f in fs)
                {
                    try { int ft = (int)f.SolidFaceType; if (ft == 0 || ft == 22) pl++; else bs++; }
                    catch { bs++; }
                }
                r.PartingSurfaceSummary = fs.Length + "面(平面" + pl + ",曲面" + bs + ")";
            }
            return r;
        }

        public string ComputeSha256(string path)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var s = System.IO.File.OpenRead(path))
            { byte[] h = sha.ComputeHash(s); return BitConverter.ToString(h).Replace("-", "").ToLowerInvariant(); }
        }
    }

    internal struct Candidate
    {
        public double EdgeLen, Angle, NarrowDim;
        public bool IsConfirmed;
        public double X, Y, Z;
        public List<int> FaceTags, EdgeTags;
    }
}
