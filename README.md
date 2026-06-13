# CMT Arc Angle

VB.NET console program for measuring the CMT arc angle from welding images.

## Method

The current measurement definition is:

1. Use the wire tip as the arc root.
2. Use the wire axis as the zero-degree reference.
3. Segment the main arc with a gray threshold.
4. Find the topmost and bottommost vertical extremes of the segmented arc.
5. Use the midpoint of those two extremes.
6. Connect the wire tip to that midpoint as the arc center axis.
7. Report the included angle `beta` between the arc axis and the wire axis.

## Build

```bat
dotnet build
```

## Run

```bat
dotnet run -- "D:\path\image.bmp" --threshold 210 --root-x 872 --root-y 529.3 --wire-angle 1.55 --roi 670,60,1010,700
```

Or after build:

```bat
bin\Debug\net8.0-windows\CmtArcAngle.exe "D:\path\image.bmp"
```

## Outputs

For each input image, the program writes:

- `*_arc_angle_measured_vbnet.png`: annotated image
- `*_arc_angle_result_vbnet.json`: measurement result

## Parameters

| Parameter | Meaning |
|---|---|
| `--threshold` | Gray threshold for main arc segmentation. |
| `--root-x` | Wire tip x coordinate. |
| `--root-y` | Wire tip y coordinate. |
| `--wire-angle` | Wire axis angle in degrees. |
| `--roi` | Analysis region as `x0,y0,x1,y1`. |
| `--output` | Optional annotated image output path. |
| `--json` | Optional JSON output path. |

Default parameters are tuned for the sample image used during development:

```text
threshold = 210
root_x = 872
root_y = 529.3
wire_angle = 1.55
roi = 670,60,1010,700
```

## Python Version

A Python implementation of the same algorithm is also included:

```text
python/cmt_arc_angle.py
```

Run it with:

```bat
python python\cmt_arc_angle.py "D:\path\image.bmp" --threshold 210 --root-x 872 --root-y 529.3 --wire-angle 1.55 --roi 670,60,1010,700
```

The VB.NET and Python versions use the same measurement definition and should produce the same result when the same threshold, ROI, wire-tip coordinates, and wire-axis angle are used.
