$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$compiler = Join-Path $framework 'csc.exe'
$references = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','Microsoft.CSharp.dll') | ForEach-Object { '/r:' + (Join-Path $framework $_) }
$references += @('UIAutomationClient.dll','UIAutomationTypes.dll','WindowsBase.dll') | ForEach-Object { '/r:' + (Join-Path (Join-Path $framework 'WPF') $_) }
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /nowarn:4014 ('/out:' + (Join-Path $taskRoot 'CodexQuotaBar.exe')) @references (Join-Path $taskRoot 'QuotaBar.cs')
if ($LASTEXITCODE -ne 0) { throw '编译失败' }
Write-Output 'Built CodexQuotaBar.exe'
