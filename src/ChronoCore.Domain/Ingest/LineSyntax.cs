using System.Globalization;
using System.Text;

namespace ChronoCore.Domain.Ingest;

/// <summary>One parsed physical line of the domain archive format (.dac).
/// Column positions are retained so every diagnostic is locatable.</summary>
public sealed record SourceLine(int Number, string Keyword, string? Id, IReadOnlyList<Field> Fields, string Raw)
{
    public string? Value(string name) => Fields.FirstOrDefault(f => f.Name == name)?.RawValue;
}

public sealed record Field(string Name, string RawValue, int Column);

/// <summary>Line-oriented lexer for the core chronology archive format.
///
/// Grammar (line based, '#' comments, continuation with trailing '\'):
///   CASE id=... version=...
///   DATUM id=.. name=.. axis=.. direction=.. depth=..
///   SEGMENT id=.. seq=.. top=.. bottom=.. lith=..
///   BOUNDARY id=.. kind=.. at=.. missing=..
///   ANCHOR id=.. label=.. at=.. age=.. sigma=.. reservoir=.. method=.. dist=.. q=.. ...
///   EVENT id=.. corr=.. at=.. kind=.. thick=.. age=.. basis=..
///   EXCLUDE id=.. target=.. reason=.. note=..
/// </summary>
public static class LineSyntax
{
    public sealed record LexResult(IReadOnlyList<SourceLine> Lines, IReadOnlyList<Model.Diagnostic> Diagnostics, bool BudgetHit);

    public static LexResult Lex(string text, ParseBudget budget)
    {
        var diags = new List<Model.Diagnostic>();
        var lines = new List<SourceLine>();
        var physical = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < physical.Length; i++)
        {
            budget.TickLine();
            var raw = physical[i];
            var body = StripComment(raw).Trim();
            if (body.Length == 0) continue;
            try
            {
                var (kw, id, fields, ok) = Tokenize(body, i + 1, budget, diags);
                if (ok) lines.Add(new SourceLine(i + 1, kw, id, fields, raw));
            }
            catch (BudgetExhaustedException)
            {
                diags.Add(new Model.Diagnostic(Model.DiagSeverity.BudgetExhausted, "PARSE_BUDGET",
                    "解析资源预算（记号/行长）已耗尽，剩余输入未处理", budget.SourceId, i + 1, 1));
                return new LexResult(lines, diags, true);
            }
        }
        return new LexResult(lines, diags, false);
    }

    private static string StripComment(string s)
    {
        var inStr = false;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '"') inStr = !inStr;
            else if (s[i] == '#' && !inStr) return s[..i];
        }
        return s;
    }

    private static (string Kw, string? Id, IReadOnlyList<Field> Fields, bool Ok) Tokenize(
        string body, int lineNo, ParseBudget budget, List<Model.Diagnostic> diags)
    {
        var fields = new List<Field>();
        var pos = 0;
        string ReadToken()
        {
            var sb = new StringBuilder();
            while (pos < body.Length && char.IsWhiteSpace(body[pos])) pos++;
            if (pos >= body.Length) return sb.ToString();
            if (body[pos] == '"')
            {
                pos++;
                while (pos < body.Length && body[pos] != '"')
                {
                    budget.TickChar();
                    sb.Append(body[pos++]);
                }
                if (pos < body.Length) pos++;
            }
            else
            {
                while (pos < body.Length && !char.IsWhiteSpace(body[pos]))
                {
                    budget.TickChar();
                    sb.Append(body[pos++]);
                }
            }
            return sb.ToString();
        }

        var first = ReadToken();
        if (string.IsNullOrEmpty(first))
        {
            diags.Add(new Model.Diagnostic(Model.DiagSeverity.CorruptFormat, "SYNTAX_EMPTY",
                "空记录", budget.SourceId, lineNo, 1));
            return (first ?? "", null, fields, false);
        }
        var keyword = first.ToUpperInvariant();
        string? id = null;
        while (pos < body.Length)
        {
            var col = pos + 1;
            var tok = ReadToken();
            if (tok.Length == 0) break;
            var eq = tok.IndexOf('=');
            if (eq <= 0)
            {
                diags.Add(new Model.Diagnostic(Model.DiagSeverity.CorruptFormat, "SYNTAX_FIELD",
                    $"字段必须为 name=value，收到“{tok}”", budget.SourceId, lineNo, col));
                return (keyword, id, fields, false);
            }
            var name = tok[..eq];
            var val = tok[(eq + 1)..];
            if (name == "id") id = val;
            fields.Add(new Field(name, val, col));
            budget.TickField();
        }
        return (keyword, id, fields, true);
    }
}

/// <summary>Explicit, small resource budget. Exhaustion is a first-class
/// outcome distinct from corruption and semantic impossibility.</summary>
public sealed class ParseBudget
{
    public int MaxLines { get; }
    public int MaxChars { get; }
    public int MaxFields { get; }
    public string SourceId { get; }
    public int Lines { get; private set; }
    public int Chars { get; private set; }
    public int Fields { get; private set; }

    public ParseBudget(string sourceId, int maxLines = 20_000, int maxChars = 2_000_000, int maxFields = 200_000)
    {
        SourceId = sourceId;
        MaxLines = maxLines;
        MaxChars = maxChars;
        MaxFields = maxFields;
    }

    public void TickLine() { if (++Lines > MaxLines) throw new BudgetExhaustedException(); }
    public void TickChar() { if (++Chars > MaxChars) throw new BudgetExhaustedException(); }
    public void TickField() { if (++Fields > MaxFields) throw new BudgetExhaustedException(); }
}

public sealed class BudgetExhaustedException : Exception;
