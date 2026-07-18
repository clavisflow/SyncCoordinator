param(
    [Parameter(Mandatory = $true)]
    [string]$PresentationPath
)

$resolved = (Resolve-Path -LiteralPath $PresentationPath).Path
$tempPath = "$resolved.morph.tmp"

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$source = [System.IO.Compression.ZipFile]::OpenRead($resolved)
$target = [System.IO.Compression.ZipFile]::Open($tempPath, [System.IO.Compression.ZipArchiveMode]::Create)

try {
    foreach ($entry in $source.Entries) {
        $newEntry = $target.CreateEntry($entry.FullName, [System.IO.Compression.CompressionLevel]::Optimal)
        $input = $entry.Open()
        $output = $newEntry.Open()
        try {
            if ($entry.FullName -match '^ppt/slides/slide(\d+)\.xml$') {
                $reader = [System.IO.StreamReader]::new($input, [System.Text.Encoding]::UTF8, $true)
                $xml = $reader.ReadToEnd()
                $reader.Dispose()
                $slideNumber = [int]$Matches[1]

                if ($slideNumber -le 7) {
                    $advance = ' advClick="1" advTm="3200"'
                }
                else {
                    $advance = ' advClick="1"'
                }

                if ($slideNumber -eq 1) {
                    $transition = "<p:transition spd=`"slow`"$advance><p:fade/></p:transition>"
                }
                else {
                    $transition = @"
<mc:AlternateContent xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006">
  <mc:Choice xmlns:p159="http://schemas.microsoft.com/office/powerpoint/2015/09/main" Requires="p159">
    <p:transition spd="slow" xmlns:p14="http://schemas.microsoft.com/office/powerpoint/2010/main" p14:dur="1500"$advance>
      <p159:morph option="byObject"/>
    </p:transition>
  </mc:Choice>
  <mc:Fallback>
    <p:transition spd="slow"$advance><p:fade/></p:transition>
  </mc:Fallback>
</mc:AlternateContent>
"@
                }

                $xml = $xml -replace '</p:sld>$', "$transition</p:sld>"
                $writer = [System.IO.StreamWriter]::new($output, [System.Text.UTF8Encoding]::new($false))
                $writer.Write($xml)
                $writer.Flush()
                $writer.Dispose()
            }
            else {
                $input.CopyTo($output)
            }
        }
        finally {
            $input.Dispose()
            $output.Dispose()
        }
    }
}
finally {
    $source.Dispose()
    $target.Dispose()
}

Move-Item -LiteralPath $tempPath -Destination $resolved -Force
Write-Host "Morph transitions added: $resolved"
