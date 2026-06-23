param(
    [Parameter(Mandatory = $true)]
    [string] $SourceDir,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath,

    [Parameter(Mandatory = $true)]
    [string] $Version,

    [string] $ProductName = "clodlogs",
    [string] $Manufacturer = "clodlogs",
    [string] $UpgradeCode = "4f7b5b83-2c89-4e69-8d1e-0c383477470c",
    [string] $ShortcutTarget = "bin\launcher.exe"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function ConvertTo-WixId {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Prefix,

        [Parameter(Mandatory = $true)]
        [int] $Index
    )

    return "$Prefix$Index"
}

function ConvertTo-XmlText {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Value
    )

    return [System.Security.SecurityElement]::Escape($Value)
}

function New-StableGuid {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Value
    )

    $namespace = "dev.tobitege.clodlogs.msi"
    $normalizedValue = $Value.Replace("/", "\").ToLowerInvariant()
    $inputBytes = [System.Text.Encoding]::UTF8.GetBytes("$namespace|$normalizedValue")
    $md5 = [System.Security.Cryptography.MD5]::Create()

    try {
        $hash = $md5.ComputeHash($inputBytes)
    } finally {
        $md5.Dispose()
    }

    $guidBytes = New-Object byte[] 16
    [Array]::Copy($hash, $guidBytes, 16)
    $guidBytes[7] = ($guidBytes[7] -band 0x0f) -bor 0x30
    $guidBytes[8] = ($guidBytes[8] -band 0x3f) -bor 0x80

    return ([Guid]::new($guidBytes)).ToString()
}

function Get-WixTool {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ToolName
    )

    $command = Get-Command $ToolName -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $candidateRoots = @(
        $env:WIX,
        "${env:ProgramFiles(x86)}\WiX Toolset v3.14\bin",
        "${env:ProgramFiles(x86)}\WiX Toolset v3.11\bin"
    ) | Where-Object { $_ }

    foreach ($root in $candidateRoots) {
        $candidate = Join-Path $root $ToolName
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    throw "Could not find $ToolName. Install WiX Toolset v3 before running this script."
}

$resolvedSourceDir = (Resolve-Path -LiteralPath $SourceDir).Path
$resolvedOutputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
$outputDir = Split-Path -Parent $resolvedOutputPath
$workDir = Join-Path $outputDir "wix-work"
$wxsPath = Join-Path $workDir "clodlogs.wxs"
$wixObjPath = Join-Path $workDir "clodlogs.wixobj"
$shortcutTargetPath = Join-Path $resolvedSourceDir $ShortcutTarget

if (-not (Test-Path -LiteralPath $shortcutTargetPath)) {
    throw "Shortcut target does not exist: $shortcutTargetPath"
}

New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
if (Test-Path -LiteralPath $workDir) {
    Remove-Item -LiteralPath $workDir -Recurse -Force
}
New-Item -ItemType Directory -Path $workDir -Force | Out-Null

$files = Get-ChildItem -LiteralPath $resolvedSourceDir -File -Recurse | Sort-Object FullName
if ($files.Count -eq 0) {
    throw "No files found in source directory: $resolvedSourceDir"
}

$directories = Get-ChildItem -LiteralPath $resolvedSourceDir -Directory -Recurse | Sort-Object FullName
$directoryIds = @{}
$directoryIds[$resolvedSourceDir] = "INSTALLFOLDER"

$dirIndex = 1
foreach ($directory in $directories) {
    $directoryIds[$directory.FullName] = ConvertTo-WixId -Prefix "Dir" -Index $dirIndex
    $dirIndex++
}

$directoryChildren = @{}
$fileChildren = @{}

foreach ($directory in @($resolvedSourceDir) + $directories.FullName) {
    $directoryChildren[$directory] = New-Object System.Collections.Generic.List[string]
    $fileChildren[$directory] = New-Object System.Collections.Generic.List[System.IO.FileInfo]
}

