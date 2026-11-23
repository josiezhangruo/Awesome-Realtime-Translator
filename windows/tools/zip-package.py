#!/usr/bin/env python3
"""把 package.ps1 产出的目录压成可以直接发布的 zip。

为什么不直接用 zip(1)：macOS 自带的 zip 不设 UTF-8 文件名标志位（通用标志
第 11 位），于是「使用说明.txt」在 Windows 资源管理器里会显示成乱码——
偏偏那是唯一一个告诉用户怎么用的文件。0.1.0 和 0.1.1 都中了这一枪。

Python 的 zipfile 对非 ASCII 名字会自动置位，顺带还能保证不夹带
__MACOSX/ 和 .DS_Store。在 Windows 上跑 package.ps1 不需要这一步，
Compress-Archive 用的 .NET ZipArchive 本来就是对的。

    python3 tools/zip-package.py publish/AwesomeRealtimeTranslator-0.1.2-win-x64
"""

import sys
import zipfile
from pathlib import Path

SKIP = {".DS_Store"}


def main() -> int:
    if len(sys.argv) != 2:
        print(__doc__)
        return 2

    stage = Path(sys.argv[1]).resolve()
    if not stage.is_dir():
        print(f"不是目录：{stage}")
        return 1

    # 不能用 with_suffix：目录名里带版本号，".2-win-x64" 会被当成扩展名换掉
    out = stage.parent / (stage.name + ".zip")
    out.unlink(missing_ok=True)

    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, allowZip64=True) as z:
        for path in sorted(stage.rglob("*")):
            if path.name in SKIP:
                continue
            z.write(path, stage.name / path.relative_to(stage))

    print(f"打好了：{out} （{out.stat().st_size:,} 字节）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
