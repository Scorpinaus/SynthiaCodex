# Automated Windows Builds and Releases

The `Windows CI` GitHub Actions workflow tests and packages the same self-contained `win-x64` portable app produced by `scripts\publish-portable.ps1`.

## Download a test build

A successful push to `main` automatically produces a portable build. To package another branch on demand:

1. Open the repository's **Actions** tab on GitHub.
2. Select **Windows CI**.
3. Choose **Run workflow**, select the branch, and start the run.
4. After the `Package portable app` job succeeds, download the `SynthiaCode-<version>-win-x64-build-<run>` artifact from the workflow summary.
5. Extract the downloaded artifact and run `SynthiaCode.App.exe`.

Test-build artifacts are retained for 14 days. Pull-request runs only build and test; they do not publish downloadable packages.

## Publish a GitHub Release

The app project is the authoritative source of the release number. Before creating a release, update these values in `src\SynthiaCode.App\SynthiaCode.App.csproj`:

- `Version`
- `AssemblyVersion`
- `FileVersion`
- `InformationalVersion`

Also update the visible release number in `README.md` and `docs\current-architecture.md`. `ReleaseMetadataTests` verifies that these values remain consistent.

Commit the version changes, allow the `main` build to pass, and then create and push an exact matching `v` tag:

```powershell
git tag v0.1.1
git push origin v0.1.1
```

The tag build performs the full test suite, publishes the portable app, and verifies that `v0.1.1` matches the project's `0.1.1` version before creating the release. A mismatch fails without publishing anything.

The GitHub Release contains:

- `SynthiaCode-<version>-win-x64.zip`
- `SynthiaCode-<version>-win-x64.zip.sha256`

Re-running a successful tag workflow replaces its release assets instead of creating a duplicate release.

## Prereleases

Use a semantic prerelease version such as `0.2.0-beta.1`, with numeric `AssemblyVersion` and `FileVersion` values such as `0.2.0.0`, and push the matching tag:

```powershell
git tag v0.2.0-beta.1
git push origin v0.2.0-beta.1
```

Versions containing a prerelease suffix are automatically marked as prereleases on GitHub.

## Verify a downloaded release

From the directory containing the ZIP and checksum file, calculate the archive hash:

```powershell
Get-FileHash .\SynthiaCode-0.1.1-win-x64.zip -Algorithm SHA256
```

Compare the displayed hash with the value in `SynthiaCode-0.1.1-win-x64.zip.sha256` before extracting the archive.
