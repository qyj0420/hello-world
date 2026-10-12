param([Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $OutputPath){throw 'Diagnostic output must be new.'}
$socket=New-Object System.Net.Sockets.Socket([System.Net.Sockets.AddressFamily]::InterNetwork,[System.Net.Sockets.SocketType]::Dgram,[System.Net.Sockets.ProtocolType]::Udp)
$packets=0;$bytes=0;$senders=@{};$headers=@{};$started=[DateTime]::UtcNow
try{
 $socket.ExclusiveAddressUse=$true
 $socket.Bind((New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any,8088)))
 $timer=[Diagnostics.Stopwatch]::StartNew()
 $buffer=New-Object byte[] 8192
 while($timer.Elapsed.TotalSeconds -lt 10){
  if(-not $socket.Poll(100000,[System.Net.Sockets.SelectMode]::SelectRead)){continue}
  [System.Net.EndPoint]$source=New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any,0)
  $length=$socket.ReceiveFrom($buffer,0,$buffer.Length,[System.Net.Sockets.SocketFlags]::None,[ref]$source)
  $packets++;$bytes+=$length;$senders[$source.ToString()]=$true
  $text=[Text.Encoding]::ASCII.GetString($buffer,0,$length)
  foreach($match in [regex]::Matches($text,'\$([A-Z]{2}[A-Z]{3}),')){$headers[$match.Groups[1].Value]=$true}
 }
 $result=[ordered]@{StartedUtc=$started.ToString('O');CompletedUtc=[DateTime]::UtcNow.ToString('O');LocalPort=8088;Seconds=10;PacketCount=$packets;Bytes=$bytes;Senders=@($senders.Keys);NmeaHeaders=@($headers.Keys);OutgoingPackets=0}
 $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
 $result | ConvertTo-Json -Depth 4
}finally{$socket.Dispose()}
