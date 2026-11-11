# Signed PDF exports

Every PDF that VerbaFlow exports (minutes, transcripts, dictations and letters, whether downloaded, previewed, shared or emailed) carries a digital signature. A PDF reader such as Adobe Acrobat Reader can then show:

- that the file has not been changed since VerbaFlow made it (change one character and the signature shows as broken), and
- who signed it (the certificate's name).

The signature is invisible on the page. In Adobe Reader, open the **Signatures** panel (the pen icon on the left) to see it. Word files are not signed.

## The test certificate (what you have now)

Until the organisation has its own certificate, VerbaFlow makes a **test certificate** the first time it starts, named "VerbaFlow test signature (not trusted)", and keeps it at `src\VerbaFlow.Api\data\keys\test-signing.pfx` on this computer. It is never put on GitHub.

With the test certificate, Adobe Reader says the signature is **valid but the signer's identity is unknown** (a yellow warning). That is expected: nobody outside VerbaFlow knows this certificate. The "document has not been modified" part still works.

## The signing time (trusted timestamp, on by default)

Each signature now also carries a **timestamp** from DigiCert's free public timestamp service (`http://timestamp.digicert.com`). It is an independent confirmation of when the file was signed, so the time cannot simply come from a wrong computer clock, and readers can still check the signature after the certificate has expired. Only a fingerprint of the signature is sent to DigiCert: never the document, its name or anything in it.

- History's `signature` ends with "time confirmed by timestamp.digicert.com".
- If the service cannot be reached, the file is still signed (with the computer's own time) and History says "time not confirmed (the timestamp service could not be reached) (VF-…)".
- Another service: `dotnet user-secrets set "Signing:TimestampUrl" "http://…"`. None at all: `dotnet user-secrets set "Signing:TimestampUrl" ""`.

## Getting a trusted signature (so Adobe shows a known signer)

Adobe shows a green tick only for certificates from a company on **Adobe's Approved Trust List (AATL)**; Microsoft Word and Windows use Microsoft's own list. Such companies include GlobalSign, DigiCert, Entrust and SSL.com (check Adobe's AATL list for the current members). You buy an **organisation document-signing certificate** in the organisation's name. Expect identity checks of the organisation (a few days) and a yearly cost (prices not checked; roughly a few hundred pounds a year).

These certificates are no longer handed out as a simple file: the secret key must live in protected hardware. For VerbaFlow (on your computer now, online later) the right place is **Azure Key Vault Premium**, which keeps the key in Microsoft's hardware security modules. VerbaFlow never sees the key: it sends only a fingerprint of each PDF to the vault to be signed. **Before you buy, ask the seller: "Can you issue an AATL document signing certificate for a key generated in Azure Key Vault Premium (HSM)?"** Not every seller accepts it, and this has not been tried with a real seller.

### Steps (once the seller says yes)

1. In the Azure portal: **Create a resource**, **Key Vault**. Resource group `verbaflow`, a name such as `verbaflow-signing`, your region, **Pricing tier: Premium**, permission model **Azure role-based access control**. Create.
2. Open the vault, **Access control (IAM)**, **Add role assignment**: give yourself **Key Vault Administrator**. Then give the app registration **VerbaFlow** (and later the online app's managed identity) both **Key Vault Certificate User** and **Key Vault Crypto User**.
3. In the vault, **Certificates**, **Generate/Import**: method **Generate**, name `document-signing`, type of Certificate Authority **Certificate issued by a non-integrated CA**, subject as the seller asks (for example `CN=Your Organisation Ltd, O=Your Organisation Ltd, C=GB`), **Advanced policy configuration**: key type **RSA-HSM**, key size **3072**, **Exportable private key: No**. Create.
4. Open the new certificate, **Certificate Operation**, **Download CSR**, and send that file to the seller.
5. When the seller sends the certificate back, open the same **Certificate Operation** and choose **Merge Signed Request** with their file.
6. On the computer, from `src\VerbaFlow.Api` (your `Entra:ClientSecret` is used to reach the vault):
   ```
   dotnet user-secrets set "Signing:KeyVaultUrl" "https://verbaflow-signing.vault.azure.net/"
   dotnet user-secrets set "Signing:KeyVaultCertificate" "document-signing"
   ```
7. Restart VerbaFlow. The **Signing:** line at start-up should say the certificate's name, "key in Azure Key Vault (…)", and "1 more certificates sent with it" (the seller's in-between certificate, fetched from the web once at start-up). If the seller gave a chain file instead (.p7b, .pem, .cer): `dotnet user-secrets set "Signing:ChainPath" "C:\path\to\chain.p7b"`.
8. Export an approved PDF and open it in Adobe Reader: the signature panel should show your organisation's name with a green tick, and the approval box says "Digitally signed by <your organisation>."

Online, the app uses its managed identity instead of the secret (switch it on under the Container App's **Identity** and give it the two roles from step 2; `deploy/azure-setup.sh` does not do this yet).

### A certificate as a .pfx file

If you have a certificate as a `.pfx` file (for example a cheaper one that is not on Adobe's list, or an older one), put it somewhere safe outside the VerbaFlow folder, then from `src\VerbaFlow.Api`:

```
dotnet user-secrets set "Signing:CertificatePath" "C:\path\to\certificate.pfx"
dotnet user-secrets set "Signing:CertificatePassword" "the password"
```

Type the password into the command yourself; do not paste it into a chat. Certificates between yours and the root that are inside the .pfx are sent with each signature. Key Vault wins when both are set.

To switch signing off: `dotnet user-secrets set "Signing:Enabled" "false"`.

## The approval box on approved PDFs

An approved PDF shows a small green box: **APPROVED**, who approved it and when, and a last line where the digital signature sits, for example "Digitally signed by VerbaFlow (test certificate, not yet trusted)." On minutes it is under the details table; on a letter, Word document or email it is after the text and the sign-off. In Adobe Reader, clicking that line should open the signature details (not yet checked). Drafts and superseded copies have no box (their signature stays invisible). Word files have no box and no signature.

## Where it shows in VerbaFlow

- History: each `item.exported` of a PDF has `signature` (the certificate's name, the first 8 characters of its fingerprint and whether the time was confirmed), or `not signed (VF-…)` if signing failed. A failed signing never stops the export; the file goes out unsigned and the problem goes to Support: errors.
- **Check an exported file** adds "It carries VerbaFlow's digital signature." for a signed PDF.

## Not built yet

- Long-term validation (putting the certificates' revocation answers into the PDF, so it can be checked many years later without the internet).
- Signing with a key on a USB token or a seller's own cloud signing service.
- Signing Word files.
