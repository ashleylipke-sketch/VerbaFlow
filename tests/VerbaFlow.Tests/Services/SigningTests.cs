using System.Security.Cryptography.Pkcs;
using System.Text;
using System.Text.RegularExpressions;
using VerbaFlow.Infrastructure.Export;
using VerbaFlow.Infrastructure.Services;

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
        // Everything after the signature is covered, apart from a long-term proof update added after the signed file.
        var end = n[2] + n[3];
        Assert.True(end == pdf.Length || text[..end].TrimEnd().EndsWith("%%EOF", StringComparison.Ordinal), "the signature does not reach the end of its revision");
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
    public async Task With_signing_switched_off_a_Word_file_is_not_signed_and_History_does_not_claim_a_signature()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        var exports = new ExportService(env.Stores, env.Clock, null, env.Reporter, null);
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

public class ApprovalBoxTests
{
    private static PdfSigner TestSigner(Env env) => PdfSigner.Create(null, null, Path.Combine(env.Dir, "keys"));

    /// <summary>The rectangle of the signature field, in PDF points.</summary>
    private static double[] SignatureRect(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var field = Regex.Match(text, @"/FT\s*/Sig", RegexOptions.Singleline);
        Assert.True(field.Success, "no signature field");
        var start = text.LastIndexOf("obj", field.Index, StringComparison.Ordinal);
        var end = text.IndexOf("endobj", field.Index, StringComparison.Ordinal);
        var rect = Regex.Match(text[start..end], @"/Rect\s*\[\s*([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s*\]");
        Assert.True(rect.Success, "no /Rect on the signature field");
        return rect.Groups.Values.Skip(1).Select(g => double.Parse(g.Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    }

    [Fact]
    public async Task An_approved_meeting_PDF_shows_the_approval_box_with_a_visible_valid_signature()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        await env.Meeting.ApproveAsync(env.Alice, id);
        var exports = new ExportService(env.Stores, env.Clock, null, env.Reporter, TestSigner(env));
        var file = await exports.ExportAsync(env.Alice, id, "pdf");
        SigningTests.VerifySignature(file.Content);
        var r = SignatureRect(file.Content);
        Assert.True(r[2] - r[0] > 100 && r[3] - r[1] > 5, $"signature box too small: {string.Join(' ', r)}");
        Assert.DoesNotContain("/VerbaFlowApprovalBox", Encoding.Latin1.GetString(file.Content));
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "vf-approved-minutes.pdf"), file.Content);
    }

    [Fact]
    public async Task An_approved_letter_PDF_shows_the_box_and_a_draft_has_none()
    {
        using var env = new Env();
        env.Speech.Heard = "Please call me back full stop";
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, env.Dictation());
        await env.Processing.ProcessAsync(id);
        var exports = new ExportService(env.Stores, env.Clock, null, env.Reporter, TestSigner(env));

        var draft = await exports.ExportAsync(env.Alice, id, "pdf");
        SigningTests.VerifySignature(draft.Content);
        var d = SignatureRect(draft.Content);
        Assert.True(d[2] - d[0] == 0 && d[3] - d[1] == 0, "a draft's signature should be invisible");

        await env.Meeting.ApproveAsync(env.Alice, id);
        var file = await exports.ExportAsync(env.Alice, id, "pdf");
        SigningTests.VerifySignature(file.Content);
        var r = SignatureRect(file.Content);
        Assert.True(r[2] - r[0] > 100, "signature box missing on the approved letter");
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "vf-approved-letter.pdf"), file.Content);
    }

    [Fact]
    public async Task On_a_PDF_letterhead_the_box_and_signature_land_on_the_letterhead_sized_page()
    {
        using var env = new Env();
        var letterheads = new LetterheadService(env.Stores, env.Clock);
        await letterheads.UploadAsync(env.Carol, "pdf", new MemoryStream(LetterheadTests.PdfLetterhead()), "acme.pdf");
        await new MinutesTemplateService(env.Stores, env.Clock).CreateAsync(env.Carol, MinutesTemplateTests.Input(onLetterhead: true));
        var exports = new ExportService(env.Stores, env.Clock, null, env.Reporter, TestSigner(env));

        env.Speech.Heard = "Dear Mr Smith comma new paragraph thank you for your letter full stop";
        var letter = await env.Meeting.CreateRecordingAsync(env.Alice, env.Dictation());
        await env.Processing.ProcessAsync(letter);
        await env.Meeting.ApproveAsync(env.Alice, letter);
        var meeting = await env.ProcessedRecordingAsync();
        await env.Meeting.ApproveAsync(env.Alice, meeting);

        foreach (var (id, name) in new[] { (letter, "letter"), (meeting, "minutes") })
        {
            var file = await exports.ExportAsync(env.Alice, id, "pdf");
            SigningTests.VerifySignature(file.Content);
            var r = SignatureRect(file.Content);
            Assert.True(r[0] > 0 && r[2] < 612 && r[1] > 0 && r[3] < 792 - 120, $"{name}: box off the page or under the letterhead band: {string.Join(' ', r)}");
            File.WriteAllBytes(Path.Combine(Path.GetTempPath(), $"vf-letterhead-{name}.pdf"), file.Content);
        }
    }
}