foreach ($directory in $directories) {
    $parent = Split-Path -Parent $directory.FullName
    $directoryChildren[$parent].Add($directory.FullName)
}

foreach ($file in $files) {
    $fileChildren[$file.DirectoryName].Add($file)
}

$componentRefs = New-Object System.Collections.Generic.List[string]
$fileIndexRef = [ref] 1
$componentIndexRef = [ref] 1

function Write-WixDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string] $DirectoryPath,

        [Parameter(Mandatory = $true)]
        [string] $SourceRoot,

        [Parameter(Mandatory = $true)]
        [System.Text.StringBuilder] $Builder,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary] $DirectoryIds,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary] $DirectoryChildren,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary] $FileChildren,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[string]] $ComponentRefs,

        [Parameter(Mandatory = $true)]
        [ref] $FileIndex,

        [Parameter(Mandatory = $true)]
        [ref] $ComponentIndex,

        [int] $Depth = 6
    )

    $indent = " " * $Depth

    foreach ($file in $FileChildren[$DirectoryPath]) {
        $componentId = ConvertTo-WixId -Prefix "Cmp" -Index $ComponentIndex.Value
        $fileId = ConvertTo-WixId -Prefix "File" -Index $FileIndex.Value
        $relativePath = $file.FullName.Substring($SourceRoot.Length).TrimStart("\", "/")
        $componentGuid = New-StableGuid -Value "file:$relativePath"
        $source = ConvertTo-XmlText -Value $file.FullName
        $name = ConvertTo-XmlText -Value $file.Name

        [void] $Builder.AppendLine("$indent<Component Id=`"$componentId`" Guid=`"$componentGuid`" Win64=`"yes`">")
        [void] $Builder.AppendLine("$indent  <File Id=`"$fileId`" Source=`"$source`" Name=`"$name`" KeyPath=`"yes`" />")
        [void] $Builder.AppendLine("$indent</Component>")
        $ComponentRefs.Add($componentId)
        $ComponentIndex.Value++
        $FileIndex.Value++
    }

    foreach ($childPath in $DirectoryChildren[$DirectoryPath]) {
        $directoryId = $DirectoryIds[$childPath]
        $name = ConvertTo-XmlText -Value (Split-Path -Leaf $childPath)

        [void] $Builder.AppendLine("$indent<Directory Id=`"$directoryId`" Name=`"$name`">")
        Write-WixDirectory `
            -DirectoryPath $childPath `
            -SourceRoot $SourceRoot `
            -Builder $Builder `
            -DirectoryIds $DirectoryIds `
            -DirectoryChildren $DirectoryChildren `
            -FileChildren $FileChildren `
            -ComponentRefs $ComponentRefs `
            -FileIndex $FileIndex `
            -ComponentIndex $ComponentIndex `
            -Depth ($Depth + 2)
        [void] $Builder.AppendLine("$indent</Directory>")
    }
}

$sourceName = ConvertTo-XmlText -Value (Split-Path -Leaf $resolvedSourceDir)
$productNameXml = ConvertTo-XmlText -Value $ProductName
$manufacturerXml = ConvertTo-XmlText -Value $Manufacturer
$shortcutDescription = ConvertTo-XmlText -Value "$ProductName desktop app"
$relativeShortcutTarget = ConvertTo-XmlText -Value $ShortcutTarget
$upgradeCodeXml = ConvertTo-XmlText -Value $UpgradeCode

