param([string]$ExePath = "")
$ErrorActionPreference = 'Stop'
# 默认用本仓库 Release 产物（不写死本机绝对路径）
if (-not $ExePath) { $ExePath = Join-Path $PSScriptRoot "Zhuabao\bin\Release\net8.0-windows\ZhuabaoCreater.exe" }
if (-not (Test-Path $ExePath)) { throw "未找到 $ExePath，请先 dotnet build -c Release" }
$reg = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'

# 记录原始系统代理状态
$origEnable = (Get-ItemProperty $reg -Name ProxyEnable -ErrorAction SilentlyContinue).ProxyEnable
$origServer = (Get-ItemProperty $reg -Name ProxyServer -ErrorAction SilentlyContinue).ProxyServer
Write-Output "ORIGINAL: ProxyEnable=$origEnable ProxyServer=$origServer"

# 清理
Get-Process ZhuabaoCreater -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
$cap = Split-Path $ExePath -Parent
$cap = Join-Path $cap "Capture"
if (Test-Path $cap) { Remove-Item $cap -Recurse -Force }

# 1. 启动 GUI (--autostart: 自动开代理 + 自动接管系统代理)
$gui = Start-Process -FilePath $ExePath -ArgumentList "--autostart" -PassThru
Start-Sleep -Seconds 6

# 2. 校验系统代理已被接管
$now = Get-ItemProperty $reg
Write-Output ("TAKEOVER: ProxyEnable=" + $now.ProxyEnable + " ProxyServer=" + $now.ProxyServer)
if ($now.ProxyEnable -eq 1 -and $now.ProxyServer -like "*8899*") { Write-Output "TAKEOVER-OK" } else { Write-Output "TAKEOVER-FAIL" }

# 3. 本地目标站 (job 内自建)
Start-Job -ScriptBlock {
    $l = [System.Net.HttpListener]::new()
    $l.Prefixes.Add("http://127.0.0.1:18080/")
    $l.Start()
    while ($l.IsListening) {
        try {
            $ctx = $l.GetContext()
            $b = [System.Text.Encoding]::UTF8.GetBytes('{"ok":true,"via":"wininet","path":"' + $ctx.Request.Url.PathAndQuery + '"}')
            $ctx.Response.ContentType = "application/json"
            $ctx.Response.ContentLength64 = $b.Length
            $ctx.Response.OutputStream.Write($b, 0, $b.Length)
            $ctx.Response.Close()
        } catch { break }
    }
} | Out-Null
Start-Sleep -Milliseconds 800

# 4. 临时清空 ProxyOverride，让 127.* 也走代理（否则默认绕过本地）
$savedOverride = $now.ProxyOverride
Set-ItemProperty $reg -Name ProxyOverride -Value "" -Type String

# 5. 用真实 WinINET 客户端 (powershell 5.1 = .NET Framework HttpWebRequest) 访问 → 应被代理捕获
$probe = & powershell.exe -NoProfile -Command "try { (Invoke-WebRequest -Uri 'http://127.0.0.1:18080/probe-wininet' -UseBasicParsing -TimeoutSec 15).Content } catch { 'PROBE-ERR: ' + `$_.Exception.Message }" 2>&1
Write-Output "PROBE: $probe"

Start-Sleep -Seconds 2

# 6. 校验抓到会话
$files = @(Get-ChildItem $cap -Filter *.zb -ErrorAction SilentlyContinue)
Write-Output ("CAPTURED: " + $files.Count)

# 7. 关闭 GUI → 系统代理应恢复原设置
Stop-Process -Id $gui.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
$after = Get-ItemProperty $reg
# 恢复值应为接管前的原始值（而不是我们设置的 8899）
Write-Output ("RESTORED: ProxyEnable=" + $after.ProxyEnable + " ProxyServer=" + $after.ProxyServer)
if ($after.ProxyEnable -eq $origEnable -and [string]$after.ProxyServer -eq [string]$origServer) { Write-Output "RESTORE-OK" } else { Write-Output "RESTORE-FAIL (orig=$origEnable|$origServer)" }

# 清理 override 残留与 job
if ($null -ne $savedOverride) { Set-ItemProperty $reg -Name ProxyOverride -Value $savedOverride -Type String }
Get-Job | Stop-Job -ErrorAction SilentlyContinue
Get-Process ZhuabaoCreater -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Write-Output "DONE"
