param(
    [Parameter(Mandatory = $true)]
    [string]$ScreenshotDirectory,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [string]$CharacterDirectory = (Join-Path $PSScriptRoot 'video-assets\characters'),

    [switch]$WorkerFlowPreviewOnly
)

$ErrorActionPreference = 'Stop'
$frameRate = 30
$targetWidth = 1920
$targetHeight = 1080

function Invoke-Ffmpeg {
    param([string[]]$Arguments)

    & ffmpeg @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "ffmpeg failed with exit code $LASTEXITCODE."
    }
}

function Resolve-Asset {
    param([string]$FileName)

    $assetPath = Join-Path $ScreenshotDirectory $FileName
    if (Test-Path -LiteralPath $assetPath -PathType Leaf) {
        return (Resolve-Path -LiteralPath $assetPath).Path
    }

    $characterPath = Join-Path $CharacterDirectory $FileName
    if (Test-Path -LiteralPath $characterPath -PathType Leaf) {
        return (Resolve-Path -LiteralPath $characterPath).Path
    }

    $videoAssetPath = Join-Path (Join-Path $PSScriptRoot 'video-assets') $FileName
    if (Test-Path -LiteralPath $videoAssetPath -PathType Leaf) {
        return (Resolve-Path -LiteralPath $videoAssetPath).Path
    }

    throw "Asset not found in screenshot or character directories: $FileName"
}

$resolvedScreenshotDirectory = (Resolve-Path -LiteralPath $ScreenshotDirectory).Path
$resolvedOutputDirectory = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($OutputPath))
if ([string]::IsNullOrWhiteSpace($resolvedOutputDirectory)) {
    throw 'OutputPath must include a directory.'
}

New-Item -ItemType Directory -Force -Path $resolvedOutputDirectory | Out-Null
$workRoot = Join-Path $resolvedOutputDirectory '.rough-cut-work'
New-Item -ItemType Directory -Force -Path $workRoot | Out-Null

