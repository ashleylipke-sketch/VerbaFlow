using System.Security.Cryptography.Pkcs;
using System.Text;
using System.Text.RegularExpressions;
using VerbaFlow.Infrastructure.Export;

namespace VerbaFlow.Tests.Services;

public class SigningTests
{
    /// <summary>Checks a signed PDF the way a PDF reader does: the signature covers every byte but itself and is valid.</summary>
    internal static SignedCms VerifySignature(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var range = Regex.Match(text, @"/ByteRange\s*\[\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*\]");
        Assert.True(range.Success, "no /ByteRange in the PDF");
        var n = range.Groups.Values.Skip(1).Select(g => int.Parse(g.Value)).ToArray();
        Assert.Equal(0, n[0]);
        Assert.Equal(pdf.Length, n[2] + n[3]); // everything after the signature is covered
        var signed = pdf[n[0]..(n[0] + n[1])].Concat(pdf[n[2]..(n[2] + n[3])]).ToArray();
        var hex = text[(n[0] + n[1])..n[2]].Trim('<', '>', ' ', '\n', '\r');
        var der = Convert.FromHexString(hex);
        // The space kept for the signature is padded with zeros after it.
        var length = der[1] == 0x80 ? der.Length : 2 + (der[1] < 0x80 ? der[1] : der[2..(2 + (der[1] & 0x7f))].Aggregate(0, (a, b) => a * 256 + b) + (der[1] & 0x7f));
        var cms = new SignedCms(new System.Security.Cryptography.Pkcs.ContentInfo(signed), detached: true);
        cms.Decode(der[..length]);
        cms.CheckSignature(verifySignatureOnly: true);
        return cms;
    }

    private static PdfSigner TestSigner(Env env) => PdfSigner.Create(null, null, Path.Combine(env.Dir, "keys"));

    [Fact]
    public async Task A_PDF_export_is_signed_the_signature_is_valid_and_History_and_Check_say_so()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        var signer = TestSigner(env);
        var exports = new ExportService(env.Stores, env.Clock, null, env.Reporter, signer);

        var file = await exports.ExportAsync(env.Alice, id, "pdf");
        var cms = VerifySignature(file.Content);
        Assert.Contains(PdfSigner.TestName, cms.SignerInfos[0].Certificate!.Subject);
        Assert.Contains(@"/Reason(Exported from VerbaFlow \(draft\))", Encoding.Latin1.GetString(file.Content));

        var record = Assert.Single(await env.Stores.Exports.ListAsync());
        Assert.Equal(signer.Signature.Describe, record.Signature);
        var ev = (await env.Stores.Audit.ListAsync(id)).Last();
        Assert.Equal("item.exported", ev.Type);
        Assert.Contains(PdfSigner.TestName, ev.Details);

        var check = await exports.CheckAsync(env.Alice, id, new MemoryStream(file.Content));
        Assert.True(check.Matches);
        Assert.Contains("digital signature", check.Message);

        // One changed byte inside the signed part breaks the signature as well as the fingerprint.
        var changed = (byte[])file.Content.Clone();
        changed[200] ^= 1;
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => VerifySignature(changed));
        Assert.False((await exports.CheckAsync(env.Alice, id, new MemoryStream(changed))).Matches);
    }

    [Fact]
    public async Task Word_files_are_not_signed_and_History_does_not_claim_a_signature()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        var exports = new ExportService(env.Stores, env.Clock, null, env.Reporter, TestSigner(env));
        await exports.ExportAsync(env.Alice, id, "docx");
        Assert.Null(Assert.Single(await env.Stores.Exports.ListAsync()).Signature);
        Assert.DoesNotContain("signature", (await env.Stores.Audit.ListAsync(id)).Last().Details);
    }

    [Fact]
    public void The_test_certificate_is_made_once_and_kept()
    {
        using var env = new Env();
        var first = TestSigner(env);
        var second = TestSigner(env);
        Assert.True(first.Signature.TestCertificate);
        Assert.Equal(first.Signature.Thumbprint, second.Signature.Thumbprint);
        Assert.Equal(PdfSigner.TestName, first.Signature.SignedBy);
    }

    [Fact]
    public void An_organisation_certificate_with_a_password_is_used_as_given()
    {
        using var env = new Env();
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=Example Org", rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var path = Path.Combine(env.Dir, "org.pfx");
        File.WriteAllBytes(path, cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, "pw"));
        var signer = PdfSigner.Create(path, "pw", Path.Combine(env.Dir, "keys"));
        Assert.False(signer.Signature.TestCertificate);
        Assert.Equal("Example Org", signer.Signature.SignedBy);
        Assert.False(File.Exists(Path.Combine(env.Dir, "keys", "test-signing.pfx")));
    }
}
