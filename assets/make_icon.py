"""
生成 VisioView 图标（G 方案：立体折面）。

设计：深蓝圆角磁贴 + 金色 V。V 沿中缝分成左右两个明暗面，
左亮右暗，中间一条深色棱线；大尺寸再加一层斜向高光模拟金属反光。

小尺寸会主动简化（去掉渐变、棱线、高光），只保留结构性明暗对比——
16px 下那些细节本来就看不见，留着只会让图标发糊。

用法：python assets/make_icon.py
产出：VisioView.svg / VisioView.ico / VisioView-256.png
"""
import io
import os
import struct
from PIL import Image, ImageDraw, ImageChops

HERE = os.path.dirname(os.path.abspath(__file__))
LOGICAL = 72.0          # 逻辑坐标系：-36..36
SUPERSAMPLE = 4         # 超采样倍数，抗锯齿用

# 配色
BLUE = (24, 95, 165)    # 磁贴底 #185FA5
SEAM = (133, 79, 11)    # 棱线 #854F0B
LEFT_TOP = (253, 227, 160)
LEFT_BOTTOM = (232, 169, 59)
RIGHT_TOP = (201, 132, 37)
RIGHT_BOTTOM = (138, 74, 12)
HIGHLIGHT = (255, 246, 220, 120)

# V 轮廓（逻辑坐标）
V_POINTS = [(-26, -20), (-13, -20), (0, 6), (13, -20), (26, -20), (0, 32)]
# 斜向高光带，沿左臂方向
HI_POINTS = [(-32, -40), (-22, -40), (8, 40), (-2, 40)]


def hexs(c):
    return '#%02X%02X%02X' % c[:3]


def lerp(a, b, t):
    return tuple(round(x + (y - x) * t) for x, y in zip(a, b))


def render(size):
    """渲染指定像素尺寸，返回 RGBA 图。"""
    n = int(size * SUPERSAMPLE)
    k = n / LOGICAL

    def p(v):
        return (v + 36) * k

    def poly(pts):
        return [(p(x), p(y)) for x, y in pts]

    detail = 3 if size >= 128 else (2 if size >= 48 else 1)

    base = Image.new('RGBA', (n, n), (0, 0, 0, 0))
    ImageDraw.Draw(base).rounded_rectangle(
        [p(-36), p(-36), p(36), p(36)],
        radius=16 * k, fill=BLUE + (255,),
    )

    mask = Image.new('L', (n, n), 0)
    ImageDraw.Draw(mask).polygon(poly(V_POINTS), fill=255)

    face = Image.new('RGBA', (n, n), (0, 0, 0, 0))
    d = ImageDraw.Draw(face)
    mid = p(0)

    if detail == 1:
        # 16/32px：两个纯色面，只留结构性对比
        d.rectangle([0, 0, mid, n], fill=(250, 199, 117, 255))
        d.rectangle([mid, 0, n, n], fill=(186, 117, 23, 255))
    else:
        # 逐行画竖向渐变
        for y in range(n):
            t = y / max(1, n - 1)
            l = lerp(LEFT_TOP, LEFT_BOTTOM, t)
            r = lerp(RIGHT_TOP, RIGHT_BOTTOM, t)
            d.line([(0, y), (mid, y)], fill=l + (255,))
            d.line([(mid, y), (n, y)], fill=r + (255,))
        seam = max(1, round(3 * k)) if detail >= 3 else max(1, round(1.5 * k))
        d.rectangle([mid - seam / 2, 0, mid + seam / 2, n], fill=SEAM + (255,))

        if detail >= 3:
            hi = Image.new('RGBA', (n, n), (0, 0, 0, 0))
            ImageDraw.Draw(hi).polygon(poly(HI_POINTS), fill=HIGHLIGHT)
            left = Image.new('L', (n, n), 0)
            ImageDraw.Draw(left).rectangle([0, 0, mid, n], fill=255)
            hi.putalpha(ImageChops.multiply(hi.getchannel('A'), left))
            face = Image.alpha_composite(face, hi)

    base.paste(face, (0, 0), mask)
    return base.resize((size, size), Image.LANCZOS)


def write_ico(images, path):
    """手写 ICO 容器：每个尺寸嵌入各自的 PNG，避免从大图统一缩放。"""
    blobs = []
    for size, im in images:
        buf = io.BytesIO()
        im.save(buf, format='PNG', optimize=True)
        blobs.append((size, buf.getvalue()))

    out = io.BytesIO()
    out.write(struct.pack('<HHH', 0, 1, len(blobs)))
    offset = 6 + 16 * len(blobs)
    for size, data in blobs:
        b = 0 if size >= 256 else size
        out.write(struct.pack('<BBBBHHII', b, b, 0, 0, 1, 32, len(data), offset))
        offset += len(data)
    for _, data in blobs:
        out.write(data)
    with open(path, 'wb') as f:
        f.write(out.getvalue())


SVG = '''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 72 72" width="256" height="256">
  <defs>
    <linearGradient id="left" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#FDE3A0"/>
      <stop offset="1" stop-color="#E8A93B"/>
    </linearGradient>
    <linearGradient id="right" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#C98425"/>
      <stop offset="1" stop-color="#8A4A0C"/>
    </linearGradient>
    <clipPath id="v"><path d="M10 16 L23 16 L36 42 L49 16 L62 16 L36 68 Z"/></clipPath>
    <clipPath id="leftHalf"><rect x="0" y="0" width="36" height="72"/></clipPath>
  </defs>
  <rect width="72" height="72" rx="16" fill="#185FA5"/>
  <g clip-path="url(#v)">
    <rect x="0" y="0" width="36" height="72" fill="url(#left)"/>
    <rect x="36" y="0" width="36" height="72" fill="url(#right)"/>
    <rect x="34.5" y="0" width="3" height="72" fill="#854F0B"/>
    <g clip-path="url(#leftHalf)"><path d="M4 -4 L14 -4 L44 76 L34 76 Z" fill="#FFF6DC" opacity="0.47"/></g>
  </g>
</svg>
'''


def main():
    sizes = [16, 24, 32, 48, 64, 128, 256]
    images = [(s, render(s)) for s in sizes]

    with open(os.path.join(HERE, 'VisioView.svg'), 'w', encoding='utf-8') as f:
        f.write(SVG)
    images[-1][1].save(os.path.join(HERE, 'VisioView-256.png'))
    write_ico(images, os.path.join(HERE, 'VisioView.ico'))

    for s, _ in images:
        print('rendered', s)
    print('ico bytes', os.path.getsize(os.path.join(HERE, 'VisioView.ico')))


if __name__ == '__main__':
    main()
