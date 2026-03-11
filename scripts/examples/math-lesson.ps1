[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [int]$Left,

    [Parameter(Mandatory = $true)]
    [int]$Right,

    [string]$Title = "基础加法讲解",

    [ValidateSet("lesson", "practice")]
    [string]$Mode = "lesson"
)

$sum = $Left + $Right

function Write-Section {
    param(
        [string]$Text,
        [string]$Color = "Cyan"
    )

    Write-Host ""
    Write-Host "========================================" -ForegroundColor $Color
    Write-Host " $Text" -ForegroundColor $Color
    Write-Host "========================================" -ForegroundColor $Color
    Write-Host ""
}

function Show-Dots {
    param(
        [int]$Count,
        [string]$Label,
        [ConsoleColor]$Color
    )

    $dots = @()
    for ($i = 0; $i -lt $Count; $i++) {
        $dots += "●"
    }

    Write-Host "$Label：$($dots -join ' ')" -ForegroundColor $Color
}

Write-Section -Text $Title

Write-Host "今天学习的算式：$Left + $Right" -ForegroundColor Yellow
Write-Host ""

if ($Mode -eq "lesson") {
    Write-Host "第一步：先看左边的数量。" -ForegroundColor White
    Show-Dots -Count $Left -Label "左边有 $Left 个小圆点" -Color Green

    Write-Host ""
    Write-Host "第二步：再看右边的数量。" -ForegroundColor White
    Show-Dots -Count $Right -Label "右边有 $Right 个小圆点" -Color Magenta

    Write-Host ""
    Write-Host "第三步：把它们放在一起数一数。" -ForegroundColor White
    Show-Dots -Count $sum -Label "合起来一共有 $sum 个小圆点" -Color Cyan

    Write-Host ""
    Write-Host "所以答案是：$Left + $Right = $sum" -ForegroundColor Green
    Write-Host ""
    Write-Host "记忆小口诀：" -ForegroundColor Yellow
    Write-Host "  $Left 加 $Right，合起来是 $sum。" -ForegroundColor White
    Write-Host "  大声读三遍，写作业更轻松。" -ForegroundColor White

    Write-Host ""
    Write-Host "跟读练习：" -ForegroundColor Yellow
    for ($i = 1; $i -le 3; $i++) {
        Write-Host "  第 $i 遍：$Left + $Right = $sum" -ForegroundColor White
    }
}
else {
    Write-Host "请先自己想一想，再看下面的参考结果。" -ForegroundColor Yellow
    Write-Host ""

    for ($i = 1; $i -le 5; $i++) {
        Write-Host "第 $i 题：$Left + $Right = ____" -ForegroundColor White
    }

    Write-Host ""
    Write-Host "参考答案：" -ForegroundColor Green
    for ($i = 1; $i -le 5; $i++) {
        Write-Host "第 $i 题：$Left + $Right = $sum" -ForegroundColor Green
    }

    Write-Host ""
    Write-Host "完成后检查：" -ForegroundColor Yellow
    Write-Host "  看到 $Left 个，再看到 $Right 个，合起来就是 $sum 个。" -ForegroundColor White
}

Write-Host ""
Write-Host "本次训练完成，继续加油！" -ForegroundColor Green
