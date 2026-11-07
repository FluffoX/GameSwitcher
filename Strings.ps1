# Localization: loads lang/<code>.json and provides T for translated strings.
# Missing keys fall back to English, then to the key itself.
$langDir = Join-Path $PSScriptRoot 'lang'
$script:strings = @{}
$script:fallbackStrings = @{}
$script:language = 'en'

function Read-LangFile([string]$Code) {
    $h = @{}
    $path = Join-Path $langDir "$Code.json"
    if (Test-Path $path) {
        $json = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($p in $json.PSObject.Properties) { $h[$p.Name] = [string]$p.Value }
    }
    $h
}

# All available languages as Code/Name (native name from the "_name" key)
function Get-Languages {
    foreach ($f in Get-ChildItem $langDir -Filter '*.json' | Sort-Object Name) {
        $name = (Get-Content $f.FullName -Raw -Encoding UTF8 | ConvertFrom-Json).'_name'
        [pscustomobject]@{ Code = $f.BaseName; Name = $name }
    }
}

# Language from settings.json, otherwise the Windows display language, otherwise English
function Get-ConfiguredLanguage {
    $code = $null
    try { $code = (Get-Content (Join-Path $PSScriptRoot 'settings.json') -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json).Language } catch {}
    if (-not $code) { $code = [Globalization.CultureInfo]::CurrentUICulture.TwoLetterISOLanguageName }
    if (Test-Path (Join-Path $langDir "$code.json")) { $code } else { 'en' }
}

function Initialize-Language([string]$Code) {
    $script:fallbackStrings = Read-LangFile 'en'
    $script:strings = Read-LangFile $Code
    $script:language = $Code
}

# T 'key' [format arguments...]
function T([string]$Key) {
    $text = $script:strings[$Key]
    if (-not $text) { $text = $script:fallbackStrings[$Key] }
    if (-not $text) { $text = $Key }
    if ($args.Count) { $text = $text -f $args }
    $text
}
