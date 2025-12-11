using System.Drawing.Imaging;
using System.Text;

namespace Cspdf.Internal;

/// <summary>
/// Internal class for writing PDF documents
/// </summary>
internal class PdfWriter
{
    private readonly PdfDocument _document;
    private readonly StringBuilder _content = new();
    private readonly List<(int position, byte[] data)> _binaryData = new();
    private int _objectNumber = 1;
    private readonly Dictionary<string, int> _objectMap = new();

    public PdfWriter(PdfDocument document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
    }

    public void Write(Stream stream)
    {
        _content.Clear();
        _objectMap.Clear();
        _binaryData.Clear();
        _objectNumber = 1;

        // PDF Header
        WriteLine("%PDF-1.7");
        WriteLine("%\xE2\xE3\xCF\xD3");

        // Write pages
        var pageRefs = new List<int>();
        foreach (var page in _document.Pages)
        {
            var pageObjNum = WritePage(page);
            pageRefs.Add(pageObjNum);
        }

        // Write catalog
        var catalogObjNum = WriteCatalog(pageRefs);

        // Write document info
        var infoObjNum = WriteDocumentInfo();

        // Write xref table
        var xrefOffset = WriteXRefTable();

        // Write trailer
        WriteTrailer(catalogObjNum, infoObjNum, xrefOffset);

        // Write to stream - replace placeholders with binary data
        var textContent = _content.ToString();
        var textBytes = Encoding.UTF8.GetBytes(textContent);
        
        // Sort binary data by position
        var sortedBinaryData = _binaryData.OrderBy(b => b.position).ToList();
        
        if (sortedBinaryData.Count == 0)
        {
            // No binary data, just write text
            stream.Write(textBytes, 0, textBytes.Length);
            return;
        }
        
        // Write text content and binary data at correct positions
        int currentPos = 0;
        
        foreach (var (position, data) in sortedBinaryData)
        {
            // Find placeholder in text
            var placeholder = $"<BINARY_DATA_{_binaryData.IndexOf((position, data))}>";
            var placeholderBytes = Encoding.UTF8.GetBytes(placeholder);
            var placeholderIndex = FindBytes(textBytes, placeholderBytes, currentPos);
            
            if (placeholderIndex >= 0)
            {
                // Write text up to placeholder
                var textToWrite = new byte[placeholderIndex - currentPos];
                Array.Copy(textBytes, currentPos, textToWrite, 0, textToWrite.Length);
                stream.Write(textToWrite, 0, textToWrite.Length);
                
                // Write binary data
                stream.Write(data, 0, data.Length);
                currentPos = placeholderIndex + placeholderBytes.Length;
            }
        }
        
        // Write remaining text
        if (currentPos < textBytes.Length)
        {
            var remainingText = new byte[textBytes.Length - currentPos];
            Array.Copy(textBytes, currentPos, remainingText, 0, remainingText.Length);
            stream.Write(remainingText, 0, remainingText.Length);
        }
    }
    
