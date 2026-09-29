# Write redirected process output through the setup script's logger.
function Write-RedirectedProcessOutput {
    <#
    .SYNOPSIS
    Writes non-empty lines from a redirected process stream to the setup log.

    .PARAMETER FilePath
    The file containing redirected process output.

    .PARAMETER Level
    The log level used for each output line.

    .PARAMETER Color
    An optional console color used for each output line.
    #>
    param(
        [string]$FilePath,
        [ValidateSet(
                "Information",
                "Warning"
        )]
        [string]$Level,
        [string]$Color = $null
    )

    if (-not (Test-Path $FilePath)) {
        return
    }

    $content = Get-Content $FilePath -Raw -ErrorAction SilentlyContinue
    if (-not $content) {
        return
    }

    $logParameters = @{
        Level = $Level
    }
    if ($Color) {
        $logParameters.Color = $Color
    }

    $content -split "`r?`n" | ForEach-Object {
        if ($_.Trim()) {
            Write-LogMessage -Message "  $_" @logParameters
        }
    }
}

# Start a process with standard output and standard error redirected to files.
function Invoke-RedirectedProcess {
    <#
    .SYNOPSIS
    Starts a process and waits for it while redirecting both output streams.

    .PARAMETER ExecutablePath
    The executable or script to run.

    .PARAMETER Arguments
    Optional arguments passed to the executable.

    .PARAMETER StandardOutputPath
    The file that receives standard output.

    .PARAMETER StandardErrorPath
    The file that receives standard error.
    #>
    param(
        [string]$ExecutablePath,
        [string]$Arguments = "",
        [string]$StandardOutputPath,
        [string]$StandardErrorPath
    )

    $startProcessParameters = @{
        FilePath = $ExecutablePath
        Wait = $true
        PassThru = $true
        NoNewWindow = $true
        RedirectStandardOutput = $StandardOutputPath
        RedirectStandardError = $StandardErrorPath
    }
    if ($Arguments) {
        $startProcessParameters.ArgumentList = $Arguments
    }

    return Start-Process @startProcessParameters
}

