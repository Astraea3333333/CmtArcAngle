from __future__ import annotations

import argparse
import json
import math
from collections import deque
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw


def included_line_angle_deg(a: float, b: float) -> float:
    """Smallest included angle between two image-plane line directions."""
    delta = abs(a - b) % 180.0
    return 180.0 - delta if delta > 90.0 else delta


def connected_components(mask: np.ndarray, min_area: int = 80) -> list[np.ndarray]:
    h, w = mask.shape
    visited = np.zeros_like(mask, dtype=bool)
    components: list[np.ndarray] = []

    for yy in range(h):
        for xx in range(w):
            if not mask[yy, xx] or visited[yy, xx]:
                continue

            queue: deque[tuple[int, int]] = deque([(yy, xx)])
            visited[yy, xx] = True
            points: list[tuple[int, int]] = []

            while queue:
                cy, cx = queue.popleft()
                points.append((cy, cx))
                for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    ny, nx = cy + dy, cx + dx
                    if (
                        0 <= ny < h
                        and 0 <= nx < w
                        and mask[ny, nx]
                        and not visited[ny, nx]
                    ):
                        visited[ny, nx] = True
                        queue.append((ny, nx))

            if len(points) >= min_area:
                components.append(np.array(points, dtype=np.int32))

    return components


def select_component_near_root(
    components: list[np.ndarray],
    root_roi_xy: tuple[float, float],
) -> np.ndarray:
    rx, ry = root_roi_xy

    def score(comp: np.ndarray) -> float:
        # comp columns are [y, x]. Prefer components near the wire tip, with
        # slight preference for larger connected arc bodies.
        min_dist2 = np.min((comp[:, 0] - ry) ** 2 + (comp[:, 1] - rx) ** 2)
        return float(min_dist2) - 0.0002 * len(comp)

    return min(components, key=score)


def measure_arc_angle(
    image_path: Path,
    root_x: float,
    root_y: float,
    wire_angle: float,
    threshold: float,
    roi: tuple[int, int, int, int],
    fan_filter: bool = True,
) -> dict:
    image = Image.open(image_path).convert("RGB")
    gray = np.array(image.convert("L"), dtype=np.float32)

    x0, y0, x1, y1 = roi
    roi_gray = gray[y0:y1, x0:x1]
    mask = roi_gray > threshold
    components = connected_components(mask, min_area=80)
    if not components:
        raise RuntimeError("No arc component found. Lower threshold or adjust ROI.")

    root = np.array([root_x, root_y], dtype=np.float64)
    root_roi_xy = (root_x - x0, root_y - y0)
    component = select_component_near_root(components, root_roi_xy)

    y_pixels = component[:, 0] + y0
    x_pixels = component[:, 1] + x0

    if fan_filter:
        vectors = np.stack([x_pixels - root_x, y_pixels - root_y], axis=1)
        radii = np.linalg.norm(vectors, axis=1)
        angles = np.degrees(np.arctan2(vectors[:, 1], vectors[:, 0]))

        # CMT arc should mainly develop from the wire tip into the left/up/down fan.
        # This removes unrelated reflections while preserving the root-adjacent plume.
        keep = (
            ((angles < -35.0) | (angles > 95.0) | ((x_pixels < root_x + 70.0) & (y_pixels < root_y + 60.0)))
            & (radii > 8.0)
        )
        x_pixels = x_pixels[keep]
        y_pixels = y_pixels[keep]

    if len(x_pixels) < 20:
        raise RuntimeError("Too few arc pixels after filtering. Lower threshold or disable fan filter.")

    y_top_raw = float(np.min(y_pixels))
    y_bottom_raw = float(np.max(y_pixels))
    top_band = y_pixels <= y_top_raw + 4.0
    bottom_band = y_pixels >= y_bottom_raw - 4.0

    top_point = np.array(
        [float(np.median(x_pixels[top_band])), float(np.median(y_pixels[top_band]))],
        dtype=np.float64,
    )
    bottom_point = np.array(
        [float(np.median(x_pixels[bottom_band])), float(np.median(y_pixels[bottom_band]))],
        dtype=np.float64,
    )
    midpoint = (top_point + bottom_point) / 2.0

    axis_vector = midpoint - root
    axis_angle = math.degrees(math.atan2(float(axis_vector[1]), float(axis_vector[0])))
    beta = included_line_angle_deg(axis_angle, wire_angle)

    return {
        "image": str(image_path),
        "method": "gray_threshold_vertical_extreme_midpoint",
        "threshold": threshold,
        "roi": {"x0": x0, "y0": y0, "x1": x1, "y1": y1},
        "wire_tip": {"x": root_x, "y": root_y},
        "wire_angle_deg": wire_angle,
        "top_point": {"x": float(top_point[0]), "y": float(top_point[1])},
        "bottom_point": {"x": float(bottom_point[0]), "y": float(bottom_point[1])},
        "midpoint": {"x": float(midpoint[0]), "y": float(midpoint[1])},
        "arc_axis_angle_deg": axis_angle,
        "beta_deg": beta,
        "arc_pixels": int(len(x_pixels)),
    }


