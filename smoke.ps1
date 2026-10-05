param(
    [string]$Configuration = "Release",
    [string]$ExePath = ""
)

$ErrorActionPreference = 'Stop'
# 仓库根目录 = 本脚本所在目录（不写死任何本机绝对路径）
$repoRoot = $PSScriptRoot
$bin = Join-Path $repoRoot "Zhuabao\bin\$Configuration\net8.0-windows"
$exe = if ($ExePath) { $ExePath } else { Join-Path $bin "ZhuabaoCreater.exe" }
if (-not (Test-Path $exe)) { throw "未找到 $exe，请先 dotnet build / publish" }
$bin = Split-Path $exe -Parent
$captureDir = Join-Path $bin "Capture"
if (Test-Path $captureDir) { Remove-Item $captureDir -Recurse -Force }

# 0. 清理残留实例，避免端口 8899 冲突
Get-Process ZhuabaoCreater -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

# 1. 启动 GUI (--autostart 让代理自动启动，等价于点击“启动(F5)”)
$gui = Start-Process -FilePath $exe -ArgumentList "--autostart" -PassThru
Start-Sleep -Seconds 5
if ($gui.HasExited) { throw "GUI 已退出，退出码 $($gui.ExitCode)" }
Write-Output "GUI-PID: $($gui.Id)"

# 2. 打开一个本地 HTTP 服务模拟目标网站 (job 进程内自建 HttpListener)
Start-Job -ScriptBlock {
    $l = [System.Net.HttpListener]::new()
    $l.Prefixes.Add("http://127.0.0.1:18080/")
    $l.Start()
    while ($l.IsListening) {
        try {
            $ctx = $l.GetContext()
            $bytes = [System.Text.Encoding]::UTF8.GetBytes('{"ok":true,"path":"' + $ctx.Request.Url.PathAndQuery + '"}')
            $ctx.Response.ContentType = "application/json"
            $ctx.Response.ContentLength64 = $bytes.Length
            $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
            $ctx.Response.Close()
        } catch { break }
    }
} | Out-Null
Start-Sleep -Milliseconds 800
Write-Output "LISTENER: http://127.0.0.1:18080/"

# 3. 用 curl 走代理访问 (HTTP)，并打印响应与退出码
$proxyArg = "-x"
$resp = & curl.exe -x http://127.0.0.1:8899 -s -m 10 -o - -w "HTTPCODE=%{http_code}" "http://127.0.0.1:18080/hello?from=zhuabao" 2>&1
Write-Output "CURL-RESP: $resp"
Write-Output "CURL-EXIT: $LASTEXITCODE"

Start-Sleep -Seconds 2

# 4. 检查捕获目录
$files = Get-ChildItem $captureDir -Filter *.zb -ErrorAction SilentlyContinue
Write-Output ("CAPTURED: " + ($files | Measure-Object).Count)
if ($files) {
    $raw = Get-Content $files[0].FullName -Raw
    Write-Output ("FIRST-SESSION-HEAD: " + $raw.Substring(0, [Math]::Min(220, $raw.Length)))
}

try { Get-Job | Stop-Job } catch {}
Stop-Process -Id $gui.Id -Force -ErrorAction SilentlyContinue
Write-Output "DONE"
