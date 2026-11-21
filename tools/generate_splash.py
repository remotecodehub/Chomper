"""Generate the deterministic 16-bit isometric Chomper splash artwork.

The project intentionally has no external art dependency for this asset. The
renderer paints a small pixel canvas with primitive geometry and scales it with
nearest-neighbor sampling, producing a crisp 1280x720 PNG.
"""

from __future__ import annotations

import random
import struct
import zlib
from pathlib import Path


LOW_WIDTH = 320
LOW_HEIGHT = 180
SCALE = 4
OUTPUT = Path(r"I:\dev\Chomper\src\Chomper.Core\Content\splash.png")

Color = tuple[int, int, int]
Point = tuple[int, int]

PALETTE: dict[str, Color] = {
    "ink": (8, 8, 24), "deep_navy": (16, 24, 64), "navy": (24, 40, 88),
    "wall": (32, 80, 144), "wall_highlight": (48, 176, 176),
    "floor": (24, 32, 72), "floor_light": (32, 48, 96),
    "white": (248, 240, 208), "green": (64, 184, 96), "green_dark": (24, 96, 64),
    "yellow": (248, 216, 72), "yellow_dark": (168, 112, 24),
    "blue": (56, 136, 216), "blue_dark": (24, 64, 144),
    "red": (224, 48, 56), "orange": (232, 120, 48), "pink": (216, 72, 136),
}


def new_canvas(width: int, height: int, color: Color) -> list[list[Color]]:
    """Create a mutable RGB canvas filled with one palette color."""
    return [[color for _ in range(width)] for _ in range(height)]


def set_pixel(canvas: list[list[Color]], x: int, y: int, color: Color) -> None:
    """Set one pixel when it falls inside the canvas bounds."""
    if 0 <= y < len(canvas) and 0 <= x < len(canvas[0]):
        canvas[y][x] = color


def fill_rect(canvas: list[list[Color]], x: int, y: int, width: int, height: int, color: Color) -> None:
    """Paint an axis-aligned rectangle with clipped integer coordinates."""
    for row in range(max(0, y), min(len(canvas), y + height)):
        for column in range(max(0, x), min(len(canvas[0]), x + width)):
            canvas[row][column] = color


