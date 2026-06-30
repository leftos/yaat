# macOS code signing & notarization setup

The release pipeline signs and notarizes the macOS `.pkg` and `.app` so they
launch without a Gatekeeper warning and auto-update silently. This is a
**one-time setup** of an Apple Developer account's certificates plus eight
GitHub Actions secrets. Until the secrets exist, `release.yml` still builds a
working macOS package — just unsigned, with the old right-click-to-open caveat.

Most of the certificate steps need a Mac (Keychain Access). Adding the secrets
can be done from any machine with the `gh` CLI.

## What the pipeline expects

`release.yml`'s `package-macos` job reads these secrets. If
`MACOS_DEVID_APP_CERT_P12_BASE64` is empty, the whole signing path is skipped.

| Secret | What it is |
|--------|------------|
| `MACOS_DEVID_APP_CERT_P12_BASE64` | Developer ID **Application** cert + private key, as a base64-encoded `.p12` |
| `MACOS_DEVID_INSTALLER_CERT_P12_BASE64` | Developer ID **Installer** cert + private key, as a base64-encoded `.p12` |
| `MACOS_DEVID_CERT_PASSWORD` | The password protecting both `.p12` files (use the same one for both) |
| `MACOS_SIGN_APP_IDENTITY` | The Application cert's name, e.g. `Developer ID Application: Jane Doe (AB12CD34EF)` |
| `MACOS_SIGN_INSTALL_IDENTITY` | The Installer cert's name, e.g. `Developer ID Installer: Jane Doe (AB12CD34EF)` |
| `MACOS_NOTARY_API_KEY_P8_BASE64` | App Store Connect API key (`.p8`), base64-encoded |
| `MACOS_NOTARY_API_KEY_ID` | The API key's Key ID (10 chars, e.g. `2X9R4HXF34`) |
| `MACOS_NOTARY_API_ISSUER_ID` | The API key's Issuer ID (a UUID) |

The build keychain password is generated fresh on every run, so it is not a
secret.

