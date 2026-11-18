#!/usr/bin/env python
"""Generate the synthetic .docx test corpus for the equation-picture-to-OMML converter.

Every package is built from raw XML strings with zipfile (no python-docx).
Outputs:   tests/corpus/*.docx, tests/corpus/expected.json
Usage:     python -I scripts/make_corpus.py [--validate]

--validate additionally parses every XML part of every generated file and runs
LibreOffice headless (PDF conversion) on each one.
"""
from __future__ import annotations

import io
import json
import struct
import subprocess
import sys
import zipfile
from pathlib import Path
import xml.etree.ElementTree as ET

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402
import numpy as np  # noqa: E402
from PIL import Image, ImageChops, ImageDraw  # noqa: E402

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "tests" / "corpus"
TMP = ROOT / "out" / "corpus_tmp"
SOFFICE = r"C:\Program Files\LibreOffice\program\soffice.exe"
LO_PROFILE = (ROOT / "out" / "lo_profile_corpus").as_uri()

EMU_PER_IN = 914400
EMU_PER_PT = 12700
ONE_INCH = 914400
DPI = 200          # equation render resolution
EQ_FONTSIZE = 13   # pt

# --------------------------------------------------------------------------
# Equations (matplotlib mathtext compatible). truth_latex = string without $.
# --------------------------------------------------------------------------
EQUATIONS = [
    r"\frac{a}{b}+\frac{c}{d}=\frac{ad+bc}{bd}",                    # 0
    r"x^2+y^2=z^2",                                                  # 1
    r"\sum_{i=1}^{n} i=\frac{n(n+1)}{2}",                            # 2
    r"\int_0^1 x^2\,dx=\frac{1}{3}",                                 # 3
    r"\sqrt{a^2+b^2}",                                               # 4
    r"e^{i\pi}+1=0",                                                 # 5
    r"\alpha+\beta=\gamma",                                          # 6
    r"x=\frac{-b\pm\sqrt{b^2-4ac}}{2a}",                             # 7
    r"\lim_{x\to 0}\frac{\sin x}{x}=1",                              # 8
    r"E=mc^2",                                                       # 9
    r"a_{n+1}=a_n+d",                                                # 10
    r"\prod_{k=1}^{n} k=n!",                                         # 11
    r"\frac{\partial f}{\partial x}",                                # 12
    r"\nabla\cdot\vec{E}=\frac{\rho}{\epsilon_0}",                   # 13
    r"\sin^2\theta+\cos^2\theta=1",                                  # 14
    r"\sqrt[3]{x+1}",                                                # 15
    r"\int_{-\infty}^{\infty} e^{-x^2}\,dx=\sqrt{\pi}",              # 16
    r"f(x)=\sum_{n=0}^{\infty}\frac{f^{(n)}(a)}{n!}(x-a)^n",         # 17
    r"\Delta x\,\Delta p\geq\frac{\hbar}{2}",                        # 18
    r"\log_b(xy)=\log_b x+\log_b y",                                 # 19
    r"\binom{n}{k}=\frac{n!}{k!(n-k)!}",                             # 20
    r"\lambda_{max}=\frac{b}{T}",                                    # 21
    r"\mathbf{F}=m\mathbf{a}",                                       # 22
    r"\overline{z}=a-bi",                                            # 23
    r"\frac{d}{dx}\left(\frac{1}{x}\right)=-\frac{1}{x^2}",          # 24
]

# --------------------------------------------------------------------------
# Namespaces
# --------------------------------------------------------------------------
NS = {
    "w": "http://schemas.openxmlformats.org/wordprocessingml/2006/main",
    "r": "http://schemas.openxmlformats.org/officeDocument/2006/relationships",
    "wp": "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing",
    "a": "http://schemas.openxmlformats.org/drawingml/2006/main",
    "pic": "http://schemas.openxmlformats.org/drawingml/2006/picture",
    "m": "http://schemas.openxmlformats.org/officeDocument/2006/math",
    "mc": "http://schemas.openxmlformats.org/markup-compatibility/2006",
    "v": "urn:schemas-microsoft-com:vml",
    "o": "urn:schemas-microsoft-com:office:office",
    "w10": "urn:schemas-microsoft-com:office:word",
    "w14": "http://schemas.microsoft.com/office/word/2010/wordml",
    "wp14": "http://schemas.microsoft.com/office/word/2010/wordprocessingDrawing",
    "wps": "http://schemas.microsoft.com/office/word/2010/wordprocessingShape",
    "wpc": "http://schemas.microsoft.com/office/word/2010/wordprocessingCanvas",
}
NSDECL = " ".join(f'xmlns:{k}="{v}"' for k, v in NS.items()) + ' mc:Ignorable="w14 wp14"'
XMLDECL = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'

RT = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/"
REL_IMAGE = RT + "image"
REL_STYLES = RT + "styles"
REL_HEADER = RT + "header"
REL_FOOTER = RT + "footer"
REL_COMMENTS = RT + "comments"
REL_OLE = RT + "oleObject"

CT_MAIN = "application/vnd.openxmlformats-officedocument.wordprocessingml"
PART_CT = {
    "word/document.xml": CT_MAIN + ".document.main+xml",
    "word/styles.xml": CT_MAIN + ".styles+xml",
    "word/header1.xml": CT_MAIN + ".header+xml",
    "word/footer1.xml": CT_MAIN + ".footer+xml",
    "word/comments.xml": CT_MAIN + ".comments+xml",
}

STYLES_XML = XMLDECL + (
    f'<w:styles xmlns:w="{NS["w"]}">'
    '<w:docDefaults><w:rPrDefault><w:rPr>'
    '<w:rFonts w:ascii="Calibri" w:hAnsi="Calibri" w:eastAsia="Calibri" w:cs="Times New Roman"/>'
    '<w:sz w:val="22"/><w:szCs w:val="22"/><w:lang w:val="en-US"/></w:rPr></w:rPrDefault>'
    '<w:pPrDefault><w:pPr><w:spacing w:after="160" w:line="259" w:lineRule="auto"/></w:pPr></w:pPrDefault>'
    '</w:docDefaults>'
    '<w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:qFormat/></w:style>'
    '<w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/><w:basedOn w:val="Normal"/>'
    '<w:next w:val="Normal"/><w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before="240" w:after="120"/>'
    '<w:outlineLvl w:val="0"/></w:pPr><w:rPr><w:b/><w:sz w:val="32"/></w:rPr></w:style>'
    '<w:style w:type="character" w:default="1" w:styleId="DefaultParagraphFont">'
    '<w:name w:val="Default Paragraph Font"/><w:uiPriority w:val="1"/><w:semiHidden/></w:style>'
    '<w:style w:type="table" w:default="1" w:styleId="TableNormal"><w:name w:val="Normal Table"/>'
    '<w:semiHidden/><w:tblPr><w:tblInd w:w="0" w:type="dxa"/><w:tblCellMar>'
    '<w:top w:w="0" w:type="dxa"/><w:left w:w="108" w:type="dxa"/><w:bottom w:w="0" w:type="dxa"/>'
    '<w:right w:w="108" w:type="dxa"/></w:tblCellMar></w:tblPr></w:style>'
    '<w:style w:type="numbering" w:default="1" w:styleId="NoList"><w:name w:val="No List"/><w:semiHidden/></w:style>'
    '</w:styles>'
)

