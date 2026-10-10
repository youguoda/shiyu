# seed.ps1 - fill a probe data directory with synthetic placeholder data.
#
# Ticket 15. Everything this writes is recognisably placeholder text or a
# generated PNG; a real clipboard item must never end up in a probe database.
# The heavy lifting is done by a tiny C# tool that is compiled on the fly
# into %TEMP%\shiyu-probe-seed and talks to the SAME EntryStore API the app
# uses (referencing the worktree's freshly built Shiyu.Core.dll), so the
# schema can never drift between seeder and app.
#
# ASCII only: PowerShell 5 renders un-BOM'd Chinese in .ps1 files as mojibake.
# Chinese placeholder strings live in the C# source as \uXXXX escapes.
#
# Usage:  .\seed.ps1 -DataDir <dir> [-AppBin <app bin dir>]

param(
    [Parameter(Mandatory = $true)][string]$DataDir,
    [string]$AppBin = ""
)

$ErrorActionPreference = 'Stop'

if ($AppBin -eq "") {
    $AppBin = Join-Path $PSScriptRoot '..\..\src\Shiyu.App\bin\Debug\net9.0-windows'
}
$AppBin = [IO.Path]::GetFullPath($AppBin)

foreach ($name in @('Shiyu.Core.dll', 'Microsoft.Data.Sqlite.dll',
                    'SQLitePCLRaw.batteries_v2.dll', 'SQLitePCLRaw.core.dll',
                    'SQLitePCLRaw.provider.e_sqlite3.dll')) {
    if (-not (Test-Path (Join-Path $AppBin $name))) {
        throw "missing $name under $AppBin - build the app first (dotnet build -c Debug)"
    }
}
$nativeSqlite = Join-Path $AppBin 'runtimes\win-x64\native\e_sqlite3.dll'
if (-not (Test-Path $nativeSqlite)) {
    throw "missing $nativeSqlite"
}

$ToolDir = Join-Path $env:TEMP 'shiyu-probe-seed'
$OutDir  = Join-Path $ToolDir 'out'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$Csproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>shiyu_seedtool</AssemblyName>
    <RootNamespace>shiyu_seedtool</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="Shiyu.Core">
      <HintPath>$(Join-Path $AppBin 'Shiyu.Core.dll')</HintPath>
    </Reference>
    <Reference Include="Microsoft.Data.Sqlite">
      <HintPath>$(Join-Path $AppBin 'Microsoft.Data.Sqlite.dll')</HintPath>
    </Reference>
    <Reference Include="SQLitePCLRaw.batteries_v2">
      <HintPath>$(Join-Path $AppBin 'SQLitePCLRaw.batteries_v2.dll')</HintPath>
    </Reference>
    <Reference Include="SQLitePCLRaw.core">
      <HintPath>$(Join-Path $AppBin 'SQLitePCLRaw.core.dll')</HintPath>
    </Reference>
    <Reference Include="SQLitePCLRaw.provider.e_sqlite3">
      <HintPath>$(Join-Path $AppBin 'SQLitePCLRaw.provider.e_sqlite3.dll')</HintPath>
    </Reference>
    <None Include="$nativeSqlite" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
"@

# The seeder itself. Chinese placeholder content is written with \uXXXX
# escapes so this file stays pure ASCII end to end.
$Program = @'
// shiyu probe seeder (ticket 15). Writes ONLY synthetic data:
// placeholder text, generated PNGs, fake paths. Built by seed.ps1.
using System.IO.Compression;
using Shiyu.Core;

// Read-only mode (ticket 43): how many entries does the history hold right now?
// probe-reverse.ps1 asserts the reverse input box leaves it unchanged. Reading
// never reseeds; run it only after the probe app has exited.
if (args.Length == 2 && args[0] == "--count")
{
    using var counted = EntryStore.Open(Path.Combine(args[1], "history.db"));
    Console.WriteLine($"count={counted.Count()}");
    return 0;
}

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: shiyu_seedtool <data-dir> | --count <data-dir>");
    return 2;
}

var root = args[0];
Directory.CreateDirectory(root);
var images = Path.Combine(root, "images");
Directory.CreateDirectory(images);

// A probe directory is reseeded from zero every run - leftover WAL files
// of a killed probe would otherwise resurface old rows. The delete retries:
// a probe instance that is still shutting down can hold the file a moment.
foreach (var stale in new[] { "history.db", "history.db-wal", "history.db-shm" })
{
    var stalePath = Path.Combine(root, stale);
    for (var attempt = 0; File.Exists(stalePath) && attempt < 20; attempt++)
    {
        try { File.Delete(stalePath); }
        catch (IOException) { Thread.Sleep(250); }
        catch (UnauthorizedAccessException) { Thread.Sleep(250); }
    }

    if (File.Exists(stalePath))
    {
        Console.Error.WriteLine($"cannot remove {stalePath}: still locked by another process");
        return 3;
    }
}

File.WriteAllText(Path.Combine(root, "settings.json"), SettingsJson());

