using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ChronoCore.Domain.Common;

/// <summary>
/// Minimal canonical JSON serializer. Property order is explicit (declaration order on
/// the dictionaries the domain builds), doubles are round-trippable invariant strings,
/// so two equivalent domain inputs hash identically regardless of machine culture.
/// </summary>
public static class CanonicalJson
{
    public static string Serialize(object? value)
    {
        var sb = new StringBuilder();
        Write(sb, value);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                break;
            case string s:
                WriteString(sb, s);
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case IConvertible c:
                WriteScalar(sb, c);
                break;
            case IDictionary<string, object?> d:
                WriteObject(sb, d);
                break;
            case IReadOnlyDictionary<string, object?> rd:
                WriteObject(sb, rd);
                break;
            case IEnumerable<object?> e:
                WriteArray(sb, e);
                break;
            default:
                WriteString(sb, value.ToString() ?? string.Empty);
                break;
        }
    }

    private static void WriteScalar(StringBuilder sb, IConvertible c)
    {
        switch (c.GetTypeCode())
        {
            case TypeCode.Single:
            case TypeCode.Double:
            case TypeCode.Decimal:
                var d = c.ToDouble(CultureInfo.InvariantCulture);
                sb.Append(double.IsNaN(d) || double.IsInfinity(d)
                    ? "null"
                    : d.ToString("R", CultureInfo.InvariantCulture));
                break;
            case TypeCode.Int16:
            case TypeCode.Int32:
            case TypeCode.Int64:
            case TypeCode.UInt16:
            case TypeCode.UInt32:
            case TypeCode.UInt64:
            case TypeCode.Byte:
            case TypeCode.SByte:
                sb.Append(c.ToInt64(CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                break;
            case TypeCode.Boolean:
                sb.Append(c.ToBoolean(CultureInfo.InvariantCulture) ? "true" : "false");
                break;
            default:
                WriteString(sb, c.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                break;
        }
    }

    private static void WriteObject(StringBuilder sb, IReadOnlyDictionary<string, object?> d)
    {
        sb.Append('{');
        var first = true;
        foreach (var kv in d)
        {
            if (!first) sb.Append(',');
            first = false;
            WriteString(sb, kv.Key);
            sb.Append(':');
            Write(sb, kv.Value);
        }
        sb.Append('}');
    }

    private static void WriteArray(StringBuilder sb, IEnumerable<object?> e)
    {
        sb.Append('[');
        var first = true;
        foreach (var item in e)
        {
            if (!first) sb.Append(',');
            first = false;
            Write(sb, item);
        }
        sb.Append(']');
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (ch < 0x20)
                    {
                        sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                    break;
            }
        }
        sb.Append('"');
    }

    public static ulong Fingerprint(object? value) =>
        StableHash.OfString(Serialize(value));
}
