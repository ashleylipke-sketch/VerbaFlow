using System.Security.Cryptography;
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

/// <summary>
/// Puts a digital signature (PKCS#7, SHA-256) into an exported PDF, so a PDF reader can show that the file has not been
/// changed since VerbaFlow made it and who signed it. The signature is invisible on the page; Adobe Reader shows it in
/// its signature panel. A test certificate is made on this computer when no organisation certificate is set up: readers
/// then say the signer is not trusted (but still that the file is unchanged). Word files are not signed.
/// </summary>
public sealed class PdfSigner
{
    /// <summary>The name a test certificate carries, so nobody mistakes it for the organisation's own.</summary>
    public const string TestName = "VerbaFlow test signature (not trusted)";

    private readonly X509Certificate2 certificate;
    private readonly SemaphoreSlim one = new(1, 1);

    public PdfSigner(X509Certificate2 certificate, bool testCertificate)
    {
        if (!certificate.HasPrivateKey) throw new InvalidOperationException("The signing certificate has no private key.");
        this.certificate = certificate;
        Signature = new PdfSignature(certificate.GetNameInfo(X509NameType.SimpleName, false), certificate.Thumbprint, testCertificate);
    }

    public PdfSignature Signature { get; }

    /// <summary>The organisation's certificate (.pfx file and its password) when given, otherwise a test certificate kept in
    /// <paramref name="keysDir"/> (made the first time).</summary>
    public static PdfSigner Create(string? certificatePath, string? password, string keysDir)
    {
        if (!string.IsNullOrWhiteSpace(certificatePath))
            return new PdfSigner(Load(File.ReadAllBytes(certificatePath), password), false);
        var path = Path.Combine(keysDir, "test-signing.pfx");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(keysDir);
            var made = MakeTestCertificate();
            var temp = path + ".new";
            File.WriteAllBytes(temp, made);
            File.Move(temp, path, overwrite: true);
        }
        return new PdfSigner(Load(File.ReadAllBytes(path), null), true);
    }

    private static X509Certificate2 Load(byte[] pfx, string? password) =>
        X509CertificateLoader.LoadPkcs12(pfx, password,
            OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet);

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
    public async Task<byte[]> SignAsync(byte[] pdf, string reason, ExportWords? words = null)
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
            DigitalSignatureHandler.ForDocument(document, new PdfSharpDefaultSigner(certificate, PdfMessageDigestType.SHA256, null), options);
            using var output = new MemoryStream();
            await document.SaveAsync(output, false);
            return output.ToArray();
        }
        finally { one.Release(); }
    }
}