// First-use footer hints replace the entry count for the first five summons;
// a probe wants the real footer, so the state file starts exhausted.
File.WriteAllText(Path.Combine(root, "hints.json"), @"{""SummonsLeft"":0,""ActionsSeen"":3}");

using var store = EntryStore.Open(Path.Combine(root, "history.db"));
var now = DateTimeOffset.Now;

// --- 12 text entries (zh/en mixed, short and long, several subtypes) -----
// Newest first in the list below == first in the bar.
Entry T(int minutesAgo, string text, string source) =>
    store.Append(text, source, now.AddMinutes(-minutesAgo));

var t01 = T(1,   "\u5360\u4f4d\u6b63\u6587 01 - short placeholder line", "probe-notepad");
var t02 = T(9,   "placeholder line 02 with \u5360\u4f4d mixed English words", "probe-browser");
var t03 = T(23,  "https://example.invalid/probe/link-03", "probe-browser");
var img1 = store.AppendImage(
    "\u5360\u4f4d\u56fe\u7247 01 (placeholder image)",
    Png(320, 200, 1), SaveOriginal(images, 1, 800, 500),
    "probe-paint", now.AddMinutes(-41), 800, 500);
var t04 = T(65,  "probe.user@example.invalid", "probe-browser");
var files1 = store.AppendFiles(
    new[] { @"C:\probe-placeholder\alpha-01.txt", @"C:\probe-placeholder\beta-02.bin" },
    "probe-explorer", now.AddMinutes(-122));
var t05 = T(200, "#4C8DFF", "probe-designer");
var t06 = T(260, "\u5360\u4f4d\u6b63\u6587 06 " + string.Concat(Enumerable.Repeat(
    "\u957f\u6587\u672c\u5360\u4f4d long placeholder body text ", 6)), "probe-notepad");
var img2 = store.AppendImage(
    "\u5360\u4f4d\u56fe\u7247 02 (placeholder image)",
    Png(320, 200, 2), SaveOriginal(images, 2, 640, 640),
    "probe-paint", now.AddHours(-5), 640, 640);
var t07 = T(310, "var x = probe(); // \u5360\u4f4d\u4ee3\u7801 code placeholder", "probe-terminal");
var files2 = store.AppendFiles(
    new[]
    {
        @"C:\probe-placeholder\report-04.pdf",
        @"C:\probe-placeholder\data-05.csv",
        @"C:\probe-placeholder\notes-06.txt",
        @"C:\probe-placeholder\archive-07.zip",
    },
    "probe-explorer", now.AddHours(-20));
var t08 = T(1500, @"C:\probe-placeholder\path-08\deep\nested.txt", "probe-terminal");
var img3 = store.AppendImage(
    "\u5360\u4f4d\u56fe\u7247 03 (placeholder image)",
    Png(320, 200, 3), SaveOriginal(images, 3, 900, 300),
    "probe-browser", now.AddHours(-30), 900, 300);
var t09 = T(2600, "\u5360\u4f4d 09 zh only line", "probe-notepad");
var t10 = T(3000, "placeholder ten \u5360\u4f4d\u5341", "probe-browser");
var t11 = T(4000, "\u5360\u4f4d\u6b63\u6587 11 - another mixed placeholder", "probe-notepad");
var t12 = T(5000, "\u5360\u4f4d\u6b63\u6587 12 - pinned placeholder", "probe-notepad");

// --- tags / groups / favourites / pinned / notes / use counts -------------
store.AddTag(t02.Id, "probe-a");
store.AddTag(t05.Id, "probe-a");
store.AddTag(t05.Id, "probe-b");
store.AddTag(t09.Id, "probe-c");
store.AddTag(t11.Id, "probe-b");

var group1 = store.CreateGroup("probe-group-1");
var group2 = store.CreateGroup("probe-group-2");
store.SetEntryGroup(t02.Id, group1);
store.SetEntryGroup(t09.Id, group2);

store.SetFavorite(t05.Id, true);   // favourite + protection => delete hidden
store.SetFavorite(img1.Id, true);
store.SetPinned(t12.Id, true);

store.SetNote(t02.Id, "\u5360\u4f4d\u5907\u6ce8 placeholder note");
store.BumpUse(t01.Id);
store.BumpUse(t01.Id);
store.BumpUse(t06.Id);

Console.WriteLine(
    $"seeded: text=12 image=3 files=2 total={store.Count()} tags=3 groups=2 favorites=2 pinned=1");
Console.WriteLine($"data dir: {root}");
return 0;

// --- placeholder settings --------------------------------------------------
// OwnKey + a scheme-less base URL makes every translation attempt fail with
// a raw English .NET exception (the panel-defect probe needs exactly that),
// deterministically and without touching the network.
static string SettingsJson()
    =>
    """
    {
      "TargetLanguage": "Chinese",
      "TranslationBackend": "OwnKey",
      "BackendBaseUrl": "probe.invalid",
      "BackendModel": "probe-placeholder-model",
      "BackendApiKey": "probe-placeholder-key",
      "OnboardingCompleted": true,
      "UpdateAutoCheck": false,
      "TakeOverWinV": false,
      "SelectionBadge": false,
      "Theme": "System"
    }
    """;

