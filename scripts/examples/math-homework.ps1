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

Write-Section -Text "小学数学课后作业"

Write-Host "主题：1+1 和 1+2 的基础训练" -ForegroundColor Yellow
Write-Host "要求：先自己完成，再核对答案。" -ForegroundColor Yellow

Write-Host ""
Write-Host "一、填空题" -ForegroundColor White
Write-Host "1. 1 + 1 = ____" -ForegroundColor White
Write-Host "2. 1 + 2 = ____" -ForegroundColor White
Write-Host "3. 1 + 1 = ____" -ForegroundColor White
Write-Host "4. 1 + 2 = ____" -ForegroundColor White

Write-Host ""
Write-Host "二、看图想一想（老师/家长可配合实物）" -ForegroundColor White
Write-Host "1. 一个苹果，再拿来一个苹果，一共有几个苹果？" -ForegroundColor White
Write-Host "2. 一个小球，再拿来两个小球，一共有几个小球？" -ForegroundColor White

Write-Host ""
Write-Host "三、口算复习" -ForegroundColor White
for ($i = 1; $i -le 3; $i++) {
    Write-Host "第 $i 轮：1 + 1 = 2" -ForegroundColor DarkCyan
    Write-Host "第 $i 轮：1 + 2 = 3" -ForegroundColor DarkCyan
}

Write-Host ""
Write-Host "参考答案" -ForegroundColor Green
Write-Host "1. 1 + 1 = 2" -ForegroundColor Green
Write-Host "2. 1 + 2 = 3" -ForegroundColor Green
Write-Host "3. 1 + 1 = 2" -ForegroundColor Green
Write-Host "4. 1 + 2 = 3" -ForegroundColor Green
Write-Host "苹果题答案：2 个苹果" -ForegroundColor Green
Write-Host "小球题答案：3 个小球" -ForegroundColor Green

Write-Host ""
Write-Host "今天的作业完成啦，记得表扬小朋友的认真表现！" -ForegroundColor Green
