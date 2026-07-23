using System;
using System.Collections.Generic;

namespace PartingSurfaceRepair
{
    public sealed class RepairResult
    {
        public string op_id;
        public string issue_id;
        public string status;
        public string error;
        public string suggestion;
        public GeometrySnapshot before;
        public GeometrySnapshot after;
        public NewEntityTags new_entity_tags;
    }

    public sealed class GeometrySnapshot
    {
        public int edge_count;
        public int face_count;
        public double min_edge_length_mm;
        public double min_face_angle_deg;
        public double min_narrow_dim_mm;
    }

    public sealed class NewEntityTags
    {
        public List<int> faces;
        public List<int> edges;
    }

    public sealed class RepairLog
    {
        public string schema_version = "1.0";
        public string dll_version = "1.0.0";
        public string parasolid_version;
        public string execution_utc;
        public string status;
        public string input_prt_sha256;
        public string output_prt_sha256;
        public string output_prt_path;
        public int total_operations;
        public int succeeded;
        public int failed;
        public List<RepairResult> operations;
        public BodyValidation body_validation;
        public List<string> warnings;
    }

    public sealed class BodyValidation
    {
        public int body_tag;
        public bool is_solid;
        public bool is_watertight;
        public int face_count_delta;
        public int edge_count_delta;
        public List<string> issues;
    }
}