// --- a tiny PNG encoder (no System.Drawing: no restore, no locale) --------
static string SaveOriginal(string dir, int index, int w, int h)
{
    var path = Path.Combine(dir, $"probe-image-{index:00}.png");
    File.WriteAllBytes(path, Png(w, h, index));
    return path;
}

static byte[] Png(int w, int h, int variant)
{
    var raw = new byte[h * (1 + w * 3)];
    for (var y = 0; y < h; y++)
    {
        var row = y * (1 + w * 3);
        raw[row] = 0; // filter: none
        for (var x = 0; x < w; x++)
        {
            var i = row + 1 + x * 3;
            (raw[i], raw[i + 1], raw[i + 2]) = Pixel(x, y, w, h, variant);
        }
    }

    using var compressed = new MemoryStream();
    using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal))
    {
        deflate.Write(raw, 0, raw.Length);
    }

    using var png = new MemoryStream();
    png.WriteByte(0x89);
    var signature = System.Text.Encoding.ASCII.GetBytes("PNG\r\n\x1a\n");
    png.Write(signature, 0, signature.Length);
    WriteChunk(png, "IHDR", Ihdr(w, h));
    WriteChunk(png, "IDAT", ZlibWrap(compressed.ToArray(), raw));
    WriteChunk(png, "IEND", []);
    return png.ToArray();

    static (byte, byte, byte) Pixel(int x, int y, int w, int h, int variant)
    {
        // Three distinguishable placeholder patterns, all clearly synthetic:
        // diagonal two-tone bands with a white cross / stripes / checker.
        var band = (x + y * variant) / Math.Max(8, w / 12) % 2 == 0;
        switch (variant)
        {
            case 1:
                if (Math.Abs(x - w / 2) < w / 16 || Math.Abs(y - h / 2) < h / 16) return (255, 255, 255);
                return band ? ((byte)26, (byte)102, (byte)219) : ((byte)76, (byte)141, (byte)255);
            case 2:
                return (x / (w / 8) + y / (h / 4)) % 2 == 0
                    ? ((byte)255, (byte)26, (byte)102)
                    : ((byte)255, (byte)204, (byte)221);
            default:
                return band ? ((byte)240, (byte)173, (byte)78) : ((byte)64, (byte)64, (byte)72);
        }
    }

    static byte[] Ihdr(int w, int h)
    {
        var b = new byte[13];
        WriteBe(b, 0, w);
        WriteBe(b, 4, h);
        b[8] = 8;  // bit depth
        b[9] = 2;  // colour type: truecolor RGB
        return b;
    }

    static void WriteBe(byte[] b, int offset, int value)
    {
        b[offset] = (byte)(value >> 24);
        b[offset + 1] = (byte)(value >> 16);
        b[offset + 2] = (byte)(value >> 8);
        b[offset + 3] = (byte)value;
    }

    // The Adler-32 trailer covers the UNCOMPRESSED bytes. Summing the deflate
    // output instead left every seeded PNG failing its checksum at the very
    // end, and the decoder dropped the last scanline - a black row under every
    // seeded image (found 2026-10-10 by probe-preview-image's seam check).
    static byte[] ZlibWrap(byte[] deflate, byte[] raw)
    {
        var zlib = new byte[deflate.Length + 6];
        zlib[0] = 0x78;
        zlib[1] = 0x9c;
        deflate.CopyTo(zlib, 2);
        WriteBe(zlib, zlib.Length - 4, unchecked((int)Adler32(raw)));
        return zlib;
    }

    static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var d in data)
        {
            a = (a + d) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBe(len, 0, data.Length);
        s.Write(len, 0, 4);

        var body = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(body, 0);
        data.CopyTo(body, 4);
        s.Write(body, 0, body.Length);

        var crc = new byte[4];
        WriteBe(crc, 0, unchecked((int)Crc32(body)));
        s.Write(crc, 0, 4);
    }

    static uint Crc32(byte[] data)
    {
        uint crc = 0xffffffff;
        foreach (var d in data)
        {
            crc ^= d;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc >> 1) ^ (0xedb88320 & (0 - (crc & 1)));
            }
        }
        return crc ^ 0xffffffff;
    }
}
'@

# Write the tool sources every run (hint paths embed the current worktree).
New-Item -ItemType Directory -Force -Path $ToolDir | Out-Null
Set-Content -Path (Join-Path $ToolDir 'shiyu_seedtool.csproj') -Value $Csproj -Encoding ASCII
Set-Content -Path (Join-Path $ToolDir 'Program.cs') -Value $Program -Encoding ASCII

# Build quietly to the fixed output dir, then run the dll directly.
dotnet build (Join-Path $ToolDir 'shiyu_seedtool.csproj') -v q --nologo `
    /p:OutDir="$OutDir\" | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "seed tool build failed - see output above"
}

dotnet (Join-Path $OutDir 'shiyu_seedtool.dll') $DataDir
if ($LASTEXITCODE -ne 0) {
    throw "seed tool failed"
}
