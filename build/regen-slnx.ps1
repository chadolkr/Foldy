# Foldy.slnx를 src/ 와 tests/ 아래 모든 csproj로 다시 만든다 (병합 충돌 해결용).
$root = Split-Path $PSScriptRoot -Parent
function List($dir) {
    Get-ChildItem (Join-Path $root $dir) -Recurse -Filter *.csproj |
        Where-Object FullName -notmatch '\\(bin|obj)\\' |
        ForEach-Object { "$dir/" + $_.FullName.Substring((Join-Path $root $dir).Length + 1).Replace('\', '/') } |
        Sort-Object
}
$sb = "<Solution>`n"
foreach ($dir in 'src', 'tests') {
    $sb += "  <Folder Name=`"/$dir/`">`n"
    foreach ($p in List $dir) { $sb += "    <Project Path=`"$p`">`n      <Platform Project=`"x64`" />`n    </Project>`n" }
    $sb += "  </Folder>`n"
}
$sb += "</Solution>`n"
[IO.File]::WriteAllText((Join-Path $root 'Foldy.slnx'), $sb)
