using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;

namespace VerbaFlow.Infrastructure.Export;

/// <summary>
/// Signs a Word file the way Word itself does (ECMA-376 part 2 digital signatures, XAdES): a signature part in
/// <c>_xmlsignatures</c> lists a SHA-256 fingerprint of every part of the document, and is signed with the same certificate
/// as the PDFs (test certificate, .pfx or Key Vault), with the signing time confirmed by the same timestamp service
/// (XAdES-T). Word shows it under File › Info › View Signatures; changing and saving the document removes the signature.
/// The document properties (title, author, dates) are left out of the signature, as Word does.
/// </summary>
internal sealed class WordSigner(PdfSigner owner)
{
    private const string Dsig = "http://www.w3.org/2000/09/xmldsig#";
    private const string Xades = "http://uri.etsi.org/01903/v1.3.2#";
    private const string Mdssi = "http://schemas.openxmlformats.org/package/2006/digital-signature";
    private const string Office = "http://schemas.microsoft.com/office/2006/digsig";
    private const string RelsNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string TypesNs = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string C14N = "http://www.w3.org/TR/2001/REC-xml-c14n-20010315";
    private const string Sha256 = "http://www.w3.org/2001/04/xmlenc#sha256";
    private const string RelationshipTransform = "http://schemas.openxmlformats.org/package/2006/RelationshipTransform";
    private const string OriginType = "http://schemas.openxmlformats.org/package/2006/relationships/digital-signature/origin";
    private const string SignatureType = "http://schemas.openxmlformats.org/package/2006/relationships/digital-signature/signature";
    private const string OriginPart = "_xmlsignatures/origin.sigs";
    private const string SignaturePart = "_xmlsignatures/sig1.xml";

    /// <summary>Relationships (and the parts they lead to) Word leaves out of a signature: document properties and signatures.</summary>
    internal static bool Unsigned(string type) =>
        type.EndsWith("/metadata/core-properties", StringComparison.Ordinal)
        || type.EndsWith("/extended-properties", StringComparison.Ordinal)
        || type.EndsWith("/custom-properties", StringComparison.Ordinal)
        || type.EndsWith("/metadata/thumbnail", StringComparison.Ordinal)
        || type.Contains("/digital-signature/", StringComparison.Ordinal);