$builder = [System.Text.StringBuilder]::new()
[void] $builder.AppendLine('<?xml version="1.0" encoding="UTF-8"?>')
[void] $builder.AppendLine('<Wix xmlns="http://schemas.microsoft.com/wix/2006/wi">')
[void] $builder.AppendLine("  <Product Id=`"*`" Name=`"$productNameXml`" Language=`"1033`" Version=`"$Version`" Manufacturer=`"$manufacturerXml`" UpgradeCode=`"$upgradeCodeXml`">")
[void] $builder.AppendLine('    <Package InstallerVersion="500" Compressed="yes" InstallScope="perMachine" Platform="x64" />')
[void] $builder.AppendLine('    <MajorUpgrade DowngradeErrorMessage="A newer version of clodlogs is already installed." />')
[void] $builder.AppendLine('    <MediaTemplate EmbedCab="yes" />')
[void] $builder.AppendLine('    <Directory Id="TARGETDIR" Name="SourceDir">')
[void] $builder.AppendLine('      <Directory Id="ProgramFiles64Folder">')
[void] $builder.AppendLine("        <Directory Id=`"INSTALLFOLDER`" Name=`"$sourceName`">")

Write-WixDirectory `
    -DirectoryPath $resolvedSourceDir `
    -SourceRoot $resolvedSourceDir `
    -Builder $builder `
    -DirectoryIds $directoryIds `
    -DirectoryChildren $directoryChildren `
    -FileChildren $fileChildren `
    -ComponentRefs $componentRefs `
    -FileIndex $fileIndexRef `
    -ComponentIndex $componentIndexRef

[void] $builder.AppendLine('        </Directory>')
[void] $builder.AppendLine('      </Directory>')
[void] $builder.AppendLine('      <Directory Id="ProgramMenuFolder">')
[void] $builder.AppendLine("        <Directory Id=`"ApplicationProgramsFolder`" Name=`"$productNameXml`" />")
[void] $builder.AppendLine('      </Directory>')
[void] $builder.AppendLine('    </Directory>')
[void] $builder.AppendLine('    <DirectoryRef Id="ApplicationProgramsFolder">')
$shortcutComponentGuid = New-StableGuid -Value "shortcut:start-menu"
[void] $builder.AppendLine("      <Component Id=`"ApplicationShortcut`" Guid=`"$shortcutComponentGuid`" Win64=`"yes`">")
[void] $builder.AppendLine("        <Shortcut Id=`"ApplicationStartMenuShortcut`" Name=`"$productNameXml`" Description=`"$shortcutDescription`" Target=`"[INSTALLFOLDER]$relativeShortcutTarget`" WorkingDirectory=`"INSTALLFOLDER`" />")
[void] $builder.AppendLine('        <RemoveFolder Id="ApplicationProgramsFolder" On="uninstall" />')
[void] $builder.AppendLine('        <RegistryValue Root="HKLM" Key="Software\clodlogs" Name="installed" Type="integer" Value="1" KeyPath="yes" />')
[void] $builder.AppendLine('      </Component>')
[void] $builder.AppendLine('    </DirectoryRef>')
[void] $builder.AppendLine('    <Feature Id="MainFeature" Title="clodlogs" Level="1">')
[void] $builder.AppendLine('      <ComponentRef Id="ApplicationShortcut" />')

foreach ($componentRef in $componentRefs) {
    [void] $builder.AppendLine("      <ComponentRef Id=`"$componentRef`" />")
}

[void] $builder.AppendLine('    </Feature>')
[void] $builder.AppendLine('  </Product>')
[void] $builder.AppendLine('</Wix>')

$encoding = [System.Text.UTF8Encoding]::new($true)
[System.IO.File]::WriteAllText($wxsPath, $builder.ToString(), $encoding)

$candle = Get-WixTool -ToolName "candle.exe"
$light = Get-WixTool -ToolName "light.exe"

$candleArgs = @(
    "-nologo",
    "-arch",
    "x64",
    "-out",
    $wixObjPath,
    $wxsPath
)
& $candle @candleArgs
if ($LASTEXITCODE -ne 0) {
    throw "candle.exe failed with exit code $LASTEXITCODE"
}

$lightArgs = @(
    "-nologo",
    "-out",
    $resolvedOutputPath,
    $wixObjPath
)
& $light @lightArgs
if ($LASTEXITCODE -ne 0) {
    throw "light.exe failed with exit code $LASTEXITCODE"
}

Write-Host "Created MSI: $resolvedOutputPath"
