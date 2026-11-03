# Signed PDF exports

Every PDF that VerbaFlow exports (minutes, transcripts, dictations and letters, whether downloaded, previewed, shared or emailed) carries a digital signature. A PDF reader such as Adobe Acrobat Reader can then show:

- that the file has not been changed since VerbaFlow made it (change one character and the signature shows as broken), and
- who signed it (the certificate's name).

The signature is invisible on the page. In Adobe Reader, open the **Signatures** panel (the pen icon on the left) to see it. Word files are not signed.

## The test certificate (what you have now)

Until the organisation has its own certificate, VerbaFlow makes a **test certificate** the first time it starts, named "VerbaFlow test signature (not trusted)", and keeps it at `src\VerbaFlow.Api\data\keys\test-signing.pfx` on this computer. It is never put on GitHub.

With the test certificate, Adobe Reader says the signature is **valid but the signer's identity is unknown** (a yellow warning). That is expected: nobody outside VerbaFlow knows this certificate. The "document has not been modified" part still works.

## Using the organisation's own certificate

When you buy a document-signing certificate (from a provider on Adobe's trusted list, so Adobe shows a green tick), you get a `.pfx` file and its password. Put the file somewhere safe on the computer (not in the VerbaFlow folder that goes to GitHub), then from the `src\VerbaFlow.Api` folder run:

```
dotnet user-secrets set "Signing:CertificatePath" "C:\path\to\certificate.pfx"
dotnet user-secrets set "Signing:CertificatePassword" "the password"
```

Type the password into the command yourself; do not paste it into a chat. Restart VerbaFlow (`run.cmd`). The start-up log line **Signing:** says which certificate is used.

To switch signing off: `dotnet user-secrets set "Signing:Enabled" "false"`.

## Where it shows in VerbaFlow

- History: each `item.exported` of a PDF has `signature` (the certificate's name and the first 8 characters of its fingerprint), or `not signed (VF-…)` if signing failed. A failed signing never stops the export; the file goes out unsigned and the problem goes to Support: errors.
- **Check an exported file** adds "It carries VerbaFlow's digital signature." for a signed PDF.

## Not built yet

- Keeping the certificate in Azure Key Vault (decision 13): planned for when VerbaFlow runs online.
- A trusted timestamp, which keeps the signature checkable after the certificate expires.
- A visible signature box on the page.
