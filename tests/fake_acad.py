"""A small in-memory imitation of the AutoCAD ActiveX/COM object model.

It mirrors the method/property names the COM backend uses (AddLine, HandleToObject, Layers.Item...)
so the backend's logic -- threading, radians/degrees, VARIANT packing, error translation -- is
testable on any OS. It is intentionally not a geometry kernel.
"""

from __future__ import annotations

import itertools
import math
import os
import threading


class FakeComError(Exception):
    def __init__(self, hresult: int, message: str = "error", description: str | None = None):
        super().__init__(hresult, message, (0, "AutoCAD", description, None, 0, hresult), None)
        self.hresult = hresult


E_FAIL = -2147467259
BUSY = -2147418111


def _v(p):
    return tuple(float(c) for c in p)


def _rot(p, base, ang):
    x, y = p[0] - base[0], p[1] - base[1]
    c, s = math.cos(ang), math.sin(ang)
    return (base[0] + x * c - y * s, base[1] + x * s + y * c, p[2] if len(p) > 2 else 0.0)


def _mirror(p, a, b):
    dx, dy = b[0] - a[0], b[1] - a[1]
    t = ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / (dx * dx + dy * dy)
    fx, fy = a[0] + t * dx, a[1] + t * dy
    return (2 * fx - p[0], 2 * fy - p[1], p[2])


class Entity:
    POINTS: tuple[str, ...] = ()
    FLAT: str | None = None  # name of a flat coordinate array attribute
    FLAT_DIM = 2
    object_name = "AcDbEntity"

    def __init__(self, doc, **attrs):
        self._doc = doc
        self.Handle = doc.next_handle()
        self.Layer = doc.ActiveLayer.Name
        self.color = 256
        self.Linetype = "ByLayer"
        self.Lineweight = -1
        self.owner = doc._ms
        for k, v in attrs.items():
            setattr(self, k, v)
        doc.objects[self.Handle] = self

    @property
    def ObjectName(self):  # noqa: N802
        return self.object_name

    @property
    def OwnerID(self):  # noqa: N802
        return self.owner.ObjectID

    def Update(self):  # noqa: N802
        self._doc.updates += 1

    def Delete(self):  # noqa: N802
        self.owner.items.remove(self)
        del self._doc.objects[self.Handle]

    def _map(self, fn):
        for name in self.POINTS:
            setattr(self, name, fn(getattr(self, name)))
        if self.FLAT:
            c = list(getattr(self, self.FLAT))
            d = self.FLAT_DIM
            pts = [fn(tuple(c[i : i + d]) + ((0.0,) if d == 2 else ())) for i in range(0, len(c), d)]
            setattr(self, self.FLAT, tuple(v for p in pts for v in p[:d]))

    def Move(self, p1, p2):  # noqa: N802
        d = [b - a for a, b in zip(p1, p2, strict=True)]
        self._map(lambda p: (p[0] + d[0], p[1] + d[1], p[2] + d[2]))

    def Rotate(self, base, ang):  # noqa: N802
        self._map(lambda p: _rot(p, base, ang))
        if hasattr(self, "Rotation"):
            self.Rotation += ang
        if hasattr(self, "StartAngle"):
            self.StartAngle += ang
            self.EndAngle += ang

    def ScaleEntity(self, base, f):  # noqa: N802
        self._map(lambda p: tuple(base[i] + (p[i] - base[i]) * f for i in range(3)))
        for scalar in ("Radius", "Height"):
            if hasattr(self, scalar):
                setattr(self, scalar, getattr(self, scalar) * f)

    def _clone(self):
        attrs = {
            k: v for k, v in self.__dict__.items() if not k.startswith("_") and k not in ("Handle", "owner")
        }
        clone = type(self)(self._doc, **attrs)
        self.owner.items.append(clone)
        return clone

    def Copy(self):  # noqa: N802
        return self._clone()

    def Mirror(self, a, b):  # noqa: N802
        clone = self._clone()
        clone._map(lambda p: _mirror(p, a, b))
        return clone

    def Offset(self, distance):  # noqa: N802
        raise FakeComError(E_FAIL, "Offset", "Invalid input")


class Line(Entity):
    object_name = "AcDbLine"
    POINTS = ("StartPoint", "EndPoint")

    @property
    def Length(self):  # noqa: N802
        return math.dist(self.StartPoint, self.EndPoint)


