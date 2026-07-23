# -*- coding: utf-8 -*-
'''修复指挥官 — 桥接审查结果与 Parasolid PK 修改 DLL。

职责:
  1. 读取 review_result.json 中的 repair_plan.operations
  2. 读取 geometry_evidence.json 获取 face/edge tag 信息
  3. 为每个 issue 生成结构化 geometry_operations
  4. 序列化为 repair_instructions.json
  5. 调用 PartingSurfaceRepair.dll 执行修改
  6. 验证输出，生成修改日志
'''

from __future__ import annotations

import argparse
import ctypes
import json
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Dict, List, Optional

# Reuse existing modules
SCRIPT_DIR = Path(__file__).resolve().parent
if str(SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPT_DIR))

from geometry_pipeline import sha256_file
from repair_copy import prepare_working_copy, finalize_reviewed_copy


class RepairCommanderError(RuntimeError):
    pass


class RepairCommander:
    '''修复指令生成器与 DLL 调用器。'''

    def __init__(self, nx_root: Optional[str] = None):
        self.nx_root = nx_root

    # ── 步骤 1: 生成修复指令 ──────────────────────────────

    def generate_instructions(
        self,
        review_result_path: Path,
        evidence_path: Path,
        output_path: Path,
    ) -> Dict:
        '''从审查结果生成结构化修复指令 JSON。'''
        review = self._load_json(review_result_path)
        evidence = self._load_json(evidence_path)

        operations = []
        repair_plan = review.get('repair_plan', {})
        issues = review.get('issues', [])
        evidence_sharp_steels = evidence.get('sharp_steels', [])

        # Build issue index by issue_id
        issue_map = {iss['issue_id']: iss for iss in issues}
        evidence_map = {ss['id'].replace('SS-', 'ISS-', 1): ss for ss in evidence_sharp_steels}

        seq = 0
        for op in repair_plan.get('operations', []):
            seq += 1
            issue_id = op['issue_id']
            issue = issue_map.get(issue_id, {})
            evidence_item = evidence_map.get(issue_id)
            preferred = op.get('preferred_action', {})

            geo_op = self._translate_issue_to_operation(
                issue_id=issue_id,
                issue=issue,
                evidence_item=evidence_item,
                preferred_action=preferred,
                sequence=seq,
                evidence=evidence,
            )
            operations.append(geo_op)

        instructions = {
            'schema_version': '1.0',
            'generated_utc': datetime.now(timezone.utc).isoformat(),
            'source': {
                'review_id': review.get('review_id', ''),
                'evidence_path': str(evidence_path.resolve()),
                'review_result_path': str(review_result_path.resolve()),
                'parting_surface_body_tag': evidence.get('role_assignment', {}).get(
                    'parting_surface_body_tags', [0])[0],
                'reference_product_body_tag': evidence.get('role_assignment', {}).get(
                    'reference_product_body_tag', 0),
            },
            'target': {
                'input_prt': review.get('source', {}).get('full_path', ''),
                'output_prt': '',  # filled by execute step
                'parasolid_partition_id': self._find_ps_sheet_partition_id(evidence),
                'parasolid_partition_label': 'PS Sheet',
            },
            'operations': operations,
            'post_operations': [
                {
                    'action': 'body_sew_check',
                    'body_tag': evidence.get('role_assignment', {}).get(
                        'parting_surface_body_tags', [0])[0],
                    'tolerance_mm': 0.025,
                },
                {
                    'action': 'body_validate',
                    'body_tag': evidence.get('role_assignment', {}).get(
                        'parting_surface_body_tags', [0])[0],
                },
            ],
        }

        output_path.parent.mkdir(parents=True, exist_ok=True)
        output_path.write_text(
            json.dumps(instructions, ensure_ascii=False, indent=2),
            encoding='utf-8',
        )
        return instructions

    def _translate_issue_to_operation(
        self,
        issue_id: str,
        issue: Dict,
        evidence_item: Optional[Dict],
        preferred_action: Dict,
        sequence: int,
        evidence: Dict,
    ) -> Dict:
        '''核心翻译逻辑：将 issue 映射为具体几何操作类型。'''
        classification = issue.get('classification', 'candidate')
        measurements = issue.get('measurements', {})
        geo_evidence = issue.get('geometry_evidence', {})
        face_tags = geo_evidence.get('face_tags', [])
        edge_tags = geo_evidence.get('edge_tags', [])

        # Also get from evidence_item if available (sharper data)
        if evidence_item:
            face_tags = list(set(face_tags + evidence_item.get('evidence', {}).get('face_tags', [])))
            edge_tags = list(set(edge_tags + evidence_item.get('evidence', {}).get('edge_tags', [])))

        reasons = (evidence_item or {}).get('evidence', {}).get('reasons', [])
        wedge_angle = measurements.get('wedge_angle_deg', 90.0)
        edge_length = measurements.get('representative_edge_length_mm', 999)
        coord = issue.get('coordinate_approx') or preferred_action.get('coordinate_approx', {})

        base = {
            'op_id': 'OP-{:03d}'.format(sequence),
            'issue_id': issue_id,
            'sequence': sequence,
            'region': {
                'center_mm': coord,
                'radius_mm': 3.0,
                'target_face_tags': face_tags,
                'target_edge_tags': edge_tags,
            },
            'verification': {
                'post_condition': 'recheck',
                'target_min_edge_length_mm': 1.5,
                'target_min_angle_deg': 5.0,
                'target_max_narrow_dim_mm': 1.0,
            },
        }

        if classification == 'confirmed_geometry_risk':
            base['type'] = 'local_face_replacement'
            base['removal'] = {'action': 'delete_faces', 'face_tags': face_tags}
            base['reconstruction'] = {
                'method': 'n-sided_fill',
                'boundary_constraint': 'tangent_to_neighbors',
                'parameters': {
                    'max_face_count': 4,
                    'min_angle_deg': max(15.0, wedge_angle * 1.5),
                    'min_edge_length_mm': 0.5,
                },
            }
        elif 'micro_edge' in reasons:
            base['type'] = 'edge_delete_and_sew'
        elif classification == 'candidate':
            if wedge_angle < 5.0 and edge_length < 1.5:
                base['type'] = 'edge_merge_and_smooth'
                base['merge'] = {
                    'action': 'merge_faces',
                    'face_tag_pairs': self._pair_faces_by_edge(face_tags, edge_tags),
                    'tolerance_mm': 0.01,
                    'prefer_larger_face': True,
                }
                base['edge_treatment'] = {
                    'action': 'edge_blend',
                    'edge_tags': edge_tags,
                    'radius_mm': 1.0,
                    'overflow': 'extend_adjacent',
                }
            else:
                base['type'] = 'face_merge_only'
                base['merge'] = {
                    'action': 'merge_faces',
                    'face_tag_pairs': self._pair_faces_by_edge(face_tags, edge_tags),
                    'tolerance_mm': 0.025,
                    'prefer_larger_face': True,
                }
        else:
            base['type'] = 'local_face_replacement'
            base['removal'] = {'action': 'delete_faces', 'face_tags': face_tags}
            base['reconstruction'] = {
                'method': 'n-sided_fill',
                'boundary_constraint': 'tangent_to_neighbors',
                'parameters': {'max_face_count': 4},
            }

        return base

    @staticmethod
    def _pair_faces_by_edge(face_tags: List[int], edge_tags: List[int]) -> List[List[int]]:
        '''将 face_tags 配对为 [face_a, face_b] 列表。'''
        if len(face_tags) >= 2:
            return [face_tags[:2]]
        return [[face_tags[0], face_tags[0]]] if face_tags else []

    # ── 步骤 2: 调用 DLL 执行修改 ──────────────────────────

    def execute_repair(
        self,
        instructions: Dict,
        dll_path: Path,
        working_prt_path: Path,
    ) -> Dict:
        '''调用 PartingSurfaceRepair.dll 执行几何修改。'''
        dll_path = dll_path.resolve()
        if not dll_path.exists():
            raise RepairCommanderError('DLL 不存在: {}'.format(dll_path))

        # Update target output path in instructions
        instructions['target']['output_prt'] = str(working_prt_path.resolve())

        # Write updated instructions to temp file
        instructions_path = working_prt_path.with_suffix('.instructions.json')
        instructions_path.write_text(
            json.dumps(instructions, ensure_ascii=False, indent=2),
            encoding='utf-8',
        )

        # Load DLL
        dll = ctypes.CDLL(str(dll_path))

        # RepairPartingSurface(instructionsPath, nxRoot, out resultJson)
        dll.RepairPartingSurface.argtypes = [
            ctypes.c_char_p,  # instructionsPath
            ctypes.c_char_p,  # nxRoot
            ctypes.POINTER(ctypes.c_void_p),  # resultJson (out)
        ]
        dll.RepairPartingSurface.restype = ctypes.c_int

        # FreeResult(ptr)
        dll.FreeResult.argtypes = [ctypes.c_void_p]
        dll.FreeResult.restype = None

        result_ptr = ctypes.c_void_p()
        exit_code = dll.RepairPartingSurface(
            str(instructions_path).encode('utf-8'),
            (self.nx_root or '').encode('utf-8'),
            ctypes.byref(result_ptr),
        )

        if result_ptr.value:
            result_bytes = ctypes.cast(result_ptr, ctypes.c_char_p).value
            log = json.loads(result_bytes.decode('utf-8'))
            dll.FreeResult(result_ptr)
        else:
            log = {'status': 'failed', 'error': 'DLL returned null result', 'exit_code': exit_code}

        return log

    def dry_run(
        self,
        instructions: Dict,
        operation_index: int,
        dll_path: Path,
        input_prt: str,
    ) -> Dict:
        '''单步试运行，不保存 .prt。'''
        dll_path = dll_path.resolve()
        if not dll_path.exists():
            raise RepairCommanderError('DLL 不存在: {}'.format(dll_path))

        op_json = json.dumps(instructions['operations'][operation_index], ensure_ascii=False)
        dll = ctypes.CDLL(str(dll_path))
        dll.DryRunOperation.argtypes = [
            ctypes.c_char_p, ctypes.c_char_p, ctypes.c_char_p,
            ctypes.POINTER(ctypes.c_void_p),
        ]
        dll.DryRunOperation.restype = ctypes.c_int
        dll.FreeResult.argtypes = [ctypes.c_void_p]
        dll.FreeResult.restype = None

        result_ptr = ctypes.c_void_p()
        dll.DryRunOperation(
            input_prt.encode('utf-8'),
            op_json.encode('utf-8'),
            (self.nx_root or '').encode('utf-8'),
            ctypes.byref(result_ptr),
        )

        if result_ptr.value:
            result_bytes = ctypes.cast(result_ptr, ctypes.c_char_p).value
            report = json.loads(result_bytes.decode('utf-8'))
            dll.FreeResult(result_ptr)
        else:
            report = {'error': 'null result'}

        return report

    # ── 自动化流程 ──────────────────────────────────────────

    def auto_repair(
        self,
        session_directory: Path,
        dll_path: Path,
        source_path: Path,
        round_number: int,
    ) -> Dict:
        '''自动化: generate → execute → verify。'''
        review_path = session_directory / 'round-{:02d}'.format(round_number) / 'review_result.json'
        evidence_path = session_directory / 'round-{:02d}'.format(round_number) / 'geometry_evidence.json'

        if not review_path.exists() or not evidence_path.exists():
            raise RepairCommanderError(
                '缺少审查结果: review={}, evidence={}'.format(
                    review_path.exists(), evidence_path.exists()))

        # Step 1: Generate instructions
        instructions_path = session_directory / 'round-{:02d}'.format(round_number) / 'repair_instructions.json'
        instructions = self.generate_instructions(review_path, evidence_path, instructions_path)

        # Step 2: Prepare working copy
        work_dir = session_directory / '.work'
        manifest = prepare_working_copy(source_path, work_dir, round_number)
        working_prt = Path(manifest['working_copy']['path'])

        # Update instructions with actual paths
        instructions['target']['input_prt'] = str(working_prt.resolve())
        instructions['target']['output_prt'] = str(working_prt.resolve())  # modify in-place

        # Step 3: Execute repair
        repair_log = self.execute_repair(instructions, dll_path, working_prt)

        # Save repair log
        log_path = session_directory / 'round-{:02d}'.format(round_number) / 'repair_log.json'
        log_path.parent.mkdir(parents=True, exist_ok=True)
        log_path.write_text(json.dumps(repair_log, ensure_ascii=False, indent=2), encoding='utf-8')

        return {
            'instructions': instructions,
            'repair_log': repair_log,
            'working_prt': str(working_prt),
            'manifest': manifest,
        }

    # ── 辅助方法 ────────────────────────────────────────────

    @staticmethod
    def _load_json(path: Path) -> Dict:
        try:
            return json.loads(path.read_text(encoding='utf-8-sig'))
        except (OSError, ValueError) as exc:
            raise RepairCommanderError('无法读取 JSON: {} — {}'.format(path, exc))

    @staticmethod
    def _find_ps_sheet_partition_id(evidence: Dict) -> int:
        bodies = evidence.get('bodies', [])
        for body in bodies:
            if body.get('partition_label') == 'PS Sheet':
                return body.get('partition_id', 0)
        return 0


