using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using VerbaFlow.Infrastructure.Export;

namespace VerbaFlow.Tests.Services;

/// <summary>Long-term proof (PAdES B-LT): certificate checks stored in the signed PDF, with a pretend certificate authority.</summary>
public class LongTermProofTests
{
    /// <summary>A certificate authority on this computer: an OCSP service for the signer's certificate and a revocation
    /// list (CRL) for the issuing CA's certificate.</summary>
    private sealed class FakeAuthority : HttpMessageHandler
    {
        public required X509Certificate2 Root { get; init; }   // with its key: signs the revocation list
        public required X509Certificate2 Middle { get; init; } // with its key: signs OCSP answers
        public required X509Certificate2 Leaf { get; init; }
        public bool LeafRevoked;
        public bool Down;
        public int OcspAsked, CrlAsked;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Down) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (request.RequestUri!.Host == "ocsp.test")
            {
                OcspAsked++;
                Assert.Equal("application/ocsp-request", request.Content!.Headers.ContentType!.MediaType);
                var asked = await request.Content.ReadAsByteArrayAsync(ct);
                Assert.Contains(Leaf.SerialNumber, Convert.ToHexString(asked)); // only the certificate is named
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Ocsp()) };
            }
            CrlAsked++;
            var builder = new CertificateRevocationListBuilder();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(builder.Build(Root, 1, DateTimeOffset.UtcNow.AddDays(7), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)),
            };
        }

        private byte[] Ocsp()
        {
            var now = DateTimeOffset.UtcNow;
            var tbs = new AsnWriter(AsnEncodingRules.DER);
            using (tbs.PushSequence())
            {
                using (tbs.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2))) // responderID byKey
                    tbs.WriteOctetString(SHA1.HashData(Middle.PublicKey.EncodedKeyValue.RawData));
                tbs.WriteGeneralizedTime(now, omitFractionalSeconds: true);
                using (tbs.PushSequence())
                using (tbs.PushSequence()) // SingleResponse
                {
                    using (tbs.PushSequence()) // CertID
                    {
                        using (tbs.PushSequence()) { tbs.WriteObjectIdentifier("1.3.14.3.2.26"); tbs.WriteNull(); }
                        tbs.WriteOctetString(SHA1.HashData(Middle.SubjectName.RawData));
                        tbs.WriteOctetString(SHA1.HashData(Middle.PublicKey.EncodedKeyValue.RawData));
                        tbs.WriteInteger(Convert.FromHexString(Leaf.SerialNumber));
                    }
                    if (LeafRevoked)
                        using (tbs.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1)))
                            tbs.WriteGeneralizedTime(now.AddDays(-1), omitFractionalSeconds: true);
                    else tbs.WriteNull(new Asn1Tag(TagClass.ContextSpecific, 0));
                    tbs.WriteGeneralizedTime(now, omitFractionalSeconds: true);
                    using (tbs.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                        tbs.WriteGeneralizedTime(now.AddDays(3), omitFractionalSeconds: true);
                }
            }
            var tbsBytes = tbs.Encode();
            var basic = new AsnWriter(AsnEncodingRules.DER);
            using (basic.PushSequence())
            {
                basic.WriteEncodedValue(tbsBytes);
                using (basic.PushSequence()) { basic.WriteObjectIdentifier("1.2.840.113549.1.1.11"); basic.WriteNull(); }
                basic.WriteBitString(Middle.GetRSAPrivateKey()!.SignData(tbsBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            }
            var outer = new AsnWriter(AsnEncodingRules.DER);
            using (outer.PushSequence())
            {
                outer.WriteEncodedValue(new byte[] { 0x0A, 0x01, 0x00 }); // successful
                using (outer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                using (outer.PushSequence())
                {
                    outer.WriteObjectIdentifier("1.3.6.1.5.5.7.48.1.1");
                    outer.WriteOctetString(basic.Encode());
                }
            }
            return outer.Encode();
        }
    }

    /// <summary>A root, an issuing CA whose certificate names a revocation list, and a signer whose certificate names an
    /// OCSP service, as bought certificates do.</summary>
    private static FakeAuthority Authority()
    {
        var rootKey = RSA.Create(2048);
        var rootReq = new CertificateRequest("CN=Test Root CA", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        rootReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootReq.PublicKey, false));
        var root = rootReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddYears(2));
        var midKey = RSA.Create(2048);
        var midReq = new CertificateRequest("CN=Test Issuing CA", midKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        midReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        midReq.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension(["http://crl.test/root.crl"]));
        using var midPublic = midReq.Create(root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), [1, 2]);
        var middle = midPublic.CopyWithPrivateKey(midKey);
        var leafKey = RSA.Create(2048);
        var leafReq = new CertificateRequest("CN=Example Law LLP", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leafReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        leafReq.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension(["http://ocsp.test/"], null));
        using var leafPublic = leafReq.Create(middle, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(300), [3, 4]);
        return new FakeAuthority { Root = root, Middle = middle, Leaf = leafPublic.CopyWithPrivateKey(leafKey) };
    }

    private static PdfSigner Signer(FakeAuthority ca) =>
        new(ca.Leaf, false, null, [ca.Middle]) { LongTerm = new LongTermProof(new HttpClient(ca)) };

    private static string Dss(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var dss = Regex.Match(text, @"<</Type/DSS[^\n]*>>");
        Assert.True(dss.Success, "no Document Security Store in the PDF");
        Assert.Matches(@"/DSS \d+ 0 R/Extensions<</ESIC<</BaseVersion/1.7/ExtensionLevel 5>>>>", text);
        Assert.Equal(2, Regex.Matches(text, "%%EOF").Count); // the signed file, then the update
        Assert.Matches(@"/Prev \d+>>\nstartxref\n\d+\n%%EOF\n$", text);
        return dss.Value;
    }

    [Fact]
    public async Task Certificate_checks_are_stored_after_the_signature_and_the_signature_stays_valid()
    {
        using var env = new Env();
        var ca = Authority();
        var exports = new ExportService(env.Stores, env.Clock, null, env.Reporter, Signer(ca));
        var id = await env.ProcessedRecordingAsync();

        var file = await exports.ExportAsync(env.Alice, id, "pdf");
        Assert.Empty(await env.Stores.SupportErrors.ListAsync());
        var cms = SigningTests.VerifySignature(file.Content);
        Assert.Equal("CN=Example Law LLP", cms.SignerInfos[0].Certificate!.Subject);
        var dss = Dss(file.Content);
        Assert.Matches(@"/Certs\[\d+ 0 R \d+ 0 R\]/OCSPs\[\d+ 0 R\]/CRLs\[\d+ 0 R\]", dss);
        Assert.Equal((1, 1), (ca.OcspAsked, ca.CrlAsked));
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "vf-long-term.pdf"), file.Content);

        var record = Assert.Single(await env.Stores.Exports.ListAsync());
        Assert.EndsWith(", long-term proof added (2 certificates, 2 revocation checks)", record.Signature);
        Assert.Contains("long-term proof added", (await env.Stores.Audit.ListAsync(id)).Last().Details);
        Assert.True((await exports.CheckAsync(env.Alice, id, new MemoryStream(file.Content))).Matches);

        // The answers are kept until they run out, so the next export does not ask again.
        await exports.ExportAsync(env.Alice, id, "pdf");
        Assert.Equal((1, 1), (ca.OcspAsked, ca.CrlAsked));
    }

    [Fact]
    public async Task A_revoked_certificate_gets_no_long_term_proof_and_History_gives_a_reference()
    {
        using var env = new Env();
        var ca = Authority();
        ca.LeafRevoked = true;
        var exports = new ExportService(env.Stores, env.Clock, null, env.Reporter, Signer(ca));
        var id = await env.ProcessedRecordingAsync();

        var file = await exports.ExportAsync(env.Alice, id, "pdf");
        SigningTests.VerifySignature(file.Content);
        Assert.DoesNotContain("/Type/DSS", Encoding.Latin1.GetString(file.Content));
        var record = Assert.Single(await env.Stores.Exports.ListAsync());
        Assert.Matches(@"long-term proof not added \(a certificate could not be checked online\) \(VF-[A-Z0-9]+\)$", record.Signature);
        var error = Assert.Single(await env.Stores.SupportErrors.ListAsync(), e => e.Area == "long-term-proof");
        Assert.DoesNotContain("revoked", record.Signature);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task When_the_certificate_authority_cannot_be_reached_the_file_is_still_signed()
    {
        using var env = new Env();
        var ca = Authority();
        ca.Down = true;
        var exports = new ExportService(env.Stores, env.Clock, null, env.Reporter, Signer(ca));
        var id = await env.ProcessedRecordingAsync();

        var file = await exports.ExportAsync(env.Alice, id, "pdf");
        SigningTests.VerifySignature(file.Content);
        Assert.Contains("long-term proof not added", Assert.Single(await env.Stores.Exports.ListAsync()).Signature);
    }

    [Fact]
    public async Task The_test_certificate_has_nothing_to_check_online_but_its_certificate_is_stored()
    {
        using var env = new Env();
        var signer = PdfSigner.Create(null, null, Path.Combine(env.Dir, "keys"));
        signer.LongTerm = new LongTermProof(new HttpClient(new FakeAuthority { Root = null!, Middle = null!, Leaf = null!, Down = true }));
        var exports = new ExportService(env.Stores, env.Clock, null, env.Reporter, signer);
        var id = await env.ProcessedRecordingAsync();

        var file = await exports.ExportAsync(env.Alice, id, "pdf");
        SigningTests.VerifySignature(file.Content);
        Assert.Matches(@"/Certs\[\d+ 0 R\]>>", Dss(file.Content));
        Assert.EndsWith(", long-term proof added (1 certificate, nothing to check online)", Assert.Single(await env.Stores.Exports.ListAsync()).Signature);
    }

    [Fact]
    public void The_revocation_list_address_is_read_from_a_certificate()
    {
        var ca = Authority();
        Assert.Equal(["http://ocsp.test/"], LongTermProof.Addresses(ca.Leaf).Ocsp);
        Assert.Empty(LongTermProof.Addresses(ca.Leaf).Crl);
        Assert.Equal(["http://crl.test/root.crl"], LongTermProof.Addresses(ca.Middle).Crl);
    }
}
