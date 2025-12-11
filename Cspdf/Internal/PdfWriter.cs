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
    private readonly List<(string placeholder, byte[] data)> _binaryData = new();
    private int _objectNumber = 1;
    private readonly Dictionary<string, int> _objectMap = new();
    private readonly Dictionary<int, long> _objectOffsets = new();
    private readonly List<int> _imageObjectNumbers = new();
    private int _pagesObjectNumber = 0;

    public PdfWriter(PdfDocument document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
    }

    public void Write(Stream stream)
    {
        _content.Clear();
        _objectMap.Clear();
        _binaryData.Clear();
        _objectOffsets.Clear();
        _imageObjectNumbers.Clear();
        _objectNumber = 1;

        // PDF Header
        WriteLine("%PDF-1.7");
        WriteLine("%\xE2\xE3\xCF\xD3");

        // Reserve object numbers for catalog and pages tree (we'll write them later)
        var catalogObjNum = GetNextObjectNumber();
        _pagesObjectNumber = GetNextObjectNumber();

        // Write pages
        var pageRefs = new List<int>();
        foreach (var page in _document.Pages)
        {
            var pageObjNum = WritePage(page);
            pageRefs.Add(pageObjNum);
        }

        // Write catalog and pages tree with reserved numbers
        WriteCatalogWithNumber(catalogObjNum, pageRefs);

        // Write document info
        var infoObjNum = WriteDocumentInfo();

        // Write to stream - replace placeholders with binary data
        var textContent = _content.ToString();
        
        if (_binaryData.Count == 0)
        {
            // No binary data, just write text
            var textBytes = Encoding.UTF8.GetBytes(textContent);
            stream.Write(textBytes, 0, textBytes.Length);
            return;
        }
        
        // Build final content by replacing placeholders with binary data
        using var ms = new MemoryStream();
        var writer = new StreamWriter(ms, Encoding.UTF8, leaveOpen: true);
        
        int lastIndex = 0;
        long currentPosition = 0;
        
        // Track object positions and replace placeholders
        var sortedData = _binaryData.OrderBy(b => textContent.IndexOf(b.placeholder)).ToList();
        
        foreach (var (placeholder, data) in sortedData)
        {
            var placeholderIndex = textContent.IndexOf(placeholder, lastIndex);
            if (placeholderIndex >= 0)
            {
                // Write text before placeholder
                if (placeholderIndex > lastIndex)
                {
                    var textPart = textContent.Substring(lastIndex, placeholderIndex - lastIndex);
                    
                    // Track object offsets in this text part
                    TrackObjectOffsets(textPart, currentPosition);
                    
                    writer.Write(textPart);
                    writer.Flush();
                    currentPosition = ms.Position;
                }
                
                // Write binary data directly to stream
                ms.Write(data, 0, data.Length);
                currentPosition = ms.Position;
                lastIndex = placeholderIndex + placeholder.Length;
            }
        }
        
        // Write remaining text and track offsets
        if (lastIndex < textContent.Length)
        {
            var remainingText = textContent.Substring(lastIndex);
            TrackObjectOffsets(remainingText, currentPosition);
            writer.Write(remainingText);
            writer.Flush();
        }
        
        // Write xref table
        var xrefOffset = WriteXRefTable(ms);
        
        // Write trailer
        WriteTrailer(ms, catalogObjNum, infoObjNum, xrefOffset);
        
        // Copy to output stream
        ms.Position = 0;
        ms.CopyTo(stream);
    }
    
    private void TrackObjectOffsets(string text, long startPosition)
    {
        int searchIndex = 0;
        while (searchIndex < text.Length)
        {
            var objIndex = text.IndexOf(" 0 obj", searchIndex);
            if (objIndex < 0) break;
            
            // Find the object number before " 0 obj"
            int numberStart = objIndex - 1;
            while (numberStart >= 0 && char.IsDigit(text[numberStart]))
            {
                numberStart--;
            }
            numberStart++;
            
            if (numberStart < objIndex)
            {
                var objNumStr = text.Substring(numberStart, objIndex - numberStart);
                if (int.TryParse(objNumStr, out var objNum))
                {
                    // Calculate position: count bytes before this point
                    var bytesBeforeObj = Encoding.UTF8.GetByteCount(text.Substring(0, numberStart));
                    _objectOffsets[objNum] = startPosition + bytesBeforeObj;
                }
            }
            
            searchIndex = objIndex + 6;
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

        // Write page content stream
        var content = WritePageContent(page);
        var contentBytes = Encoding.UTF8.GetBytes(content);
        
        // Write content stream object
        WriteLine($"{contentObjNum} 0 obj");
        WriteLine($@"<<
/Length {contentBytes.Length}
>>");
        WriteLine("stream");
        _content.Append(content);
        WriteLine("endstream");
        WriteLine("endobj");
        WriteLine("");

        // Build XObject resources for images
        var xobjects = new StringBuilder();
        foreach (var imgNum in _imageObjectNumbers)
        {
            xobjects.Append($"    /Im{imgNum} {imgNum} 0 R\n");
        }

        // Write page object
        var pageContent = $@"<<
/Type /Page
/Parent {GetPageTreeRef()}
/MediaBox [0 0 {page.Width} {page.Height}]
/Contents {contentObjNum} 0 R
/Resources <<
  /XObject <<
{xobjects}  >>
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
        _imageObjectNumbers.Add(objNum);
        
        // Write image XObject with stream
        WriteLine($"{objNum} 0 obj");
        WriteLine($@"<<
/Type /XObject
/Subtype /Image
/Width {width}
/Height {height}
/ColorSpace /DeviceRGB
/BitsPerComponent 8
/Filter /DCTDecode
/Length {imageData.Length}
>>");
        WriteLine("stream");
        var placeholder = $"<BINARY_DATA_{objNum}>";
        _binaryData.Add((placeholder, imageData));
        _content.Append(placeholder);
        WriteLine("");
        WriteLine("endstream");
        WriteLine("endobj");
        WriteLine("");
        return objNum;
    }

    private void WriteCatalogWithNumber(int catalogObjNum, List<int> pageRefs)
    {
        // Write Pages object with reserved number
        var kids = string.Join(" ", pageRefs.Select(p => $"{p} 0 R"));
        var pagesContent = $@"<<
/Type /Pages
/Kids [{kids}]
/Count {pageRefs.Count}
>>";
        WriteLine($"{_pagesObjectNumber} 0 obj");
        WriteLine(pagesContent);
        WriteLine("endobj");
        WriteLine("");

        // Write Catalog object with reserved number
        var catalogContent = $@"<<
/Type /Catalog
/Pages {_pagesObjectNumber} 0 R
>>";
        WriteLine($"{catalogObjNum} 0 obj");
        WriteLine(catalogContent);
        WriteLine("endobj");
        WriteLine("");
    }

    private int GetPageTreeRef()
    {
        return _pagesObjectNumber;
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

    private long WriteXRefTable(MemoryStream ms)
    {
        var xrefOffset = ms.Position;
        var writer = new StreamWriter(ms, Encoding.UTF8, leaveOpen: true);
        
        writer.WriteLine("xref");
        writer.WriteLine($"0 {_objectNumber}");
        writer.WriteLine("0000000000 65535 f ");
        
        for (int i = 1; i < _objectNumber; i++)
        {
            if (_objectOffsets.TryGetValue(i, out var offset))
            {
                writer.WriteLine($"{offset:D10} 00000 n ");
            }
            else
            {
                writer.WriteLine("0000000000 00000 n ");
            }
        }
        
        writer.Flush();
        return xrefOffset;
    }

    private void WriteTrailer(MemoryStream ms, int catalogObjNum, int infoObjNum, long xrefOffset)
    {
        var writer = new StreamWriter(ms, Encoding.UTF8, leaveOpen: true);
        
        writer.WriteLine("trailer");
        writer.WriteLine($@"<<
/Size {_objectNumber}
/Root {catalogObjNum} 0 R
/Info {infoObjNum} 0 R
>>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefOffset.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();
    }

    private void WriteObject(int objNum, string content)
    {
        WriteLine($"{objNum} 0 obj");
        WriteLine(content);
        WriteLine("endobj");
        WriteLine("");
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


