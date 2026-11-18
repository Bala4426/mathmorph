using System.Security;
using System.Xml.Linq;
using MathMorph.Contracts;

namespace MathMorph.Engine;

/// <summary>Builds the new text of a part by replacing only the runs of converted pictures.</summary>
public static class Rewriter
{
    public sealed record Replacement(PictureInfo Picture, XElement PictureElement, XElement Omml);

    public static string Rewrite(Inventory.PartScan scan, IReadOnlyList<Replacement> reps)
    {
        var sp = new XmlSplicer(scan.Text);
        var edits = new List<(int, int, string)>();
        var seenRuns = new HashSet<(int, int)>();
        foreach (var r in reps)
        {
            var loc = r.Picture.Run ?? throw new InvalidOperationException("picture has no replaceable run");
            var run = r.PictureElement.Ancestors(Ns.W + "r").First();
            var (rs, re) = sp.SpanOf(loc.Line, loc.Column, loc.QualifiedName);
            if (!seenRuns.Add((rs, re))) throw new InvalidOperationException("same run targeted twice");
            bool display = loc.ParagraphOnlyThisPicture && loc.ParentIsParagraph;
            var math = OmmlConverter.Serialize(r.Omml, display);
            if (!loc.RunAltContentChoice)
            {
                edits.Add((rs, re, math));
                continue;
            }
            // run > mc:AlternateContent > (mc:Choice > picture, mc:Fallback > legacy content)
            // becomes mc:AlternateContent > (mc:Choice > math, mc:Fallback > run with the untouched fallback content)
            var ac = run.Element(Ns.MC + "AlternateContent")!;
            var choice = ac.Element(Ns.MC + "Choice")!;
            var fb = ac.Element(Ns.MC + "Fallback");
            var (acs, ace) = sp.SpanOf(ac);
            var (cs, _) = sp.SpanOf(choice);
            string acStart = scan.Text[acs..(StartTagEnd(scan.Text, acs) + 1)];
            string choiceStart = scan.Text[cs..(StartTagEnd(scan.Text, cs) + 1)];
            string fbInner = "";
            string fbQn = Inventory.QualifiedName(ac).Split(':') is { Length: 2 } parts ? parts[0] + ":Fallback" : "Fallback";
            string fbStart = "<" + fbQn + ">";
            if (fb != null)
            {
                var (fs, fe) = sp.SpanOf(fb);
                int fst = StartTagEnd(scan.Text, fs);
                fbStart = scan.Text[fs..(fst + 1)];
                fbQn = Inventory.QualifiedName(fb);
                if (scan.Text[fst - 1] == '/') { fbStart = fbStart[..^2] + ">"; fbInner = ""; }
                else
                {
                    int closeAt = scan.Text.LastIndexOf("</", fe - 1, StringComparison.Ordinal);
                    fbInner = scan.Text[(fst + 1)..closeAt];
                }
            }
            string newRun = scan.Text[rs..acs] + fbInner + scan.Text[ace..re];
            string acQn = Inventory.QualifiedName(ac), chQn = Inventory.QualifiedName(choice);
            var repl = acStart + choiceStart + math + "</" + chQn + ">" + fbStart + newRun + "</" + fbQn + ">" + "</" + acQn + ">";
            edits.Add((rs, re, repl));
        }
        var text = XmlSplicer.Apply(scan.Text, edits);
        SafeXml.Parse(text, lineInfo: false); // must stay well-formed
        return text;
    }

    /// <summary>Index of the '>' closing the start tag that begins at <paramref name="lt"/>.</summary>
    public static int StartTagEnd(string text, int lt)
    {
        char q = '\0';
        for (int j = lt + 1; j < text.Length; j++)
        {
            char c = text[j];
            if (q != '\0') { if (c == q) q = '\0'; }
            else if (c == '"' || c == '\'') q = c;
            else if (c == '>') return j;
        }
        throw new InvalidOperationException("unterminated start tag");
    }

    public static string XmlAttr(string s) => SecurityElement.Escape(s) ?? "";
}
