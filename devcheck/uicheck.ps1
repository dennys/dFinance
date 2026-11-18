param([int]$ProcessId, [string]$PdfPath, [string]$Password)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
$sig = '[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n); [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y); [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern void mouse_event(int f, int dx, int dy, int d, int e);'
Add-Type -MemberDefinition $sig -Name W32 -Namespace Native

$Out = "D:\dennys\dFinance\devcheck\uicheck_out.txt"
Remove-Item $Out -ErrorAction SilentlyContinue
function Say([string]$s) { Add-Content -Path $Out -Value $s -Encoding UTF8 }

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if ($null -eq $win) { Say "WINDOW NOT FOUND"; exit 1 }

# 解析用的控件在「匯入 PDF」分頁裡；分頁没被選取时内容不在視覺樹，要先切過去
$tabCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '匯入 PDF')
$tab = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $tabCond)
if ($null -eq $tab) { Say "TAB NOT FOUND: 匯入 PDF"; exit 1 }
$tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 800

function ById([string]$id) {
  $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
  return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

$pathBox = ById 'PathBox'
$pwBox = ById 'PasswordBox'
$btn = ById 'ParseButton'
Say ("found pathBox={0} pw={1} btn={2}" -f ($null -ne $pathBox), ($null -ne $pwBox), ($null -ne $btn))

$pathBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($PdfPath)

$hwnd = (Get-Process -Id $ProcessId).MainWindowHandle
Say ("fg=" + [Native.W32]::SetForegroundWindow($hwnd) + " rect=" + $pwBox.Current.BoundingRectangle)
[Native.W32]::ShowWindow($hwnd, 5) | Out-Null
[Native.W32]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 800
$r = $pwBox.Current.BoundingRectangle
$cx = [int]($r.X + $r.Width / 2); $cy = [int]($r.Y + $r.Height / 2)
[Native.W32]::SetCursorPos($cx, $cy) | Out-Null
[Native.W32]::mouse_event(2, 0, 0, 0, 0); [Native.W32]::mouse_event(4, 0, 0, 0, 0)
Start-Sleep -Milliseconds 400
Say ("typing at $cx,$cy")
[System.Windows.Forms.SendKeys]::SendWait($Password)

$btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 5

Say ("SUMMARY: " + (ById 'SummaryText').Current.Name)
Say ("WARN: " + (ById 'WarningText').Current.Name)

$grid = ById 'Grid'
$rows = $grid.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem)))
Say ("ROWS: " + $rows.Count)
foreach ($i in 0, ($rows.Count - 1)) {
  if ($rows.Count -eq 0) { break }
  $cells = $rows[$i].FindAll([System.Windows.Automation.TreeScope]::Children,
      (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
  Say ("ROW{0}: " -f $i)
  foreach ($c in $cells) { Say ("   [" + $c.Current.Name + "]") }
}
Say ("RAWLEN: " + (ById 'RawText').Current.Value.Length)
