using System.Text;

namespace FastExcel.Internal.OpenXml;

/// <summary>One floating image placement on a worksheet.</summary>
internal readonly record struct DrawingAnchor(int Row, int Column, MediaPart Media, int WidthPx, int HeightPx);

/// <summary>
/// Buffers the (small) list of image anchors for a single worksheet and emits the
/// xl/drawings/drawingN.xml part plus its relationship file. Anchors are held in memory only as
/// coordinates + media references; image bytes live in <see cref="MediaCollector"/>.
/// </summary>
internal sealed class DrawingWriter
{
    private const int EmuPerPx = 9525; // 96 DPI
    private readonly List<DrawingAnchor> _anchors = new();

    public string PartName { get; }
    public bool HasAnchors => _anchors.Count > 0;

    public DrawingWriter(int sheetIndex) => PartName = $"xl/drawings/drawing{sheetIndex}.xml";

    public void Add(DrawingAnchor anchor) => _anchors.Add(anchor);

    /// <summary>Builds drawing xml and its .rels. Returns drawing XML string, rels XML string, and the media parts referenced.</summary>
    public (string DrawingXml, string RelsXml) Build()
    {
        // Assign one rId per distinct media part used on this sheet.
        var relIds = new Dictionary<MediaPart, int>(ReferenceComparer.Instance);
        var orderedMedia = new List<MediaPart>();
        foreach (var a in _anchors)
        {
            if (!relIds.ContainsKey(a.Media))
            {
                relIds[a.Media] = orderedMedia.Count + 1;
                orderedMedia.Add(a.Media);
            }
        }

        var d = new StringBuilder(256 + _anchors.Count * 400);
        d.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        d.Append("<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" ")
         .Append("xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" ")
         .Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");

        int picId = 2;
        foreach (var a in _anchors)
        {
            int cx = Math.Max(1, a.WidthPx) * EmuPerPx;
            int cy = Math.Max(1, a.HeightPx) * EmuPerPx;
            int rid = relIds[a.Media];
            d.Append("<xdr:oneCellAnchor>")
             .Append("<xdr:from><xdr:col>").Append(a.Column)
             .Append("</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>").Append(a.Row)
             .Append("</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from>")
             .Append("<xdr:ext cx=\"").Append(cx).Append("\" cy=\"").Append(cy).Append("\"/>")
             .Append("<xdr:pic><xdr:nvPicPr><xdr:cNvPr id=\"").Append(picId)
             .Append("\" name=\"Picture ").Append(picId).Append("\"/><xdr:cNvPicPr/></xdr:nvPicPr>")
             .Append("<xdr:blipFill><a:blip xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" r:embed=\"rId")
             .Append(rid).Append("\"/><a:stretch><a:fillRect/></a:stretch></xdr:blipFill>")
             .Append("<xdr:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"").Append(cx)
             .Append("\" cy=\"").Append(cy).Append("\"/></a:xfrm>")
             .Append("<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></xdr:spPr></xdr:pic>")
             .Append("<xdr:clientData/></xdr:oneCellAnchor>");
            picId++;
        }
        d.Append("</xdr:wsDr>");

        var r = new StringBuilder(128 + orderedMedia.Count * 160);
        r.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        r.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        for (int i = 0; i < orderedMedia.Count; i++)
        {
            // media part is "xl/media/imageN.ext"; target relative to drawing is "../media/imageN.ext"
            var target = orderedMedia[i].PartName.StartsWith("xl/") ? orderedMedia[i].PartName[3..] : orderedMedia[i].PartName;
            r.Append("<Relationship Id=\"rId").Append(i + 1)
             .Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"../")
             .Append(target).Append("\"/>");
        }
        r.Append("</Relationships>");

        return (d.ToString(), r.ToString());
    }

    private sealed class ReferenceComparer : IEqualityComparer<MediaPart>
    {
        public static readonly ReferenceComparer Instance = new();
        public bool Equals(MediaPart? x, MediaPart? y) => ReferenceEquals(x, y);
        public int GetHashCode(MediaPart obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
