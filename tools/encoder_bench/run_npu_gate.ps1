# Push candidate models to the A35 and run TFLite NNAPI with CPU disabled.
# Never trust a run that allowed CPU fallback -- compare enn-nocpu vs CPU times.

$ErrorActionPreference = "Stop"
$Adb = "C:/Program Files/Unity/Hub/Editor/6000.5.7f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"
$Models = Join-Path $PSScriptRoot "npu_models"
$Remote = "/data/local/tmp/npugate"
$Log = Join-Path $PSScriptRoot "npu_gate_raw.txt"

function Invoke-Adb {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Args)
    & $Adb @Args
}

$bench = Join-Path $Models "android_aarch64_benchmark_model"
if (-not (Test-Path $bench)) {
    throw "benchmark_model missing. Run fetch_npu_candidates.py first."
}

Invoke-Adb shell mkdir -p $Remote
Invoke-Adb push $bench "$Remote/benchmark_model"
Invoke-Adb shell chmod 755 "$Remote/benchmark_model"

Get-ChildItem $Models -Filter *.tflite | ForEach-Object {
    Write-Host "push $($_.Name)"
    Invoke-Adb push $_.FullName "$Remote/$($_.Name)" | Out-Null
}

$onnxV4 = Join-Path $PSScriptRoot "onnx_int8\mobilenetv4-s_int8.onnx"
if (Test-Path $onnxV4) {
    Invoke-Adb push $onnxV4 "$Remote/mobilenetv4-s_int8.onnx" | Out-Null
}
$onnxV2 = Join-Path $Models "mobilenet_v2_224_int8.onnx"
if (Test-Path $onnxV2) {
    Invoke-Adb push $onnxV2 "$Remote/mobilenet_v2_224_int8.onnx" | Out-Null
}

$tflites = @(
    "mobilenet_v1_1.0_224_quant.tflite",
    "mobilenet_v2_1.0_224_quant.tflite",
    "coral_mobilenet_v2_1.0_224_quant.tflite",
    "deeplabv3_mnv2_pascal_8bit.tflite",
    "coral_deeplabv3_mnv2_pascal_quant.tflite",
    "deeplabv3_257_mv_gpu.tflite",
    "mediapipe_deeplab_v3_f32.tflite",
    "mediapipe_selfie_multiclass_256.tflite",
    "mediapipe_selfie_segmenter_f16.tflite"
)

$modes = @(
    @{ name = "cpu";          extra = "--use_nnapi=false --num_threads=4" },
    @{ name = "nnapi-hybrid"; extra = "--use_nnapi=true --nnapi_accelerator_name=enn" },
    @{ name = "nnapi-nocpu";  extra = "--use_nnapi=true --nnapi_accelerator_name=enn --disable_nnapi_cpu=true" }
)

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# npu gate raw $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
$lines.Add("")

foreach ($m in $tflites) {
    $exists = Invoke-Adb shell "if [ -f $Remote/$m ]; then echo yes; else echo no; fi"
    if (($exists | Out-String).Trim() -ne "yes") {
        $lines.Add("MISSING $m")
        continue
    }
    foreach ($mode in $modes) {
        Write-Host ""
        Write-Host "===== $m  $($mode.name) ====="
        Invoke-Adb logcat -c | Out-Null
        $shell = "cd $Remote; ./benchmark_model --graph=$m $($mode.extra) --num_runs=20 --warmup_runs=8 --num_threads=4"
        $result = Invoke-Adb shell $shell 2>&1 | Out-String
        $enn = Invoke-Adb logcat -d 2>&1 | Select-String -Pattern "ENN|Operation Not Supported|NNAPI|accelerator"
        Write-Host $result
        $lines.Add("===== $m  $($mode.name) =====")
        $lines.Add($result.TrimEnd())
        $lines.Add("-- logcat ENN/NNAPI --")
        if ($enn) { $lines.Add(($enn | ForEach-Object { $_.Line } | Out-String).TrimEnd()) }
        $lines.Add("")
    }
}

$ort = Join-Path $PSScriptRoot "android\bench_ort"
if (Test-Path $ort) {
    Invoke-Adb push $ort "$Remote/bench_ort" | Out-Null
    $ortSo = Join-Path $PSScriptRoot "android\ort_aar\jni\arm64-v8a\libonnxruntime.so"
    Invoke-Adb push $ortSo "$Remote/libonnxruntime.so" | Out-Null
    Invoke-Adb shell chmod 755 "$Remote/bench_ort"
    foreach ($onnx in @("mobilenetv4-s_int8.onnx", "mobilenet_v2_224_int8.onnx")) {
        $exists = Invoke-Adb shell "if [ -f $Remote/$onnx ]; then echo yes; else echo no; fi"
        if (($exists | Out-String).Trim() -ne "yes") { continue }
        foreach ($ep in @("cpu", "nnapi-nocpu")) {
            Write-Host ""
            Write-Host "===== ORT $onnx  $ep ====="
            $result = Invoke-Adb shell "cd $Remote; LD_LIBRARY_PATH=. ./bench_ort -m $onnx -e $ep -r 20 -w 8 -t 4" 2>&1 | Out-String
            Write-Host $result
            $lines.Add("===== ORT $onnx  $ep =====")
            $lines.Add($result.TrimEnd())
            $lines.Add("")
        }
    }
}

Set-Content -Path $Log -Value $lines -Encoding UTF8
Write-Host ""
Write-Host "wrote $Log"