def draw_annotation(
    image_path: Path,
    result: dict,
    output_path: Path,
    sample_step_target: int = 12000,
) -> None:
    image = Image.open(image_path).convert("RGB")
    gray = np.array(image.convert("L"), dtype=np.float32)
    draw = ImageDraw.Draw(image)

    x0 = int(result["roi"]["x0"])
    y0 = int(result["roi"]["y0"])
    x1 = int(result["roi"]["x1"])
    y1 = int(result["roi"]["y1"])
    threshold = float(result["threshold"])
    root_x = float(result["wire_tip"]["x"])
    root_y = float(result["wire_tip"]["y"])
    wire_angle = float(result["wire_angle_deg"])

    roi_gray = gray[y0:y1, x0:x1]
    mask = roi_gray > threshold
    components = connected_components(mask, min_area=80)
    component = select_component_near_root(components, (root_x - x0, root_y - y0))
    y_pixels = component[:, 0] + y0
    x_pixels = component[:, 1] + x0

    vectors = np.stack([x_pixels - root_x, y_pixels - root_y], axis=1)
    radii = np.linalg.norm(vectors, axis=1)
    angles = np.degrees(np.arctan2(vectors[:, 1], vectors[:, 0]))
    keep = (
        ((angles < -35.0) | (angles > 95.0) | ((x_pixels < root_x + 70.0) & (y_pixels < root_y + 60.0)))
        & (radii > 8.0)
    )
    x_pixels = x_pixels[keep]
    y_pixels = y_pixels[keep]

    draw.rectangle((x0, y0, x1, y1), outline=(255, 255, 0), width=2)
    step = max(1, len(x_pixels) // sample_step_target)
    for x, y in zip(x_pixels[::step], y_pixels[::step]):
        draw.point((int(x), int(y)), fill=(255, 150, 0))

    top = result["top_point"]
    bottom = result["bottom_point"]
    mid = result["midpoint"]
    root = result["wire_tip"]

    for point in (top, bottom):
        x = int(round(point["x"]))
        y = int(round(point["y"]))
        draw.ellipse((x - 7, y - 7, x + 7, y + 7), outline=(0, 255, 255), width=3)

    draw.line(
        (
            int(round(top["x"])),
            int(round(top["y"])),
            int(round(bottom["x"])),
            int(round(bottom["y"])),
        ),
        fill=(0, 180, 255),
        width=2,
    )

    mx = int(round(mid["x"]))
    my = int(round(mid["y"]))
    draw.ellipse((mx - 7, my - 7, mx + 7, my + 7), outline=(255, 0, 255), width=3)

    rx = int(round(root["x"]))
    ry = int(round(root["y"]))
    draw.ellipse((rx - 7, ry - 7, rx + 7, ry + 7), outline=(255, 255, 0), width=3)
    draw.line((rx - 12, ry, rx + 12, ry), fill=(255, 255, 0), width=2)
    draw.line((rx, ry - 12, rx, ry + 12), fill=(255, 255, 0), width=2)

    draw.line((rx, ry, mx, my), fill=(255, 0, 0), width=4)

    wire_slope = math.tan(math.radians(wire_angle))
    xw = image.width - 20
    yw = root_y + wire_slope * (xw - root_x)
    draw.line((rx, ry, int(xw), int(yw)), fill=(0, 255, 0), width=3)

    draw.text(
        (30, 30),
        f"gray>{threshold:.0f}: beta={result['beta_deg']:.2f} deg",
        fill=(255, 255, 0),
    )

    output_path.parent.mkdir(parents=True, exist_ok=True)
    image.save(output_path)


def parse_roi(value: str) -> tuple[int, int, int, int]:
    parts = [int(part.strip()) for part in value.split(",")]
    if len(parts) != 4:
        raise argparse.ArgumentTypeError("ROI must be x0,y0,x1,y1")
    x0, y0, x1, y1 = parts
    if x1 <= x0 or y1 <= y0:
        raise argparse.ArgumentTypeError("ROI must satisfy x1>x0 and y1>y0")
    return x0, y0, x1, y1


def main() -> None:
    parser = argparse.ArgumentParser(
        description=(
            "Measure CMT arc angle using gray threshold, vertical extreme "
            "midpoint, and a wire-tip origin."
        )
    )
    parser.add_argument("image", type=Path, help="Input image path")
    parser.add_argument("--root-x", type=float, default=872.0, help="Wire tip x coordinate")
    parser.add_argument("--root-y", type=float, default=529.3, help="Wire tip y coordinate")
    parser.add_argument("--wire-angle", type=float, default=1.55, help="Wire axis angle in degrees")
    parser.add_argument("--threshold", type=float, default=210.0, help="Gray threshold for main arc")
    parser.add_argument("--roi", type=parse_roi, default=(670, 60, 1010, 700), help="ROI as x0,y0,x1,y1")
    parser.add_argument("--output", type=Path, default=None, help="Annotated image output path")
    parser.add_argument("--json", type=Path, default=None, help="JSON result output path")
    args = parser.parse_args()

    result = measure_arc_angle(
        image_path=args.image,
        root_x=args.root_x,
        root_y=args.root_y,
        wire_angle=args.wire_angle,
        threshold=args.threshold,
        roi=args.roi,
    )

    output_path = args.output
    if output_path is None:
        output_path = args.image.with_name(args.image.stem + "_arc_angle_measured.png")
    draw_annotation(args.image, result, output_path)

    json_path = args.json
    if json_path is None:
        json_path = args.image.with_name(args.image.stem + "_arc_angle_result.json")
    json_path.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")

    print(f"beta_deg = {result['beta_deg']:.3f}")
    print(f"annotated = {output_path}")
    print(f"json = {json_path}")


if __name__ == "__main__":
    main()
