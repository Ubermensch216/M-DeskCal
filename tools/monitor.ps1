# 안정성 측정: 실행 중인 M-DeskCal 의 메모리·핸들·스레드·CPU 를 일정 간격으로 CSV 에 기록한다.
# 베타 기준(7일 상시 실행)은 이 스크립트를 켜 두고 마지막에 요약을 보는 방식으로 확인한다.
# 사용법:
#   powershell -ExecutionPolicy Bypass -File tools\monitor.ps1 -Minutes 10080 -IntervalSeconds 300 -Out monitor.csv
#   powershell -ExecutionPolicy Bypass -File tools\monitor.ps1 -Summary monitor.csv      # 기록 요약만 보기
param(
    [int]$Minutes = 60,
    [int]$IntervalSeconds = 60,
    [string]$Out = "monitor.csv",
    [string]$ProcessName = "DeskCal.App",
    [string]$Summary = ""
)
if ($Summary) {
    $rows = Import-Csv $Summary
    if ($rows.Count -lt 2) { "기록이 너무 적습니다."; return }
    $first = $rows[0]; $last = $rows[-1]
    $hours = ([datetime]$last.time - [datetime]$first.time).TotalHours
    $ws = $rows | ForEach-Object { [double]$_.workingSetMB }
    $priv = $rows | ForEach-Object { [double]$_.privateMB }
    # 시작 직후의 들쭉날쭉함을 피하려고 처음 10% 는 건너뛴 뒤 기울기(MB/시간)를 구한다
    $skip = [int]($rows.Count * 0.1); $body = $rows | Select-Object -Skip $skip
    $n = $body.Count; $t0 = [datetime]$body[0].time
    $xs = $body | ForEach-Object { ([datetime]$_.time - $t0).TotalHours }; $ys = $body | ForEach-Object { [double]$_.privateMB }
    $mx = ($xs | Measure-Object -Average).Average; $my = ($ys | Measure-Object -Average).Average
    $num = 0; $den = 0; for ($i = 0; $i -lt $n; $i++) { $num += ($xs[$i] - $mx) * ($ys[$i] - $my); $den += [math]::Pow($xs[$i] - $mx, 2) }
    $slope = if ($den -gt 0) { $num / $den } else { 0 }
    $cpuSec = [double]$last.cpuSeconds - [double]$first.cpuSeconds
    $restarts = ($rows | Select-Object -ExpandProperty pid -Unique).Count - 1
    "기간: {0:N1} 시간, 표본 {1}개, 프로세스 재시작 {2}회" -f $hours, $rows.Count, $restarts
    "작업 집합(MB): 시작 {0:N0} → 끝 {1:N0}, 최대 {2:N0}" -f $ws[0], $ws[-1], ($ws | Measure-Object -Maximum).Maximum
    "개인 메모리(MB): 시작 {0:N0} → 끝 {1:N0}, 최대 {2:N0}, 증가 추세 {3:+0.00;-0.00} MB/시간" -f $priv[0], $priv[-1], ($priv | Measure-Object -Maximum).Maximum, $slope
    "핸들: {0} → {1}, 스레드: {2} → {3}" -f $first.handles, $last.handles, $first.threads, $last.threads
    "평균 CPU: {0:N3} %% (1 코어 기준)" -f (100 * $cpuSec / ($hours * 3600))
    return
}
if (-not (Test-Path $Out)) { "time,pid,workingSetMB,privateMB,handles,threads,cpuSeconds" | Set-Content $Out -Encoding ASCII }
$end = (Get-Date).AddMinutes($Minutes)
while ((Get-Date) -lt $end) {
    $p = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($p) {
        $line = "{0:s},{1},{2:N1},{3:N1},{4},{5},{6:N2}" -f (Get-Date), $p.Id, ($p.WorkingSet64 / 1MB), ($p.PrivateMemorySize64 / 1MB), $p.HandleCount, $p.Threads.Count, $p.TotalProcessorTime.TotalSeconds
        Add-Content $Out $line -Encoding ASCII
    }
    Start-Sleep -Seconds $IntervalSeconds
}
