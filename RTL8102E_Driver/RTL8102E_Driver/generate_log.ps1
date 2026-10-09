$jsonPath = "C:\Users\admin\.gemini\antigravity\brain\da8e2a00-7079-44de-b972-ceccc8a655c7\.system_generated\logs\transcript_full.jsonl"
$outPath = "C:\Users\admin\Documents\personal projects\RTL8102E_Driver\RTL8102E_Final_Driver\Chat_Log.txt"

$lines = Get-Content -Path $jsonPath -Raw
$jsonLines = $lines -split "`n"

$outFile = [System.IO.File]::CreateText($outPath)

foreach ($line in $jsonLines) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    try {
        $obj = $line | ConvertFrom-Json
        
        $source = $obj.source
        $content = $obj.content
        
        if ($source -eq "USER_EXPLICIT") {
            $outFile.WriteLine("==================================================")
            $outFile.WriteLine("USER:")
            $outFile.WriteLine($content)
            $outFile.WriteLine("==================================================`n")
        }
        elseif ($source -eq "MODEL" -and $obj.type -eq "PLANNER_RESPONSE") {
            if (-not [string]::IsNullOrWhiteSpace($content)) {
                $outFile.WriteLine("ANTIGRAVITY:")
                $outFile.WriteLine($content)
                $outFile.WriteLine("--------------------------------------------------`n")
            }
        }
    } catch {
        # ignore parse errors
    }
}

$outFile.Close()
Write-Host "Log generated at $outPath"
