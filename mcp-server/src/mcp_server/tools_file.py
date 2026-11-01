"""文件与数据类 MCP 工具（Python 本地实现，不经过 CAD 桥）。

包含 11 个：list_files / read_file / write_file / delete_file /
append_file / replace_in_file / save_library / list_outputs /
export_csv / create_table / param_set。

所有相对路径均基于输出目录（默认 ~/Documents/AutoCAD-MCP，
可用环境变量 AUTOCAD_MCP_OUTPUT_DIR 覆盖）；绝对路径直接使用。
"""

from __future__ import annotations

import csv
import json
import os
from datetime import datetime

OUTPUT_DIR = os.environ.get(
    "AUTOCAD_MCP_OUTPUT_DIR",
    os.path.join(
        os.path.expanduser("~"), "Documents", "AutoCAD-MCP"
    ),
)

_LIBRARY_FILE = os.path.join(OUTPUT_DIR, "library.json")
_PARAMS_FILE = os.path.join(OUTPUT_DIR, "params.json")


def _resolve(path: str, *, must_exist: bool = False) -> str:
    """把相对路径解析到输出目录；绝对路径原样返回。

    同时拒绝 .. 路径穿越，防止意外访问目录外文件。
    """
    if not path:
        raise ValueError("path 不能为空")

    if os.path.isabs(path):
        full = os.path.normpath(path)
    else:
        full = os.path.normpath(os.path.join(OUTPUT_DIR, path))

    root = os.path.normpath(OUTPUT_DIR)

    if not os.path.isabs(full) or not (
        full == root or full.startswith(root + os.sep)
    ):
        raise ValueError(f"路径超出输出目录范围：{path}")

    if must_exist and not os.path.exists(full):
        raise ValueError(f"文件不存在：{path}")

    return full


def _ensure_dir() -> None:
    os.makedirs(OUTPUT_DIR, exist_ok=True)


def _read_json(path: str, default) -> object:
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return default


def _write_json(path: str, data) -> None:
    _ensure_dir()

    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)