$segments = @(
    [pscustomobject]@{ Kind = 'Motion'; Duration = 5; Scene = 'IntroPortal'; Background = '0x10251d'; Files = @(); Caption = '顧客がポータルから、件名を更新' },
    [pscustomobject]@{ Kind = 'Motion'; Duration = 5; Scene = 'IntroCrm'; Background = '0x10251d'; Files = @(); Caption = 'その間にCRM担当者も、同じ件名を更新' },
    [pscustomobject]@{ Kind = 'Motion'; Duration = 5; Scene = 'IntroConflict'; Background = '0x10251d'; Files = @(); Caption = '前回同期後に双方で変わると、競合を検知' },
    [pscustomobject]@{ Kind = 'Focus'; Duration = 6; Files = @('33-routes-bidirectional.png'); Crop = '1250:540:330:80'; Caption = '2つの双方向ルートで、3システムを接続' },
    [pscustomobject]@{ Kind = 'Focus'; Duration = 7; Files = @('mapping-crm-field-full.png'); Crop = '900:420:320:260'; Caption = '異なるテーブル構造を、マッピングとして定義' },
    [pscustomobject]@{ Kind = 'PersonCard'; Duration = 5; Files = @('customer-opening-ai.png'); CardSide = 'Right'; CardSystem = 'CUSTOMER PORTAL'; CardTitle = '問い合わせ'; CardValue = '冷風が出ない\N早めの訪問を希望'; CardColor = '215CD9'; Caption = '問い合わせをポータルから管理システムへ' },
    [pscustomobject]@{ Kind = 'PersonCard'; Duration = 5; Files = @('crm-operator-entry-ai.png'); CardSide = 'Right'; CardSystem = 'SERVICE CRM'; CardTitle = '作業指示'; CardValue = '訪問予定\N7月21日 10:00–12:00'; CardColor = 'EB6F1F'; Caption = '管理担当者が作業指示を登録' },
    [pscustomobject]@{ Kind = 'Motion'; Duration = 4; Scene = 'WorkOrderFlow'; Background = '0x10251d'; Files = @(); Caption = '作業指示を現場の作業システムへ' },
    [pscustomobject]@{ Kind = 'PersonCard'; Duration = 4; Files = @('field-technician-inspection-ai.png'); CardSide = 'Left'; CardSystem = 'FIELD SERVICE'; CardTitle = '訪問作業'; CardValue = '室外機を含む\N現地点検を実施'; CardColor = '6E760F'; Caption = '現場担当者が作業を実施' },
    [pscustomobject]@{ Kind = 'PersonCard'; Duration = 4; Files = @('field-technician-report-ai.png'); CardSide = 'Left'; CardSystem = 'FIELD SERVICE'; CardTitle = '作業結果'; CardValue = '吸込口を清掃\N冷媒圧を確認'; CardColor = '6E760F'; Caption = '作業結果を管理システムへ返却' },
    [pscustomobject]@{ Kind = 'PersonCard'; Duration = 6; Files = @('customer-opening-ai.png'); CardSide = 'Right'; CardSystem = 'CUSTOMER PORTAL'; CardTitle = 'Portalから届いた値'; CardValue = '冷風が出ない\N（お客様から再連絡）'; CardColor = '215CD9'; Caption = '顧客が件名を更新' },
    [pscustomobject]@{ Kind = 'PersonCard'; Duration = 6; Files = @('crm-operator-entry-ai.png'); CardSide = 'Right'; CardSystem = 'SERVICE CRM'; CardTitle = 'CRMに保存された現在値'; CardValue = '冷房不良・\N訪問点検が必要'; CardColor = 'EB6F1F'; Caption = 'CRM担当者も同じ件名を更新' },
    [pscustomobject]@{ Kind = 'Motion'; Duration = 7; Scene = 'ConflictCompare'; Background = '0xf2efe8'; Files = @(); Caption = '前回同期後に、同じ項目が双方で変更' },
    [pscustomobject]@{ Kind = 'Motion'; Duration = 6; Scene = 'ConflictHold'; Background = '0x10251d'; Files = @(); Caption = '自動上書きせず、解決フローへ' },
    [pscustomobject]@{ Kind = 'Focus'; Duration = 8; Files = @('23-conflict-rehearsal-unselected.png'); Crop = '1360:680:520:120'; Caption = '前回値・受信値・現在値を比較' },
    [pscustomobject]@{ Kind = 'Focus'; Duration = 7; Files = @('23-conflict-rehearsal-unselected.png'); Crop = '1050:180:560:290'; Caption = '詳細は、CRMの現在値を維持' },
    [pscustomobject]@{ Kind = 'Focus'; Duration = 7; Files = @('23-conflict-rehearsal-unselected.png'); Crop = '1050:150:560:400'; Caption = '件名は、ポータルからの受信値を採用' },
    [pscustomobject]@{ Kind = 'Focus'; Duration = 8; Files = @('24-conflict-rehearsal-ready-to-resolve.png'); Crop = '1360:680:520:120'; Caption = '競合した項目ごとに、採用値を選択' },
    [pscustomobject]@{ Kind = 'Focus'; Duration = 6; Files = @('24-conflict-rehearsal-ready-to-resolve.png'); Crop = '1050:150:560:400'; Caption = '件名は受信値' },
    [pscustomobject]@{ Kind = 'Focus'; Duration = 6; Files = @('24-conflict-rehearsal-ready-to-resolve.png'); Crop = '1050:180:560:290'; Caption = '詳細は現在値' },
    [pscustomobject]@{ Kind = 'Focus'; Duration = 5; Files = @('24-conflict-rehearsal-ready-to-resolve.png'); Crop = '1360:360:520:190'; Caption = '採用結果を項目ごとに決定' },
    [pscustomobject]@{ Kind = 'Focus'; Duration = 8; Files = @('24-conflict-rehearsal-ready-to-resolve.png'); Crop = '1360:460:520:430'; Caption = '誰が、何を採用したか。解決メモとともに記録' },
    [pscustomobject]@{ Kind = 'Focus'; Duration = 5; Files = @('25-conflict-resolve-confirm.png'); Crop = '1150:650:385:190'; Caption = '内容を確認して、解決を登録' },
    [pscustomobject]@{ Kind = 'WorkerFlow'; Duration = 8; Files = @(); Caption = 'Workerがバックグラウンドで解決内容を反映' },
    [pscustomobject]@{ Kind = 'Still'; Duration = 7; Files = @('27-crm-conflict-resolved.png'); Caption = '採用した内容をCRMへ反映' },
    [pscustomobject]@{ Kind = 'Focus'; Duration = 8; Files = @('26-conflict-resolved-final.png'); Crop = '1360:760:520:100'; Caption = '誰が、何を採用したかを記録' },
    [pscustomobject]@{ Kind = 'Focus'; Duration = 7; Files = @('30-operations-resolution-complete.png'); Crop = '1360:720:520:100'; Caption = '競合の発生から解決まで追跡できる' },
    [pscustomobject]@{ Kind = 'PersonCard'; Duration = 7; Files = @('customer-relief-ai.png'); CardSide = 'Right'; CardSystem = 'CUSTOMER PORTAL'; CardTitle = '担当者からの回答'; CardValue = '訪問点検が完了しました\N冷房運転を確認済み'; CardColor = '215CD9'; Caption = '変更を止めずに、データを正しくつなぐ。' },
    [pscustomobject]@{ Kind = 'Solid'; Duration = 8; Files = @(); Caption = $null }
)

$totalDuration = ($segments | Measure-Object -Property Duration -Sum).Sum
if ($totalDuration -ne 180) {
    throw "Segment duration must total 180 seconds, but was $totalDuration."
}

$motionAssHeader = @'
[Script Info]
ScriptType: v4.00+
PlayResX: 1920
PlayResY: 1080
WrapStyle: 2
ScaledBorderAndShadow: yes

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Shape,Meiryo,20,&H00FFFFFF,&H000000FF,&H00FFFFFF,&H00000000,0,0,0,0,100,100,0,0,1,0,0,7,0,0,0,1
Style: Kicker,Meiryo,25,&H00A6D8C9,&H000000FF,&H0010251D,&H00000000,-1,0,0,0,100,100,3,0,1,0,0,5,30,30,30,1
Style: Big,Meiryo,50,&H00FFFFFF,&H000000FF,&H0010251D,&H00000000,-1,0,0,0,100,100,0,0,1,0,0,5,30,30,30,1
Style: Medium,Meiryo,36,&H00FFFFFF,&H000000FF,&H0010251D,&H00000000,-1,0,0,0,100,100,0,0,1,0,0,5,30,30,30,1
Style: Small,Meiryo,26,&H00DDECE5,&H000000FF,&H0010251D,&H00000000,0,0,0,0,100,100,0,0,1,0,0,5,30,30,30,1
Style: Dark,Meiryo,34,&H00102720,&H000000FF,&H00102720,&H00000000,-1,0,0,0,100,100,0,0,1,0,0,5,30,30,30,1
Style: DarkSmall,Meiryo,25,&H003D4A45,&H000000FF,&H003D4A45,&H00000000,0,0,0,0,100,100,0,0,1,0,0,5,30,30,30,1
Style: Badge,Meiryo,30,&H00FFFFFF,&H000000FF,&H0010251D,&H0010251D,-1,0,0,0,100,100,2,0,3,0,0,5,30,30,30,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
'@