class Circle(Entity):
    object_name = "AcDbCircle"
    POINTS = ("Center",)

    def Offset(self, distance):  # noqa: N802
        c = self._clone()
        c.Radius = self.Radius + distance
        return (c,)


class Arc(Circle):
    object_name = "AcDbArc"


class LwPolyline(Entity):
    object_name = "AcDbPolyline"
    FLAT = "Coordinates"
    Closed = False
    Elevation = 0.0


class Poly3d(Entity):
    object_name = "AcDb3dPolyline"
    FLAT = "Coordinates"
    FLAT_DIM = 3
    Closed = False


class Point(Entity):
    object_name = "AcDbPoint"
    POINTS = ("Coordinates",)


class Ellipse(Entity):
    object_name = "AcDbEllipse"
    POINTS = ("Center",)


class Text(Entity):
    object_name = "AcDbText"
    POINTS = ("InsertionPoint",)
    Rotation = 0.0


class MText(Entity):
    object_name = "AcDbMText"
    POINTS = ("InsertionPoint",)
    Height = 2.5


class Dimension(Entity):
    object_name = "AcDbAlignedDimension"
    POINTS = ("P1", "P2")
    TextOverride = ""

    @property
    def Measurement(self):  # noqa: N802
        if self.object_name == "AcDbRotatedDimension":
            ang = self.RotationRad
            d = (self.P2[0] - self.P1[0], self.P2[1] - self.P1[1])
            return abs(d[0] * math.cos(ang) + d[1] * math.sin(ang))
        return math.dist(self.P1, self.P2)


class Hatch(Entity):
    object_name = "AcDbHatch"
    PatternScale = 1.0
    PatternAngle = 0.0
    evaluated = False

    def AppendOuterLoop(self, objs):  # noqa: N802
        for o in objs:
            if isinstance(o, LwPolyline) and not o.Closed:
                raise FakeComError(E_FAIL, "AppendOuterLoop", "Invalid input")
        self.loops = list(objs)

    def Evaluate(self):  # noqa: N802
        self.evaluated = True


class BlockRef(Entity):
    object_name = "AcDbBlockReference"
    POINTS = ("InsertionPoint",)

    @property
    def EffectiveName(self):  # noqa: N802
        return self.Name


class Collection:
    def __init__(self):
        self.items: list = []

    @property
    def Count(self):  # noqa: N802
        return len(self.items)

    def Item(self, key):  # noqa: N802
        if isinstance(key, int):
            return self.items[key]
        for it in self.items:
            if it.Name.lower() == str(key).lower():
                return it
        raise FakeComError(E_FAIL, "Item", "Key not found")

    def __iter__(self):
        return iter(self.items)


class Block(Collection):
    def __init__(self, doc, name, origin=(0, 0, 0), is_layout=False):
        super().__init__()
        self._doc = doc
        self.Name = name
        self.Origin = _v(origin)
        self.IsLayout = is_layout
        self.IsXRef = False
        self.ObjectID = id(self)

    def _add(self, cls, **attrs):
        e = cls(self._doc, **attrs)
        e.owner = self
        self.items.append(e)
        return e

    def AddLine(self, p1, p2):  # noqa: N802
        return self._add(Line, StartPoint=_v(p1), EndPoint=_v(p2))

    def AddLightWeightPolyline(self, coords):  # noqa: N802
        assert len(coords) % 2 == 0
        return self._add(LwPolyline, Coordinates=tuple(coords))

    def Add3DPoly(self, coords):  # noqa: N802
        assert len(coords) % 3 == 0
        return self._add(Poly3d, Coordinates=tuple(coords))

    def AddCircle(self, c, r):  # noqa: N802
        return self._add(Circle, Center=_v(c), Radius=r)

    def AddArc(self, c, r, a0, a1):  # noqa: N802
        return self._add(Arc, Center=_v(c), Radius=r, StartAngle=a0, EndAngle=a1)

    def AddEllipse(self, c, major, ratio):  # noqa: N802
        return self._add(Ellipse, Center=_v(c), MajorAxis=_v(major), RadiusRatio=ratio)

    def AddPoint(self, p):  # noqa: N802
        return self._add(Point, Coordinates=_v(p))

    def AddText(self, text, p, h):  # noqa: N802
        return self._add(Text, TextString=text, InsertionPoint=_v(p), Height=h)

    def AddMText(self, p, width, text):  # noqa: N802
        return self._add(MText, InsertionPoint=_v(p), Width=width, TextString=text)

    def AddDimAligned(self, p1, p2, loc):  # noqa: N802
        return self._add(Dimension, P1=_v(p1), P2=_v(p2), Loc=_v(loc))

    def AddDimRotated(self, p1, p2, loc, rot):  # noqa: N802
        d = self._add(Dimension, P1=_v(p1), P2=_v(p2), Loc=_v(loc), RotationRad=rot)
        d.object_name = "AcDbRotatedDimension"
        return d

    def AddHatch(self, pattern_type, name, assoc):  # noqa: N802
        if name not in ("SOLID", "ANSI31", "ANSI37", "NET"):
            raise FakeComError(E_FAIL, "AddHatch", "Invalid pattern name")
        return self._add(Hatch, PatternName=name)

    def InsertBlock(self, p, name, xs, ys, zs, rot):  # noqa: N802
        if not os.path.exists(name):
            self._doc.Blocks.Item(name)
        else:
            name = os.path.splitext(os.path.basename(name))[0]
        return self._add(BlockRef, Name=name, InsertionPoint=_v(p), XScaleFactor=xs, Rotation=rot)


