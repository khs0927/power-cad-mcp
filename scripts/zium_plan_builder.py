"""Build a ZIUM 1/80 floor-plan sheet as cad_create entities.

Layout and drawing forms come from docs/standards/zium_plan_sheet.json (measured from 건축,구조1 S16);
the building itself (grid, openings, rooms, texts, sheet origin) comes from a small spec JSON.

    python scripts/zium_plan_builder.py examples/zium_plan_test.json --out plan.json

The output is a list of entities for cad_create (split into chunks of 200 if needed).
Before sending it: check that the active document is the intended one, run dry_run first, keep the
returned handles so the sheet can be removed exactly.
"""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

from shapely.geometry import MultiPolygon, box
from shapely.ops import unary_union

ROOT = Path(__file__).resolve().parents[1]
STD_PATH = ROOT / "docs" / "standards" / "zium_plan_sheet.json"


def arc_points(p, q, bulge, n=5):
    """Sample a bulge segment p->q (positive = counter-clockwise) as n points, q included."""
    if abs(bulge) < 1e-9:
        return [q]
    th = 4 * math.atan(bulge)
    dx, dy = q[0] - p[0], q[1] - p[1]
    c = math.hypot(dx, dy)
    r = c / (2 * abs(math.sin(th / 2)))
    d = c / (2 * math.tan(th / 2))
    cx, cy = (p[0] + q[0]) / 2 - dy / c * d, (p[1] + q[1]) / 2 + dx / c * d
    a0 = math.atan2(p[1] - cy, p[0] - cx)
    return [(cx + r * math.cos(a0 + th * i / n), cy + r * math.sin(a0 + th * i / n)) for i in range(1, n + 1)]


