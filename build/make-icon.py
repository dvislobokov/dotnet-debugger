"""Draws the extension icon (vscode/icon.png, 256x256) without any imaging library:
a rounded .NET-purple tile with a white "play" triangle and a red breakpoint dot."""
import struct
import zlib
from pathlib import Path

SIZE = 256
SCALE = 4  # supersampling for smooth edges


def inside_rounded_square(x, y, size, radius):
    cx = min(max(x, radius), size - radius)
    cy = min(max(y, radius), size - radius)
    return (x - cx) ** 2 + (y - cy) ** 2 <= radius ** 2


def inside_triangle(x, y, a, b, c):
    def side(p, q):
        return (x - q[0]) * (p[1] - q[1]) - (p[0] - q[0]) * (y - q[1])
    d1, d2, d3 = side(a, b), side(b, c), side(c, a)
    return not ((d1 < 0 or d2 < 0 or d3 < 0) and (d1 > 0 or d2 > 0 or d3 > 0))


def sample(x, y):
    """Colour (r, g, b, a) of one sub-pixel; coordinates in 0..1."""
    if not inside_rounded_square(x, y, 1.0, 0.18):
        return (0, 0, 0, 0)
    if (x - 0.27) ** 2 + (y - 0.30) ** 2 <= 0.105 ** 2:
        return (229, 57, 53, 255)      # breakpoint
    if inside_triangle(x, y, (0.40, 0.36), (0.40, 0.80), (0.80, 0.58)):
        return (255, 255, 255, 255)    # continue / run
    return (81, 43, 212, 255)          # .NET purple


rows = []
for py in range(SIZE):
    row = bytearray([0])  # filter type: none
    for px in range(SIZE):
        total = [0, 0, 0, 0]
        for sy in range(SCALE):
            for sx in range(SCALE):
                r, g, b, a = sample((px + (sx + 0.5) / SCALE) / SIZE, (py + (sy + 0.5) / SCALE) / SIZE)
                total[0] += r * a
                total[1] += g * a
                total[2] += b * a
                total[3] += a
        alpha = total[3] // (SCALE * SCALE)
        colour = [c // total[3] if total[3] else 0 for c in total[:3]]
        row += bytes(colour + [alpha])
    rows.append(bytes(row))


def chunk(kind, data):
    body = kind + data
    return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)


png = b"\x89PNG\r\n\x1a\n"
png += chunk(b"IHDR", struct.pack(">IIBBBBB", SIZE, SIZE, 8, 6, 0, 0, 0))
png += chunk(b"IDAT", zlib.compress(b"".join(rows), 9))
png += chunk(b"IEND", b"")

target = Path(__file__).resolve().parent.parent / "vscode" / "icon.png"
target.write_bytes(png)
print(f"{target} ({len(png)} bytes)")