function Get-MotionEvents {
    param([string]$Scene)

    switch ($Scene) {
        'IntroPortal' {
            return @'
Dialogue: 0,0:00:00.00,0:00:05.00,Kicker,,0,0,0,,{\pos(960,190)\fad(180,250)}CUSTOMER PORTAL / UPDATE
Dialogue: 0,0:00:00.00,0:00:05.00,Shape,,0,0,0,,{\an7\move(-1400,320,300,320,0,1100)\p1\c&H00215CD9&\bord0\fad(100,250)}m 0 0 l 1320 0 l 1320 390 l 0 390
Dialogue: 1,0:00:00.00,0:00:05.00,DarkSmall,,0,0,0,,{\move(-740,430,960,430,0,1100)\fad(100,250)}Portalから届いた値
Dialogue: 1,0:00:00.00,0:00:05.00,Dark,,0,0,0,,{\move(-740,545,960,545,0,1100)\fad(100,250)}冷風が出ない\N（お客様から再連絡）
'@
        }
        'IntroCrm' {
            return @'
Dialogue: 0,0:00:00.00,0:00:05.00,Kicker,,0,0,0,,{\pos(960,190)\fad(180,250)}SERVICE CRM / UPDATE
Dialogue: 0,0:00:00.00,0:00:05.00,Shape,,0,0,0,,{\an7\move(2050,320,300,320,0,1100)\p1\c&H00EB6F1F&\bord0\fad(100,250)}m 0 0 l 1320 0 l 1320 390 l 0 390
Dialogue: 1,0:00:00.00,0:00:05.00,Big,,0,0,0,,{\move(2710,430,960,430,0,1100)\fad(100,250)}CRMに保存された現在値
Dialogue: 1,0:00:00.00,0:00:05.00,Big,,0,0,0,,{\move(2710,545,960,545,0,1100)\fad(100,250)}冷房不良・訪問点検が必要
'@
        }
        'IntroConflict' {
            return @'
Dialogue: 0,0:00:00.00,0:00:05.00,Kicker,,0,0,0,,{\pos(960,150)\fad(150,250)}SAME FIELD / TWO UPDATES
Dialogue: 0,0:00:00.00,0:00:05.00,Shape,,0,0,0,,{\an7\move(80,285,430,285,0,1400)\p1\c&H00215CD9&\bord0}m 0 0 l 700 0 l 700 260 l 0 260
Dialogue: 0,0:00:00.00,0:00:05.00,Shape,,0,0,0,,{\an7\move(1140,285,790,285,0,1400)\p1\c&H00EB6F1F&\bord0}m 0 0 l 700 0 l 700 260 l 0 260
Dialogue: 1,0:00:00.00,0:00:05.00,DarkSmall,,0,0,0,,{\move(430,350,780,350,0,1400)}PORTAL
Dialogue: 1,0:00:00.00,0:00:05.00,Medium,,0,0,0,,{\move(430,430,780,430,0,1400)\c&H0010251D&}冷風が出ない\N（再連絡）
Dialogue: 1,0:00:00.00,0:00:05.00,Small,,0,0,0,,{\move(1490,350,1140,350,0,1400)}CRM
Dialogue: 1,0:00:00.00,0:00:05.00,Medium,,0,0,0,,{\move(1490,430,1140,430,0,1400)}冷房不良・\N訪問点検が必要
Dialogue: 2,0:00:01.30,0:00:05.00,Badge,,0,0,0,,{\pos(960,675)\fad(180,250)}CONFLICT DETECTED
Dialogue: 2,0:00:01.70,0:00:05.00,Big,,0,0,0,,{\pos(960,770)\fad(180,250)}SyncCoordinator
Dialogue: 2,0:00:02.00,0:00:05.00,Small,,0,0,0,,{\pos(960,835)\fad(180,250)}ルールに従って解決フローへ
'@
        }
        'WorkOrderFlow' {
            return @'
Dialogue: 0,0:00:00.00,0:00:04.00,Kicker,,0,0,0,,{\pos(960,175)\fad(150,200)}WORK INSTRUCTION
Dialogue: 0,0:00:00.00,0:00:04.00,Shape,,0,0,0,,{\an7\pos(140,315)\p1\c&H00EB6F1F&\bord0\fad(150,200)}m 0 0 l 500 0 l 500 330 l 0 330
Dialogue: 0,0:00:00.00,0:00:04.00,Shape,,0,0,0,,{\an7\pos(1280,315)\p1\c&H006E760F&\bord0\fad(150,200)}m 0 0 l 500 0 l 500 330 l 0 330
Dialogue: 1,0:00:00.00,0:00:04.00,Big,,0,0,0,,{\pos(390,440)\fad(150,200)}SERVICE CRM
Dialogue: 1,0:00:00.00,0:00:04.00,Small,,0,0,0,,{\pos(390,520)\fad(150,200)}作業指示を登録
Dialogue: 1,0:00:00.00,0:00:04.00,Big,,0,0,0,,{\pos(1530,440)\fad(150,200)}FIELD SERVICE
Dialogue: 1,0:00:00.00,0:00:04.00,Small,,0,0,0,,{\pos(1530,520)\fad(150,200)}作業指示を受信
Dialogue: 1,0:00:00.00,0:00:04.00,Medium,,0,0,0,,{\move(700,480,1220,480,450,3150)\fad(150,200)}7月21日 10:00–12:00  →
'@
        }
        'ConflictCompare' {
            return @'
Dialogue: 0,0:00:00.00,0:00:07.00,DarkSmall,,0,0,0,,{\pos(960,115)\fad(180,250)}前回同期時の件名
Dialogue: 0,0:00:00.00,0:00:07.00,Shape,,0,0,0,,{\an7\pos(460,155)\p1\c&H00D8D4CC&\bord0\fad(180,250)}m 0 0 l 1000 0 l 1000 170 l 0 170
Dialogue: 1,0:00:00.00,0:00:07.00,Dark,,0,0,0,,{\pos(960,240)\fad(180,250)}冷風が出ない
Dialogue: 0,0:00:00.70,0:00:07.00,Shape,,0,0,0,,{\an7\move(-800,430,100,430,0,900)\p1\c&H00215CD9&\bord0}m 0 0 l 800 0 l 800 330 l 0 330
Dialogue: 0,0:00:00.70,0:00:07.00,Shape,,0,0,0,,{\an7\move(1920,430,1020,430,0,900)\p1\c&H00EB6F1F&\bord0}m 0 0 l 800 0 l 800 330 l 0 330
Dialogue: 1,0:00:00.70,0:00:07.00,DarkSmall,,0,0,0,,{\move(-400,505,500,505,0,900)}PORTALから届いた値
Dialogue: 1,0:00:00.70,0:00:07.00,Dark,,0,0,0,,{\move(-400,625,500,625,0,900)}冷風が出ない\N（お客様から再連絡）
Dialogue: 1,0:00:00.70,0:00:07.00,Small,,0,0,0,,{\move(2320,505,1420,505,0,900)}CRMの現在値
Dialogue: 1,0:00:00.70,0:00:07.00,Big,,0,0,0,,{\move(2320,625,1420,625,0,900)}冷房不良・\N訪問点検が必要
'@
        }
        'ConflictHold' {
            return @'
Dialogue: 0,0:00:00.00,0:00:06.00,Kicker,,0,0,0,,{\pos(960,170)\fad(180,250)}CONFLICT POLICY
Dialogue: 0,0:00:00.00,0:00:06.00,Shape,,0,0,0,,{\an7\pos(490,275)\p1\c&H00B7E8D9&\bord0\fad(180,250)}m 0 0 l 940 0 l 940 360 l 0 360
Dialogue: 1,0:00:00.00,0:00:06.00,Big,,0,0,0,,{\pos(960,405)\c&H0010251D&\fad(180,250)}SyncCoordinator
Dialogue: 1,0:00:00.00,0:00:06.00,DarkSmall,,0,0,0,,{\pos(960,500)\fad(180,250)}両方の値を保持
Dialogue: 2,0:00:01.00,0:00:06.00,Badge,,0,0,0,,{\pos(960,690)\fad(180,250)}HOLD & NOTIFY
'@
        }
        default {
            throw "Unsupported motion scene: $Scene"
        }
    }
}

