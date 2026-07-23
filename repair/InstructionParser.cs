using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PartingSurfaceRepair
{
    public sealed class RepairInstruction
    {
        public string schema_version;
        public string generated_utc;
        public SourceInfo source;
        public TargetInfo target;
        public List<RepairOperation> operations;
        public List<PostOperation> post_operations;
    }

    public sealed class SourceInfo
    {
        public string review_id;
        public string evidence_path;
        public string review_result_path;
        public int parting_surface_body_tag;
        public int reference_product_body_tag;
    }

    public sealed class TargetInfo
    {
        public string input_prt;
        public string output_prt;
        public int parasolid_partition_id;
        public string parasolid_partition_label;
    }

    public sealed class RepairOperation
    {
        public string op_id;
        public string issue_id;
        public int sequence;
        public string type;
        public RegionDef region;
        public RemovalDef removal;
        public ReconstructionDef reconstruction;
        public MergeDef merge;
        public EdgeTreatmentDef edge_treatment;
        public VerificationDef verification;
    }

    public sealed class RegionDef
    {
        public Coordinate3D center_mm;
        public double radius_mm;
        public List<int> target_face_tags;
        public List<int> target_edge_tags;
    }

    public sealed class Coordinate3D
    {
        public double x;
        public double y;
        public double z;
    }

    public sealed class RemovalDef
    {
        public string action;
        public List<int> face_tags;
    }

    public sealed class ReconstructionDef
    {
        public string method;
        public string boundary_constraint;
        public JObject parameters;
    }

    public sealed class MergeDef
    {
        public string action;
        public List<List<int>> face_tag_pairs;
        public double tolerance_mm;
        public bool prefer_larger_face;
    }

    public sealed class EdgeTreatmentDef
    {
        public string action;
        public List<int> edge_tags;
        public double radius_mm;
        public string overflow;
    }

    public sealed class VerificationDef
    {
        public string post_condition;
        public double? target_min_edge_length_mm;
        public double? target_min_angle_deg;
        public double? target_max_narrow_dim_mm;
    }

    public sealed class PostOperation
    {
        public string action;
        public int body_tag;
        public double tolerance_mm;
    }

    public static class InstructionParser
    {
        public static RepairInstruction Parse(string path)
        {
            string json = File.ReadAllText(path, System.Text.Encoding.UTF8);
            return JsonConvert.DeserializeObject<RepairInstruction>(json);
        }
    }
}
