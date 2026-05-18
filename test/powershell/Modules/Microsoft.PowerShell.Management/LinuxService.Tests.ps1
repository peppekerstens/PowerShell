# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

# Tests for the Linux D-Bus implementation of the *-Service cmdlets.
# These tests are skipped on Windows and macOS; they require systemd to be
# running (i.e. a full Linux distro — not Alpine/musl with OpenRC).

Describe "Linux Service cmdlet tests" -Tags "CI", "RequireAdminOnLinux" {

    BeforeAll {
        $originalDefaultParameterValues = $PSDefaultParameterValues.Clone()

        # Skip entirely on non-Linux, or if systemd is not pid 1
        if (-not $IsLinux) {
            $PSDefaultParameterValues["it:skip"] = $true
        }
        else {
            # Check systemd is the init — if not (e.g. Alpine/OpenRC) skip too
            $init = (Get-Item /proc/1/exe -ErrorAction SilentlyContinue)?.Target
            if ($init -notmatch 'systemd') {
                $PSDefaultParameterValues["it:skip"] = $true
            }
        }

        # A well-known service that ships on every systemd distro and is safe to
        # query. We do NOT start/stop it in CI — read-only tests only unless the
        # suite is run as root (RequireAdminOnLinux tag).
        $script:knownService = 'dbus'
    }

    AfterAll {
        $global:PSDefaultParameterValues = $originalDefaultParameterValues
    }

    # -------------------------------------------------------------------------
    # Get-Service
    # -------------------------------------------------------------------------

    Context "Get-Service" {

        It "Returns results without -Name (list all)" {
            $svcs = Get-Service
            $svcs | Should -Not -BeNullOrEmpty
        }

        It "Returns a LinuxServiceController object" {
            $svc = Get-Service -Name $script:knownService | Select-Object -First 1
            $svc | Should -Not -BeNullOrEmpty
            $svc.GetType().Name | Should -BeExactly 'LinuxServiceController'
        }

        It "Result has expected properties" {
            $svc = Get-Service -Name $script:knownService | Select-Object -First 1
            $svc.Name        | Should -Not -BeNullOrEmpty
            $svc.DisplayName | Should -Not -BeNullOrEmpty
            $svc.Status      | Should -Not -BeNullOrEmpty
            $svc.StartType   | Should -Not -BeNullOrEmpty
        }

        It "Accepts .service suffix transparently" {
            $a = Get-Service -Name $script:knownService
            $b = Get-Service -Name "$($script:knownService).service"
            $a.Name | Should -BeExactly $b.Name
        }

        It "Wildcard pattern returns subset" {
            $svcs = Get-Service -Name 'd*'
            $svcs | Should -Not -BeNullOrEmpty
            $svcs | ForEach-Object { $_.Name | Should -Match '^d' }
        }

        It "Non-existent service returns no output (not an error)" {
            $result = Get-Service -Name "nonexistent-$(New-Guid).service" -ErrorAction SilentlyContinue
            $result | Should -BeNullOrEmpty
        }

        It "Accepts pipeline input of LinuxServiceController objects" {
            $svc = Get-Service -Name $script:knownService | Select-Object -First 1
            $svc2 = $svc | Get-Service
            $svc2.Name | Should -BeExactly $svc.Name
        }

        It "Throws on null -Name" {
            { Get-Service -Name $null -ErrorAction Stop } |
                Should -Throw -ErrorId 'ParameterArgumentValidationError,Microsoft.Powershell.Commands.GetServiceCommand'
        }

        It "Throws on empty -Name" {
            { Get-Service -Name '' -ErrorAction Stop } |
                Should -Throw -ErrorId 'ParameterArgumentValidationError,Microsoft.Powershell.Commands.GetServiceCommand'
        }
    }

    # -------------------------------------------------------------------------
    # Suspend-Service / Resume-Service — always unsupported on Linux
    # -------------------------------------------------------------------------

    Context "Suspend-Service and Resume-Service are not supported" {

        It "Suspend-Service writes a non-terminating PlatformNotSupportedException error" {
            $err = $null
            Suspend-Service -Name $script:knownService -ErrorAction SilentlyContinue -ErrorVariable err
            $err | Should -Not -BeNullOrEmpty
            $err[0].Exception | Should -BeOfType [System.PlatformNotSupportedException]
            $err[0].FullyQualifiedErrorId | Should -BeLike 'PlatformNotSupported*'
        }

        It "Resume-Service writes a non-terminating PlatformNotSupportedException error" {
            $err = $null
            Resume-Service -Name $script:knownService -ErrorAction SilentlyContinue -ErrorVariable err
            $err | Should -Not -BeNullOrEmpty
            $err[0].Exception | Should -BeOfType [System.PlatformNotSupportedException]
            $err[0].FullyQualifiedErrorId | Should -BeLike 'PlatformNotSupported*'
        }
    }

    # -------------------------------------------------------------------------
    # -WhatIf tests — ShouldProcess must work without D-Bus or root
    # -------------------------------------------------------------------------

    Context "-WhatIf safety" {

        It "Start-Service -WhatIf does not throw" {
            { Start-Service -Name $script:knownService -WhatIf } | Should -Not -Throw
        }

        It "Stop-Service -WhatIf does not throw" {
            { Stop-Service -Name $script:knownService -WhatIf } | Should -Not -Throw
        }

        It "Restart-Service -WhatIf does not throw" {
            { Restart-Service -Name $script:knownService -WhatIf } | Should -Not -Throw
        }

        It "Set-Service -WhatIf does not throw" {
            { Set-Service -Name $script:knownService -StartupType Manual -WhatIf } | Should -Not -Throw
        }

        It "New-Service -WhatIf does not throw" {
            { New-Service -Name pester-test -BinaryPathName '/usr/bin/true' -WhatIf } | Should -Not -Throw
        }

        It "Remove-Service -WhatIf does not throw" {
            { Remove-Service -Name pester-test -WhatIf } | Should -Not -Throw
        }
    }

    # -------------------------------------------------------------------------
    # Start/Stop/Restart-Service and Set-Service
    # Require root (RequireAdminOnLinux tag) — skipped in unprivileged CI runs
    # -------------------------------------------------------------------------

    Context "Start-Service / Stop-Service / Restart-Service (requires root)" -Skip:(-not (whoami) -eq 'root') {

        BeforeAll {
            # Use a lightweight no-op service unit created just for tests
            $script:testUnit = 'pester-noop.service'
            $unitContent = @"
[Unit]
Description=Pester no-op test service

[Service]
Type=oneshot
ExecStart=/bin/true
RemainAfterExit=yes
"@
            $unitPath = "/etc/systemd/system/$($script:testUnit)"
            $unitContent | Set-Content -Path $unitPath -Encoding utf8
            & systemctl daemon-reload
        }

        AfterAll {
            & systemctl stop  $script:testUnit 2>$null
            & systemctl disable $script:testUnit 2>$null
            Remove-Item "/etc/systemd/system/$($script:testUnit)" -Force -ErrorAction SilentlyContinue
            & systemctl daemon-reload
        }

        It "Start-Service starts the unit" {
            & systemctl stop $script:testUnit 2>$null
            Start-Service -Name $script:testUnit
            $svc = Get-Service -Name $script:testUnit
            $svc.Status | Should -BeExactly ([System.ServiceProcess.ServiceControllerStatus]::Running)
        }

        It "Start-Service -PassThru returns updated LinuxServiceController" {
            & systemctl stop $script:testUnit 2>$null
            $svc = Start-Service -Name $script:testUnit -PassThru
            $svc | Should -Not -BeNullOrEmpty
            $svc.GetType().Name | Should -BeExactly 'LinuxServiceController'
            $svc.Status | Should -BeExactly ([System.ServiceProcess.ServiceControllerStatus]::Running)
        }

        It "Stop-Service stops the unit" {
            & systemctl start $script:testUnit 2>$null
            Stop-Service -Name $script:testUnit
            $svc = Get-Service -Name $script:testUnit
            $svc.Status | Should -BeExactly ([System.ServiceProcess.ServiceControllerStatus]::Stopped)
        }

        It "Restart-Service restarts the unit" {
            & systemctl start $script:testUnit 2>$null
            Restart-Service -Name $script:testUnit
            $svc = Get-Service -Name $script:testUnit
            $svc.Status | Should -BeExactly ([System.ServiceProcess.ServiceControllerStatus]::Running)
        }

        It "Set-Service -StartupType Disabled disables the unit" {
            Set-Service -Name $script:testUnit -StartupType Disabled
            $svc = Get-Service -Name $script:testUnit
            $svc.StartType | Should -BeExactly ([Microsoft.PowerShell.Commands.ServiceStartupType]::Disabled)
        }

        It "Set-Service -StartupType Automatic enables the unit" {
            # Ensure it starts disabled
            & systemctl disable $script:testUnit 2>$null
            Set-Service -Name $script:testUnit -StartupType Automatic
            $svc = Get-Service -Name $script:testUnit
            $svc.StartType | Should -BeExactly ([Microsoft.PowerShell.Commands.ServiceStartupType]::Automatic)
        }

        It "Set-Service -Status Running starts the unit" {
            & systemctl stop $script:testUnit 2>$null
            Set-Service -Name $script:testUnit -Status Running
            $svc = Get-Service -Name $script:testUnit
            $svc.Status | Should -BeExactly ([System.ServiceProcess.ServiceControllerStatus]::Running)
        }

        It "Set-Service -Status Stopped stops the unit" {
            & systemctl start $script:testUnit 2>$null
            Set-Service -Name $script:testUnit -Status Stopped
            $svc = Get-Service -Name $script:testUnit
            $svc.Status | Should -BeExactly ([System.ServiceProcess.ServiceControllerStatus]::Stopped)
        }

        It "Set-Service -PassThru returns updated LinuxServiceController" {
            $svc = Set-Service -Name $script:testUnit -Status Running -PassThru
            $svc | Should -Not -BeNullOrEmpty
            $svc.GetType().Name | Should -BeExactly 'LinuxServiceController'
        }

        It "Pipeline input works for Start-Service" {
            & systemctl stop $script:testUnit 2>$null
            Get-Service -Name $script:testUnit | Start-Service
            $svc = Get-Service -Name $script:testUnit
            $svc.Status | Should -BeExactly ([System.ServiceProcess.ServiceControllerStatus]::Running)
        }
    }

    # -------------------------------------------------------------------------
    # New-Service / Remove-Service round-trip
    # Require root — writes to /etc/systemd/system/
    # -------------------------------------------------------------------------

    Context "New-Service / Remove-Service (requires root)" -Skip:(-not (whoami) -eq 'root') {

        BeforeAll {
            $script:newSvcUnit = 'pester-native-svc.service'
            $script:newSvcName = 'pester-native-svc'
        }

        AfterAll {
            # Clean up any leftover unit from a failed test
            & systemctl stop  $script:newSvcUnit 2>$null
            & systemctl disable $script:newSvcUnit 2>$null
            Remove-Item "/etc/systemd/system/$($script:newSvcUnit)" -Force -ErrorAction SilentlyContinue
            & systemctl daemon-reload
        }

        It "New-Service creates a unit file" {
            New-Service -Name $script:newSvcName -BinaryPathName '/usr/bin/true' -Description 'Pester native service test'
            $svc = Get-Service -Name $script:newSvcUnit -ErrorAction SilentlyContinue
            $svc | Should -Not -BeNullOrEmpty
            $svc.Name | Should -BeExactly $script:newSvcUnit
            $svc.DisplayName | Should -BeExactly 'Pester native service test'
        }

        It "New-Service returns the service object" {
            $svc = New-Service -Name 'pester-native-rt' -BinaryPathName '/usr/bin/true' -Description 'Pester round-trip'
            $svc | Should -Not -BeNullOrEmpty
            $svc.GetType().Name | Should -BeExactly 'LinuxServiceController'
            $svc.Status | Should -BeExactly ([System.ServiceProcess.ServiceControllerStatus]::Stopped)
            # Clean up
            & systemctl stop 'pester-native-rt.service' 2>$null
            & systemctl disable 'pester-native-rt.service' 2>$null
            Remove-Item "/etc/systemd/system/pester-native-rt.service" -Force -ErrorAction SilentlyContinue
            & systemctl daemon-reload
        }

        It "New-Service -StartupType Automatic enables the unit" {
            New-Service -Name 'pester-native-auto' -BinaryPathName '/usr/bin/true' -StartupType Automatic
            $svc = Get-Service -Name 'pester-native-auto.service'
            $svc.StartType | Should -BeExactly ([Microsoft.PowerShell.Commands.ServiceStartupType]::Automatic)
            # Clean up
            & systemctl stop 'pester-native-auto.service' 2>$null
            & systemctl disable 'pester-native-auto.service' 2>$null
            Remove-Item "/etc/systemd/system/pester-native-auto.service" -Force -ErrorAction SilentlyContinue
            & systemctl daemon-reload
        }

        It "Remove-Service stops, disables, and deletes the unit file" {
            # Recreate the service for Remove-Service to act on
            New-Service -Name $script:newSvcName -BinaryPathName '/usr/bin/true'
            $svc = Get-Service -Name $script:newSvcUnit -ErrorAction SilentlyContinue
            $svc | Should -Not -BeNullOrEmpty

            Remove-Service -Name $script:newSvcName
            $svc = Get-Service -Name $script:newSvcUnit -ErrorAction SilentlyContinue
            $svc | Should -BeNullOrEmpty

            Test-Path "/etc/systemd/system/$($script:newSvcUnit)" | Should -BeFalse
        }

        It "Remove-Service accepts pipeline input" {
            New-Service -Name 'pester-native-pipe' -BinaryPathName '/usr/bin/true'
            Get-Service -Name 'pester-native-pipe.service' | Remove-Service
            $svc = Get-Service -Name 'pester-native-pipe.service' -ErrorAction SilentlyContinue
            $svc | Should -BeNullOrEmpty
        }
    }
}
