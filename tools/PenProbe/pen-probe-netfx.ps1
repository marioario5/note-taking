# Bare WPF InkCanvas. No NoteTaker code of any kind.
# Answers one question: does this machine deliver stylus events to a WPF ink surface at all?
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

$log = Join-Path $env:LOCALAPPDATA 'NoteTaker\pen-probe.txt'
$sb  = New-Object System.Text.StringBuilder

function Note($t) { [void]$sb.AppendLine($t) }

Note "tablet devices: $([System.Windows.Input.Tablet]::TabletDevices.Count)"
foreach ($t in [System.Windows.Input.Tablet]::TabletDevices) {
    Note "  tablet: $($t.Name) type=$($t.Type) styluses=$($t.StylusDevices.Count)"
}

$w = New-Object System.Windows.Window
$w.Title = 'Pen probe - draw here, then close'
$w.Width = 900; $w.Height = 650

$ink = New-Object System.Windows.Controls.InkCanvas
$ink.Background = 'White'
$w.Content = $ink

$counts = @{ StylusDown = 0; StylusMove = 0; StylusUp = 0; MouseDown = 0; MouseMove = 0; Stroke = 0 }

$ink.Add_StylusDown({   $counts.StylusDown++ })
$ink.Add_StylusMove({   $counts.StylusMove++ })
$ink.Add_StylusUp({     $counts.StylusUp++   })
$ink.Add_MouseDown({    $counts.MouseDown++  })
$ink.Add_MouseMove({    if ($_.LeftButton -eq 'Pressed') { $counts.MouseMove++ } })
$ink.Add_StrokeCollected({ $counts.Stroke++ })

$w.Add_Closed({
    Note ''
    Note "StylusDown : $($counts.StylusDown)"
    Note "StylusMove : $($counts.StylusMove)"
    Note "StylusUp   : $($counts.StylusUp)"
    Note "MouseDown  : $($counts.MouseDown)"
    Note "MouseMove  : $($counts.MouseMove)"
    Note "Strokes    : $($counts.Stroke)"
    Set-Content -Path $log -Value $sb.ToString() -Encoding utf8
})

[void]$w.ShowDialog()
