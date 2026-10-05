<#
  Elite HUD Studio - helper
  -------------------------
  Started by "Start HUD Studio - Browser version.bat". Runs a tiny web server that only this PC can reach
  (http://localhost:47810) so the HUD Studio page can read and save your HUD colour files.
  Uses only what ships with Windows (PowerShell + .NET). Close this window to stop it.
#>
param(
  [string]$GameDir,      # force a specific ...\Products\elite-dangerous-odyssey-64 folder
  [int]$Port = 47810,
  [switch]$NoBrowser,    # don't open the browser automatically
  [switch]$TestMode      # for testing on a copy: skips the "Elite is running" safety check
)

$ErrorActionPreference = 'Stop'
$AppVersion   = '1.0'
$AppDir       = $PSScriptRoot
$Root         = Split-Path -Parent $AppDir
$PageFile     = Join-Path $Root 'HUD Studio.html'
$BackupDir    = Join-Path $Root 'Backups'
$MyThemesDir  = Join-Path $Root 'My Themes'
$SettingsFile = Join-Path $Root 'settings.json'
$ColorFiles   = @('Startup-Profile', 'Advanced', 'SuitHud', 'XML-Profile')
$IdleMinutes  = 30
$Utf8NoBom    = New-Object System.Text.UTF8Encoding($false)

$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
$tokenBytes = New-Object byte[] 16
$rng.GetBytes($tokenBytes)
$Token = -join ($tokenBytes | ForEach-Object { $_.ToString('x2') })

[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression.FileSystem

# ---------------------------------------------------------------- native keyboard input for the F11 colour reload
# Browsers are not allowed to generate a real OS-level F11 for another process.
# HUD Studio therefore uses this small Windows user32 bridge to focus Elite and
# send a genuine F11 after the colour files have been saved.
if (-not ('HudStudioNativeInput' -as [type])) {
  Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class HudStudioNativeInput
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const byte VK_F11 = 0x7A;
    public const int SW_RESTORE = 9;
}
'@
}

function Invoke-EdhmReload {
  $result = [ordered]@{ attempted = $false; sent = $false; error = $null }
  $proc = @(Get-Process -Name 'EliteDangerous64' -ErrorAction SilentlyContinue | Select-Object -First 1)
  if (-not $proc) { return $result }

  $result.attempted = $true
  try {
    $hwnd = [IntPtr]$proc.MainWindowHandle
    if ($hwnd -eq [IntPtr]::Zero) {
      throw 'Elite Dangerous is running but its main window handle is not available.'
    }

    [void][HudStudioNativeInput]::ShowWindow($hwnd, [HudStudioNativeInput]::SW_RESTORE)
    Start-Sleep -Milliseconds 100
    if (-not [HudStudioNativeInput]::SetForegroundWindow($hwnd)) {
      throw 'Windows would not activate the Elite Dangerous window.'
    }
    Start-Sleep -Milliseconds 100

    [HudStudioNativeInput]::keybd_event([HudStudioNativeInput]::VK_F11, 0, 0, [UIntPtr]::Zero)
    [HudStudioNativeInput]::keybd_event([HudStudioNativeInput]::VK_F11, 0, [HudStudioNativeInput]::KEYEVENTF_KEYUP, [UIntPtr]::Zero)
    $result.sent = $true
    Write-Log 'Sent F11 to Elite Dangerous to reload the HUD colours.'
  } catch {
    $result.error = $_.Exception.Message
    Write-Log "Could not send F11 to Elite Dangerous: $($result.error)"
  }
  return $result
}

function Write-Log([string]$msg) { Write-Host ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $msg) }
function ConvertTo-StringArray($list) { if ($null -eq $list) { return ,@() }; return ,@($list | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ }) }
# Windows' old 260-character path limit: the \\?\ form lets .NET use longer paths (deep backup folders).
function Get-LongPath([string]$p) { if ($p.Length -ge 240 -and $p -match '^[A-Za-z]:\\' ) { return '\\?\' + $p }; return $p }

# ---------------------------------------------------------------- settings
function Get-Settings {
  $h = @{}
  if (Test-Path -LiteralPath $SettingsFile) {
    try { $o = [IO.File]::ReadAllText($SettingsFile) | ConvertFrom-Json; foreach ($p in $o.PSObject.Properties) { $h[$p.Name] = $p.Value } } catch {}
  }
  return $h
}
function Save-Settings($h) { [IO.File]::WriteAllText($SettingsFile, (ConvertTo-Json -InputObject $h -Depth 5), $Utf8NoBom) }

# ---------------------------------------------------------------- finding the game
function Find-GameDirs {
  $roots = New-Object System.Collections.Generic.List[object]
  try {
    $steam = (Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -ErrorAction Stop).SteamPath
    if ($steam) {
      $steam = $steam -replace '/', '\'
      $libs = @($steam)
      $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
      if (Test-Path -LiteralPath $vdf) {
        foreach ($m in [regex]::Matches([IO.File]::ReadAllText($vdf), '"path"\s+"([^"]+)"')) { $libs += ($m.Groups[1].Value -replace '\\\\', '\') }
      }
      foreach ($l in ($libs | Select-Object -Unique)) { $roots.Add(@{ dir = (Join-Path $l 'steamapps\common\Elite Dangerous'); store = 'Steam' }) }
    }
  } catch {}
  $epic = 'C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests'
  if (Test-Path -LiteralPath $epic) {
    foreach ($f in (Get-ChildItem -LiteralPath $epic -Filter '*.item' -ErrorAction SilentlyContinue)) {
      try { $m = [IO.File]::ReadAllText($f.FullName) | ConvertFrom-Json; if ($m.DisplayName -like '*Elite Dangerous*') { $roots.Add(@{ dir = $m.InstallLocation; store = 'Epic' }) } } catch {}
    }
  }
  foreach ($p in @("${env:ProgramFiles(x86)}\Frontier", "$env:ProgramFiles\Frontier", "$env:LOCALAPPDATA\Frontier_Developments")) { $roots.Add(@{ dir = $p; store = 'Frontier' }) }

  $found = New-Object System.Collections.Generic.List[object]
  foreach ($r in $roots) {
    if (-not $r.dir -or -not (Test-Path -LiteralPath $r.dir)) { continue }
    foreach ($sub in @('Products\elite-dangerous-odyssey-64', 'EDLaunch\Products\elite-dangerous-odyssey-64', 'elite-dangerous-odyssey-64')) {
      $d = Join-Path $r.dir $sub
      if ((Test-Path -LiteralPath (Join-Path $d 'EliteDangerous64.exe')) -and -not ($found | Where-Object { $_.path -eq $d })) {
        $found.Add([ordered]@{ path = $d; store = $r.store; edhm = (Test-Path -LiteralPath (Join-Path $d 'd3dx.ini')) })
      }
    }
  }
  return ,$found.ToArray()
}

function Select-Game {
  $script:games = Find-GameDirs
  $s = Get-Settings
  $pick = $null
  if ($GameDir) {
    if (-not (Test-Path -LiteralPath $GameDir)) { throw "Game folder not found: $GameDir" }
    $pick = [ordered]@{ path = (Resolve-Path -LiteralPath $GameDir).Path; store = 'Custom'; edhm = (Test-Path -LiteralPath (Join-Path $GameDir 'd3dx.ini')) }
  } elseif ($s['gameDir'] -and (Test-Path -LiteralPath (Join-Path $s['gameDir'] 'EliteDangerous64.exe'))) {
    $pick = $script:games | Where-Object { $_.path -eq $s['gameDir'] } | Select-Object -First 1
    if (-not $pick) { $pick = [ordered]@{ path = $s['gameDir']; store = 'Custom'; edhm = (Test-Path -LiteralPath (Join-Path $s['gameDir'] 'd3dx.ini')) } }
  } else {
    $pick = $script:games | Where-Object { $_.edhm } | Select-Object -First 1
    if (-not $pick) { $pick = $script:games | Select-Object -First 1 }
  }
  $script:game = $pick
}

function Test-GameRunning { return (@(Get-Process -Name 'EliteDangerous64' -ErrorAction SilentlyContinue).Count -gt 0) }

function Get-GameVersion([string]$dir) {
  $f = Join-Path $dir 'VersionInfo.txt'
  if (Test-Path -LiteralPath $f) { try { return ([IO.File]::ReadAllText($f) | ConvertFrom-Json).Version } catch {} }
  return $null
}

function Get-EdhmVersion([string]$d3dxText) {
  $m = [regex]::Match($d3dxText, 'EDHM\)[^\r\n]*?v(\d+(?:\.\d+)+)\s*\(([^)]*)\)(?:\s*for FDev Update\s*([\d.]+))?')
  if ($m.Success) { return [ordered]@{ version = $m.Groups[1].Value; date = $m.Groups[2].Value; forGame = $m.Groups[3].Value } }
  return $null
}

function Get-IniDir {
  if (-not $script:game) { return $null }
  $d = Join-Path $script:game.path 'EDHM-ini'
  if (Test-Path -LiteralPath $d) { return $d }
  return $null
}

function Get-EdhmInfo([string]$dir) {
  $info = [ordered]@{ installed = $false; version = $null; date = $null; forGame = $null; iniDir = $null; linkTarget = $null; files = [ordered]@{} }
  $d3dx = Join-Path $dir 'd3dx.ini'
  if (Test-Path -LiteralPath $d3dx) {
    $v = Get-EdhmVersion ([IO.File]::ReadAllText($d3dx))
    if ($v) { $info.version = $v.version; $info.date = $v.date; $info.forGame = $v.forGame }
  }
  $iniDir = Join-Path $dir 'EDHM-ini'
  if (Test-Path -LiteralPath $iniDir) {
    $info.iniDir = $iniDir
    $item = Get-Item -LiteralPath $iniDir -Force
    if ($item.LinkType) { $info.linkTarget = [string]($item.Target | Select-Object -First 1) }
    foreach ($n in $ColorFiles) { $info.files[$n] = (Test-Path -LiteralPath (Join-Path $iniDir "$n.ini")) }
  }
  $info.installed = (Test-Path -LiteralPath $d3dx) -and (Test-Path -LiteralPath (Join-Path $dir 'd3d11.dll')) -and [bool]$info.iniDir
  return $info
}

# Elite's own built-in colour file. The HUD colours look right when it is left at default.
function Get-XmlStatus {
  $p = Join-Path $env:LOCALAPPDATA 'Frontier Developments\Elite Dangerous\Options\Graphics\GraphicsConfigurationOverride.xml'
  $r = [ordered]@{ path = $p; exists = (Test-Path -LiteralPath $p); isDefault = $true; matrix = $null }
  if ($r.exists) {
    try {
      [xml]$x = [IO.File]::ReadAllText($p)
      $d = $x.GraphicsConfig.GUIColour.Default
      if ($d) {
        $rows = @()
        foreach ($row in @($d.MatrixRed, $d.MatrixGreen, $d.MatrixBlue)) { $rows += ,@(([string]$row -split ',') | ForEach-Object { [double]$_.Trim() }) }
        $r.matrix = $rows
        $id = @(@(1, 0, 0), @(0, 1, 0), @(0, 0, 1))
        for ($i = 0; $i -lt 3; $i++) { for ($k = 0; $k -lt 3; $k++) { if ([math]::Abs($rows[$i][$k] - $id[$i][$k]) -gt 0.001) { $r.isDefault = $false } } }
      }
    } catch { $r.isDefault = $null }
  }
  return $r
}

function Get-ThemesDir {
  # HUD Studio only uses its own My Themes folder.
  return $null
}

# ---------------------------------------------------------------- ini reading / writing
function Get-ConstantsSpan([string]$text) {
  $sec = [regex]::Match($text, '(?im)^[ \t]*\[Constants\][^\n]*')
  if (-not $sec.Success) { return $null }
  $start = $sec.Index + $sec.Length
  $next = [regex]::Match($text.Substring($start), '(?m)^[ \t]*\[')
  $end = $text.Length
  if ($next.Success) { $end = $start + $next.Index }
  return @{ start = $start; end = $end }
}

function Get-IniValues([string]$text) {
  $h = [ordered]@{}
  $span = Get-ConstantsSpan $text
  if (-not $span) { return $h }
  $body = $text.Substring($span.start, $span.end - $span.start)
  foreach ($m in [regex]::Matches($body, '(?m)^[ \t]*([xyzw]\d+)[ \t]*=[ \t]*(-?[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?)')) {
    if (-not $h.Contains($m.Groups[1].Value)) { $h[$m.Groups[1].Value] = $m.Groups[2].Value }
  }
  return $h
}

# Changes only the numbers after "key =" inside [Constants]; every comment and line stays as it was.
function Update-IniText([string]$text, $values) {
  $span = Get-ConstantsSpan $text
  if (-not $span) { throw 'The file has no [Constants] section.' }
  $head = $text.Substring(0, $span.start)
  $body = $text.Substring($span.start, $span.end - $span.start)
  $tail = $text.Substring($span.end)
  $count = 0
  $missing = New-Object System.Collections.Generic.List[string]
  foreach ($k in @($values.Keys)) {
    $m = [regex]::Match($body, '(?m)^([ \t]*' + $k + '[ \t]*=[ \t]*)(-?[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?)')
    if ($m.Success) {
      $g = $m.Groups[2]
      $body = $body.Substring(0, $g.Index) + [string]$values[$k] + $body.Substring($g.Index + $g.Length)
      $count++
    } else { $missing.Add($k) }
  }
  return @{ text = ($head + $body + $tail); count = $count; missing = $missing.ToArray() }
}

function Write-TextFile([string]$path, [string]$text) {
  $p = Get-LongPath $path
  $bom = $false
  if ([IO.File]::Exists($p)) {
    $fs = [IO.File]::OpenRead($p)
    try { $b = New-Object byte[] 3; $n = $fs.Read($b, 0, 3); $bom = ($n -eq 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF) } finally { $fs.Close() }
  }
  $enc = $Utf8NoBom
  if ($bom) { $enc = New-Object System.Text.UTF8Encoding($true) }
  $tmp = Get-LongPath "$path.hudstudio-tmp"
  [IO.File]::WriteAllText($tmp, $text, $enc)
  if (-not [IO.File]::Exists($p)) { [IO.File]::Move($tmp, $p); return }
  try { [IO.File]::Replace($tmp, $p, $null) } catch { [IO.File]::Copy($tmp, $p, $true); [IO.File]::Delete($tmp) }
}

# Validates {"Advanced": {"x232": "0.3278"}, ...} coming from the page.
function ConvertTo-ChangePlan($changes) {
  $plan = [ordered]@{}
  if (-not $changes) { return $plan }
  foreach ($fp in $changes.PSObject.Properties) {
    if ($ColorFiles -notcontains $fp.Name) { throw "Unknown settings file: $($fp.Name)" }
    $kv = [ordered]@{}
    foreach ($p in $fp.Value.PSObject.Properties) {
      if ($p.Name -notmatch '^[xyzw]\d{1,3}$') { throw "Bad setting name: $($p.Name)" }
      $v = [string]$p.Value
      if ($v -notmatch '^-?\d{1,4}(\.\d{1,6})?$') { throw "Bad value for $($p.Name): $v" }
      $kv[$p.Name] = $v
    }
    if ($kv.Count -gt 0) { $plan[$fp.Name] = $kv }
  }
  return $plan
}

# ---------------------------------------------------------------- backups
function New-BackupName([string]$reason) { return ((Get-Date -Format 'yyyy-MM-dd_HH-mm-ss') + ' ' + $reason) -replace '[\\/:*?"<>|]', '-' }

function Write-BackupInfo([string]$dir, $info) { [IO.File]::WriteAllText((Get-LongPath (Join-Path $dir 'info.json')), (ConvertTo-Json -InputObject $info -Depth 5), $Utf8NoBom) }

function Copy-OneFile([string]$src, [string]$dst) {
  [void][IO.Directory]::CreateDirectory((Get-LongPath (Split-Path -Parent $dst)))
  [IO.File]::Copy((Get-LongPath $src), (Get-LongPath $dst), $true)
}

function Copy-ColorFiles([string]$fromDir, [string]$toDir) {
  foreach ($n in $ColorFiles) { $src = Join-Path $fromDir "$n.ini"; if ([IO.File]::Exists((Get-LongPath $src))) { Copy-OneFile $src (Join-Path $toDir "$n.ini") } }
}

function New-ColorBackup([string]$reason) {
  $iniDir = Get-IniDir
  if (-not $iniDir) { return $null }
  $name = New-BackupName $reason
  $dir = Join-Path $BackupDir $name
  [void][IO.Directory]::CreateDirectory((Get-LongPath $dir))
  Copy-ColorFiles $iniDir $dir
  $ver = (Get-EdhmInfo $script:game.path).version
  Write-BackupInfo $dir ([ordered]@{ kind = 'colors'; reason = $reason; time = (Get-Date).ToString('s'); edhm = $ver })
  Write-Log "Backup made: $name"
  return $name
}

function Copy-TreeContents([string]$src, [string]$dst) {
  $s = Get-LongPath $src
  [void][IO.Directory]::CreateDirectory((Get-LongPath $dst))
  foreach ($f in [IO.Directory]::GetFiles($s, '*', 'AllDirectories')) {
    $rel = $f.Substring($s.Length).TrimStart('\')
    Copy-OneFile $f (Join-Path $dst $rel)
  }
}

# info.json is written last: a backup without it is incomplete and is never used for a restore.
function Get-BackupList {
  $items = New-Object System.Collections.Generic.List[object]
  if (Test-Path -LiteralPath $BackupDir) {
    foreach ($d in @(Get-ChildItem -LiteralPath $BackupDir -Directory | Sort-Object Name -Descending)) {
      $info = $null
      $ip = Get-LongPath (Join-Path $d.FullName 'info.json')
      if ([IO.File]::Exists($ip)) { try { $info = [IO.File]::ReadAllText($ip) | ConvertFrom-Json } catch {} }
      $item = [ordered]@{ name = $d.Name; time = $d.CreationTime.ToString('s'); reason = 'Incomplete backup'; kind = 'incomplete'; edhm = $null; to = $null }
      if ($info) { $item.time = $info.time; $item.reason = $info.reason; $item.edhm = $info.edhm; $item.kind = 'colors'; if ($info.kind) { $item.kind = $info.kind }; if ($info.to) { $item.to = $info.to } }
      $items.Add($item)
    }
  }
  return ,$items.ToArray()
}

function Test-SafeName([string]$n) {
  return ([bool]$n -and $n.Length -le 200 -and $n -notmatch '[\\/:*?"<>|]' -and -not $n.StartsWith('.'))
}

function Get-FolderIniTexts([string]$dir) {
  $files = [ordered]@{}
  foreach ($n in $ColorFiles) { $p = Get-LongPath (Join-Path $dir "$n.ini"); if ([IO.File]::Exists($p)) { $files[$n] = [IO.File]::ReadAllText($p) } }
  return $files
}

# ---------------------------------------------------------------- themes
function Get-ThemeList {
  if ($script:themeCache -and ((Get-Date) - $script:themeCache.time).TotalSeconds -lt 60) { return $script:themeCache.data }
  $dir = Get-ThemesDir
  $ui = New-Object System.Collections.Generic.List[object]
  if ($dir) {
    foreach ($d in @(Get-ChildItem -LiteralPath $dir -Directory | Sort-Object Name)) {
      if (-not ((Test-Path -LiteralPath (Join-Path $d.FullName 'Startup-Profile.ini')) -or (Test-Path -LiteralPath (Join-Path $d.FullName 'Advanced.ini')))) { continue }
      $author = ''; $colors = @()
      $cred = Get-ChildItem -LiteralPath $d.FullName -Filter '*.credits' -ErrorAction SilentlyContinue | Select-Object -First 1
      if ($cred) { try { $c = [IO.File]::ReadAllText($cred.FullName) | ConvertFrom-Json; $author = [string]$c.author; $colors = ConvertTo-StringArray $c.color } catch {} }
      $ui.Add([ordered]@{ id = $d.Name; name = ($d.Name -replace '^@', ''); author = $author; colors = $colors; preview = (Test-Path -LiteralPath (Join-Path $d.FullName 'PREVIEW.jpg')) })
    }
  }
  $mine = New-Object System.Collections.Generic.List[object]
  if (Test-Path -LiteralPath $MyThemesDir) {
    foreach ($f in @(Get-ChildItem -LiteralPath $MyThemesDir -Filter '*.json' -File | Sort-Object LastWriteTime -Descending)) {
      try {
        $t = [IO.File]::ReadAllText($f.FullName) | ConvertFrom-Json
        $mine.Add([ordered]@{ id = $f.BaseName; name = [string]$t.name; author = [string]$t.author; created = [string]$t.created; colors = (ConvertTo-StringArray $t.colors) })
      } catch {}
    }
  }
  $data = [ordered]@{ ok = $true; dir = $dir; edhmui = $ui.ToArray(); mine = $mine.ToArray() }
  $script:themeCache = @{ time = Get-Date; data = $data }
  return $data
}

function Save-MyTheme($body) {
  $name = ([string]$body.name).Trim()
  if (-not $name -or $name.Length -gt 80) { throw 'Please give the theme a name (up to 80 characters).' }
  $safe = (($name -replace '[\\/:*?"<>|]', '-').Trim()).TrimEnd('.')
  if (-not (Test-SafeName $safe)) { throw 'That name cannot be used as a file name.' }
  $plan = ConvertTo-ChangePlan $body.values
  if ($plan.Count -eq 0) { throw 'The theme has no values.' }
  New-Item -ItemType Directory -Force $MyThemesDir | Out-Null
  $obj = [ordered]@{ app = 'hud-studio'; version = 1; name = $name; author = [string]$body.author; created = (Get-Date).ToString('s'); colors = (ConvertTo-StringArray $body.colors); values = $plan }
  [IO.File]::WriteAllText((Join-Path $MyThemesDir "$safe.json"), (ConvertTo-Json -InputObject $obj -Depth 6), $Utf8NoBom)
  $script:themeCache = $null
  Write-Log "Theme saved: $name"
  return [ordered]@{ ok = $true; id = $safe }
}

# ---------------------------------------------------------------- API
function Get-Status {
  $st = [ordered]@{
    ok = $true; app = 'hud-studio'; appVersion = $AppVersion; testMode = [bool]$TestMode
    running = (Test-GameRunning); game = $null; games = $script:games; edhm = $null
    xml = (Get-XmlStatus); themesDir = (Get-ThemesDir); backupsDir = $BackupDir; root = $Root
  }
  if ($script:game) {
    $st.game = [ordered]@{ path = $script:game.path; store = $script:game.store; version = (Get-GameVersion $script:game.path) }
    # the player confirmed this folder once; remembered in settings.json so Studio doesn't ask again
    $st.gameConfirmed = ([string](Get-Settings)['gameConfirmed'] -eq [string]$script:game.path)
    $st.edhm = Get-EdhmInfo $script:game.path
  }
  return $st
}

function Save-Changes($body) {
  $iniDir = Get-IniDir
  if (-not $iniDir) { throw 'HUD color files were not found in the selected game folder.' }
  $plan = ConvertTo-ChangePlan $body.changes
  if ($plan.Count -eq 0) { return [ordered]@{ ok = $true; written = 0; missing = @(); backup = $null } }
  $backup = $null
  if ($body.backup -or -not $script:sessionBackup) { $backup = New-ColorBackup 'Before save'; $script:sessionBackup = $true }
  $written = 0
  $missing = New-Object System.Collections.Generic.List[string]
  foreach ($file in @($plan.Keys)) {
    $path = Join-Path $iniDir "$file.ini"
    if (-not (Test-Path -LiteralPath $path)) { foreach ($k in $plan[$file].Keys) { $missing.Add("${file}:$k") }; continue }
    $res = Update-IniText ([IO.File]::ReadAllText($path)) $plan[$file]
    if ($res.count -gt 0) { Write-TextFile $path $res.text }
    $written += $res.count
    foreach ($k in $res.missing) { $missing.Add("${file}:$k") }
  }
  $running = Test-GameRunning
  $reload = [ordered]@{ attempted = $false; sent = $false; error = $null }
  if ($written -gt 0 -and $running) { $reload = Invoke-EdhmReload }
  Write-Log ("Saved {0} setting(s) to the HUD colour files{1}" -f $written, $(if ($reload.sent) { ' - F11 reload sent' } elseif ($running) { ' - F11 reload could not be sent automatically' } else { '' }))
  return [ordered]@{ ok = $true; written = $written; missing = $missing.ToArray(); backup = $backup; running = $running; reload = $reload }
}

function Open-Folder([string]$what) {
  $p = $null
  switch ($what) {
    'tool'     { $p = $Root }
    'backups'  { $p = $BackupDir; New-Item -ItemType Directory -Force $p | Out-Null }
    'mythemes' { $p = $MyThemesDir; New-Item -ItemType Directory -Force $p | Out-Null }
    'game'     { if ($script:game) { $p = $script:game.path } }
    'edhm'     { $p = Get-IniDir }
    'themes'   { $p = Get-ThemesDir }
  }
  if (-not $p -or -not (Test-Path -LiteralPath $p)) { throw 'That folder was not found.' }
  Start-Process -FilePath 'explorer.exe' -ArgumentList ('"' + $p + '"')
  return [ordered]@{ ok = $true }
}

function Get-QueryValue($ctx, [string]$name) {
  $q = $ctx.Request.Url.Query
  if (-not $q) { return $null }
  foreach ($pair in $q.TrimStart('?').Split('&')) {
    $kv = $pair.Split('=', 2)
    if ([Uri]::UnescapeDataString($kv[0]) -eq $name) { if ($kv.Count -gt 1) { return [Uri]::UnescapeDataString($kv[1].Replace('+', ' ')) } else { return '' } }
  }
  return $null
}

# ---------------------------------------------------------------- HTTP plumbing
function Send-Bytes($ctx, [int]$code, [string]$type, [byte[]]$bytes, [string]$cache = 'no-store') {
  $res = $ctx.Response
  $res.StatusCode = $code
  $res.ContentType = $type
  $res.Headers['Cache-Control'] = $cache
  $res.Headers['X-Content-Type-Options'] = 'nosniff'
  $res.ContentLength64 = $bytes.Length
  $res.OutputStream.Write($bytes, 0, $bytes.Length)
  $res.OutputStream.Close()
}
function Send-Json($ctx, $obj, [int]$code = 200) {
  $json = ConvertTo-Json -InputObject $obj -Depth 12 -Compress
  Send-Bytes $ctx $code 'application/json; charset=utf-8' ($Utf8NoBom.GetBytes($json))
}
function Send-Text($ctx, [int]$code, [string]$text) { Send-Bytes $ctx $code 'text/plain; charset=utf-8' ($Utf8NoBom.GetBytes($text)) }

function Read-JsonBody($ctx) {
  $r = New-Object IO.StreamReader($ctx.Request.InputStream, [Text.Encoding]::UTF8)
  try { $b = $r.ReadToEnd() } finally { $r.Close() }
  if ([string]::IsNullOrWhiteSpace($b)) { return [pscustomobject]@{} }
  return ($b | ConvertFrom-Json)
}

function Send-Page($ctx) {
  if (-not (Test-Path -LiteralPath $PageFile)) { Send-Text $ctx 404 'HUD Studio.html is missing from the HUD Studio folder.'; return }
  $html = [IO.File]::ReadAllText($PageFile).Replace('__HUD_TOKEN__', $Token)
  Send-Bytes $ctx 200 'text/html; charset=utf-8' ($Utf8NoBom.GetBytes($html))
}

function Send-StaticFile($ctx, [string]$urlPath) {
  $rel = [Uri]::UnescapeDataString($urlPath.Substring(5)) -replace '/', '\'
  if ($rel -match '\.\.' -or $rel -match ':' -or $rel.StartsWith('\')) { Send-Text $ctx 400 'Bad path'; return }
  $full = [IO.Path]::GetFullPath((Join-Path $AppDir $rel))
  if (-not $full.StartsWith($AppDir + '\', [StringComparison]::OrdinalIgnoreCase)) { Send-Text $ctx 403 'Forbidden'; return }
  $types = @{ '.json' = 'application/json; charset=utf-8'; '.js' = 'text/javascript; charset=utf-8'; '.css' = 'text/css; charset=utf-8'; '.png' = 'image/png'; '.jpg' = 'image/jpeg'; '.svg' = 'image/svg+xml'; '.ico' = 'image/x-icon' }
  $ext = [IO.Path]::GetExtension($full).ToLowerInvariant()
  if (-not $types.ContainsKey($ext) -or -not (Test-Path -LiteralPath $full -PathType Leaf)) { Send-Text $ctx 404 'Not found'; return }
  Send-Bytes $ctx 200 $types[$ext] ([IO.File]::ReadAllBytes($full))
}

function Send-ThemeImage($ctx) {
  $id = Get-QueryValue $ctx 'id'
  $dir = Get-ThemesDir
  if (-not $dir -or -not (Test-SafeName $id)) { Send-Text $ctx 404 'Not found'; return }
  $folder = Join-Path $dir $id
  $img = Join-Path $folder 'PREVIEW.jpg'
  if (-not (Test-Path -LiteralPath $img)) { Send-Text $ctx 404 'Not found'; return }
  Send-Bytes $ctx 200 'image/jpeg' ([IO.File]::ReadAllBytes($img)) 'max-age=3600'
}

function Invoke-Api($ctx, [string]$method, [string]$path) {
  switch ("$method $path") {
    'GET /api/status'  { return (Get-Status) }
    'GET /api/ini' {
      $iniDir = Get-IniDir
      if (-not $iniDir) { return [ordered]@{ ok = $true; files = [ordered]@{} } }
      return [ordered]@{ ok = $true; files = (Get-FolderIniTexts $iniDir) }
    }
    'POST /api/save'   { return (Save-Changes (Read-JsonBody $ctx)) }
    'POST /api/reload-edhm' { return (Invoke-EdhmReload) }
    'GET /api/backups' { return [ordered]@{ ok = $true; dir = $BackupDir; items = (Get-BackupList) } }
    'POST /api/backup' {
      $b = Read-JsonBody $ctx
      $reason = 'Manual backup'
      if ($b.reason) { $reason = ([string]$b.reason) -replace '[^\w \-]', '' }
      $n = New-ColorBackup $reason
      if (-not $n) { throw 'No HUD color files were found, so there is nothing to back up.' }
      return [ordered]@{ ok = $true; name = $n }
    }
    'GET /api/backup' {
      $name = Get-QueryValue $ctx 'name'
      if (-not (Test-SafeName $name) -or -not [IO.Directory]::Exists((Get-LongPath (Join-Path $BackupDir $name)))) { throw 'Backup not found.' }
      return [ordered]@{ ok = $true; name = $name; files = (Get-FolderIniTexts (Join-Path $BackupDir $name)) }
    }
    'GET /api/themes'  { return (Get-ThemeList) }
    'GET /api/theme' {
      $src = Get-QueryValue $ctx 'src'
      $id = Get-QueryValue $ctx 'id'
      if (-not (Test-SafeName $id)) { throw 'Theme not found.' }
      if ($src -eq 'mine') {
        $p = Join-Path $MyThemesDir "$id.json"
        if (-not (Test-Path -LiteralPath $p)) { throw 'Theme not found.' }
        $t = [IO.File]::ReadAllText($p) | ConvertFrom-Json
        return [ordered]@{ ok = $true; id = $id; name = [string]$t.name; author = [string]$t.author; values = $t.values }
      }
      $dir = Get-ThemesDir
      if (-not $dir -or -not (Test-Path -LiteralPath (Join-Path $dir $id))) { throw 'Theme not found.' }
      return [ordered]@{ ok = $true; id = $id; name = ($id -replace '^@', ''); files = (Get-FolderIniTexts (Join-Path $dir $id)) }
    }
    'POST /api/mythemes' { return (Save-MyTheme (Read-JsonBody $ctx)) }
    'POST /api/mythemes/delete' {
      $b = Read-JsonBody $ctx
      $id = [string]$b.id
      $p = Join-Path $MyThemesDir "$id.json"
      if (-not (Test-SafeName $id) -or -not (Test-Path -LiteralPath $p)) { throw 'Theme not found.' }
      Add-Type -AssemblyName Microsoft.VisualBasic
      [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile($p, 'OnlyErrorDialogs', 'SendToRecycleBin')
      $script:themeCache = $null
      return [ordered]@{ ok = $true }
    }
    'POST /api/open'          { $b = Read-JsonBody $ctx; return (Open-Folder ([string]$b.what)) }
    'POST /api/select-game' {
      $b = Read-JsonBody $ctx
      $p = [string]$b.path
      if (-not $p -or -not (Test-Path -LiteralPath (Join-Path $p 'EliteDangerous64.exe'))) { throw 'That folder does not contain EliteDangerous64.exe.' }
      $s = Get-Settings; $s['gameDir'] = $p; $s['gameConfirmed'] = $p; Save-Settings $s
      Select-Game
      return (Get-Status)
    }
    'POST /api/confirm-game' {
      if (-not $script:game) { throw 'No game folder to confirm.' }
      $s = Get-Settings; $s['gameConfirmed'] = [string]$script:game.path; Save-Settings $s
      return (Get-Status)
    }
    'POST /api/heartbeat' { return [ordered]@{ ok = $true } }
    'POST /api/shutdown'  { $script:running = $false; return [ordered]@{ ok = $true } }
  }
  return $null
}

function Invoke-Route($ctx) {
  $req = $ctx.Request
  $hostHeader = [string]$req.Headers['Host']
  if ($hostHeader -ne "localhost:$Port" -and $hostHeader -ne "127.0.0.1:$Port") { Send-Text $ctx 403 'Forbidden'; return }
  $path = $req.Url.AbsolutePath
  $method = $req.HttpMethod
  if ($method -eq 'GET' -and ($path -eq '/' -or $path -eq '/index.html')) { Send-Page $ctx; return }
  if ($method -eq 'GET' -and $path.StartsWith('/app/')) { Send-StaticFile $ctx $path; return }
  if ($method -eq 'GET' -and $path -eq '/theme-img') { Send-ThemeImage $ctx; return }
  if ($path -eq '/api/ping') { Send-Json $ctx ([ordered]@{ app = 'hud-studio'; version = $AppVersion }); return }
  if ($path.StartsWith('/api/')) {
    if ([string]$req.Headers['X-HUD-Token'] -ne $Token) { Send-Json $ctx ([ordered]@{ ok = $false; error = 'HUD Studio was restarted. Please reload this page.' }) 401; return }
    if ($method -eq 'POST' -and -not ([string]$req.ContentType).StartsWith('application/json')) { Send-Json $ctx ([ordered]@{ ok = $false; error = 'Bad request.' }) 400; return }
    $script:lastSeen = Get-Date
    try {
      $result = Invoke-Api $ctx $method $path
      if ($null -eq $result) { Send-Json $ctx ([ordered]@{ ok = $false; error = 'Unknown request.' }) 404 } else { Send-Json $ctx $result }
    } catch {
      $msg = $_.Exception.Message
      if ($msg -match 'denied') { $msg += ' Windows blocked HUD Studio from changing this file. If Elite is open, close it and try again; if it still fails, right-click "Start HUD Studio - Browser version.bat" and choose "Run as administrator".' }
      elseif ($msg -match 'being used by another process') { $msg += ' Another program (usually Elite) has this file open. Close it and try again.' }
      Write-Log "Problem: $msg"
      Send-Json $ctx ([ordered]@{ ok = $false; error = $msg }) 500
    }
    return
  }
  Send-Text $ctx 404 'Not found'
}

# ---------------------------------------------------------------- start
$Host.UI.RawUI.WindowTitle = 'Elite HUD Studio'
Select-Game

$listener = $null
for ($p = $Port; $p -lt $Port + 10; $p++) {
  try {
    $r = Invoke-RestMethod -Uri "http://localhost:$p/api/ping" -TimeoutSec 2
    if ($r.app -eq 'hud-studio') {
      Write-Host 'HUD Studio is already running - opening it in your browser.'
      if (-not $NoBrowser) { Start-Process "http://localhost:$p/" }
      exit 0
    }
  } catch {}
  try {
    $l = New-Object System.Net.HttpListener
    $l.Prefixes.Add("http://localhost:$p/")
    $l.Start()
    $listener = $l; $Port = $p
    break
  } catch {}
}
if (-not $listener) { Write-Host 'Could not start HUD Studio: no free port between 47810 and 47819.'; exit 1 }

$url = "http://localhost:$Port/"
Write-Host ''
Write-Host '  ELITE HUD STUDIO is running.' -ForegroundColor Cyan
Write-Host "  Your browser should open by itself. If it doesn't, go to: $url"
Write-Host '  Keep this window open while you use HUD Studio (you can minimize it).'
Write-Host "  It closes by itself $IdleMinutes minutes after you close the HUD Studio tab."
if ($script:game) { Write-Host "  Game folder: $($script:game.path)" } else { Write-Host '  Elite Dangerous was not found automatically - you can pick the folder in HUD Studio.' -ForegroundColor Yellow }
if ($TestMode) { Write-Host '  TEST MODE: the "Elite is running" safety check is off.' -ForegroundColor Yellow }
Write-Host ''
if (-not $NoBrowser) { Start-Process $url }

$script:running = $true
$script:lastSeen = Get-Date
try {
  while ($script:running -and $listener.IsListening) {
    $ar = $listener.BeginGetContext($null, $null)
    while (-not $ar.AsyncWaitHandle.WaitOne(500)) {
      if (((Get-Date) - $script:lastSeen).TotalMinutes -ge $IdleMinutes) { $script:running = $false; Write-Log 'No HUD Studio tab has been open for a while - closing.'; break }
    }
    if (-not $script:running) { break }
    $ctx = $listener.EndGetContext($ar)
    try { Invoke-Route $ctx } catch { Write-Log "Problem: $($_.Exception.Message)"; try { $ctx.Response.Abort() } catch {} }
  }
} finally {
  $listener.Stop()
  $listener.Close()
  Write-Log 'HUD Studio stopped.'
}
