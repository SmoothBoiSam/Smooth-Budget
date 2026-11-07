using SmoothBudget.Core.Pay;
using UglyToad.PdfPig;

namespace SmoothBudget.Api.Payday;

/// <summary>Reads a PDF's first page as positioned words for <see cref="PayStubParser"/>. The file is read in memory and never written to disk.</summary>
public static class PdfWords
{
    public static IReadOnlyList<PageWord> FirstPage(byte[] pdf)
    {
        try
        {
            using var doc = PdfDocument.Open(pdf);
            var page = doc.GetPages().FirstOrDefault() ?? throw new PayStubParseException("The PDF has no pages.");
            return page.GetWords().Select(w => new PageWord(w.Text, w.BoundingBox.Left, w.BoundingBox.Right, w.BoundingBox.Bottom)).ToList();
        }
        catch (PayStubParseException) { throw; }
        catch (Exception)
        {
            throw new PayStubParseException("This file couldn't be read as a PDF.");
        }
    }
}
