using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Signatures;

namespace VerbaFlow.Infrastructure.Export;

/// <summary>Who signed an exported PDF: the certificate's name and fingerprint, and whether it is only a test certificate.</summary>
public sealed record PdfSignature(string SignedBy, string Thumbprint, bool TestCertificate)
{
    /// <summary>The words written to History, e.g. "VerbaFlow test signature (not trusted) (test certificate 1A2B3C4D)".</summary>
    public string Describe => TestCertificate ? $"{SignedBy} (test certificate {Thumbprint[..8]})" : $"{SignedBy} (certificate {Thumbprint[..8]})";
}

/// <summary>A signed PDF, and what History says about the signature: who signed it and whether the time was confirmed by a
/// timestamp service. <see cref="TimestampFailure"/> is set when the timestamp service could not be used (the file is still
/// signed, with the computer's own time).</summary>
public sealed record SignedPdf(byte[] Bytes, string Describe, Exception? TimestampFailure = null, Exception? LongTermFailure = null);

/// <summary>
/// Puts a digital signature (PKCS#7, SHA-256) into an exported PDF, so a PDF reader can show that the file has not been
/// changed since VerbaFlow made it and who signed it. The signature is invisible on the page unless the PDF has an
/// approval box; Adobe Reader shows it in its signature panel. The key is either in a .pfx file, in Azure Key Vault (it
/// never leaves the vault: only the fingerprint of the file is sent there to be signed), or a test certificate made on
/// this computer when nothing is set up: readers then say the signer is not trusted (but still that the file is
/// unchanged). With a <see cref="Timestamper"/>, a trusted timestamp service vouches for the signing time (only a
/// fingerprint of the signature is sent to it). Word files are not signed.
/// </summary>
public sealed class PdfSigner
{
    /// <summary>The name a test certificate carries, so nobody mistakes it for the organisation's own.</summary>
    public const string TestName = "VerbaFlow test signature (not trusted)";

    private readonly X509Certificate2 certificate;
    private readonly AsymmetricAlgorithm key;
    private readonly X509Certificate2Collection chain;
    private readonly Timestamper? timestamps;
    private readonly SemaphoreSlim one = new(1, 1);

    /// <param name="key">The private key; by default the certificate's own. A Key Vault key is a <see cref="KeyVaultRsa"/>.</param>
    /// <param name="others">Certificates between the signer and the root (from the .pfx, a chain file, or downloaded).</param>
    public PdfSigner(X509Certificate2 certificate, bool testCertificate, AsymmetricAlgorithm? key = null,
        X509Certificate2Collection? others = null, Timestamper? timestamps = null, string? keyPlace = null)
    {
        this.key = key ?? (AsymmetricAlgorithm?)certificate.GetRSAPrivateKey() ?? certificate.GetECDsaPrivateKey()
            ?? throw new InvalidOperationException("The signing certificate has no private key.");
        this.certificate = certificate;
        this.timestamps = timestamps;
        chain = Intermediates(certificate, others ?? []);
        Signature = new PdfSignature(certificate.GetNameInfo(X509NameType.SimpleName, false), certificate.Thumbprint, testCertificate);
        KeyPlace = keyPlace ?? (testCertificate ? "a test certificate on this computer" : "a certificate file");
    }

    public PdfSignature Signature { get; }

    internal X509Certificate2 Certificate => certificate;
    internal AsymmetricAlgorithm Key => key;
    internal X509Certificate2Collection Chain => chain;
    internal Timestamper? Timestamps => timestamps;

    /// <summary>The same Word file with a digital signature Word checks (Office's own signature format).</summary>
    public Task<SignedPdf> SignWordAsync(byte[] docx, string reason, DateTimeOffset now) => new WordSigner(this).SignAsync(docx, reason, now);

    /// <summary>Where the key is kept, for the start-up log line.</summary>
    public string KeyPlace { get; }

    /// <summary>The timestamp service's host name, or null when the computer's own time is used.</summary>
    public string? TimestampService => timestamps?.Host;

    /// <summary>Adds the certificates and revocation answers after the signature (PAdES B-LT), or null to leave them out.</summary>
    public LongTermProof? LongTerm { get; set; }

    /// <summary>The certificates sent with each signature besides the signer's own.</summary>
    public int ChainLength => chain.Count;

