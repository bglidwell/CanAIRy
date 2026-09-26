# Publishing CanAIRy through WinGet

The proposed package identifier is `BobbyGlidwell.CanAIRy`. The GitHub release
installer is per-user, supports Inno Setup's silent switches, and does not require
administrator privileges.

## First submission

Microsoft requires the initial package manifest to be submitted and reviewed in
the `microsoft/winget-pkgs` repository before updates can be automated.

1. Push to `main` and wait for the automatically versioned GitHub Release.
2. Download Microsoft's WinGet Manifest Creator with `winget install wingetcreate`.
3. Run:

   ```powershell
   wingetcreate new https://github.com/bglidwell/CanAIRy/releases/download/v0.1.0/CanAIRy-0.1.0-win-x64-setup.exe
   ```

4. Use these package values:
   - Package identifier: `BobbyGlidwell.CanAIRy`
   - Publisher: `Bobby Glidwell`
   - Package name: `CanAIRy`
   - License: `Proprietary` until a project-wide license is selected
   - Installer type: `Inno`
   - Scope: `User`
   - Product URL: `https://github.com/bglidwell/CanAIRy`
   - Support URL: `https://github.com/bglidwell/CanAIRy/issues`

5. Let `wingetcreate` open the initial pull request and complete Microsoft's
   validation/review process.

## Later releases

After the first package is accepted, add a repository Actions secret named
`WINGET_CREATE_GITHUB_TOKEN`. It must be a GitHub personal access token capable
of opening the external `microsoft/winget-pkgs` pull request. Run the
**Submit WinGet update** workflow with the new released version. It uses
Microsoft's `wingetcreate` utility to calculate the installer hash and submit the
updated manifest.
