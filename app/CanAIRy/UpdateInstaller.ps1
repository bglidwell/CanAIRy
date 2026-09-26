param([Parameter(Mandatory)][string]$RequestPath)
$ErrorActionPreference = 'Stop'
$request = Get-Content -LiteralPath $RequestPath -Raw | ConvertFrom-Json
$directory = Split-Path $RequestPath
$log = Join-Path $directory 'install.log'
try {
    $parent = Get-Process -Id $request.ProcessId -ErrorAction SilentlyContinue
    if ($parent -and -not $parent.WaitForExit(90000)) { throw 'CanAIRy did not exit; update cancelled.' }
    $otherCopies = Get-Process -Name CanAIRy -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $request.Executable }
    if ($otherCopies) { throw 'Another copy of CanAIRy is running; update cancelled.' }
    if ((Get-FileHash -LiteralPath $request.Package -Algorithm SHA256).Hash -ne $request.Sha256) {
        throw 'Installer checksum changed; update cancelled.'
    }
    if ($request.Kind -eq 'msi') {
        $installer = Join-Path $env:SystemRoot 'System32/msiexec.exe'
        $arguments = @('/i', ('"' + $request.Package + '"'), '/qn', '/norestart', '/L*v', ('"' + $log + '"'))
    } elseif ($request.Kind -eq 'exe') {
        $installer = $request.Package
        $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOCLOSEAPPLICATIONS',
            ('/DIR="' + (Split-Path $request.Executable) + '"'), ('/LOG="' + $log + '"'))
    } else { throw 'Unknown installation type.' }
    $result = Start-Process -FilePath $installer -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
    if ($result.ExitCode -notin @(0, 3010)) { throw "Installer exited with code $($result.ExitCode). See $log" }
    Set-Content -LiteralPath (Join-Path $directory 'result.txt') -Value 'Update installed successfully.'
} catch {
    Set-Content -LiteralPath (Join-Path $directory 'result.txt') -Value $_.Exception.Message
} finally {
    # A failed installer is also followed by a restart, so the previous build can
    # show its activity log. It will not automatically retry the same version.
    if (-not (Get-Process -Id $request.ProcessId -ErrorAction SilentlyContinue) -and
        (Test-Path -LiteralPath $request.Executable)) {
        Start-Process -FilePath $request.Executable -ArgumentList '--tray' -WindowStyle Hidden
    }
}
