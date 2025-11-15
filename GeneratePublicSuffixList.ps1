$url = "https://www.publicsuffix.org/list/public_suffix_list.dat"
$csFilePath = Join-Path $PSScriptRoot "PublicSuffixData.cs"
if (Test-Path $csFilePath) { return }
$objPath = Join-Path $PSScriptRoot "obj"
$listPath = Join-Path $objPath "public_suffix_list.dat"

# Download if not exists
if (-not (Test-Path $listPath)) {
    Invoke-WebRequest -Uri $url -OutFile $listPath
}

$exact = @()
$wildcards = @()
$exceptions = @()

Get-Content $listPath | ForEach-Object {
    $line = $_.Trim()
    if ($line -eq '' -or $line.StartsWith('//')) { return }
    if ($line.StartsWith('!')) {
        $exceptions += $line.Substring(1)
    } elseif ($line.StartsWith('*.')) {
        $wildcards += $line.Substring(2)
    } else {
        $exact += $line
    }
}

$exactStr = ($exact | ForEach-Object { "        `"$_`"" }) -join ",`n"
$wildcardsStr = ($wildcards | ForEach-Object { "        `"$_`"" }) -join ",`n"
$exceptionsStr = ($exceptions | ForEach-Object { "        `"$_`"" }) -join ",`n"

$csContent = @"
using System.Collections.Generic;

public static class PublicSuffixData
{
    public static readonly HashSet<string> Exact = new HashSet<string>
    {
$exactStr
    };

    public static readonly HashSet<string> Wildcards = new HashSet<string>
    {
$wildcardsStr
    };

    public static readonly HashSet<string> Exceptions = new HashSet<string>
    {
$exceptionsStr
    };
}
"@

$csContent | Out-File $csFilePath -Encoding UTF8