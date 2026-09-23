using System.Text;

namespace StratAge.Core.Parsing;

public enum IngestCategory { Ok, FormatDamaged, IncompleteInput, ResourceBudgetExhausted, SemanticUnresolvable }

public sealed class Positioned
{
    public Positioned(int pos, int line, int col) { Pos = pos; Line = line; Col = col; }
    public int Pos { get; }
    public int Line { get; }
    public int Column { get; }
}

public sealed class JToken : Positioned
{
    public JToken(string kind, int pos, int line, int col) : base(pos, line, col) => Kind = kind;
    public string Kind { get; } // object | array | string | number | bool | null
    public List<(string key, JToken value, int keyLine, int keyCol)> Members { get; } = [];
    public List<JToken> Items { get; } = [];
    public string? Text { get; set; }
    public double Number { get; set; }
    public JToken? Get(string key) { foreach (var m in Members) if (m.key == key) return m.value; return null; }
}

public sealed class ParseFailure
{
    public required IngestCategory Category { get; init; }
    public required Diag Diagnostic { get; init; }
}

public sealed class JsonParser
{
    readonly string _s;
    int _i;
    int _line = 1, _col = 1;
    int _depth;
    long _tokens;
    readonly int _maxDepth;
    readonly long _maxTokens;

    JsonParser(string s, int maxDepth, long maxTokens) { _s = s; _maxDepth = maxDepth; _maxTokens = maxTokens; }

    public static (JToken? root, ParseFailure? failure) Parse(string s, int maxDepth = 64, long maxTokens = 200_000)
        => new JsonParser(s, maxDepth, maxTokens).ParseRoot();

    (JToken?, ParseFailure?) ParseRoot()
    {
        SkipWs();
        var t = ParseValue();
        if (t == null) return (null, _failure!);
        SkipWs();
        if (_i != _s.Length)
            return (null, Fail(IngestCategory.FormatDamaged, "JSON_TRAILING", "根值之后存在多余字符"));
        return (t, null);
    }

    ParseFailure? _failure;
    (JToken?, ParseFailure?) FailT(IngestCategory cat, string code, string msg) => (null, Fail(cat, code, msg));
    ParseFailure Fail(IngestCategory cat, string code, string msg)
        => _failure ??= new ParseFailure { Category = cat, Diagnostic = new Diag(code, msg, null, _line, _col) };

    void Advance(int n)
    {
        for (var k = 0; k < n; k++)
        {
            if (_i < _s.Length && _s[_i] == '\n') { _line++; _col = 1; } else _col++;
            _i++;
        }
    }

