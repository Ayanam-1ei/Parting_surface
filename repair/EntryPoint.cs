using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Newtonsoft.Json;
using PartingSurfaceRepair.Operations;
using PK = PLMComponents.Parasolid.PK_.Unsafe;

namespace PartingSurfaceRepair
{
    public static class EntryPoint
    {
        /// <summary>
        /// Main repair entry point. Reads repair_instructions.json, executes all operations,
        /// saves the modified .prt, and returns a repair log JSON.
        /// </summary>
        [DllExport]
        public static int RepairPartingSurface(
            [MarshalAs(UnmanagedType.LPStr)] string instructionsPath,
            [MarshalAs(UnmanagedType.LPStr)] string nxRoot,
            out IntPtr resultJson)
        {
            resultJson = IntPtr.Zero;
            RepairLog log = new RepairLog
            {
                execution_utc = DateTime.UtcNow.ToString("o"),
                operations = new List<RepairResult>(),
                warnings = new List<string>(),
                status = "completed"
            };

            try
            {
                // Parse instructions
                RepairInstruction instructions = InstructionParser.Parse(instructionsPath);
                log.schema_version = instructions.schema_version;

                // Start Parasolid session
                PrtSession.Start(nxRoot);

                // Compute input hash
                log.input_prt_sha256 = ComputeSha256(instructions.target.input_prt);

                // Load the PRT
                PK.BODY_t body = PrtSession.LoadBody(instructions.target.input_prt);
                log.parasolid_version = "detected";

                // Load reference product body if needed
                PK.BODY_t productBody = new PK.BODY_t();
                bool hasProductBody = false;

                // Execute operations in sequence order
                instructions.operations.Sort((a, b) => a.sequence.CompareTo(b.sequence));
                log.total_operations = instructions.operations.Count;

                int originalFaceCount = 0, originalEdgeCount = 0;
                var faceQuery = new PK.BODY.ask_faces_r_t();
                var edgeQuery = new PK.BODY.ask_edges_r_t();
                if (PK.BODY.ask_faces(body, &faceQuery) == PK.ERROR.code_t.no_errors)
                    originalFaceCount = faceQuery.n_faces;
                if (PK.BODY.ask_edges(body, &edgeQuery) == PK.ERROR.code_t.no_errors)
                    originalEdgeCount = edgeQuery.n_edges;

                foreach (var op in instructions.operations)
                {
                    RepairResult result;
                    switch (op.type)
                    {
                        case "local_face_replacement":
                            result = FaceReplacementOp.Execute(op, body);
                            break;
                        case "edge_merge_and_smooth":
                        case "face_merge_only":
                        case "edge_blend_only":
                            result = EdgeMergeOp.Execute(op, body);
                            break;
                        case "edge_delete_and_sew":
                            result = EdgeDeleteSewOp.Execute(op, body);
                            break;
                        case "face_extend_and_trim":
                            result = FaceExtendTrimOp.Execute(op, body);
                            break;
                        case "rebuild_region_from_product":
                            if (productBody.Equals(new PK.BODY_t()))
                            {
                                result = new RepairResult
                                {
                                    op_id = op.op_id,
                                    issue_id = op.issue_id,
                                    status = "failed",
                                    error = "No reference product body available for region rebuild"
                                };
                            }
                            else
                            {
                                result = RegionRebuildOp.Execute(op, body, productBody);
                            }
                            break;
                        default:
                            result = new RepairResult
                            {
                                op_id = op.op_id,
                                issue_id = op.issue_id,
                                status = "failed",
                                error = "Unknown operation type: " + op.type
                            };
                            break;
                    }

                    log.operations.Add(result);
                    if (result.status == "succeeded") log.succeeded++;
                    else if (result.status == "failed") log.failed++;
                    else log.succeeded++; // partial counts as succeeded with warnings
                }

                // Run post-operations: sew check + body validation
                foreach (var postOp in instructions.post_operations)
                {
                    if (postOp.action == "body_sew_check")
                    {
                        var checkResult = new PK.BODY.check_o_t(true);
                        checkResult.check_sheet_watertight = true;
                        int nFaults = 0;
                        PK.BODY.fault_t* faults = null;
                        PK.ERROR.code_t status = PK.BODY.check(body, &checkResult, &nFaults, &faults);
                        if (status != PK.ERROR.code_t.no_errors || nFaults > 0)
                            log.warnings.Add(string.Format("Post sew check: {0} faults", nFaults));
                    }
                    else if (postOp.action == "body_validate")
                    {
                        log.body_validation = Validation.ValidateBody(body, originalFaceCount, originalEdgeCount);
                    }
                }

                // Save the modified PRT
                string outputDir = Path.GetDirectoryName(instructions.target.output_prt);
                if (!string.IsNullOrEmpty(outputDir))
                    Directory.CreateDirectory(outputDir);
                PrtSession.SaveBody(body, instructions.target.output_prt);
                log.output_prt_path = instructions.target.output_prt;
                log.output_prt_sha256 = ComputeSha256(instructions.target.output_prt);

                // Update overall status
                if (log.failed == log.total_operations)
                    log.status = "failed";
                else if (log.failed > 0)
                    log.status = "partial";

                // Serialize result
                string json = JsonConvert.SerializeObject(log, Formatting.Indented);
                resultJson = Marshal.StringToHGlobalAnsi(json);
                return log.status == "failed" ? 1 : 0;
            }
            catch (Exception ex)
            {
                log.status = "failed";
                log.warnings.Add("Fatal: " + ex.GetType().Name + ": " + ex.Message);
                string json = JsonConvert.SerializeObject(log, Formatting.Indented);
                resultJson = Marshal.StringToHGlobalAnsi(json);
                return 2;
            }
            finally
            {
                PrtSession.Stop();
            }
        }

