"""把发布目录打包为 bootstrapper 内嵌的 payload.zip。

用法：python tools/pack-payload.py <源目录> <输出 zip>
"""
import os
import sys
import zipfile

src = sys.argv[1] if len(sys.argv) > 1 else 'build/app'
out = sys.argv[2] if len(sys.argv) > 2 else 'src/MultiReplacer.Bootstrapper/payload.zip'

if not os.path.isdir(src):
    raise SystemExit('源目录不存在: ' + src)

files = []
for root, dirs, names in os.walk(src):
    for name in names:
        full = os.path.join(root, name)
        rel = os.path.relpath(full, src).replace(os.sep, '/')
        files.append((full, rel))
files.sort(key=lambda item: item[1])

os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
if os.path.exists(out):
    os.remove(out)

with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
    for full, rel in files:
        zf.write(full, rel)

total = sum(os.path.getsize(f) for f, _ in files)
size = os.path.getsize(out)
print('entries=%d raw=%.1fMB zip=%.1fMB' % (len(files), total / 1048576.0, size / 1048576.0))
