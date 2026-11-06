using System.Text;

namespace SmoothBudget.Core.Import;

/// <summary>Minimal RFC 4180 reader: quoted fields, doubled quotes, embedded commas and newlines, CRLF or LF.</summary>
public static class CsvReader
{
    /// <summary>Returns one string array per non-blank line. Rows may have different lengths.</summary>
    public static List<string[]> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 0 && text[0] == '﻿')
            text = text[1..];

        var rows = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        void EndField()
        {
            fields.Add(field.ToString());
            field.Clear();
        }

        void EndRow()
        {
            EndField();
            if (fields.Count > 1 || fields[0].Trim().Length > 0)
                rows.Add([.. fields]);
            fields.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }

            switch (c)
            {
                case '"': inQuotes = true; break;
                case ',': EndField(); break;
                case '\r': break;
                case '\n': EndRow(); break;
                default: field.Append(c); break;
            }
        }

        if (field.Length > 0 || fields.Count > 0)
            EndRow();
        return rows;
    }
}
