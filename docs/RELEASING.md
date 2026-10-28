# Releases

1. Update `Directory.Build.props`, `packaging/arch/PKGBUILD` and the package filenames in the installation guides.
2. Add `.github/release-notes/vX.Y.Z.md` with the changes and installation instructions. Without this file, GitHub generates the notes.
3. Commit and push to `main`, then tag that commit:

   ```sh
   git tag vX.Y.Z
   git push origin vX.Y.Z
   ```

The Release workflow checks the version, runs the existing build and sandbox workflows, verifies the packages and publishes seven assets with SHA-256 checksums. Versions below 1.0 are marked as prereleases. Publication uses the job's `GITHUB_TOKEN`; no personal token is needed.

If publication fails after creating a draft, fix the cause and rerun the failed jobs. The workflow can resume a draft but refuses to replace a published release. For a manual workflow run, select the version tag rather than a branch.
