# Legacy edition maintenance

This branch keeps the original WinForms application and its .NET Framework 4.0
project files intact. It is intentionally separate from the modern rewrite.

## Build

- Windows with Visual Studio Build Tools / MSBuild
- .NET Framework 4.0 targeting pack
- Open `PSXDownloadHelper/PSSDownloadHelper.sln` and build Release

A Windows GitHub Actions workflow builds the original solution on pushes and
pull requests. This workflow verifies compilation; it does not certify runtime
behavior on every Windows version.

## Compatibility policy

- Do not change the original project target framework as part of maintenance.
- Keep the existing solution and project layout.
- Fix defects in small, reviewable commits.
- Validate proxy, local-file replacement, CDN mapping and Wi-Fi helper behavior
  on a real Windows test machine before publishing a release.
- Keep the modern edition in `feature/modern-dotnet10`; do not merge modern
  project files into this legacy branch.

## Security note

The legacy proxy has historical networking code. Run it only on trusted
networks, review the bind address before starting it, and do not assume that
HTTPS traffic is decrypted or inspected.
