# Dot-sourced by Update-CatalogSeed.ps1. Where the catalog is published, how one asset is fetched,
# and what a manifest has to say. Ported from the 3.x installer's Scripts/CatalogRelease.ps1
# (Into-The-Latent/DiffusionNexus.Installer), without its release-gate helpers.

# Where the embedded seed lives, relative to this repo: the two files DiffusionNexus.UI embeds as
# catalog.zip and manifest.json.
$CatalogSeedFolder = 'DiffusionNexus.UI/Assets/Catalog'

# The catalog repo's releases. releases/latest is GitHub's own "newest full release" redirect, which
# never names a pre-release, so <base>/latest/download/manifest.json is the stable channel's manifest.
$DefaultCatalogReleases = 'https://github.com/Into-The-Latent/DiffusionNexus.Catalog/releases'

# The releases page to read: the one given, else DIFFUSIONNEXUS_CATALOG_RELEASES, else the real one.
# Callers print the URL they read, so a redirected run is never mistaken for the real one.
function Get-CatalogReleaseBase([string]$Given) {
    $base = if ($Given) { $Given } elseif ($env:DIFFUSIONNEXUS_CATALOG_RELEASES) { $env:DIFFUSIONNEXUS_CATALOG_RELEASES } else { $DefaultCatalogReleases }
    $base.TrimEnd('/')
}

# One release asset to a file. Public, no token. Redirects are followed (releases/latest is one).
# Throws naming the URL on any failure: no server, a 404, a timeout.
function Save-CatalogAsset([string]$Url, [string]$Path) {
    try { Invoke-WebRequest -Uri $Url -OutFile $Path -TimeoutSec 60 | Out-Null }
    catch { throw "could not download $Url ($($_.Exception.Message))" }
}

# A file's sha256 as manifests write it (lower-case hex), or '' when there is no file.
function Get-Sha256([string]$Path) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() } else { '' }
}

# One definition of a seed: the catalogVersion, the catalog commit, the archive's sha256
# (lower-cased; hex is case-insensitive), the channel and the pack time (generatedAt, compared as an
# instant). Throws naming $What when a value is not that.
function New-CatalogSeed([string]$Version, [string]$Commit, [string]$Sha256, [string]$Channel, [string]$GeneratedAt, [string]$What) {
    if ($Version -notmatch '^\d+$') { throw "$What has no catalogVersion (got '$Version')." }
    if ($Commit -notmatch '^[0-9a-fA-F]{40}$') { throw "$What has no 40-digit commit (got '$Commit')." }
    if ($Sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw "$What has no archive sha256 (got '$Sha256')." }
    if ($Channel -notmatch '^[A-Za-z]+$') { throw "$What names no channel (got '$Channel')." }
    $packed = [DateTimeOffset]::MinValue
    if ($GeneratedAt -notmatch '^\d{4}-\d\d-\d\dT' -or
        -not [DateTimeOffset]::TryParse($GeneratedAt, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$packed)) {
        throw "$What has no generatedAt (got '$GeneratedAt')."
    }
    [pscustomobject]@{
        Version = [int]$Version; Commit = $Commit.ToLowerInvariant(); Sha256 = $Sha256.ToLowerInvariant()
        Channel = $Channel; GeneratedAt = $packed; Short = $Commit.Substring(0, 7).ToLowerInvariant()
    }
}

# The seed a manifest's text describes, parsed once and as leniently as the SDK's reader
# (CatalogSchema.Json: trailing commas, comments). Every value is read as the text it is: ConvertFrom-Json
# would turn generatedAt into a local DateTime first.
function Read-CatalogManifest([string]$Text, [string]$What) {
    $options = [System.Text.Json.JsonDocumentOptions]@{ AllowTrailingCommas = $true; CommentHandling = 'Skip' }
    try { $doc = [System.Text.Json.JsonDocument]::Parse($Text.TrimStart([char]0xFEFF), $options) }
    catch { throw "$What is not JSON ($($_.InnerException.Message ?? $_.Exception.Message))" }
    try {
        $fields = @{}
        if ($doc.RootElement.ValueKind -eq 'Object') {
            foreach ($property in $doc.RootElement.EnumerateObject()) {
                if ($property.Name -eq 'archive' -and $property.Value.ValueKind -eq 'Object') {
                    foreach ($field in $property.Value.EnumerateObject()) { if ($field.Name -eq 'sha256') { $fields['sha256'] = $field.Value.ToString() } }
                } else { $fields[$property.Name] = $property.Value.ToString() }
            }
        }
    } finally { $doc.Dispose() }
    New-CatalogSeed $fields['catalogVersion'] $fields['commit'] $fields['sha256'] $fields['channel'] $fields['generatedAt'] $What
}

# A release's manifest: downloaded to $Path, read, and returned as its seed plus its Text.
# Throws naming the URL on any failure.
function Read-CatalogReleaseManifest([string]$Url, [string]$Path, [string]$What) {
    Save-CatalogAsset $Url $Path
    $text = Get-Content -LiteralPath $Path -Raw
    Read-CatalogManifest $text "$What ($Url)" | Add-Member -NotePropertyName Text -NotePropertyValue $text -PassThru
}
