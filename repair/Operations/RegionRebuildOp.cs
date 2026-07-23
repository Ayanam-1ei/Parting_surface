using System;
using System.Collections.Generic;
using PK = PLMComponents.Parasolid.PK_.Unsafe;

namespace PartingSurfaceRepair.Operations
{
    /// <summary>
    /// Handles: rebuild_region_from_product — offset product faces to rebuild parting surface region.
    /// This is the most complex operation and requires a reference solid body.
    /// </summary>
    public static class RegionRebuildOp
    {
        public static unsafe RepairResult Execute(RepairOperation op, PK.BODY_t partingBody, PK.BODY_t productBody)
        {
            var result = new RepairResult
            {
                op_id = op.op_id,
                issue_id = op.issue_id,
                status = "succeeded",
                before = GeometryMetrics.CaptureRegionMetrics(partingBody, op.region.target_face_tags, op.region.target_edge_tags),
                new_entity_tags = new NewEntityTags { faces = new List<int>(), edges = new List<int>() }
            };

            try
            {
                // Find faces on product body near the repair center
                PK.VECTOR_t center = new PK.VECTOR_t();
                center.coord[0] = op.region.center_mm.x / 1000.0; // mm to meters (PK internal)
                center.coord[1] = op.region.center_mm.y / 1000.0;
                center.coord[2] = op.region.center_mm.z / 1000.0;

                double radiusM = op.region.radius_mm / 1000.0;

                // Get all faces of product body
                var prodFaces = new PK.BODY.ask_faces_r_t();
                PK.ERROR.code_t status = PK.BODY.ask_faces(productBody, &prodFaces);
                if (status != PK.ERROR.code_t.no_errors || prodFaces.n_faces == 0)
                {
                    result.status = "failed";
                    result.error = "Cannot query product body faces";
                    return result;
                }

                // Find faces within radius of center
                var nearbyFaces = new List<PK.FACE_t>();
                for (int i = 0; i < prodFaces.n_faces; i++)
                {
                    var box = new PK.BOX_t();
                    PK.ENTITY_t entity = (PK.ENTITY_t)prodFaces.faces[i];
                    status = PK.TOPOL.find_box(entity, &box);
                    if (status != PK.ERROR.code_t.no_errors) continue;

                    double cx = (box.min_coord[0] + box.max_coord[0]) * 0.5;
                    double cy = (box.min_coord[1] + box.max_coord[1]) * 0.5;
                    double cz = (box.min_coord[2] + box.max_coord[2]) * 0.5;
                    double dist = Math.Sqrt(
                        (cx - center.coord[0]) * (cx - center.coord[0]) +
                        (cy - center.coord[1]) * (cy - center.coord[1]) +
                        (cz - center.coord[2]) * (cz - center.coord[2]));

                    if (dist <= radiusM)
                        nearbyFaces.Add(prodFaces.faces[i]);
                }

                if (nearbyFaces.Count == 0)
                {
                    result.status = "failed";
                    result.error = "No product faces found near repair center";
                    return result;
                }

                // Offset each nearby product face
                foreach (var face in nearbyFaces)
                {
                    var offsetOpts = new PK.FACE.offset_o_t(true);
                    offsetOpts.offset_distance = 0.001; // 1mm offset in meters (PK internal)
                    offsetOpts.create_new_body = false;

                    PK.ERROR.code_t offStatus = PK.FACE.offset(face, &offsetOpts);
                    if (offStatus == PK.ERROR.code_t.no_errors)
                        result.new_entity_tags.faces.Add((int)face);
                    else
                        result.warnings.Add(string.Format(
                            "PK_FACE_offset tag={0}: {1}", (int)face, offStatus));
                }

                result.after = GeometryMetrics.CaptureRegionMetrics(
                    partingBody, result.new_entity_tags.faces, result.new_entity_tags.edges);
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