def register(registry) -> None:
    """向 ToolRegistry（官方 SDK 工具注册中心）注册本模块全部工具。"""

    @registry.tool()
    def list_files(
        path: str = ".", pattern: str = "*"
    ) -> dict:
        """列出输出目录（或其子目录）下的文件。

        pattern 支持通配符，如 "*.csv"、"*_result.*"。
        """
        try:
            full = _resolve(path, must_exist=True)

            if not os.path.isdir(full):
                raise ValueError(f"不是目录：{path}")

            import fnmatch

            names = sorted(
                n for n in os.listdir(full)
                if fnmatch.fnmatch(n, pattern)
            )

            items = []
            for n in names:
                p = os.path.join(full, n)

                items.append(
                    {
                        "name": n,
                        "is_dir": os.path.isdir(p),
                        "size": os.path.getsize(p)
                        if os.path.isfile(p)
                        else None,
                        "modified": datetime.fromtimestamp(
                            os.path.getmtime(p)
                        ).strftime("%Y-%m-%d %H:%M:%S"),
                    }
                )

            return {
                "success": True,
                "path": full,
                "count": len(items),
                "files": items,
            }
        except Exception as exc:
            return {"success": False, "error": str(exc)}

    @registry.tool()
    def read_file(path: str, encoding: str = "utf-8") -> dict:
        """读取文本文件内容（基于输出目录）。"""
        try:
            full = _resolve(path, must_exist=True)

            with open(full, encoding=encoding) as f:
                content = f.read()

            return {
                "success": True,
                "path": full,
                "size": len(content),
                "content": content,
            }
        except Exception as exc:
            return {"success": False, "error": str(exc)}

    @registry.tool()
    def write_file(
        path: str, content: str, encoding: str = "utf-8"
    ) -> dict:
        """写入文本文件（自动创建目录，覆盖已有文件）。"""
        try:
            full = _resolve(path)
            _ensure_dir()

            with open(full, "w", encoding=encoding) as f:
                f.write(content)

            return {
                "success": True,
                "path": full,
                "size": len(content),
                "message": f"文件已写入：{full}",
            }
        except Exception as exc:
            return {"success": False, "error": str(exc)}

    @registry.tool()
    def delete_file(path: str) -> dict:
        """删除文件（基于输出目录）。"""
        try:
            full = _resolve(path, must_exist=True)

            os.remove(full)

            return {
                "success": True,
                "deleted": full,
                "message": f"文件已删除：{full}",
            }
        except Exception as exc:
            return {"success": False, "error": str(exc)}

    @registry.tool()
    def append_file(
        path: str, content: str, encoding: str = "utf-8"
    ) -> dict:
        """向文本文件追加内容（文件不存在时创建）。"""
        try:
            full = _resolve(path)
            _ensure_dir()

            with open(full, "a", encoding=encoding) as f:
                f.write(content)

            return {
                "success": True,
                "path": full,
                "message": f"已追加 {len(content)} 字符到 {full}",
            }
        except Exception as exc:
            return {"success": False, "error": str(exc)}

    @registry.tool()
    def replace_in_file(
        path: str,
        old: str,
        new: str,
        encoding: str = "utf-8",
    ) -> dict:
        """替换文本文件中的内容（替换所有匹配项）。"""
        try:
            full = _resolve(path, must_exist=True)

            with open(full, encoding=encoding) as f:
                content = f.read()

            count = content.count(old)

            if count == 0:
                return {
                    "success": False,
                    "error": f"未找到匹配内容：{old[:50]}",
                }

            content = content.replace(old, new)

            with open(full, "w", encoding=encoding) as f:
                f.write(content)

            return {
                "success": True,
                "path": full,
                "replaced": count,
                "message": f"已替换 {count} 处",
            }
        except Exception as exc:
            return {"success": False, "error": str(exc)}

    @registry.tool()
    def save_library(name: str, data: dict) -> dict:
        """把数据保存到键值库（JSON 文件）。

        库文件位于输出目录 library.json；同名键覆盖保存。
        适合保存图元 handle 清单、参数集等中间结果。
        """
        try:
            _ensure_dir()

            library = _read_json(_LIBRARY_FILE, {})
            assert isinstance(library, dict)

            library[name] = {
                "saved_at": datetime.now().strftime(
                    "%Y-%m-%d %H:%M:%S"
                ),
                "data": data,
            }

            _write_json(_LIBRARY_FILE, library)

            return {
                "success": True,
                "name": name,
                "path": _LIBRARY_FILE,
                "message": f"已保存到库：{name}",
            }
        except Exception as exc:
            return {"success": False, "error": str(exc)}

    @registry.tool()
    def list_outputs() -> dict:
        """列出输出目录中的全部文件（含子目录）。"""
        try:
            _ensure_dir()

            items = []

            for root, _dirs, files in os.walk(OUTPUT_DIR):
                for name in sorted(files):
                    p = os.path.join(root, name)

                    items.append(
                        {
                            "name": os.path.relpath(
                                p, OUTPUT_DIR
                            ),
                            "size": os.path.getsize(p),
                            "modified": datetime.fromtimestamp(
                                os.path.getmtime(p)
                            ).strftime("%Y-%m-%d %H:%M:%S"),
                        }
                    )

            return {
                "success": True,
                "output_dir": OUTPUT_DIR,
                "count": len(items),
                "files": items,
            }
        except Exception as exc:
            return {"success": False, "error": str(exc)}

    @registry.tool()
    def export_csv(
        path: str, rows: list[dict], columns: list[str] | None = None
    ) -> dict:
        """把行数据导出为 CSV 文件。

        rows 为字典列表；columns 指定列顺序（缺省取首行的键）。
        """
        try:
            if not rows:
                raise ValueError("rows 不能为空")

            if columns is None:
                columns = list(rows[0].keys())

            full = _resolve(path)
            _ensure_dir()

            with open(
                full, "w", newline="", encoding="utf-8-sig"
            ) as f:
                writer = csv.DictWriter(
                    f, fieldnames=columns, extrasaction="ignore"
                )
                writer.writeheader()
                writer.writerows(rows)

            return {
                "success": True,
                "path": full,
                "rows": len(rows),
                "columns": columns,
                "message": f"CSV 已导出：{full}",
            }
        except Exception as exc:
            return {"success": False, "error": str(exc)}

    @registry.tool()
    def create_table(
        path: str, headers: list[str], rows: list[list]
    ) -> dict:
        """创建 CSV 表格文件。

        headers 为表头；rows 为数据行（每行元素个数与表头一致）。
        """
        try:
            full = _resolve(path)
            _ensure_dir()

            with open(
                full, "w", newline="", encoding="utf-8-sig"
            ) as f:
                writer = csv.writer(f)
                writer.writerow(headers)

                for row in rows:
                    if len(row) != len(headers):
                        raise ValueError(
                            f"行长度 {len(row)} 与表头长度 "
                            f"{len(headers)} 不一致"
                        )

                    writer.writerow(row)

            return {
                "success": True,
                "path": full,
                "rows": len(rows),
                "message": f"表格已创建：{full}",
            }
        except Exception as exc:
            return {"success": False, "error": str(exc)}

    @registry.tool()
    def param_set(key: str, value: str | float | int | bool) -> dict:
        """设置/保存工作参数（键值存储，持久化到 params.json）。"""
        try:
            _ensure_dir()

            params = _read_json(_PARAMS_FILE, {})
            assert isinstance(params, dict)

            params[key] = {
                "value": value,
                "updated_at": datetime.now().strftime(
                    "%Y-%m-%d %H:%M:%S"
                ),
            }

            _write_json(_PARAMS_FILE, params)

            return {
                "success": True,
                "key": key,
                "value": value,
                "path": _PARAMS_FILE,
                "message": f"参数已保存：{key}",
            }
        except Exception as exc:
            return {"success": False, "error": str(exc)}
