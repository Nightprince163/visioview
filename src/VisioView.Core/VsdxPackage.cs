using System.IO.Compression;
using System.Xml.Linq;

namespace VisioView.Core;

/// <summary>读取 .vsdx（OPC/ZIP）包中的 XML 部件。</summary>
public sealed class VsdxPackage : IDisposable
{
    private readonly ZipArchive _zip;

    public VsdxPackage(string path)
    {
        _zip = ZipFile.OpenRead(path);
    }

    public XDocument GetXml(string partPath)
    {
        var entry = _zip.GetEntry(partPath)
                   ?? throw new FileNotFoundException($"部件不存在: {partPath}");
        using var s = entry.Open();
        return XDocument.Load(s);
    }

    public bool HasPart(string partPath) => _zip.GetEntry(partPath) != null;

    /// <summary>读取二进制部件（图片等）。</summary>
    public byte[]? ReadBytes(string partPath)
    {
        var entry = _zip.GetEntry(partPath);
        if (entry == null) return null;
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public IEnumerable<string> PartNames
        => _zip.Entries.Select(e => e.FullName).Where(n => n.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));

    public void Dispose() => _zip.Dispose();
}