    /// <summary>The organisation's certificate (.pfx file and its password) when given, otherwise a test certificate kept in
    /// <paramref name="keysDir"/> (made the first time).</summary>
    public static PdfSigner Create(string? certificatePath, string? password, string keysDir, Timestamper? timestamps = null,
        X509Certificate2Collection? others = null)
    {
        if (!string.IsNullOrWhiteSpace(certificatePath))
        {
            var all = LoadAll(File.ReadAllBytes(certificatePath), password);
            var signer = all.FirstOrDefault(c => c.HasPrivateKey)
                ?? throw new InvalidOperationException("The .pfx file has no certificate with a private key.");
            var rest = new X509Certificate2Collection();
            foreach (var c in all) if (!ReferenceEquals(c, signer)) rest.Add(c);
            if (others is not null) rest.AddRange(others);
            return new PdfSigner(signer, false, null, rest, timestamps);
        }
        var path = Path.Combine(keysDir, "test-signing.pfx");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(keysDir);
            var made = MakeTestCertificate();
            var temp = path + ".new";
            File.WriteAllBytes(temp, made);
            File.Move(temp, path, overwrite: true);
        }
        return new PdfSigner(Load(File.ReadAllBytes(path), null), true, null, null, timestamps);
    }

    private static X509KeyStorageFlags Flags =>
        OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet;

    private static X509Certificate2 Load(byte[] pfx, string? password) => X509CertificateLoader.LoadPkcs12(pfx, password, Flags);

    private static X509Certificate2Collection LoadAll(byte[] pfx, string? password) =>
        X509CertificateLoader.LoadPkcs12Collection(pfx, password, Flags);

    /// <summary>Certificates from a chain file the certificate seller gave (.p7b, .pem, .crt or .cer).</summary>
    public static X509Certificate2Collection LoadChainFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var found = new X509Certificate2Collection();
        var text = System.Text.Encoding.ASCII.GetString(bytes);
        if (text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)) { found.ImportFromPem(text); return found; }
        try
        {
            var cms = new SignedCms();
            cms.Decode(bytes);
            found.AddRange(cms.Certificates);
            return found;
        }
        catch (CryptographicException) { }
        found.Add(X509CertificateLoader.LoadCertificate(bytes));
        return found;
    }

    /// <summary>The certificates from the signer up to (not including) a root: from <paramref name="others"/>, or, when
    /// they do not reach that far, those the certificates point to on the web (looked up once, at start-up).</summary>
    private static X509Certificate2Collection Intermediates(X509Certificate2 leaf, X509Certificate2Collection others)
    {
        var result = new X509Certificate2Collection();
        if (leaf.SubjectName.RawData.AsSpan().SequenceEqual(leaf.IssuerName.RawData)) return result; // self-signed
        try
        {
            using var built = new X509Chain();
            built.ChainPolicy.ExtraStore.AddRange(others);
            built.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            built.ChainPolicy.VerificationFlags = X509VerificationFlags.AllFlags;
            built.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(10);
            built.Build(leaf);
            foreach (var e in built.ChainElements.Skip(1))
                if (!e.Certificate.SubjectName.RawData.AsSpan().SequenceEqual(e.Certificate.IssuerName.RawData))
                    result.Add(e.Certificate);
        }
        catch (CryptographicException) { }
        foreach (var c in others)
            if (!c.SubjectName.RawData.AsSpan().SequenceEqual(c.IssuerName.RawData) && result.Find(X509FindType.FindByThumbprint, c.Thumbprint, false).Count == 0
                && c.Thumbprint != leaf.Thumbprint)
                result.Add(c);
        return result;
    }

    /// <summary>A self-signed certificate for signing documents, valid for five years, with no password (it is a test key
    /// that stays on this computer and is trusted by nobody).</summary>
    public static byte[] MakeTestCertificate()
    {
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest($"CN={TestName}, O=VerbaFlow", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        // Document signing.
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.4.1.311.10.3.12")], false));
        var now = DateTimeOffset.UtcNow;
        using var cert = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(5));
        return cert.Export(X509ContentType.Pfx);
    }

    /// <summary>The same PDF with the signature added. <paramref name="reason"/> shows in the reader's signature panel.</summary>
    public async Task<SignedPdf> SignAsync(byte[] pdf, string reason, ExportWords? words = null)
    {
        await one.WaitAsync();
        try
        {
            using var input = new MemoryStream(pdf, writable: false);
            using var document = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
            // An approved PDF has a box with a line left for the signature: the signature is shown there.
            var place = ApprovalBox.Take(document);
            var options = place is not { } p
                ? new DigitalSignatureOptions { Reason = reason, Location = "VerbaFlow", AppName = "VerbaFlow", ContactInfo = "", PageIndex = 0 }
                : new DigitalSignatureOptions
                {
                    Reason = reason, Location = "VerbaFlow", AppName = "VerbaFlow", ContactInfo = "",
                    PageIndex = p.Page,
                    Rectangle = p.Rect,
                    AppearanceHandler = new ApprovalBox.SignatureLine(Signature.TestCertificate
                        ? (words ?? ExportWords.English).SignedTest
                        : string.Format((words ?? ExportWords.English).SignedBy, Signature.SignedBy)),
                };
            var cms = new CmsMaker(this);
            DigitalSignatureHandler.ForDocument(document, cms, options);
            using var output = new MemoryStream();
            await document.SaveAsync(output, false);
            var describe = Signature.Describe + (timestamps is null ? ""
                : cms.Stamped ? $", time confirmed by {timestamps.Host}"
                : ", time not confirmed (the timestamp service could not be reached)");
            var bytes = output.ToArray();
            Exception? longTermFailure = null;
            if (LongTerm is { } proof && cms.Encoded is { } signature)
            {
                try
                {
                    var (withProof, result) = await proof.AddAsync(bytes, signature);
                    bytes = withProof;
                    describe += ", " + result.Describe;
                }
                catch (Exception ex)
                {
                    longTermFailure = ex;
                    describe += ", long-term proof not added (a certificate could not be checked online)";
                }
            }
            return new SignedPdf(bytes, describe, cms.TimestampFailure, longTermFailure);
        }
        finally { one.Release(); }
    }

    /// <summary>ESS signing-certificate-v2 (RFC 5035): names the signer's certificate inside the signed part, as PAdES asks.</summary>
    public static AsnEncodedData SigningCertificateV2(X509Certificate2 cert)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        using (w.PushSequence())
        using (w.PushSequence())
            w.WriteOctetString(SHA256.HashData(cert.RawData)); // hashAlgorithm left out: SHA-256 is the default
        return new AsnEncodedData(new Oid("1.2.840.113549.1.9.16.2.47"), w.Encode());
    }

    /// <summary>Makes the PKCS#7 signature PDFsharp puts into the file.</summary>
    private sealed class CmsMaker(PdfSigner owner) : IDigitalSigner
    {
        public bool Stamped { get; private set; }
        public Exception? TimestampFailure { get; private set; }
        public byte[]? Encoded { get; private set; }

        public string CertificateName => owner.Signature.SignedBy;

        public Task<int> GetSignatureSizeAsync()
        {
            // The space kept in the file: the certificates, the signature itself and room for the rest (and the timestamp,
            // which brings the service's own certificates).
            var size = owner.certificate.RawData.Length + owner.chain.Cast<X509Certificate2>().Sum(c => c.RawData.Length)
                + 1024 + 2048 + (owner.timestamps is null ? 0 : 16384);
            return Task.FromResult(size);
        }

        public async Task<byte[]> GetSignatureAsync(Stream stream)
        {
            using var copy = new MemoryStream();
            // PDFsharp's stream of the signed parts cannot say whether it seeks, so CopyTo cannot be used.
            stream.Position = 0;
            var buffer = new byte[81920];
            int n;
            while ((n = await stream.ReadAsync(buffer)) > 0) copy.Write(buffer, 0, n);
            var cms = new SignedCms(new ContentInfo(copy.ToArray()), detached: true);
            var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, owner.certificate, owner.key)
            {
                DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"), // SHA-256
                IncludeOption = X509IncludeOption.EndCertOnly,
            };
            signer.Certificates.AddRange(owner.chain);
            signer.SignedAttributes.Add(SigningCertificateV2(owner.certificate));
            cms.ComputeSignature(signer, silent: true);
            if (owner.timestamps is { } t)
            {
                try
                {
                    var info = cms.SignerInfos[0];
                    var token = await t.StampAsync(info.GetSignature());
                    info.AddUnsignedAttribute(new AsnEncodedData(new Oid(Timestamper.TokenAttribute), token));
                    Stamped = true;
                }
                catch (Exception ex) { TimestampFailure = ex; }
            }
            return Encoded = cms.Encode();
        }
    }
}

