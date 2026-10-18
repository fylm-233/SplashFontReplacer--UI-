import struct, zlib, os

W = 256

def rounded_rect(x, y, left, top, right, bottom, radius):
    if x < left or x > right or y < top or y > bottom:
        return False
    cx = min(max(x, left + radius), right - radius)
    cy = min(max(y, top + radius), bottom - radius)
    dx = x - cx
    dy = y - cy
    return dx * dx + dy * dy <= radius * radius

def lerp(a, b, t):
    return a + (b - a) * t

# 背景渐变色
TOP = (0x2B, 0x5C, 0xE6)
BOTTOM = (0x6C, 0x2C, 0xC8)
WHITE = (0xFF, 0xFF, 0xFF)

def triangle_left(x, y):
    # 指向左侧的三角，apex (58,128)，底边 x=92
    if x < 58 or x > 92:
        return False
    t = (x - 58) / (92 - 58.0)
    half = 26 * t
    return abs(y - 128) <= half

def triangle_right(x, y):
    if x < 164 or x > 198:
        return False
    t = (198 - x) / (198 - 164.0)
    half = 26 * t
    return abs(y - 128) <= half

def bar(x, y):
    return 86 <= x <= 170 and 118 <= y <= 138

def shade(px, py):
    # 超采样 3x3 抗锯齿
    acc_r = acc_g = acc_b = acc_a = 0.0
    S = 3
    for sy in range(S):
        for sx in range(S):
            x = px + (sx + 0.5) / S
            y = py + (sy + 0.5) / S
            r = g = b = a = 0.0
            if rounded_rect(x, y, 6, 6, 250, 250, 54):
                t = (y - 6) / 244.0
                r, g, b = (lerp(TOP[i], BOTTOM[i], t) for i in range(3))
                a = 255.0
                if bar(x, y) or triangle_left(x, y) or triangle_right(x, y):
                    r, g, b = WHITE
            acc_r += r; acc_g += g; acc_b += b; acc_a += a
    n = S * S
    return (int(acc_r / n), int(acc_g / n), int(acc_b / n), int(acc_a / n))

base = [shade(x, y) for y in range(W) for x in range(W)]

def downscale(px, size):
    if size == W:
        return px
    out = []
    scale = W // size
    for y in range(size):
        for x in range(size):
            r = g = b = a = 0
            for sy in range(scale):
                for sx in range(scale):
                    p = px[(y * scale + sy) * W + (x * scale + sx)]
                    r += p[0]; g += p[1]; b += p[2]; a += p[3]
            n = scale * scale
            out.append((r // n, g // n, b // n, a // n))
    return out

def png_bytes(pixels, size):
    raw = bytearray()
    for y in range(size):
        raw.append(0)
        for x in range(size):
            raw.extend(pixels[y * size + x])
    def chunk(tag, data):
        return (struct.pack('>I', len(data)) + tag + data +
                struct.pack('>I', zlib.crc32(tag + data) & 0xFFFFFFFF))
    ihdr = struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0)
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', ihdr) +
            chunk(b'IDAT', zlib.compress(bytes(raw), 9)) + chunk(b'IEND', b''))

sizes = [16, 24, 32, 48, 64, 128, 256]
entries = []
for s in sizes:
    entries.append((s, png_bytes(downscale(base, s), s)))

out = bytearray(struct.pack('<HHH', 0, 1, len(entries)))
offset = 6 + 16 * len(entries)
blobs = bytearray()
for s, data in entries:
    dim = 0 if s >= 256 else s
    out.extend(struct.pack('<BBBBHHII', dim, dim, 0, 0, 1, 32, len(data), offset))
    offset += len(data)
    blobs.extend(data)
out.extend(blobs)

path = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'app.ico')
with open(path, 'wb') as f:
    f.write(bytes(out))
print('written', path, len(out), 'bytes')