VML_SHAPETYPE_75 = (
    '<v:shapetype id="_x0000_t75" coordsize="21600,21600" o:spt="75" o:preferrelative="t" '
    'path="m@4@5l@4@11@9@11@9@5xe" filled="f" stroked="f"><v:stroke joinstyle="miter"/>'
    '<v:formulas><v:f eqn="if lineDrawn pixelLineWidth 0"/><v:f eqn="sum @0 1 0"/>'
    '<v:f eqn="sum 0 0 @1"/><v:f eqn="prod @2 1 2"/><v:f eqn="prod @3 21600 pixelWidth"/>'
    '<v:f eqn="prod @3 21600 pixelHeight"/><v:f eqn="sum @0 0 1"/><v:f eqn="prod @6 1 2"/>'
    '<v:f eqn="prod @7 21600 pixelWidth"/><v:f eqn="sum @8 21600 0"/>'
    '<v:f eqn="prod @7 21600 pixelHeight"/><v:f eqn="sum @10 21600 0"/></v:formulas>'
    '<v:path o:extrusionok="f" gradientshapeok="t" o:connecttype="rect"/>'
    '<o:lock v:ext="edit" aspectratio="t"/></v:shapetype>'
)
VML_SHAPETYPE_202 = (
    '<v:shapetype id="_x0000_t202" coordsize="21600,21600" o:spt="202" path="m,l,21600r21600,l21600,xe">'
    '<v:stroke joinstyle="miter"/><v:path gradientshapeok="t" o:connecttype="rect"/></v:shapetype>'
)


def esc(s: str) -> str:
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace('"', "&quot;")


# --------------------------------------------------------------------------
# Images
# --------------------------------------------------------------------------
class Img:
    def __init__(self, data: bytes, fmt: str, cx: int, cy: int, latex: str | None = None,
                 logo: bool = False, label: str = "img"):
        self.data, self.fmt, self.cx, self.cy = data, fmt, int(cx), int(cy)
        self.latex, self.logo, self.label = latex, logo, label


matplotlib.rcParams["mathtext.fontset"] = "cm"
_eq_cache: dict[tuple[int, str], Img] = {}


def eq_img(i: int, fmt: str = "png") -> Img:
    key = (i, fmt)
    if key in _eq_cache:
        return _eq_cache[key]
    latex = EQUATIONS[i]
    fig = plt.figure(figsize=(0.1, 0.1))
    fig.text(0, 0, f"${latex}$", fontsize=EQ_FONTSIZE, color="black")
    buf = io.BytesIO()
    fig.savefig(buf, dpi=DPI, format="png", bbox_inches="tight", pad_inches=0.1, facecolor="white")
    plt.close(fig)
    im = Image.open(io.BytesIO(buf.getvalue())).convert("RGB")
    diff = ImageChops.difference(im, Image.new("RGB", im.size, (255, 255, 255))).convert("L")
    bbox = diff.point(lambda v: 255 if v > 8 else 0).getbbox()
    im = im.crop(bbox)
    m = 4
    canvas = Image.new("RGB", (im.width + 2 * m, im.height + 2 * m), (255, 255, 255))
    canvas.paste(im, (m, m))
    out = io.BytesIO()
    if fmt == "png":
        canvas.save(out, format="PNG", dpi=(DPI, DPI))
    else:
        canvas.save(out, format="JPEG", quality=92, dpi=(DPI, DPI))
    cx = round(canvas.width / DPI * EMU_PER_IN)
    cy = round(canvas.height / DPI * EMU_PER_IN)
    assert cy < ONE_INCH, (latex, cy)
    img = Img(out.getvalue(), fmt, cx, cy, latex=latex, label=f"eq{i}")
    _eq_cache[key] = img
    return img


