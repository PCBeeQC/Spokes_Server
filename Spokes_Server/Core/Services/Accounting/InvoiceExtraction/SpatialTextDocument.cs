namespace Spokes_Server.Core.Services.Accounting.InvoiceExtraction;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

/// <summary>
/// Represents a word with geometric position in a PDF document.
/// </summary>
public record SpatialWord(string Text, double Left, double Bottom, double Right, double Top);

/// <summary>
/// Represents a horizontal cell cluster on a spatial line with bounding coordinates.
/// </summary>
public record SpatialCell(string Text, double Left, double Right, double Y);

/// <summary>
/// Represents a line of words grouped horizontally by Y coordinate.
/// </summary>
public class SpatialLine
{
    public double Y { get; set; }
    public List<SpatialWord> Words { get; set; } = [];

    public string Text => string.Join(" ", Words.Select(w => w.Text));
    public double Left => Words.Count > 0 ? Words.Min(w => w.Left) : 0;
    public double Right => Words.Count > 0 ? Words.Max(w => w.Right) : 0;

    /// <summary>
    /// Clusters words into discrete spatial cells with coordinates based on horizontal gaps (> 12 points).
    /// Also supports single-word text lines separated by pipes, tabs, or 2+ spaces.
    /// </summary>
    public List<SpatialCell> GetSpatialCells(double minGap = 12.0)
    {
        var cells = new List<SpatialCell>();
        if (Words.Count == 0) return cells;

        if (Words.Count == 1 && Regex.IsMatch(Words[0].Text, @"(?:\s{2,}|\t|\|)"))
        {
            var w = Words[0];
            var text = w.Text;
            var totalLen = Math.Max(1, text.Length);
            var width = Math.Max(100.0, w.Right - w.Left);
            var matches = Regex.Matches(text, @"[^\t|]+?(?=(?:\s{2,}|\t|\||$))");
            foreach (Match m in matches)
            {
                var trimmed = m.Value.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;
                var cellLeft = w.Left + (m.Index / (double)totalLen) * width;
                var cellRight = w.Left + ((m.Index + trimmed.Length) / (double)totalLen) * width;
                cells.Add(new SpatialCell(trimmed, cellLeft, cellRight, Y));
            }

            if (cells.Count > 0) return cells;
        }

        var currentWords = new List<SpatialWord> { Words[0] };
        for (int i = 1; i < Words.Count; i++)
        {
            var prevWord = Words[i - 1];
            var currWord = Words[i];

            if ((currWord.Left - prevWord.Right) > minGap)
            {
                cells.Add(new SpatialCell(
                    string.Join(" ", currentWords.Select(w => w.Text)),
                    currentWords[0].Left,
                    currentWords[^1].Right,
                    Y));
                currentWords = [currWord];
            }
            else
            {
                currentWords.Add(currWord);
            }
        }

        if (currentWords.Count > 0)
        {
            cells.Add(new SpatialCell(
                string.Join(" ", currentWords.Select(w => w.Text)),
                currentWords[0].Left,
                currentWords[^1].Right,
                Y));
        }

        return cells;
    }

    /// <summary>
    /// Clusters words into discrete column cells based on horizontal gaps (> 12 points).
    /// </summary>
    public List<string> GetColumnCells(double minGap = 12.0)
    {
        return GetSpatialCells(minGap).Select(c => c.Text).ToList();
    }
}

/// <summary>
/// Represents a parsed page segmented into functional layout zones.
/// </summary>
public class SpatialPage
{
    public int PageNumber { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public List<SpatialLine> Lines { get; set; } = [];

    /// <summary>
    /// Top letterhead and header band (top 35% of page).
    /// </summary>
    public IEnumerable<SpatialLine> HeaderLines => Lines.Where(l => l.Y >= Height * 0.65);

    /// <summary>
    /// Middle tabular and body area (between 25% and 65% of page height).
    /// </summary>
    public IEnumerable<SpatialLine> BodyLines => Lines.Where(l => l.Y < Height * 0.65 && l.Y >= Height * 0.25);

    /// <summary>
    /// Bottom summary and tax totals area (bottom 25% of page).
    /// </summary>
    public IEnumerable<SpatialLine> FooterLines => Lines.Where(l => l.Y < Height * 0.25);
}

/// <summary>
/// A layout-aware PDF document preserving spatial coordinates and line structures.
/// </summary>
public class SpatialTextDocument
{
    public List<SpatialPage> Pages { get; } = [];
    public string FullLayoutText { get; }
    public string RawFullText { get; }
    public int TotalPageCount { get; init; }
    public bool HasSelectableText { get; init; }

    private SpatialTextDocument(List<SpatialPage> pages, string fullLayoutText, string rawFullText)
    {
        Pages = pages;
        FullLayoutText = fullLayoutText;
        RawFullText = rawFullText;
        TotalPageCount = pages.Count;
        HasSelectableText = pages.Any(p => p.Lines.Any(l => !string.IsNullOrWhiteSpace(l.Text))) || !string.IsNullOrWhiteSpace(fullLayoutText);
    }

