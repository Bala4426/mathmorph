import sys
from pptx import Presentation
from pptx.util import Inches, Pt
from pptx.dml.color import RGBColor
from pptx.enum.shapes import MSO_SHAPE
from pptx.enum.text import PP_ALIGN, MSO_ANCHOR

OUT = sys.argv[1]
INK = RGBColor(0x16, 0x1A, 0x3A)      # deep indigo (dominant)
INK2 = RGBColor(0x24, 0x2A, 0x55)
WHITE = RGBColor(0xFF, 0xFF, 0xFF)
SOFT = RGBColor(0xF4, 0xF5, 0xFB)
MUTED = RGBColor(0x5B, 0x60, 0x80)
CORAL = RGBColor(0xFF, 0x5E, 0x6C)
TEAL = RGBColor(0x14, 0xB8, 0xA6)
AMBER = RGBColor(0xF5, 0xA5, 0x24)
VIOLET = RGBColor(0x7C, 0x5C, 0xFF)
SKY = RGBColor(0x2D, 0x9C, 0xDB)
ACC = [CORAL, AMBER, TEAL, SKY, VIOLET, CORAL]
HEAD, BODY = "Cambria", "Calibri"

prs = Presentation()
prs.slide_width, prs.slide_height = Inches(13.333), Inches(7.5)
BLANK = prs.slide_layouts[6]


def bg(s, color):
    f = s.background.fill
    f.solid(); f.fore_color.rgb = color


def text(s, x, y, w, h, runs, size=16, color=INK, bold=False, font=BODY, align=PP_ALIGN.LEFT, anchor=MSO_ANCHOR.TOP, italic=False):
    tb = s.shapes.add_textbox(Inches(x), Inches(y), Inches(w), Inches(h))
    tf = tb.text_frame; tf.word_wrap = True
    tf.margin_left = tf.margin_right = tf.margin_top = tf.margin_bottom = 0
    tf.vertical_anchor = anchor
    paras = runs if isinstance(runs, list) else [runs]
    for i, p_text in enumerate(paras):
        p = tf.paragraphs[0] if i == 0 else tf.add_paragraph()
        p.alignment = align
        p.space_after = Pt(6)
        r = p.add_run(); r.text = p_text
        r.font.size = Pt(size); r.font.bold = bold; r.font.italic = italic; r.font.name = font; r.font.color.rgb = color
    return tb


def box(s, x, y, w, h, fill, shape=MSO_SHAPE.ROUNDED_RECTANGLE, radius=0.12, shadow=False):
    sh = s.shapes.add_shape(shape, Inches(x), Inches(y), Inches(w), Inches(h))
    sh.fill.solid(); sh.fill.fore_color.rgb = fill
    sh.line.fill.background()
    if shape == MSO_SHAPE.ROUNDED_RECTANGLE:
        sh.adjustments[0] = radius
    if not shadow:
        sh.shadow.inherit = False
    return sh


def badge(s, x, y, d, fill, label, size=20):
    c = box(s, x, y, d, d, fill, MSO_SHAPE.OVAL)
    tf = c.text_frame; tf.margin_left = tf.margin_right = 0
    p = tf.paragraphs[0]; p.alignment = PP_ALIGN.CENTER
    r = p.add_run(); r.text = label; r.font.size = Pt(size); r.font.bold = True; r.font.color.rgb = WHITE; r.font.name = BODY
    tf.vertical_anchor = MSO_ANCHOR.MIDDLE
    return c


def title(s, t, sub=None, dark=False):
    text(s, 0.7, 0.55, 12, 0.8, t, size=36, bold=True, font=HEAD, color=WHITE if dark else INK)
    if sub:
        text(s, 0.7, 1.35, 12, 0.5, sub, size=16, color=RGBColor(0xC9, 0xCC, 0xE8) if dark else MUTED)


def notes(s, n):
    s.notes_slide.notes_text_frame.text = n


def dots(s):
    # signature motif: a row of coloured dots, bottom-right
    for i, c in enumerate([CORAL, AMBER, TEAL, SKY, VIOLET]):
        box(s, 11.55 + i * 0.3, 6.95, 0.16, 0.16, c, MSO_SHAPE.OVAL)