class Builder:
    def __init__(self, std: dict, spec: dict):
        self.s, self.p = std, spec
        self.ox, self.oy = spec["sheet_origin"]
        self.std_layers = spec.get("use_standard_layers", False)
        self.E: list[dict] = []

    # --- primitives -------------------------------------------------------
    def L(self, node: dict) -> str:
        return node.get("standard_layer", node["layer"]) if self.std_layers else node["layer"]

    def P(self, x, y):
        return [round(self.ox + x, 2), round(self.oy + y, 2)]

    def line(self, a, b, layer, **k):
        self.E.append(dict(type="line", layer=layer, start=self.P(*a), end=self.P(*b), **k))

    def poly(self, pts, layer, closed=True, **k):
        self.E.append(
            dict(type="polyline", layer=layer, closed=closed, points=[self.P(*q) for q in pts], **k)
        )

    def text(self, t, xy, h, layer, just="left"):
        if t:
            self.E.append(
                dict(
                    type="text",
                    text=t,
                    position=self.P(*xy),
                    height=h,
                    layer=layer,
                    justify=just,
                    style=self.s["text_style"],
                )
            )

    @staticmethod
    def rect(x0, y0, x1, y1):
        return [(x0, y0), (x1, y0), (x1, y1), (x0, y1)]

    # --- sheet frame ------------------------------------------------------
    def sheet(self):
        s, p = self.s, self.p
        sh = s["sheet"]
        self.E.append(
            dict(type="insert", name=sh["block"], position=self.P(0, 0), scale=sh["scale"], layer=sh["layer"])
        )
        tc = s["title_column"]
        lay = self.L(tc)
        self.text(p["title"], tc["name"]["position"], tc["name"]["height"], lay)
        self.text(p.get("date", ""), tc["date"]["position"], tc["date"]["height"], lay)
        self.text(tc["scale"]["text"], tc["scale"]["position"], tc["scale"]["height"], lay)
        self.text(p["number"], tc["number"]["position"], tc["number"]["height"], lay)
        t = s["title"]
        self.E.append(
            dict(
                type="circle",
                layer=t["layer"],
                center=self.P(*t["circle"]["center"]),
                radius=t["circle"]["radius"],
            )
        )
        self.line(*t["rule_line"], t["layer"])
        self.text(
            t["number_top"]["text"],
            t["number_top"]["position"],
            t["number_top"]["height"],
            t["layer"],
            "middle",
        )
        self.text(
            p["number"], t["number_bottom"]["position"], t["number_bottom"]["height"], t["layer"], "middle"
        )
        self.text(p["title"], t["name"]["position"], t["name"]["height"], t["layer"])
        self.text(
            t["scale_text"]["format"], t["scale_text"]["position"], t["scale_text"]["height"], t["layer"]
        )
        x0, y0, x1, y1 = s["plan_boundary"]
        for off in s["guides"]["offsets"]:
            self.poly(self.rect(x0 - off, y0 - off, x1 + off, y1 + off), s["guides"]["layer"])
        na = s["north_arrow"]
        lay = self.L(na)
        (cx, cy), r = na["center"], na["radius"]
        self.E.append(dict(type="circle", layer=lay, center=self.P(cx, cy), radius=r))
        tri = [(cx, cy + r), (cx + 230, cy - r * 0.7), (cx, cy - r * 0.35), (cx - 230, cy - r * 0.7)]
        self.poly(tri, lay)
        self.E.append(
            dict(
                type="hatch",
                layer=lay,
                pattern="SOLID",
                color=na["fill_color"],
                points=[self.P(*q) for q in (tri[0], tri[2], tri[3])],
            )
        )
        self.text("N", (cx, cy + r + 180), 200, lay, "middle")

    # --- grid ---------------------------------------------------------------
    def grid(self):
        g, X, Y = self.s["grid"], self.p["grid"]["x"], self.p["grid"]["y"]
        x0, y0, x1, y1 = self.s["plan_boundary"]
        for x in X:
            self.line((x, y1), (x, y0), g["line_layer"])
        for y in Y:
            self.line((x0, y), (x1, y), g["line_layer"])
        b = g["bubble"]
        bl = self.L(b)
        tl = self.L(b["label"])
        h = b["label"]["height"]
        o = b["label_offset"]
        for i, x in enumerate(X):
            self.E.append(
                dict(
                    type="insert",
                    name=b["block"],
                    position=self.P(x, b["top_y"]),
                    rotation=b["top_rotation"],
                    scale=b["scale"],
                    layer=bl,
                )
            )
            self.text(f"X{i + 1}", (x, b["top_y"] + o), h, tl, "middle")
        for i, y in enumerate(Y):
            self.E.append(
                dict(
                    type="insert",
                    name=b["block"],
                    position=self.P(b["left_x"], y),
                    rotation=b["left_rotation"],
                    scale=b["scale"],
                    layer=bl,
                )
            )
            self.text(f"Y{i + 1}", (b["left_x"] - o, y), h, tl, "middle")

    # --- walls, insulation, openings ---------------------------------------
    def faces(self):
        X, Y, t = self.p["grid"]["x"], self.p["grid"]["y"], self.s["walls"]["exterior_thickness"] / 2
        return X[0] - t, X[-1] + t, Y[0] - t, Y[-1] + t

    def walls(self):
        w = self.s["walls"]
        W0, E0, S0, N0 = self.faces()
        th = w["exterior_thickness"]
        cw, cd = w["column"]["size"]
        ring = box(W0, S0, E0, N0).difference(box(W0 + th, S0 + th, E0 - th, N0 - th))
        cols = [
            box(W0, S0, W0 + cw, S0 + cd),
            box(E0 - cw, S0, E0, S0 + cd),
            box(W0, N0 - cd, W0 + cw, N0),
            box(E0 - cw, N0 - cd, E0, N0),
        ]
        for x in self.p["grid"]["x"][1:-1]:
            cols += [box(x - cw / 2, S0, x + cw / 2, S0 + cd), box(x - cw / 2, N0 - cd, x + cw / 2, N0)]
        cuts = []
        for o in self.p["openings"]:
            a, b = o["from"], o["to"]
            cuts.append(
                {
                    "S": box(a, S0 - 1, b, S0 + th + 1),
                    "N": box(a, N0 - th - 1, b, N0 + 1),
                    "W": box(W0 - 1, a, W0 + th + 1, b),
                    "E": box(E0 - th - 1, a, E0 + 1, b),
                }[o["side"]]
            )
        conc = (
            unary_union([ring] + cols).difference(unary_union(cuts)) if cuts else unary_union([ring] + cols)
        )
        fill = w["fill"]
        for g in conc.geoms if isinstance(conc, MultiPolygon) else [conc]:
            pts = [(round(x, 1), round(y, 1)) for x, y in list(g.exterior.coords)[:-1]]
            self.poly(pts, self.L(w))
            self.E.append(
                dict(
                    type="hatch",
                    layer=self.L(fill),
                    pattern=fill["pattern"],
                    color=fill["color"],
                    points=[self.P(*q) for q in pts],
                )
            )
        pw = w["partition"]
        half = pw["thickness"] / 2
        for part in self.p.get("partitions", []):
            x = part["x"]
            y_from, y_to = part.get("from", S0 + cd), part.get("to", N0 - cd)
            d = part.get("door")
            spans = (
                [(y_from, y_to)]
                if not d
                else [(y_from, d["at"]), (d["at"] + self.s["door"]["room_door_block"]["width"], y_to)]
            )
            for a, b in spans:
                self.poly(self.rect(x - half, a, x + half, b), self.L(pw))
            if d:  # block local: opening along x 100..680, wall face at local y 448 -> rotate 90 swings +X
                blk = self.s["door"]["room_door_block"]
                self.E.append(
                    dict(
                        type="insert",
                        name=blk["name"],
                        position=self.P(x + half + 448, d["at"] - 100),
                        rotation=90,
                        layer=self.L(blk),
                    )
                )

    def edges(self):
        W0, E0, S0, N0 = self.faces()
        return {
            "S": ((W0, S0), (E0, S0), (0, -1)),
            "E": ((E0, S0), (E0, N0), (1, 0)),
            "N": ((E0, N0), (W0, N0), (0, 1)),
            "W": ((W0, N0), (W0, S0), (-1, 0)),
        }

    def band(self, side):
        ins = self.s["insulation"]["bands"]
        key = self.p.get("bands", {}).get(side, "default")
        return ins[key]["band"], ins[key]["period"]

    def wave(self, length, band, period):
        m = self.s["insulation"]["motif"]
        k, per = band / m["band"], period
        verts, s0 = [], 0.0
        while s0 < length - 1e-6:
            for d, s, b in m["vertices"]:
                verts.append((s0 + s * per / m["period"], d * k, b))
            s0 += per
        verts.append((s0, band, 0))
        verts = [v for v in verts if v[0] <= length + 1e-6]
        out = [(verts[0][0], verts[0][1])]
        for v0, v1 in zip(verts, verts[1:], strict=False):
            out += arc_points(v0[:2], v1[:2], v0[2])
        return out

    def insulation(self):
        ins = self.s["insulation"]
        for side, (a, b, n) in self.edges().items():
            L = math.dist(a, b)
            ux, uy = (b[0] - a[0]) / L, (b[1] - a[1]) / L
            base = a[0] if ux else a[1]
            sgn = ux or uy
            cuts = sorted(
                tuple(sorted(((o["from"] - base) * sgn, (o["to"] - base) * sgn)))
                for o in self.p["openings"]
                if o["side"] == side
            )
            spans, s = [], 0.0
            for c0, c1 in cuts:
                spans.append((s, c0))
                s = c1
            spans.append((s, L))
            w, per = self.band(side)
            for s0, s1 in spans:
                p0 = (a[0] + ux * s0, a[1] + uy * s0)
                p1 = (a[0] + ux * s1, a[1] + uy * s1)
                pts = [
                    (p0[0] + ux * s + n[0] * d, p0[1] + uy * s + n[1] * d)
                    for s, d in self.wave(s1 - s0, w, per)
                ]
                self.poly(pts, ins["layer"], closed=False)
                self.line(
                    (p0[0] + n[0] * w, p0[1] + n[1] * w),
                    (p1[0] + n[0] * w, p1[1] + n[1] * w),
                    ins["finish_layer"],
                )
                for q in (p0, p1):
                    self.line(q, (q[0] + n[0] * w, q[1] + n[1] * w), ins["finish_layer"])
        W0, E0, S0, N0 = self.faces()
        for (cx, cy), (sx, sy), (hs, vs) in (
            ((W0, S0), (-1, -1), ("W", "S")),
            ((E0, S0), (1, -1), ("E", "S")),
            ((E0, N0), (1, 1), ("E", "N")),
            ((W0, N0), (-1, 1), ("W", "N")),
        ):
            wx, wy = self.band(hs)[0], self.band(vs)[0]
            self.line((cx + sx * wx, cy), (cx + sx * wx, cy + sy * wy), ins["finish_layer"])
            self.line((cx, cy + sy * wy), (cx + sx * wx, cy + sy * wy), ins["finish_layer"])

    def openings(self):
        win, door = self.s["window"], self.s["door"]["entrance"]
        E = self.edges()
        for o in self.p["openings"]:
            side, a, b = o["side"], o["from"], o["to"]
            (sx, sy), _, n = E[side]
            w = self.band(side)[0]

            def Q(s, d, sx=sx, sy=sy, n=n, side=side):
                return (s, sy + n[1] * d) if side in "SN" else (sx + n[0] * d, s)

            sill = win["sill_lines"]
            for d in (w, sill["d"][1]):
                self.line(Q(a, d), Q(b, d), self.L(sill))
            if o["kind"] == "window":
                fe = win["frame_ends"]
                al = fe["along"]
                d0, d1 = fe["d"]
                for s0, s1 in ((a, a + al), (b - al, b)):
                    self.poly([Q(s0, d0), Q(s1, d0), Q(s1, d1), Q(s0, d1)], self.L(fe))
                for key in ("frame_lines", "glass_lines"):
                    nd = win[key]
                    i = nd["inset"]
                    for d in nd["d"]:
                        self.line(Q(a + i, d), Q(b - i, d), self.L(nd))
            else:
                fe = door["frame_ends"]
                al = fe["along"]
                d0, d1 = fe["d"]
                for s0, s1 in ((a, a + al), (b - al, b)):
                    self.poly([Q(s0, d0), Q(s1, d0), Q(s1, d1), Q(s0, d1)], self.L(fe))
                lv = door["leaves"]
                m = (a + b) / 2
                ov = lv["overlap"]
                for (s0, s1), d in zip(((a + al, m + ov), (m - ov, b - al)), lv["d"], strict=False):
                    for dd in (d, d - lv["thickness"]):
                        self.line(Q(s0, dd), Q(s1, dd), lv["layer"], color=lv["color"])

    # --- annotation and dimensions -----------------------------------------
    def annotation(self):
        an, p = self.s["annotation"], self.p
        lu = an["legal_use"]
        for r in p.get("rooms", []):
            x, y = r["at"]
            self.text(r["legal"], (x, y + lu["line_gap"] / 2), lu["height"], self.L(lu), "middle")
            self.text(r["use"], (x, y - lu["line_gap"] / 2), lu["height"], self.L(lu), "middle")
        bn = an["boxed_name"]
        bw, bh = bn["box"]
        for r in p.get("boxed_names", []):
            x, y = r["at"]
            self.text(r["text"], (x, y), bn["height"], bn["layer"], "middle")
            self.poly(self.rect(x - bw / 2, y - bh / 2, x + bw / 2, y + bh / 2), bn["layer"])
        if "entrance" in p:
            en, br = an["entrance"], an["braille"]
            ex, ey = p["entrance"]["text_at"]
            self.text(en["text"], (ex, ey), en["height"], self.L(en), "middle")
            bx, by = br["block_origin_offset"]
            tx0, ty0 = p["entrance"]["braille_at"]
            for i in range(br["count"]):
                self.E.append(
                    dict(
                        type="insert",
                        name=br["block"],
                        position=self.P(tx0 + br["tile"] * i - bx, ty0 - by),
                        layer=self.L(br),
                    )
                )
        if "site_margin" in p:
            W0, E0, S0, N0 = self.faces()
            m = p["site_margin"]
            self.poly(self.rect(W0 - m[0], S0 - m[1], E0 + m[2], N0 + m[3]), self.L(an["site_boundary"]))
        ld = an["leader"]
        lt = ld["text"]
        for leader in p.get("leaders", []):
            self.E.append(
                dict(
                    type="leader",
                    layer=ld["layer"],
                    points=[self.P(*q) for q in leader["points"]],
                    arrow=ld["arrow"],
                )
            )
            ex, ey = leader["points"][-1]
            right = leader["points"][-1][0] >= leader["points"][-2][0]
            dx = lt["dx"] if right else -lt["dx"]
            just = "ML" if right else "MR"
            self.text(leader["lines"][0], (ex + dx, ey + lt["line1_dy"]), lt["height"], self.L(lt), just)
            if len(leader["lines"]) > 1:
                self.text(leader["lines"][1], (ex + dx, ey + lt["line2_dy"]), lt["height"], self.L(lt), just)

    def dimensions(self):
        d = self.s["dimensions"]
        R = d["rows"]
        X, Y = self.p["grid"]["x"], self.p["grid"]["y"]
        W0, E0, S0, N0 = self.faces()

        def row(vals, fixed, pos, horiz):
            for a, b in zip(vals, vals[1:], strict=False):
                if horiz:
                    p1, p2, lp, rot = (a, fixed), (b, fixed), (a, pos), 0
                else:
                    p1, p2, lp, rot = (fixed, a), (fixed, b), (pos, a), 90
                self.E.append(
                    dict(
                        type="dimension",
                        layer=d["layer"],
                        kind="rotated",
                        p1=self.P(*p1),
                        p2=self.P(*p2),
                        line_point=self.P(*lp),
                        rotation=rot,
                        style=d["style"],
                    )
                )

        def cuts(side, lo, hi):
            return (
                [lo]
                + sorted(v for o in self.p["openings"] if o["side"] == side for v in (o["from"], o["to"]))
                + [hi]
            )

        row(cuts("S", W0, E0), S0, R["bottom"]["detail"], True)
        row(X, Y[0], R["bottom"]["grid"], True)
        row([W0, E0], S0, R["bottom"]["overall"], True)
        row(cuts("N", W0, E0), N0, R["top"]["detail"], True)
        row(X, Y[-1], R["top"]["grid"], True)
        row([W0, E0], N0, R["top"]["overall"], True)
        row(Y, X[0], R["left"]["grid"], False)
        row([S0, N0], W0, R["left"]["overall"], False)
        row(cuts("E", S0, N0), E0, R["right"]["detail"], False)
        row([S0, N0], E0, R["right"]["overall"], False)

    def build(self):
        self.sheet()
        self.grid()
        self.walls()
        self.insulation()
        self.openings()
        self.annotation()
        self.dimensions()
        return self.E


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("spec")
    ap.add_argument("--out", default="plan_entities.json")
    ap.add_argument("--standard", default=str(STD_PATH))
    a = ap.parse_args()
    std = json.loads(Path(a.standard).read_text(encoding="utf-8"))
    spec = json.loads(Path(a.spec).read_text(encoding="utf-8"))
    ents = Builder(std, spec).build()
    Path(a.out).write_text(json.dumps(ents, ensure_ascii=False), encoding="utf-8")
    print(f"{len(ents)} entities -> {a.out}")


if __name__ == "__main__":
    main()
