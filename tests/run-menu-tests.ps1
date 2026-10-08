param([string]$JavaHome = $env:JAVA_HOME)
$ErrorActionPreference = 'Stop'
if (-not $JavaHome) { throw 'Set JAVA_HOME to a JDK 21 installation, or pass -JavaHome.' }
$repoRoot = Split-Path -Parent $PSScriptRoot
$classes = Join-Path $repoRoot 'fabric/build/menu-layout-tests'
New-Item -ItemType Directory -Path $classes -Force | Out-Null
& (Join-Path $JavaHome 'bin/javac.exe') --release 21 -encoding UTF-8 -d $classes `
	(Join-Path $repoRoot 'fabric/src/main/java/dev/ultracraft/MenuLayout.java') `
	(Join-Path $PSScriptRoot 'MenuLayoutTest.java')
if ($LASTEXITCODE -ne 0) { throw 'MenuLayout test compilation failed.' }
& (Join-Path $JavaHome 'bin/java.exe') -ea -cp $classes dev.ultracraft.MenuLayoutTest
if ($LASTEXITCODE -ne 0) { throw 'MenuLayout tests failed.' }
