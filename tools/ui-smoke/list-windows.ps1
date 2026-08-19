Add-Type -AssemblyName UIAutomationClient
$p = Get-Process AiMemoryManager -ErrorAction SilentlyContinue
if (-not $p) { Write-Output "app not running"; exit }
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
foreach ($w in $wins) {
    Write-Output ("{0} | class={1} | name='{2}' | enabled={3}" -f $w.Current.ControlType.ProgrammaticName, $w.Current.ClassName, $w.Current.Name, $w.Current.IsEnabled)
}
