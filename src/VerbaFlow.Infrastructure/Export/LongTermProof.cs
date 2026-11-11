using System.Collections.Concurrent;
using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace VerbaFlow.Infrastructure.Export;

/// <summary>What was added to a signed PDF for long-term proof: how many certificates and revocation answers.</summary>
public sealed record LongTermResult(int Certificates, int OcspAnswers, int RevocationLists)
{
    public string Describe => OcspAnswers + RevocationLists == 0
        ? $"long-term proof added ({Certificates} certificate{(Certificates == 1 ? "" : "s")}, nothing to check online)"
        : $"long-term proof added ({Certificates} certificates, {OcspAnswers + RevocationLists} revocation check{(OcspAnswers + RevocationLists == 1 ? "" : "s")})";
}

/// <summary>
/// Long-term proof (PAdES B-LT): after a PDF is signed, the certificates of the signer and of the timestamp service, and
/// the answers saying none of them had been revoked (OCSP answers, or revocation lists when a certificate has no OCSP
/// address), are put into the PDF's Document Security Store. A reader can then check the signature years later, without
/// the internet and after the certificates have expired. The store is added after the signed part (an incremental
/// update), so the signature itself is untouched. Answers are kept in memory until they expire (at most 12 hours), so
/// each export does not ask again. Only certificates are sent to the certificate authorities, never the document.
/// </summary>
public sealed class LongTermProof(HttpClient http)
{
    private sealed record Answer(byte[] Data, bool Ocsp, DateTimeOffset Until);

    private readonly ConcurrentDictionary<string, Answer> answers = new();
    private static readonly TimeSpan KeepAtMost = TimeSpan.FromHours(12);

    /// <summary>The PDF with the Document Security Store added. Throws when a certificate that has an address to check it
    /// cannot be checked, or is revoked: then nothing is added and the caller keeps the signed file as it was.</summary>
    public async Task<(byte[] Pdf, LongTermResult Result)> AddAsync(byte[] signedPdf, byte[] signature, CancellationToken ct = default)
    {
        var certs = CertificatesOf(signature);
        var chains = Chains(certs);
        var ocsps = new List<byte[]>();
        var crls = new List<byte[]>();
        foreach (var (cert, issuer) in chains)
        {
            var answer = await CheckAsync(cert, issuer, ct);
            if (answer is null) continue;
            (answer.Ocsp ? ocsps : crls).Add(answer.Data);
        }
        var all = new List<X509Certificate2>();
        foreach (var c in chains.SelectMany(p => new[] { p.Cert, p.Issuer }).Where(c => c is not null).Concat(certs))
            if (!all.Any(a => a.Thumbprint == c!.Thumbprint)) all.Add(c!);
        var pdf = Append(signedPdf, all.Select(c => c.RawData).ToList(), ocsps, crls);
        return (pdf, new LongTermResult(all.Count, ocsps.Count, crls.Count));
    }

    /// <summary>The signer's certificates and those of the timestamp service, from the PKCS#7 signature.</summary>
    internal static List<X509Certificate2> CertificatesOf(byte[] signature)
    {
        var cms = new SignedCms();
        cms.Decode(signature);
        var found = cms.Certificates.Cast<X509Certificate2>().ToList();
        foreach (var info in cms.SignerInfos)
            foreach (var attr in info.UnsignedAttributes)
                if (attr.Oid.Value == Timestamper.TokenAttribute)
                    foreach (var v in attr.Values)
                    {
                        var token = new SignedCms();
                        token.Decode(v.RawData);
                        found.AddRange(token.Certificates.Cast<X509Certificate2>());
                    }
        return found;
    }

    private static bool SelfSigned(X509Certificate2 c) => c.SubjectName.RawData.AsSpan().SequenceEqual(c.IssuerName.RawData);

