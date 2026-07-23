using System;
using System.Collections.Generic;
using NXOpen;

namespace PartingSurfaceReview.Repair
{
    public class FaceRepairOps
    {
        private readonly Session _session;
        private readonly Part _workPart;

        public FaceRepairOps()
        {
            _session = Session.GetSession();
            _workPart = _session.Parts.Work;
        }

        private int CountAllFaces()
        {
            int total = 0;
            Body[] bodies = _workPart.Bodies.ToArray();
            foreach (Body b in bodies) total += b.GetFaces().Length;
            return total;
        }

        public RepairResult DeleteFacesAndHeal(List<int> faceTags)
        {
            var result = new RepairResult();
            try
            {
                var faces = new List<Face>();
                Body[] bodies = _workPart.Bodies.ToArray();
                foreach (Body b in bodies)
                    foreach (Face f in b.GetFaces())
                        if (faceTags.Contains((int)f.Tag)) faces.Add(f);

                if (faces.Count == 0)
                { result.Success = false; result.ErrorMessage = "未找到目标面(tags=" + string.Join(",", faceTags) + ")"; return result; }

                result.FacesBefore = CountAllFaces();

                var builder = _workPart.Features.CreateDeleteFaceBuilder(null);
                builder.Heal = true;

                var rule = _workPart.ScRuleFactory.CreateRuleFaceDumb(faces.ToArray());
                builder.FaceCollector.ReplaceRules(
                    new SelectionIntentRule[] { rule }, null, false);
                builder.CommitFeature();
                builder.Destroy();

                result.FacesAfter = CountAllFaces();
                result.Success = true;
            }
            catch (Exception ex)
            { result.Success = false; result.ErrorMessage = "DeleteFacesAndHeal: " + ex.GetType().Name + ": " + ex.Message; }
            return result;
        }

        public RepairResult JoinFaces(List<int> faceTag1, List<int> faceTag2)
        {
            var all = new List<int>();
            all.AddRange(faceTag1);
            all.AddRange(faceTag2);
            return DeleteFacesAndHeal(all);
        }

        public RepairResult BlendEdges(List<int> edgeTags, double radius)
        {
            // EdgeBlend is unreliable on sheet bodies; redirect to face deletion on adjacent faces.
            // This is called with the edge's adjacent face tags from the analyzer.
            var result = new RepairResult { Success = false,
                ErrorMessage = "BlendEdges disabled — use DeleteFacesAndHeal on adjacent faces instead" };
            return result;
        }

        public Session.UndoMarkId CreateUndoMark()
        {
            return _session.SetUndoMark(Session.MarkVisibility.Visible, "PreRepair");
        }

        public void UndoToMark(Session.UndoMarkId markId)
        {
            try { _session.UndoToMark(markId, "PreRepair"); }
            catch { }
        }
    }
}