/// <summary>
/// Asks a timestamp service (RFC 3161) to vouch for the time a signature was made. Only a SHA-256 fingerprint of the
/// signature is sent, never the document. Readers that trust the service show the signing time as confirmed.
/// </summary>
public sealed class Timestamper(HttpClient http, Uri address)
{
    /// <summary>The unsigned attribute that carries the token (id-aa-signatureTimeStampToken).</summary>
    public const string TokenAttribute = "1.2.840.113549.1.9.16.2.14";

    /// <summary>A free public service most PDF readers trust.</summary>
    public const string Default = "http://timestamp.digicert.com";

    public string Host => address.Host;

    /// <summary>The service's token (DER) for <paramref name="signature"/>.</summary>
    public async Task<byte[]> StampAsync(byte[] signature, CancellationToken ct = default)
    {
        var nonce = RandomNumberGenerator.GetBytes(8);
        nonce[0] &= 0x7f; // kept positive
        var request = Rfc3161TimestampRequest.CreateFromData(signature, HashAlgorithmName.SHA256, requestSignerCertificates: true, nonce: nonce);
        using var content = new ByteArrayContent(request.Encode());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/timestamp-query");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await http.PostAsync(address, content, timeout.Token);
        response.EnsureSuccessStatusCode();
        var answer = await response.Content.ReadAsByteArrayAsync(timeout.Token);
        var token = request.ProcessResponse(answer, out _);
        return token.AsSignedCms().Encode();
    }
}

