#!/usr/bin/env python3
"""
Draws the macOS icons from the Windows tray icon design (WhisperLocal/Ui/TrayIcons.cs):
five waveform bars on a dark rounded square.

  WhisperLocal.Mac/Resources/AppIcon.icns          app icon (Finder, Dock), macOS icon grid
  WhisperLocal.Mac/Resources/MenuBarIcon.png       menu bar template image (bars only, 36 px = 18 pt @2x)
  WhisperLocal.Mac/Resources/MenuBarIconOff.png    the same, faded (dictation disabled)
  WhisperLocal.Mac/Resources/MenuBarIconRecording.png / MenuBarIconProcessing.png
                                                   coloured (not template) bars: the menu bar ignores tint colours

Requires Pillow (pip install pillow). Run from anywhere: python3 scripts/make-mac-icons.py
"""
import io
import struct
from pathlib import Path

from PIL import Image, ImageDraw

# Same relative bar heights as TrayIcons.BarHeights
BAR_HEIGHTS = [0.4, 0.75, 1.0, 0.6, 0.35]
BACKGROUND = (32, 33, 36, 255)  # TrayIcons.Idle
OUT = Path(__file__).resolve().parent.parent / "WhisperLocal.Mac" / "Resources"
SUPERSAMPLE = 4


def draw_bars(draw, box, color, bar_ratio=0.11, gap_ratio=0.065, height_ratio=0.66):
    """Bars sized relative to box (x, y, size) exactly like TrayIcons.Make."""
    x0, y0, size = box
    bar = size * bar_ratio
    gap = size * gap_ratio
    total = len(BAR_HEIGHTS) * bar + (len(BAR_HEIGHTS) - 1) * gap
    x = x0 + (size - total) / 2
    for h in BAR_HEIGHTS:
        height = max(bar, size * height_ratio * h)
        y = y0 + (size - height) / 2
        draw.rounded_rectangle((x, y, x + bar, y + height), radius=bar / 2, fill=color)
        x += bar + gap


def app_icon(px):
    """macOS Big Sur+ grid: an 824/1024 rounded square, centred, with a soft shadow-free edge."""
    s = px * SUPERSAMPLE
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    body = s * 824 / 1024
    off = (s - body) / 2
    d.rounded_rectangle((off, off, off + body, off + body), radius=body * 0.225, fill=BACKGROUND)
    draw_bars(d, (off, off, body), (255, 255, 255, 255))
    return img.resize((px, px), Image.LANCZOS)


def menu_bar_icon(px, color):
    """Template images use only the alpha channel (macOS colours them for light/dark menu bars)."""
    s = px * SUPERSAMPLE
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    # Wider, taller bars than on the filled tray icon: there is no background square around them
    draw_bars(ImageDraw.Draw(img), (0, 0, s), color, bar_ratio=0.13, gap_ratio=0.075, height_ratio=0.9)
    return img.resize((px, px), Image.LANCZOS)


def png_bytes(img):
    buf = io.BytesIO()
    img.save(buf, "PNG")
    return buf.getvalue()


def write_icns(path):
    # PNG-based icns entries (macOS 10.7+): type → pixel size
    entries = [("icp4", 16), ("icp5", 32), ("icp6", 64), ("ic07", 128), ("ic08", 256), ("ic09", 512),
               ("ic10", 1024), ("ic11", 32), ("ic12", 64), ("ic13", 256), ("ic14", 512)]
    cache = {}
    chunks = b""
    for kind, px in entries:
        if px not in cache:
            cache[px] = png_bytes(app_icon(px))
        data = cache[px]
        chunks += kind.encode("ascii") + struct.pack(">I", len(data) + 8) + data
    path.write_bytes(b"icns" + struct.pack(">I", len(chunks) + 8) + chunks)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    write_icns(OUT / "AppIcon.icns")
    menu_bar_icon(36, (0, 0, 0, 255)).save(OUT / "MenuBarIcon.png")
    menu_bar_icon(36, (0, 0, 0, 110)).save(OUT / "MenuBarIconOff.png")
    # macOS systemRed / systemOrange, as on the Windows tray icon's recording/processing states
    menu_bar_icon(36, (255, 59, 48, 255)).save(OUT / "MenuBarIconRecording.png")
    menu_bar_icon(36, (255, 149, 0, 255)).save(OUT / "MenuBarIconProcessing.png")
    print(f"Written to {OUT}")


if __name__ == "__main__":
    main()
