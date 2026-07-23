using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PartingSurface.Repair
{
    /// <summary>
    /// 生成的 NX Journal Python 脚本
    /// </summary>
    public class GeneratedScript
    {
        public string FileName;
        public string Content;
        public string OperationType;
        public string IssueId;
    }

    /// <summary>
    /// 为修复操作生成 NX Journal Python 脚本
    /// 使用 @@PLACEHOLDER@@ 模板替换，避免 C# string.Format 与 Python {} 冲突
    /// </summary>
    public static class ScriptGenerator
    {
        public static List<GeneratedScript> Generate(List<RepairOperation> operations, string prtPath)
        {
            var scripts = new List<GeneratedScript>();

            for (int i = 0; i < operations.Count; i++)
            {
                var op = operations[i];
                int index = i + 1;
                string scriptName = string.Format(CultureInfo.InvariantCulture,
                    "repair_{0:000}_{1}_{2}.py", index, op.IssueId, string.IsNullOrEmpty(op.Type) ? "unknown" : op.Type);
                string content;

                switch (op.Type)
                {
                    case "parting_surface_local_rework":
                        content = GenerateLocalReworkScript(op, prtPath, index);
                        break;
                    case "local_insert":
                        content = GenerateLocalInsertScript(op, prtPath, index);
                        break;
                    case "manual_review":
                        content = GenerateManualReviewScript(op, prtPath, index);
                        break;
                    default:
                        content = GenerateManualReviewScript(op, prtPath, index);
                        break;
                }

                scripts.Add(new GeneratedScript
                {
                    FileName = scriptName,
                    Content = content,
                    OperationType = op.Type,
                    IssueId = op.IssueId
                });
            }

            return scripts;
        }

        public static GeneratedScript GenerateMasterScript(List<GeneratedScript> scripts, string prtPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# -*- coding: utf-8 -*-");
            sb.AppendLine("\"\"\"主驱动脚本：按顺序执行所有修复操作\"\"\"");
            sb.AppendLine("import os");
            sb.AppendLine("import sys");
            sb.AppendLine("import importlib.util");
            sb.AppendLine("");
            sb.AppendLine("SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))");
            sb.AppendLine("PRT_PATH = r\"" + prtPath + "\"");
            sb.AppendLine("");
            sb.AppendLine("scripts = [");
            foreach (var s in scripts)
            {
                sb.AppendLine("    \"" + s.FileName + "\",");
            }
            sb.AppendLine("]");
            sb.AppendLine("");
            sb.AppendLine("for script_name in scripts:");
            sb.AppendLine("    script_path = os.path.join(SCRIPT_DIR, script_name)");
            sb.AppendLine("    print('[Master] Executing ' + script_name + '...')");
            sb.AppendLine("    try:");
            sb.AppendLine("        spec = importlib.util.spec_from_file_location('repair_module', script_path)");
            sb.AppendLine("        mod = importlib.util.module_from_spec(spec)");
            sb.AppendLine("        spec.loader.exec_module(mod)");
            sb.AppendLine("        if hasattr(mod, 'main'):");
            sb.AppendLine("            mod.main()");
            sb.AppendLine("        print('[Master] ' + script_name + ' completed.')");
            sb.AppendLine("    except Exception as e:");
            sb.AppendLine("        print('[Master] ERROR in ' + script_name + ': ' + str(e))");
            sb.AppendLine("        sys.exit(1)");
            sb.AppendLine("");
            sb.AppendLine("print('[Master] All repair scripts completed.')");

            return new GeneratedScript
            {
                FileName = "repair_master.py",
                Content = sb.ToString(),
                OperationType = "master",
                IssueId = "ALL"
            };
        }

        private static string GenerateLocalReworkScript(RepairOperation op, string prtPath, int index)
        {
            double clusterRadius = GetParam(op, "inspection_radius_mm", 2.0);
            double targetRadius = GetParam(op, "target_min_true_radius_mm", 0.5);
            string prtRaw = prtPath.Replace("\\", "/");

            var sb = new StringBuilder();
            sb.AppendLine("# -*- coding: utf-8 -*-");
            sb.AppendLine("\"\"\"");
            sb.AppendLine("修复脚本 #" + index + ": " + op.IssueId + " [" + op.Type + "]");
            sb.AppendLine("问题: " + EscapePy(op.Instruction));
            sb.AppendLine("坐标: X=" + Fmt(op.CoordX) + " Y=" + Fmt(op.CoordY) + " Z=" + Fmt(op.CoordZ));
            sb.AppendLine("操作类型: 分型面局部重构");
            sb.AppendLine("说明: 在坐标附近 " + Fmt(clusterRadius) + "mm 范围内查找并重构狭窄碎面，消除微小边");
            sb.AppendLine("\"\"\"");
            sb.AppendLine("import NXOpen");
            sb.AppendLine("import NXOpen.GeometricUtilities");
            sb.AppendLine("import math");
            sb.AppendLine("import sys");
            sb.AppendLine("");
            sb.AppendLine("def main():");
            sb.AppendLine("    the_session = NXOpen.Session.GetSession()");
            sb.AppendLine("");
            sb.AppendLine("    base_part, load_status = the_session.Parts.OpenBaseDisplay(r'" + prtPath + "')");
            sb.AppendLine("    if load_status.NumberUnloadedParts > 0:");
            sb.AppendLine("        print('[Repair " + index + "] WARNING: parts failed to load')");
            sb.AppendLine("");
            sb.AppendLine("    part = the_session.Parts.Work");
            sb.AppendLine("    if part is None:");
            sb.AppendLine("        print('[Repair " + index + "] ERROR: No work part found')");
            sb.AppendLine("        sys.exit(1)");
            sb.AppendLine("");
            sb.AppendLine("    # NX 内部单位是米，输入坐标是毫米");
            sb.AppendLine("    target_x = " + Fmt(op.CoordX / 1000.0));
            sb.AppendLine("    target_y = " + Fmt(op.CoordY / 1000.0));
            sb.AppendLine("    target_z = " + Fmt(op.CoordZ / 1000.0));
            sb.AppendLine("    search_radius_m = " + Fmt(clusterRadius / 1000.0));
            sb.AppendLine("");
            sb.AppendLine("    print('[Repair " + index + "] Searching faces near target within " + Fmt(clusterRadius) + "mm...')");
            sb.AppendLine("");
            sb.AppendLine("    faces_near = []");
            sb.AppendLine("    for face in part.Faces.ToArray():");
            sb.AppendLine("        try:");
            sb.AppendLine("            evaluator = face.GetEvaluator()");
            sb.AppendLine("            u_min, u_max, v_min, v_max = evaluator.GetParameters()");
            sb.AppendLine("            u_mid = (u_min + u_max) / 2.0");
            sb.AppendLine("            v_mid = (v_min + v_max) / 2.0");
            sb.AppendLine("            pos = evaluator.Evaluate(u_mid, v_mid)");
            sb.AppendLine("            px = pos[0]");
            sb.AppendLine("            py = pos[1]");
            sb.AppendLine("            pz = pos[2]");
            sb.AppendLine("            dist = math.sqrt((px - target_x)**2 + (py - target_y)**2 + (pz - target_z)**2)");
            sb.AppendLine("            if dist <= search_radius_m:");
            sb.AppendLine("                faces_near.append(face)");
            sb.AppendLine("                print('[Repair " + index + "] Found face at distance ' + str(dist))");
            sb.AppendLine("        except Exception:");
            sb.AppendLine("            continue");
            sb.AppendLine("");
            sb.AppendLine("    if not faces_near:");
            sb.AppendLine("        print('[Repair " + index + "] WARNING: No faces found within search radius. Skipping.')");
            sb.AppendLine("        the_session.Parts.CloseAll(NXOpen.BasePart.CloseModified.CloseModified)");
            sb.AppendLine("        return");
            sb.AppendLine("");
            sb.AppendLine("    undo_mark = the_session.SetUndoMark(NXOpen.Session.MarkVisibility.Visible, 'Repair " + index + ": Local Rework')");
            sb.AppendLine("");
            sb.AppendLine("    try:");
            sb.AppendLine("        blend_radius_m = " + Fmt(targetRadius / 1000.0));
            sb.AppendLine("        edges_to_blend = []");
            sb.AppendLine("");
            sb.AppendLine("        for face in faces_near:");
            sb.AppendLine("            for edge in face.GetEdges():");
            sb.AppendLine("                try:");
            sb.AppendLine("                    edge_eval = edge.GetEvaluator()");
            sb.AppendLine("                    mid = edge_eval.GetPoint(0.5)");
            sb.AppendLine("                    mx = mid[0]");
            sb.AppendLine("                    my = mid[1]");
            sb.AppendLine("                    mz = mid[2]");
            sb.AppendLine("                    dist = math.sqrt((mx - target_x)**2 + (my - target_y)**2 + (mz - target_z)**2)");
            sb.AppendLine("                    if dist <= search_radius_m:");
            sb.AppendLine("                        edges_to_blend.append(edge)");
            sb.AppendLine("                except Exception:");
            sb.AppendLine("                    continue");
            sb.AppendLine("");
            sb.AppendLine("        if edges_to_blend:");
            sb.AppendLine("            print('[Repair " + index + "] Applying edge blend radius=" + Fmt(targetRadius) + "mm to ' + str(len(edges_to_blend)) + ' edges')");
            sb.AppendLine("            features = part.Features");
            sb.AppendLine("            blend_builder = features.CreateEdgeBlendFeatureBuilder()");
            sb.AppendLine("            blend_builder.RadiusFirst = blend_radius_m");
            sb.AppendLine("");
            sb.AppendLine("            for edge in edges_to_blend:");
            sb.AppendLine("                try:");
            sb.AppendLine("                    blend_builder.Geometry.Add(edge)");
            sb.AppendLine("                except Exception as e:");
            sb.AppendLine("                    print('[Repair " + index + "] Could not add edge: ' + str(e))");
            sb.AppendLine("");
            sb.AppendLine("            if blend_builder.Geometry.ArraySize > 0:");
            sb.AppendLine("                blend_feature = blend_builder.CommitFeature()");
            sb.AppendLine("                print('[Repair " + index + "] Edge blend committed')");
            sb.AppendLine("            else:");
            sb.AppendLine("                print('[Repair " + index + "] No edges could be added to blend. Skipping blend.')");
            sb.AppendLine("            blend_builder.Destroy()");
            sb.AppendLine("");
            sb.AppendLine("        output_path = r'" + prtPath + "'.replace('.prt', '_repaired_" + index.ToString("000", CultureInfo.InvariantCulture) + ".prt')");
            sb.AppendLine("        the_session.Parts.SaveAs(output_path)");
            sb.AppendLine("        print('[Repair " + index + "] Saved to: ' + output_path)");
            sb.AppendLine("");
            sb.AppendLine("    except Exception as e:");
            sb.AppendLine("        print('[Repair " + index + "] ERROR during rework: ' + str(e))");
            sb.AppendLine("        the_session.UndoToMark(undo_mark, 'Repair " + index + ": Local Rework')");
            sb.AppendLine("        raise");
            sb.AppendLine("    finally:");
            sb.AppendLine("        the_session.Parts.CloseAll(NXOpen.BasePart.CloseModified.CloseModified)");
            sb.AppendLine("");
            sb.AppendLine("if __name__ == '__main__':");
            sb.AppendLine("    main()");

            return sb.ToString();
        }

        private static string GenerateLocalInsertScript(RepairOperation op, string prtPath, int index)
        {
            double targetLigament = GetParam(op, "target_min_insert_ligament_mm", 2.5);
            double targetRadius = GetParam(op, "target_min_true_radius_mm", 0.5);

            var sb = new StringBuilder();
            sb.AppendLine("# -*- coding: utf-8 -*-");
            sb.AppendLine("\"\"\"");
            sb.AppendLine("修复脚本 #" + index + ": " + op.IssueId + " [" + op.Type + "]");
            sb.AppendLine("问题: " + EscapePy(op.Instruction));
            sb.AppendLine("坐标: X=" + Fmt(op.CoordX) + " Y=" + Fmt(op.CoordY) + " Z=" + Fmt(op.CoordZ));
            sb.AppendLine("操作类型: 局部镶件");
            sb.AppendLine("说明: 在坐标处创建镶件凹槽，最小韧带 " + Fmt(targetLigament) + "mm，圆角 " + Fmt(targetRadius) + "mm");
            sb.AppendLine("\"\"\"");
            sb.AppendLine("import NXOpen");
            sb.AppendLine("import NXOpen.GeometricUtilities");
            sb.AppendLine("import math");
            sb.AppendLine("import sys");
            sb.AppendLine("");
            sb.AppendLine("def main():");
            sb.AppendLine("    the_session = NXOpen.Session.GetSession()");
            sb.AppendLine("");
            sb.AppendLine("    base_part, load_status = the_session.Parts.OpenBaseDisplay(r'" + prtPath + "')");
            sb.AppendLine("    if load_status.NumberUnloadedParts > 0:");
            sb.AppendLine("        print('[Repair " + index + "] WARNING: parts failed to load')");
            sb.AppendLine("");
            sb.AppendLine("    part = the_session.Parts.Work");
            sb.AppendLine("    if part is None:");
            sb.AppendLine("        print('[Repair " + index + "] ERROR: No work part found')");
            sb.AppendLine("        sys.exit(1)");
            sb.AppendLine("");
            sb.AppendLine("    target_x = " + Fmt(op.CoordX / 1000.0));
            sb.AppendLine("    target_y = " + Fmt(op.CoordY / 1000.0));
            sb.AppendLine("    target_z = " + Fmt(op.CoordZ / 1000.0));
            sb.AppendLine("    ligament_m = " + Fmt(targetLigament / 1000.0));
            sb.AppendLine("    radius_m = " + Fmt(targetRadius / 1000.0));
            sb.AppendLine("");
            sb.AppendLine("    print('[Repair " + index + "] Creating insert pocket at target...')");
            sb.AppendLine("");
            sb.AppendLine("    undo_mark = the_session.SetUndoMark(NXOpen.Session.MarkVisibility.Visible, 'Repair " + index + ": Local Insert')");
            sb.AppendLine("");
            sb.AppendLine("    try:");
            sb.AppendLine("        cylinder_radius = ligament_m / 2.0");
            sb.AppendLine("        cylinder_height = ligament_m * 3.0");
            sb.AppendLine("        origin = NXOpen.Point3d(target_x, target_y, target_z - cylinder_height / 2.0)");
            sb.AppendLine("");
            sb.AppendLine("        features = part.Features");
            sb.AppendLine("        cylinder_builder = features.CreateCylinderFeatureBuilder()");
            sb.AppendLine("        cylinder_builder.Diameter.Value = cylinder_radius * 2.0");
            sb.AppendLine("        cylinder_builder.Height.Value = cylinder_height");
            sb.AppendLine("        cylinder_builder.Origin = origin");
            sb.AppendLine("        cylinder_builder.BooleanOption.Type = NXOpen.GeometricUtilities.BooleanOperation.Create");
            sb.AppendLine("");
            sb.AppendLine("        cylinder_feature = cylinder_builder.CommitFeature()");
            sb.AppendLine("        cylinder_body = cylinder_feature.GetBodies()[0]");
            sb.AppendLine("        print('[Repair " + index + "] Created tool cylinder')");
            sb.AppendLine("        cylinder_builder.Destroy()");
            sb.AppendLine("");
            sb.AppendLine("        target_body = None");
            sb.AppendLine("        for body in part.Bodies.ToArray():");
            sb.AppendLine("            target_body = body");
            sb.AppendLine("            break");
            sb.AppendLine("");
            sb.AppendLine("        if target_body is not None:");
            sb.AppendLine("            print('[Repair " + index + "] Subtracting tool from target body...')");
            sb.AppendLine("            subtract_builder = features.CreateBooleanFeatureBuilder()");
            sb.AppendLine("            subtract_builder.Operation = NXOpen.GeometricUtilities.BooleanOperation.Subtract");
            sb.AppendLine("            subtract_builder.TargetBody.Add(target_body)");
            sb.AppendLine("            subtract_builder.ToolBody.Add(cylinder_body)");
            sb.AppendLine("            subtract_feature = subtract_builder.CommitFeature()");
            sb.AppendLine("            print('[Repair " + index + "] Subtract completed')");
            sb.AppendLine("            subtract_builder.Destroy()");
            sb.AppendLine("");
            sb.AppendLine("        output_path = r'" + prtPath + "'.replace('.prt', '_repaired_" + index.ToString("000", CultureInfo.InvariantCulture) + ".prt')");
            sb.AppendLine("        the_session.Parts.SaveAs(output_path)");
            sb.AppendLine("        print('[Repair " + index + "] Saved to: ' + output_path)");
            sb.AppendLine("");
            sb.AppendLine("    except Exception as e:");
            sb.AppendLine("        print('[Repair " + index + "] ERROR during insert creation: ' + str(e))");
            sb.AppendLine("        the_session.UndoToMark(undo_mark, 'Repair " + index + ": Local Insert')");
            sb.AppendLine("        raise");
            sb.AppendLine("    finally:");
            sb.AppendLine("        the_session.Parts.CloseAll(NXOpen.BasePart.CloseModified.CloseModified)");
            sb.AppendLine("");
            sb.AppendLine("if __name__ == '__main__':");
            sb.AppendLine("    main()");

            return sb.ToString();
        }

        private static string GenerateManualReviewScript(RepairOperation op, string prtPath, int index)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# -*- coding: utf-8 -*-");
            sb.AppendLine("\"\"\"");
            sb.AppendLine("修复脚本 #" + index + ": " + op.IssueId + " [" + (op.Type ?? "unknown") + "]");
            sb.AppendLine("问题: " + EscapePy(op.Instruction));
            sb.AppendLine("坐标: X=" + Fmt(op.CoordX) + " Y=" + Fmt(op.CoordY) + " Z=" + Fmt(op.CoordZ));
            sb.AppendLine("操作类型: 人工确认 (不执行自动修复)");
            sb.AppendLine("说明: 该位置为候选级风险，需工程师确认后再决定修复策略");
            sb.AppendLine("\"\"\"");
            sb.AppendLine("import sys");
            sb.AppendLine("");
            sb.AppendLine("def main():");
            sb.AppendLine("    print('=' * 60)");
            sb.AppendLine("    print('[Repair " + index + "] MANUAL REVIEW REQUIRED')");
            sb.AppendLine("    print('[Repair " + index + "] Issue ID: " + op.IssueId + "')");
            sb.AppendLine("    print('[Repair " + index + "] Classification: " + (op.Classification ?? "") + "')");
            sb.AppendLine("    print('[Repair " + index + "] Severity: " + (op.Severity ?? "") + "')");
            sb.AppendLine("    print('[Repair " + index + "] Coordinate: X=" + Fmt(op.CoordX) + " Y=" + Fmt(op.CoordY) + " Z=" + Fmt(op.CoordZ) + "')");
            sb.AppendLine("    print('[Repair " + index + "] Edge Length: " + Fmt(op.RepresentativeEdgeLengthMm) + " mm')");
            sb.AppendLine("    print('[Repair " + index + "] Wedge Angle: " + Fmt(op.WedgeAngleDeg) + " deg')");
            sb.AppendLine("    print('[Repair " + index + "] Narrow Face Min Dim: " + Fmt(op.MinNarrowFaceDimensionMm) + " mm')");
            sb.AppendLine("    print('[Repair " + index + "] Instruction: " + EscapePy(op.Instruction) + "')");
            sb.AppendLine("    print('=' * 60)");
            sb.AppendLine("    print('[Repair " + index + "] No automatic repair performed. Please review manually in NX.')");
            sb.AppendLine("");
            sb.AppendLine("if __name__ == '__main__':");
            sb.AppendLine("    main()");

            return sb.ToString();
        }

        private static double GetParam(RepairOperation op, string key, double defaultValue)
        {
            object v;
            if (op.Parameters.TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
                catch { return defaultValue; }
            }
            return defaultValue;
        }

        private static string Fmt(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return "0.0";
            return value.ToString("G", CultureInfo.InvariantCulture);
        }

        private static string EscapePy(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("'", "\\'").Replace("\n", " ").Replace("\r", "");
        }
    }
}
