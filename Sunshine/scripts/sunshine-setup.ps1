# Sunshine Setup Script
# This script orchestrates the installation and uninstallation of Sunshine
# Usage: sunshine-setup.ps1 -Action [install|uninstall] [-Silent]

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet(
            "install",
            "uninstall"
    )]
    [string]$Action,

    [Parameter(Mandatory=$false)]
    [switch]$Silent
)

# Constants
$DocsUrl = "https://docs.lizardbyte.dev/projects/sunshine"

# Set preference variables for output streams
$InformationPreference = 'Continue'

# Function to write output to both console (with color/stream) and log file (without color)
function Write-LogMessage {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '',
        Justification='Write-Host is required for colored output')]
    param(
        [Parameter(Mandatory=$true)]
        [AllowEmptyString()]
        [string]$Message,

        [Parameter(Mandatory=$false)]
        [ValidateSet(
                'Debug',
                'Error',
                'Information',
                'Step',
                'Success',
                'Verbose',
                'Warning'
        )]
        [string]$Level = 'Information',

        [Parameter(Mandatory=$false)]
        [ValidateSet(
                'Black',
                'Blue',
                'Cyan',
                'DarkGray',
                'Gray',
                'Green',
                'Magenta',
                'Red',
                'White',
                'Yellow'
        )]
        [string]$Color = $null,

        [Parameter(Mandatory=$false)]
        [switch]$NoTimestamp,

        [Parameter(Mandatory=$false)]
        [switch]$NoLogFile
    )

    # Map levels to colors and output streams
    $levelConfig = @{
        'Debug' = @{ DefaultColor = 'DarkGray'; Stream = 'Debug'; Emoji = ''; LogLevel = 'DEBUG' }
        'Error' = @{ DefaultColor = 'Red'; Stream = 'Error'; Emoji = '✗'; LogLevel = 'ERROR' }
        'Information' = @{ DefaultColor = $null; Stream = 'Host'; Emoji = ''; LogLevel = 'INFO' }
        'Step' = @{ DefaultColor = 'Cyan'; Stream = 'Host'; Emoji = '==>'; LogLevel = 'INFO' }
        'Success' = @{ DefaultColor = 'Green'; Stream = 'Host'; Emoji = '✓'; LogLevel = 'INFO' }
        'Verbose' = @{ DefaultColor = 'DarkGray'; Stream = 'Verbose'; Emoji = ''; LogLevel = 'VERBOSE' }
        'Warning' = @{ DefaultColor = 'Yellow'; Stream = 'Warning'; Emoji = '⚠'; LogLevel = 'WARN' }
    }

    $config = $levelConfig[$Level]

    # Use custom color if specified, otherwise use default color for the level
    $displayColor = if ($Color) { $Color } else { $config.DefaultColor }

    # Write to appropriate output stream with color
    switch ($config.Stream) {
        'Debug' {
            Write-Debug $Message
        }
        'Error' {
            Write-Error $Message
        }
        'Host' {
            if ($null -ne $displayColor) {
                Write-Host "$($config.Emoji) $Message" -ForegroundColor $displayColor
            } else {
                Write-Host "$($config.Emoji) $Message"
            }
        }
        'Information' {
            Write-Information $Message
        }
        'Verbose' {
            Write-Verbose $Message
        }
        'Warning' {
            Write-Warning $Message
        }
        default {
            Write-Information $Message
        }
    }

    # Write to log file without color codes (only if LogPath exists and not disabled)
    if ($script:LogPath -and -not $NoLogFile) {
        try {
            # Format log entry with timestamp and level
            if ($NoTimestamp) {
                $logEntry = $Message
            } else {
                $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
                $logEntry = "[$timestamp] [$($config.LogLevel)] $Message"
            }

            $logEntry | Out-File `
                -FilePath $script:LogPath `
                -Append `
                -Encoding UTF8
        } catch {
            # Avoid infinite recursion - use Write-Verbose directly
            Write-Verbose "Could not write to log file: $($_.Exception.Message)"
        }
    }
}

# Function to print a separator bar
function Write-Bar {
    param(
        [string]$Level = 'Information',
        [int]$Length = 63,
        [string]$Color = $null,
        [switch]$NoTimestamp
    )
    $bar = "=" * $Length
    if ($Color) {
        Write-LogMessage -Message $bar -Level $Level -Color $Color -NoTimestamp:$NoTimestamp
    } else {
        Write-LogMessage -Message $bar -Level $Level -NoTimestamp:$NoTimestamp
    }
}

# Function to print text framed by bars
function Write-FramedText {
    param(
        [string]$Message,
        [string]$Level = 'Information',
        [int]$BarLength = 63,
        [string]$Color = $null,
        [switch]$NoTimestamp,
        [switch]$NoCenter
    )

    # Center the message if NoCenter is not specified
    $displayMessage = $Message
    if (-not $NoCenter) {
        $messageLength = $Message.Trim().Length

        if ($messageLength -lt $BarLength) {
            $totalPadding = $BarLength - $messageLength
            $leftPadding = [Math]::Floor($totalPadding / 2)
            $displayMessage = (' ' * $leftPadding) + $Message.Trim()
        } else {
            $displayMessage = $Message.Trim()
        }
    }

    if ($Color) {
        Write-Bar -Level $Level -Length $BarLength -Color $Color -NoTimestamp:$NoTimestamp
        Write-LogMessage -Message $displayMessage -Level $Level -Color $Color -NoTimestamp:$NoTimestamp
        Write-Bar -Level $Level -Length $BarLength -Color $Color -NoTimestamp:$NoTimestamp
    } else {
        Write-Bar -Level $Level -Length $BarLength -NoTimestamp:$NoTimestamp
        Write-LogMessage -Message $displayMessage -Level $Level -NoTimestamp:$NoTimestamp
        Write-Bar -Level $Level -Length $BarLength -NoTimestamp:$NoTimestamp
    }
}

# Function to write to log file (helper function)
function Write-LogFile {
    param(
        [string[]]$Lines
    )
    if ($script:LogPath) {
        try {
            foreach ($line in $Lines) {
                $line | Out-File `
                    -FilePath $script:LogPath `
                    -Append `
                    -Encoding UTF8
            }
        } catch {
            Write-Warning "Failed to write to log file: $($_.Exception.Message)"
        }
    }
}

# If Action is not provided, prompt the user
if (-not $Action) {
    Write-Information ""
    Write-FramedText -Message "🔅 Sunshine Setup Script" -Level "Information" -Color "Cyan"
    Write-Information ""
    Write-LogMessage -Message "Please select an action:" -Level "Information" -Color "Yellow"
    Write-LogMessage -Message "  1. Install Sunshine" -Level "Information" -Color "Green"
    Write-LogMessage -Message "  2. Uninstall Sunshine" -Level "Information" -Color "Red"
    Write-Information ""

    $validChoice = $false
    while (-not $validChoice) {
        $choice = Read-Host "Enter your choice (1 or 2)"

        switch ($choice) {
            "1" {
                $Action = "install"
                $validChoice = $true
            }
            "2" {
                $Action = "uninstall"
                $validChoice = $true
            }
            default {
                Write-Warning "Invalid choice. Please select 1 or 2."
                Write-Information ""
            }
        }
    }
    Write-Information ""
}

# Check if running as administrator, if not, relaunch with elevation
$currentPrincipal = New-Object `
        Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
$isAdmin = $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Warning "This script requires administrator privileges. Relaunching with elevation..."

    # Build the argument list for the elevated process
    $arguments = "-ExecutionPolicy Bypass -File `"$($MyInvocation.MyCommand.Path)`" -Action $Action"
    if ($Silent) {
        $arguments += " -Silent"
    }

    try {
        # Relaunch the script with elevation
        Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments -Wait
        exit $LASTEXITCODE
    } catch {
        Write-Error "Failed to elevate privileges: $($_.Exception.Message)"
        exit 1
    }
}

# Get the script directory and root directory
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RootDir = Split-Path -Parent $ScriptDir

# Set up transcript logging
$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$logDir = Join-Path $env:TEMP "Sunshine\logs\$Action"
$LogPath = Join-Path $logDir "${timestamp}.log"

# Ensure the log directory exists
if (-not (Test-Path $logDir)) {
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
}

# Store LogPath in script scope for logging functions
$script:LogPath = $LogPath

. (Join-Path $ScriptDir "sunshine-setup-process.ps1")

