using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;
using VerbaFlow.Infrastructure.Export;

namespace VerbaFlow.Tests.Services;

/// <summary>Word files signed the way Word signs them, checked here as a signature checker would.</summary>
public class WordSigningTests
{
    private static Dictionary<string, byte[]> Parts(byte[] docx)
    {
        var parts = new Dictionary<string, byte[]>();
        using var zip = new ZipArchive(new MemoryStream(docx));
        foreach (var e in zip.Entries)
        {
            using var s = e.Open();
            using var m = new MemoryStream();
            s.CopyTo(m);
            parts[e.FullName] = m.ToArray();
        }
        return parts;
    }

    /// <summary>Checks the signature: the signed part of the signature with its certificate, then each part's fingerprint
    /// listed in it. Returns the signature document.</summary>
    internal static XmlDocument VerifyWord(byte[] docx)
    {
        var parts = Parts(docx);
        Assert.Contains("digital-signature/origin", Encoding.UTF8.GetString(parts["_rels/.rels"]));
        Assert.Contains("sig1.xml", Encoding.UTF8.GetString(parts["_xmlsignatures/_rels/origin.sigs.rels"]));
        Assert.Contains("/_xmlsignatures/sig1.xml", Encoding.UTF8.GetString(parts["[Content_Types].xml"]));
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.Load(new MemoryStream(parts["_xmlsignatures/sig1.xml"]));
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("ds", "http://www.w3.org/2000/09/xmldsig#");
        var signed = new SignedXml(doc);
        signed.LoadXml(doc.DocumentElement!);
        var cert = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(doc.SelectSingleNode("//ds:X509Certificate", ns)!.InnerText));
        Assert.True(signed.CheckSignature(cert, verifySignatureOnly: true), "the signature itself is not valid");

        var references = doc.SelectNodes("//ds:Manifest/ds:Reference", ns)!.Cast<XmlElement>().ToList();
        Assert.Contains(references, r => r.GetAttribute("URI").StartsWith("/word/document.xml?", StringComparison.Ordinal));
        foreach (var r in references)
        {
            var name = r.GetAttribute("URI")[1..].Split('?')[0];
            var data = parts[name];
            var sources = r.SelectNodes(".//*[local-name()='RelationshipReference']")!.Cast<XmlElement>().Select(e => e.GetAttribute("SourceId")).ToHashSet();
            if (sources.Count > 0)
            {
                // The relationship transform, done here independently: keep those relationships, sorted, then C14N.
                var rels = new XmlDocument();
                rels.Load(new MemoryStream(data));
                var kept = new XmlDocument();
                var root = kept.CreateElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
                kept.AppendChild(root);
                foreach (var e in rels.DocumentElement!.ChildNodes.OfType<XmlElement>().Where(e => sources.Contains(e.GetAttribute("Id")))
                    .OrderBy(e => e.GetAttribute("Id"), StringComparer.Ordinal))
                {
                    var copy = kept.CreateElement("Relationship", root.NamespaceURI);
                    foreach (var a in new[] { "Id", "Target", "TargetMode", "Type" })
                        copy.SetAttribute(a, a == "TargetMode" && !e.HasAttribute(a) ? "Internal" : e.GetAttribute(a));
                    root.AppendChild(copy);
                }
                var c14n = new XmlDsigC14NTransform();
                c14n.LoadInput(kept);
                using var s = (Stream)c14n.GetOutput(typeof(Stream));
                using var m = new MemoryStream();
                s.CopyTo(m);
                data = m.ToArray();
            }
            Assert.True(Convert.ToBase64String(SHA256.HashData(data)) == r.SelectSingleNode("ds:DigestValue", ns)!.InnerText,
                $"the part {name} has changed since it was signed");
        }
        return doc;
    }

    [Fact]
    public async Task A_Word_export_is_signed_and_the_signature_checks_out()
    {
        using var env = new Env();
        using var tsa = new TrustedSignatureTests.FakeTimestampService();
        var id = await env.ProcessedRecordingAsync();
        var signer = PdfSigner.Create(null, null, Path.Combine(env.Dir, "keys"), new Timestamper(new HttpClient(tsa), new Uri("http://tsa.test/")));
        var exports = new ExportService(env.Stores, env.Clock, null, env.Reporter, signer);

        var file = await exports.ExportAsync(env.Alice, id, "docx");
        var doc = VerifyWord(file.Content);
        var xml = doc.OuterXml;
        Assert.Contains("<SignatureComments>Exported from VerbaFlow (draft)</SignatureComments>", xml);
        Assert.Contains("EncapsulatedTimeStamp", xml);
        Assert.DoesNotContain("docProps/core.xml", xml); // document properties are left out, as Word does

        var record = Assert.Single(await env.Stores.Exports.ListAsync());
        Assert.Equal("docx", record.Format);
        Assert.EndsWith(", time confirmed by tsa.test", record.Signature);
        Assert.Contains("time confirmed by tsa.test", (await env.Stores.Audit.ListAsync(id)).Last().Details);
        var check = await exports.CheckAsync(env.Alice, id, new MemoryStream(file.Content));
        Assert.True(check.Matches);
        Assert.Contains("digital signature", check.Message);
    }

    [Fact]
    public async Task A_changed_word_in_a_signed_Word_file_breaks_the_signature()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        var exports = new ExportService(env.Stores, env.Clock, null, env.Reporter, PdfSigner.Create(null, null, Path.Combine(env.Dir, "keys")));
        var file = await exports.ExportAsync(env.Alice, id, "docx");
        VerifyWord(file.Content);

        var parts = Parts(file.Content);
        var text = Encoding.UTF8.GetString(parts["word/document.xml"]);
        var at = text.IndexOf("<w:t", StringComparison.Ordinal);
        parts["word/document.xml"] = Encoding.UTF8.GetBytes(text.Insert(text.IndexOf('>', at) + 1, "X"));
        using var changed = new MemoryStream();
        using (var zip = new ZipArchive(changed, ZipArchiveMode.Create, true))
            foreach (var (name, data) in parts)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(data);
            }
        var problem = Assert.ThrowsAny<Exception>(() => VerifyWord(changed.ToArray()));
        Assert.Contains("word/document.xml", problem.Message);
    }

    [Fact]
    public async Task A_letter_on_a_Word_letterhead_with_a_bought_certificate_is_signed_with_its_chain()
    {
        using var env = new Env();
        using var leafKey = RSA.Create(2048);
        var path = Path.Combine(env.Dir, "org.pfx");
        using (var rootKey = RSA.Create(2048))
        {
            var rootReq = new CertificateRequest("CN=Test Root CA", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            using var root = rootReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddYears(2));
            var leafReq = new CertificateRequest("CN=Example Law LLP", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var leafPublic = leafReq.Create(root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(300), [5, 6]);
            using var leaf = leafPublic.CopyWithPrivateKey(leafKey);
            File.WriteAllBytes(path, new X509Certificate2Collection { leaf, root }.Export(X509ContentType.Pfx, "pw")!);
        }
        var signer = PdfSigner.Create(path, "pw", Path.Combine(env.Dir, "keys"));
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, env.Dictation());
        await env.Processing.ProcessAsync(id);
        var exports = new ExportService(env.Stores, env.Clock, null, env.Reporter, signer);
        var file = await exports.ExportAsync(env.Alice, id, "docx");
        var doc = VerifyWord(file.Content);
        Assert.Contains("CN=Test Root CA", doc.OuterXml); // the issuer is named in the signed properties
        Assert.Contains("Example Law LLP", Assert.Single(await env.Stores.Exports.ListAsync()).Signature);
    }
}
