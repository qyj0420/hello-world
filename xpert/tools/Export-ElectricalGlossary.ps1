$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$output=Join-Path $root 'docs/international'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$utf8=[Text.UTF8Encoding]::new($true)
$sources=[ordered]@{
    'IEC-60270'='https://webstore.iec.ch/en/publication/65087'
    'IEC-62478'='https://webstore.iec.ch/en/publication/25740'
    'IEEE-400.4'='https://standards.ieee.org/ieee/400.4/11418/'
    'IEEE-400.2'='https://standards.ieee.org/ieee/400.2/11049/'
    'IEC-62271'='https://webstore.iec.ch/en/publication/6729'
    'OMICRON-PD'='https://www.omicronenergy.com/en/partialdischargebulletin/'
    'OMICRON-HFCT'='https://www.omicronenergy.com/en/products/mct-085/'
    'Megger-DFR'='https://www.megger.com/en/products/idax-series-insulation-diagnostic-analysers'
    'Megger-CFL'='https://www.megger.com/en-ca/applications/cables/cable-fault-location'
}
$files=@(Get-ChildItem -LiteralPath (Join-Path $root 'src'),(Join-Path $root 'modules-src') -Recurse -Filter '*.cs' -File | Where-Object { $_.Name -notlike 'InternationalText*' })
$corpus=@{}
foreach($file in $files) { $corpus[$file.FullName.Substring($root.Length+1).Replace('\','/')]=[IO.File]::ReadAllText($file.FullName) }
$terms=@(foreach($line in Get-Content -LiteralPath (Join-Path $root 'assets/i18n/electrical-glossary.psv') -Encoding UTF8) {
    if(!$line -or $line.StartsWith('#')){continue}
    $parts=$line.Split('|'); if($parts.Count -ne 7){throw ('Glossary row shape: '+$line)}
    $found=[Collections.Generic.List[string]]::new(); $count=0
    if($parts[3] -ne '—') {
        $pattern='(?<![A-Za-z0-9_])'+[regex]::Escape($parts[3])+'(?![A-Za-z0-9_])'
        foreach($file in $corpus.Keys){$n=[regex]::Matches($corpus[$file],$pattern).Count;if($n){$count+=$n;$found.Add($file)}}
    }
    [ordered]@{Category=$parts[0];Chinese=$parts[1];English=$parts[2];ShortForm=$parts[3];Classification=$parts[4];Notes=$parts[5];Evidence=$parts[6];ReferenceUrl=$sources[$parts[6]];SourceOccurrenceCount=$count;SourceFiles=@($found | Sort-Object)}
})
$document=[ordered]@{Schema='XIONGXIN.ElectricalGlossary.v1';Date='2026-10-02';Scope='项目源码涉及的术语及相关设备/检测/绝缘评估扩展词汇。源码命中是文本扫描，不能证明对应功能已实现。';Standardization='区分通用缩写、领域或厂商简称、项目标识、量和单位符号。没有统一缩写的术语保留全称。';ReviewStatus='工作术语表；已查阅所列公开标准摘要及厂家技术资料，不是逐条标准全文核验或认证翻译。非英语界面译文尚未经过母语电气专业人员审校。';Sources=$sources;Terms=$terms}
[IO.File]::WriteAllText((Join-Path $output 'electrical-glossary.json'),($document|ConvertTo-Json -Depth 8),$utf8)
function EncodeHtml([string]$value){return [Net.WebUtility]::HtmlEncode($value)}
$rows=foreach($t in $terms){
    $evidence=if($t.ReferenceUrl){'<a href="'+(EncodeHtml $t.ReferenceUrl)+'">'+(EncodeHtml $t.Evidence)+'</a>'}else{EncodeHtml $t.Evidence}
    '<tr><td>'+(EncodeHtml $t.Category)+'</td><td>'+(EncodeHtml $t.Chinese)+'</td><td>'+(EncodeHtml $t.English)+'</td><td class="abbr">'+(EncodeHtml $t.ShortForm)+'</td><td>'+(EncodeHtml $t.Classification)+'</td><td>'+(EncodeHtml $t.Notes)+'</td><td>'+$evidence+'</td><td>'+$t.SourceOccurrenceCount+'</td></tr>'
}
$html=@'
<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>高压电气与电缆绝缘术语表 · XIONGXIN</title>
<style>body{font:15px/1.65 'Segoe UI','Microsoft YaHei',sans-serif;margin:28px;color:#172b42;background:#f5f7fa}h1{font-size:27px;margin-bottom:6px}p{max-width:1100px}input{width:min(700px,90%);padding:12px;border:1px solid #789;border-radius:6px;font:inherit}table{border-collapse:collapse;background:white;width:100%;margin-top:18px;table-layout:fixed}td,th{padding:10px;border:1px solid #dbe2eb;vertical-align:top;overflow-wrap:anywhere}th{background:#173858;color:white;text-align:left;position:sticky;top:0}td.abbr{font-weight:600;color:#134f76}tr:nth-child(even){background:#f0f5fa}.note{background:#fff5df;border-left:4px solid #bf8421;padding:12px 16px}.small{color:#52657b;font-size:13px}a{color:#166491}button{padding:11px;font:inherit}.wrap{overflow:auto}@media print{body{margin:0;font-size:9px}input,button{display:none}th{position:static;color:black;background:#dde6ee}td,th{padding:5px}}</style>
<h1>高压电气、局部放电与电缆绝缘术语表</h1><p>XIONGXIN · HV-DAC Xpert · 2026-10-02 · <b>__COUNT__</b> 项</p>
<p class="note">“国际常用”不等于每个术语都有全球唯一缩写。表中分别标明通用缩写、领域/行业用法、项目约定、量与单位符号；无统一缩写的项目使用英文全称。DAC、PF、IR、CT、CB、MAP 等必须结合语境解释。</p>
<p>覆盖高压设备、电压试验、局放测量、传感与采集、定位和信号处理、状态监测、绝缘诊断、电缆材料、单位及项目接口。源文件命中数为大小写敏感文本扫描，包含注释和字符串；0表示未命中该简写，不能据此断言功能存在或不存在。通用符号如q、U的命中不具有语义证明力。</p>
<p class="small">已查阅所列公开标准摘要及厂家技术资料。参考链接用于追溯术语和技术背景，不表示每一行已经过标准全文逐条核验。本表不提供试验电压、合格阈值或操作规程。对应软件各语言仍有未覆盖的详细说明和弹窗，按既有资源或英文回退；国家语言译文待母语电气专业人员审校。</p>
<input id="search" type="search" placeholder="检索中文、英文或缩写，例如 PDIV、绝缘、DAC"><button onclick="window.print()">打印 / 保存为PDF</button><span id="count"></span>
<div class="wrap"><table><colgroup><col style="width:9%"><col style="width:13%"><col style="width:18%"><col style="width:7%"><col style="width:10%"><col style="width:23%"><col style="width:14%"><col style="width:6%"></colgroup><thead><tr><th>领域</th><th>中文</th><th>英文全称</th><th>缩写/符号</th><th>类别</th><th>用法与歧义</th><th>参考或核验状态</th><th>源码命中</th></tr></thead><tbody>__ROWS__</tbody></table></div>
<script>const input=document.getElementById('search'),rows=Array.from(document.querySelectorAll('tbody tr'));input.addEventListener('input',()=>{const q=input.value.trim().toLocaleLowerCase();let n=0;for(const row of rows){row.hidden=!row.textContent.toLocaleLowerCase().includes(q);if(!row.hidden)n++;}document.getElementById('count').textContent=' '+n+' / '+rows.length;});</script></html>
'@
$html=$html.Replace('__COUNT__',[string]$terms.Count).Replace('__ROWS__',($rows -join "`n"))
[IO.File]::WriteAllText((Join-Path $output 'electrical-glossary.html'),$html,$utf8)
$lines=@('高压电气与电缆绝缘术语表','2026-10-02','共 '+$terms.Count+' 项','类别 | 中文 | 英文全称 | 缩写或符号 | 用法与歧义','')
$lines+=@($terms|ForEach-Object{$_.Category+' | '+$_.Chinese+' | '+$_.English+' | '+$_.ShortForm+' | '+$_.Notes})
[IO.File]::WriteAllLines((Join-Path $output 'electrical-glossary.txt'),$lines,$utf8)
Write-Output ('Exported '+$terms.Count+' glossary entries; scanned '+$files.Count+' source files.')