    private int FindBytes(byte[] haystack, byte[] needle, int startIndex)
    {
        for (int i = startIndex; i <= haystack.Length - needle.Length; i++)
        {
            bool found = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    found = false;
                    break;
                }
            }
            if (found) return i;
        }
        return -1;
    }

    private int WritePage(IPage page)
    {
        var objNum = GetNextObjectNumber();
        var contentObjNum = GetNextObjectNumber();

        // Write page content
        var content = WritePageContent(page);
        WriteObject(contentObjNum, content);

        // Write page object
        var pageContent = $@"<<
/Type /Page
/Parent {GetPageTreeRef()}
/MediaBox [0 0 {page.Width} {page.Height}]
/Contents {contentObjNum} 0 R
/Resources <<
  /XObject <<
  >>
  /Font <<
  >>
>>
/Rotate {page.Rotation}
>>";
        WriteObject(objNum, pageContent);

        return objNum;
    }

    private string WritePageContent(IPage page)
    {
        var sb = new StringBuilder();
        sb.AppendLine("q");

        if (page is PdfPage pdfPage && pdfPage.Graphics is PdfGraphics graphics)
        {
            var bitmap = graphics.GetBitmap();
            if (bitmap != null)
            {
                // Convert bitmap to JPEG for better PDF compatibility
                using var ms = new MemoryStream();
                bitmap.Save(ms, ImageFormat.Jpeg);
                var imageData = ms.ToArray();
                var imageObjNum = WriteImage(imageData, bitmap.Width, bitmap.Height);
                sb.AppendLine($"{bitmap.Width} 0 0 {bitmap.Height} 0 0 cm");
                sb.AppendLine($"/Im{imageObjNum} Do");
            }
        }

        sb.AppendLine("Q");
        return sb.ToString();
    }

    private int WriteImage(byte[] imageData, int width, int height)
    {
        var objNum = GetNextObjectNumber();
        var imageContent = $@"<<
/Type /XObject
/Subtype /Image
/Width {width}
/Height {height}
/ColorSpace /DeviceRGB
/BitsPerComponent 8
/Filter /DCTDecode
/Length {imageData.Length}
>>";
        WriteObject(objNum, imageContent);
        var streamStartPos = _content.Length;
        WriteLine("stream");
        // Store binary data position and data
        var streamMarker = "stream\r\n";
        var streamMarkerBytes = Encoding.UTF8.GetBytes(streamMarker);
        var position = streamStartPos + streamMarkerBytes.Length;
        _binaryData.Add((position, imageData));
        // Write placeholder for binary data (will be replaced)
        WriteLine($"<BINARY_DATA_{_binaryData.Count - 1}>");
        WriteLine("endstream");
        return objNum;
    }

    private int WriteCatalog(List<int> pageRefs)
    {
        var objNum = GetNextObjectNumber();
        var pagesObjNum = WritePages(pageRefs);
        var catalogContent = $@"<<
/Type /Catalog
/Pages {pagesObjNum} 0 R
>>";
        WriteObject(objNum, catalogContent);
        return objNum;
    }

    private int WritePages(List<int> pageRefs)
    {
        var objNum = GetNextObjectNumber();
        var kids = string.Join(" ", pageRefs.Select(p => $"{p} 0 R"));
        var pagesContent = $@"<<
/Type /Pages
/Kids [{kids}]
/Count {pageRefs.Count}
>>";
        WriteObject(objNum, pagesContent);
        return objNum;
    }

    private int GetPageTreeRef()
    {
        // This would reference the Pages object
        return 2; // Simplified
    }

    private int WriteDocumentInfo()
    {
        var objNum = GetNextObjectNumber();
        var metadata = _document.Metadata;
        var infoContent = $@"<<
/Title ({EscapeString(metadata.Title ?? "")})
/Author ({EscapeString(metadata.Author ?? "")})
/Subject ({EscapeString(metadata.Subject ?? "")})
/Keywords ({EscapeString(metadata.Keywords ?? "")})
/Creator ({EscapeString(metadata.Creator ?? "Cspdf")})
/Producer ({EscapeString(metadata.Producer ?? "Cspdf Library")})
/CreationDate (D:{DateTime.Now:yyyyMMddHHmmss})
/ModDate (D:{(metadata.ModificationDate ?? DateTime.Now):yyyyMMddHHmmss})
>>";
        WriteObject(objNum, infoContent);
        return objNum;
    }

    private int WriteXRefTable()
    {
        // Simplified xref table
        return _content.Length;
    }

    private void WriteTrailer(int catalogObjNum, int infoObjNum, int xrefOffset)
    {
        WriteLine("trailer");
        WriteLine($@"<<
/Size {_objectNumber}
/Root {catalogObjNum} 0 R
/Info {infoObjNum} 0 R
>>");
        WriteLine("startxref");
        WriteLine(xrefOffset.ToString());
        WriteLine("%%EOF");
    }

    private void WriteObject(int objNum, string content)
    {
        WriteLine($"{objNum} 0 obj");
        WriteLine(content);
        WriteLine("endobj");
    }

    private int GetNextObjectNumber()
    {
        return _objectNumber++;
    }

    private void WriteLine(string line)
    {
        _content.AppendLine(line);
    }

    private string EscapeString(string str)
    {
        return str.Replace("\\", "\\\\")
                  .Replace("(", "\\(")
                  .Replace(")", "\\)")
                  .Replace("\r", "\\r")
                  .Replace("\n", "\\n");
    }
}