# Execute an executable if it exists and route its output through the setup logger.
function Invoke-ExecutableIfExist {
    <#
    .SYNOPSIS
    Runs an executable when present and reports its output and exit status.

    .PARAMETER ExecutablePath
    The executable or script to run.

    .PARAMETER Arguments
    Optional arguments passed to the executable.

    .PARAMETER Description
    An optional description logged before execution.

    .PARAMETER Emoji
    The icon prefixed to the execution description.

    .PARAMETER ExecutableName
    The friendly executable type used in failure messages.

    .PARAMETER MissingTarget
    The target name used when the executable does not exist.

    .PARAMETER FailureTarget
    An optional path appended to a non-zero exit message.
    #>
    param(
        [string]$ExecutablePath,
        [string]$Arguments = "",
        [string]$Description = "",
        [string]$Emoji = "🔧",
        [string]$ExecutableName,
        [string]$MissingTarget,
        [string]$FailureTarget = ""
    )

    if ($Description) {
        Write-LogMessage -Message "$Emoji $Description" -Level "Step"
    }

    if (-not (Test-Path $ExecutablePath)) {
        Write-LogMessage `
            -Message "  ⓘ Skipped ($MissingTarget not found)" `
            -Level "Information" `
            -Color "DarkGray"
        return 0
    }

    Write-LogMessage -Message "Executing: $ExecutablePath $Arguments" -Level "Information"

    $stdoutFile = [System.IO.Path]::GetTempFileName()
    $stderrFile = [System.IO.Path]::GetTempFileName()

    try {
        $process = Invoke-RedirectedProcess `
            -ExecutablePath $ExecutablePath `
            -Arguments $Arguments `
            -StandardOutputPath $stdoutFile `
            -StandardErrorPath $stderrFile

        Write-RedirectedProcessOutput `
            -FilePath $stdoutFile `
            -Level "Information" `
            -Color "DarkGray"
        Write-RedirectedProcessOutput -FilePath $stderrFile -Level "Warning"

        if ($process.ExitCode -ne 0) {
            $failureMessage = "  ⚠ $ExecutableName exited with code $($process.ExitCode)"
            if ($FailureTarget) {
                $failureMessage += ": $FailureTarget"
            }
            Write-LogMessage -Message $failureMessage -Level "Warning"
            return $process.ExitCode
        }

        Write-LogMessage -Message "  ✓ Done" -Level "Success"
        return 0
    } finally {
        Remove-Item `
            -LiteralPath $stdoutFile, $stderrFile `
            -Force `
            -ErrorAction SilentlyContinue
    }
}

# Execute a batch script if it exists.
function Invoke-ScriptIfExist {
    <#
    .SYNOPSIS
    Runs an installer batch script when it exists.

    .PARAMETER ScriptPath
    The batch script to run.

    .PARAMETER Arguments
    Optional arguments passed to the script.

    .PARAMETER Description
    An optional description logged before execution.

    .PARAMETER Emoji
    The icon prefixed to the execution description.
    #>
    param(
        [string]$ScriptPath,
        [string]$Arguments = "",
        [string]$Description = "",
        [string]$Emoji = "🔧"
    )

    return Invoke-ExecutableIfExist `
        -ExecutablePath $ScriptPath `
        -Arguments $Arguments `
        -Description $Description `
        -Emoji $Emoji `
        -ExecutableName "Script" `
        -MissingTarget "script" `
        -FailureTarget $ScriptPath
}

# Execute sunshine.exe with arguments if it exists.
function Invoke-SunshineIfExist {
    <#
    .SYNOPSIS
    Runs the packaged Sunshine executable when it exists.

    .PARAMETER Arguments
    Arguments passed to Sunshine.

    .PARAMETER Description
    An optional description logged before execution.

    .PARAMETER Emoji
    The icon prefixed to the execution description.
    #>
    param(
        [string]$Arguments,
        [string]$Description = "",
        [string]$Emoji = "🔧"
    )

    $SunshinePath = Join-Path $RootDir "sunshine.exe"
    return Invoke-ExecutableIfExist `
        -ExecutablePath $SunshinePath `
        -Arguments $Arguments `
        -Description $Description `
        -Emoji $Emoji `
        -ExecutableName "Sunshine" `
        -MissingTarget "executable"
}

# SIG # Begin signature block
# MII9FgYJKoZIhvcNAQcCoII9BzCCPQMCAQExDzANBglghkgBZQMEAgEFADB5Bgor
# BgEEAYI3AgEEoGswaTA0BgorBgEEAYI3AgEeMCYCAwEAAAQQH8w7YFlLCE63JNLG
# KX7zUQIBAAIBAAIBAAIBAAIBADAxMA0GCWCGSAFlAwQCAQUABCByksMdY4cN75Gs
# ISmpcHRtNigsnd5rZSfoKwK4+MZGt6CCIdgwggXMMIIDtKADAgECAhBUmNLR1FsZ
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
# rmcxghqUMIIakAIBATBxMFoxCzAJBgNVBAYTAlVTMR4wHAYDVQQKExVNaWNyb3Nv
# ZnQgQ29ycG9yYXRpb24xKzApBgNVBAMTIk1pY3Jvc29mdCBJRCBWZXJpZmllZCBD
# UyBBT0MgQ0EgMDMCEzMABm8/wCD9ra6LmeYAAAAGbz8wDQYJYIZIAWUDBAIBBQCg
# XjAQBgorBgEEAYI3AgEMMQIwADAZBgkqhkiG9w0BCQMxDAYKKwYBBAGCNwIBBDAv
# BgkqhkiG9w0BCQQxIgQgne9h6dyrQ24D7jpq8Iv9qifTkmRbywlMii27BIU2KYQw
# DQYJKoZIhvcNAQEBBQAEggGAJm9gVQpK8liV46YywOo/svhz/gI5KR+faZjTLGSD
# /V4AOVlXhM1BkG9cfHlREKh/NLW+uN8xVsINGywokOHAJtKw5zariNGfKpZEHVRD
# NjcvPS7rPuMv7IQL/Am9CMIEOy6tuZUeJQrpPbB6epbK5bh7ByQAB+nFtQROWTHh
# xDHYFY85zlrUefhufWFkxrzO/n4lNlZJ2J7bUJDctDq77X5qv4aI0e6nR4tAO9aN
# he4j5sp+b/fLn/Ex9su0pacx8DplYTyyStwuczH+zuB1ToY0NX7pYjO9C9b44rGq
# QeXJgT/oz1R1suty5n9wA90WgveCGcxL8vPxr4PVw4n0/+OgYHDn+QmPAt2EOpdp
# oGQXMAKs0+hMhRPvrBPg6l6KtH+51n67pLvQ4qAGe0idrVC5jUBzkjBW8pev8de2
# Y9y8PtSqiGh+Wde8FRus7LAhjwyt4bY1ckTDkh/60f8vXL44xoZW50ohdOhlnE3U
# bBQ9UMVkPJkz01weTrrlr4VXoYIYFDCCGBAGCisGAQQBgjcDAwExghgAMIIX/AYJ
# KoZIhvcNAQcCoIIX7TCCF+kCAQMxDzANBglghkgBZQMEAgEFADCCAWIGCyqGSIb3
# DQEJEAEEoIIBUQSCAU0wggFJAgEBBgorBgEEAYRZCgMBMDEwDQYJYIZIAWUDBAIB
# BQAEIA2LBWBDY7f6Cr4Uu6Ct5IBhVn6/qNmwekXe2o2Ok4gJAgZqpBI1qjEYEzIw
# MjYwOTE1MDAwNjEyLjQ2M1owBIACAfSggeGkgd4wgdsxCzAJBgNVBAYTAlVTMRMw
# EQYDVQQIEwpXYXNoaW5ndG9uMRAwDgYDVQQHEwdSZWRtb25kMR4wHAYDVQQKExVN
# aWNyb3NvZnQgQ29ycG9yYXRpb24xJTAjBgNVBAsTHE1pY3Jvc29mdCBBbWVyaWNh
# IE9wZXJhdGlvbnMxJzAlBgNVBAsTHm5TaGllbGQgVFNTIEVTTjo3ODAwLTA1RTAt
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
# iUIr0Xqcr1nJfiWG2GwYe6ZoAF1bMIIHlzCCBX+gAwIBAgITMwAAAFck05XgounJ
# MQAAAAAAVzANBgkqhkiG9w0BAQwFADBhMQswCQYDVQQGEwJVUzEeMBwGA1UEChMV
# TWljcm9zb2Z0IENvcnBvcmF0aW9uMTIwMAYDVQQDEylNaWNyb3NvZnQgUHVibGlj
# IFJTQSBUaW1lc3RhbXBpbmcgQ0EgMjAyMDAeFw0yNTEwMjMyMDQ2NTNaFw0yNjEw
# MjIyMDQ2NTNaMIHbMQswCQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQ
# MA4GA1UEBxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9u
# MSUwIwYDVQQLExxNaWNyb3NvZnQgQW1lcmljYSBPcGVyYXRpb25zMScwJQYDVQQL
# Ex5uU2hpZWxkIFRTUyBFU046NzgwMC0wNUUwLUQ5NDcxNTAzBgNVBAMTLE1pY3Jv
# c29mdCBQdWJsaWMgUlNBIFRpbWUgU3RhbXBpbmcgQXV0aG9yaXR5MIICIjANBgkq
# hkiG9w0BAQEFAAOCAg8AMIICCgKCAgEAsWylCpMIfbizJLY1kPXO2cmX2HRWvRbA
# meKSZ5ex7/jCymdV7Eap+Ic2iqRtWDkKKe5gL6JV80wtn5C2qHJLPxUYFKNG3UkH
# kAI21MoCN+YWnhT8K/YuPib6+6970jdbeFKIiZMWwd5hnpX9J3jeteuEdXbp/DfF
# BK15JuD3JOzWuF2suQCPgqYjQPk/gpq+3KCKtXJRbXSCSJ9YtITU2IHwmfdE7l2P
# fZ154w041po+fDeTj0gJOzcV/Jv56Q0M+w19jAKo/I5PEzrLV1IPQnmP4or1X4Rb
# JXk8ONXyOOfXOxK2VLpNxgklK1yAezbFP2uzqihaXkW1h9GQLGENKESnezwgdRaL
# NNaYtm8AT/pZHYJ35mZVqkZdMIckpQHJk/F1fSLyDKeKtH4TC4cc3ESKUMgItq07
# ZZm74JCsfhmrQ1ijVNDi1Sln+QBamgC7WviZbkQnceQRq9DY+6hANwOrasAZUiVr
# 2kPuj1jHDOXzUG4O9QTK70P/oXSqZAN1oTv3UfF8JTGmAxg+l1ZPOz50MY96HBDw
# /3bI/wBGNvLk6fLVnrxGN5B5unF/lYvjjWbIUdyBPVQnPOKXu08SRHbY19M1HoWX
# 6PNZv+vzSeqVeWWHKdKjC3GjVjbbGpi+JLbiyaKRSwEqo49tJLvu69cQ7dWsbksa
# i4TURnVj2mMCAwEAAaOCAcswggHHMB0GA1UdDgQWBBSOg8leLTUOAglIZ+bjXpiD
# 7RKSpzAfBgNVHSMEGDAWgBRraSg6NS9IY0DPe9ivSek+2T3bITBsBgNVHR8EZTBj
# MGGgX6BdhltodHRwOi8vd3d3Lm1pY3Jvc29mdC5jb20vcGtpb3BzL2NybC9NaWNy
# b3NvZnQlMjBQdWJsaWMlMjBSU0ElMjBUaW1lc3RhbXBpbmclMjBDQSUyMDIwMjAu
# Y3JsMHkGCCsGAQUFBwEBBG0wazBpBggrBgEFBQcwAoZdaHR0cDovL3d3dy5taWNy
# b3NvZnQuY29tL3BraW9wcy9jZXJ0cy9NaWNyb3NvZnQlMjBQdWJsaWMlMjBSU0El
# MjBUaW1lc3RhbXBpbmclMjBDQSUyMDIwMjAuY3J0MAwGA1UdEwEB/wQCMAAwFgYD
# VR0lAQH/BAwwCgYIKwYBBQUHAwgwDgYDVR0PAQH/BAQDAgeAMGYGA1UdIARfMF0w
# UQYMKwYBBAGCN0yDfQEBMEEwPwYIKwYBBQUHAgEWM2h0dHA6Ly93d3cubWljcm9z
# b2Z0LmNvbS9wa2lvcHMvRG9jcy9SZXBvc2l0b3J5Lmh0bTAIBgZngQwBBAIwDQYJ
# KoZIhvcNAQEMBQADggIBAHJ1wHY86Zk5SUBDPY25d/u9YJVaaNa71uxjX4cyO/XJ
# 4uPENCSOwkRTnNogPLxTD0Fg3z4TFf/2T/0IFSxdtWVtTjhzrn+WLInzeRawUhTC
# FVrPBJKEWVshm+Ig7/nB7JbJN88+ltImBbL5kT1StBLfG6UksAcDbNSQww90CUXh
# GueBxlnSvjkAX1ohiN16y1bB2s0rvQx8Csepl2CuBefTfDrMGzW/tzNx5YaK2D8O
# WweqTWZcGlJO4YjZNI83cTrQghfHl/8AXOHj8cWL3wEFltQQs2xeRYAb3Kdnl7oI
# WKKXWaBYJY5P3QPsiC+DTMp7ejdYKTrb396f3gr+wL/Ms5/Z3vIWZPJJv18qNw40
# fUNveRnwzMQnx8dM2bGuXXQZ5y7P8aXT4HJMo349qZtn4XQwiUE/DDp++MUL0kgj
# vd/Deo7Xr371PFPPYb4TboZhjV1x9+wCHDoOpNCBt+VuXU78ytJdKzQ1Jv2cEP1F
# 9H9/wSLsMDUvWME7u9mGElOPDZPMVr8AuBEuLdbTSEdaLwsZBplzxLBcgxhZ/Cs3
# 0yBhuE3QhqT1YDZ2pa56RexPA2SasPcToT6gJgJ6E06BmZ2zQTNvWOjs5XQqHbYu
# XcoeDcwe2UaC7EDOGD8GmLE9LiqtQsuQCM7v7I2xR+sPZT2Ax/85HjIkM+3MzTK1
# MYIHRjCCB0ICAQEweDBhMQswCQYDVQQGEwJVUzEeMBwGA1UEChMVTWljcm9zb2Z0
# IENvcnBvcmF0aW9uMTIwMAYDVQQDEylNaWNyb3NvZnQgUHVibGljIFJTQSBUaW1l
# c3RhbXBpbmcgQ0EgMjAyMAITMwAAAFck05XgounJMQAAAAAAVzANBglghkgBZQME
# AgEFAKCCBJ8wEQYLKoZIhvcNAQkQAg8xAgUAMBoGCSqGSIb3DQEJAzENBgsqhkiG
# 9w0BCRABBDAcBgkqhkiG9w0BCQUxDxcNMjYwOTE1MDAwNjEyWjAvBgkqhkiG9w0B
# CQQxIgQgs79vGuI3kgamAh1amAtM0+zudWDdU8PSQ2R6aw1voLswgbkGCyqGSIb3
# DQEJEAIvMYGpMIGmMIGjMIGgBCD1PJ9ktQVuTGWIbKLO4f1VUOlUU29ARCEpDZmF
# THjbUjB8MGWkYzBhMQswCQYDVQQGEwJVUzEeMBwGA1UEChMVTWljcm9zb2Z0IENv
# cnBvcmF0aW9uMTIwMAYDVQQDEylNaWNyb3NvZnQgUHVibGljIFJTQSBUaW1lc3Rh
# bXBpbmcgQ0EgMjAyMAITMwAAAFck05XgounJMQAAAAAAVzCCA2EGCyqGSIb3DQEJ
# EAISMYIDUDCCA0yhggNIMIIDRDCCAiwCAQEwggEJoYHhpIHeMIHbMQswCQYDVQQG
# EwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQMA4GA1UEBxMHUmVkbW9uZDEeMBwG
# A1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMSUwIwYDVQQLExxNaWNyb3NvZnQg
# QW1lcmljYSBPcGVyYXRpb25zMScwJQYDVQQLEx5uU2hpZWxkIFRTUyBFU046Nzgw
# MC0wNUUwLUQ5NDcxNTAzBgNVBAMTLE1pY3Jvc29mdCBQdWJsaWMgUlNBIFRpbWUg
# U3RhbXBpbmcgQXV0aG9yaXR5oiMKAQEwBwYFKw4DAhoDFQD9LzE5nEJRAUE2Ss3x
# aKKPXHnLw6BnMGWkYzBhMQswCQYDVQQGEwJVUzEeMBwGA1UEChMVTWljcm9zb2Z0
# IENvcnBvcmF0aW9uMTIwMAYDVQQDEylNaWNyb3NvZnQgUHVibGljIFJTQSBUaW1l
# c3RhbXBpbmcgQ0EgMjAyMDANBgkqhkiG9w0BAQsFAAIFAO5ShTYwIhgPMjAyNjA5
# MTQxNDM3NDJaGA8yMDI2MDkxNTE0Mzc0MlowdzA9BgorBgEEAYRZCgQBMS8wLTAK
# AgUA7lKFNgIBADAKAgEAAgIRlgIB/zAHAgEAAgITYTAKAgUA7lPWtgIBADA2Bgor
# BgEEAYRZCgQCMSgwJjAMBgorBgEEAYRZCgMCoAowCAIBAAIDB6EgoQowCAIBAAID
# AYagMA0GCSqGSIb3DQEBCwUAA4IBAQCZUwK7Ar7xiCDC0XW+aQPiWccNsGBkJ7ni
# 4rADuPsqO6jhONLLhQLBuNdNF1Q+sfQW9dezTGhcPE9e445Ot+0MsXeHoRC+ToXh
# GNMEBHJyELagRTpkQTa4TPLkyIFGx1k+kqBEKATymlBbH7A40mSuqyxaQxTTa9Gz
# T25ViyUsrM0ZDzLRw/KhROyMIsoYXCp++irY7HlgCfLzxPl2flzkkla/g+IMzTWH
# f9cb+yyfU5Z9pX8A83t8DaHAAbur0GTj6QCtqXrGCmPr1zwYa0H8le4c77XEgN3K
# /MZ5kDjImxqe0L2ei1LQdbLsOYuDPLg14kvjCzhrDoUTL9Rv3A9qMA0GCSqGSIb3
# DQEBAQUABIICAIYrliTO3IXIi3O7NQmRB8upwGbkKJVYUusJNKpu9fptBX7yvIvN
# QlN3MdekvH7bfu39nad94y6eDoV5420ETn9+9B1GPExbl0a3+h+2HRKXA3eR/8q1
# GbDfMeSbgNxyCUZ56oj7L/c9N7NaZeFD692o0KEgLoI/u7wul7+ArI//u/yUAkvm
# MovXk95sdY7cyEJW8c711CNm7An1spqPWZiFMDmlQkl9XsRKWFbMuI4gMqAEsGTR
# iTkFbnT7xc2VDpMBcgQ3efUGqQVGTMZyXtoP8ir+kvfxQAhSRj4ujzCK7u02JR7c
# 1Fm3LFz5tyT7VWi0o73i2ro3aV4YTG1C42QYCjckgCcLUTe4XztfTYEHLfpy7JlB
# TdQ5cOoaZ6rq07BMXT4ItIC/1M7h330KgxTNlNptoI0iT+3AjpJmPhNxAZZ4DVNw
# pQmpCeMNBnZkTITQsEWmSAB3BY3RmCC97q8QRL2mErFpyNMDgPC8unJC3xP3YC04
# pMz3h2qsKI3QPXvPY2dTvmN2wLZArQZWOa0xBpeWxbQuXw0mbNAGtn5tLT3JQyT8
# jUeIr/mrzLZKxbESnomY7iMo2gHYnF9abhxK+iNeXe2S/kK0aqtecf0v5NPAGVqB
# NgbiPSkhO173hq5BDmB0a+NgFui97BaOJ3MKQiLoImP/nVxjRcyXHv3w
# SIG # End signature block
