// Engine contracts. Owned by the lead engineer; subagents must not change these.
namespace MathMorph.Contracts;

/// <summary>How a picture element is embedded.</summary>
public enum PictureKind { Inline, Anchor, Vml, VmlFloating, Ole }

/// <summary>Detection category. Every picture element gets exactly one (Gate 0).</summary>
public enum Category { Candidate, OlePreview, Excluded, UnsupportedFormat, Native }

/// <summary>Final per-item decision written to report.csv.</summary>
public enum Status { CONVERTED, NEEDS_REVIEW, UNSUPPORTED_FORMAT, OLE_PREVIEW, EXCLUDED, NO_IMAGE }

public enum AltContentBranch { None, Choice, Fallback }

public sealed class PictureInfo
{
    public required string Part { get; init; }            // e.g. word/document.xml
    public required string Container { get; init; }       // body, table, textbox, header, footer, footnote, endnote, comments
    public required int ParaIndex { get; init; }          // 0-based index among all w:p in the part (document order)
    public required int Ordinal { get; init; }            // 0-based order of picture elements within the part
    public required PictureKind Kind { get; init; }
    public string? RelId { get; init; }
    public string? Target { get; init; }                  // resolved part name, e.g. word/media/image1.png
    public bool ExternalLink { get; init; }
    public string Format { get; set; } = "unknown";       // png, jpeg, emf, wmf, gif, bmp, tiff, svg, unknown
    public long ExtentCx { get; init; }
    public long ExtentCy { get; init; }
    public bool ParagraphHasOtherContent { get; init; }   // other text or other runs with visible content
    public AltContentBranch AltContent { get; init; }
    public string DocPrName { get; init; } = "";
    public string DocPrDescr { get; init; } = "";
    public string? Sha256 { get; set; }
    public Category Category { get; set; }
    public string Reason { get; set; } = "";
    /// <summary>Opaque locator for the run element to replace (line/col + run prefix). Null if not replaceable.</summary>
    public RunLocator? Run { get; init; }
    /// <summary>OLE objects (w:object) only: the o:OLEObject ProgID (e.g. Equation.DSMT4) and its embedded part.</summary>
    public string? OleProgId { get; init; }
    public string? OleTarget { get; init; }
}

public sealed record RunLocator(int Line, int Column, string QualifiedName, bool OnlyPictureInRun,
    bool ParentIsParagraph, bool ParagraphOnlyThisPicture, bool RunAltContentChoice, string? ChoiceRequires);

public sealed class DocumentInventory
{
    public required string File { get; init; }
    public List<PictureInfo> Pictures { get; } = new();
    public int NativeOMath { get; set; }       // m:oMath elements (all)
    public int NativeOMathPara { get; set; }   // m:oMathPara elements
    public string MainPart { get; set; } = "word/document.xml";
}

public sealed class RecognitionResult
{
    public string Path { get; set; } = "";
    public bool Ok { get; set; }
    public string Latex { get; set; } = "";
    public string Error { get; set; } = "";
    public double Seconds { get; set; }
    public double ColorfulFraction { get; set; } = -1;   // fraction of saturated pixels
    public double MidtoneFraction { get; set; } = -1;    // fraction of mid-gray pixels (photo-likeness)
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class VerifyResult
{
    public bool Ok { get; set; }
    public double Ssim { get; set; } = -1;
    public string RenderPath { get; set; } = "";
    public string Error { get; set; } = "";
}

public sealed class ItemDecision
{
    public required PictureInfo Picture { get; init; }
    public Status Status { get; set; }
    public double Score { get; set; } = -1;
    public string Latex { get; set; } = "";
    public string Note { get; set; } = "";
    public double RecognitionSeconds { get; set; }
    public string? OriginalImagePath { get; set; }
    public string? RenderPath { get; set; }
    public string? Omml { get; set; }   // validated OMML (m:oMath element) when CONVERTED
}

public sealed class SafetyLimits
{
    public int MaxEntries { get; init; } = 10_000;
    public long MaxTotalUncompressed { get; init; } = 1L << 30;      // 1 GiB
    public long MaxEntryUncompressed { get; init; } = 512L << 20;    // 512 MiB
    public double MaxCompressionRatio { get; init; } = 200.0;        // per entry, applies when entry > 1 MiB
    public long MaxInputFileBytes { get; init; } = 512L << 20;
}

public sealed class ConvertOptions
{
    public double MinScore { get; set; } = 0.95;
    public bool DryRun { get; set; }
    public bool Force { get; set; }
    public string? WorkRoot { get; set; }
    public string ToolsRoot { get; set; } = "";
    public SafetyLimits Limits { get; set; } = new();
    public bool RunGateChecks { get; set; } = true;
    /// <summary>Convert MathType OLE equations (Equation.DSMT*) from their embedded equation data (default on; --mathtype skip turns it off).</summary>
    public bool ConvertMathType { get; set; } = true;
    /// <summary>Proofreader corrections: image SHA-256 (lower-case hex) -> LaTeX to use instead of recognition.</summary>
    public Dictionary<string, string> LatexOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
