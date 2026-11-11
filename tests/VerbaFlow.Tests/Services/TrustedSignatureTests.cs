using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using VerbaFlow.Infrastructure.Export;

namespace VerbaFlow.Tests.Services;

/// <summary>Trusted timestamps, the seller's certificate chain and keys kept in Azure Key Vault, without any real service.</summary>
public class TrustedSignatureTests
{
    /// <summary>A timestamp service (RFC 3161) on this computer, with its own certificate, like DigiCert's.</summary>
    internal sealed class FakeTimestampService : HttpMessageHandler, IDisposable
    {
        private readonly X509Certificate2 tsa;
        public int Asked;
        public bool Down;

        public FakeTimestampService()
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=Test Timestamp Service", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.8")], true)); // time stamping
            using var made = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            tsa = X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.EphemeralKeySet);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked++;
            if (Down) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            Assert.Equal("application/timestamp-query", request.Content!.Headers.ContentType!.MediaType);
            Assert.True(Rfc3161TimestampRequest.TryDecode(await request.Content.ReadAsByteArrayAsync(ct), out var asked, out _));
            var info = new Rfc3161TimestampTokenInfo(new Oid("1.2.3.4.1"), asked!.HashAlgorithmId, asked.GetMessageHash(),
                new byte[] { 1, 2, 3 }, DateTimeOffset.UtcNow, nonce: asked.GetNonce());
            var cms = new SignedCms(new ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), info.Encode()));
            var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, tsa) { IncludeOption = X509IncludeOption.EndCertOnly };
            signer.SignedAttributes.Add(PdfSigner.SigningCertificateV2(tsa));
            cms.ComputeSignature(signer, silent: true);
            var w = new AsnWriter(AsnEncodingRules.DER);
            using (w.PushSequence())
            {
                using (w.PushSequence()) w.WriteInteger(0); // granted
                w.WriteEncodedValue(cms.Encode());
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(w.Encode()) };
        }

        public new void Dispose() { tsa.Dispose(); base.Dispose(); }
    }

    private static PdfSigner Signer(Env env, Timestamper? t) => PdfSigner.Create(null, null, Path.Combine(env.Dir, "keys"), t);

    [Fact]
    public async Task A_timestamp_service_confirms_the_signing_time_and_History_says_so()
    {
        using var env = new Env();
        using var service = new FakeTimestampService();
        var id = await env.ProcessedRecordingAsync();
        var exports = new ExportService(env.Stores, env.Clock, null, env.Reporter,
            Signer(env, new Timestamper(new HttpClient(service), new Uri("http://tsa.test/"))));

        var file = await exports.ExportAsync(env.Alice, id, "pdf");
        var cms = SigningTests.VerifySignature(file.Content);
        var signerInfo = cms.SignerInfos[0];
        // The signer's certificate is named inside the signed part (PAdES).
        Assert.Contains(signerInfo.SignedAttributes.Cast<CryptographicAttributeObject>(), a => a.Oid.Value == "1.2.840.113549.1.9.16.2.47");
        var stamp = signerInfo.UnsignedAttributes.Cast<CryptographicAttributeObject>().Single(a => a.Oid.Value == Timestamper.TokenAttribute);
        Assert.True(Rfc3161TimestampToken.TryDecode(stamp.Values[0].RawData, out var token, out _));
        Assert.True(token!.VerifySignatureForSignerInfo(signerInfo, out var by));
        Assert.Equal("CN=Test Timestamp Service", by!.Subject);
        Assert.Equal(1, service.Asked);
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "vf-timestamped.pdf"), file.Content);

        var record = Assert.Single(await env.Stores.Exports.ListAsync());
        Assert.EndsWith(", time confirmed by tsa.test", record.Signature);
        Assert.Contains("time confirmed by tsa.test", (await env.Stores.Audit.ListAsync(id)).Last().Details);
        Assert.True((await exports.CheckAsync(env.Alice, id, new MemoryStream(file.Content))).Matches);
    }

    [Fact]
    public async Task When_the_timestamp_service_is_down_the_file_is_still_signed_and_History_gives_a_reference()
    {
        using var env = new Env();
        using var service = new FakeTimestampService { Down = true };
        var id = await env.ProcessedRecordingAsync();
        var exports = new ExportService(env.Stores, env.Clock, null, env.Reporter,
            Signer(env, new Timestamper(new HttpClient(service), new Uri("http://tsa.test/"))));

        var file = await exports.ExportAsync(env.Alice, id, "pdf");
        var cms = SigningTests.VerifySignature(file.Content);
        Assert.DoesNotContain(cms.SignerInfos[0].UnsignedAttributes.Cast<CryptographicAttributeObject>(), a => a.Oid.Value == Timestamper.TokenAttribute);
        var record = Assert.Single(await env.Stores.Exports.ListAsync());
        Assert.Matches(@"time not confirmed \(the timestamp service could not be reached\) \(VF-[A-Z0-9]+\)$", record.Signature);
        Assert.Contains(await env.Stores.SupportErrors.ListAsync(), e => e.Area == "timestamp");
    }

    /// <summary>A root, an intermediate (as a certificate seller has) and a document-signing certificate under them.</summary>
    private static (X509Certificate2 Root, X509Certificate2 Middle, X509Certificate2 Leaf) Chain(RSA leafKey)
    {
        using var rootKey = RSA.Create(2048);
        var rootReq = new CertificateRequest("CN=Test Root CA", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var root = rootReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddYears(2));
        using var midKey = RSA.Create(2048);
        var midReq = new CertificateRequest("CN=Test Issuing CA", midKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        midReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var midPublic = midReq.Create(root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), [1, 2]);
        var middle = midPublic.CopyWithPrivateKey(midKey);
        var leafReq = new CertificateRequest("CN=Example Law LLP", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leafReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        var leaf = leafReq.Create(middle, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(300), [3, 4]);
        return (root, middle, leaf);
    }

    [Fact]
    public async Task A_bought_certificate_is_sent_with_its_issuer_but_not_the_root()
    {
        using var env = new Env();
        using var leafKey = RSA.Create(2048);
        var (root, middle, leafPublic) = Chain(leafKey);
        using var leaf = leafPublic.CopyWithPrivateKey(leafKey);
        var path = Path.Combine(env.Dir, "org.pfx");
        File.WriteAllBytes(path, new X509Certificate2Collection { leaf, middle, root }.Export(X509ContentType.Pfx, "pw")!);
        var signer = PdfSigner.Create(path, "pw", Path.Combine(env.Dir, "keys"));
        Assert.Equal("Example Law LLP", signer.Signature.SignedBy);
        Assert.Equal(1, signer.ChainLength);

        var id = await env.ProcessedRecordingAsync();
        var file = await new ExportService(env.Stores, env.Clock, null, env.Reporter, signer).ExportAsync(env.Alice, id, "pdf");
        var cms = SigningTests.VerifySignature(file.Content);
        var sent = cms.Certificates.Cast<X509Certificate2>().Select(c => c.Subject).Order().ToArray();
        Assert.Equal(["CN=Example Law LLP", "CN=Test Issuing CA"], sent);
        Assert.Equal("CN=Example Law LLP", cms.SignerInfos[0].Certificate!.Subject);
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "vf-chain.pdf"), file.Content);
    }

    [Fact]
    public void A_chain_file_from_the_seller_is_read_as_PEM_or_PKCS7()
    {
        using var env = new Env();
        using var leafKey = RSA.Create(2048);
        var (root, middle, _) = Chain(leafKey);
        var pem = Path.Combine(env.Dir, "chain.pem");
        File.WriteAllText(pem, middle.ExportCertificatePem() + "\n" + root.ExportCertificatePem());
        Assert.Equal(2, PdfSigner.LoadChainFile(pem).Count);
        var p7b = Path.Combine(env.Dir, "chain.p7b");
        File.WriteAllBytes(p7b, new X509Certificate2Collection { middle, root }.Export(X509ContentType.Pkcs7)!);
        Assert.Equal(2, PdfSigner.LoadChainFile(p7b).Count);
        var cer = Path.Combine(env.Dir, "issuer.cer");
        File.WriteAllBytes(cer, middle.RawData);
        Assert.Single(PdfSigner.LoadChainFile(cer));
    }

    /// <summary>Key Vault standing in: signs a hash with a key the caller never sees.</summary>
    private sealed class FakeVault(RSA key) : global::Azure.Security.KeyVault.Keys.Cryptography.CryptographyClient
    {
        public int Signed;
        public override global::Azure.Security.KeyVault.Keys.Cryptography.SignResult Sign(
            global::Azure.Security.KeyVault.Keys.Cryptography.SignatureAlgorithm algorithm, byte[] digest, CancellationToken cancellationToken = default)
        {
            Assert.Equal(global::Azure.Security.KeyVault.Keys.Cryptography.SignatureAlgorithm.RS256, algorithm);
            Assert.Equal(32, digest.Length); // only the fingerprint goes to the vault
            Signed++;
            return global::Azure.Security.KeyVault.Keys.CryptographyModelFactory.SignResult("https://vault.test/keys/k/1",
                key.SignHash(digest, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), algorithm);
        }
    }

    [Fact]
    public async Task A_key_kept_in_Key_Vault_signs_without_leaving_it()
    {
        using var env = new Env();
        using var vaultKey = RSA.Create(2048);
        var (_, middle, leaf) = Chain(vaultKey); // the certificate VerbaFlow gets has no private key
        Assert.False(leaf.HasPrivateKey);
        var vault = new FakeVault(vaultKey);
        var key = new KeyVaultRsa(vault, leaf.GetRSAPublicKey()!);
        Assert.Throws<CryptographicException>(() => key.ExportParameters(true));
        var signer = new PdfSigner(leaf, false, key, [middle], null, "Azure Key Vault (vault.test)");

        var id = await env.ProcessedRecordingAsync();
        var file = await new ExportService(env.Stores, env.Clock, null, env.Reporter, signer).ExportAsync(env.Alice, id, "pdf");
        var cms = SigningTests.VerifySignature(file.Content);
        Assert.Equal("CN=Example Law LLP", cms.SignerInfos[0].Certificate!.Subject);
        Assert.Equal(1, vault.Signed);
        Assert.Contains("Example Law LLP (certificate", Assert.Single(await env.Stores.Exports.ListAsync()).Signature);
    }
}
