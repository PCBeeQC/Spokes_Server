using System.IO;
using System.Text;

namespace Spokes_Server.Core.Helpers;

/// <summary>
/// Lightweight, zero-dependency RFC 4180 compliant CSV parser.
/// Correctly handles quoted fields containing commas, double-quote escaping (""),
/// multiline values, and mixed CRLF/LF line endings.
/// </summary>
public static class CsvReaderHelper
{
    /// <summary>
    /// Parses CSV content from a <see cref="TextReader"/> into a list of rows, where each row is a list of field values.
    /// </summary>
    public static List<List<string>> Parse(TextReader reader)
    {
        var rows = new List<List<string>>();
        var currentRow = new List<string>();
        var currentField = new StringBuilder();
        bool inQuotes = false;
        bool hasFieldContent = false;

        int ch;
        while ((ch = reader.Read()) != -1)
        {
            char c = (char)ch;

            if (inQuotes)
            {
                if (c == '"')
                {
                    int next = reader.Peek();
                    if (next == '"')
                    {
                        // Escaped double quote: ""
                        reader.Read();
                        currentField.Append('"');
                    }
                    else
                    {
                        // Closing quote
                        inQuotes = false;
                    }
                }
                else
                {
                    currentField.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                    hasFieldContent = true;
                }
                else if (c == ',')
                {
                    currentRow.Add(currentField.ToString());
                    currentField.Clear();
                    hasFieldContent = false;
                }
                else if (c == '\r')
                {
                    if (reader.Peek() == '\n')
                    {
                        reader.Read();
                    }

                    currentRow.Add(currentField.ToString());
                    currentField.Clear();
                    rows.Add(currentRow);
                    currentRow = new List<string>();
                    hasFieldContent = false;
                }
                else if (c == '\n')
                {
                    currentRow.Add(currentField.ToString());
                    currentField.Clear();
                    rows.Add(currentRow);
                    currentRow = new List<string>();
                    hasFieldContent = false;
                }
                else
                {
                    currentField.Append(c);
                    hasFieldContent = true;
                }
            }
        }

        // Flush remaining trailing field and row if any
        if (hasFieldContent || currentField.Length > 0 || currentRow.Count > 0)
        {
            currentRow.Add(currentField.ToString());
            rows.Add(currentRow);
        }

        return rows;
    }

    /// <summary>
    /// Parses a CSV string into a list of rows.
    /// </summary>
    public static List<List<string>> Parse(string csvText)
    {
        if (string.IsNullOrEmpty(csvText))
        {
            return [];
        }

        using var reader = new StringReader(csvText);
        return Parse(reader);
    }
}
