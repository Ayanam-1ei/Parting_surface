using System;
using System.Collections.Generic;
using PK = PLMComponents.Parasolid.PK_.Unsafe;

namespace PartingSurfaceRepair.Operations
{
    /// <summary>
    /// Handles: edge_delete_and_sew — delete micro-edges and re-sew adjacent faces
    /// </summary>
    public static class EdgeDeleteSewOp
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
                // Step 1: Delete the micro edges
                foreach (int edgeTag in op.region.target_edge_tags)
                {
                    PK.EDGE_t edge = (PK.EDGE_t)edgeTag;

                    // Get adjacent faces before deletion
                    var adjResult = new PK.EDGE.ask_faces_r_t();
                    PK.ERROR.code_t status = PK.EDGE.ask_faces(edge, &adjResult);
                    List<PK.FACE_t> adjacentFaces = new List<PK.FACE_t>();
                    if (status == PK.ERROR.code_t.no_errors && adjResult.n_faces > 0)
                    {
                        for (int a = 0; a < adjResult.n_faces; a++)
                            adjacentFaces.Add(adjResult.faces[a]);
                    }

                    // Delete the edge
                    var deleteOpts = new PK.EDGE.delete_o_t(true);
                    status = PK.EDGE.delete(edge, &deleteOpts);
                    if (status != PK.ERROR.code_t.no_errors)
                    {
                        result.warnings.Add(string.Format(
                            "PK_EDGE_delete tag={0}: {1}", edgeTag, status));
                        result.status = "partial";
                        continue;
                    }

                    // Step 2: Try to merge adjacent faces now that edge is gone
                    if (adjacentFaces.Count >= 2)
                    {
                        for (int i = 0; i < adjacentFaces.Count - 1; i++)
                        {
                            var mergeOpts = new PK.FACE.merge_o_t(true);
                            mergeOpts.tolerance = 0.01;
                            mergeOpts.delete_redundant = true;

                            PK.FACE_t merged;
                            status = PK.FACE.merge(adjacentFaces[i], adjacentFaces[i + 1], &mergeOpts, &merged);
                            if (status == PK.ERROR.code_t.no_errors)
                                result.new_entity_tags.faces.Add((int)merged);
                        }
                    }
                }

                result.after = GeometryMetrics.CaptureRegionMetrics(
                    body, op.region.target_face_tags, op.region.target_edge_tags);
            }
            catch (Exception ex)
            {
                result.status = "failed";
                result.error = ex.GetType().Name + ": " + ex.Message;
            }

            return result;
        }
    }
}
