using System;
using System.Collections.Generic;
using PK = PLMComponents.Parasolid.PK_.Unsafe;

namespace PartingSurfaceRepair.Operations
{
    public static class FaceReplacementOp
    {
        public static unsafe RepairResult Execute(RepairOperation op, PK.BODY_t body)
        {
            var result = new RepairResult
            {
                op_id = op.op_id,
                issue_id = op.issue_id,
                status = "succeeded",
                before = GeometryMetrics.CaptureRegionMetrics(body, op.region.target_face_tags, op.region.target_edge_tags),
                new_entity_tags = new NewEntityTags { faces = new List<int>(), edges = new List<int>() }
            };

            try
            {
                var boundaryEdges = new List<PK.EDGE_t>();

                foreach (int faceTag in op.removal.face_tags)
                {
                    PK.FACE_t face = (PK.FACE_t)faceTag;
                    if (!CheckOwner((PK.ENTITY_t)face, body))
                    {
                        result.warnings.Add(string.Format(
                            "Face tag {0} does not belong to body, skipping", faceTag));
                        result.status = "partial";
                        continue;
                    }

                    // Collect loop edges as boundary for reconstruction
                    var loopResult = new PK.FACE.ask_loops_r_t();
                    PK.ERROR.code_t status = PK.FACE.ask_loops(face, &loopResult);
                    if (status == PK.ERROR.code_t.no_errors && loopResult.n_loops > 0)
                    {
                        for (int l = 0; l < loopResult.n_loops; l++)
                        {
                            PK.LOOP_t loop = loopResult.loops[l];
                            var finResult = new PK.LOOP.ask_fins_r_t();
                            status = PK.LOOP.ask_fins(loop, &finResult);
                            if (status == PK.ERROR.code_t.no_errors)
                            {
                                for (int f = 0; f < finResult.n_fins; f++)
                                    boundaryEdges.Add(finResult.fins[f].edge);
                            }
                        }
                    }

                    // Delete face
                    var deleteOpts = new PK.FACE.delete_o_t(true);
                    status = PK.FACE.delete(face, &deleteOpts);
                    if (status != PK.ERROR.code_t.no_errors)
                    {
                        result.warnings.Add(string.Format(
                            "PK_FACE_delete tag={0}: {1}", faceTag, status));
                        result.status = "partial";
                    }
                }

                // Deduplicate boundary edges
                var seen = new HashSet<int>();
                var cleanEdges = new List<PK.EDGE_t>();
                foreach (var edge in boundaryEdges)
                {
                    if (seen.Add((int)edge))
                        cleanEdges.Add(edge);
                }

                if (cleanEdges.Count == 0)
                {
                    result.status = "failed";
                    result.error = "No boundary edges for reconstruction";
                    return result;
                }

                // N-sided fill
                PK.EDGE_t[] edgeArray = cleanEdges.ToArray();
                PK.FACE_t newFace;
                fixed (PK.EDGE_t* edgePtr = edgeArray)
                {
                    var fillOpts = new PK.FACE.make_n_sided_o_t(true);
                    fillOpts.n_edges = cleanEdges.Count;
                    fillOpts.edges = edgePtr;

                    if (op.reconstruction?.parameters != null)
                    {
                        if (op.reconstruction.parameters.TryGetValue("max_face_count", out var mfc))
                            fillOpts.max_faces = mfc.Value<int>();
                    }

                    PK.ERROR.code_t status = PK.FACE.make_n_sided(&fillOpts, &newFace);
                    if (status != PK.ERROR.code_t.no_errors)
                    {
                        result.status = "failed";
                        result.error = string.Format("PK_FACE_make_n_sided: {0}", status);
                        result.suggestion = "边界边可能不共面或不连续，手动在 NX 中检查";
                        return result;
                    }
                }
                result.new_entity_tags.faces.Add((int)newFace);

                // Sew
                PK.FACE_t[] sewFaces = new[] { newFace };
                fixed (PK.FACE_t* sewPtr = sewFaces)
                {
                    var sewOpts = new PK.BODY.sew_o_t(true);
                    sewOpts.n_faces = 1;
                    sewOpts.faces = sewPtr;
                    sewOpts.tolerance = 0.025;

                    PK.ERROR.code_t status = PK.BODY.sew(body, &sewOpts);
                    if (status != PK.ERROR.code_t.no_errors)
                        result.warnings.Add(string.Format("PK_BODY_sew: {0}", status));
                }

                result.after = GeometryMetrics.CaptureRegionMetrics(
                    body, result.new_entity_tags.faces, result.new_entity_tags.edges);
            }
            catch (Exception ex)
            {
                result.status = "failed";
                result.error = ex.GetType().Name + ": " + ex.Message;
            }

            return result;
        }

        private static unsafe bool CheckOwner(PK.ENTITY_t entity, PK.BODY_t expected)
        {
            PK.BODY_t owner = new PK.BODY_t();
            PK.ERROR.code_t status = PK.ENTITY.ask_owner(entity, (PK.ENTITY_t*)&owner);
            return status == PK.ERROR.code_t.no_errors && (int)owner == (int)expected;
        }
    }
}
