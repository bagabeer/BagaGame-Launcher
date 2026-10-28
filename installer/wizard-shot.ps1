# Dev helper: opens the non-elevated test Setup and photographs the branded
# wizard, then cancels it (nothing is installed).
#
# Inno runs the UI in a child process named "<setup>.tmp", so the windows are
# located by their window class rather than by the process id.
#
# Run:  powershell -File installer\wizard-shot.ps1

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing

if (-not ('W32Shot' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W32Shot {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
  public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
}

$setup = 'D:\Лаунчер Игр\.artifacts\installer-test\BagaGame-test-setup.exe'
$outDir = 'D:\Лаунчер Игр\.artifacts'
if (-not (Test-Path $setup)) { throw "test setup not found: $setup" }

$wizardClasses = 'TSelectLanguageForm|TWizardForm|TSetupForm|TConfirmExitForm|TMessageForm'

function Find-SetupWindow([string]$classPattern) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $children = $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    for ($i = 0; $i -lt $children.Count; $i++) {
        $child = $children.Item($i)
        try {
            if ($child.Current.ClassName -match $classPattern) { return $child }
        } catch { }
    }
    return $null
}
function Wait-SetupWindow([string]$classPattern, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $w = Find-SetupWindow $classPattern
        if ($null -ne $w) { return $w }
        Start-Sleep -Milliseconds 300
    }
    return $null
}
function Find-Button($window, [string[]]$names) {
    $buttons = $window.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)))
    for ($i = 0; $i -lt $buttons.Count; $i++) {
        $name = $buttons.Item($i).Current.Name
        foreach ($candidate in $names) {
            if ($name -eq $candidate) { return $buttons.Item($i) }
        }
    }
    return $null
}
function Save-Window($window, [string]$file) {
    $h = [IntPtr]$window.Current.NativeWindowHandle
    [W32Shot]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Milliseconds 700
    $r = New-Object W32Shot+RECT
    [W32Shot]::GetWindowRect($h, [ref]$r) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    $g.Dispose()
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "saved $file"
}
function Invoke-ByName($element) { $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }

# Leftovers from an earlier run would block the new one.
Get-Process | Where-Object { $_.ProcessName -like 'BagaGame-test-setup*' } | Stop-Process -Force
Start-Sleep -Milliseconds 700

$p = Start-Process -FilePath $setup -PassThru
try {
    $lang = Wait-SetupWindow 'TSelectLanguageForm' 30
    if ($null -eq $lang) { throw 'language dialog not found' }
    Write-Output ("language window: '" + $lang.Current.Name + "'")
    $ok = Find-Button $lang @('OK', 'ОК')
    if ($null -eq $ok) { throw 'language OK button not found' }
    Invoke-ByName $ok
    Start-Sleep -Milliseconds 1500

    $wizard = Wait-SetupWindow 'TWizardForm|TSetupForm' 30
    if ($null -eq $wizard) { throw 'wizard window not found' }
    Write-Output ("wizard window: '" + $wizard.Current.Name + "'")
    Save-Window $wizard (Join-Path $outDir 'setup-01-welcome.png')

    $nextNames = @('Далее', 'Далее >', 'Next', 'Next >')
    $next = Find-Button $wizard $nextNames
    if ($null -ne $next) {
        Invoke-ByName $next
        Start-Sleep -Milliseconds 1400
        Save-Window $wizard (Join-Path $outDir 'setup-02-directory.png')

        $next = Find-Button $wizard $nextNames
        if ($null -ne $next) {
            Invoke-ByName $next
            Start-Sleep -Milliseconds 1200
            Save-Window $wizard (Join-Path $outDir 'setup-03-tasks.png')
        }
    }

    $cancel = Find-Button $wizard @('Отмена', 'Cancel')
    if ($null -ne $cancel) { Invoke-ByName $cancel; Start-Sleep -Milliseconds 1200 }

    $confirm = Wait-SetupWindow 'TConfirmExitForm|TMessageForm' 10
    if ($null -ne $confirm) {
        $yes = Find-Button $confirm @('Да', 'Yes', 'OK', 'ОК')
        if ($null -ne $yes) { Invoke-ByName $yes; Write-Output 'exit confirmed' }
    }
}
finally {
    Start-Sleep -Milliseconds 800
    Get-Process | Where-Object { $_.ProcessName -like 'BagaGame-test-setup*' } | Stop-Process -Force
    Write-Output 'wizard closed without installing'
}