/// <summary>
/// An RSA key kept in Azure Key Vault: signing sends only the fingerprint (hash) to the vault, which signs it there. The
/// private key cannot be read out, so it cannot be copied off the server.
/// </summary>
public sealed class KeyVaultRsa : RSA
{
    private readonly global::Azure.Security.KeyVault.Keys.Cryptography.CryptographyClient vault;
    private readonly RSA publicKey;

    public KeyVaultRsa(global::Azure.Security.KeyVault.Keys.Cryptography.CryptographyClient vault, RSA publicKey)
    {
        this.vault = vault;
        this.publicKey = publicKey;
        KeySizeValue = publicKey.KeySize;
        LegalKeySizesValue = [new KeySizes(publicKey.KeySize, publicKey.KeySize, 0)];
    }

    /// <summary>The organisation's certificate named <paramref name="certificateName"/> in the vault, with its key there.</summary>
    public static PdfSigner Signer(Uri vaultAddress, string certificateName, global::Azure.Core.TokenCredential credential,
        X509Certificate2Collection? others = null, Timestamper? timestamps = null)
    {
        var certificates = new global::Azure.Security.KeyVault.Certificates.CertificateClient(vaultAddress, credential);
        var stored = certificates.GetCertificate(certificateName).Value;
        var cert = X509CertificateLoader.LoadCertificate(stored.Cer);
        var rsa = cert.GetRSAPublicKey() ?? throw new InvalidOperationException("Only RSA keys in Key Vault can sign PDFs.");
        var key = new KeyVaultRsa(new global::Azure.Security.KeyVault.Keys.Cryptography.CryptographyClient(stored.KeyId, credential), rsa);
        return new PdfSigner(cert, false, key, others, timestamps, $"Azure Key Vault ({vaultAddress.Host})");
    }

    public override byte[] SignHash(byte[] hash, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding)
    {
        if (padding != RSASignaturePadding.Pkcs1) throw new CryptographicException("Only PKCS#1 signatures are made.");
        var algorithm = hashAlgorithm == HashAlgorithmName.SHA256 ? global::Azure.Security.KeyVault.Keys.Cryptography.SignatureAlgorithm.RS256
            : hashAlgorithm == HashAlgorithmName.SHA384 ? global::Azure.Security.KeyVault.Keys.Cryptography.SignatureAlgorithm.RS384
            : hashAlgorithm == HashAlgorithmName.SHA512 ? global::Azure.Security.KeyVault.Keys.Cryptography.SignatureAlgorithm.RS512
            : throw new CryptographicException($"Hash {hashAlgorithm.Name} is not used.");
        return vault.Sign(algorithm, hash).Signature;
    }

    public override bool VerifyHash(byte[] hash, byte[] signature, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding) =>
        publicKey.VerifyHash(hash, signature, hashAlgorithm, padding);

    public override RSAParameters ExportParameters(bool includePrivateParameters) =>
        includePrivateParameters ? throw new CryptographicException("The key stays in Key Vault.") : publicKey.ExportParameters(false);

    public override void ImportParameters(RSAParameters parameters) => throw new NotSupportedException();

    public override byte[] Encrypt(byte[] data, RSAEncryptionPadding padding) => publicKey.Encrypt(data, padding);

    public override byte[] Decrypt(byte[] data, RSAEncryptionPadding padding) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) publicKey.Dispose();
        base.Dispose(disposing);
    }
}