class Layer:
    def __init__(self, name):
        self.Name = name
        self.color = 7
        self.Linetype = "Continuous"
        self.Lineweight = -3
        self.LayerOn = True
        self.Freeze = False
        self.Lock = False
        self._doc = None

    def Delete(self):  # noqa: N802
        if any(e.Layer.lower() == self.Name.lower() for e in self._doc.objects.values()):
            raise FakeComError(E_FAIL, "Delete", "Object is referenced")
        self._doc.Layers.items.remove(self)


class Layers(Collection):
    def __init__(self, doc):
        super().__init__()
        self._doc = doc

    def Add(self, name):  # noqa: N802
        try:
            return self.Item(name)
        except FakeComError:
            layer = Layer(name)
            layer._doc = self._doc
            self.items.append(layer)
            return layer


class Linetypes(Collection):
    AVAILABLE = {"DASHED", "CENTER", "HIDDEN", "PHANTOM"}

    def __init__(self):
        super().__init__()
        self.items = [type("LT", (), {"Name": n})() for n in ("ByBlock", "ByLayer", "Continuous")]

    def Load(self, name, lin):  # noqa: N802
        if name.upper() not in self.AVAILABLE:
            raise FakeComError(E_FAIL, "Load", "Invalid input")
        self.items.append(type("LT", (), {"Name": name})())


class Blocks(Collection):
    def __init__(self, doc):
        super().__init__()
        self._doc = doc

    def Add(self, origin, name):  # noqa: N802
        b = Block(self._doc, name, origin)
        self.items.append(b)
        return b


class SelectionSet(Collection):
    def __init__(self, doc, name):
        super().__init__()
        self._doc, self.Name = doc, name

    def Select(self, mode):  # noqa: N802
        assert mode == 5
        self.items = list(self._doc.ModelSpace.items)

    def Delete(self):  # noqa: N802
        self._doc.SelectionSets.items.remove(self)


class SelectionSets(Collection):
    def __init__(self, doc):
        super().__init__()
        self._doc = doc

    def Add(self, name):  # noqa: N802
        s = SelectionSet(self._doc, name)
        self.items.append(s)
        return s


class Plot:
    def PlotToFile(self, path, config):  # noqa: N802
        with open(path, "wb") as fh:
            fh.write(b"%PDF-1.7 fake")
        return True


class Layout:
    def RefreshPlotDeviceInfo(self):  # noqa: N802
        pass


