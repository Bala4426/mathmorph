# MathMorph

**Turns equation pictures and MathType objects in Word documents into native, editable Word equations — fully offline.**

Many manuscripts arrive with equations pasted as images or embedded as legacy MathType objects. They can't be
edited, searched or read by screen readers. MathMorph finds them, converts them to Word's own equation format
(OMML) and leaves every other byte of the document untouched.

![.NET 8](https://img.shields.io/badge/.NET-8-512BD4) ![Python 3.13](https://img.shields.io/badge/Python-3.13-3776AB) ![Windows](https://img.shields.io/badge/platform-Windows-0078D6) ![Tests](https://img.shields.io/badge/unit%20tests-99%20passing-2EA043)

## Highlights

- **Two conversion paths**
  - *Pictures* (PNG/JPEG): recognised to LaTeX by a local OCR model (pix2text, ONNX on CPU), re-rendered and
    scored against the original with SSIM before anything is replaced.
  - *MathType objects*: the equation is read **exactly** from the embedded MTEF binary data (own OLE compound-file
    and MTEF v5 parsers) — no OCR, no guessing. On a real 123-equation manuscript: 123/123 converted.
- **Surgical edits**: the changed XML runs are spliced into the original text, so the output differs from the input
  only where an equation was replaced. A post-save gate proves it (byte identity, re-inventory, Open XML SDK
  validation, LibreOffice open check).
- **Safe with untrusted files**: bounded zip reader (zip-bomb limits), hardened XML parsing, bounds-checked binary
  parsers, isolated Python workers.
- **Proofreading UI** (WinForms): side-by-side original vs rendered equation, one-click LaTeX check through the same
  converter the pipeline uses, saved corrections re-applied in seconds thanks to a persistent recognition cache.
- **Nothing leaves the machine**: OCR model, Pandoc and the renderer all run locally.

## How it works

```
.docx ─► SafePackage ─► Inventory ─┬─► MathType (MTEF → LaTeX) ───────────────┐
                                   └─► pix2text OCR ─► render + SSIM score ───┤
                                                                              ▼
            Gates ◄─ Save ◄─ XmlSplicer (replace exact spans) ◄─ Pandoc LaTeX → OMML (validated)
```

Anything uncertain is never guessed: it is marked **NEEDS_REVIEW** and shown in the Review screen.
Details: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), design decisions: [docs/DECISIONS.md](docs/DECISIONS.md).

## Tech

| Area | Stack |
|---|---|
| Engine + CLI | C# / .NET 8, built-in libraries only (System.IO.Compression, System.Xml.Linq) |
| GUI | Windows Forms, custom-drawn design system, high-DPI aware |
| Recognition / verification | Python workers over a JSON-lines protocol: pix2text (ONNX), Pandoc, headless Edge, scikit-image SSIM |
| Validation | Open XML SDK validator (separate process), LibreOffice |
| Tests | 99 unit tests + 12 synthetic corpus documents covering inline/display, tables, anchored images, AlternateContent, VML, OLE, EMF/WMF, tracked changes |

## Project layout

```
src/        engine and CLI (MathMorph.exe)
gui/        Windows Forms front end (MathMorph.Gui.exe)
workers/    Python recognition and verification workers
tests/      unit tests and synthetic test corpus
scripts/    corpus generator, checks, model download
docs/       user guide, architecture, decisions, gates
demo/       demo slide deck and its generator
```

## Getting started

Requirements: Windows x64, .NET 8 SDK, Python 3.13, Microsoft Edge. LibreOffice is optional.

```powershell
# Python environment and offline models
py -3.13 -m venv .venv
.venv\Scripts\python.exe -m pip install pix2text onnxruntime scikit-image pypandoc_binary
Copy-Item .venv\Lib\site-packages\pypandoc\files\pandoc.exe tools\pandoc\
.venv\Scripts\python.exe -I scripts\download_models.py

# Build
dotnet publish src\MathMorph.csproj -c Release -r win-x64 --self-contained true -o bin
dotnet publish gui\MathMorph.Gui.csproj -c Release -r win-x64 --self-contained true -o gui_bin

# Run
Start-MathMorph.bat                                    # GUI
bin\MathMorph.exe input\paper.docx output --min-score 0  # CLI
powershell -File tests\Unit\run_tests.ps1               # tests
```

Full usage, statuses and report formats: [docs/README.md](docs/README.md).
