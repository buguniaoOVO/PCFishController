#!/usr/bin/env python3
"""Extract the current PC FISH AtlasFish sprites into fish-icons.png/json.

Requires UnityPy on the build machine. Runtime code uses only the generated PNG
and JSON; it does not read or modify the game folder.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import UnityPy
from PIL import Image


def guid_key(guid, path_id):
    return (guid.data_0_, guid.data_1_, guid.data_2_, guid.data_3_, path_id)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--game-dir", required=True, type=Path)
    parser.add_argument("--output-dir", required=True, type=Path)
    args = parser.parse_args()

    assets = args.game_dir / "PCFish_Data" / "sharedassets0.assets"
    if not assets.is_file():
        raise SystemExit(f"Missing game asset file: {assets}")

    env = UnityPy.load(str(assets))
    atlas = None
    for obj in env.objects:
        if obj.type.name == "SpriteAtlas":
            data = obj.read()
            if data.m_Name == "AtlasFish":
                atlas = data
    if atlas is None:
        raise SystemExit("AtlasFish was not found")

    packed_names = set(atlas.m_PackedSpriteNamesToIndex or [])
    sprites = {}
    for obj in env.objects:
        if obj.type.name != "Sprite":
            continue
        sprite = obj.read()
        name = sprite.m_Name or ""
        # FishModel.Fish is the four-part code the controller receives.
        parts = name.split("_")
        if len(parts) != 4 or not name.startswith("FS"):
            continue
        if packed_names and name not in packed_names:
            continue
        sprites[name] = sprite.image.convert("RGBA")

    if len(sprites) < 100:
        raise SystemExit(f"Only {len(sprites)} fish sprites were mapped; refusing to write partial assets")

    args.output_dir.mkdir(parents=True, exist_ok=True)
    # SpriteAtlas textureRect uses Unity packing coordinates, which can include
    # rotation/flip metadata. Re-pack each decoded Sprite.image on a simple 64px
    # grid so runtime rectangles always point at the visible sprite pixels.
    cell = 64
    columns = 32
    rows = (len(sprites) + columns - 1) // columns
    image = Image.new("RGBA", (columns * cell, rows * cell), (0, 0, 0, 0))
    rects = {}
    for index, name in enumerate(sorted(sprites)):
        x = (index % columns) * cell
        y = (index // columns) * cell
        sprite = sprites[name]
        if sprite.width > cell or sprite.height > cell:
            raise SystemExit(f"Sprite exceeds the {cell}px packing cell: {name} {sprite.size}")
        image.alpha_composite(sprite, (x, y))
        rects[name] = [float(x), float(y), float(sprite.width), float(sprite.height)]
    image.save(args.output_dir / "fish-icons.png")
    index = {
        "source": "PCFish_Data/sharedassets0.assets / AtlasFish (decoded sprites repacked)",
        "width": int(image.width),
        "height": int(image.height),
        "count": len(rects),
        "rects": rects,
    }
    (args.output_dir / "fish-icons.json").write_text(
        json.dumps(index, ensure_ascii=False, separators=(",", ":")),
        encoding="utf-8",
    )
    print(f"Extracted and repacked {len(rects)} fish sprites from AtlasFish ({image.width}x{image.height})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