class Document:
    _counter = itertools.count(1)

    def __init__(self, app, name=None):
        self.app = app
        self.Name = name or f"Drawing{next(self._counter)}.dwg"
        self.FullName = ""
        self.Saved = True
        self.objects: dict[str, Entity] = {}
        self._handle = itertools.count(0x2A0)
        self.updates = 0
        self.Layers = Layers(self)
        zero = self.Layers.Add("0")
        self.ActiveLayer = zero
        self.Linetypes = Linetypes()
        self.Blocks = Blocks(self)
        self._ms = Block(self, "*Model_Space", is_layout=True)
        self.Blocks.items.append(self._ms)
        self.SelectionSets = SelectionSets(self)
        self.Plot = Plot()
        self.ActiveLayout = Layout()
        self.variables = {"INSUNITS": 4, "BACKGROUNDPLOT": 2}
        self.commands: list[str] = []

    @property
    def ModelSpace(self):  # noqa: N802
        self.app.touch()
        return self._ms

    def next_handle(self):
        return format(next(self._handle), "X")

    def HandleToObject(self, handle):  # noqa: N802
        try:
            return self.objects[handle]
        except KeyError:
            raise FakeComError(E_FAIL, "HandleToObject", "Invalid handle") from None

    def GetVariable(self, name):  # noqa: N802
        if name in ("EXTMIN", "EXTMAX"):
            pts = []
            for e in self._ms.items:
                for attr in e.POINTS:
                    pts.append(getattr(e, attr))
                if e.FLAT:
                    c, d = getattr(e, e.FLAT), e.FLAT_DIM
                    pts += [tuple(c[i : i + d]) + ((0.0,) if d == 2 else ()) for i in range(0, len(c), d)]
            if not pts:
                return (1e20, 1e20, 1e20) if name == "EXTMIN" else (-1e20, -1e20, -1e20)
            fn = min if name == "EXTMIN" else max
            return tuple(fn(p[i] for p in pts) for i in range(3))
        return self.variables[name]

    def SetVariable(self, name, value):  # noqa: N802
        self.variables[name] = value

    def SendCommand(self, text):  # noqa: N802
        self.commands.append(text)

    def Save(self):  # noqa: N802
        self._write(self.FullName)

    def SaveAs(self, path, save_type=None):  # noqa: N802
        self._write(path)
        self.FullName, self.Name = path, os.path.basename(path)
        self.last_save_type = save_type

    def _write(self, path):
        with open(path, "w") as fh:
            fh.write(f"fake drawing with {len(self._ms.items)} entities")
        self.Saved = True

    def CopyObjects(self, objs, owner):  # noqa: N802
        for o in objs:
            attrs = {
                k: v for k, v in o.__dict__.items() if not k.startswith("_") and k not in ("Handle", "owner")
            }
            clone = type(o)(self, **attrs)
            clone.owner = owner
            owner.items.append(clone)

    def Export(self, base, ext, ss):  # noqa: N802
        with open(f"{base}.{ext.lower()}", "wb") as fh:
            fh.write(b"BM fake")


class Documents(Collection):
    def __init__(self, app):
        super().__init__()
        self._app = app

    def Add(self, template=None):  # noqa: N802
        d = Document(self._app)
        self.items.append(d)
        self._app._active = d
        return d

    def Open(self, path):  # noqa: N802
        d = Document(self._app, os.path.basename(path))
        d.FullName = path
        self.items.append(d)
        self._app._active = d
        return d


class AcadState:
    IsQuiescent = True


class Application:
    Name = "AutoCAD"
    Version = "26.0s (LMS Tech)"

    def __init__(self):
        self.Visible = True
        self.Documents = Documents(self)
        self._active = None
        self.threads: set[int] = set()
        self.busy_failures = 0  # next N ModelSpace accesses raise RPC_E_CALL_REJECTED
        self.zoomed: list = []
        self.Documents.Add()

    def touch(self):
        self.threads.add(threading.get_ident())
        if self.busy_failures:
            self.busy_failures -= 1
            raise FakeComError(BUSY, "Call was rejected by callee.")

    @property
    def ActiveDocument(self):  # noqa: N802
        if self._active is None:
            raise FakeComError(E_FAIL, "ActiveDocument", "No document")
        return self._active

    def ZoomExtents(self):  # noqa: N802
        self.zoomed.append("extents")

    def ZoomWindow(self, p1, p2):  # noqa: N802
        self.zoomed.append(("window", p1, p2))

    def GetAcadState(self):  # noqa: N802
        return AcadState()


class FakeClient:
    """Stands in for power_cad_mcp.backends.com_backend.Win32Client."""

    def __init__(self, app: Application | None = None, running: bool = True):
        self.app = app or Application()
        self.running = running
        self.progids_tried: list[str] = []
        self.initialized_threads: set[int] = set()

    def co_initialize(self):
        self.initialized_threads.add(threading.get_ident())

    def get_active(self, progid):
        self.progids_tried.append(progid)
        if not self.running:
            raise FakeComError(-2147221021, "Operation unavailable")
        return self.app

    def dispatch(self, progid):
        self.running = True
        return self.app

    def point(self, p):
        assert len(p) == 3
        return tuple(float(v) for v in p)

    def doubles(self, values):
        return tuple(float(v) for v in values)

    def objects(self, objs):
        return list(objs)
