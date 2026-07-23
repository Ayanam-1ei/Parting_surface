using System;
using System.Collections.Generic;
using PK = PLMComponents.Parasolid.PK_.Unsafe;

namespace PartingSurfaceRepair
{
    public static class Validation
    {
        public static unsafe BodyValidation ValidateBody(PK.BODY_t body, int originalFaceCount, int originalEdgeCount)
        {
            var result = new BodyValidation
            {
                body_tag = (int)body,
                issues = new List<string>()
            };

            try
            {
                // Check body type
                var typeResult = new PK.BODY.ask_type_r_t();
                PK.ERROR.code_t status = PK.BODY.ask_type(body, &typeResult);
                if (status == PK.ERROR.code_t.no_errors)
                {
                    result.is_solid = typeResult.body_type == PK.BODY.type_t.solid_c;
                }

                // Count current faces
                var facesResult = new PK.BODY.ask_faces_r_t();
                status = PK.BODY.ask_faces(body, &facesResult);
                if (status == PK.ERROR.code_t.no_errors)
                {
                    result.face_count_delta = facesResult.n_faces - originalFaceCount;
                }

                // Count current edges
                var edgesResult = new PK.BODY.ask_edges_r_t();
                status = PK.BODY.ask_edges(body, &edgesResult);
                if (status == PK.ERROR.code_t.no_errors)
                {
                    result.edge_count_delta = edgesResult.n_edges - originalEdgeCount;
                }

                // Check watertight (for sheet bodies, check all edges are manifold)
                if (!result.is_solid && status == PK.ERROR.code_t.no_errors)
                {
                    result.is_watertight = true;
                    for (int i = 0; i < edgesResult.n_edges; i++)
                    {
                        var adjFaces = new PK.EDGE.ask_faces_r_t();
                        PK.ERROR.code_t edgeStatus = PK.EDGE.ask_faces(edgesResult.edges[i], &adjFaces);
                        if (edgeStatus != PK.ERROR.code_t.no_errors || adjFaces.n_faces > 2)
                        {
                            result.is_watertight = false;
                            result.issues.Add(string.Format(
                                "Non-manifold edge tag={0}, adjacent faces={1}",
                                (int)edgesResult.edges[i],
                                edgeStatus == PK.ERROR.code_t.no_errors ? adjFaces.n_faces : -1));
                        }
                    }
                }

                // Check for self-intersection
                var checkOpts = new PK.BODY.check_o_t(true);
                checkOpts.check_self_intersection = true;
                checkOpts.check_geometry = true;
                int nFaults = 0;
                PK.BODY.fault_t* faults = null;
                status = PK.BODY.check(body, &checkOpts, &nFaults, &faults);
                if (status == PK.ERROR.code_t.no_errors && nFaults > 0)
                {
                    result.issues.Add(string.Format("Body check found {0} faults", nFaults));
                }
            }
            catch (Exception ex)
            {
                result.issues.Add("Validation exception: " + ex.Message);
            }

            return result;
        }
    }
}