# 1 — title
s = prs.slides.add_slide(BLANK); bg(s, INK)
for i, (c, x, y, d) in enumerate([(CORAL, 9.3, 0.8, 2.2), (AMBER, 11.0, 2.4, 1.5), (TEAL, 9.9, 3.6, 1.9), (VIOLET, 11.6, 4.9, 1.2), (SKY, 8.9, 5.6, 0.9)]):
    box(s, x, y, d, d, c, MSO_SHAPE.OVAL)
text(s, 0.8, 1.9, 8, 0.5, "DEMO", size=16, bold=True, color=AMBER)
text(s, 0.8, 2.4, 8, 1.3, "MathMorph", size=66, bold=True, font=HEAD, color=WHITE)
text(s, 0.8, 3.7, 7.8, 1.2, "Equation pictures and MathType objects → native, editable Word equations", size=24, color=RGBColor(0xDD, 0xE0, 0xF5))
text(s, 0.8, 5.4, 7.5, 0.5, "Runs 100% locally on Windows  ·  No document leaves the machine", size=16, color=RGBColor(0xA9, 0xAE, 0xD6))
box(s, 10.95, 4.0, 0.0001, 0.0001, INK)  # no-op spacer
t = s.shapes[-1]; t._element.getparent().remove(t._element)
lbl = text(s, 10.0, 4.25, 1.7, 0.6, "∑ ∫ √", size=30, bold=True, color=WHITE, align=PP_ALIGN.CENTER)
notes(s, "MathMorph takes Word documents whose equations are pictures or MathType objects and turns them into real Word equations you can edit, search and read aloud.")

# 2 — problem
s = prs.slides.add_slide(BLANK); bg(s, WHITE)
title(s, "Equations as pictures are a dead end", "Common in submitted manuscripts and legacy documents")
probs = [("✎", CORAL, "Not editable", "A typo in a subscript means redrawing the equation from scratch."),
         ("⌕", AMBER, "Not searchable", "Text search and copy-paste skip every symbol in the image."),
         ("♿", TEAL, "Not accessible", "Screen readers announce “picture” — the math is lost."),
         ("⇄", VIOLET, "Breaks pipelines", "XML / typesetting workflows cannot reuse picture math.")]
for i, (ic, c, h, d) in enumerate(probs):
    x = 0.7 + i * 3.05
    box(s, x, 2.3, 2.8, 3.9, SOFT)
    badge(s, x + 0.3, 2.6, 0.9, c, ic, 26)
    text(s, x + 0.3, 3.75, 2.3, 0.5, h, size=20, bold=True, font=HEAD)
    text(s, x + 0.3, 4.35, 2.3, 1.7, d, size=15, color=MUTED)
dots(s)
notes(s, "Why this matters: picture equations can't be edited, searched or read by assistive technology.")

# 3 — what it does (stat callouts)
s = prs.slides.add_slide(BLANK); bg(s, WHITE)
title(s, "What MathMorph delivers", "One click from picture math to OMML (Word’s native equation format)")
stats = [("100%", "local", "Offline OCR model, local Pandoc, local renderer", CORAL),
         ("Exact", "MathType", "Read straight from the embedded MTEF data — no guessing", TEAL),
         ("1 run", "changed", "Only the picture's run is replaced; every other byte is identical", VIOLET)]
for i, (big, small, d, c) in enumerate(stats):
    x = 0.7 + i * 4.05
    box(s, x, 2.2, 3.8, 4.1, c)
    text(s, x + 0.35, 2.55, 3.2, 1.1, big, size=60, bold=True, font=HEAD, color=WHITE)
    text(s, x + 0.35, 3.65, 3.2, 0.5, small.upper(), size=18, bold=True, color=WHITE)
    text(s, x + 0.35, 4.35, 3.1, 1.6, d, size=16, color=WHITE)
dots(s)
notes(s, "Three promises: nothing leaves the machine, MathType is converted exactly, and the document is otherwise untouched byte for byte.")

# 4 — pipeline
s = prs.slides.add_slide(BLANK); bg(s, SOFT)
title(s, "How a document flows through MathMorph")
steps = [("1", "Inventory", "Find & classify every picture"),
         ("2", "Recognise", "pix2text → LaTeX"),
         ("3", "Verify", "Re-render + SSIM score"),
         ("4", "Convert", "Pandoc → validated OMML"),
         ("5", "Splice", "Replace only that run"),
         ("6", "Gates", "Byte identity, re-scan, validator")]
