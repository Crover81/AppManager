# Compila AppManager con il compilatore C# incluso in Windows (.NET Framework 4.8).
# Uso:  powershell -ExecutionPolicy Bypass -File build.ps1 [-Harness] [-AsInvoker]
param(
    [switch]$Harness,     # compila anche lo strumento di test da console (tools\Harness.exe)
    [switch]$AsInvoker    # versione senza richiesta di privilegi di amministratore (per i test)
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$fw   = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc  = Join-Path $fw 'csc.exe'
$wpf  = Join-Path $fw 'WPF'
$out  = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $out | Out-Null

$refs = @(
    'System.dll', 'System.Core.dll', 'System.Xml.dll', 'System.Xml.Linq.dll', 'System.Xaml.dll',
    'System.Web.Extensions.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll',
    'System.Drawing.dll', 'System.Windows.Forms.dll', 'Microsoft.CSharp.dll',
    (Join-Path $wpf 'PresentationCore.dll'), (Join-Path $wpf 'PresentationFramework.dll'), (Join-Path $wpf 'WindowsBase.dll')
) | ForEach-Object { "/r:$_" }

$core = Get-ChildItem (Join-Path $root 'src\core') -Filter *.cs | ForEach-Object { $_.FullName }
$ui   = $core + (Get-ChildItem (Join-Path $root 'src\app') -Filter *.cs | ForEach-Object { $_.FullName })
$xamlFiles = Get-ChildItem (Join-Path $root 'ui') -Filter *.xaml -ErrorAction SilentlyContinue
foreach ($f in $xamlFiles) {
    # lo XAML viene caricato a runtime: un file malformato va bloccato qui, non all'avvio del programma
    try { [xml](Get-Content $f.FullName -Raw -Encoding UTF8) | Out-Null }
    catch { throw "XAML non valido in $($f.Name): $($_.Exception.Message)" }
}
$xaml = $xamlFiles | ForEach-Object { "/resource:$($_.FullName),AppManager.ui.$($_.Name)" }
# cataloghi delle lingue: incorporati nell'eseguibile, che resta un file solo
$lang = Get-ChildItem (Join-Path $root 'ui\lang') -Filter *.txt -ErrorAction SilentlyContinue | ForEach-Object { "/resource:$($_.FullName),AppManager.ui.lang.$($_.Name)" }

function Invoke-Csc([string[]]$csArgs) {
    & $csc @csArgs
    if ($LASTEXITCODE -ne 0) { throw "Compilazione fallita" }
}

$common = @('/nologo', '/optimize+', '/platform:anycpu', '/utf8output', '/nowarn:1591') + $refs

if ($Harness) {
    $tools = Get-ChildItem (Join-Path $root 'tools') -Filter *.cs | ForEach-Object { $_.FullName }
    Invoke-Csc ($common + @('/target:exe', "/out:$(Join-Path $root 'tools\Harness.exe')", '/define:HARNESS') + $lang + $core + $tools)
    Write-Host 'OK  tools\Harness.exe'
}

if (Test-Path (Join-Path $root 'src\app\App.cs')) {
    $manifest = if ($AsInvoker) { 'app.asinvoker.manifest' } else { 'app.manifest' }
    $name = if ($AsInvoker) { 'AppManager.Test.exe' } else { 'AppManager.exe' }
    $icon = Join-Path $root 'ui\app.ico'
    # l'icona serve anche all'area di notifica: è incorporata pure come risorsa
    $iconArg = if (Test-Path $icon) { @("/win32icon:$icon", "/resource:$icon,AppManager.ui.app.ico") } else { @() }
    Invoke-Csc ($common + @('/target:winexe', "/out:$(Join-Path $out $name)", "/win32manifest:$(Join-Path $root $manifest)") + $iconArg + $xaml + $lang + $ui)
    Write-Host "OK  bin\$name"

    if (-not $AsInvoker) {
        # Cartella "Portable": i soli file per usare il programma da una chiavetta
        $portable = Join-Path $root 'Portable'
        New-Item -ItemType Directory -Force $portable | Out-Null
        try { Copy-Item (Join-Path $out $name) (Join-Path $portable $name) -Force -ErrorAction Stop }
        catch { Write-Warning "Portable\$name è in uso (AppManager è aperto): chiudilo e ricompila per aggiornare la cartella Portable." }
        # file delle versioni precedenti: il programma è sempre portable, non serve più nessun file che lo attivi
        foreach ($old in 'GameManager.exe', 'GameManager.portable', 'AppManager.portable') {
            $p = Join-Path $portable $old
            if (Test-Path $p) { try { Remove-Item $p -Force -ErrorAction Stop } catch { Write-Warning "Impossibile rimuovere Portable\$old (in uso?)" } }
        }
        $utf8bom = New-Object System.Text.UTF8Encoding($true)
        [IO.File]::WriteAllText((Join-Path $portable 'LEGGIMI.txt'), @"
AppManager
==========

ITALIANO
Avvio: doppio clic su AppManager.exe (Windows chiede i permessi di amministratore).
Requisiti: Windows 10 o 11. Non va installato: usa .NET Framework 4.8, già incluso in Windows.
La lingua è quella di Windows (inglese se non è tra quelle disponibili) e si cambia nelle Impostazioni.

AppManager è sempre portable: impostazioni, registro attività e immagini scaricate stanno nella
cartella Dati, accanto al programma. Riconosce da solo ogni PC: cartelle ed elementi nascosti sono
ricordati per ciascun PC, e su uno nuovo si parte puliti. Le immagini non più usate e i file
temporanei vengono tolti da soli.
I backup e i profili di salvataggio restano sul PC a cui appartengono (Documenti\AppManager Backup
e Documenti\AppManager Salvataggi); le cartelle si possono cambiare nelle Impostazioni.

Per aggiornare il programma basta sostituire AppManager.exe: la cartella Dati resta.

ENGLISH
Start: double-click AppManager.exe (Windows asks for administrator permission).
Requirements: Windows 10 or 11. No installation needed: it uses .NET Framework 4.8, included in Windows.
The language follows Windows (English if not available) and can be changed in Settings.

AppManager is always portable: settings, activity log and downloaded images live in the Dati folder
next to the program. It recognises each PC by itself: folders and hidden items are remembered per PC,
and a new PC starts clean. Images no longer used and temporary files are removed automatically.
Backups and save profiles stay on the PC they belong to (Documents\AppManager Backup and
Documents\AppManager Salvataggi); these folders can be changed in Settings.

To update the program just replace AppManager.exe: the Dati folder stays.
"@.Replace("`r`n", "`n").Replace("`n", "`r`n"), $utf8bom)
        Write-Host "OK  Portable\ (AppManager.exe, LEGGIMI.txt)"
    }
}