    public async Task<SignedPdf> SignAsync(byte[] docx, string reason, DateTimeOffset now)
    {
        var parts = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using (var zip = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read))
            foreach (var e in zip.Entries.Where(e => !e.FullName.EndsWith('/')))
            {
                using var s = e.Open();
                using var m = new MemoryStream();
                s.CopyTo(m);
                parts[e.FullName] = m.ToArray();
            }
        if (parts.Keys.Any(k => k.StartsWith("_xmlsignatures/", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The Word file is already signed.");

        // The package gets an origin part that leads to the signature part (left out of what is signed).
        parts["_rels/.rels"] = AddRelationship(parts["_rels/.rels"], OriginType, OriginPart);
        parts[OriginPart] = [];
        parts["_xmlsignatures/_rels/origin.sigs.rels"] = Encoding.UTF8.GetBytes(
            $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"{RelsNs}\"><Relationship Id=\"rId1\" Type=\"{SignatureType}\" Target=\"sig1.xml\"/></Relationships>");
        parts["[Content_Types].xml"] = AddContentTypes(parts["[Content_Types].xml"]);
        var types = ContentTypes(parts["[Content_Types].xml"]);

        var leftOut = UnsignedTargets(parts["_rels/.rels"]);
        var manifest = new StringBuilder();
        foreach (var name in parts.Keys.Order(StringComparer.Ordinal))
        {
            if (name == "[Content_Types].xml" || name.StartsWith("_xmlsignatures/", StringComparison.Ordinal) || leftOut.Contains(name)) continue;
            var uri = "/" + name + "?ContentType=" + types(name);
            if (name.EndsWith(".rels", StringComparison.Ordinal))
            {
                var (ids, canon) = Relationships(parts[name]);
                if (ids.Count == 0) continue;
                manifest.Append($"<Reference URI=\"{Attr(uri)}\"><Transforms><Transform Algorithm=\"{RelationshipTransform}\">");
                foreach (var id in ids) manifest.Append($"<mdssi:RelationshipReference xmlns:mdssi=\"{Mdssi}\" SourceId=\"{Attr(id)}\"/>");
                manifest.Append($"</Transform><Transform Algorithm=\"{C14N}\"/></Transforms>{Digest(SHA256.HashData(canon))}</Reference>");
            }
            else manifest.Append($"<Reference URI=\"{Attr(uri)}\">{Digest(SHA256.HashData(parts[name]))}</Reference>");
        }

        var cert = owner.Certificate;
        var time = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var serial = new BigInteger(cert.GetSerialNumber(), isUnsigned: true, isBigEndian: false).ToString(CultureInfo.InvariantCulture);
        var ecdsa = owner.Key is ECDsa;
        var signatureMethod = ecdsa ? "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha256" : "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256";
        var keyInfo = string.Concat(new[] { cert }.Concat(owner.Chain.Cast<X509Certificate2>())
            .Select(c => $"<X509Certificate>{Convert.ToBase64String(c.RawData)}</X509Certificate>"));
        var xml = $"<Signature xmlns=\"{Dsig}\" Id=\"idPackageSignature\"><SignedInfo/><SignatureValue/>"
            + $"<KeyInfo><X509Data>{keyInfo}</X509Data></KeyInfo>"
            + $"<Object Id=\"idPackageObject\"><Manifest>{manifest}</Manifest><SignatureProperties>"
            + $"<SignatureProperty Id=\"idSignatureTime\" Target=\"#idPackageSignature\"><mdssi:SignatureTime xmlns:mdssi=\"{Mdssi}\">"
            + $"<mdssi:Format>YYYY-MM-DDThh:mm:ssTZD</mdssi:Format><mdssi:Value>{time}</mdssi:Value></mdssi:SignatureTime></SignatureProperty>"
            + "</SignatureProperties></Object>"
            + $"<Object Id=\"idOfficeObject\"><SignatureProperties><SignatureProperty Id=\"idOfficeV1Details\" Target=\"#idPackageSignature\">"
            + $"<SignatureInfoV1 xmlns=\"{Office}\"><SetupID/><SignatureText/><SignatureImage/><SignatureComments>{Text(reason)}</SignatureComments>"
            + "<WindowsVersion>10.0</WindowsVersion><OfficeVersion>16.0</OfficeVersion><ApplicationVersion>16.0</ApplicationVersion>"
            + "<Monitors>1</Monitors><HorizontalResolution>1920</HorizontalResolution><VerticalResolution>1080</VerticalResolution><ColorDepth>32</ColorDepth>"
            + "<SignatureProviderId>{00000000-0000-0000-0000-000000000000}</SignatureProviderId><SignatureProviderUrl/>"
            + $"<SignatureProviderDetails>9</SignatureProviderDetails><SignatureType>1</SignatureType><ManifestHashAlgorithm>{Sha256}</ManifestHashAlgorithm>"
            + "</SignatureInfoV1></SignatureProperty></SignatureProperties></Object>"
            + $"<Object><xd:QualifyingProperties xmlns:xd=\"{Xades}\" Target=\"#idPackageSignature\"><xd:SignedProperties Id=\"idSignedProperties\">"
            + $"<xd:SignedSignatureProperties><xd:SigningTime>{time}</xd:SigningTime><xd:SigningCertificate><xd:Cert><xd:CertDigest>"
            + Digest(SHA256.HashData(cert.RawData))
            + $"</xd:CertDigest><xd:IssuerSerial><X509IssuerName>{Text(cert.IssuerName.Name)}</X509IssuerName><X509SerialNumber>{serial}</X509SerialNumber></xd:IssuerSerial>"
            + "</xd:Cert></xd:SigningCertificate><xd:SignaturePolicyIdentifier><xd:SignaturePolicyImplied/></xd:SignaturePolicyIdentifier>"
            + "</xd:SignedSignatureProperties></xd:SignedProperties></xd:QualifyingProperties></Object></Signature>";

        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(xml);
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("ds", Dsig);
        ns.AddNamespace("xd", Xades);
        var root = doc.DocumentElement!;
        string Ref(string id, string? type, bool transform)
        {
            var target = (XmlElement)root.SelectSingleNode($"//*[@Id='{id}']")!;
            return $"<Reference{(type is null ? "" : $" Type=\"{type}\"")} URI=\"#{id}\">"
                + (transform ? $"<Transforms><Transform Algorithm=\"{C14N}\"/></Transforms>" : "")
                + Digest(SHA256.HashData(Canonical(target))) + "</Reference>";
        }
        var signedInfo = $"<SignedInfo xmlns=\"{Dsig}\"><CanonicalizationMethod Algorithm=\"{C14N}\"/><SignatureMethod Algorithm=\"{signatureMethod}\"/>"
            + Ref("idPackageObject", Dsig + "Object", false)
            + Ref("idOfficeObject", Dsig + "Object", false)
            + Ref("idSignedProperties", "http://uri.etsi.org/01903#SignedProperties", true)
            + "</SignedInfo>";
        var infoDoc = new XmlDocument { PreserveWhitespace = true };
        infoDoc.LoadXml(signedInfo);
        var info = doc.ImportNode(infoDoc.DocumentElement!, true);
        root.ReplaceChild(info, root.SelectSingleNode("ds:SignedInfo", ns)!);
        info.Attributes!.RemoveNamedItem("xmlns");
        var hash = SHA256.HashData(Canonical((XmlElement)info));
        var value = owner.Key switch
        {
            RSA rsa => rsa.SignHash(hash, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            ECDsa ec => ec.SignHash(hash),
            _ => throw new InvalidOperationException("The signing key is neither RSA nor ECDSA."),
        };
        var signatureValue = (XmlElement)root.SelectSingleNode("ds:SignatureValue", ns)!;
        signatureValue.InnerText = Convert.ToBase64String(value);

        // XAdES-T: the timestamp service vouches for the time of the signature value.
        Exception? timestampFailure = null;
        var stamped = false;
        if (owner.Timestamps is { } t)
        {
            try
            {
                var token = await t.StampAsync(Canonical(signatureValue));
                var qualifying = (XmlElement)root.SelectSingleNode("//xd:QualifyingProperties", ns)!;
                var unsigned = doc.CreateElement("xd", "UnsignedProperties", Xades);
                var props = doc.CreateElement("xd", "UnsignedSignatureProperties", Xades);
                var stamp = doc.CreateElement("xd", "SignatureTimeStamp", Xades);
                stamp.SetAttribute("Id", "idSignatureTimeStamp");
                var method = doc.CreateElement("CanonicalizationMethod", Dsig);
                method.SetAttribute("Algorithm", C14N);
                var encapsulated = doc.CreateElement("xd", "EncapsulatedTimeStamp", Xades);
                encapsulated.InnerText = Convert.ToBase64String(token);
                stamp.AppendChild(method);
                stamp.AppendChild(encapsulated);
                props.AppendChild(stamp);
                unsigned.AppendChild(props);
                qualifying.AppendChild(unsigned);
                stamped = true;
            }
            catch (Exception ex) { timestampFailure = ex; }
        }

        parts[SignaturePart] = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" + root.OuterXml);
        var describe = owner.Signature.Describe + (owner.Timestamps is null ? ""
            : stamped ? $", time confirmed by {owner.Timestamps.Host}"
            : ", time not confirmed (the timestamp service could not be reached)");
        return new SignedPdf(Zip(parts), describe, timestampFailure);
    }

    private static byte[] Zip(Dictionary<string, byte[]> parts)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            // [Content_Types].xml first, as Word writes it.
            foreach (var (name, data) in parts.OrderBy(p => p.Key == "[Content_Types].xml" ? 0 : 1))
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var s = entry.Open();
                s.Write(data);
            }
        }
        return output.ToArray();
    }

    /// <summary>Inclusive canonical XML of an element as it stands inside the signature (namespaces of its ancestors
    /// included), as signature checkers compute it.</summary>
    internal static byte[] Canonical(XmlElement element)
    {
        var copy = new XmlDocument { PreserveWhitespace = true };
        var root = (XmlElement)copy.ImportNode(element, true);
        copy.AppendChild(root);
        for (var up = element.ParentNode as XmlElement; up is not null; up = up.ParentNode as XmlElement)
            foreach (XmlAttribute a in up.Attributes)
                if ((a.Name == "xmlns" || a.Prefix == "xmlns") && !root.HasAttribute(a.Name))
                    root.SetAttribute(a.Name, a.Value);
        if (root.Prefix.Length == 0 && !root.HasAttribute("xmlns") && root.NamespaceURI.Length > 0) root.SetAttribute("xmlns", root.NamespaceURI);
        var c14n = new XmlDsigC14NTransform();
        c14n.LoadInput(copy);
        using var s = (Stream)c14n.GetOutput(typeof(Stream));
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    /// <summary>The relationship transform (ECMA-376 part 2): the signed relationships sorted by id, written canonically.</summary>
    internal static (List<string> Ids, byte[] Canonical) Relationships(byte[] rels)
    {
        var doc = new XmlDocument();
        doc.Load(new MemoryStream(rels));
        var kept = doc.DocumentElement!.ChildNodes.OfType<XmlElement>()
            .Where(e => e.LocalName == "Relationship" && !Unsigned(e.GetAttribute("Type")))
            .OrderBy(e => e.GetAttribute("Id"), StringComparer.Ordinal).ToList();
        var sb = new StringBuilder($"<Relationships xmlns=\"{RelsNs}\">");
        foreach (var r in kept)
        {
            var mode = r.GetAttribute("TargetMode");
            sb.Append($"<Relationship Id=\"{Attr(r.GetAttribute("Id"))}\" Target=\"{Attr(r.GetAttribute("Target"))}\" TargetMode=\"{(mode.Length == 0 ? "Internal" : Attr(mode))}\" Type=\"{Attr(r.GetAttribute("Type"))}\"></Relationship>");
        }
        sb.Append("</Relationships>");
        return (kept.Select(e => e.GetAttribute("Id")).ToList(), Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static HashSet<string> UnsignedTargets(byte[] packageRels)
    {
        var doc = new XmlDocument();
        doc.Load(new MemoryStream(packageRels));
        return doc.DocumentElement!.ChildNodes.OfType<XmlElement>()
            .Where(e => Unsigned(e.GetAttribute("Type")) && e.GetAttribute("TargetMode") != "External")
            .Select(e => e.GetAttribute("Target").TrimStart('/'))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static byte[] AddRelationship(byte[] rels, string type, string target)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.Load(new MemoryStream(rels));
        var ids = doc.DocumentElement!.ChildNodes.OfType<XmlElement>().Select(e => e.GetAttribute("Id")).ToHashSet();
        var n = 1;
        while (ids.Contains($"rIdVfSig{n}")) n++;
        var r = doc.CreateElement("Relationship", RelsNs);
        r.SetAttribute("Id", $"rIdVfSig{n}");
        r.SetAttribute("Type", type);
        r.SetAttribute("Target", target);
        doc.DocumentElement.AppendChild(r);
        return Save(doc);
    }

    private static byte[] AddContentTypes(byte[] types)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.Load(new MemoryStream(types));
        var root = doc.DocumentElement!;
        if (!root.ChildNodes.OfType<XmlElement>().Any(e => e.LocalName == "Default" && e.GetAttribute("Extension").Equals("sigs", StringComparison.OrdinalIgnoreCase)))
        {
            var d = doc.CreateElement("Default", TypesNs);
            d.SetAttribute("Extension", "sigs");
            d.SetAttribute("ContentType", "application/vnd.openxmlformats-package.digital-signature-origin");
            root.PrependChild(d);
        }
        var o = doc.CreateElement("Override", TypesNs);
        o.SetAttribute("PartName", "/" + SignaturePart);
        o.SetAttribute("ContentType", "application/vnd.openxmlformats-package.digital-signature-xmlsignature+xml");
        root.AppendChild(o);
        return Save(doc);
    }

    private static Func<string, string> ContentTypes(byte[] types)
    {
        var doc = new XmlDocument();
        doc.Load(new MemoryStream(types));
        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in doc.DocumentElement!.ChildNodes.OfType<XmlElement>())
            if (e.LocalName == "Default") defaults[e.GetAttribute("Extension")] = e.GetAttribute("ContentType");
            else if (e.LocalName == "Override") overrides[e.GetAttribute("PartName").TrimStart('/')] = e.GetAttribute("ContentType");
        return name => overrides.TryGetValue(name, out var o) ? o
            : defaults.TryGetValue(Path.GetExtension(name).TrimStart('.'), out var d) ? d
            : throw new InvalidOperationException($"The Word file has no content type for {name}.");
    }

    private static byte[] Save(XmlDocument doc)
    {
        using var m = new MemoryStream();
        using (var w = XmlWriter.Create(m, new XmlWriterSettings { Encoding = new UTF8Encoding(false) })) doc.Save(w);
        return m.ToArray();
    }

    private static string Digest(byte[] hash) =>
        $"<DigestMethod Algorithm=\"{Sha256}\"/><DigestValue>{Convert.ToBase64String(hash)}</DigestValue>";

    private static string Text(string s) => s.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    /// <summary>An attribute value as canonical XML writes it.</summary>
    private static string Attr(string s) => s.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal).Replace("\t", "&#x9;", StringComparison.Ordinal)
        .Replace("\n", "&#xA;", StringComparison.Ordinal).Replace("\r", "&#xD;", StringComparison.Ordinal);
}