for i, (n, h, d) in enumerate(steps):
    x = 0.55 + i * 2.07
    c = ACC[i] if i < 5 else VIOLET
    sh = box(s, x, 2.4, 1.95, 1.3, c, MSO_SHAPE.CHEVRON if i else MSO_SHAPE.PENTAGON)
    tf = sh.text_frame; p = tf.paragraphs[0]; p.alignment = PP_ALIGN.CENTER
    r = p.add_run(); r.text = n; r.font.size = Pt(30); r.font.bold = True; r.font.color.rgb = WHITE; r.font.name = HEAD
    text(s, x + 0.1, 4.0, 1.85, 0.5, h, size=18, bold=True, font=HEAD, align=PP_ALIGN.CENTER)
    text(s, x + 0.1, 4.5, 1.85, 1.0, d, size=14, color=MUTED, align=PP_ALIGN.CENTER)
box(s, 0.7, 5.75, 11.9, 0.85, WHITE)
text(s, 1.0, 5.9, 11.4, 0.6, "Anything uncertain is never guessed — it is marked NEEDS_REVIEW and shown on the review page.", size=17, bold=True, color=INK, anchor=MSO_ANCHOR.MIDDLE)
dots(s)
notes(s, "Walk through the six steps. Emphasise the safety net: items that fail a check go to review instead of being replaced.")

# 5 — two paths
s = prs.slides.add_slide(BLANK); bg(s, WHITE)
title(s, "Two conversion paths", "Pictures are recognised; MathType is read exactly")
for i, (c, h, tag, items) in enumerate([
        (CORAL, "Picture equations", "PNG / JPEG", ["pix2text (mfr-1.5, ONNX, CPU) reads the image", "LaTeX is re-rendered in Edge and scored (SSIM)", "Score ≥ threshold → converted; else review", "Logos, photos, charts filtered out by pixel stats"]),
        (TEAL, "MathType objects", "Equation.DSMT*", ["“Equation Native” stream read from the OLE .bin", "MTEF v5 parsed to a tree, written as LaTeX", "No OCR, no threshold — the result is exact", "Unknown symbols or templates → review, never guessed"])]):
    x = 0.7 + i * 6.1
    box(s, x, 2.15, 5.8, 4.45, SOFT)
    box(s, x, 2.15, 5.8, 1.0, c)
    text(s, x + 0.35, 2.3, 3.6, 0.7, h, size=24, bold=True, font=HEAD, color=WHITE, anchor=MSO_ANCHOR.MIDDLE)
    text(s, x + 3.8, 2.3, 1.75, 0.7, tag, size=13, bold=True, color=WHITE, align=PP_ALIGN.RIGHT, anchor=MSO_ANCHOR.MIDDLE)
    for j, it in enumerate(items):
        y = 3.45 + j * 0.75
        box(s, x + 0.35, y + 0.08, 0.22, 0.22, c, MSO_SHAPE.OVAL)
        text(s, x + 0.75, y, 4.8, 0.7, it, size=16, color=INK)
dots(s)
notes(s, "MathType is the strongest path: because the equation data is inside the file, the conversion is exact. Enable it with --mathtype convert or the GUI checkbox.")

# 6 — real result
s = prs.slides.add_slide(BLANK); bg(s, INK)
title(s, "Real manuscript: every equation converted", "123 equation pictures in a journal submission (96 distinct images)", dark=True)
for i, (num, lbl, c) in enumerate([("123", "of 123 now native Word equations", TEAL), ("107", "accepted as recognised", AMBER), ("16", "fixed via proofread overrides (13 images)", CORAL)]):
    x = 0.7 + i * 4.05
    box(s, x, 2.3, 3.8, 2.6, INK2)
    text(s, x + 0.35, 2.5, 3.2, 1.3, num, size=72, bold=True, font=HEAD, color=c)
    text(s, x + 0.35, 3.85, 3.2, 0.9, lbl, size=17, color=WHITE)
# proportion bar
tot = 12.0
box(s, 0.7, 5.35, tot * 107 / 123, 0.45, AMBER, MSO_SHAPE.RECTANGLE)
box(s, 0.7 + tot * 107 / 123, 5.35, tot * 16 / 123, 0.45, CORAL, MSO_SHAPE.RECTANGLE)
text(s, 0.7, 5.95, 11, 0.5, "Corrections were typical OCR slips: wrong symbol (f_s → f_5), lost subscript, minus vs hyphen.", size=15, color=RGBColor(0xC9, 0xCC, 0xE8))
notes(s, "Numbers from config/proofread/PROOFREAD.md. The review page made the 16 corrections quick: original picture and converted equation side by side.")

