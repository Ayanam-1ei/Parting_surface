# -*- coding: utf-8 -*-
from __future__ import annotations

import os
import shutil
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, Optional


class RuntimeNotFoundError(RuntimeError):
    pass


@dataclass(frozen=True)
class RuntimePaths:
    nx_root: Path
    ug_inspect: Path
    nxbin: Path
    pskernel_net: Path
    pskernel: Path
    csc: Path
    run_journal: Optional[Path]


def locate_runtime(nx_root: Optional[str] = None, csc: Optional[str] = None) -> RuntimePaths:
    root = _find_nx_root(nx_root)
    nxbin = root / "NXBIN"
    ug_inspect = _first_existing(nxbin / "ug_inspect.exe", root / "UGII" / "ug_inspect.exe")
    pskernel_net = _first_existing(nxbin / "managed" / "pskernel_net.dll")
    pskernel = _first_existing(nxbin / "pskernel.dll")
    compiler = _find_csc(csc)
    run_journal = nxbin / "run_journal.exe"
    return RuntimePaths(
        nx_root=root,
        ug_inspect=ug_inspect,
        nxbin=nxbin,
        pskernel_net=pskernel_net,
        pskernel=pskernel,
        csc=compiler,
        run_journal=run_journal if run_journal.exists() else None,
    )


def _find_nx_root(explicit: Optional[str]) -> Path:
    candidates = []
    if explicit:
        candidates.append(Path(explicit))
    for variable in ("PARTING_SURFACE_NX_ROOT", "UGII_BASE_DIR", "NX_ROOT"):
        value = os.environ.get(variable)
        if value:
            candidates.append(Path(value))
    for drive in ("C", "D", "E", "F", "G", "S"):
        candidates.extend(
            [
                Path(f"{drive}:\\nx"),
                Path(f"{drive}:\\Program Files\\Siemens\\NX2306"),
                Path(f"{drive}:\\Program Files\\Siemens\\NX"),
            ]
        )
    for candidate in _deduplicate(candidates):
        if (candidate / "NXBIN" / "ug_inspect.exe").exists() and (
            candidate / "NXBIN" / "managed" / "pskernel_net.dll"
        ).exists():
            return candidate.resolve()
    raise RuntimeNotFoundError(
        "未找到兼容 NX/Parasolid 运行时。请设置 PARTING_SURFACE_NX_ROOT 指向 NX 安装目录。"
    )


def _find_csc(explicit: Optional[str]) -> Path:
    candidates = []
    if explicit:
        candidates.append(Path(explicit))
    environment_value = os.environ.get("PARTING_SURFACE_CSC")
    if environment_value:
        candidates.append(Path(environment_value))
    on_path = shutil.which("csc.exe") or shutil.which("csc")
    if on_path:
        candidates.append(Path(on_path))
    candidates.extend(
        [
            Path(r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"),
            Path(r"C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"),
        ]
    )
    program_files = Path(os.environ.get("ProgramFiles", r"C:\Program Files"))
    vs_root = program_files / "Microsoft Visual Studio" / "2022"
    for edition in ("BuildTools", "Community", "Professional", "Enterprise"):
        candidates.append(vs_root / edition / "MSBuild" / "Current" / "Bin" / "Roslyn" / "csc.exe")
    for candidate in _deduplicate(candidates):
        if candidate.exists():
            return candidate.resolve()
    raise RuntimeNotFoundError(
        "未找到 C# 编译器。请安装 .NET Framework/Visual Studio，或设置 PARTING_SURFACE_CSC。"
    )


def _first_existing(*paths: Path) -> Path:
    for path in paths:
        if path.exists():
            return path.resolve()
    raise RuntimeNotFoundError("缺少运行时文件: " + ", ".join(str(path) for path in paths))


def _deduplicate(paths: Iterable[Path]):
    seen = set()
    for path in paths:
        key = str(path).lower()
        if key not in seen:
            seen.add(key)
            yield path
