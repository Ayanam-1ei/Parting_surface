using System;
using System.Collections.Generic;
using PK = PLMComponents.Parasolid.PK_.Unsafe;

namespace PartingSurfaceRepair.Operations
{
    public static class GeometryMetrics
    {
        public static unsafe GeometrySnapshot CaptureRegionMetrics(
            PK.BODY_t body, List<int> faceTags, List<int> edgeTags)
        {
            var snapshot = new GeometrySnapshot
            {
                face_count = faceTags?.Count ?? 0,
                edge_count = edgeTags?.Count ?? 0,
                min_edge_length_mm = double.PositiveInfinity,
                min_face_angle_deg = double.PositiveInfinity,
                min_narrow_dim_mm = double.PositiveInfinity
            };

            double NX_TO_MM = 1000.0;

            // Measure edges
            if (edgeTags != null)
            {
                foreach (int edgeTag in edgeTags)
                {
                    PK.EDGE_t edge = (PK.EDGE_t)edgeTag;
                    try
                    {
                        var curveInfo = new PK.EDGE.ask_curve_r_t();
                        PK.ERROR.code_t status = PK.EDGE.ask_curve(edge, &curveInfo);
                        if (status == PK.ERROR.code_t.no_errors)
                        {
                            // Length from interval
                            double length = curveInfo.interval.finite *
                                (curveInfo.interval.value[1] - curveInfo.interval.value[0]);
                            if (length > 0 && length < snapshot.min_edge_length_mm)
                                snapshot.min_edge_length_mm = length * NX_TO_MM;
                        }
                    }
                    catch { /* Skip edges that can't be queried */ }
                }
            }

            // Measure faces
            if (faceTags != null)
            {
                foreach (int faceTag in faceTags)
                {
                    PK.FACE_t face = (PK.FACE_t)faceTag;
                    try
                    {
                        // Bounding box for narrow dimension
                        var box = new PK.BOX_t();
                        PK.ENTITY_t entity = (PK.ENTITY_t)face;
                        PK.ERROR.code_t status = PK.TOPOL.find_box(entity, &box);
                        if (status == PK.ERROR.code_t.no_errors)
                        {
                            double dx = (box.max_coord[0] - box.min_coord[0]) * NX_TO_MM;
                            double dy = (box.max_coord[1] - box.min_coord[1]) * NX_TO_MM;
                            double dz = (box.max_coord[2] - box.min_coord[2]) * NX_TO_MM;
                            double minDim = Math.Min(Math.Min(dx, dy), dz);
                            if (minDim > 0 && minDim < snapshot.min_narrow_dim_mm)
                                snapshot.min_narrow_dim_mm = minDim;
                        }
                    }
                    catch { /* Skip faces that can't be queried */ }
                }
            }

            // Clamp infinities
            if (double.IsPositiveInfinity(snapshot.min_edge_length_mm))
                snapshot.min_edge_length_mm = -1;
            if (double.IsPositiveInfinity(snapshot.min_face_angle_deg))
                snapshot.min_face_angle_deg = -1;
            if (double.IsPositiveInfinity(snapshot.min_narrow_dim_mm))
                snapshot.min_narrow_dim_mm = -1;

            return snapshot;
        }
    }
}
