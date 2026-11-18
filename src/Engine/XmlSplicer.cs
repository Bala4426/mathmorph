using System.Xml;
using System.Xml.Linq;

namespace MathMorph.Engine;

/// <summary>
/// Maps parsed elements back to exact character spans in the original part text, so a part can be
/// edited by replacing only specific element spans. All other characters stay byte-identical.
/// </summary>
public sealed class XmlSplicer
{
    private readonly string _text;
    private readonly List<int> _lineStarts = new() { 0 };

    public XmlSplicer(string text)
    {
        _text = text;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') i++; _lineStarts.Add(i + 1); }
            else if (c == '\n') _lineStarts.Add(i + 1);
        }
    }

    public string Text => _text;

    /// <summary>Returns [start,end) of the element whose start tag name begins at (line, col), verified by name.</summary>
    public (int Start, int End) SpanOf(int line, int col, string qualifiedName)
    {
        if (line < 1 || line > _lineStarts.Count) throw new InvalidOperationException("line out of range");
        int nameAt = _lineStarts[line - 1] + col - 1;
        int lt = nameAt - 1;
        if (lt < 0 || _text[lt] != '<' || string.CompareOrdinal(_text, nameAt, qualifiedName, 0, qualifiedName.Length) != 0)
            throw new InvalidOperationException($"element '{qualifiedName}' not found at {line}:{col}");
        char after = _text[nameAt + qualifiedName.Length];
        if (!(after == ' ' || after == '>' || after == '/' || after == '\t' || after == '\r' || after == '\n'))
            throw new InvalidOperationException("element name mismatch at " + line + ":" + col);
        return (lt, FindElementEnd(lt));
    }

    public (int Start, int End) SpanOf(XElement e)
    {
        var li = (IXmlLineInfo)e;
        if (!li.HasLineInfo()) throw new InvalidOperationException("no line info");
        return SpanOf(li.LineNumber, li.LinePosition, Inventory.QualifiedName(e));
    }

    /// <summary>Given index of '&lt;' of a start tag, returns index just past the matching end tag.</summary>
    public int FindElementEnd(int lt)
    {
        int depth = 0, i = lt;
        while (i < _text.Length)
        {
            if (_text[i] != '<') { i = _text.IndexOf('<', i); if (i < 0) break; continue; }
            if (Starts(i, "<!--")) { i = Need(_text.IndexOf("-->", i + 4, StringComparison.Ordinal)) + 3; continue; }
            if (Starts(i, "<![CDATA[")) { i = Need(_text.IndexOf("]]>", i + 9, StringComparison.Ordinal)) + 3; continue; }
            if (Starts(i, "<?")) { i = Need(_text.IndexOf("?>", i + 2, StringComparison.Ordinal)) + 2; continue; }
            if (Starts(i, "</"))
            {
                i = Need(_text.IndexOf('>', i)) + 1;
                depth--;
                if (depth == 0) return i;
                continue;
            }
            // start tag: scan to '>' honouring quoted attribute values
            int j = i + 1;
            char q = '\0';
            while (j < _text.Length)
            {
                char c = _text[j];
                if (q != '\0') { if (c == q) q = '\0'; }
                else if (c == '"' || c == '\'') q = c;
                else if (c == '>') break;
                j++;
            }
            Need(j < _text.Length ? j : -1);
            bool selfClosing = _text[j - 1] == '/';
            i = j + 1;
            if (!selfClosing) depth++;
            else if (depth == 0) return i;
        }
        throw new InvalidOperationException("unterminated element");
    }

    private bool Starts(int i, string s) => string.CompareOrdinal(_text, i, s, 0, s.Length) == 0;
    private static int Need(int idx) => idx >= 0 ? idx : throw new InvalidOperationException("malformed XML near splice");

    /// <summary>Applies non-overlapping replacements and returns the new text.</summary>
    public static string Apply(string text, IEnumerable<(int Start, int End, string Replacement)> edits)
    {
        var list = edits.OrderBy(e => e.Start).ToList();
        for (int k = 1; k < list.Count; k++)
            if (list[k].Start < list[k - 1].End) throw new InvalidOperationException("overlapping edits");
        var sb = new System.Text.StringBuilder(text.Length);
        int pos = 0;
        foreach (var (s, e, r) in list)
        {
            sb.Append(text, pos, s - pos);
            sb.Append(r);
            pos = e;
        }
        sb.Append(text, pos, text.Length - pos);
        return sb.ToString();
    }
}
