# UI behaviour regression harness for ClipBar.
# Runs against an isolated --data-dir; kills and restarts the app. ASCII only.
param(
    [string]$Exe = 'E:\AI\Zcode\projects\clipbar\dist\ClipBar.exe',
    [string]$DataDir = 'E:\AI\Zcode\tmp\uiregress\data'
)
$ErrorActionPreference = 'Continue'
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class UI {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int d, UIntPtr e);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
[UI]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing

$script:pass = 0
$script:fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Output ("PASS  " + $name + $(if ($detail) { "  [" + $detail + "]" } else { '' })) }
    else { $script:fail++; Write-Output ("FAIL  " + $name + $(if ($detail) { "  [" + $detail + "]" } else { '' })) }
}
function Stop-App { Get-Process ClipBar -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 700 }
function Start-App { Start-Process -FilePath $Exe -ArgumentList "--data-dir `"$DataDir`""; Start-Sleep -Milliseconds 2500 }
function Hotkey {
    [UI]::keybd_event(0x5B,0,0,[UIntPtr]::Zero); [UI]::keybd_event(0x12,0,0,[UIntPtr]::Zero); [UI]::keybd_event(0x56,0,0,[UIntPtr]::Zero)
    Start-Sleep -Milliseconds 50
    [UI]::keybd_event(0x56,0,2,[UIntPtr]::Zero); [UI]::keybd_event(0x12,0,2,[UIntPtr]::Zero); [UI]::keybd_event(0x5B,0,2,[UIntPtr]::Zero)
    Start-Sleep -Milliseconds 900
}
function AppProc { return Get-Process ClipBar -ErrorAction SilentlyContinue | Sort-Object StartTime | Select-Object -First 1 }
function WindowsOf([int]$pidValue) {
    $script:wins = @()
    $cb = [UI+EnumWindowsProc]{
        param($h, $l)
        if (-not [UI]::IsWindowVisible($h)) { return $true }
        [uint32]$wp = 0; [UI]::GetWindowThreadProcessId($h, [ref]$wp) | Out-Null
        if ($wp -ne $pidValue) { return $true }
        $r = New-Object UI+RECT; [UI]::GetWindowRect($h, [ref]$r) | Out-Null
        $script:wins += [pscustomobject]@{ H=$h; L=$r.Left; T=$r.Top; W=$r.Right-$r.Left; Ht=$r.Bottom-$r.Top }
        return $true
    }
    [UI]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
    return $script:wins
}
function PanelWin { $p = AppProc; if (-not $p) { return $null }; WindowsOf $p.Id | Where-Object { $_.W -gt 300 -and $_.Ht -gt 300 } | Select-Object -First 1 }
function MenuCount { $p = AppProc; if (-not $p) { return -1 }; @(WindowsOf $p.Id | Where-Object { $_.W -ge 100 -and $_.W -le 400 -and $_.Ht -ge 24 -and $_.Ht -le 500 }).Count }
function PanelEl {
    $p = AppProc; if (-not $p) { return $null }
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$p.Id)
    $panel = PanelWin
    if ($null -eq $panel) { return $null }
    foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
        $r = $w.Current.BoundingRectangle
        if ([math]::Abs($r.Width - $panel.W) -le 4 -and [math]::Abs($r.Height - $panel.Ht) -le 4) { return $w }
    }
    return $null
}
function Rows {
    $el = PanelEl; if ($null -eq $el) { return @() }
    $ic = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $rows = @()
    foreach ($i in $el.FindAll([System.Windows.Automation.TreeScope]::Descendants, $ic)) { if ($i.Current.Name -match 'ClipItemViewModel') { $rows += $i } }
    return $rows
}
function SelectionFlags { return (@(Rows | ForEach-Object { $_.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected }) -join ',') }
function ButtonNames {
    $el = PanelEl; $names = @()
    if ($null -eq $el) { return $names }
    $p = AppProc
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$p.Id)
    $bc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
        foreach ($b in $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, $bc)) { if ($b.Current.Name) { $names += $b.Current.Name } }
    }
    return $names
}
function RightClick([double]$x, [double]$y) {
    $pw = PanelWin
    if ($pw) { [UI]::SetForegroundWindow($pw.H) | Out-Null; Start-Sleep -Milliseconds 120 }
    [UI]::SetCursorPos([int]$x, [int]$y) | Out-Null; Start-Sleep -Milliseconds 160
    [UI]::mouse_event(0x0008,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 50
    [UI]::mouse_event(0x0010,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 800
}
function LeftClick([double]$x, [double]$y) {
    [UI]::SetCursorPos([int]$x, [int]$y) | Out-Null; Start-Sleep -Milliseconds 120
    [UI]::mouse_event(0x0002,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 40
    [UI]::mouse_event(0x0004,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 800
}
function Edge([int]$x, [int]$y) {
    $bmp = New-Object System.Drawing.Bitmap(4, 1)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($x, $y, 0, 0, $bmp.Size); $g.Dispose()
    $v = @(); for ($i=0; $i -lt 4; $i++) { $v += $bmp.GetPixel($i,0).R }; $bmp.Dispose()
    return ($v -join ',')
}
function OpenPanel {
    for ($i=0; $i -lt 4; $i++) {
        Hotkey
        $p = PanelWin
        if ($p) {
            [UI]::SetForegroundWindow($p.H) | Out-Null
            Start-Sleep -Milliseconds 350
            return $p
        }
    }
    return $null
}
function Esc { [System.Windows.Forms.SendKeys]::SendWait('{ESC}'); Start-Sleep -Milliseconds 500 }

# ---------------- setup ----------------
Stop-App
if (Test-Path $DataDir) { Remove-Item -Recurse -Force $DataDir }
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
@{
    Version = 3; Hotkey = 'Win+Alt+V'; MaxItems = 500; Theme = 'Dark'; ImagePreview = 'Medium'
    PasteOnClick = $false; KeepPanelOpenAfterPaste = $false; StartWithWindows = $false
    CaptureImages = $true; CaptureFiles = $true; CaptureHtml = $true
    ExcludedApps = @(); HasPanelPosition = $false; PanelPositionMode = 'MonitorCenter'
    PanelPositionInPixels = $false; PanelLeft = 0; PanelTop = 0; PanelWidth = 420; PanelHeight = 620
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $DataDir 'settings.json') -Encoding UTF8
Start-App
Set-Clipboard -Value 'regress text one'; Start-Sleep -Milliseconds 500
Set-Clipboard -Value 'regress text two'; Start-Sleep -Milliseconds 500
$bmp = New-Object System.Drawing.Bitmap 220, 160
$g = [System.Drawing.Graphics]::FromImage($bmp); $g.Clear([System.Drawing.Color]::SteelBlue); $g.Dispose()
for ($i=0; $i -lt 6; $i++) { try { [System.Windows.Forms.Clipboard]::SetImage($bmp); break } catch { Start-Sleep -Milliseconds 400 } }
Start-Sleep -Milliseconds 900

# ---------------- 1. right-click invariants ----------------
$panel = OpenPanel
Check 'panel opens' ($null -ne $panel)
if ($null -ne $panel) {
    [UI]::SetCursorPos(1280, 620) | Out-Null
    Start-Sleep -Milliseconds 200
    $edgeIdle = Edge $panel.L ($panel.T + 300)
    $selIdle = SelectionFlags
    $rows = Rows
    $r = $rows | Where-Object { ($_.Current.BoundingRectangle.Height -le 90) } | Select-Object -First 1
    $b = $r.Current.BoundingRectangle
    RightClick ($b.X + 90) ($b.Y + 16)
    Check 'one menu after right-click' ((MenuCount) -eq 1) ("menus=" + (MenuCount))
    Check 'panel edge unchanged' ((Edge $panel.L ($panel.T + 300)) -eq $edgeIdle) ("idle=$edgeIdle now=" + (Edge $panel.L ($panel.T + 300)))
    Check 'selection unchanged' ((SelectionFlags) -eq $selIdle) ("idle=$selIdle now=" + (SelectionFlags))
    Check 'text menu has plain paste' ((ButtonNames) -contains '粘贴为纯文本')
    Esc
    Check 'Esc closes menu, panel stays' (((MenuCount) -eq 0) -and ($null -ne (PanelWin)))

    # click inside panel (search box: no paste) closes menu, panel stays
    $b = (Rows | Select-Object -First 1).Current.BoundingRectangle
    RightClick ($b.X + 90) ($b.Y + 16)
    $sb = (PanelEl).FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))
    $sbr = $sb.Current.BoundingRectangle
    LeftClick ($sbr.X + 40) ($sbr.Y + 18)
    Check 'inside click closes menu only' (((MenuCount) -eq 0) -and ($null -ne (PanelWin)))

    # image row menu
    $imgRow = Rows | Where-Object { $_.Current.BoundingRectangle.Height -gt 90 } | Select-Object -First 1
    if ($imgRow) {
        $ib = $imgRow.Current.BoundingRectangle
        RightClick ($ib.X + 90) ($ib.Y + 16)
        Check 'image menu has save-as' ((ButtonNames) -contains '图片另存为…')
        Esc
    }

    # rapid right-clicks: one menu, no stacking
    $stable = $true
    for ($i=0; $i -lt 5; $i++) {
        $rows = Rows
        if ($rows.Count -eq 0) { break }
        $rb = $rows[$i % $rows.Count].Current.BoundingRectangle
        RightClick ($rb.X + 90) ($rb.Y + 16)
        if ((MenuCount) -ne 1) { $stable = $false }
        if ($null -eq (PanelWin)) { $stable = $false }
    }
    Check 'rapid right-clicks keep one menu' $stable
    Check 'selection unchanged after rapid clicks' ((SelectionFlags) -eq $selIdle) ("idle=$selIdle now=" + (SelectionFlags))
    Esc

    # outside click closes menu and panel
    $rows = Rows
    $rb = $rows[0].Current.BoundingRectangle
    RightClick ($rb.X + 90) ($rb.Y + 16)
    LeftClick 20 20
    Check 'outside click closes both' (((MenuCount) -eq 0) -and ($null -eq (PanelWin)))
}

# ---------------- 2. keyboard menu ----------------
$panel = OpenPanel
if ($null -ne $panel) {
    $el = PanelEl
    $edit = $el.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))
    $edit.SetFocus(); Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait('+{F10}')
    Start-Sleep -Milliseconds 900
    $bc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    $plain = $null
    $p = AppProc
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$p.Id)
    foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
        foreach ($btn in $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, $bc)) { if ($btn.Current.Name -eq '粘贴为纯文本') { $plain = $btn } }
    }
    Check 'keyboard menu opens' ($null -ne $plain)
    Check 'keyboard menu focuses first row' ($null -ne $plain -and $plain.Current.HasKeyboardFocus)
    Esc
    Check 'Esc closes keyboard menu, panel stays' (((MenuCount) -eq 0) -and ($null -ne (PanelWin)))
    Esc
}

# ---------------- 3. source funnel ----------------
$panel = OpenPanel
if ($null -ne $panel) {
    $funnel = $null
    $bc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    foreach ($b in (PanelEl).FindAll([System.Windows.Automation.TreeScope]::Descendants, $bc)) { if ($b.Current.Name -eq '来源筛选') { $funnel = $b } }
    Check 'source funnel exists' ($null -ne $funnel)
    if ($funnel) {
        $funnel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 800
        Check 'source menu opens alone' ((MenuCount) -eq 1)
        Check 'source menu lists 全部来源' ((ButtonNames) -contains '全部来源')
        Esc
        Check 'Esc closes source menu, panel stays' (((MenuCount) -eq 0) -and ($null -ne (PanelWin)))
    }
}

# ---------------- 4. show/hide cycles ----------------
$cyclesOk = $true
for ($i=0; $i -lt 10; $i++) {
    Esc
    Start-Sleep -Milliseconds 200
    if ($null -eq (OpenPanel)) { $cyclesOk = $false; break }
    if ((MenuCount) -ne 0) { $cyclesOk = $false }
}
Check '10 show/hide cycles stay clean' $cyclesOk
Esc

Stop-App
Write-Output ("RESULT pass=" + $script:pass + " fail=" + $script:fail)