def fill_polygon(canvas: list[list[Color]], points: list[Point], color: Color) -> None:
    """Fill a polygon with a scanline rasterizer suitable for pixel art."""
    if not points:
        return
    minimum_y = max(0, min(point[1] for point in points))
    maximum_y = min(len(canvas) - 1, max(point[1] for point in points))
    for y in range(minimum_y, maximum_y + 1):
        intersections: list[int] = []
        for index, first in enumerate(points):
            second = points[(index + 1) % len(points)]
            x1, y1 = first
            x2, y2 = second
            if y1 == y2:
                continue
            if min(y1, y2) <= y < max(y1, y2):
                intersections.append(x1 + (y - y1) * (x2 - x1) // (y2 - y1))
        intersections.sort()
        for index in range(0, len(intersections) - 1, 2):
            left = max(0, intersections[index])
            right = min(len(canvas[0]) - 1, intersections[index + 1])
            for x in range(left, right + 1):
                canvas[y][x] = color


def draw_line(canvas: list[list[Color]], start: Point, end: Point, color: Color) -> None:
    """Draw a one-pixel Bresenham line for crisp isometric edges."""
    x1, y1 = start
    x2, y2 = end
    dx = abs(x2 - x1)
    sx = 1 if x1 < x2 else -1
    dy = -abs(y2 - y1)
    sy = 1 if y1 < y2 else -1
    error = dx + dy
    while True:
        set_pixel(canvas, x1, y1, color)
        if x1 == x2 and y1 == y2:
            return
        doubled = 2 * error
        if doubled >= dy:
            error += dy
            x1 += sx
        if doubled <= dx:
            error += dx
            y1 += sy


def fill_circle(canvas: list[list[Color]], center: Point, radius: int, color: Color) -> None:
    """Fill a small circle using squared-distance checks."""
    center_x, center_y = center
    for y in range(center_y - radius, center_y + radius + 1):
        for x in range(center_x - radius, center_x + radius + 1):
            if (x - center_x) ** 2 + (y - center_y) ** 2 <= radius ** 2:
                set_pixel(canvas, x, y, color)


def iso_center(column: int, row: int) -> Point:
    """Map a maze cell to the center of its isometric diamond."""
    return 160 + (column - row) * 5, 69 + (column + row) * 3


def tile_points(column: int, row: int, lift: int = 0) -> list[Point]:
    """Return the four corners of an isometric tile at a chosen height."""
    center_x, center_y = iso_center(column, row)
    return [(center_x, center_y - 3 - lift), (center_x + 5, center_y - lift),
            (center_x, center_y + 3 - lift), (center_x - 5, center_y - lift)]


def create_wall_cells() -> set[tuple[int, int]]:
    """Build deterministic maze walls while leaving the ghost house open."""
    walls: set[tuple[int, int]] = set()
    columns, rows = 23, 15
    for column in range(columns):
        walls.add((column, 0)); walls.add((column, rows - 1))
    for row in range(rows):
        walls.add((0, row)); walls.add((columns - 1, row))
    segments = {
        3: list(range(2, 9)) + list(range(14, 21)),
        5: list(range(2, 6)) + list(range(7, 11)) + list(range(13, 17)) + list(range(18, 21)),
        9: list(range(2, 8)) + list(range(15, 21)),
        11: list(range(2, 10)) + list(range(13, 21)),
    }
    for row, columns_for_row in segments.items():
        for column in columns_for_row:
            walls.add((column, row))
    for row in range(5, 9):
        walls.add((5, row)); walls.add((17, row))
    for row in range(5, 10):
        for column in range(8, 15):
            walls.discard((column, row))
    return walls


def draw_background(canvas: list[list[Color]]) -> None:
    """Paint the deep-navy backdrop, scanline bands, and deterministic stars."""
    fill_rect(canvas, 0, 0, LOW_WIDTH, LOW_HEIGHT, PALETTE["ink"])
    fill_rect(canvas, 0, 0, LOW_WIDTH, 42, PALETTE["deep_navy"])
    fill_rect(canvas, 0, 42, LOW_WIDTH, 2, PALETTE["navy"])
    randomizer = random.Random(16016)
    for _ in range(42):
        x = randomizer.randrange(6, LOW_WIDTH - 6)
        y = randomizer.randrange(8, LOW_HEIGHT - 8)
        color = PALETTE["wall_highlight"] if randomizer.randrange(3) == 0 else PALETTE["blue_dark"]
        set_pixel(canvas, x, y, color)


def draw_maze(canvas: list[list[Color]], walls: set[tuple[int, int]]) -> None:
    """Render floor, raised walls, the open ghost house, dots, and power fruit."""
    for row in range(15):
        for column in range(23):
            floor_color = PALETTE["floor_light"] if (column + row) % 2 == 0 else PALETTE["floor"]
            fill_polygon(canvas, tile_points(column, row), floor_color)
    for row in range(15):
        for column in range(23):
            if (column, row) not in walls:
                continue
            top = tile_points(column, row, 3); bottom = tile_points(column, row)
            fill_polygon(canvas, [bottom[3], bottom[2], top[2], top[3]], PALETTE["blue_dark"])
            fill_polygon(canvas, [bottom[1], bottom[2], top[2], top[1]], PALETTE["wall"])
            fill_polygon(canvas, top, PALETTE["wall_highlight"])
            draw_line(canvas, top[0], top[1], PALETTE["white"])
            draw_line(canvas, top[1], top[2], PALETTE["wall"])
    house_outline = [(121, 90), (160, 67), (199, 90), (160, 113)]
    for index, point in enumerate(house_outline):
        draw_line(canvas, point, house_outline[(index + 1) % len(house_outline)], PALETTE["pink"])
    randomizer = random.Random(16016)
    reserved = {(10, 7), (12, 7), (14, 7), (18, 8), (20, 10), (11, 6), (19, 5)}
    for row in range(1, 14):
        for column in range(1, 22):
            if (column, row) in walls or (column, row) in reserved or (8 <= column <= 14 and 5 <= row <= 9):
                continue
            if randomizer.random() < 0.68:
                fill_circle(canvas, iso_center(column, row), 1, PALETTE["white"])
    for column, row in [(3, 2), (19, 2), (3, 12), (19, 12), (7, 7)]:
        fill_circle(canvas, iso_center(column, row), 2, PALETTE["orange"])
        fill_circle(canvas, iso_center(column, row), 1, PALETTE["yellow"])


def draw_ghost(canvas: list[list[Color]], center: Point, body: Color, shadow: Color) -> None:
    """Draw a compact two-tone ghost sprite with a readable 16-bit silhouette."""
    x, y = center
    fill_rect(canvas, x - 5, y + 4, 11, 2, PALETTE["ink"])
    fill_rect(canvas, x - 5, y - 4, 10, 8, body)
    fill_rect(canvas, x - 4, y - 5, 8, 1, body); fill_rect(canvas, x - 3, y - 6, 6, 1, body)
    fill_rect(canvas, x - 4, y + 4, 2, 2, body); fill_rect(canvas, x, y + 4, 2, 2, shadow)
    fill_rect(canvas, x + 3, y + 4, 2, 2, body)
    fill_rect(canvas, x - 3, y - 2, 2, 3, PALETTE["white"]); fill_rect(canvas, x + 2, y - 2, 2, 3, PALETTE["white"])
    fill_rect(canvas, x - 2, y - 1, 1, 2, PALETTE["ink"]); fill_rect(canvas, x + 3, y - 1, 1, 2, PALETTE["ink"])


def draw_chomper(canvas: list[list[Color]], center: Point, body: Color, mouth: Color) -> None:
    """Draw an open-mouth Chomper sprite and its pixel shadow."""
    x, y = center
    fill_rect(canvas, x - 6, y + 5, 13, 2, PALETTE["ink"])
    fill_circle(canvas, (x, y), 6, body)
    fill_polygon(canvas, [(x, y), (x + 7, y - 4), (x + 7, y + 4)], mouth)
    fill_rect(canvas, x - 2, y - 3, 2, 2, PALETTE["white"]); fill_rect(canvas, x - 1, y - 3, 1, 1, PALETTE["ink"])


FONT: dict[str, tuple[str, ...]] = {
    "C": ("1111", "1000", "1000", "1000", "1000", "1000", "1111"),
    "H": ("1001", "1001", "1001", "1111", "1001", "1001", "1001"),
    "O": ("0110", "1001", "1001", "1001", "1001", "1001", "0110"),
    "M": ("10001", "11011", "10101", "10101", "10001", "10001", "10001"),
    "P": ("1110", "1001", "1001", "1110", "1000", "1000", "1000"),
    "E": ("1111", "1000", "1000", "1110", "1000", "1000", "1111"),
    "R": ("1110", "1001", "1001", "1110", "1010", "1001", "1001"),
}


def draw_text(canvas: list[list[Color]], text: str, origin: Point, scale: int, color: Color) -> None:
    """Render the title with a tiny built-in bitmap font to avoid font assets."""
    x, y = origin
    for character in text:
        glyph = FONT[character]
        for row, pattern in enumerate(glyph):
            for column, value in enumerate(pattern):
                if value == "1":
                    fill_rect(canvas, x + column * scale, y + row * scale, scale, scale, color)
        x += (len(glyph[0]) + 1) * scale


def upscale(canvas: list[list[Color]], scale: int) -> list[list[Color]]:
    """Scale a pixel canvas with nearest-neighbor sampling."""
    result: list[list[Color]] = []
    for row in canvas:
        expanded_row = [pixel for pixel in row for _ in range(scale)]
        for _ in range(scale):
            result.append(expanded_row.copy())
    return result


def png_chunk(kind: bytes, payload: bytes) -> bytes:
    """Encode one PNG chunk with its length and CRC."""
    return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", zlib.crc32(kind + payload) & 0xFFFFFFFF)


def write_png(path: Path, canvas: list[list[Color]]) -> None:
    """Write an RGB PNG using only Python's standard library."""
    height = len(canvas); width = len(canvas[0])
    scanlines = b"".join(b"\x00" + bytes(channel for pixel in row for channel in pixel) for row in canvas)
    header = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    png = b"\x89PNG\r\n\x1a\n" + png_chunk(b"IHDR", header) + png_chunk(b"IDAT", zlib.compress(scanlines, 9)) + png_chunk(b"IEND", b"")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(png)


def main() -> None:
    """Compose and save the final deterministic Chomper splash."""
    canvas = new_canvas(LOW_WIDTH, LOW_HEIGHT, PALETTE["ink"])
    draw_background(canvas)
    draw_maze(canvas, create_wall_cells())
    draw_ghost(canvas, iso_center(10, 7), PALETTE["green"], PALETTE["green_dark"])
    draw_ghost(canvas, iso_center(12, 7), PALETTE["yellow"], PALETTE["yellow_dark"])
    draw_ghost(canvas, iso_center(14, 7), PALETTE["blue"], PALETTE["blue_dark"])
    draw_chomper(canvas, iso_center(18, 8), PALETTE["red"], PALETTE["ink"])
    draw_chomper(canvas, iso_center(20, 10), PALETTE["orange"], PALETTE["ink"])
    draw_text(canvas, "CHOMPER", (104, 10), 2, PALETTE["orange"])
    write_png(OUTPUT, upscale(canvas, SCALE))


if __name__ == "__main__":
    main()
