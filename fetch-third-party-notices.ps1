$ErrorActionPreference = "Stop"

$items = @(
    @{
        Name = "GTranslate"
        Version = "2.3.1"
        License = "MIT License"
        Project = "https://github.com/d4n3436/GTranslate"
        Files = @(
            @{ Label = "LICENSE"; Url = "https://raw.githubusercontent.com/d4n3436/GTranslate/master/LICENSE" }
        )
    },
    @{
        Name = "NLog"
        Version = "5.0.4"
        License = "BSD 3-Clause License"
        Project = "https://github.com/NLog/NLog"
        Files = @(
            @{ Label = "LICENSE"; Url = "https://raw.githubusercontent.com/NLog/NLog/dev/LICENSE.txt" }
        )
    },
    @{
        Name = "RapidOcrNet"
        Version = "3.0.0"
        License = "Apache License 2.0"
        Project = "https://github.com/BobLd/RapidOcrNet"
        Files = @(
            @{ Label = "LICENSE"; Url = "https://raw.githubusercontent.com/BobLd/RapidOcrNet/master/LICENSE.txt" },
            @{ Label = "NOTICE"; Url = "https://raw.githubusercontent.com/BobLd/RapidOcrNet/master/NOTICE.txt" }
        )
    },
    @{
        Name = "Velopack"
        Version = "1.2.0"
        License = "MIT License"
        Project = "https://github.com/velopack/velopack"
        Files = @(
            @{ Label = "LICENSE"; Url = "https://raw.githubusercontent.com/velopack/velopack/develop/LICENSE" }
        )
    },
    @{
        Name = "PaddleOCR Models"
        Version = "PP-OCRv5 / PP-OCRv6 models used by OverTranslate"
        License = "Apache License 2.0"
        Project = "https://github.com/PaddlePaddle/PaddleOCR"
        Files = @(
            @{ Label = "LICENSE"; Url = "https://raw.githubusercontent.com/PaddlePaddle/PaddleOCR/main/LICENSE" }
        )
    },
    @{
        Name = "ONNX Runtime (DirectML)"
        Version = "1.24.4 (native); Microsoft.ML.OnnxRuntime.Managed 1.27.0"
        License = "MIT License"
        Project = "https://github.com/microsoft/onnxruntime"
        Files = @(
            @{ Label = "LICENSE"; Url = "https://raw.githubusercontent.com/microsoft/onnxruntime/v1.24.4/LICENSE" }
        )
    },
    @{
        Name = "comic-text-and-bubble-detector (manga models, downloaded separately)"
        Version = "ogkalu/comic-text-and-bubble-detector, RT-DETR-v2 r50vd"
        License = "Apache License 2.0"
        Project = "https://huggingface.co/ogkalu/comic-text-and-bubble-detector"
        Note = "Modified by OverTranslate: the published detector.onnx converted to fp16 (TopK, GatherElements and Cast kept in fp32; duplicate Cast nodes removed). Not included in the installer; downloaded on request as detector.fp16.onnx."
        Files = @(
            @{ Label = "LICENSE"; Url = "https://www.apache.org/licenses/LICENSE-2.0.txt" }
        )
    },
    @{
        Name = "manga-ocr (manga models, downloaded separately)"
        Version = "kha-white/manga-ocr-base"
        License = "Apache License 2.0"
        Project = "https://github.com/kha-white/manga-ocr"
        Note = "Modified by OverTranslate: exported from the published PyTorch weights to ONNX in fp16, with the decoder split into a cross-attention graph and a single-step graph with a key/value cache. vocab.txt is the upstream file unchanged. Not included in the installer; downloaded on request."
        Files = @(
            @{ Label = "LICENSE"; Url = "https://raw.githubusercontent.com/kha-white/manga-ocr/master/LICENSE" }
        )
    }
)

$separator = "-" * 79
$builder = New-Object System.Text.StringBuilder

[void]$builder.AppendLine("THIRD-PARTY NOTICES")
[void]$builder.AppendLine()
[void]$builder.AppendLine("OverTranslate includes or uses the following third-party software and resources.")
[void]$builder.AppendLine("The license and notice text below is downloaded directly from the official upstream repositories.")
[void]$builder.AppendLine()

foreach ($item in $items) {
    [void]$builder.AppendLine($separator)
    [void]$builder.AppendLine($item.Name)
    [void]$builder.AppendLine($separator)
    [void]$builder.AppendLine("Version: $($item.Version)")
    [void]$builder.AppendLine("Project: $($item.Project)")
    [void]$builder.AppendLine("License: $($item.License)")
    if ($item.Note) {
        [void]$builder.AppendLine("Note: $($item.Note)")
    }
    [void]$builder.AppendLine()

    foreach ($file in $item.Files) {
        Write-Host "Downloading $($item.Name) $($file.Label)..."
        $content = (Invoke-WebRequest -Uri $file.Url -UseBasicParsing).Content
        $content = $content -replace "`r?`n", "`r`n"

        [void]$builder.AppendLine("[$($file.Label)]")
        [void]$builder.AppendLine("Source: $($file.Url)")
        [void]$builder.AppendLine()
        [void]$builder.Append($content.TrimEnd())
        [void]$builder.AppendLine()
        [void]$builder.AppendLine()
    }
}

$output = Join-Path $PSScriptRoot "THIRD-PARTY-NOTICES.txt"
$text = $builder.ToString() -replace "(?<!`r)`n", "`r`n"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($output, $text, $utf8NoBom)

Write-Host "Created: $output"