    void SkipWs()
    {
        while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) Advance(1);
    }

    JToken? ParseValue()
    {
        if (++_tokens > _maxTokens) { Fail(IngestCategory.ResourceBudgetExhausted, "BUDGET_TOKENS", "输入词元数量超过资源预算"); return null; }
        SkipWs();
        if (_i >= _s.Length) { Fail(IngestCategory.FormatDamaged, "JSON_EOF", "输入在值完成前结束"); return null; }
        var (pos, line, col) = (_i, _line, _col);
        var c = _s[_i];
        if (c == '{') return ParseObject(pos, line, col);
        if (c == '[') return ParseArray(pos, line, col);
        if (c == '"') { var str = ParseString(pos, line, col); return str == null ? null : new JToken("string", pos, line, col) { Text = str }; }
        if (c == 't' || c == 'f') return ParseBool(pos, line, col);
        if (c == 'n') return ParseNull(pos, line, col);
        if (c == '-' || char.IsDigit(c)) return ParseNumber(pos, line, col);
        Fail(IngestCategory.FormatDamaged, "JSON_UNEXPECTED", $"意外字符 '{c}'");
        return null;
    }

    JToken? ParseObject(int pos, int line, int col)
    {
        if (++_depth > _maxDepth) { Fail(IngestCategory.ResourceBudgetExhausted, "BUDGET_DEPTH", "嵌套深度超过资源预算"); return null; }
        var node = new JToken("object", pos, line, col);
        Advance(1); SkipWs();
        if (_i < _s.Length && _s[_i] == '}') { Advance(1); _depth--; return node; }
        while (true)
        {
            SkipWs();
            if (_i >= _s.Length || _s[_i] != '"') { Fail(IngestCategory.FormatDamaged, "JSON_KEY", "对象成员必须以字符串键开始"); return null; }
            var (kl, kc) = (_line, _col);
            var kpos = _i;
            var key = ParseString(kpos, kl, kc);
            if (key == null) return null;
            SkipWs();
            if (_i >= _s.Length || _s[_i] != ':') { Fail(IngestCategory.FormatDamaged, "JSON_COLON", "键后缺少 ':'"); return null; }
            Advance(1);
            var val = ParseValue();
            if (val == null) return null;
            node.Members.Add((key, val, kl, kc));
            SkipWs();
            if (_i >= _s.Length) { Fail(IngestCategory.FormatDamaged, "JSON_EOF", "对象未闭合"); return null; }
            if (_s[_i] == ',') { Advance(1); continue; }
            if (_s[_i] == '}') { Advance(1); break; }
            Fail(IngestCategory.FormatDamaged, "JSON_COMMA", "对象成员之间需要 ','"); return null;
        }
        _depth--;
        return node;
    }

    JToken? ParseArray(int pos, int line, int col)
    {
        if (++_depth > _maxDepth) { Fail(IngestCategory.ResourceBudgetExhausted, "BUDGET_DEPTH", "嵌套深度超过资源预算"); return null; }
        var node = new JToken("array", pos, line, col);
        Advance(1); SkipWs();
        if (_i < _s.Length && _s[_i] == ']') { Advance(1); _depth--; return node; }
        while (true)
        {
            var val = ParseValue();
            if (val == null) return null;
            node.Items.Add(val);
            SkipWs();
            if (_i >= _s.Length) { Fail(IngestCategory.FormatDamaged, "JSON_EOF", "数组未闭合"); return null; }
            if (_s[_i] == ',') { Advance(1); continue; }
            if (_s[_i] == ']') { Advance(1); break; }
            Fail(IngestCategory.FormatDamaged, "JSON_COMMA", "数组元素之间需要 ','"); return null;
        }
        _depth--;
        return node;
    }

    string? ParseString(int pos, int line, int col)
    {
        Advance(1);
        var sb = new StringBuilder();
        while (true)
        {
            if (_i >= _s.Length) { Fail(IngestCategory.FormatDamaged, "JSON_EOF", "字符串未闭合"); return null; }
            var c = _s[_i];
            if (c == '"') { Advance(1); return sb.ToString(); }
            if (c == '\\')
            {
                if (_i + 1 >= _s.Length) { Fail(IngestCategory.FormatDamaged, "JSON_EOF", "转义序列被截断"); return null; }
                var e = _s[_i + 1];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (_i + 5 >= _s.Length || !int.TryParse(_s.AsSpan(_i + 2, 4), System.Globalization.NumberStyles.HexNumber, null, out var hex))
                        { Fail(IngestCategory.FormatDamaged, "JSON_UNICODE", "无效的 \\u 转义"); return null; }
                        sb.Append((char)hex); Advance(4); break;
                    default: Fail(IngestCategory.FormatDamaged, "JSON_ESCAPE", $"无效转义 '\\{e}'"); return null;
                }
                Advance(2);
            }
            else if (c < 0x20) { Fail(IngestCategory.FormatDamaged, "JSON_CONTROL", "字符串中出现未转义控制字符"); return null; }
            else { sb.Append(c); Advance(1); }
        }
    }

    JToken? ParseBool(int pos, int line, int col)
    {
        if (_s.AsSpan(_i).StartsWith("true")) { Advance(4); return new JToken("bool", pos, line, col) { Number = 1 }; }
        if (_s.AsSpan(_i).StartsWith("false")) { Advance(5); return new JToken("bool", pos, line, col) { Number = 0 }; }
        Fail(IngestCategory.FormatDamaged, "JSON_LITERAL", "非法字面量（期望 true/false/null）");
        return null;
    }

    JToken? ParseNull(int pos, int line, int col)
    {
        if (_s.AsSpan(_i).StartsWith("null")) { Advance(4); return new JToken("null", pos, line, col); }
        Fail(IngestCategory.FormatDamaged, "JSON_LITERAL", "非法字面量（期望 null）");
        return null;
    }

    JToken? ParseNumber(int pos, int line, int col)
    {
        var start = _i;
        if (_s[_i] == '-') Advance(1);
        while (_i < _s.Length && char.IsDigit(_s[_i])) Advance(1);
        if (_i < _s.Length && _s[_i] == '.')
        {
            Advance(1);
            if (_i >= _s.Length || !char.IsDigit(_s[_i])) { Fail(IngestCategory.FormatDamaged, "JSON_NUMBER", "小数点后需要数字"); return null; }
            while (_i < _s.Length && char.IsDigit(_s[_i])) Advance(1);
        }
        if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
        {
            Advance(1);
            if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) Advance(1);
            if (_i >= _s.Length || !char.IsDigit(_s[_i])) { Fail(IngestCategory.FormatDamaged, "JSON_NUMBER", "指数部分需要数字"); return null; }
            while (_i < _s.Length && char.IsDigit(_s[_i])) Advance(1);
        }
        if (!double.TryParse(_s.AsSpan(start, _i - start), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n))
        { Fail(IngestCategory.FormatDamaged, "JSON_NUMBER", "无法解析的数字"); return null; }
        return new JToken("number", pos, line, col) { Number = n };
    }
}
