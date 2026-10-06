$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("s4mt-package-merge-" + [guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    $payloadA = Join-Path $tempRoot 'a.bin'
    $payloadB = Join-Path $tempRoot 'b.bin'
    $payloadC = Join-Path $tempRoot 'c.bin'
    $payloadD = Join-Path $tempRoot 'd.bin'
    [System.IO.File]::WriteAllBytes($payloadA, [byte[]](1, 2, 3))
    [System.IO.File]::WriteAllBytes($payloadB, [byte[]](4, 5, 6))
    [System.IO.File]::WriteAllBytes($payloadC, [byte[]](7, 8, 9))
    [System.IO.File]::WriteAllBytes($payloadD, [byte[]](10, 11, 12))

    $manifestAPath = Join-Path $tempRoot 'a.json'
    $manifestBPath = Join-Path $tempRoot 'b.json'
    $manifestA = [ordered]@{ format = 1; resources = @(
        [ordered]@{ type = '0x220557DA'; group = '0x00000000'; instance = '0x0000000000000001'; source = 'a.bin'; encoding = 'bytes' },
        [ordered]@{ type = '0x03B33DDF'; group = '0x00000000'; instance = '0x0000000000000002'; source = 'c.bin'; encoding = 'bytes' }
    ) }
    $manifestB = [ordered]@{ format = 1; resources = @(
        [ordered]@{ type = '0x220557DA'; group = '0x00000000'; instance = '0x0000000000000001'; source = 'b.bin'; encoding = 'bytes' },
        [ordered]@{ type = '0x220557DA'; group = '0x00000000'; instance = '0x0000000000000003'; source = 'd.bin'; encoding = 'bytes' }
    ) }
    [System.IO.File]::WriteAllText($manifestAPath, ($manifestA | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText($manifestBPath, ($manifestB | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))
    $packageA = Join-Path $tempRoot 'a.package'
    $packageB = Join-Path $tempRoot 'b.package'
    $builtA = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- build --manifest $manifestAPath --output $packageA | ConvertFrom-Json
    $builtB = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- build --manifest $manifestBPath --output $packageB | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $builtA.verified -or -not $builtB.verified) {
        throw 'Could not create package merge input fixtures.'
    }

    $mergeManifestPath = Join-Path $tempRoot 'merge.json'
    $mergeManifest = [ordered]@{ format = 1; packages = @('a.package', 'b.package') }
    [System.IO.File]::WriteAllText($mergeManifestPath, ($mergeManifest | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))
    $mergedPackage = Join-Path $tempRoot 'merged.package'
    Copy-Item -LiteralPath $packageA -Destination $mergedPackage
    $priorOutputHash = (Get-FileHash -LiteralPath $mergedPackage -Algorithm SHA256).Hash
    $collisionOutput = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- package merge --manifest $mergeManifestPath --output $mergedPackage 2>&1
    $collisionExit = $LASTEXITCODE
    $afterCollisionHash = (Get-FileHash -LiteralPath $mergedPackage -Algorithm SHA256).Hash
    if ($collisionExit -ne 2 -or $afterCollisionHash -ne $priorOutputHash) {
        throw "Default duplicate rejection changed the existing output: exit=$collisionExit output=$collisionOutput"
    }

    $mergeManifest.onConflict = 'last'
    [System.IO.File]::WriteAllText($mergeManifestPath, ($mergeManifest | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))
    $lastPackage = Join-Path $tempRoot 'last.package'
    $lastMerge = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- package merge --manifest $mergeManifestPath --output $lastPackage | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $lastMerge.verified -or $lastMerge.resourceCount -ne 3 -or $lastMerge.conflicts.Count -ne 1 -or $lastMerge.conflicts[0].keptPackage -ne $packageB) {
        throw "Last-input merge report was incorrect: $($lastMerge | ConvertTo-Json -Compress -Depth 6)"
    }

    $expectedLastPath = Join-Path $tempRoot 'expected-last.json'
    $expectedLast = [ordered]@{ format = 1; resources = @(
        [ordered]@{ type = '0x220557DA'; group = '0x00000000'; instance = '0x0000000000000001'; source = 'b.bin'; encoding = 'bytes' },
        [ordered]@{ type = '0x03B33DDF'; group = '0x00000000'; instance = '0x0000000000000002'; source = 'c.bin'; encoding = 'bytes' },
        [ordered]@{ type = '0x220557DA'; group = '0x00000000'; instance = '0x0000000000000003'; source = 'd.bin'; encoding = 'bytes' }
    ) }
    [System.IO.File]::WriteAllText($expectedLastPath, ($expectedLast | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))
    $verifyLast = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- verify --manifest $expectedLastPath --package $lastPackage | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $verifyLast.valid) {
        throw "Last-input merged payloads did not match: $($verifyLast | ConvertTo-Json -Compress -Depth 6)"
    }

    $mergeManifest.onConflict = 'first'
    [System.IO.File]::WriteAllText($mergeManifestPath, ($mergeManifest | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))
    $firstPackage = Join-Path $tempRoot 'first.package'
    $firstMerge = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- package merge --manifest $mergeManifestPath --output $firstPackage | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $firstMerge.verified -or $firstMerge.resourceCount -ne 3 -or $firstMerge.conflicts.Count -ne 1 -or $firstMerge.conflicts[0].keptPackage -ne $packageA) {
        throw "First-input merge report was incorrect: $($firstMerge | ConvertTo-Json -Compress -Depth 6)"
    }

    $expectedFirstPath = Join-Path $tempRoot 'expected-first.json'
    $expectedFirst = [ordered]@{ format = 1; resources = @(
        [ordered]@{ type = '0x220557DA'; group = '0x00000000'; instance = '0x0000000000000001'; source = 'a.bin'; encoding = 'bytes' },
        [ordered]@{ type = '0x03B33DDF'; group = '0x00000000'; instance = '0x0000000000000002'; source = 'c.bin'; encoding = 'bytes' },
        [ordered]@{ type = '0x220557DA'; group = '0x00000000'; instance = '0x0000000000000003'; source = 'd.bin'; encoding = 'bytes' }
    ) }
    [System.IO.File]::WriteAllText($expectedFirstPath, ($expectedFirst | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))
    $verifyFirst = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- verify --manifest $expectedFirstPath --package $firstPackage | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $verifyFirst.valid) {
        throw "First-input merged payloads did not match: $($verifyFirst | ConvertTo-Json -Compress -Depth 6)"
    }

    [pscustomobject]@{
        result = 'verified'
        resources = $lastMerge.resourceCount
        conflicts = $lastMerge.conflicts.Count
        duplicateDefaultRejected = $collisionExit -eq 2
        firstPolicyVerified = $verifyFirst.valid
        lastPolicyVerified = $verifyLast.valid
        failedMergePreservedOutput = $afterCollisionHash -eq $priorOutputHash
    } | ConvertTo-Json -Compress
} finally {
    $resolvedTempRoot = [System.IO.Path]::GetFullPath($tempRoot)
    $resolvedSystemTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if (-not $resolvedTempRoot.StartsWith($resolvedSystemTemp, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to delete fixture outside the temporary directory: $resolvedTempRoot"
    }
    if (Test-Path -LiteralPath $resolvedTempRoot) {
        Remove-Item -LiteralPath $resolvedTempRoot -Recurse -Force
    }
}