def logo_img(w_px: int, h_px: int, height_in: float, seed: int, fmt: str = "png", label="logo") -> Img:
    """Colorful logo/figure: diagonal gradient + colored shapes."""
    rng = np.random.default_rng(seed)
    y, x = np.mgrid[0:h_px, 0:w_px]
    t = (x / max(w_px - 1, 1) + y / max(h_px - 1, 1)) / 2
    c1 = rng.integers(0, 255, 3)
    c2 = rng.integers(0, 255, 3)
    arr = (c1[None, None, :] * (1 - t[..., None]) + c2[None, None, :] * t[..., None]).astype(np.uint8)
    im = Image.fromarray(arr, "RGB")
    d = ImageDraw.Draw(im)
    palette = [(230, 40, 40), (40, 160, 60), (30, 90, 220), (250, 200, 20), (160, 40, 200), (20, 200, 200)]
    for k in range(8):
        r = int(min(w_px, h_px) * rng.uniform(0.12, 0.3))
        cx_, cy_ = int(rng.uniform(0, w_px)), int(rng.uniform(0, h_px))
        col = palette[k % len(palette)]
        if k % 2:
            d.ellipse([cx_ - r, cy_ - r, cx_ + r, cy_ + r], fill=col, outline=(255, 255, 255), width=3)
        else:
            d.rectangle([cx_ - r, cy_ - r // 2, cx_ + r, cy_ + r // 2], fill=col)
    d.text((10, 10), "ACME", fill=(255, 255, 255))
    out = io.BytesIO()
    im.save(out, format="PNG" if fmt == "png" else "JPEG", **({} if fmt == "png" else {"quality": 90}))
    cy = round(height_in * EMU_PER_IN)
    cx = round(cy * w_px / h_px)
    return Img(out.getvalue(), fmt, cx, cy, latex=None, logo=True, label=label)


def make_emf(w_px=300, h_px=80) -> bytes:
    """Minimal valid EMF: EMR_HEADER (88 bytes) + EMR_EOF (20 bytes)."""
    total = 88 + 20
    hdr = struct.pack(
        "<II4i4iIIIIHHIII2i2i",
        1, 88,                      # iType EMR_HEADER, nSize
        0, 0, w_px - 1, h_px - 1,   # rclBounds (device units)
        0, 0, w_px * 2646 // 100, h_px * 2646 // 100,  # rclFrame in 0.01 mm (96 dpi)
        0x464D4520, 0x00010000,     # signature " EMF", version
        total, 2,                   # nBytes, nRecords
        1, 0,                       # nHandles, sReserved
        0, 0, 0,                    # nDescription, offDescription, nPalEntries
        1920, 1080,                 # szlDevice (pixels)
        508, 286,                   # szlMillimeters
    )
    assert len(hdr) == 88, len(hdr)
    eof = struct.pack("<IIIII", 14, 20, 0, 16, 20)
    return hdr + eof


def make_wmf(w=1440, h=400, inch=1440) -> bytes:
    """Minimal valid placeable WMF: placeable header + META_HEADER + META_EOF."""
    key = 0x9AC6CDD7
    words = struct.unpack("<10H", struct.pack("<IHhhhhHI", key, 0, 0, 0, w, h, inch, 0)[:20])
    checksum = 0
    for wd in words:
        checksum ^= wd
    placeable = struct.pack("<IHhhhhHIH", key, 0, 0, 0, w, h, inch, 0, checksum)
    assert len(placeable) == 22
    size_words = 9 + 3
    header = struct.pack("<HHHIHIH", 1, 9, 0x0300, size_words, 0, 3, 0)
    assert len(header) == 18
    eof = struct.pack("<IH", 3, 0x0000)
    return placeable + header + eof


EMF_IMG = None
WMF_IMG = None


def emf_img() -> Img:
    global EMF_IMG
    if EMF_IMG is None:
        EMF_IMG = Img(make_emf(300, 80), "emf", 300 * 9525, 80 * 9525, label="emf")
    return EMF_IMG


def wmf_img() -> Img:
    global WMF_IMG
    if WMF_IMG is None:  # 1440x400 twips-ish units at 1440/inch -> 1in x 0.278in
        WMF_IMG = Img(make_wmf(1440, 400, 1440), "wmf", EMU_PER_IN, round(400 / 1440 * EMU_PER_IN), label="wmf")
    return WMF_IMG


# --------------------------------------------------------------------------
# Classification rules (single source of truth for expected categories)
# --------------------------------------------------------------------------
def classify(container, alt, floating, fmt, cy, logo, is_ole):
    if container in ("header", "footer", "comments", "footnotes", "endnotes"):
        return "EXCLUDED", f"in {container}"
    if alt == "fallback":
        return "EXCLUDED", "inside mc:Fallback"
    if floating:
        return "EXCLUDED", "anchored/floating"
    if fmt in ("emf", "wmf"):
        return "UNSUPPORTED_FORMAT", f"{fmt.upper()} " + ("OLE preview" if is_ole else "picture")
    if cy > ONE_INCH:
        return "EXCLUDED", "taller than 1 inch"
    if logo:
        return "EXCLUDED", "colorful logo/figure"
    if is_ole:
        return "OLE_PREVIEW", f"OLE Equation.3 {fmt.upper()} preview"
    return "CANDIDATE", "inline equation picture"


# --------------------------------------------------------------------------
# Document builder
# --------------------------------------------------------------------------
class Doc:
    def __init__(self, fname: str):
        self.fname = fname
        self.rels: dict[str, list[tuple[str, str, str]]] = {"word/document.xml": []}
        self.files: dict[str, bytes] = {}           # zip path -> bytes
        self.media_of: dict[int, str] = {}          # id(img) -> target ("media/x.png")
        self.rid_of: dict[tuple[str, int], str] = {}
        self.records: dict[str, list[dict]] = {}
        self.parts: dict[str, str] = {}
        self.ids = 0
        self.vml_ids = 1024
        self.ole_n = 0
        self.shapetype_done: set[tuple[str, str]] = set()
        self.add_rel("word/document.xml", REL_STYLES, "styles.xml")

    # ---- relationships / media
    def add_rel(self, part, rtype, target):
        lst = self.rels.setdefault(part, [])
        rid = f"rId{len(lst) + 1}"
        lst.append((rid, rtype, target))
        return rid

    def media_target(self, img: Img) -> str:
        if id(img) not in self.media_of:
            n = len(self.media_of) + 1
            ext = img.fmt
            self.media_of[id(img)] = f"media/image{n}.{ext}"
            self.files["word/" + self.media_of[id(img)]] = img.data
        return self.media_of[id(img)]

    def img_rid(self, part, img: Img, new_rid=False) -> str:
        key = (part, id(img))
        if new_rid or key not in self.rid_of:
            rid = self.add_rel(part, REL_IMAGE, self.media_target(img))
            if key not in self.rid_of:
                self.rid_of[key] = rid
            return rid
        return self.rid_of[key]

    def next_id(self):
        self.ids += 1
        return self.ids

    def shapetype(self, part, which="75"):
        if (part, which) in self.shapetype_done:
            return ""
        self.shapetype_done.add((part, which))
        return VML_SHAPETYPE_75 if which == "75" else VML_SHAPETYPE_202

    def record(self, part, rid, kind, img, container, alt, floating=False, is_ole=False):
        cat, reason = classify(container, alt, floating, img.fmt, img.cy, img.logo, is_ole)
        self.records.setdefault(part, []).append({
            "part": part, "para_index": None, "rid": rid, "kind": kind, "format": img.fmt,
            "expected_category": cat, "expected_reason": reason, "in_altcontent": alt,
            "container": container, "truth_latex": img.latex,
        })

    # ---- picture XML
    def _graphic(self, rid, img, pid):
        return (
            '<a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture">'
            f'<pic:pic><pic:nvPicPr><pic:cNvPr id="{pid}" name="{img.label}.{img.fmt}"/><pic:cNvPicPr/></pic:nvPicPr>'
            f'<pic:blipFill><a:blip r:embed="{rid}"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>'
            f'<pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="{img.cx}" cy="{img.cy}"/></a:xfrm>'
            '<a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic>'
        )

    def inline_drawing(self, part, img, container="body", alt="none", new_rid=False, rid=None):
        """Returns <w:drawing> (no run wrapper)."""
        rid = rid or self.img_rid(part, img, new_rid)
        pid = self.next_id()
        self.record(part, rid, "inline", img, container, alt)
        return (
            '<w:drawing><wp:inline distT="0" distB="0" distL="0" distR="0">'
            f'<wp:extent cx="{img.cx}" cy="{img.cy}"/><wp:effectExtent l="0" t="0" r="0" b="0"/>'
            f'<wp:docPr id="{pid}" name="Picture {pid}"/>'
            '<wp:cNvGraphicFramePr><a:graphicFrameLocks noChangeAspect="1"/></wp:cNvGraphicFramePr>'
            + self._graphic(rid, img, pid) + '</wp:inline></w:drawing>'
        )

    def inline(self, part, img, container="body", **kw):
        return "<w:r>" + self.inline_drawing(part, img, container, **kw) + "</w:r>"

    def anchor(self, part, img, container="body", x_emu=3200400, y_emu=0, behind=False):
        rid = self.img_rid(part, img)
        pid = self.next_id()
        self.record(part, rid, "anchor", img, container, "none", floating=True)
        return (
            '<w:r><w:drawing><wp:anchor distT="0" distB="0" distL="114300" distR="114300" simplePos="0" '
            f'relativeHeight="{251658240 + pid}" behindDoc="{1 if behind else 0}" locked="0" layoutInCell="1" allowOverlap="1">'
            '<wp:simplePos x="0" y="0"/>'
            f'<wp:positionH relativeFrom="column"><wp:posOffset>{x_emu}</wp:posOffset></wp:positionH>'
            f'<wp:positionV relativeFrom="paragraph"><wp:posOffset>{y_emu}</wp:posOffset></wp:positionV>'
            f'<wp:extent cx="{img.cx}" cy="{img.cy}"/><wp:effectExtent l="0" t="0" r="0" b="0"/>'
            '<wp:wrapSquare wrapText="bothSides"/>'
            f'<wp:docPr id="{pid}" name="Floating Picture {pid}"/>'
            '<wp:cNvGraphicFramePr><a:graphicFrameLocks noChangeAspect="1"/></wp:cNvGraphicFramePr>'
            + self._graphic(rid, img, pid) + '</wp:anchor></w:drawing></w:r>'
        )

    def vml_pict(self, part, img, container="body", alt="none", absolute=False, rid=None):
        """Returns <w:pict> (no run wrapper)."""
        rid = rid or self.img_rid(part, img)
        self.vml_ids += 1
        self.record(part, rid, "vml", img, container, alt, floating=absolute)
        w_pt = round(img.cx / EMU_PER_PT, 2)
        h_pt = round(img.cy / EMU_PER_PT, 2)
        style = f"width:{w_pt}pt;height:{h_pt}pt"
        extra = ""
        if absolute:
            style = (f"position:absolute;margin-left:280pt;margin-top:6pt;{style};z-index:{251660000 + self.vml_ids};"
                     "mso-position-horizontal-relative:text;mso-position-vertical-relative:text")
            extra = '<w10:wrap type="square"/>'
        return (
            '<w:pict>' + self.shapetype(part) +
            f'<v:shape id="_x0000_i{self.vml_ids}" type="#_x0000_t75" style="{style}">'
            f'<v:imagedata r:id="{rid}" o:title=""/>{extra}</v:shape></w:pict>'
        )

    def vml(self, part, img, container="body", **kw):
        return "<w:r>" + self.vml_pict(part, img, container, **kw) + "</w:r>"

    def ole(self, part, img, container="body"):
        rid = self.img_rid(part, img)
        self.ole_n += 1
        bin_target = f"embeddings/oleObject{self.ole_n}.bin"
        # Arbitrary bytes (OLE2 signature + filler); converters must not need to parse it.
        self.files["word/" + bin_target] = (bytes.fromhex("D0CF11E0A1B11AE1") + b"\0" * 504
                                            + f"Equation.3 placeholder {self.ole_n}".encode())
        ole_rid = self.add_rel(part, REL_OLE, bin_target)
        self.vml_ids += 1
        self.record(part, rid, "ole", img, container, "none", is_ole=True)
        w_pt = round(img.cx / EMU_PER_PT, 2)
        h_pt = round(img.cy / EMU_PER_PT, 2)
        sid = f"_x0000_i{self.vml_ids}"
        return (
            f'<w:r><w:object w:dxaOrig="{round(w_pt * 20)}" w:dyaOrig="{round(h_pt * 20)}">' + self.shapetype(part) +
            f'<v:shape id="{sid}" type="#_x0000_t75" style="width:{w_pt}pt;height:{h_pt}pt" o:ole="">'
            f'<v:imagedata r:id="{rid}" o:title=""/></v:shape>'
            f'<o:OLEObject Type="Embed" ProgID="Equation.3" ShapeID="{sid}" DrawAspect="Content" '
            f'ObjectID="_17000000{self.ole_n:02d}" r:id="{ole_rid}"/></w:object></w:r>'
        )

    # ---- parts
    def set_part(self, part, xml):
        self.parts[part] = xml

    def build(self, body: str, header: str | None = None, footer: str | None = None,
              comments: str | None = None):
        sect = ""
        if header is not None:
            hr = self.add_rel("word/document.xml", REL_HEADER, "header1.xml")
            sect += f'<w:headerReference w:type="default" r:id="{hr}"/>'
            self.parts["word/header1.xml"] = XMLDECL + f"<w:hdr {NSDECL}>{header}</w:hdr>"
        if footer is not None:
            fr = self.add_rel("word/document.xml", REL_FOOTER, "footer1.xml")
            sect += f'<w:footerReference w:type="default" r:id="{fr}"/>'
            self.parts["word/footer1.xml"] = XMLDECL + f"<w:ftr {NSDECL}>{footer}</w:ftr>"
        if comments is not None:
            self.add_rel("word/document.xml", REL_COMMENTS, "comments.xml")
            self.parts["word/comments.xml"] = XMLDECL + f"<w:comments {NSDECL}>{comments}</w:comments>"
        sect = ('<w:sectPr>' + sect + '<w:pgSz w:w="12240" w:h="15840"/>'
                '<w:pgMar w:top="1440" w:right="1440" w:bottom="1440" w:left="1440" w:header="720" '
                'w:footer="720" w:gutter="0"/><w:cols w:space="720"/></w:sectPr>')
        self.parts["word/document.xml"] = XMLDECL + f"<w:document {NSDECL}><w:body>{body}{sect}</w:body></w:document>"

    def write(self, outdir: Path) -> dict:
        parts = dict(self.parts)
        parts["word/styles.xml"] = STYLES_XML
        # content types
        ct = [XMLDECL, '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">']
        for ext, typ in [("png", "image/png"), ("jpeg", "image/jpeg"), ("jpg", "image/jpeg"),
                         ("emf", "image/x-emf"), ("wmf", "image/x-wmf"),
                         ("bin", "application/vnd.openxmlformats-officedocument.oleObject"),
                         ("rels", "application/vnd.openxmlformats-package.relationships+xml"),
                         ("xml", "application/xml")]:
            ct.append(f'<Default Extension="{ext}" ContentType="{typ}"/>')
        for p in parts:
            ct.append(f'<Override PartName="/{p}" ContentType="{PART_CT[p]}"/>')
        ct.append("</Types>")
        pkg_rels = (XMLDECL + '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
                    '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/'
                    'relationships/officeDocument" Target="word/document.xml"/></Relationships>')
        OUT.mkdir(parents=True, exist_ok=True)
        path = outdir / self.fname
        with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
            z.writestr("[Content_Types].xml", "".join(ct))
            z.writestr("_rels/.rels", pkg_rels)
            for p, xml in parts.items():
                z.writestr(p, xml)
            for p, lst in self.rels.items():
                if not lst:
                    continue
                d, f = p.rsplit("/", 1)
                rx = [XMLDECL, '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">']
                for rid, typ, tgt in lst:
                    rx.append(f'<Relationship Id="{rid}" Type="{typ}" Target="{tgt}"/>')
                rx.append("</Relationships>")
                z.writestr(f"{d}/_rels/{f}.rels", "".join(rx))
            for p, data in self.files.items():
                z.writestr(p, data)
        return self.expected()

    def expected(self) -> dict:
        pics = []
        for part, xml in self.parts.items():
            refs = picture_refs(xml)
            recs = self.records.get(part, [])
            assert len(refs) == len(recs), (self.fname, part, len(refs), len(recs))
            for (pidx, rid), rec in zip(refs, recs):
                assert rid == rec["rid"], (self.fname, part, rid, rec["rid"])
                assert pidx is not None, (self.fname, part, rid)
                rec["para_index"] = pidx
                pics.append(rec)
        root = ET.fromstring(self.parts["word/document.xml"].encode())
        n_omath = sum(1 for _ in root.iter(f"{{{NS['m']}}}oMath"))
        n_para = sum(1 for _ in root.iter(f"{{{NS['m']}}}oMathPara"))
        return {"file": self.fname, "native_omath": n_omath, "native_omathpara": n_para, "pictures": pics}


def picture_refs(xml: str):
    """(para_index, rid) for each picture reference in document order.
    para_index = 0-based index among ALL w:p in the part (pre-order, incl. tables, textboxes, Choice+Fallback)."""
    root = ET.fromstring(xml.encode("utf-8"))
    W_P = f"{{{NS['w']}}}p"
    BLIP = f"{{{NS['a']}}}blip"
    IMD = f"{{{NS['v']}}}imagedata"
    R_EMBED = f"{{{NS['r']}}}embed"
    R_ID = f"{{{NS['r']}}}id"
    out, cnt = [], [-1]

    def walk(e, cur):
        if e.tag == W_P:
            cnt[0] += 1
            cur = cnt[0]
        if e.tag == BLIP:
            out.append((cur, e.get(R_EMBED)))
        elif e.tag == IMD:
            out.append((cur, e.get(R_ID)))
        for c in e:
            walk(c, cur)

    walk(root, None)
    return out


# --------------------------------------------------------------------------
# Small WordprocessingML helpers
# --------------------------------------------------------------------------
def R(text: str, bold=False, italic=False) -> str:
    rpr = ""
    if bold or italic:
        rpr = "<w:rPr>" + ("<w:b/>" if bold else "") + ("<w:i/>" if italic else "") + "</w:rPr>"
    return f'<w:r>{rpr}<w:t xml:space="preserve">{esc(text)}</w:t></w:r>'


def P(*runs: str, jc: str | None = None, style: str | None = None) -> str:
    ppr = ""
    if jc or style:
        ppr = "<w:pPr>" + (f'<w:pStyle w:val="{style}"/>' if style else "") + (f'<w:jc w:val="{jc}"/>' if jc else "") + "</w:pPr>"
    return f"<w:p>{ppr}{''.join(runs)}</w:p>"


def H(text: str) -> str:
    return P(R(text), style="Heading1")


def T(text: str) -> str:
    return P(R(text))


def table(rows: list[list[str]], widths: list[int]) -> str:
    """rows: list of rows; each cell is a string of one or more <w:p> elements."""
    borders = "".join(f'<w:{b} w:val="single" w:sz="4" w:space="0" w:color="auto"/>'
                      for b in ("top", "left", "bottom", "right", "insideH", "insideV"))
    x = [f'<w:tbl><w:tblPr><w:tblW w:w="0" w:type="auto"/><w:tblBorders>{borders}</w:tblBorders>'
         '<w:tblLook w:val="04A0" w:firstRow="1" w:lastRow="0" w:firstColumn="1" w:lastColumn="0" '
         'w:noHBand="0" w:noVBand="1"/></w:tblPr><w:tblGrid>']
    x += [f'<w:gridCol w:w="{w}"/>' for w in widths]
    x.append("</w:tblGrid>")
    for row in rows:
        x.append("<w:tr>")
        for w, cell in zip(widths, row):
            x.append(f'<w:tc><w:tcPr><w:tcW w:w="{w}" w:type="dxa"/><w:vAlign w:val="center"/></w:tcPr>{cell}</w:tc>')
        x.append("</w:tr>")
    x.append("</w:tbl>")
    return "".join(x)


LOREM = [
    "The following derivation is reproduced from the lecture notes of the second semester.",
    "All quantities are expressed in SI units unless stated otherwise.",
    "We first recall a few standard identities that will be used throughout this section.",
    "The proof is elementary and follows directly from the definitions given above.",
    "Readers familiar with the material may skip ahead to the worked examples.",
    "This result plays a central role in the convergence analysis presented later.",
]

D = "word/document.xml"


# --------------------------------------------------------------------------
# Documents
# --------------------------------------------------------------------------
def doc01():
    d = Doc("01_inline_display.docx")
    b = [H("Inline and display equations"), T(LOREM[0]), T("The basic fraction identity reads:"),
         P(d.inline(D, eq_img(0)), jc="center"),
         P(R("Einstein's relation "), d.inline(D, eq_img(9)), R(" connects mass and energy in special relativity.")),
         T("The roots of a quadratic equation are given by the familiar formula"),
         P(d.inline(D, eq_img(7, "jpeg")), jc="center"),
         P(R("For angles we write "), d.inline(D, eq_img(6)), R(" and for sequences "),
           d.inline(D, eq_img(10, "jpeg")), R(", where d is the common difference.")),
         T("Summing the first n integers gives"),
         P(d.inline(D, eq_img(2))),
         T(LOREM[3])]
    d.build("".join(b))
    return d


def doc02():
    d = Doc("02_table_cells.docx")
    rows = [
        [T("Equation"), T("No.")],
        [P(d.inline(D, eq_img(3), container="table"), jc="center"), P(R("(1)"), jc="right")],
        [P(d.inline(D, eq_img(4), container="table"), jc="center"), P(R("(2)"), jc="right")],
        [P(d.inline(D, eq_img(14, "jpeg"), container="table"), jc="center"), P(R("(3)"), jc="right")],
    ]
    rows2 = [[T("where"), P(d.inline(D, eq_img(16), container="table"), jc="center"), P(R("(4)"), jc="right")]]
    b = [H("Numbered equations in tables"), T(LOREM[1]), T("Table 1 lists the key results."),
         table(rows, [7000, 1500]), T("The Gaussian integral is laid out in a three-column table:"),
         table(rows2, [1200, 6300, 1000]), T(LOREM[5])]
    d.build("".join(b))
    return d


def doc03():
    d = Doc("03_anchored.docx")
    b = [H("Floating equation pictures"),
         P(d.anchor(D, eq_img(5), x_emu=3657600, y_emu=0),
           R("This paragraph has a floating equation picture anchored to it; it is positioned at the "
             "right margin and wraps the text. " + LOREM[2])),
         T(LOREM[4]),
         P(d.anchor(D, eq_img(11, "jpeg"), x_emu=0, y_emu=91440)),
         T("Above is an empty paragraph holding only an anchored picture."),
         P(R("Another floating picture follows. "), d.anchor(D, eq_img(1), x_emu=2743200, y_emu=45720, behind=True),
           R("It is placed behind the text.")),
         T("The only regular inline equation in this document is:"),
         P(d.inline(D, eq_img(12)), jc="center"),
         T(LOREM[5])]
    d.build("".join(b))
    return d


def doc04():
    d = Doc("04_altcontent.docx")
    img = eq_img(13)
    rid = d.img_rid(D, img)
    choice = d.inline_drawing(D, img, alt="choice", rid=rid)
    fallback = d.vml_pict(D, img, alt="fallback", rid=rid)
    alt_pic = (f'<w:r><mc:AlternateContent><mc:Choice Requires="wps">{choice}</mc:Choice>'
               f'<mc:Fallback>{fallback}</mc:Fallback></mc:AlternateContent></w:r>')

    tb_img = eq_img(15)
    tb_rid = d.img_rid(D, tb_img)
    tb_cx, tb_cy = 2286000, 685800  # 2.5in x 0.75in
    pid = d.next_id()
    inner_choice = (P(R("Box 1: cube root")) +
                    P(d.inline(D, tb_img, container="textbox", alt="choice", rid=tb_rid), jc="center"))
    choice_tb = (
        '<w:drawing><wp:anchor distT="45720" distB="45720" distL="114300" distR="114300" simplePos="0" '
        f'relativeHeight="{251658240 + pid}" behindDoc="0" locked="0" layoutInCell="1" allowOverlap="1">'
        '<wp:simplePos x="0" y="0"/><wp:positionH relativeFrom="column"><wp:posOffset>3200400</wp:posOffset></wp:positionH>'
        '<wp:positionV relativeFrom="paragraph"><wp:posOffset>0</wp:posOffset></wp:positionV>'
        f'<wp:extent cx="{tb_cx}" cy="{tb_cy}"/><wp:effectExtent l="0" t="0" r="0" b="0"/>'
        f'<wp:wrapSquare wrapText="bothSides"/><wp:docPr id="{pid}" name="Text Box {pid}"/><wp:cNvGraphicFramePr/>'
        '<a:graphic><a:graphicData uri="http://schemas.microsoft.com/office/word/2010/wordprocessingShape">'
        '<wps:wsp><wps:cNvSpPr txBox="1"/><wps:spPr>'
        f'<a:xfrm><a:off x="0" y="0"/><a:ext cx="{tb_cx}" cy="{tb_cy}"/></a:xfrm>'
        '<a:prstGeom prst="rect"><a:avLst/></a:prstGeom><a:solidFill><a:srgbClr val="FFFFFF"/></a:solidFill>'
        '<a:ln w="6350"><a:solidFill><a:srgbClr val="000000"/></a:solidFill></a:ln></wps:spPr>'
        f'<wps:txbx><w:txbxContent>{inner_choice}</w:txbxContent></wps:txbx>'
        '<wps:bodyPr rot="0" vert="horz" wrap="square" lIns="91440" tIns="45720" rIns="91440" bIns="45720" '
        'anchor="t" anchorCtr="0"><a:noAutofit/></wps:bodyPr></wps:wsp></a:graphicData></a:graphic>'
        '</wp:anchor></w:drawing>'
    )
    inner_fb = (P(R("Box 1: cube root")) +
                P(d.inline(D, tb_img, container="textbox", alt="fallback", rid=tb_rid), jc="center"))
    fb_tb = (
        '<w:pict>' + d.shapetype(D, "202") +
        f'<v:shape id="Text Box {pid}" o:spid="_x0000_s{1025 + pid}" type="#_x0000_t202" '
        f'style="position:absolute;margin-left:252pt;margin-top:0;width:{tb_cx / EMU_PER_PT:.0f}pt;'
        f'height:{tb_cy / EMU_PER_PT:.0f}pt;z-index:251659264;mso-position-horizontal-relative:text;'
        'mso-position-vertical-relative:text" fillcolor="white" strokeweight=".5pt">'
        f'<v:textbox><w:txbxContent>{inner_fb}</w:txbxContent></v:textbox>'
        '<w10:wrap type="square"/></v:shape></w:pict>'
    )
    alt_tb = (f'<w:r><mc:AlternateContent><mc:Choice Requires="wps">{choice_tb}</mc:Choice>'
              f'<mc:Fallback>{fb_tb}</mc:Fallback></mc:AlternateContent></w:r>')
    b = [H("Markup compatibility content"), T(LOREM[0]),
         T("Gauss's law in differential form is shown below (DrawingML with a VML fallback):"),
         P(alt_pic, jc="center"),
         P(alt_tb, R("A text box floats to the right of this paragraph. It contains a caption and an "
                     "equation picture. " + LOREM[2] + " " + LOREM[4])),
         T(LOREM[3])]
    d.build("".join(b))
    return d


def doc05():
    d = Doc("05_vml_legacy.docx")
    b = [H("Legacy VML pictures"), T("This document was saved by an old word processor that uses VML."),
         T("The Gaussian integral:"),
         P(d.vml(D, eq_img(16)), jc="center"),
         P(R("The classic limit "), d.vml(D, eq_img(8)), R(" is proved with the squeeze theorem.")),
         P(d.vml(D, eq_img(19, "jpeg")), jc="center"),
         P(d.vml(D, eq_img(20), absolute=True),
           R("This paragraph carries an absolutely positioned VML picture that floats beside the text. "
             + LOREM[1])),
         T(LOREM[5])]
    d.build("".join(b))
    return d


def doc06():
    d = Doc("06_ole_equations.docx")
    b = [H("Equation Editor 3.0 objects"), T("The equations below are embedded Microsoft Equation 3.0 objects."),
         P(d.ole(D, eq_img(21)), jc="center"),
         T("The next object only has a Windows Metafile preview:"),
         P(d.ole(D, wmf_img()), jc="center"),
         P(R("An object inline in text: "), d.ole(D, eq_img(22)), R(" is Newton's second law.")),
         T(LOREM[3])]
    d.build("".join(b))
    return d


def doc07():
    d = Doc("07_emf_wmf.docx")
    b = [H("Metafile pictures"), T("The first picture is an Enhanced Metafile:"),
         P(d.inline(D, emf_img()), jc="center"),
         T("The second picture is a Windows Metafile:"),
         P(d.inline(D, wmf_img()), jc="center"),
         T("For comparison, a PNG equation:"),
         P(d.inline(D, eq_img(23)), jc="center"),
         T(LOREM[4])]
    d.build("".join(b))
    return d


def mr(t):
    return f'<m:r><w:rPr><w:rFonts w:ascii="Cambria Math" w:hAnsi="Cambria Math"/></w:rPr><m:t>{esc(t)}</m:t></m:r>'


def doc08():
    d = Doc("08_native_only.docx")
    frac = (f'<m:oMathPara><m:oMath><m:f><m:num>{mr("a+b")}</m:num><m:den>{mr("c")}</m:den></m:f>'
            f'{mr("=")}<m:f><m:num>{mr("a")}</m:num><m:den>{mr("c")}</m:den></m:f>{mr("+")}'
            f'<m:f><m:num>{mr("b")}</m:num><m:den>{mr("c")}</m:den></m:f></m:oMath></m:oMathPara>')
    subsup = (f'<m:oMath><m:sSubSup><m:e>{mr("x")}</m:e><m:sub>{mr("i")}</m:sub><m:sup>{mr("2")}</m:sup>'
              f'</m:sSubSup></m:oMath>')
    rad = (f'<m:oMathPara><m:oMath>{mr("r=")}<m:rad><m:radPr><m:degHide m:val="1"/></m:radPr><m:deg/>'
           f'<m:e><m:sSup><m:e>{mr("x")}</m:e><m:sup>{mr("2")}</m:sup></m:sSup>{mr("+")}'
           f'<m:sSup><m:e>{mr("y")}</m:e><m:sup>{mr("2")}</m:sup></m:sSup></m:e></m:rad></m:oMath></m:oMathPara>')
    nary = (f'<m:oMath><m:nary><m:naryPr><m:chr m:val="\u2211"/></m:naryPr><m:sub>{mr("k=1")}</m:sub>'
            f'<m:sup>{mr("n")}</m:sup><m:e>{mr("k")}</m:e></m:nary></m:oMath>')
    b = [H("Native Office Math"), T("All equations in this document are native OMML."),
         P(frac),
         P(R("The squared component "), subsup, R(" appears in the variance.")),
         P(rad),
         P(R("A sum written inline: "), nary, R(".")),
         T(LOREM[2])]
    d.build("".join(b))
    return d


def doc09():
    d = Doc("09_no_equations.docx")
    rows = [[T("Item"), T("Quantity"), T("Price")],
            [T("Notebook"), T("3"), T("4.50")],
            [T("Pencil"), T("12"), T("0.80")]]
    b = [H("Meeting notes"), T("This document contains only text and a table."), T(LOREM[0]), T(LOREM[1]),
         table(rows, [4000, 2000, 2000]), T(LOREM[4]), T("No pictures and no mathematics appear here.")]
    d.build("".join(b))
    return d


def doc10():
    d = Doc("10_logo_figure.docx")
    hdr_logo = logo_img(600, 300, 1.2, seed=1, label="header_logo")
    body_logo = logo_img(360, 180, 0.6, seed=2, label="small_logo")
    figure = logo_img(500, 440, 2.2, seed=3, fmt="jpeg", label="figure")
    ftr_pic = logo_img(240, 80, 0.35, seed=4, label="footer_logo")
    header = P(d.inline("word/header1.xml", hdr_logo, container="header")) + P(R("ACME Research Ltd. - Internal report"))
    footer = P(R("Page footer  ")) + P(d.inline("word/footer1.xml", ftr_pic, container="footer"), jc="right")
    b = [H("Report with logos and figures"),
         P(d.inline(D, body_logo)), T("The company logo above is a small inline colorful picture."),
         T(LOREM[0]),
         P(d.inline(D, figure), jc="center"),
         T("Figure 1: Experimental results (colorful figure taller than one inch)."),
         T("The only equation in this document:"),
         P(d.inline(D, eq_img(24)), jc="center"),
         T(LOREM[5])]
    d.build("".join(b), header=header, footer=footer)
    return d


def doc11():
    d = Doc("11_tracked_comments.docx")
    A1 = 'w:author="Alice Reviewer" w:date="2026-03-01T10:00:00Z"'
    A2 = 'w:author="Bob Editor" w:date="2026-03-02T09:30:00Z"'
    p1 = P(R("The following result "),
           f'<w:del w:id="101" {A1}><w:r><w:delText xml:space="preserve">was conjectured </w:delText></w:r></w:del>',
           f'<w:ins w:id="102" {A1}><w:r><w:t xml:space="preserve">is proved </w:t></w:r></w:ins>',
           R("in the appendix."))
    p2 = P('<w:commentRangeStart w:id="0"/>', R("Please double-check the constant in the next equation."),
           '<w:commentRangeEnd w:id="0"/>', '<w:r><w:commentReference w:id="0"/></w:r>')
    eq1 = P(d.inline(D, eq_img(0)), jc="center")
    p3 = P('<w:commentRangeStart w:id="1"/>', R("The uncertainty principle states"))
    eq2 = P(d.inline(D, eq_img(18)), jc="center")
    p4 = P(R("for any quantum state."), '<w:commentRangeEnd w:id="1"/>', '<w:r><w:commentReference w:id="1"/></w:r>')
    p5 = P(R("An equation that was added with tracked changes on:"))
    eq_ins = P(f'<w:ins w:id="103" {A2}>' + d.inline(D, eq_img(17, "jpeg")) + '</w:ins>', jc="center")
    p6 = P(f'<w:ins w:id="104" {A2}><w:r><w:t xml:space="preserve">This sentence was inserted. </w:t></w:r></w:ins>',
           R(LOREM[3]))
    comments = (
        '<w:comment w:id="0" w:author="Carol" w:date="2026-03-03T08:00:00Z" w:initials="C">'
        + P(R("Is the denominator correct?")) + '</w:comment>'
        '<w:comment w:id="1" w:author="Dave" w:date="2026-03-03T08:05:00Z" w:initials="D">'
        + P(R("This comment spans the equation paragraph.")) + '</w:comment>'
    )
    d.build("".join([H("Reviewed document"), p1, p2, eq1, p3, eq2, p4, p5, eq_ins, p6]), comments=comments)
    return d


def doc12():
    d = Doc("12_mixed_many.docx")
    reuse = eq_img(1)
    b = [H("Problem set with many equations"), T(LOREM[2])]
    # display (6)
    for i, f in [(0, "png"), (2, "jpeg"), (3, "png"), (7, "png"), (13, "jpeg"), (17, "png")]:
        b.append(T(f"Problem {len(b)}: evaluate the expression below."))
        b.append(P(d.inline(D, eq_img(i, f)), jc="center"))
    # inline in text (6)
    b.append(P(R("Recall "), d.inline(D, eq_img(9)), R(" and "), d.inline(D, eq_img(6, "jpeg")),
               R(", together with "), d.inline(D, eq_img(10)), R(".")))
    b.append(P(R("Also "), d.inline(D, eq_img(14)), R(", "), d.inline(D, eq_img(22)), R(" and "),
               d.inline(D, eq_img(23, "jpeg")), R(" hold.")))
    # table (6)
    rows = []
    for n, (i, f) in enumerate([(4, "png"), (5, "png"), (8, "jpeg"), (11, "png"), (19, "png"), (21, "jpeg")], 1):
        rows.append([P(d.inline(D, eq_img(i, f), container="table"), jc="center"), P(R(f"({n})"), jc="right")])
    b.append(table(rows, [7000, 1500]))
    # reuse: same rId twice + same media via a second rId
    b.append(T("The Pythagorean relation is repeated three times below (shared image)."))
    b.append(P(d.inline(D, reuse), jc="center"))
    b.append(P(d.inline(D, reuse), jc="center"))                 # same rId again
    b.append(P(d.inline(D, reuse, new_rid=True), jc="center"))   # same media part, new rId
    # one native equation mixed in
    b.append(P(R("A native equation for contrast: "),
               f'<m:oMath><m:sSup><m:e>{mr("e")}</m:e><m:sup>{mr("x")}</m:sup></m:sSup></m:oMath>'))
    b.append(T(LOREM[5]))
    d.build("".join(b))
    return d


DOCS = [doc01, doc02, doc03, doc04, doc05, doc06, doc07, doc08, doc09, doc10, doc11, doc12]


# --------------------------------------------------------------------------
def validate(files: list[Path]) -> list[str]:
    errors = []
    for f in files:
        with zipfile.ZipFile(f) as z:
            for n in z.namelist():
                if n.endswith(".xml") or n.endswith(".rels"):
                    try:
                        ET.fromstring(z.read(n))
                    except ET.ParseError as e:
                        errors.append(f"{f.name}:{n}: {e}")
    pdf_dir = TMP / "pdf"
    pdf_dir.mkdir(parents=True, exist_ok=True)
    for f in files:
        pdf = pdf_dir / (f.stem + ".pdf")
        if pdf.exists():
            pdf.unlink()
        try:
            subprocess.run([SOFFICE, f"-env:UserInstallation={LO_PROFILE}", "--headless", "--convert-to", "pdf",
                            "--outdir", str(pdf_dir), str(f)], capture_output=True, timeout=180)
        except subprocess.TimeoutExpired:
            errors.append(f"{f.name}: LibreOffice timeout")
            continue
        if not pdf.exists() or pdf.stat().st_size == 0:
            errors.append(f"{f.name}: LibreOffice produced no PDF")
        else:
            print(f"  LO ok: {f.name} -> {pdf.stat().st_size} bytes")
    return errors


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    docs = []
    files = []
    for fn in DOCS:
        d = fn()
        exp = d.write(OUT)
        docs.append(exp)
        files.append(OUT / d.fname)
        counts: dict[str, int] = {}
        for p in exp["pictures"]:
            counts[p["expected_category"]] = counts.get(p["expected_category"], 0) + 1
        print(f"{d.fname}: pictures={len(exp['pictures'])} {counts} omath={exp['native_omath']} "
              f"omathpara={exp['native_omathpara']}")
    (OUT / "expected.json").write_text(json.dumps({"docs": docs}, indent=2, ensure_ascii=False), encoding="utf-8")
    if "--validate" in sys.argv:
        errs = validate(files)
        print("VALIDATION:", "OK" if not errs else "\n".join(errs))
        return 1 if errs else 0
    return 0


if __name__ == "__main__":
    sys.exit(main())