    /// <summary>Each certificate below a root, with the certificate that issued it. Missing issuers are fetched from the
    /// addresses in the certificates (once).</summary>
    private static List<(X509Certificate2 Cert, X509Certificate2? Issuer)> Chains(List<X509Certificate2> certs)
    {
        var pairs = new List<(X509Certificate2, X509Certificate2?)>();
        foreach (var leaf in certs)
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.ExtraStore.AddRange(certs.ToArray());
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllFlags;
            chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(10);
            try { chain.Build(leaf); } catch (CryptographicException) { continue; }
            var elements = chain.ChainElements.Select(e => e.Certificate).ToList();
            for (var i = 0; i < elements.Count; i++)
            {
                var c = elements[i];
                if (SelfSigned(c) || pairs.Any(p => p.Item1.Thumbprint == c.Thumbprint)) continue;
                pairs.Add((c, i + 1 < elements.Count ? elements[i + 1] : null));
            }
        }
        return pairs;
    }

    private async Task<Answer?> CheckAsync(X509Certificate2 cert, X509Certificate2? issuer, CancellationToken ct)
    {
        var key = cert.Thumbprint;
        if (answers.TryGetValue(key, out var kept) && kept.Until > DateTimeOffset.UtcNow) return kept;
        var (ocspUrls, crlUrls) = Addresses(cert);
        if (ocspUrls.Count == 0 && crlUrls.Count == 0) return null; // nothing to check it against (e.g. a test certificate)
        Exception? last = null;
        if (issuer is not null)
            foreach (var url in ocspUrls)
                try
                {
                    var (data, until) = await OcspAsync(url, cert, issuer, ct);
                    return answers[key] = new Answer(data, true, until);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { last = ex; }
        foreach (var url in crlUrls)
            try
            {
                var (data, until) = await CrlAsync(url, cert, ct);
                return answers[key] = new Answer(data, false, until);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { last = ex; }
        throw new InvalidOperationException($"No revocation answer for {cert.Subject}: {last?.Message}", last);
    }

    /// <summary>The OCSP and revocation list (CRL) addresses in a certificate (http only, as the standards say).</summary>
    public static (List<string> Ocsp, List<string> Crl) Addresses(X509Certificate2 cert)
    {
        var ocsp = new List<string>();
        var crl = new List<string>();
        foreach (var ext in cert.Extensions)
        {
            if (ext is X509AuthorityInformationAccessExtension aia)
                ocsp.AddRange(aia.EnumerateOcspUris().Where(u => u.StartsWith("http", StringComparison.OrdinalIgnoreCase)));
            else if (ext.Oid?.Value == "2.5.29.31")
                crl.AddRange(CrlUris(ext.RawData));
        }
        return (ocsp, crl);
    }

    /// <summary>The fullName URIs of a CRL distribution points extension: every [6] (uniformResourceIdentifier) inside.</summary>
    internal static IEnumerable<string> CrlUris(byte[] der)
    {
        var found = new List<string>();
        void Walk(AsnReader r)
        {
            while (r.HasData)
            {
                var tag = r.PeekTag();
                if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 6 && !tag.IsConstructed)
                    found.Add(Encoding.ASCII.GetString(r.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 6))));
                else if (tag.IsConstructed) Walk(r.ReadSequence(tag));
                else r.ReadEncodedValue();
            }
        }
        try { Walk(new AsnReader(der, AsnEncodingRules.DER)); } catch (Exception e) when (e is AsnContentException or ArgumentException) { }
        return found.Where(u => u.StartsWith("http", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private async Task<(byte[], DateTimeOffset)> OcspAsync(string url, X509Certificate2 cert, X509Certificate2 issuer, CancellationToken ct)
    {
        var request = OcspRequest(cert, issuer);
        using var content = new ByteArrayContent(request);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/ocsp-request");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await http.PostAsync(url, content, timeout.Token);
        response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadAsByteArrayAsync(timeout.Token);
        var next = ReadOcsp(data, cert);
        var until = DateTimeOffset.UtcNow + KeepAtMost;
        return (data, next is { } n && n < until ? n : until);
    }

    private async Task<(byte[], DateTimeOffset)> CrlAsync(string url, X509Certificate2 cert, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var data = await http.GetByteArrayAsync(url, timeout.Token);
        if (data.Length > 10_000_000) throw new InvalidOperationException("The revocation list is too large.");
        var crl = CertificateRevocationListBuilder.Load(data, out _);
        if (crl.RemoveEntry(Convert.FromHexString(cert.SerialNumber)))
            throw new InvalidOperationException($"The certificate {cert.Subject} has been revoked.");
        var until = DateTimeOffset.UtcNow + KeepAtMost;
        return (data, CrlNextUpdate(data) is { } n && n < until ? n : until);
    }

    /// <summary>When a revocation list says it will next be published (its nextUpdate), if it says.</summary>
    internal static DateTimeOffset? CrlNextUpdate(byte[] data)
    {
        var tbs = new AsnReader(data, AsnEncodingRules.DER).ReadSequence().ReadSequence();
        if (tbs.PeekTag().HasSameClassAndValue(Asn1Tag.Integer)) tbs.ReadEncodedValue(); // version
        tbs.ReadEncodedValue(); // signature algorithm
        tbs.ReadEncodedValue(); // issuer
        ReadTime(tbs); // thisUpdate
        return tbs.HasData && (tbs.PeekTag().HasSameClassAndValue(Asn1Tag.UtcTime) || tbs.PeekTag().HasSameClassAndValue(Asn1Tag.GeneralizedTime))
            ? ReadTime(tbs) : null;
    }

    private static DateTimeOffset ReadTime(AsnReader r) =>
        r.PeekTag().HasSameClassAndValue(Asn1Tag.UtcTime) ? r.ReadUtcTime() : r.ReadGeneralizedTime();

    /// <summary>An OCSP request for one certificate (RFC 6960), with SHA-1 names as responders expect.</summary>
    internal static byte[] OcspRequest(X509Certificate2 cert, X509Certificate2 issuer)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())            // OCSPRequest
        using (w.PushSequence())            // TBSRequest
        using (w.PushSequence())            // requestList
        using (w.PushSequence())            // Request
            WriteCertId(w, cert, issuer);
        return w.Encode();
    }

    internal static void WriteCertId(AsnWriter w, X509Certificate2 cert, X509Certificate2 issuer)
    {
        using (w.PushSequence())
        {
            using (w.PushSequence())
            {
                w.WriteObjectIdentifier("1.3.14.3.2.26"); // SHA-1
                w.WriteNull();
            }
            w.WriteOctetString(SHA1.HashData(issuer.SubjectName.RawData));
            w.WriteOctetString(SHA1.HashData(issuer.PublicKey.EncodedKeyValue.RawData));
            w.WriteInteger(Convert.FromHexString(cert.SerialNumber));
        }
    }

    /// <summary>Checks an OCSP answer says the certificate is good, and returns when it should be asked again.</summary>
    internal static DateTimeOffset? ReadOcsp(byte[] data, X509Certificate2 cert)
    {
        var outer = new AsnReader(data, AsnEncodingRules.DER).ReadSequence();
        var status = outer.ReadEnumeratedBytes().Span;
        if (status.Length != 1 || status[0] != 0) throw new InvalidOperationException($"The OCSP service answered status {Convert.ToHexString(status)}.");
        var bytes = outer.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
        if (bytes.ReadObjectIdentifier() != "1.3.6.1.5.5.7.48.1.1") throw new InvalidOperationException("Not a basic OCSP answer.");
        var basic = new AsnReader(bytes.ReadOctetString(), AsnEncodingRules.DER).ReadSequence();
        var tbs = basic.ReadSequence();
        if (tbs.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0))) tbs.ReadEncodedValue(); // version
        tbs.ReadEncodedValue(); // responderID
        tbs.ReadGeneralizedTime(); // producedAt
        var responses = tbs.ReadSequence();
        var serial = Convert.FromHexString(cert.SerialNumber);
        while (responses.HasData)
        {
            var single = responses.ReadSequence();
            var certId = single.ReadSequence();
            certId.ReadSequence();
            certId.ReadOctetString();
            certId.ReadOctetString();
            var s = certId.ReadIntegerBytes().ToArray();
            var statusTag = single.PeekTag();
            single.ReadEncodedValue();
            single.ReadGeneralizedTime(); // thisUpdate
            DateTimeOffset? next = null;
            if (single.HasData && single.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
                next = single.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadGeneralizedTime();
            if (!s.AsSpan().TrimStart((byte)0).SequenceEqual(serial.AsSpan().TrimStart((byte)0))) continue;
            if (statusTag.TagValue == 1) throw new InvalidOperationException($"The certificate {cert.Subject} has been revoked.");
            if (statusTag.TagValue != 0) throw new InvalidOperationException($"The OCSP service does not know {cert.Subject}.");
            return next;
        }
        throw new InvalidOperationException("The OCSP answer is not about this certificate.");
    }

    /// <summary>Adds the Document Security Store after the end of the PDF (incremental update): new streams for the
    /// certificates and answers, the store, and the document catalogue again with the store named in it.</summary>
    internal static byte[] Append(byte[] pdf, List<byte[]> certs, List<byte[]> ocsps, List<byte[]> crls)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var startxref = int.Parse(Regex.Matches(text, @"startxref\s+(\d+)").Last().Groups[1].Value, CultureInfo.InvariantCulture);
        var trailer = Regex.Matches(text, @"trailer\s*<<(.*?)>>\s*(?:%[^\n]*\n)?\s*startxref", RegexOptions.Singleline).LastOrDefault()
            ?? throw new InvalidOperationException("The PDF has no classic trailer.");
        var dict = trailer.Groups[1].Value;
        var size = int.Parse(Regex.Match(dict, @"/Size\s+(\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
        var root = Regex.Match(dict, @"/Root\s+(\d+)\s+(\d+)\s+R");
        var rootNo = int.Parse(root.Groups[1].Value, CultureInfo.InvariantCulture);
        var info = Regex.Match(dict, @"/Info\s+\d+\s+\d+\s+R").Value;
        var id = Regex.Match(dict, @"/ID\s*\[.*?\]", RegexOptions.Singleline).Value;

        var catalog = CatalogBody(text, rootNo);
        catalog = Regex.Replace(catalog, @"/DSS\s+\d+\s+\d+\s+R", "");

        using var output = new MemoryStream();
        output.Write(pdf);
        if (pdf[^1] != '\n') output.WriteByte((byte)'\n');
        var offsets = new SortedDictionary<int, long>();
        var next = size;
        List<int> Streams(List<byte[]> items)
        {
            var numbers = new List<int>();
            foreach (var item in items)
            {
                var n = next++;
                offsets[n] = output.Position;
                Write(output, $"{n} 0 obj\n<</Length {item.Length}>>stream\n");
                output.Write(item);
                Write(output, "\nendstream\nendobj\n");
                numbers.Add(n);
            }
            return numbers;
        }
        static string Refs(List<int> n) => "[" + string.Join(" ", n.Select(x => $"{x} 0 R")) + "]";
        var certNos = Streams(certs);
        var ocspNos = Streams(ocsps);
        var crlNos = Streams(crls);
        var dss = next++;
        offsets[dss] = output.Position;
        Write(output, $"{dss} 0 obj\n<</Type/DSS/Certs{Refs(certNos)}"
            + (ocspNos.Count > 0 ? $"/OCSPs{Refs(ocspNos)}" : "") + (crlNos.Count > 0 ? $"/CRLs{Refs(crlNos)}" : "") + ">>\nendobj\n");
        offsets[rootNo] = output.Position;
        // The ESIC extension tells readers the file uses the PAdES store (ISO 32000-1 readers ignore it otherwise).
        var extensions = catalog.Contains("/Extensions") ? "" : "/Extensions<</ESIC<</BaseVersion/1.7/ExtensionLevel 5>>>>";
        var close = catalog.LastIndexOf(">>", StringComparison.Ordinal);
        Write(output, $"{rootNo} 0 obj\n{catalog[..close]}/DSS {dss} 0 R{extensions}>>\nendobj\n");

        var xref = output.Position;
        var sb = new StringBuilder("xref\n0 1\n0000000000 65535 f \n");
        foreach (var run in Runs(offsets))
        {
            sb.Append(CultureInfo.InvariantCulture, $"{run[0].Key} {run.Count}\n");
            foreach (var (_, off) in run) sb.Append(CultureInfo.InvariantCulture, $"{off:D10} 00000 n \n");
        }
        sb.Append(CultureInfo.InvariantCulture, $"trailer\n<</Size {next}/Root {rootNo} 0 R{(info.Length > 0 ? "/" + info.TrimStart('/') : "")}{id}/Prev {startxref}>>\nstartxref\n{xref}\n%%EOF\n");
        Write(output, sb.ToString());
        return output.ToArray();
    }

    private static List<List<KeyValuePair<int, long>>> Runs(SortedDictionary<int, long> offsets)
    {
        var runs = new List<List<KeyValuePair<int, long>>>();
        foreach (var kv in offsets)
        {
            if (runs.Count > 0 && runs[^1][^1].Key + 1 == kv.Key) runs[^1].Add(kv);
            else runs.Add([kv]);
        }
        return runs;
    }

    /// <summary>The dictionary of the document catalogue as last written, found through the cross-reference tables.</summary>
    private static string CatalogBody(string text, int rootNo)
    {
        var all = Regex.Matches(text, $@"(?:^|\n|\r){rootNo}\s+0\s+obj\s*(<<.*?)\s*endobj", RegexOptions.Singleline);
        if (all.Count == 0) throw new InvalidOperationException("The document catalogue was not found (it may be in an object stream).");
        return all.Last().Groups[1].Value.TrimEnd();
    }

    private static void Write(Stream s, string text) => s.Write(Encoding.Latin1.GetBytes(text));
}
