param(
    [string]$UnityData = 'D:\ProgramingSoftware\UnityHub\Editors\2022.3.62f3\Editor\Data'
)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$outputDirectory = Join-Path $project 'Logs/Stage1ManagedCheck'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$runtime = Join-Path $UnityData 'NetCoreRuntime/dotnet.exe'
$compiler = Join-Path $UnityData 'DotNetSdkRoslyn/csc.dll'
$mono = Join-Path $UnityData 'MonoBleedingEdge/bin/mono.exe'
$framework = Join-Path $UnityData 'UnityReferenceAssemblies/unity-4.8-api'
$engine = Join-Path $UnityData 'Managed/UnityEngine'
$references = @(
    Get-ChildItem -LiteralPath $framework -Filter '*.dll'
    Get-Item -LiteralPath (Join-Path $framework 'Facades/netstandard.dll')
    Get-ChildItem -LiteralPath $engine -Filter 'UnityEngine*.dll'
    Get-Item -LiteralPath (Join-Path $UnityData 'Managed/UnityEditor.dll')
    Get-ChildItem -LiteralPath (Join-Path $UnityData 'Managed') -Filter 'UnityEditor.*.dll'
    Get-Item -LiteralPath (Join-Path $project 'Assets/Plugins/Main.dll')
    Get-Item -LiteralPath (Join-Path $project 'Library/ScriptAssemblies/Unity.TextMeshPro.dll')
    Get-Item -LiteralPath (Join-Path $project 'Library/ScriptAssemblies/UnityEngine.UI.dll')
    Get-Item -LiteralPath (Join-Path $project 'Assets/Plugins/Sirenix/Assemblies/Sirenix.OdinInspector.Attributes.dll')
)
$referenceArgs = @($references | Select-Object -ExpandProperty FullName -Unique | ForEach-Object { '-r:' + $_ })
$runtimeSources = @(Get-ChildItem -LiteralPath (Join-Path $project 'Assets/Scripts') -Filter '*.cs' -Recurse | Select-Object -ExpandProperty FullName)
$editorSources = @(Get-ChildItem -LiteralPath (Join-Path $project 'Assets/Editor/RehabPhotoGame') -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$runtimeAssembly = Join-Path $outputDirectory 'Stage1.Runtime.dll'
$editorAssembly = Join-Path $outputDirectory 'Stage1.Editor.dll'
$assembly = Join-Path $outputDirectory 'Stage1Check.exe'
& $runtime $compiler /nologo /noconfig /nostdlib+ /target:library /langversion:9 /define:UNITY_EDITOR,UNITY_2022_3_OR_NEWER "/out:$runtimeAssembly" @referenceArgs @runtimeSources
if ($LASTEXITCODE -ne 0) { throw 'Runtime C# managed compilation failed.' }
& $runtime $compiler /nologo /noconfig /nostdlib+ /target:library /langversion:9 /define:UNITY_EDITOR,UNITY_2022_3_OR_NEWER "/out:$editorAssembly" "-r:$runtimeAssembly" @referenceArgs @editorSources
if ($LASTEXITCODE -ne 0) { throw 'Editor C# managed compilation failed.' }
& $runtime $compiler /nologo /noconfig /nostdlib+ /target:exe /langversion:9 "/out:$assembly" "-r:$runtimeAssembly" "-r:$editorAssembly" @referenceArgs (Join-Path $PSScriptRoot 'Stage1Check.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test runner compilation failed.' }
Write-Output 'PASS: managed compilation of Assets/Scripts and Stage 1 editor tools.'
$previousMonoPath = $env:MONO_PATH
try {
    $env:MONO_PATH = "$(Join-Path $UnityData 'Managed');$engine;$(Join-Path $project 'Assets/Plugins');$(Join-Path $project 'Assets/Plugins/Sirenix/Assemblies');$(Join-Path $project 'Library/ScriptAssemblies')"
    & $mono $assembly
    if ($LASTEXITCODE -ne 0) { throw 'Stage 1 logic regression failed.' }
}
finally { $env:MONO_PATH = $previousMonoPath }