# ── CLI ──────────────────────────────────────────────────────

def main() -> int:
    parser = argparse.ArgumentParser(description='修复指挥官 — 生成修复指令并调用 Parasolid PK DLL')
    sub = parser.add_subparsers(dest='command', required=True)

    gen = sub.add_parser('generate', help='从审查结果生成 repair_instructions.json')
    gen.add_argument('--review', type=Path, required=True)
    gen.add_argument('--evidence', type=Path, required=True)
    gen.add_argument('--output', type=Path, required=True)
    gen.add_argument('--nx-root')

    exe = sub.add_parser('execute', help='调用 DLL 执行修改')
    exe.add_argument('--instructions', type=Path, required=True)
    exe.add_argument('--dll', type=Path, required=True)
    exe.add_argument('--output', type=Path, required=True)
    exe.add_argument('--nx-root')

    dry = sub.add_parser('dry-run', help='单步试运行')
    dry.add_argument('--instructions', type=Path, required=True)
    dry.add_argument('--dll', type=Path, required=True)
    dry.add_argument('--input-prt', required=True)
    dry.add_argument('--op-index', type=int, default=0)
    dry.add_argument('--nx-root')

    auto = sub.add_parser('auto', help='自动生成+执行')
    auto.add_argument('--session', type=Path, required=True)
    auto.add_argument('--dll', type=Path, required=True)
    auto.add_argument('--source', type=Path, required=True)
    auto.add_argument('--round', type=int, default=1)
    auto.add_argument('--nx-root')

    args = parser.parse_args()
    commander = RepairCommander(nx_root=args.nx_root)

    if args.command == 'generate':
        result = commander.generate_instructions(args.review, args.evidence, args.output)
        print(json.dumps(result, ensure_ascii=False, indent=2))

    elif args.command == 'execute':
        instructions = commander._load_json(args.instructions)
        log = commander.execute_repair(instructions, args.dll, args.output)
        print(json.dumps(log, ensure_ascii=False, indent=2))

    elif args.command == 'dry-run':
        instructions = commander._load_json(args.instructions)
        report = commander.dry_run(instructions, args.op_index, args.dll, args.input_prt)
        print(json.dumps(report, ensure_ascii=False, indent=2))

    elif args.command == 'auto':
        result = commander.auto_repair(args.session, args.dll, args.source, args.round)
        print(json.dumps(result, ensure_ascii=False, indent=2))

    return 0


if __name__ == '__main__':
    raise SystemExit(main())