# 7 — safety
s = prs.slides.add_slide(BLANK); bg(s, WHITE)
title(s, "Built to be trusted with real documents")
items = [("🔒", CORAL, "Hardened input", "Zip-bomb limits, safe entry names, no XML entities"),
         ("≡", AMBER, "Byte identity", "Output differs only in the replaced runs"),
         ("↻", TEAL, "Re-inventory", "Pictures − converted = remaining; math count matches"),
         ("✓", SKY, "OpenXML validator", "No new schema errors vs the input"),
         ("▣", VIOLET, "Opens in LibreOffice", "Converted file renders to PDF"),
         ("⚑", CORAL, "Gate failure", "Output is renamed GATE3_FAILED")]
for i, (ic, c, h, d) in enumerate(items):
    col, row = i % 3, i // 3
    x, y = 0.7 + col * 4.05, 1.9 + row * 2.4
    box(s, x, y, 3.8, 2.1, SOFT)
    badge(s, x + 0.3, y + 0.35, 0.8, c, ic, 22)
    text(s, x + 1.3, y + 0.35, 2.35, 0.6, h, size=18, bold=True, font=HEAD)
    text(s, x + 1.3, y + 0.95, 2.35, 1.1, d, size=14, color=MUTED)
dots(s)
notes(s, "Gate 3 runs after every save. Any failure is visible in FINAL_REPORT.md and the file is renamed so it can't be mistaken for good output.")

# 8 — demo flow
s = prs.slides.add_slide(BLANK); bg(s, SOFT)
title(s, "Live demo: three steps in the app")
for i, (n, h, d, c) in enumerate([("1", "Add documents", "Drag .docx files or a folder onto the window. MathType equations are counted automatically.", CORAL),
                                   ("2", "Convert", "Pick a mode — safe, convert-all, or a custom threshold — and press Convert.", AMBER),
                                   ("3", "Review", "See each original next to its converted equation; fix any slip and re-run.", TEAL)]):
    x = 0.7 + i * 4.05
    box(s, x, 2.0, 3.8, 4.3, WHITE, shadow=True)
    badge(s, x + 0.35, 2.35, 1.1, c, n, 36)
    text(s, x + 0.35, 3.75, 3.1, 0.6, h, size=24, bold=True, font=HEAD)
    text(s, x + 0.35, 4.45, 3.1, 1.7, d, size=16, color=MUTED)
    if i < 2:
        a = box(s, x + 3.83, 3.95, 0.2, 0.4, c, MSO_SHAPE.RIGHT_ARROW)
dots(s)
notes(s, "Open MathMorph.Gui.exe. Use a corpus file with MathType (tests/corpus/06_ole_equations.docx) and the real manuscript. Show the review page and open the output in Word.")

# 9 — close
s = prs.slides.add_slide(BLANK); bg(s, INK)
for c, x, y, d in [(TEAL, 10.4, 0.6, 1.8), (CORAL, 11.6, 2.2, 1.1), (AMBER, 10.0, 4.8, 1.4)]:
    box(s, x, y, d, d, c, MSO_SHAPE.OVAL)
text(s, 0.8, 1.2, 9, 1.0, "Next steps", size=44, bold=True, font=HEAD, color=WHITE)
for j, (c, t) in enumerate([(CORAL, "Pilot on a batch of incoming manuscripts"),
                            (AMBER, "Turn on MathType conversion by default for production"),
                            (TEAL, "Feed proofread corrections back to tune the threshold"),
                            (VIOLET, "Extend MathType coverage to MTEF v3 (Equation Editor 3.0)")]):
    y = 2.7 + j * 0.9
    box(s, 0.85, y + 0.12, 0.28, 0.28, c, MSO_SHAPE.OVAL)
    text(s, 1.4, y, 8.2, 0.6, t, size=22, color=WHITE)
text(s, 0.8, 6.5, 9, 0.5, "MathMorph  ·  local, exact, reviewable", size=14, italic=True, color=RGBColor(0xA9, 0xAE, 0xD6))
notes(s, "Close with the ask: a pilot batch, and agreement to enable MathType conversion by default.")

prs.save(OUT)
print("saved", OUT)
