Prepare a new YAAT release. Walk through these steps interactively:

## Step 0: Verify release secrets

Confirm that the `LMKIT_LICENSE_KEY` secret is configured on the repo
(`gh secret list --repo leftos/yaat`). If absent, warn the user that the
released installer will run as LM-Kit Community Edition and ask whether
to proceed anyway or stop and configure the secret first
(`gh secret set LMKIT_LICENSE_KEY --repo leftos/yaat`). This is a soft
gate — the build succeeds either way, but users expecting a licensed
build should be told upfront.

## Step 1: Read current version
Read `Directory.Build.props` at the repo root to get the current `<Version>` value.

## Step 2: Find previous release
Run `git tag --sort=-v:refname | head -5` to find existing release tags.

## Step 3: Ask for new version
Suggest the next version based on the current one (e.g., `0.1.0-alpha` → `0.2.0-alpha`). Ask the user what the new version should be.

## Step 4: Generate changelog
Run `bash .github/scripts/generate-changelog.sh <previous-tag> HEAD` to generate the grouped changelog since the last release. If there is no previous tag, run with `--` as the first argument.

## Step 5: Generate highlights
From the changelog, write 3-4 concise bullet points highlighting the most notable changes for users. Be specific and factual — no marketing language.

## Step 6: Present draft release notes
Show the user the full draft:

```
## Highlights
- [your generated bullets]

## Changelog
[output from generate-changelog.sh]
```

Ask the user to review and edit. Apply any requested changes.

## Step 7: Ship it (after user approval)
Once the user approves:
1. Update `<Version>` in `Directory.Build.props` to the new version
2. Update `docs/architecture.md` if any new files were added
3. Stage and commit: `release: v{version}`
4. Create tag: `git tag v{version}`
5. Push commit and tag: `git push origin main --tags`

This triggers the `release.yml` GitHub Actions workflow which builds all platforms, creates the Velopack installer, and publishes the GitHub Release with the changelog and highlights.
