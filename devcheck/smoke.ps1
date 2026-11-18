# UI 煙霧測試（不用人按）：開主頁 + 一個帳戶分頁，再關掉，把結果印出來。
# 驗的是「開得起來、控件接得上、帳本真的載入到列」；資料夾只讀不寫（啟動會多一份備份）。
# 用法：powershell -ExecutionPolicy Bypass -File devcheck/smoke.ps1 [-Configuration Debug]
param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$exe = "D:\dennys\dFinance\dFinance\bin\$Configuration\net8.0-windows\dFinance.exe"
$out = "$PSScriptRoot\smoke_out.txt"
if (-not (Test-Path $exe)) { throw "找不到 $exe（先 dotnet build dFinance.sln -c $Configuration）" }

Remove-Item $out -ErrorAction SilentlyContinue
$p = Start-Process -FilePath $exe -ArgumentList @('--uismoke', $out) -PassThru -Wait
if (-not (Test-Path $out)) { throw "沒写出結果檔，程式可能没起來（exit $($p.ExitCode)）" }
Get-Content $out
exit $p.ExitCode