function Render-MotionSegment {
    param(
        [pscustomobject]$Segment,
        [int]$Frames,
        [int]$Number,
        [string]$Path
    )

    $assPath = Join-Path $workRoot ('motion-{0:D2}.ass' -f $Number)
    $assText = $motionAssHeader + "`r`n" + (Get-MotionEvents -Scene $Segment.Scene) + "`r`n"
    [System.IO.File]::WriteAllText($assPath, $assText, [System.Text.UTF8Encoding]::new($false))
    $filterPath = $assPath.Replace('\', '/').Replace(':', '\:')

    Invoke-Ffmpeg @(
        '-hide_banner', '-loglevel', 'error', '-y',
        '-f', 'lavfi', '-i', "color=c=$($Segment.Background):s=${targetWidth}x${targetHeight}:r=${frameRate}:d=$($Segment.Duration)",
        '-vf', "ass='$filterPath':fontsdir='C\:/Windows/Fonts',format=yuv420p",
        '-frames:v', "$Frames", '-an',
        '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '20', '-pix_fmt', 'yuv420p', '-r', "$frameRate",
        $Path
    )
}

function Render-PersonCardSegment {
    param(
        [pscustomobject]$Segment,
        [int]$Frames,
        [int]$Number,
        [string]$Path
    )

    $imagePath = Resolve-Asset $Segment.Files[0]
    $cardX = if ($Segment.CardSide -eq 'Left') { 80 } else { 1080 }
    $textX = $cardX + 54
    $systemX = $cardX + 185
    $durationAss = '{0:D2}' -f [int]$Segment.Duration
    $assPath = Join-Path $workRoot ('person-card-{0:D2}.ass' -f $Number)
    $assText = @"
[Script Info]
ScriptType: v4.00+
PlayResX: 1920
PlayResY: 1080
WrapStyle: 2
ScaledBorderAndShadow: yes

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Shape,Meiryo,20,&H00FFFFFF,&H000000FF,&H00FFFFFF,&H00000000,0,0,0,0,100,100,0,0,1,0,0,7,0,0,0,1
Style: System,Meiryo,22,&H00FFFFFF,&H000000FF,&H0010251D,&H00000000,-1,0,0,0,100,100,2,0,1,0,0,5,20,20,20,1
Style: Title,Meiryo,27,&H00444B47,&H000000FF,&H00444B47,&H00000000,-1,0,0,0,100,100,0,0,1,0,0,7,20,20,20,1
Style: Value,Meiryo,42,&H00102720,&H000000FF,&H00102720,&H00000000,-1,0,0,0,100,100,0,0,1,0,0,7,20,20,20,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:00.00,0:00:$durationAss.00,Shape,,0,0,0,,{\an7\pos($cardX,285)\p1\c&H00FFFFFF&\bord0\fad(220,250)}m 0 0 l 760 0 l 760 420 l 0 420
Dialogue: 1,0:00:00.00,0:00:$durationAss.00,Shape,,0,0,0,,{\an7\pos($cardX,285)\p1\c&H00$($Segment.CardColor)&\bord0\fad(220,250)}m 0 0 l 18 0 l 18 420 l 0 420
Dialogue: 1,0:00:00.00,0:00:$durationAss.00,Shape,,0,0,0,,{\an7\pos($($cardX + 54),325)\p1\c&H00$($Segment.CardColor)&\bord0\fad(220,250)}m 0 0 l 262 0 l 262 52 l 0 52
Dialogue: 2,0:00:00.00,0:00:$durationAss.00,System,,0,0,0,,{\pos($systemX,351)\fad(220,250)}$($Segment.CardSystem)
Dialogue: 2,0:00:00.00,0:00:$durationAss.00,Title,,0,0,0,,{\pos($textX,425)\fad(220,250)}$($Segment.CardTitle)
Dialogue: 2,0:00:00.00,0:00:$durationAss.00,Value,,0,0,0,,{\pos($textX,495)\fad(220,250)}$($Segment.CardValue)
"@
    [System.IO.File]::WriteAllText($assPath, $assText, [System.Text.UTF8Encoding]::new($false))
    $filterPath = $assPath.Replace('\', '/').Replace(':', '\:')
    $filter = "[0:v]scale=${targetWidth}:${targetHeight}:force_original_aspect_ratio=increase:flags=lanczos,crop=${targetWidth}:${targetHeight},ass='$filterPath':fontsdir='C\:/Windows/Fonts',setsar=1,format=yuv420p[v]"

    Invoke-Ffmpeg @(
        '-hide_banner', '-loglevel', 'error', '-y',
        '-loop', '1', '-framerate', "$frameRate", '-t', "$($Segment.Duration)", '-i', $imagePath,
        '-filter_complex', $filter,
        '-map', '[v]', '-frames:v', "$Frames", '-an',
        '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '20', '-pix_fmt', 'yuv420p', '-r', "$frameRate",
        $Path
    )
}

$workerFlowSubtitlePath = Join-Path $workRoot 'worker-flow.ass'
$workerFlowSubtitleText = @'
[Script Info]
ScriptType: v4.00+
PlayResX: 1920
PlayResY: 1080
WrapStyle: 2
ScaledBorderAndShadow: yes

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Title,Meiryo,58,&H00FFFFFF,&H000000FF,&H0010251D,&H0010251D,-1,0,0,0,100,100,0,0,1,1,0,5,40,40,40,1
Style: Kicker,Meiryo,24,&H007ECFBA,&H000000FF,&H0010251D,&H0010251D,-1,0,0,0,100,100,3,0,1,0,0,5,40,40,40,1
Style: Request,Meiryo,42,&H002E302D,&H000000FF,&H002E302D,&H002E302D,-1,0,0,0,100,100,0,0,1,0,0,5,20,20,20,1
Style: Worker,Meiryo,42,&H0010251D,&H000000FF,&H0010251D,&H0010251D,-1,0,0,0,100,100,0,0,1,0,0,5,20,20,20,1
Style: Crm,Meiryo,42,&H00FFFFFF,&H000000FF,&H00FFFFFF,&H00FFFFFF,-1,0,0,0,100,100,0,0,1,0,0,5,20,20,20,1
Style: Shape,Meiryo,20,&H00FFFFFF,&H000000FF,&H00FFFFFF,&H00FFFFFF,0,0,0,0,100,100,0,0,1,0,0,7,0,0,0,1
Style: Arrow,Meiryo,62,&H008EAFA4,&H000000FF,&H0010251D,&H0010251D,-1,0,0,0,100,100,0,0,1,1,0,5,10,10,10,1
Style: Dot,Meiryo,42,&H0069E1C2,&H000000FF,&H0010251D,&H0010251D,-1,0,0,0,100,100,0,0,1,0,0,5,10,10,10,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:00.00,0:00:08.00,Kicker,,0,0,0,,{\pos(960,215)\fad(250,250)}RESOLUTION FLOW
Dialogue: 0,0:00:00.00,0:00:08.00,Title,,0,0,0,,{\pos(960,285)\fad(250,250)}解決内容を安全に反映
Dialogue: 0,0:00:00.00,0:00:08.00,Shape,,0,0,0,,{\an7\pos(0,0)\p1\c&H00F4EFE5&\bord0\shad0\fad(250,250)}m 220 465 l 560 465 l 560 655 l 220 655
Dialogue: 0,0:00:00.00,0:00:08.00,Shape,,0,0,0,,{\an7\pos(0,0)\p1\c&H00B7E8D9&\bord0\shad0\fad(250,250)}m 790 465 l 1130 465 l 1130 655 l 790 655
Dialogue: 0,0:00:00.00,0:00:08.00,Shape,,0,0,0,,{\an7\pos(0,0)\p1\c&H00A84B12&\bord0\shad0\fad(250,250)}m 1360 465 l 1700 465 l 1700 655 l 1360 655
Dialogue: 0,0:00:00.00,0:00:08.00,Request,,0,0,0,,{\pos(390,560)\fad(250,250)}解決要求\N{\fs25}採用値＋解決メモ
Dialogue: 0,0:00:00.00,0:00:08.00,Worker,,0,0,0,,{\pos(960,560)\fad(250,250)}Worker\N{\fs25}現在値を再確認
Dialogue: 0,0:00:00.00,0:00:08.00,Crm,,0,0,0,,{\pos(1530,560)\fad(250,250)}CRM\N{\fs25}採用値を適用
Dialogue: 0,0:00:00.00,0:00:08.00,Arrow,,0,0,0,,{\pos(675,560)\fad(250,250)}→
Dialogue: 0,0:00:00.00,0:00:08.00,Arrow,,0,0,0,,{\pos(1245,560)\fad(250,250)}→
Dialogue: 1,0:00:00.00,0:00:04.00,Dot,,0,0,0,,{\move(610,515,745,515,600,3400)\fad(200,200)}●
Dialogue: 1,0:00:04.00,0:00:08.00,Dot,,0,0,0,,{\move(1180,515,1315,515,600,3400)\fad(200,200)}●
'@
[System.IO.File]::WriteAllText($workerFlowSubtitlePath, $workerFlowSubtitleText, [System.Text.UTF8Encoding]::new($false))
$workerFlowFilterPath = $workerFlowSubtitlePath.Replace('\', '/').Replace(':', '\:')

function Render-WorkerFlowSegment {
    param(
        [int]$Duration,
        [int]$Frames,
        [string]$Path
    )

    Invoke-Ffmpeg @(
        '-hide_banner', '-loglevel', 'error', '-y',
        '-f', 'lavfi', '-i', "color=c=0x10251d:s=${targetWidth}x${targetHeight}:r=${frameRate}:d=$Duration",
        '-vf', "ass='$workerFlowFilterPath':fontsdir='C\:/Windows/Fonts',format=yuv420p",
        '-frames:v', "$Frames", '-an',
        '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '20', '-pix_fmt', 'yuv420p', '-r', "$frameRate",
        $Path
    )
}

if ($WorkerFlowPreviewOnly) {
    $previewDuration = 8
    $previewFrames = $previewDuration * $frameRate
    $previewOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
    Render-WorkerFlowSegment -Duration $previewDuration -Frames $previewFrames -Path $previewOutputPath
    Write-Host "Worker flow preview created: $previewOutputPath"
    return
}

$segmentPaths = [System.Collections.Generic.List[string]]::new()

for ($index = 0; $index -lt $segments.Count; $index++) {
    $segment = $segments[$index]
    $segmentNumber = $index + 1
    $frames = [int]($segment.Duration * $frameRate)
    $segmentPath = Join-Path $workRoot ('segment-{0:D2}.mp4' -f $segmentNumber)
    $segmentPaths.Add($segmentPath)

    Write-Host ('Rendering segment {0:D2}/{1:D2}: {2} ({3}s)' -f $segmentNumber, $segments.Count, $segment.Kind, $segment.Duration)

    switch ($segment.Kind) {
        'Motion' {
            Render-MotionSegment -Segment $segment -Frames $frames -Number $segmentNumber -Path $segmentPath
        }
        'PersonCard' {
            Render-PersonCardSegment -Segment $segment -Frames $frames -Number $segmentNumber -Path $segmentPath
        }
        'Still' {
            $imagePath = Resolve-Asset $segment.Files[0]
            $filter = "[0:v]scale=${targetWidth}:${targetHeight}:force_original_aspect_ratio=decrease:flags=lanczos,pad=${targetWidth}:${targetHeight}:(ow-iw)/2:(oh-ih)/2:color=0xf2efe8,setsar=1,format=yuv420p[v]"
            Invoke-Ffmpeg @(
                '-hide_banner', '-loglevel', 'error', '-y',
                '-loop', '1', '-framerate', "$frameRate", '-t', "$($segment.Duration)", '-i', $imagePath,
                '-filter_complex', $filter,
                '-map', '[v]', '-frames:v', "$frames", '-an',
                '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '20', '-pix_fmt', 'yuv420p', '-r', "$frameRate",
                $segmentPath
            )
        }
        'Split' {
            $leftPath = Resolve-Asset $segment.Files[0]
            $rightPath = Resolve-Asset $segment.Files[1]
            $filter = "[0:v]scale=920:1030:force_original_aspect_ratio=decrease:flags=lanczos,pad=960:1080:(ow-iw)/2:(oh-ih)/2:color=0xf8efe1[left];[1:v]scale=920:1030:force_original_aspect_ratio=decrease:flags=lanczos,pad=960:1080:(ow-iw)/2:(oh-ih)/2:color=0xe7eff8[right];[left][right]hstack=inputs=2,setsar=1,format=yuv420p[v]"
            Invoke-Ffmpeg @(
                '-hide_banner', '-loglevel', 'error', '-y',
                '-loop', '1', '-framerate', "$frameRate", '-t', "$($segment.Duration)", '-i', $leftPath,
                '-loop', '1', '-framerate', "$frameRate", '-t', "$($segment.Duration)", '-i', $rightPath,
                '-filter_complex', $filter,
                '-map', '[v]', '-frames:v', "$frames", '-an',
                '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '20', '-pix_fmt', 'yuv420p', '-r', "$frameRate",
                $segmentPath
            )
        }
        'Triple' {
            $firstPath = Resolve-Asset $segment.Files[0]
            $secondPath = Resolve-Asset $segment.Files[1]
            $thirdPath = Resolve-Asset $segment.Files[2]
            $filter = "[0:v]scale=600:900:force_original_aspect_ratio=decrease:flags=lanczos,pad=640:1080:(ow-iw)/2:(oh-ih)/2:color=0xf8efe1[first];[1:v]scale=600:900:force_original_aspect_ratio=decrease:flags=lanczos,pad=640:1080:(ow-iw)/2:(oh-ih)/2:color=0xe7eff8[second];[2:v]scale=600:900:force_original_aspect_ratio=decrease:flags=lanczos,pad=640:1080:(ow-iw)/2:(oh-ih)/2:color=0x14383a[third];[first][second][third]hstack=inputs=3,setsar=1,format=yuv420p[v]"
            Invoke-Ffmpeg @(
                '-hide_banner', '-loglevel', 'error', '-y',
                '-loop', '1', '-framerate', "$frameRate", '-t', "$($segment.Duration)", '-i', $firstPath,
                '-loop', '1', '-framerate', "$frameRate", '-t', "$($segment.Duration)", '-i', $secondPath,
                '-loop', '1', '-framerate', "$frameRate", '-t', "$($segment.Duration)", '-i', $thirdPath,
                '-filter_complex', $filter,
                '-map', '[v]', '-frames:v', "$frames", '-an',
                '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '20', '-pix_fmt', 'yuv420p', '-r', "$frameRate",
                $segmentPath
            )
        }
        'Focus' {
            $imagePath = Resolve-Asset $segment.Files[0]
            $filter = "[0:v]scale=${targetWidth}:${targetHeight}:force_original_aspect_ratio=increase:flags=lanczos,crop=${targetWidth}:${targetHeight},eq=brightness=-0.28:saturation=0.35[background];[0:v]crop=$($segment.Crop),scale=1760:-2:flags=lanczos,pad=1784:ih+24:12:12:color=0xffffff[focus];[background][focus]overlay=(W-w)/2:(H-h)/2,setsar=1,format=yuv420p[v]"
            Invoke-Ffmpeg @(
                '-hide_banner', '-loglevel', 'error', '-y',
                '-loop', '1', '-framerate', "$frameRate", '-t', "$($segment.Duration)", '-i', $imagePath,
                '-filter_complex', $filter,
                '-map', '[v]', '-frames:v', "$frames", '-an',
                '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '20', '-pix_fmt', 'yuv420p', '-r', "$frameRate",
                $segmentPath
            )
        }
        'HumanPip' {
            $humanPath = Resolve-Asset $segment.Files[0]
            $uiPath = Resolve-Asset $segment.Files[1]
            $pipX = if ($segment.PipSide -eq 'Left') { 70 } else { 1110 }
            $filter = "[0:v]scale=${targetWidth}:${targetHeight}:force_original_aspect_ratio=increase:flags=lanczos,crop=${targetWidth}:${targetHeight}[background];[1:v]scale=720:405:force_original_aspect_ratio=decrease:flags=lanczos,pad=744:429:12:12:color=0xffffff[ui];[background][ui]overlay=x=${pipX}:y=345,setsar=1,format=yuv420p[v]"
            Invoke-Ffmpeg @(
                '-hide_banner', '-loglevel', 'error', '-y',
                '-loop', '1', '-framerate', "$frameRate", '-t', "$($segment.Duration)", '-i', $humanPath,
                '-loop', '1', '-framerate', "$frameRate", '-t', "$($segment.Duration)", '-i', $uiPath,
                '-filter_complex', $filter,
                '-map', '[v]', '-frames:v', "$frames", '-an',
                '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '20', '-pix_fmt', 'yuv420p', '-r', "$frameRate",
                $segmentPath
            )
        }
        'WorkerFlow' {
            Render-WorkerFlowSegment -Duration $segment.Duration -Frames $frames -Path $segmentPath
        }
        'Solid' {
            Invoke-Ffmpeg @(
                '-hide_banner', '-loglevel', 'error', '-y',
                '-f', 'lavfi', '-i', "color=c=0x10251d:s=${targetWidth}x${targetHeight}:r=${frameRate}:d=$($segment.Duration)",
                '-frames:v', "$frames", '-an',
                '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '20', '-pix_fmt', 'yuv420p', '-r', "$frameRate",
                $segmentPath
            )
        }
        default {
            throw "Unsupported segment kind: $($segment.Kind)"
        }
    }
}

$concatPath = Join-Path $workRoot 'segments.txt'
$concatLines = $segmentPaths | ForEach-Object {
    $unixPath = $_.Replace('\', '/').Replace("'", "'\''")
    "file '$unixPath'"
}
[System.IO.File]::WriteAllLines($concatPath, $concatLines, [System.Text.UTF8Encoding]::new($false))

$stitchedPath = Join-Path $workRoot 'stitched.mp4'
Invoke-Ffmpeg @(
    '-hide_banner', '-loglevel', 'error', '-y',
    '-f', 'concat', '-safe', '0', '-i', $concatPath,
    '-c', 'copy',
    $stitchedPath
)

$subtitlePath = Join-Path $workRoot 'roughcut.ass'
$subtitleText = @'
[Script Info]
ScriptType: v4.00+
PlayResX: 1920
PlayResY: 1080
WrapStyle: 2
ScaledBorderAndShadow: yes

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Caption,Meiryo,42,&H00FFFFFF,&H000000FF,&H00102720,&H9A102720,-1,0,0,0,100,100,0,0,3,1,0,2,90,90,48,1
Style: Center,Meiryo,76,&H00FFFFFF,&H000000FF,&H00102720,&H00102720,-1,0,0,0,100,100,0,0,1,2,0,5,90,90,90,1
Style: CenterSmall,Meiryo,42,&H00DDECE5,&H000000FF,&H00102720,&H00102720,0,0,0,0,100,100,0,0,1,1,0,5,90,90,90,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:00.00,0:00:04.00,Caption,,0,0,0,,{\fad(180,180)}同じ顧客データを、2つの業務システムが更新
Dialogue: 0,0:00:04.00,0:00:09.00,Caption,,0,0,0,,{\fad(180,180)}単純な最終更新優先では、どちらかの変更が消える
Dialogue: 0,0:00:09.00,0:00:15.00,Caption,,0,0,0,,{\fad(180,180)}SyncCoordinatorはルールに従って、解決フローへ
Dialogue: 0,0:00:15.00,0:00:28.00,Caption,,0,0,0,,{\fad(180,180)}MySQL、SQL Server、PostgreSQLに対応
Dialogue: 0,0:00:28.00,0:00:35.00,Caption,,0,0,0,,{\fad(180,180)}ポータルで受け付けた問い合わせを、管理システムへ
Dialogue: 0,0:00:35.00,0:00:42.00,Caption,,0,0,0,,{\fad(180,180)}管理システムで作業指示を発行し、現場の作業システムへ
Dialogue: 0,0:00:42.00,0:00:50.00,Caption,,0,0,0,,{\fad(180,180)}現場の作業結果を管理システムへ戻し、お客様への回答をポータルへ
Dialogue: 0,0:00:50.00,0:01:05.00,Caption,,0,0,0,,{\fad(180,180)}同じ項目が両側で変わると
Dialogue: 0,0:01:05.00,0:01:15.00,Caption,,0,0,0,,{\fad(180,180)}競合（コンフリクト）を検知
Dialogue: 0,0:01:15.00,0:01:45.00,Caption,,0,0,0,,{\fad(180,180)}競合しない項目は自動でマージ
Dialogue: 0,0:01:45.00,0:02:02.00,Caption,,0,0,0,,{\fad(180,180)}競合した項目は採用する値を選択
Dialogue: 0,0:02:02.00,0:02:10.00,Caption,,0,0,0,,{\fad(180,180)}誰が、何を採用したか。解決メモとともに記録
Dialogue: 0,0:02:10.00,0:02:30.00,Caption,,0,0,0,,{\fad(180,180)}Workerがバックグラウンドで解決内容を反映
Dialogue: 0,0:02:30.00,0:02:45.00,Caption,,0,0,0,,{\fad(180,180)}競合の発生から解決まで追跡できる
Dialogue: 0,0:02:45.00,0:02:52.00,Caption,,0,0,0,,{\fad(180,180)}変更を止めずに、データを正しくつなぐ。
Dialogue: 0,0:02:52.00,0:03:00.00,Center,,0,0,0,,{\pos(960,470)\fad(500,700)}SyncCoordinator
'@

function Format-AssTime {
    param([int]$Seconds)

    $hours = [int][math]::Floor($Seconds / 3600)
    $minutes = [int][math]::Floor(($Seconds % 3600) / 60)
    $remainingSeconds = [int]($Seconds % 60)
    return ('{0}:{1:D2}:{2:D2}.00' -f $hours, $minutes, $remainingSeconds)
}

$firstDialogueIndex = $subtitleText.IndexOf('Dialogue:', [StringComparison]::Ordinal)
if ($firstDialogueIndex -lt 0) {
    throw 'ASS subtitle template does not contain a dialogue marker.'
}

$subtitleHeader = $subtitleText.Substring(0, $firstDialogueIndex)
$subtitleEvents = [System.Collections.Generic.List[string]]::new()
$captionStart = 0
foreach ($segment in $segments) {
    $captionEnd = $captionStart + $segment.Duration
    if (-not [string]::IsNullOrWhiteSpace($segment.Caption)) {
        $captionText = $segment.Caption.Replace("`r`n", '\N').Replace("`n", '\N')
        $subtitleEvents.Add(('Dialogue: 0,{0},{1},Caption,,0,0,0,,{{\fad(120,120)}}{2}' -f (Format-AssTime $captionStart), (Format-AssTime $captionEnd), $captionText))
    }
    $captionStart = $captionEnd
}

$subtitleEvents.Add('Dialogue: 0,0:02:52.00,0:03:00.00,Center,,0,0,0,,{\pos(960,470)\fad(500,700)}SyncCoordinator')
$dynamicSubtitleText = $subtitleHeader + ($subtitleEvents -join "`r`n") + "`r`n"
[System.IO.File]::WriteAllText($subtitlePath, $dynamicSubtitleText, [System.Text.UTF8Encoding]::new($false))

$outputFullPath = [System.IO.Path]::GetFullPath($OutputPath)
$originalLocation = Get-Location
try {
    Set-Location $workRoot
    Invoke-Ffmpeg @(
        '-hide_banner', '-loglevel', 'error', '-y',
        '-i', $stitchedPath,
        '-f', 'lavfi', '-t', '180', '-i', 'anullsrc=channel_layout=stereo:sample_rate=48000',
        '-vf', "ass=roughcut.ass:fontsdir='C\:/Windows/Fonts'",
        '-map', '0:v:0', '-map', '1:a:0',
        '-c:v', 'libx264', '-preset', 'medium', '-crf', '18', '-pix_fmt', 'yuv420p',
        '-c:a', 'aac', '-b:a', '160k',
        '-t', '180', '-movflags', '+faststart',
        $outputFullPath
    )
}
finally {
    Set-Location $originalLocation
}

Write-Host "Rough cut created: $outputFullPath"
Write-Host "Working files retained at: $workRoot"