    public IEnumerable<SpatialLine> AllLines => Pages.SelectMany(p => p.Lines);

    /// <summary>
    /// Clusters words into horizontal lines using greedy baseline proximity (within tolerance points of the cluster's running mean Y).
    /// Prevents words on the same line from splitting across fixed rounding bucket boundaries.
    /// </summary>
    public static List<SpatialLine> ClusterWordsIntoLines(IEnumerable<SpatialWord> words, double tolerance = 3.5)
    {
        var sortedByY = words.OrderByDescending(w => w.Bottom).ThenBy(w => w.Left).ToList();
        var lines = new List<SpatialLine>();
        if (sortedByY.Count == 0) return lines;

        var currentCluster = new List<SpatialWord> { sortedByY[0] };
        double currentMeanY = sortedByY[0].Bottom;

        for (int i = 1; i < sortedByY.Count; i++)
        {
            var word = sortedByY[i];
            if (Math.Abs(word.Bottom - currentMeanY) <= tolerance)
            {
                currentCluster.Add(word);
                currentMeanY = currentCluster.Average(w => w.Bottom);
            }
            else
            {
                lines.Add(new SpatialLine
                {
                    Y = Math.Round(currentMeanY, 2),
                    Words = currentCluster.OrderBy(w => w.Left).ToList()
                });
                currentCluster = [word];
                currentMeanY = word.Bottom;
            }
        }

        if (currentCluster.Count > 0)
        {
            lines.Add(new SpatialLine
            {
                Y = Math.Round(currentMeanY, 2),
                Words = currentCluster.OrderBy(w => w.Left).ToList()
            });
        }

        return lines;
    }

    /// <summary>
    /// Resolves a field value located either immediately to the right of a label on the same line
    /// or directly below the label cell on the next 1-2 lines (for 2D boxed/grid invoice headers and footers).
    /// </summary>
    public string? FindValueNearLabel(Regex labelPattern, Regex? valueValidator = null, bool searchBottomUp = false)
    {
        var allLines = AllLines.ToList();
        if (allLines.Count == 0) return null;

        var indices = Enumerable.Range(0, allLines.Count);
        if (searchBottomUp)
        {
            indices = indices.Reverse();
        }

        foreach (int i in indices)
        {
            var line = allLines[i];
            if (string.IsNullOrWhiteSpace(line.Text)) continue;

            var cells = line.GetSpatialCells(10.0);
            for (int c = 0; c < cells.Count; c++)
            {
                var cell = cells[c];
                var match = labelPattern.Match(cell.Text);
                if (!match.Success) continue;

                // 1. Check inline remainder within the same cell after the label
                var remainder = cell.Text[(match.Index + match.Length)..].Trim(' ', ':', '#', '.', '-');
                if (!string.IsNullOrWhiteSpace(remainder) && (valueValidator == null || valueValidator.IsMatch(remainder)))
                {
                    return valueValidator != null ? valueValidator.Match(remainder).Value.Trim() : remainder;
                }

                // 2. Check the adjacent cell to the right on the same line
                if (c + 1 < cells.Count)
                {
                    var rightCell = cells[c + 1];
                    if ((rightCell.Left - cell.Right) <= 180.0 && !labelPattern.IsMatch(rightCell.Text))
                    {
                        var candidate = rightCell.Text.Trim(' ', ':', '#');
                        if (!string.IsNullOrWhiteSpace(candidate) && (valueValidator == null || valueValidator.IsMatch(candidate)))
                        {
                            return valueValidator != null ? valueValidator.Match(candidate).Value.Trim() : candidate;
                        }
                    }
                }

                // 3. Check 1-2 lines immediately below for a horizontally aligned cell (2D boxed layout)
                for (int offset = 1; offset <= 2 && (i + offset) < allLines.Count; offset++)
                {
                    var belowLine = allLines[i + offset];
                    var verticalGap = line.Y - belowLine.Y;
                    if (verticalGap < 0 || verticalGap > 45.0) break;

                    var belowCells = belowLine.GetSpatialCells(10.0);
                    SpatialCell? bestAligned = null;
                    double bestOverlap = double.MinValue;

                    foreach (var bCell in belowCells)
                    {
                        // Check horizontal overlap or proximity with the label cell
                        double overlapLeft = Math.Max(cell.Left - 20.0, bCell.Left);
                        double overlapRight = Math.Min(cell.Right + 40.0, bCell.Right);
                        double overlap = overlapRight - overlapLeft;

                        if (overlap >= 0 && overlap > bestOverlap)
                        {
                            bestOverlap = overlap;
                            bestAligned = bCell;
                        }
                    }

                    // Single-cell line fallback when both lines have only 1 cell
                    if (bestAligned == null && cells.Count == 1 && belowCells.Count == 1)
                    {
                        bestAligned = belowCells[0];
                    }

                    if (bestAligned != null)
                    {
                        var candidate = bestAligned.Text.Trim(' ', ':', '#');
                        if (!string.IsNullOrWhiteSpace(candidate) && !labelPattern.IsMatch(candidate))
                        {
                            if (valueValidator == null || valueValidator.IsMatch(candidate))
                            {
                                return valueValidator != null ? valueValidator.Match(candidate).Value.Trim() : candidate;
                            }
                        }
                    }
                }
            }
        }

        return null;
    }