# Main script logic
Write-Information ""

if ($Action -eq "install") {
    Write-FramedText `
        -Message "🔅 Sunshine Installation Script" `
        -Level "Information" `
        -Color "Yellow"
    Write-Information ""

    $totalSteps = 6
    $currentStep = 0

    # Reset permissions on the install directory
    $currentStep++
    Write-Progress `
        -Activity "Installing Sunshine" `
        -Status "Resetting permissions on installation directory" `
        -PercentComplete (($currentStep / $totalSteps) * 100)
    Write-LogMessage -Message "🔐 Resetting permissions on installation directory" -Level "Step"
    try {
        Write-LogMessage -Message "Executing: icacls.exe `"$RootDir`" /reset" -Level "Information"

        # Capture output to suppress it from console but log it
        $stdoutFile = [System.IO.Path]::GetTempFileName()
        $stderrFile = [System.IO.Path]::GetTempFileName()

        try {
            $icaclsProcess = Start-Process `
                -FilePath "icacls.exe" `
                -ArgumentList "`"$RootDir`" /reset" `
                -Wait `
                -PassThru `
                -NoNewWindow `
                -RedirectStandardOutput $stdoutFile `
                -RedirectStandardError $stderrFile

            # Log and display the output
            if (Test-Path $stdoutFile) {
                $output = Get-Content $stdoutFile -Raw -ErrorAction SilentlyContinue
                if ($output) {
                    # Display output with indentation
                    $output -split "`r?`n" | ForEach-Object {
                        if ($_.Trim()) {
                            Write-LogMessage -Message "  $_" -Level "Information" -Color "DarkGray"
                        }
                    }
                }
            }
            if (Test-Path $stderrFile) {
                $errors = Get-Content $stderrFile -Raw -ErrorAction SilentlyContinue
                if ($errors) {
                    # Display errors with indentation
                    $errors -split "`r?`n" | ForEach-Object {
                        if ($_.Trim()) {
                            Write-LogMessage -Message "  $_" -Level "Warning"
                        }
                    }
                }
            }

            if ($icaclsProcess.ExitCode -eq 0) {
                Write-LogMessage -Message "  ✓ Done" -Level "Success"
            } else {
                Write-LogMessage -Message "  ⚠ Exit code $($icaclsProcess.ExitCode)" -Level "Warning"
            }
        } finally {
            # Clean up temp files
            if (Test-Path $stdoutFile) {
                Remove-Item $stdoutFile -Force -ErrorAction SilentlyContinue
            }
            if (Test-Path $stderrFile) {
                Remove-Item $stderrFile -Force -ErrorAction SilentlyContinue
            }
        }
    } catch {
        Write-LogMessage -Message "  ⚠ Failed to reset permissions: $($_.Exception.Message)" -Level "Warning"
    }
    Write-Information ""

    # 1. Update PATH (add)
    $currentStep++
    Write-Progress `
        -Activity "Installing Sunshine" `
        -Status "Updating system PATH" `
        -PercentComplete (($currentStep / $totalSteps) * 100)
    $updatePathScript = Join-Path $RootDir "scripts\update-path.bat"
    Invoke-ScriptIfExist `
        -ScriptPath $updatePathScript `
        -Arguments "add" `
        -Description "Adding Sunshine directories to PATH" `
        -Emoji "📁"
    Write-Information ""

    # 2. Migrate configuration
    $currentStep++
    Write-Progress `
        -Activity "Installing Sunshine" `
        -Status "Migrating configuration" `
        -PercentComplete (($currentStep / $totalSteps) * 100)
    $migrateConfigScript = Join-Path $RootDir "scripts\migrate-config.bat"
    Invoke-ScriptIfExist `
        -ScriptPath $migrateConfigScript `
        -Description "Migrating configuration files" `
        -Emoji "⚙️"
    Write-Information ""

    # 3. Add firewall rules
    $currentStep++
    Write-Progress `
        -Activity "Installing Sunshine" `
        -Status "Configuring firewall" `
        -PercentComplete (($currentStep / $totalSteps) * 100)
    $addFirewallScript = Join-Path $RootDir "scripts\add-firewall-rule.bat"
    Invoke-ScriptIfExist `
        -ScriptPath $addFirewallScript `
        -Description "Adding firewall rules" `
        -Emoji "🛡️"
    Write-Information ""

    # 4. Install service
    $currentStep++
    Write-Progress `
        -Activity "Installing Sunshine" `
        -Status "Installing service" `
        -PercentComplete (($currentStep / $totalSteps) * 100)
    $installServiceScript = Join-Path $RootDir "scripts\install-service.bat"
    Invoke-ScriptIfExist `
        -ScriptPath $installServiceScript `
        -Description "Installing Windows Service" `
        -Emoji "⚡"
    Write-Information ""

    # 5. Configure autostart
    $currentStep++
    Write-Progress `
        -Activity "Installing Sunshine" `
        -Status "Configuring autostart" `
        -PercentComplete (($currentStep / $totalSteps) * 100)
    $autostartScript = Join-Path $RootDir "scripts\autostart-service.bat"
    Invoke-ScriptIfExist `
        -ScriptPath $autostartScript `
        -Description "Configuring autostart" `
        -Emoji "🚀"
    Write-Information ""

    Write-Progress -Activity "Installing Sunshine" -Completed
    Write-FramedText -Message "✓ Sunshine installation completed successfully!" -Level "Success"

    # Open documentation in browser (only if not running silently)
    if (-not $Silent) {
        Write-Information ""
        Write-LogMessage `
            -Message "📖 Opening documentation in your browser: $DocsUrl" `
            -Level "Step"
        try {
            Start-Process $DocsUrl
            Write-LogMessage -Message "  ✓ Done" -Level "Success"
        } catch {
            Write-LogMessage `
                -Message "  ⓘ Could not open browser automatically: $($_.Exception.Message)" `
                -Level "Warning"
        }
    }

} elseif ($Action -eq "uninstall") {
    Write-FramedText `
        -Message "🗑️  Sunshine Uninstallation Script" `
        -Level "Information" `
        -Color "Yellow"
    Write-Information ""

    $totalSteps = 4
    $currentStep = 0

    # 1. Delete firewall rules
    $currentStep++
    Write-Progress `
        -Activity "Uninstalling Sunshine" `
        -Status "Removing firewall rules" `
        -PercentComplete (($currentStep / $totalSteps) * 100)
    $deleteFirewallScript = Join-Path $RootDir "scripts\delete-firewall-rule.bat"
    Invoke-ScriptIfExist `
        -ScriptPath $deleteFirewallScript `
        -Description "Removing firewall rules" `
        -Emoji "🛡️"
    Write-Information ""

    # 2. Uninstall service
    $currentStep++
    Write-Progress `
        -Activity "Uninstalling Sunshine" `
        -Status "Uninstalling service" `
        -PercentComplete (($currentStep / $totalSteps) * 100)
    $uninstallServiceScript = Join-Path $RootDir "scripts\uninstall-service.bat"
    Invoke-ScriptIfExist `
        -ScriptPath $uninstallServiceScript `
        -Description "Removing Windows Service" `
        -Emoji "⚡"
    Write-Information ""

    # 3. Restore NVIDIA preferences
    $currentStep++
    Write-Progress `
        -Activity "Uninstalling Sunshine" `
        -Status "Restoring NVIDIA settings" `
        -PercentComplete (($currentStep / $totalSteps) * 100)
    Invoke-SunshineIfExist `
        -Arguments "--restore-nvprefs-undo" `
        -Description "Restoring NVIDIA preferences" `
        -Emoji "🎮"
    Write-Information ""

    # 4. Update PATH (remove)
    $currentStep++
    Write-Progress `
        -Activity "Uninstalling Sunshine" `
        -Status "Cleaning up system PATH" `
        -PercentComplete (($currentStep / $totalSteps) * 100)
    $updatePathScript = Join-Path $RootDir "scripts\update-path.bat"
    Invoke-ScriptIfExist `
        -ScriptPath $updatePathScript `
        -Arguments "remove" `
        -Description "Removing from PATH" `
        -Emoji "📁"
    Write-Information ""

    Write-Progress -Activity "Uninstalling Sunshine" -Completed
    Write-FramedText `
        -Message "✓ Sunshine uninstallation completed successfully!" `
        -Level "Success"
}

Write-Information ""
exit 0

# SIG # Begin signature block
# MII9EwYJKoZIhvcNAQcCoII9BDCCPQACAQExDzANBglghkgBZQMEAgEFADB5Bgor
# BgEEAYI3AgEEoGswaTA0BgorBgEEAYI3AgEeMCYCAwEAAAQQH8w7YFlLCE63JNLG
# KX7zUQIBAAIBAAIBAAIBAAIBADAxMA0GCWCGSAFlAwQCAQUABCBaFsixvBWspTwg
# GDGaQA3gpgcKu/xWY4H4c8py5TE8nqCCIdgwggXMMIIDtKADAgECAhBUmNLR1FsZ
# lUgTecgRwIeZMA0GCSqGSIb3DQEBDAUAMHcxCzAJBgNVBAYTAlVTMR4wHAYDVQQK
# ExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xSDBGBgNVBAMTP01pY3Jvc29mdCBJZGVu
# dGl0eSBWZXJpZmljYXRpb24gUm9vdCBDZXJ0aWZpY2F0ZSBBdXRob3JpdHkgMjAy
# MDAeFw0yMDA0MTYxODM2MTZaFw00NTA0MTYxODQ0NDBaMHcxCzAJBgNVBAYTAlVT
# MR4wHAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xSDBGBgNVBAMTP01pY3Jv
# c29mdCBJZGVudGl0eSBWZXJpZmljYXRpb24gUm9vdCBDZXJ0aWZpY2F0ZSBBdXRo
# b3JpdHkgMjAyMDCCAiIwDQYJKoZIhvcNAQEBBQADggIPADCCAgoCggIBALORKgeD
# Bmf9np3gx8C3pOZCBH8Ppttf+9Va10Wg+3cL8IDzpm1aTXlT2KCGhFdFIMeiVPvH
# or+Kx24186IVxC9O40qFlkkN/76Z2BT2vCcH7kKbK/ULkgbk/WkTZaiRcvKYhOuD
# PQ7k13ESSCHLDe32R0m3m/nJxxe2hE//uKya13NnSYXjhr03QNAlhtTetcJtYmrV
# qXi8LW9J+eVsFBT9FMfTZRY33stuvF4pjf1imxUs1gXmuYkyM6Nix9fWUmcIxC70
# ViueC4fM7Ke0pqrrBc0ZV6U6CwQnHJFnni1iLS8evtrAIMsEGcoz+4m+mOJyoHI1
# vnnhnINv5G0Xb5DzPQCGdTiO0OBJmrvb0/gwytVXiGhNctO/bX9x2P29Da6SZEi3
# W295JrXNm5UhhNHvDzI9e1eM80UHTHzgXhgONXaLbZ7LNnSrBfjgc10yVpRnlyUK
# xjU9lJfnwUSLgP3B+PR0GeUw9gb7IVc+BhyLaxWGJ0l7gpPKWeh1R+g/OPTHU3mg
# trTiXFHvvV84wRPmeAyVWi7FQFkozA8kwOy6CXcjmTimthzax7ogttc32H83rwjj
# O3HbbnMbfZlysOSGM1l0tRYAe1BtxoYT2v3EOYI9JACaYNq6lMAFUSw0rFCZE4e7
# swWAsk0wAly4JoNdtGNz764jlU9gKL431VulAgMBAAGjVDBSMA4GA1UdDwEB/wQE
# AwIBhjAPBgNVHRMBAf8EBTADAQH/MB0GA1UdDgQWBBTIftJqhSobyhmYBAcnz1AQ
# T2ioojAQBgkrBgEEAYI3FQEEAwIBADANBgkqhkiG9w0BAQwFAAOCAgEAr2rd5hnn
# LZRDGU7L6VCVZKUDkQKL4jaAOxWiUsIWGbZqWl10QzD0m/9gdAmxIR6QFm3FJI9c
# Zohj9E/MffISTEAQiwGf2qnIrvKVG8+dBetJPnSgaFvlVixlHIJ+U9pW2UYXeZJF
# xBA2CFIpF8svpvJ+1Gkkih6PsHMNzBxKq7Kq7aeRYwFkIqgyuH4yKLNncy2RtNwx
# AQv3Rwqm8ddK7VZgxCwIo3tAsLx0J1KH1r6I3TeKiW5niB31yV2g/rarOoDXGpc8
# FzYiQR6sTdWD5jw4vU8w6VSp07YEwzJ2YbuwGMUrGLPAgNW3lbBeUU0i/OxYqujY
# lLSlLu2S3ucYfCFX3VVj979tzR/SpncocMfiWzpbCNJbTsgAlrPhgzavhgplXHT2
# 6ux6anSg8Evu75SjrFDyh+3XOjCDyft9V77l4/hByuVkrrOj7FjshZrM77nq81YY
# uVxzmq/FdxeDWds3GhhyVKVB0rYjdaNDmuV3fJZ5t0GNv+zcgKCf0Xd1WF81E+Al
# GmcLfc4l+gcK5GEh2NQc5QfGNpn0ltDGFf5Ozdeui53bFv0ExpK91IjmqaOqu/dk
# ODtfzAzQNb50GQOmxapMomE2gj4d8yu8l13bS3g7LfU772Aj6PXsCyM2la+YZr9T
# 03u4aUoqlmZpxJTG9F9urJh4iIAGXKKy7aIwggaZMIIEgaADAgECAhMzAAZvP8Ag
# /a2ui5nmAAAABm8/MA0GCSqGSIb3DQEBDAUAMFoxCzAJBgNVBAYTAlVTMR4wHAYD
# VQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xKzApBgNVBAMTIk1pY3Jvc29mdCBJ
# RCBWZXJpZmllZCBDUyBBT0MgQ0EgMDMwHhcNMjYwOTEzMjIxNTUxWhcNMjYwOTE2
# MjIxNTUxWjBdMQswCQYDVQQGEwJVUzEQMA4GA1UECBMHRmxvcmlkYTESMBAGA1UE
# BxMJS0lTU0lNTUVFMRMwEQYDVQQKEwpEYXZpZCBMYW5lMRMwEQYDVQQDEwpEYXZp
# ZCBMYW5lMIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAmHxWe+6NELtt
# u7RmAOYgcRpqvun9NOhbZme7TyGYzwi8TrqctHgHcjLYAcIw/m0d/tVOD8zuKiGn
# HioS5ScBRJJEPpdYBj9SZbCwZkHhxJBNhIMcHvtvjxrkMZWchX2LvZuiQems/S+Y
# fDabqXzPaKyjPRfbwmnbYYcyDZP7RuVdCrEb28xfzqcde7QFQQCLS2ALY8QSO0E4
# XXEbnxvcUTIBppKwGZ7EK/UpNXTJhwtZED+EWpYp9k735zYoo/fKXVUTHjifoWL1
# jtOT0SWRCgWFKjJoqtobcvy1Fr7BVRaBIjlAw15Os0uZqqlXOhLZd88t9BPEgqIO
# fIgwyliuGJ0kHsibMXbfYaLtZKvHmGj3yoqWrqdGBm5kaJnmhZwPIbOCyq3JRxTE
# o37f0GY8RLloRgD9kn78w6ToLdm1+orMUq7gR2BA2VhegEpuM60kCVQQnDpAyMsX
# nN+dPx+keuVvoojeUwx/uEyLbALFFQsdBjMXoaFwlTbfehTCrubDAgMBAAGjggHT
# MIIBzzAMBgNVHRMBAf8EAjAAMA4GA1UdDwEB/wQEAwIHgDA6BgNVHSUEMzAxBgor
# BgEEAYI3YQEABggrBgEFBQcDAwYZKwYBBAGCN2GBh5LYVtGN8AD+qs4qiv26ZDAd
# BgNVHQ4EFgQUbw+VflDCZ9/3Cdsphysub/awg0owHwYDVR0jBBgwFoAUpEMMf3Za
# pYXnPo0oDwwXokVpcMYwZwYDVR0fBGAwXjBcoFqgWIZWaHR0cDovL3d3dy5taWNy
# b3NvZnQuY29tL3BraW9wcy9jcmwvTWljcm9zb2Z0JTIwSUQlMjBWZXJpZmllZCUy
# MENTJTIwQU9DJTIwQ0ElMjAwMy5jcmwwdAYIKwYBBQUHAQEEaDBmMGQGCCsGAQUF
# BzAChlhodHRwOi8vd3d3Lm1pY3Jvc29mdC5jb20vcGtpb3BzL2NlcnRzL01pY3Jv
# c29mdCUyMElEJTIwVmVyaWZpZWQlMjBDUyUyMEFPQyUyMENBJTIwMDMuY3J0MFQG
# A1UdIARNMEswSQYEVR0gADBBMD8GCCsGAQUFBwIBFjNodHRwOi8vd3d3Lm1pY3Jv
# c29mdC5jb20vcGtpb3BzL0RvY3MvUmVwb3NpdG9yeS5odG0wDQYJKoZIhvcNAQEM
# BQADggIBAEbZYfumzls/xGJI93tFCjG6h8bh3gs2yO66pLz57O6FOFh4Km5Q3OIY
# h5MD7AQ+ywBimBDXAvu7r40rqABMyWJVot48WbbYGxo08HhjhU5gvvT7m2nae0zf
# nPk1HhPJGysobrT4EZCCyuHKbg4IL3oByJ/roc5Qh1dqCL/DRPoypMgDj04ME0Qk
# +aZQXItdDUDjTfXs+uy8rGJpdCnMWjyXzGDApikWTotoTxe4Q/o/6R8Q7T9qlrPG
# UaeNRFEUK4Z3anITlKgC/n6Ijt/Vak3kWs8TTmpd17v7AH0/39YQV8JfPGCBPTPl
# Gwkw7Yj07Hx5kKW4BhStXPKXqGM39yVbKGNupUtJvyIiHRa4Topc0O56qYpuVNOa
# 39GjkjTUKDoKt2Hprtn/RdbSeYtVxFShXaOwltONRL1Gp9BfBdYBaLrwQwJehgnf
# YL/Y203q0y5bAtvnLA9A8qtUMM2s1MM+ka8BiUOeMbs+stp5PpkudnpFukDpCQ7I
# mVK82J/SnpNhXo8ChU5E8/Cq7CESB2SZkACmiHKGEZDncKJCrvI00z2NmjvRepcy
# vsXEvbrmd/Q/Jcx8cfkLDsRLry7JbhNCjMl8NXG/goOgBp555tAbBeJsIv5lmzvQ
# BoQD197Tq6T2iWVTlq2bpS/G8lCBpv4nkYs0fqMvde+ssdZUFV3oMIIGmTCCBIGg
# AwIBAgITMwAGbz/AIP2trouZ5gAAAAZvPzANBgkqhkiG9w0BAQwFADBaMQswCQYD
# VQQGEwJVUzEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMSswKQYDVQQD
# EyJNaWNyb3NvZnQgSUQgVmVyaWZpZWQgQ1MgQU9DIENBIDAzMB4XDTI2MDkxMzIy
# MTU1MVoXDTI2MDkxNjIyMTU1MVowXTELMAkGA1UEBhMCVVMxEDAOBgNVBAgTB0Zs
# b3JpZGExEjAQBgNVBAcTCUtJU1NJTU1FRTETMBEGA1UEChMKRGF2aWQgTGFuZTET
# MBEGA1UEAxMKRGF2aWQgTGFuZTCCAaIwDQYJKoZIhvcNAQEBBQADggGPADCCAYoC
# ggGBAJh8VnvujRC7bbu0ZgDmIHEaar7p/TToW2Znu08hmM8IvE66nLR4B3Iy2AHC
# MP5tHf7VTg/M7iohpx4qEuUnAUSSRD6XWAY/UmWwsGZB4cSQTYSDHB77b48a5DGV
# nIV9i72bokHprP0vmHw2m6l8z2isoz0X28Jp22GHMg2T+0blXQqxG9vMX86nHXu0
# BUEAi0tgC2PEEjtBOF1xG58b3FEyAaaSsBmexCv1KTV0yYcLWRA/hFqWKfZO9+c2
# KKP3yl1VEx44n6Fi9Y7Tk9ElkQoFhSoyaKraG3L8tRa+wVUWgSI5QMNeTrNLmaqp
# VzoS2XfPLfQTxIKiDnyIMMpYrhidJB7ImzF232Gi7WSrx5ho98qKlq6nRgZuZGiZ
# 5oWcDyGzgsqtyUcUxKN+39BmPES5aEYA/ZJ+/MOk6C3ZtfqKzFKu4EdgQNlYXoBK
# bjOtJAlUEJw6QMjLF5zfnT8fpHrlb6KI3lMMf7hMi2wCxRULHQYzF6GhcJU233oU
# wq7mwwIDAQABo4IB0zCCAc8wDAYDVR0TAQH/BAIwADAOBgNVHQ8BAf8EBAMCB4Aw
# OgYDVR0lBDMwMQYKKwYBBAGCN2EBAAYIKwYBBQUHAwMGGSsGAQQBgjdhgYeS2FbR
# jfAA/qrOKor9umQwHQYDVR0OBBYEFG8PlX5Qwmff9wnbKYcrLm/2sINKMB8GA1Ud
# IwQYMBaAFKRDDH92WqWF5z6NKA8MF6JFaXDGMGcGA1UdHwRgMF4wXKBaoFiGVmh0
# dHA6Ly93d3cubWljcm9zb2Z0LmNvbS9wa2lvcHMvY3JsL01pY3Jvc29mdCUyMElE
# JTIwVmVyaWZpZWQlMjBDUyUyMEFPQyUyMENBJTIwMDMuY3JsMHQGCCsGAQUFBwEB
# BGgwZjBkBggrBgEFBQcwAoZYaHR0cDovL3d3dy5taWNyb3NvZnQuY29tL3BraW9w
# cy9jZXJ0cy9NaWNyb3NvZnQlMjBJRCUyMFZlcmlmaWVkJTIwQ1MlMjBBT0MlMjBD
# QSUyMDAzLmNydDBUBgNVHSAETTBLMEkGBFUdIAAwQTA/BggrBgEFBQcCARYzaHR0
# cDovL3d3dy5taWNyb3NvZnQuY29tL3BraW9wcy9Eb2NzL1JlcG9zaXRvcnkuaHRt
# MA0GCSqGSIb3DQEBDAUAA4ICAQBG2WH7ps5bP8RiSPd7RQoxuofG4d4LNsjuuqS8
# +ezuhThYeCpuUNziGIeTA+wEPssAYpgQ1wL7u6+NK6gATMliVaLePFm22BsaNPB4
# Y4VOYL70+5tp2ntM35z5NR4TyRsrKG60+BGQgsrhym4OCC96Acif66HOUIdXagi/
# w0T6MqTIA49ODBNEJPmmUFyLXQ1A40317PrsvKxiaXQpzFo8l8xgwKYpFk6LaE8X
# uEP6P+kfEO0/apazxlGnjURRFCuGd2pyE5SoAv5+iI7f1WpN5FrPE05qXde7+wB9
# P9/WEFfCXzxggT0z5RsJMO2I9Ox8eZCluAYUrVzyl6hjN/clWyhjbqVLSb8iIh0W
# uE6KXNDueqmKblTTmt/Ro5I01Cg6Crdh6a7Z/0XW0nmLVcRUoV2jsJbTjUS9RqfQ
# XwXWAWi68EMCXoYJ32C/2NtN6tMuWwLb5ywPQPKrVDDNrNTDPpGvAYlDnjG7PrLa
# eT6ZLnZ6RbpA6QkOyJlSvNif0p6TYV6PAoVORPPwquwhEgdkmZAApohyhhGQ53Ci
# Qq7yNNM9jZo70XqXMr7FxL265nf0PyXMfHH5Cw7ES68uyW4TQozJfDVxv4KDoAae
# eebQGwXibCL+ZZs70AaEA9fe06uk9ollU5atm6UvxvJQgab+J5GLNH6jL3XvrLHW
# VBVd6DCCBygwggUQoAMCAQICEzMAAAAYDeuRVamKAJgAAAAAABgwDQYJKoZIhvcN
# AQEMBQAwYzELMAkGA1UEBhMCVVMxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3Jh
# dGlvbjE0MDIGA1UEAxMrTWljcm9zb2Z0IElEIFZlcmlmaWVkIENvZGUgU2lnbmlu
# ZyBQQ0EgMjAyMTAeFw0yNjAzMjYxODExMzJaFw0zMTAzMjYxODExMzJaMFoxCzAJ
# BgNVBAYTAlVTMR4wHAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xKzApBgNV
# BAMTIk1pY3Jvc29mdCBJRCBWZXJpZmllZCBDUyBBT0MgQ0EgMDMwggIiMA0GCSqG
# SIb3DQEBAQUAA4ICDwAwggIKAoICAQDIgNpgNFaiif2VWeWP5I6PnFXxJ/lB37fJ
# R55GCvR7GLZBMkBijbiKVwgpBI3xM5nf484znH/qncJ+OCq6y3jgnQW+R8Zd7U+7
# LjlrmcskalzSQ0ghMxEpnBW8/HHs2V8ZJzQk6HP+SDsbvsL7LdlH/eO2l4mknhDB
# wr0Z/Q966TvEth5b8kCxj1vqiV4YNthLGRqZR9u2fK/yBMWu83p6O4uo2Edg++gE
# ew5IL7vnnnKFqmSh/R9vPJy3WF1YcZewAUx8sXZNUnx3ZhVg59l2LpitPiwzE6FM
# qIsqaEvVe3MzuFd2a/uWDZH6VbDyUiRK78mIg1DQYA9zDEyyBFcNI+nxVSzglvL6
# u7PRuNqgcV3sf6ELxw89ysQM/Z4R1hRFWXRpyOWKKAKtfBHTk0UnNiPcxmLMMYs8
# jeUjOidfVPjTIry/UVwnwxdlkK85cZfBEMYZ/DBNOwdomP459Y1n8izKkbhsa+p4
# lw+cQVxATBFx9ggR79HhryT7HDmpPLvkJvBZ4wW4CW32UT2SMyDe28nIOU3m+hfH
# lVeKcLBQcym5VoRDjIcCVI7uqgGW2PNME0cfei8zCwCy6HCsssJWFS7eg/YbFhnA
# TJcyWfMrkNuAbMfMN8Npg8crS6jVVowyD0GG5zdgi+uQVcSK/638mA1xEYK3pnIo
# QgO09uuDBwIDAQABo4IB3DCCAdgwDgYDVR0PAQH/BAQDAgGGMBAGCSsGAQQBgjcV
# AQQDAgEAMB0GA1UdDgQWBBSkQwx/dlqlhec+jSgPDBeiRWlwxjBUBgNVHSAETTBL
# MEkGBFUdIAAwQTA/BggrBgEFBQcCARYzaHR0cDovL3d3dy5taWNyb3NvZnQuY29t
# L3BraW9wcy9Eb2NzL1JlcG9zaXRvcnkuaHRtMBkGCSsGAQQBgjcUAgQMHgoAUwB1
# AGIAQwBBMBIGA1UdEwEB/wQIMAYBAf8CAQAwHwYDVR0jBBgwFoAU2UEpsA8PY2zv
# adf1zSmepEhqMOYwcAYDVR0fBGkwZzBloGOgYYZfaHR0cDovL3d3dy5taWNyb3Nv
# ZnQuY29tL3BraW9wcy9jcmwvTWljcm9zb2Z0JTIwSUQlMjBWZXJpZmllZCUyMENv
# ZGUlMjBTaWduaW5nJTIwUENBJTIwMjAyMS5jcmwwfQYIKwYBBQUHAQEEcTBvMG0G
# CCsGAQUFBzAChmFodHRwOi8vd3d3Lm1pY3Jvc29mdC5jb20vcGtpb3BzL2NlcnRz
# L01pY3Jvc29mdCUyMElEJTIwVmVyaWZpZWQlMjBDb2RlJTIwU2lnbmluZyUyMFBD
# QSUyMDIwMjEuY3J0MA0GCSqGSIb3DQEBDAUAA4ICAQBxxyBW+X6mhdRiSwD9PMMW
# cGUAnx5/QUwnNvZdFGEX+4DRDIr9WCh4C87wHtw+lg1D3uzK10DstPX0LFLBFAC3
# vWMYX4ImXwoLhoR0xlN8mUdorJ3bgnpCJWuI1531Z1rCwPuUrSkBxfOIGDk3p2EC
# b3Ho/xHi5PRSR/OUrWuQHwXiaXMTuXu3IRLezwVkZpFmNwYRD57R9Nx2F/yM7tzO
# Y0Hh0hGCaYEK38/6FrS0SXadXWyDUCfn5XOGACRjUCnHx+JQUG0f4SHD+iblpAI0
# gl+ZHnVmdXXxHTZeTa0CYCIhFxKP2922s0g6zLmeiV13LWUmtt/UF7TrWXpMi2/0
# UNniaDoH7rnPGRV5xVX8uXy4sZii4aswzqPM7Y7+mzcranqZ8EjZk5gjLhQ3A2sZ
# aprlOu8CaRmyfcIiVH7zVfgAvm81MWXFziAf7my7QOvnyEFPGddq8MSfPtfRyw/U
# q3uH6KpoaJNIfPYH6fceZSi53Rat1A9grExq3ROjhhSpTcchuBItAMNVPxoKNbUm
# +iR/X3XkL+9WQginjyHe+hXLclY8vAGXFD1p40PqMIpAYsmEJBFKW9df4//1N5oQ
# Dr/FY9IBJl/oSS979i5rtT7NZz9KvYraCPRBGs0QCy+sWvgQa0coM70QJVLeVwmS
# xUO/0od0w9Qry7bSLrxGoDCCB54wggWGoAMCAQICEzMAAAAHh6M0o3uljhwAAAAA
# AAcwDQYJKoZIhvcNAQEMBQAwdzELMAkGA1UEBhMCVVMxHjAcBgNVBAoTFU1pY3Jv
# c29mdCBDb3Jwb3JhdGlvbjFIMEYGA1UEAxM/TWljcm9zb2Z0IElkZW50aXR5IFZl
# cmlmaWNhdGlvbiBSb290IENlcnRpZmljYXRlIEF1dGhvcml0eSAyMDIwMB4XDTIx
# MDQwMTIwMDUyMFoXDTM2MDQwMTIwMTUyMFowYzELMAkGA1UEBhMCVVMxHjAcBgNV
# BAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjE0MDIGA1UEAxMrTWljcm9zb2Z0IElE
# IFZlcmlmaWVkIENvZGUgU2lnbmluZyBQQ0EgMjAyMTCCAiIwDQYJKoZIhvcNAQEB
# BQADggIPADCCAgoCggIBALLwwK8ZiCji3VR6TElsaQhVCbRS/3pK+MHrJSj3Zxd3
# KU3rlfL3qrZilYKJNqztA9OQacr1AwoNcHbKBLbsQAhBnIB34zxf52bDpIO3NJlf
# IaTE/xrweLoQ71lzCHkD7A4As1Bs076Iu+mA6cQzsYYH/Cbl1icwQ6C65rU4V9NQ
# hNUwgrx9rGQ//h890Q8JdjLLw0nV+ayQ2Fbkd242o9kH82RZsH3HEyqjAB5a8+Ae
# 2nPIPc8sZU6ZE7iRrRZywRmrKDp5+TcmJX9MRff241UaOBs4NmHOyke8oU1TYrkx
# h+YeHgfWo5tTgkoSMoayqoDpHOLJs+qG8Tvh8SnifW2Jj3+ii11TS8/FGngEaNAW
# rbyfNrC69oKpRQXY9bGH6jn9NEJv9weFxhTwyvx9OJLXmRGbAUXN1U9nf4lXezky
# 6Uh/cgjkVd6CGUAf0K+Jw+GE/5VpIVbcNr9rNE50Sbmy/4RTCEGvOq3GhjITbCa4
# crCzTTHgYYjHs1NbOc6brH+eKpWLtr+bGecy9CrwQyx7S/BfYJ+ozst7+yZtG2wR
# 461uckFu0t+gCwLdN0A6cFtSRtR8bvxVFyWwTtgMMFRuBa3vmUOTnfKLsLefRaQc
# VTgRnzeLzdpt32cdYKp+dhr2ogc+qM6K4CBI5/j4VFyC4QFeUP2YAidLtvpXRRo3
# AgMBAAGjggI1MIICMTAOBgNVHQ8BAf8EBAMCAYYwEAYJKwYBBAGCNxUBBAMCAQAw
# HQYDVR0OBBYEFNlBKbAPD2Ns72nX9c0pnqRIajDmMFQGA1UdIARNMEswSQYEVR0g
# ADBBMD8GCCsGAQUFBwIBFjNodHRwOi8vd3d3Lm1pY3Jvc29mdC5jb20vcGtpb3Bz
# L0RvY3MvUmVwb3NpdG9yeS5odG0wGQYJKwYBBAGCNxQCBAweCgBTAHUAYgBDAEEw
# DwYDVR0TAQH/BAUwAwEB/zAfBgNVHSMEGDAWgBTIftJqhSobyhmYBAcnz1AQT2io
# ojCBhAYDVR0fBH0wezB5oHegdYZzaHR0cDovL3d3dy5taWNyb3NvZnQuY29tL3Br
# aW9wcy9jcmwvTWljcm9zb2Z0JTIwSWRlbnRpdHklMjBWZXJpZmljYXRpb24lMjBS
# b290JTIwQ2VydGlmaWNhdGUlMjBBdXRob3JpdHklMjAyMDIwLmNybDCBwwYIKwYB
# BQUHAQEEgbYwgbMwgYEGCCsGAQUFBzAChnVodHRwOi8vd3d3Lm1pY3Jvc29mdC5j
# b20vcGtpb3BzL2NlcnRzL01pY3Jvc29mdCUyMElkZW50aXR5JTIwVmVyaWZpY2F0
# aW9uJTIwUm9vdCUyMENlcnRpZmljYXRlJTIwQXV0aG9yaXR5JTIwMjAyMC5jcnQw
# LQYIKwYBBQUHMAGGIWh0dHA6Ly9vbmVvY3NwLm1pY3Jvc29mdC5jb20vb2NzcDAN
# BgkqhkiG9w0BAQwFAAOCAgEAfyUqnv7Uq+rdZgrbVyNMul5skONbhls5fccPlmIb
# zi+OwVdPQ4H55v7VOInnmezQEeW4LqK0wja+fBznANbXLB0KrdMCbHQpbLvG6UA/
# Xv2pfpVIE1CRFfNF4XKO8XYEa3oW8oVH+KZHgIQRIwAbyFKQ9iyj4aOWeAzwk+f9
# E5StNp5T8FG7/VEURIVWArbAzPt9ThVN3w1fAZkF7+YU9kbq1bCR2YD+MtunSQ1R
# ft6XG7b4e0ejRA7mB2IoX5hNh3UEauY0byxNRG+fT2MCEhQl9g2i2fs6VOG19CNe
# p7SquKaBjhWmirYyANb0RJSLWjinMLXNOAga10n8i9jqeprzSMU5ODmrMCJE12xS
# /NWShg/tuLjAsKP6SzYZ+1Ry358ZTFcx0FS/mx2vSoU8s8HRvy+rnXqyUJ9HBqS0
# DErVLjQwK8VtsBdekBmdTbQVoCgPCqr+PDPB3xajYnzevs7eidBsM71PINK2BoE2
# UfMwxCCX3mccFgx6UsQeRSdVVVNSyALQe6PT12418xon2iDGE81OGCreLzDcMAZn
# rUAx4XQLUz6ZTl65yPUiOh3k7Yww94lDf+8oG2oZmDh5O1Qe38E+M3vhKwmzIeoB
# 1dVLlz4i3IpaDcR+iuGjH2TdaC1ZOmBXiCRKJLj4DT2uhJ04ji+tHD6n58vhavFI
# rmcxghqRMIIajQIBATBxMFoxCzAJBgNVBAYTAlVTMR4wHAYDVQQKExVNaWNyb3Nv
# ZnQgQ29ycG9yYXRpb24xKzApBgNVBAMTIk1pY3Jvc29mdCBJRCBWZXJpZmllZCBD
# UyBBT0MgQ0EgMDMCEzMABm8/wCD9ra6LmeYAAAAGbz8wDQYJYIZIAWUDBAIBBQCg
# XjAQBgorBgEEAYI3AgEMMQIwADAZBgkqhkiG9w0BCQMxDAYKKwYBBAGCNwIBBDAv
# BgkqhkiG9w0BCQQxIgQg4F1eGXnvhvcRBRKBADoLcIP3gMZDDrnbvrotFXTWnUYw
# DQYJKoZIhvcNAQEBBQAEggGAjV7t2TAgzpgHpIbLEaCuLA+BKt57yVQzNQHAf6Eo
# gyrYkrUys9KyOhCtTBv1J18F2YzCdBRobcWCRk4MepNt7r9DEfvU3h4GO3u+SvdM
# 5sLLa8/vI2bRw1k4oarwKplFFCRRONClXI0HJWU1n2OxvQWBLmw6q5dTfHb1oOae
# oGm17OQd/dwM+pWSyz45rQb5D/pITI++7eGt/PaQQuXKwNmzHL+nfEQywxfbArR5
# oomt4LOdmBS4xFDNcokyrVhOnK82a0ZRuSVtA5WFwXM0GhuGDpQL5TsAwTtG6Nal
# X/1RSHNwFB6+f7p9hP1qqFet2ubsSrcLRbXXt67uDoI+mBrzolf6+eULnQT/wnQg
# sUUKF2U2HJJqsxsCAdkv2XKADGIAkzCRKRkANl1xXP+SD5bEbnT7Z+cCPnZrZBgs
# Ks61e0IMC2KoJnXElC/PWDmut9dPn7KKpLg9dMF0XQJu8fDoYO0ap3rZPlJOroPT
# CRtiOVwP77nTnZlDMgD5NuMQoYIYETCCGA0GCisGAQQBgjcDAwExghf9MIIX+QYJ
# KoZIhvcNAQcCoIIX6jCCF+YCAQMxDzANBglghkgBZQMEAgEFADCCAWIGCyqGSIb3
# DQEJEAEEoIIBUQSCAU0wggFJAgEBBgorBgEEAYRZCgMBMDEwDQYJYIZIAWUDBAIB
# BQAEIOA8bh+kdqTrYCaYPG0iCl6ZiuqGs5N+cbBuunIo6N/MAgZqpBBApZkYEzIw
# MjYwOTE1MDAwNjEzLjk2NVowBIACAfSggeGkgd4wgdsxCzAJBgNVBAYTAlVTMRMw
# EQYDVQQIEwpXYXNoaW5ndG9uMRAwDgYDVQQHEwdSZWRtb25kMR4wHAYDVQQKExVN
# aWNyb3NvZnQgQ29ycG9yYXRpb24xJTAjBgNVBAsTHE1pY3Jvc29mdCBBbWVyaWNh
# IE9wZXJhdGlvbnMxJzAlBgNVBAsTHm5TaGllbGQgVFNTIEVTTjo3QTAwLTA1RTAt
# RDk0NzE1MDMGA1UEAxMsTWljcm9zb2Z0IFB1YmxpYyBSU0EgVGltZSBTdGFtcGlu
# ZyBBdXRob3JpdHmggg8hMIIHgjCCBWqgAwIBAgITMwAAAAXlzw//Zi7JhwAAAAAA
# BTANBgkqhkiG9w0BAQwFADB3MQswCQYDVQQGEwJVUzEeMBwGA1UEChMVTWljcm9z
# b2Z0IENvcnBvcmF0aW9uMUgwRgYDVQQDEz9NaWNyb3NvZnQgSWRlbnRpdHkgVmVy
# aWZpY2F0aW9uIFJvb3QgQ2VydGlmaWNhdGUgQXV0aG9yaXR5IDIwMjAwHhcNMjAx
# MTE5MjAzMjMxWhcNMzUxMTE5MjA0MjMxWjBhMQswCQYDVQQGEwJVUzEeMBwGA1UE
# ChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMTIwMAYDVQQDEylNaWNyb3NvZnQgUHVi
# bGljIFJTQSBUaW1lc3RhbXBpbmcgQ0EgMjAyMDCCAiIwDQYJKoZIhvcNAQEBBQAD
# ggIPADCCAgoCggIBAJ5851Jj/eDFnwV9Y7UGIqMcHtfnlzPREwW9ZUZHd5HBXXBv
# f7KrQ5cMSqFSHGqg2/qJhYqOQxwuEQXG8kB41wsDJP5d0zmLYKAY8Zxv3lYkuLDs
# fMuIEqvGYOPURAH+Ybl4SJEESnt0MbPEoKdNihwM5xGv0rGofJ1qOYSTNcc55EbB
# T7uq3wx3mXhtVmtcCEr5ZKTkKKE1CxZvNPWdGWJUPC6e4uRfWHIhZcgCsJ+sozf5
# EeH5KrlFnxpjKKTavwfFP6XaGZGWUG8TZaiTogRoAlqcevbiqioUz1Yt4FRK53P6
# ovnUfANjIgM9JDdJ4e0qiDRm5sOTiEQtBLGd9Vhd1MadxoGcHrRCsS5rO9yhv2fj
# JHrmlQ0EIXmp4DhDBieKUGR+eZ4CNE3ctW4uvSDQVeSp9h1SaPV8UWEfyTxgGjOs
# RpeexIveR1MPTVf7gt8hY64XNPO6iyUGsEgt8c2PxF87E+CO7A28TpjNq5eLiiun
# hKbq0XbjkNoU5JhtYUrlmAbpxRjb9tSreDdtACpm3rkpxp7AQndnI0Shu/fk1/rE
# 3oWsDqMX3jjv40e8KN5YsJBnczyWB4JyeeFMW3JBfdeAKhzohFe8U5w9WuvcP1E8
# cIxLoKSDzCCBOu0hWdjzKNu8Y5SwB1lt5dQhABYyzR3dxEO/T1K/BVF3rV69AgMB
# AAGjggIbMIICFzAOBgNVHQ8BAf8EBAMCAYYwEAYJKwYBBAGCNxUBBAMCAQAwHQYD
# VR0OBBYEFGtpKDo1L0hjQM972K9J6T7ZPdshMFQGA1UdIARNMEswSQYEVR0gADBB
# MD8GCCsGAQUFBwIBFjNodHRwOi8vd3d3Lm1pY3Jvc29mdC5jb20vcGtpb3BzL0Rv
# Y3MvUmVwb3NpdG9yeS5odG0wEwYDVR0lBAwwCgYIKwYBBQUHAwgwGQYJKwYBBAGC
# NxQCBAweCgBTAHUAYgBDAEEwDwYDVR0TAQH/BAUwAwEB/zAfBgNVHSMEGDAWgBTI
# ftJqhSobyhmYBAcnz1AQT2ioojCBhAYDVR0fBH0wezB5oHegdYZzaHR0cDovL3d3
# dy5taWNyb3NvZnQuY29tL3BraW9wcy9jcmwvTWljcm9zb2Z0JTIwSWRlbnRpdHkl
# MjBWZXJpZmljYXRpb24lMjBSb290JTIwQ2VydGlmaWNhdGUlMjBBdXRob3JpdHkl
# MjAyMDIwLmNybDCBlAYIKwYBBQUHAQEEgYcwgYQwgYEGCCsGAQUFBzAChnVodHRw
# Oi8vd3d3Lm1pY3Jvc29mdC5jb20vcGtpb3BzL2NlcnRzL01pY3Jvc29mdCUyMElk
# ZW50aXR5JTIwVmVyaWZpY2F0aW9uJTIwUm9vdCUyMENlcnRpZmljYXRlJTIwQXV0
# aG9yaXR5JTIwMjAyMC5jcnQwDQYJKoZIhvcNAQEMBQADggIBAF+Idsd+bbVaFXXn
# THho+k7h2ESZJRWluLE0Oa/pO+4ge/XEizXvhs0Y7+KVYyb4nHlugBesnFqBGEdC
# 2IWmtKMyS1OWIviwpnK3aL5JedwzbeBF7POyg6IGG/XhhJ3UqWeWTO+Czb1c2NP5
# zyEh89F72u9UIw+IfvM9lzDmc2O2END7MPnrcjWdQnrLn1Ntday7JSyrDvBdmgbN
# nCKNZPmhzoa8PccOiQljjTW6GePe5sGFuRHzdFt8y+bN2neF7Zu8hTO1I64XNGqs
# t8S+w+RUdie8fXC1jKu3m9KGIqF4aldrYBamyh3g4nJPj/LR2CBaLyD+2BuGZCVm
# oNR/dSpRCxlot0i79dKOChmoONqbMI8m04uLaEHAv4qwKHQ1vBzbV/nG89LDKbRS
# SvijmwJwxRxLLpMQ/u4xXxFfR4f/gksSkbJp7oqLwliDm/h+w0aJ/U5ccnYhYb7v
# PKNMN+SZDWycU5ODIRfyoGl59BsXR/HpRGtiJquOYGmvA/pk5vC1lcnbeMrcWD/2
# 6ozePQ/TWfNXKBOmkFpvPE8CH+EeGGWzqTCjdAsno2jzTeNSxlx3glDGJgcdz5D/
# AAxw9Sdgq/+rY7jjgs7X6fqPTXPmaCAJKVHAP19oEjJIBwD1LyHbaEgBxFCogYSO
# iUIr0Xqcr1nJfiWG2GwYe6ZoAF1bMIIHlzCCBX+gAwIBAgITMwAAAFhlzes/odf8
# 0gAAAAAAWDANBgkqhkiG9w0BAQwFADBhMQswCQYDVQQGEwJVUzEeMBwGA1UEChMV
# TWljcm9zb2Z0IENvcnBvcmF0aW9uMTIwMAYDVQQDEylNaWNyb3NvZnQgUHVibGlj
# IFJTQSBUaW1lc3RhbXBpbmcgQ0EgMjAyMDAeFw0yNTEwMjMyMDQ2NTVaFw0yNjEw
# MjIyMDQ2NTVaMIHbMQswCQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQ
# MA4GA1UEBxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9u
# MSUwIwYDVQQLExxNaWNyb3NvZnQgQW1lcmljYSBPcGVyYXRpb25zMScwJQYDVQQL
# Ex5uU2hpZWxkIFRTUyBFU046N0EwMC0wNUUwLUQ5NDcxNTAzBgNVBAMTLE1pY3Jv
# c29mdCBQdWJsaWMgUlNBIFRpbWUgU3RhbXBpbmcgQXV0aG9yaXR5MIICIjANBgkq
# hkiG9w0BAQEFAAOCAg8AMIICCgKCAgEAnXg0pHaQ7PVAlln+HZZrJFcLoKbekhW1
# yL+QNBUgFFUsjZIKaqqN4oIJsJM3ps0rJNSO7ndCNRuZDX2Wgur3Ak77eXrloBXq
# ZmO6ZVXeDNRCLldW4A0/NfjzJ7XXkdEhjr81ghXEpR7zC+wbaNN+sPSxzLAZBeib
# DFP7Xws5wX0ZtIsN1a2+Xq5bvWp3kRMytwskTjunRgeLZL/tBp237JVdRPFAQ9jY
# RKpCqUBo/v1xjBLRCV3PalKjnGfb3MN4U7jVyqifFHShcnW5CERRoBmUa6sygDzF
# Sr8e3g93TPNLFUivUE0GmLfbX5ceD1Gt1FcZ6x/JLVATzk5+BWHbMxwJIVkVPTqS
# SMjQ6KTKdcnq3pH0c4AFJp/glvcpq0U9fzZIjJGGvdpishlRl77RQtUhSjxHvCn3
# LC/xqQQwOHSQDsGh6NX2D0RfsSyEtTAByAae+2w1HByTDTcmlTNLEuQLeCj1gNBd
# IWj0WOYyDtjjQ/8iTWY6ey1vb9qHljIj5HgIndT5P9MYk2Vg2e7hKUZNBNbA/hsg
# BsuoZ+IX89WvjEN9abF91S4OJVuinmKsLO/MLbnl7ikuD0dN6oA0YewyDQncs12s
# M9HOtu72QA/TZlefvW8r9xtMXAYoQlcGjsk8W4Uc7cfqVqbIPjdoc8ZxBzLcXcVy
# P4p5cyLwvkMCAwEAAaOCAcswggHHMB0GA1UdDgQWBBRyjU3Fer4VxXJ+hjPcRJnx
# nRIJsDAfBgNVHSMEGDAWgBRraSg6NS9IY0DPe9ivSek+2T3bITBsBgNVHR8EZTBj
# MGGgX6BdhltodHRwOi8vd3d3Lm1pY3Jvc29mdC5jb20vcGtpb3BzL2NybC9NaWNy
# b3NvZnQlMjBQdWJsaWMlMjBSU0ElMjBUaW1lc3RhbXBpbmclMjBDQSUyMDIwMjAu
# Y3JsMHkGCCsGAQUFBwEBBG0wazBpBggrBgEFBQcwAoZdaHR0cDovL3d3dy5taWNy
# b3NvZnQuY29tL3BraW9wcy9jZXJ0cy9NaWNyb3NvZnQlMjBQdWJsaWMlMjBSU0El
# MjBUaW1lc3RhbXBpbmclMjBDQSUyMDIwMjAuY3J0MAwGA1UdEwEB/wQCMAAwFgYD
# VR0lAQH/BAwwCgYIKwYBBQUHAwgwDgYDVR0PAQH/BAQDAgeAMGYGA1UdIARfMF0w
# UQYMKwYBBAGCN0yDfQEBMEEwPwYIKwYBBQUHAgEWM2h0dHA6Ly93d3cubWljcm9z
# b2Z0LmNvbS9wa2lvcHMvRG9jcy9SZXBvc2l0b3J5Lmh0bTAIBgZngQwBBAIwDQYJ
# KoZIhvcNAQEMBQADggIBAHvrxIiVF1iHcXvxrJTCD8eOtbPUbK9x+Lz70iYehh+G
# 0UoOMcMf04QD6tQPTeZ5HhGETkcn0raDJ5NpfbRBuKEH31rxbZK97o12KRDNJ3Nu
# 4ePaUIpH/TcWz8PLVOCECywSxbEgEG20kyydGc46c591tXzpfkJDckjoYrypaerd
# eQLRQH9LaoTYZfdAzMo+Dy0O1DzFJkF5YnsmAM8lt9r1NtXdFjdbFMCbV5dau64m
# V22s186A8Umi+l239+Ue0cbJQIykWhIlhhWhxQgoksqHz7kp2GFZAAeySTmIOQOW
# yXOA8JA8TISJyn3JDOgStv583P3V0QSALT6JXDCW26FV208VGJMzkv0S22iOTZJ/
# oamTpk8RzD8oWT8pfbe1q/k/bxPiXYRbzps96a5YOko7n0Vdo61DOJhL/mhk01Y3
# 48gq6vhG/VTcdGHh1rCkwOM05B35AZZq9AtPpfRzJinrHzzGRx+r6fD3ccYMPMMX
# /Nwd2irzrph172fQcSf1fMwvwIhmfH4GWJJ+mf1HA6uXoAOVByckguXvlj8gPi7T
# 2ES6RU8+QssfqTNTJKjsBheWKWv2W4ESVen2L7lCz7i79FhA+0kp0yXJnYwdzWS0
# ovTINULINmzVyMcSUm5WuVf8YZ33cAud2Opr6N1+RuLZDavDvjiehlI5dH+GEy56
# MYIHQzCCBz8CAQEweDBhMQswCQYDVQQGEwJVUzEeMBwGA1UEChMVTWljcm9zb2Z0
# IENvcnBvcmF0aW9uMTIwMAYDVQQDEylNaWNyb3NvZnQgUHVibGljIFJTQSBUaW1l
# c3RhbXBpbmcgQ0EgMjAyMAITMwAAAFhlzes/odf80gAAAAAAWDANBglghkgBZQME
# AgEFAKCCBJwwEQYLKoZIhvcNAQkQAg8xAgUAMBoGCSqGSIb3DQEJAzENBgsqhkiG
# 9w0BCRABBDAcBgkqhkiG9w0BCQUxDxcNMjYwOTE1MDAwNjEzWjAvBgkqhkiG9w0B
# CQQxIgQgF0klECA6btqc6QvnVP/QuuKaAMSvG33iQNknrr14YgEwgbkGCyqGSIb3
# DQEJEAIvMYGpMIGmMIGjMIGgBCDFIlS7sgfQ+wAo1cWbWz+WN69VBds58hbran91
# 9aLocTB8MGWkYzBhMQswCQYDVQQGEwJVUzEeMBwGA1UEChMVTWljcm9zb2Z0IENv
# cnBvcmF0aW9uMTIwMAYDVQQDEylNaWNyb3NvZnQgUHVibGljIFJTQSBUaW1lc3Rh
# bXBpbmcgQ0EgMjAyMAITMwAAAFhlzes/odf80gAAAAAAWDCCA14GCyqGSIb3DQEJ
# EAISMYIDTTCCA0mhggNFMIIDQTCCAikCAQEwggEJoYHhpIHeMIHbMQswCQYDVQQG
# EwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQMA4GA1UEBxMHUmVkbW9uZDEeMBwG
# A1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMSUwIwYDVQQLExxNaWNyb3NvZnQg
# QW1lcmljYSBPcGVyYXRpb25zMScwJQYDVQQLEx5uU2hpZWxkIFRTUyBFU046N0Ew
# MC0wNUUwLUQ5NDcxNTAzBgNVBAMTLE1pY3Jvc29mdCBQdWJsaWMgUlNBIFRpbWUg
# U3RhbXBpbmcgQXV0aG9yaXR5oiMKAQEwBwYFKw4DAhoDFQCdZHkb26ercF2O62vC
# dZUfUSvEXKBnMGWkYzBhMQswCQYDVQQGEwJVUzEeMBwGA1UEChMVTWljcm9zb2Z0
# IENvcnBvcmF0aW9uMTIwMAYDVQQDEylNaWNyb3NvZnQgUHVibGljIFJTQSBUaW1l
# c3RhbXBpbmcgQ0EgMjAyMDANBgkqhkiG9w0BAQsFAAIFAO5Sg0EwIhgPMjAyNjA5
# MTQxNDI5MjFaGA8yMDI2MDkxNTE0MjkyMVowdDA6BgorBgEEAYRZCgQBMSwwKjAK
# AgUA7lKDQQIBADAHAgEAAgIJHDAHAgEAAgITPDAKAgUA7lPUwQIBADA2BgorBgEE
# AYRZCgQCMSgwJjAMBgorBgEEAYRZCgMCoAowCAIBAAIDB6EgoQowCAIBAAIDAYag
# MA0GCSqGSIb3DQEBCwUAA4IBAQAHQ8xMyZB3VY62MDeNcAJmJtY4krAkd8rUrI6Z
# okVL8vFbSRcM+DWVPxjM021K6adIdthsnXFVN2y4bVCo324hwmuaCEePTlmFJlIr
# OJ4WHMJuT8mlsC3UmOo6T8cs55Rwtn1Jzqt8TwX3KGLrLFl8Z/uf/522QB7tRMYq
# i2Wl0hV9jovS3jxjLiSF6SBFX0EckOHNhkQ01Yju8Aqktui/RH8+ukC9KChhsnlc
# od9kbkwVLQH9N7VthVmtglVVqASUAYhQ5hg3vIByH+tWjE6ibyRVK+pfhSbo7qEm
# uU94l5MUixsqCt8bU16ClX4xBM2OpO9mlw4hlB4vse3dcNzZMA0GCSqGSIb3DQEB
# AQUABIICAC7foaQ4A9/cq5zbkV8EazOQB4zc8Qaofm7y8s8rIqyP99IpTMVDAl2d
# es38xP3gD9ncBjVGLliesvXEOWpr9AvEvK//tIsKGYjNprOqz2jwTLFgTggNamK/
# JO4mmJ1xLnBLrOYUxqBNHTwkeXUzhWsx7+d5B+/3ltPiI6KpB3d3YtElC9iysFTe
# Qxc23C7aFGmgcErmqBo7F1w/ZQXqtzUdXAw6yQYwmde41n3eGbUOhlmYD+pxJkVV
# PACrY7wIf0CmWAfwHIvTVKk8+SsKVNsIr7LcQikR74yaTpciV+IccXOnpuKcjQau
# Cm35trKTJqz3iSgV3n/m7fbIxeSfNpMf3abREb+HZu4VnZLwBZjisQd8O7gtAc2V
# 4YoUEXMtIwudRUVbxm8lKj1IOBNokzCgz33oqSEznirj9In0GeB3+MFz/50tpVUU
# qbvqkf3MAY+v2RwXpzNRjFoMmo6Nbzji7QDpxL9f+RUBN1Ad0x0ZNTc1Mxd0nk0u
# m8rBRRq/ziFVe80r8Mrdpnd6WTlINhgD65aI2YuUuuSvlJ1ppzsmCKUe5IXZIxyH
# i2PWgzrB+AD2wqyUS4zZ5MdCa2U3iDgg/PDeb1L4v1K3ZhxL4q3LjZwK7Rv0QXf2
# 7mvOjhKM+tUi8VUuZR1reYs2uoK5vEbrHZ65bqjnCPq5SBgMlFWf
# SIG # End signature block
