using System;
using System.Collections.Generic;
using PK = PLMComponents.Parasolid.PK_.Unsafe;

namespace PartingSurfaceRepair.Operations
{
    /// <summary>
    /// Handles: edge_merge_and_smooth, face_merge_only, edge_blend_only
    /// </summary>
    public static class EdgeMergeOp
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
                // Step 1: Face merging (if requested)
                if (op.merge != null && op.merge.face_tag_pairs != null)
                {
                    foreach (var pair in op.merge.face_tag_pairs)
                    {
                        if (pair.Count != 2) continue;

                        PK.FACE_t faceA = (PK.FACE_t)pair[0];
                        PK.FACE_t faceB = (PK.FACE_t)pair[1];

                        var mergeOpts = new PK.FACE.merge_o_t(true);
                        mergeOpts.tolerance = op.merge.tolerance_mm;
                        mergeOpts.delete_redundant = true;

                        PK.FACE_t mergedFace;
                        PK.ERROR.code_t status = PK.FACE.merge(faceA, faceB, &mergeOpts, &mergedFace);
                        if (status != PK.ERROR.code_t.no_errors)
                        {
                            result.warnings.Add(string.Format(
                                "PK_FACE_merge {0}+{1}: {2}", pair[0], pair[1], status));
                            result.status = "partial";
                        }
                        else
                        {
                            result.new_entity_tags.faces.Add((int)mergedFace);
                        }
                    }
                }

                // Step 2: Edge blending (if requested)
                if (op.edge_treatment != null && op.edge_treatment.edge_tags != null)
                {
                    foreach (int edgeTag in op.edge_treatment.edge_tags)
                    {
                        PK.EDGE_t edge = (PK.EDGE_t)edgeTag;

                        var blendOpts = new PK.EDGE.blend_o_t(true);
                        blendOpts.radius = op.edge_treatment.radius_mm;
                        blendOpts.overflow = 0; // extend_adjacent

                        PK.ERROR.code_t status = PK.EDGE.blend(edge, &blendOpts);
                        if (status != PK.ERROR.code_t.no_errors)
                        {
                            result.warnings.Add(string.Format(
                                "PK_EDGE_blend tag={0} r={1}: {2}",
                                edgeTag, op.edge_treatment.radius_mm, status));
                            result.status = result.status == "succeeded" ? "partial" : result.status;
                        }
                        else
                        {
                            result.new_entity_tags.edges.Add(edgeTag);
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