    public static SpatialTextDocument FromPdf(PdfDocument document, int maxPages = 6)
    {
        var pages = new List<SpatialPage>();
        var rawSb = new StringBuilder();
        var totalPages = document.NumberOfPages;

        // Sample head + tail pages when document exceeds maxPages so multi-page invoice totals on the final pages are never missed
        var pageNumbersToSample = new List<int>();
        if (totalPages <= maxPages || maxPages < 3)
        {
            for (int i = 1; i <= Math.Min(maxPages, totalPages); i++)
            {
                pageNumbersToSample.Add(i);
            }
        }
        else
        {
            int headCount = maxPages - 2;
            for (int i = 1; i <= headCount; i++)
            {
                pageNumbersToSample.Add(i);
            }
            pageNumbersToSample.Add(totalPages - 1);
            pageNumbersToSample.Add(totalPages);
        }

        foreach (int i in pageNumbersToSample.Distinct())
        {
            var page = document.GetPage(i);
            rawSb.AppendLine(page.Text);

            var pageObj = new SpatialPage
            {
                PageNumber = i,
                Width = page.Width,
                Height = page.Height
            };

            var words = page.GetWords().ToList();
            if (words.Count > 0)
            {
                var spatialWords = ExtractSpatialWordsWithGapSplitting(words);
                pageObj.Lines.AddRange(ClusterWordsIntoLines(spatialWords, 3.5));
            }
            else if (!string.IsNullOrWhiteSpace(page.Text))
            {
                // Fallback for simple text streams
                var textLines = page.Text.Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToList();

                double estimatedY = page.Height - 50.0;
                foreach (var tl in textLines)
                {
                    pageObj.Lines.Add(new SpatialLine
                    {
                        Y = estimatedY,
                        Words = [new SpatialWord(tl, 50, estimatedY, page.Width - 50, estimatedY + 12)]
                    });
                    estimatedY -= 15.0;
                }
            }

            pages.Add(pageObj);
        }

        var fullLayoutText = string.Join("\n", pages.SelectMany(p => p.Lines.Select(l => l.Text)));
        bool hasSelectableText = pages.Any(p => p.Lines.Any(l => !string.IsNullOrWhiteSpace(l.Text))) || !string.IsNullOrWhiteSpace(fullLayoutText);

        return new SpatialTextDocument(pages, fullLayoutText, rawSb.ToString())
        {
            TotalPageCount = totalPages,
            HasSelectableText = hasSelectableText
        };
    }

    /// <summary>
    /// Converts PdfPig words into SpatialWords while splitting tokens that DefaultWordExtractor
    /// accidentally merged across wide column gaps (>= 8pt and > 1.2x font height).
    /// </summary>
    private static List<SpatialWord> ExtractSpatialWordsWithGapSplitting(List<Word> words)
    {
        var result = new List<SpatialWord>(words.Count);

        foreach (var w in words)
        {
            var letters = w.Letters;
            if (letters == null || letters.Count <= 1 || w.Text.Length <= 3)
            {
                result.Add(new SpatialWord(w.Text, w.BoundingBox.Left, w.BoundingBox.Bottom, w.BoundingBox.Right, w.BoundingBox.Top));
                continue;
            }

            double avgHeight = letters.Average(l => Math.Max(1.0, l.BoundingBox.Height));
            double wideGapThreshold = Math.Max(8.0, avgHeight * 1.2);

            var currentLetters = new List<Letter>();
            foreach (var letter in letters)
            {
                if (string.IsNullOrWhiteSpace(letter.Value))
                {
                    if (currentLetters.Count > 0)
                    {
                        result.Add(BuildSpatialWord(currentLetters));
                        currentLetters.Clear();
                    }
                    continue;
                }

                if (currentLetters.Count > 0)
                {
                    var prev = currentLetters[^1];
                    double glyphGap = letter.BoundingBox.Left - prev.BoundingBox.Right;
                    if (glyphGap > wideGapThreshold)
                    {
                        result.Add(BuildSpatialWord(currentLetters));
                        currentLetters.Clear();
                    }
                }

                currentLetters.Add(letter);
            }

            if (currentLetters.Count > 0)
            {
                result.Add(BuildSpatialWord(currentLetters));
            }
        }

        return result;
    }

    private static SpatialWord BuildSpatialWord(List<Letter> letters)
    {
        var text = string.Concat(letters.Select(l => l.Value));
        double left = letters.Min(l => l.BoundingBox.Left);
        double bottom = letters.Min(l => l.BoundingBox.Bottom);
        double right = letters.Max(l => l.BoundingBox.Right);
        double top = letters.Max(l => l.BoundingBox.Top);
        return new SpatialWord(text, left, bottom, right, top);
    }
}
