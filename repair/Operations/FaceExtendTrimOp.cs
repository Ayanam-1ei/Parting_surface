using System;
using System.Collections.Generic;
using PK = PLMComponents.Parasolid.PK_.Unsafe;

namespace PartingSurfaceRepair.Operations
{
    /// <summary>
    /// Handles: face_extend_and_trim — extend faces and mutually trim
    /// </summary>
    public static class FaceExtendTrimOp
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
                // Extend faces by a small margin so they overlap
                double extendDistance = op.region.radius_mm * 0.5;
                foreach (int faceTag in op.region.target_face_tags)
                {
                    PK.FACE_t face = (PK.FACE_t)faceTag;
                    var extendOpts = new PK.FACE.extend_o_t(true);
                    extendOpts.distance = extendDistance;
                    extendOpts.extension_type = 0; // natural

                    PK.ERROR.code_t status = PK.FACE.extend(face, &extendOpts);
                    if (status != PK.ERROR.code_t.no_errors)
                    {
                        result.warnings.Add(string.Format(
                            "PK_FACE_extend tag={0}: {1}", faceTag, status));
                    }
                }

                // Mutual trim: imprint and trim overlapping faces
                if (op.region.target_face_tags.Count >= 2)
                {
                    for (int i = 0; i < op.region.target_face_tags.Count; i++)
                    {
                        for (int j = i + 1; j < op.region.target_face_tags.Count; j++)
                        {
                            PK.FACE_t faceA = (PK.FACE_t)op.region.target_face_tags[i];
                            PK.FACE_t faceB = (PK.FACE_t)op.region.target_face_tags[j];

                            // Imprint face B onto face A
                            var imprintOpts = new PK.FACE.imprint_o_t(true);
                            PK.ERROR.code_t status = PK.FACE.imprint(faceA, faceB, &imprintOpts);
                            if (status == PK.ERROR.code_t.no_errors)
                            {
                                // Trim face A by the imprint curves on the side away from face B
                                var trimOpts = new PK.FACE.trim_o_t(true);
                                status = PK.FACE.trim(faceA, &trimOpts);
                                if (status != PK.ERROR.code_t.no_errors)
                                    result.warnings.Add(string.Format(
                                        "PK_FACE_trim {0}+{1}: {2}", faceA, faceB, status));
                            }
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