The standalone **`yaat-crc-config`** tool (`.github/workflows/yaat-crc-config.yml`)
reuses the same Developer ID **Application** certificate and notary key to sign,
notarize, and staple its macOS `.dmg`. A `.dmg` is signed with the Application
identity, so the tool does **not** touch the Installer cert or identity (those
are only for the client's `.pkg`) — it reads six of the eight secrets:
`MACOS_DEVID_APP_CERT_P12_BASE64`, `MACOS_DEVID_CERT_PASSWORD`,
`MACOS_SIGN_APP_IDENTITY`, and the three `MACOS_NOTARY_API_*` values.

## Step 1 — Create the two Developer ID certificates (on a Mac)

You need **two** certificates: one signs the `.app`, the other signs the
`.pkg`. Both come from the same Apple Developer account.

1. Open **Keychain Access** → menu **Certificate Assistant → Request a
   Certificate From a Certificate Authority**.
   - User Email: your Apple ID email. Common Name: anything (e.g. "YAAT signing").
   - Choose **Saved to disk**. This produces a `CertificateSigningRequest.certSigningRequest` (CSR).
2. Go to <https://developer.apple.com/account/resources/certificates/list> →
   **+** (Create a Certificate).
   - Create one of type **Developer ID Application**, upload the CSR, download the `.cer`.
   - Repeat **+** for type **Developer ID Installer**, upload the *same* CSR, download that `.cer`.
3. Double-click each downloaded `.cer` to import it into Keychain Access (login keychain).

> If the certificate types are greyed out, your account may need the Account
> Holder to create them, or you may have hit the per-account Developer ID cert
> limit (revoke an unused one to free a slot).

## Step 2 — Export each certificate as a `.p12`

In Keychain Access, **login** keychain, **My Certificates** category:

1. Expand the **"Developer ID Application: …"** row so both the certificate and
   its private key are selected, right-click → **Export 2 items…** → format
   **Personal Information Exchange (.p12)** → save as `devid_app.p12`, set a
   password (remember it — this becomes `MACOS_DEVID_CERT_PASSWORD`).
2. Do the same for **"Developer ID Installer: …"** → `devid_installer.p12`,
   using the **same** password.

Capture the exact identity strings while you're here:

```bash
security find-identity -v -p codesigning   # shows the Application identity
security find-identity -v                   # shows both, including the Installer
```

Copy the quoted names (e.g. `Developer ID Application: Jane Doe (AB12CD34EF)`)
into `MACOS_SIGN_APP_IDENTITY` / `MACOS_SIGN_INSTALL_IDENTITY`.

## Step 3 — Create an App Store Connect API key for notarization

A Team key is more robust than an app-specific password: it never expires with
your Apple ID password and is revocable on its own.

1. Go to <https://appstoreconnect.apple.com/access/integrations/api> →
   **Team Keys** → **+**.
2. Name it (e.g. "YAAT notarization"), give it the **Developer** access role,
   and **Generate**.
3. **Download** the `AuthKey_XXXXXXXXXX.p8` — Apple lets you download it **once**.
4. Note the **Key ID** (the `XXXXXXXXXX` in the filename) and the **Issuer ID**
   shown at the top of the Keys page (a UUID).

## Step 4 — Base64-encode the three files

```bash
base64 -i devid_app.p12        -o devid_app.p12.b64
base64 -i devid_installer.p12  -o devid_installer.p12.b64
base64 -i AuthKey_XXXXXXXXXX.p8 -o notary_key.p8.b64
```

## Step 5 — Add the secrets to GitHub

From a checkout of `leftos/yaat`, with the `gh` CLI authenticated:

```bash
gh secret set MACOS_DEVID_APP_CERT_P12_BASE64       < devid_app.p12.b64
gh secret set MACOS_DEVID_INSTALLER_CERT_P12_BASE64 < devid_installer.p12.b64
gh secret set MACOS_NOTARY_API_KEY_P8_BASE64        < notary_key.p8.b64

gh secret set MACOS_DEVID_CERT_PASSWORD             # paste the .p12 password
gh secret set MACOS_SIGN_APP_IDENTITY               # paste "Developer ID Application: …"
gh secret set MACOS_SIGN_INSTALL_IDENTITY           # paste "Developer ID Installer: …"
gh secret set MACOS_NOTARY_API_KEY_ID               # paste the 10-char Key ID
gh secret set MACOS_NOTARY_API_ISSUER_ID            # paste the Issuer UUID
```

Then **delete the local `.p12`, `.p8`, and `.b64` files** — they are signing
credentials.

```bash
rm devid_app.p12 devid_installer.p12 AuthKey_*.p8 *.b64
```

## Step 6 — Verify

Cut a release as usual (the `/prepare-release` skill → tag push). In the
`package-macos` job log you should see, from `vpk pack`:

- `Code signing application bundle recursively (with --deep)…`
- `Notarization completed successfully` (the upload to Apple usually takes a
  few minutes)
- `Verifying signature/notarization … using spctl` for both the app and the
  installer

To sanity-check a downloaded artifact on a Mac:

```bash
spctl --assess -vvv --type install YaatClient-<ver>-osx-Setup.pkg   # → "accepted, source=Notarized Developer ID"
xcrun stapler validate YaatClient-<ver>-osx-Setup.pkg               # → "The validate action worked!"
```

## Maintenance notes

- **Certificates expire after 5 years.** When the Developer ID certs are
  renewed, re-export the `.p12`s and update the two cert secrets (and the
  identity strings if the team suffix changes).
- **The custom `Info.plist` mirrors Velopack's generated plist.** If a `vpk`
  upgrade changes the keys Velopack emits, re-check `build/macos/Info.plist.template`
  against `Velopack.Packaging.Unix/PlistWriter.cs`.
- **Entitlements** live in `build/macos/Yaat.Client.entitlements`. The four
  `cs.*` keys are required for any notarized .NET app; `device.audio-input` is
  for `AudioCaptureService`'s push-to-talk microphone capture.