        [DllExport]
        public static void FreeResult(IntPtr ptr)
        {
            if (ptr != IntPtr.Zero)
                Marshal.FreeHGlobal(ptr);
        }

        [DllExport]
        public static int DryRunOperation(
            [MarshalAs(UnmanagedType.LPStr)] string inputPrtPath,
            [MarshalAs(UnmanagedType.LPStr)] string operationJson,
            [MarshalAs(UnmanagedType.LPStr)] string nxRoot,
            out IntPtr dryRunReport)
        {
            dryRunReport = IntPtr.Zero;
            try
            {
                PrtSession.Start(nxRoot);
                PK.BODY_t body = PrtSession.LoadBody(inputPrtPath);

                var op = JsonConvert.DeserializeObject<RepairOperation>(operationJson);
                RepairResult result;
                switch (op.type)
                {
                    case "local_face_replacement":
                        result = FaceReplacementOp.Execute(op, body);
                        break;
                    case "edge_merge_and_smooth":
                    case "face_merge_only":
                    case "edge_blend_only":
                        result = EdgeMergeOp.Execute(op, body);
                        break;
                    case "edge_delete_and_sew":
                        result = EdgeDeleteSewOp.Execute(op, body);
                        break;
                    case "face_extend_and_trim":
                        result = FaceExtendTrimOp.Execute(op, body);
                        break;
                    default:
                        result = new RepairResult { op_id = op.op_id, status = "failed", error = "Unknown type" };
                        break;
                }

                var report = new { operation = result, note = "DRY RUN — no file was saved" };
                string json = JsonConvert.SerializeObject(report, Formatting.Indented);
                dryRunReport = Marshal.StringToHGlobalAnsi(json);
                return result.status == "failed" ? 1 : 0;
            }
            catch (Exception ex)
            {
                string json = JsonConvert.SerializeObject(new { error = ex.Message }, Formatting.Indented);
                dryRunReport = Marshal.StringToHGlobalAnsi(json);
                return 2;
            }
            finally
            {
                PrtSession.Stop();
            }
        }

        private static string ComputeSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
