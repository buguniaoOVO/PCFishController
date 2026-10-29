#!/usr/bin/env python3
"""Verify packaged S2 catalog and recipes against the installed Unity game assets."""

from __future__ import annotations

import argparse
import csv
import io
import json
from pathlib import Path

import UnityPy


S2_CODES = {"FS00038", "FS00039", "FS00040"}
NEW_CODES = {"FS00036", "FS00037", "FS00038", "FS00039", "FS00040", "FS00041", "FS00042", "FS00043"}


def text_asset(assets, name: str) -> str:
    for obj in assets.objects:
        if obj.type.name != "TextAsset":
            continue
        data = obj.read()
        if getattr(data, "m_Name", "") == name:
            content = data.m_Script
            return content.decode("utf-8-sig") if isinstance(content, bytes) else str(content)
    raise SystemExit(f"Missing TextAsset: {name}")


def parse_csv(value: str) -> list[dict[str, str]]:
    return list(csv.DictReader(io.StringIO(value)))


def expanded_recipe(recipe: dict, target: str, grade: int) -> list[tuple[str, int, int]]:
    result: list[tuple[str, int, int]] = []
    if grade > 1:
        result.append((target, grade - 1, 1))
    for ingredient in recipe["Ingredients"]:
        if grade > 1 and recipe.get("PreviousTargetReplacesCore", True) and ingredient.get("Role") == "核心鱼":
            continue
        star = ingredient["Star"] if ingredient.get("Role") == "核心鱼" or not recipe.get("RepeatSupportAtTargetStar", True) else grade
        result.append((ingredient["Type"], star, ingredient["Count"]))
    return sorted(result)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--game-dir", required=True, type=Path)
    parser.add_argument("--data", type=Path, default=Path(__file__).resolve().parents[1] / "PCFishController" / "Assets" / "seasonal-recipes.json")
    args = parser.parse_args()

    assets_path = args.game_dir / "PCFish_Data" / "resources.assets"
    assets = UnityPy.load(str(assets_path))
    game_types = parse_csv(text_asset(assets, "fish_type_v1.5"))
    game_recipes = parse_csv(text_asset(assets, "fish_season_recipe_v1.1"))
    packaged = json.loads(args.data.read_text(encoding="utf-8"))

    game_species = {
        row["type"]: (int(row["level"]), int(row["season"]))
        for row in game_types if row["type"] in NEW_CODES
    }
    packaged_species = {row["Code"]: (int(row["Tier"]), int(row["Season"])) for row in packaged["Fish"]}
    if game_species != {key: packaged_species.get(key) for key in game_species}:
        raise SystemExit(f"Fish type data differs: game={game_species}, packaged={packaged_species}")
    if NEW_CODES - packaged_species.keys():
        raise SystemExit(f"Packaged catalog lacks: {sorted(NEW_CODES - packaged_species.keys())}")

    actual: dict[str, list[tuple[int, str, int, int]]] = {target: [] for target in S2_CODES}
    for row in game_recipes:
        if int(row["season"]) != 2 or row["seasonFishType"] not in S2_CODES:
            continue
        actual[row["seasonFishType"]].append((int(row["seasonFishGrade"]), row["recipeFishType"],
                                               int(row["recipeFishGrade"]), int(row["recipeAmount"])))

    recipes = {row["Target"]: row for row in packaged["Recipes"]}
    if S2_CODES - recipes.keys():
        raise SystemExit(f"Packaged recipes lack: {sorted(S2_CODES - recipes.keys())}")
    checked = 0
    for target in S2_CODES:
        for grade in range(1, 6):
            expected = sorted((fish_type, star, count) for target_grade, fish_type, star, count in actual[target]
                              if target_grade == grade)
            generated = expanded_recipe(recipes[target], target, grade)
            if generated != expected:
                raise SystemExit(f"S2 {target} grade {grade} differs: game={expected}, packaged={generated}")
            checked += len(expected)

    print(f"PASS: {len(NEW_CODES)} fish catalog rows and {checked} S2 ingredient rows match the installed game assets.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
