# -*- coding: utf-8 -*-
"""NX Batch Journal: 分型面尖钢数据提取 v2 (纯 NXOpen, 无 UF)"""

import NXOpen, sys, os, json, math
from datetime import datetime

def main(args):
    prt_path = args[0] if args else None
    output = _empty_result(prt_path)
    
    if not prt_path or not os.path.exists(prt_path):
        output["error"] = "File not found: " + str(prt_path)
        _save(output, prt_path if prt_path else "error")
        return
    
    session = NXOpen.Session.GetSession()
    base_part = session.Parts.OpenBase(prt_path)[0]
    work_part = base_part or session.Parts.Work
    
    output["meta"]["full_path"] = prt_path
    output["meta"]["file_name"] = os.path.basename(prt_path)
    output["meta"]["extract_time"] = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    output["meta"]["nx_version"] = session.GetEnvironmentVariableValue("UGII_BASE_DIR") or ""
    
    try:
        bodies = list(work_part.Bodies)
        output["_debug"] = {"body_count": len(bodies)}
        
        if not bodies:
            output["error"] = "No bodies found"
            _save(output, prt_path)
            return
        
        # ── Compute bounding box from all edge vertices ──
        all_x, all_y, all_z = [], [], []
        body_min_ys = []  # per-body min Y
        body_max_ys = []  # per-body max Y
        
        for body in bodies:
            bx, by, bz = [], [], []
            for edge in body.GetEdges():
                try:
                    v1, v2 = edge.GetVertices()
                    for v in (v1, v2):
                        bx.append(v.X); by.append(v.Y); bz.append(v.Z)
                        all_x.append(v.X); all_y.append(v.Y); all_z.append(v.Z)
                except Exception:
                    continue
            
            if bx:
                body_min_ys.append(min(by))
                body_max_ys.append(max(by))
        
        if not all_x:
            output["error"] = "No vertices found"
            _save(output, prt_path)
            return
        
        min_x, max_x = min(all_x), max(all_x)
        min_y, max_y = min(all_y), max(all_y)
        min_z, max_z = min(all_z), max(all_z)
        dx, dy, dz = max_x - min_x, max_y - min_y, max_z - min_z
        
        output["product"]["max_outer_diameter_mm"] = round(max(dx, dz), 2)
        output["product"]["total_height_mm"] = round(dy, 2)
        output["product"]["max_projected_area_cm2"] = round((dx * dz) / 100.0, 2)
        
        mid_y = (min_y + max_y) / 2.0
        output["parting_line"]["coordinate_y_mm"] = round(mid_y, 2)
        
        # ── Estimate flatness ──
        # Collect all unique Y coordinates from vertices near mid-height
        y_near_mid = set()
        for y in all_y:
            if abs(y - mid_y) < dy * 0.1:
                y_near_mid.add(round(y, 1))
        
        if len(y_near_mid) <= 3:
            output["parting_line"]["shape_type"] = "flat"
            output["parting_line"]["flatness_score"] = 10
        elif len(y_near_mid) <= 8:
            output["parting_line"]["shape_type"] = "wavy"
            output["parting_line"]["flatness_score"] = 5
        else:
            output["parting_line"]["shape_type"] = "stepped"
            output["parting_line"]["flatness_score"] = 2
        
        output["parting_line"]["max_product_diameter_at_pl_mm"] = round(max(dx, dz), 2)
        output["parting_line"]["is_at_max_contour"] = True  # Assume yes
        
        # ── Estimate wall thickness ──
        # For each body, approximate thickness from closest edge pair distances
        total_volume = 0.0
        for body in bodies:
            try:
                mb = work_part.MeasureManager.NewMeasureBodiesBuilder()
                mb.AddBody(body)
                measure = mb.Commit()
                total_volume += measure.Volume
                mb.Destroy()
            except Exception:
                pass
        
        total_area_est = 2 * (dx*dz + dx*dy + dy*dz)
        if total_area_est > 0 and total_volume > 0:
            output["product"]["nominal_wall_thickness_mm"] = round((total_volume / total_area_est) * 2.0, 2)
        
        output["_debug"]["bbox"] = {"x": [round(min_x,1), round(max_x,1)], "y": [round(min_y,1), round(max_y,1)], "z": [round(min_z,1), round(max_z,1)]}
        output["_debug"]["volume"] = round(total_volume, 2)
        output["_debug"]["vertex_count"] = len(all_x)
        output["_debug"]["y_near_mid_count"] = len(y_near_mid)
        
        # ── Sharp steel detection ──
        ss_counter = 0
        pl_y = output["parting_line"]["coordinate_y_mm"]
        
        for body in bodies:
            # Process edges near parting line
            for edge in body.GetEdges():
                try:
                    v1, v2 = edge.GetVertices()
                    e_mid_y = (v1.Y + v2.Y) / 2.0
                    
                    # Only edges near parting line
                    if abs(e_mid_y - pl_y) > dy * 0.2:
                        continue
                    
                    # Get adjacent faces
                    faces = list(edge.GetFaces())
                    if len(faces) < 2:
                        continue
                    
                    # Estimate local thickness from edge length and adjacent edges
                    e_len = math.sqrt((v2.X-v1.X)**2 + (v2.Y-v1.Y)**2 + (v2.Z-v1.Z)**2)
                    if e_len < 0.1:
                        continue
                    
                    # Find closest parallel edge on opposite face
                    min_dist = float("inf")
                    for face in faces:
                        for other_edge in face.GetEdges():
                            if other_edge is edge:
                                continue
                            try:
                                ov1, ov2 = other_edge.GetVertices()
                                om = ((ov1.X+ov2.X)/2, (ov1.Y+ov2.Y)/2, (ov1.Z+ov2.Z)/2)
                                em = ((v1.X+v2.X)/2, (v1.Y+v2.Y)/2, (v1.Z+v2.Z)/2)
                                d = math.sqrt((om[0]-em[0])**2 + (om[1]-em[1])**2 + (om[2]-em[2])**2)
                                if d > 0.01:
                                    min_dist = min(min_dist, d)
                            except Exception:
                                continue
                    
                    if min_dist == float("inf") or min_dist > 10.0:
                        continue
                    
                    thickness = min_dist
                    height = abs((v1.Y + v2.Y) / 2.0 - pl_y)
                    
                    # Only report thin steel
                    if thickness >= 3.0:
                        continue
                    
                    ss_counter += 1
                    if ss_counter > 50:  # Limit
                        break
                    
                    output["sharp_steels"].append({
                        "id": "SS-{:03d}".format(ss_counter),
                        "position": "edge_Y{:.1f}".format(e_mid_y),
                        "coordinate_approx": {"x": round((v1.X+v2.X)/2, 1), "y": round(e_mid_y, 1), "z": round((v1.Z+v2.Z)/2, 1)},
                        "thickness_mm": round(thickness, 2),
                        "height_mm": round(height, 2),
                        "aspect_ratio": round(height / thickness, 2) if thickness > 0 else 99,
                        "edge_radius_mm": 0.0,
                        "is_on_parting_line": abs(e_mid_y - pl_y) < 2.0,
                        "is_in_high_pressure_zone": abs((v1.X+v2.X)/2) < dx * 0.3 and abs((v1.Z+v2.Z)/2) < dz * 0.3
                    })
                except Exception:
                    continue
        
        output["_debug"]["ss_found"] = len(output["sharp_steels"])
        
        # ── Undercut detection ──
        uc_counter = 0
        for body in bodies:
            for face in body.GetFaces():
                try:
                    # Get face center Y from its edges
                    fy = []
                    for edge in face.GetEdges():
                        try:
                            v1, v2 = edge.GetVertices()
                            fy.append(v1.Y)
                            fy.append(v2.Y)
                        except Exception:
                            continue
                    if not fy:
                        continue
                    face_min_y = min(fy)
                    face_max_y = max(fy)
                    face_mid_y = (face_min_y + face_max_y) / 2.0
                    
                    # Face below parting line and has significant depth
                    if face_mid_y < pl_y and (pl_y - face_mid_y) > 0.5:
                        uc_counter += 1
                        if uc_counter > 20:
                            break
                        output["undercuts"].append({
                            "id": "UC-{:03d}".format(uc_counter),
                            "position": "face_Y{:.1f}".format(face_mid_y),
                            "depth_mm": round(pl_y - face_mid_y, 2),
                            "direction": "vertical" if abs(pl_y - face_mid_y) > 2 else "horizontal",
                            "requires_slider": (pl_y - face_mid_y) > 2.0
                        })
                except Exception:
                    continue
        
        output["_debug"]["uc_found"] = len(output["undercuts"])
        
    except Exception as e:
        output["error"] = str(e)
    
    _save(output, prt_path)

def _empty_result(path):
    return {
        "meta": {"file_name": "", "full_path": path or "", "extract_time": "", "nx_version": ""},
        "product": {"material": "UNKNOWN", "max_outer_diameter_mm": 0.0, "total_height_mm": 0.0, "nominal_wall_thickness_mm": 0.0, "max_projected_area_cm2": 0.0},
        "parting_line": {"location_relative_to_product": "middle", "shape_type": "flat", "flatness_score": 10, "coordinate_y_mm": 0.0, "max_product_diameter_at_pl_mm": 0.0, "is_at_max_contour": False},
        "sharp_steels": [],
        "undercuts": [],
        "mold": {"cavity_material": "P20", "core_material": "P20", "expected_shot_life_k": 10}
    }

def _save(output, prt_path):
    out = os.path.splitext(prt_path)[0] + "_extracted.json" if prt_path else "error.json"
    with open(out, "w", encoding="utf-8") as f:
        json.dump(output, f, ensure_ascii=False, indent=2)

if __name__ == "__main__":
    main(sys.argv[1:])
