<#
.SYNOPSIS
    Copies the texts Granary takes from Silo's web client into src/Granary.Core/Texts/Catalogs/web
    (docs/release.md, "Translations").

.DESCRIPTION
    texts.lock.json pins a silo-server commit and the SHA-256 of each language's web/src/locales/<tag>.json there: the
    web's compiled catalog, one message per key. For every language the script gets the pinned file, checks its SHA-256
    and keeps only the keys src/Granary.Core/Texts/Catalogs/web-keys.txt lists, each message unchanged. It writes them
    to src/Granary.Core/Texts/Catalogs/web/<tag>.json, UTF-8 without BOM, keys in ordinal order, indented by two spaces.
    Nothing in the repository changes until every file is checked, and a file whose content stays the same is left
    alone, so a second run changes nothing.

    A listed key missing from English stops the script: English is every language's fallback. A listed key missing from
    another language is only reported, since that language then shows the English text.

    Downloads come from raw.githubusercontent.com: the repository is public. Runs in Windows PowerShell 5.1 and in
    PowerShell 7.

.PARAMETER SourceDir
    A clone of silo-server to read the files from instead of downloading them. They are read from the pinned commit
    with git, whatever is checked out and whatever its line endings, and their SHA-256 is still checked.

.PARAMETER Commit
    Moves the pin to this silo-server commit: the files are taken from it and texts.lock.json gets the commit and the
    files' new SHA-256. A download needs the full 40-character commit; with -SourceDir anything git resolves to a commit
    will do. To add a language, add its tag to the lock's sha256 with an empty value and run with -Commit.

.EXAMPLE
    ./scripts/sync-web-texts.ps1
    Downloads the pinned files and writes the catalogs from them.

.EXAMPLE
    ./scripts/sync-web-texts.ps1 -SourceDir ..\silo-server
    The same from a local clone that has the pinned commit.

.EXAMPLE
    ./scripts/sync-web-texts.ps1 -Commit 0e95e55b605b38477a130ae26c88e760bbe7a302
    Moves the pin to that commit, then writes the catalogs from it.
#>
[CmdletBinding()]
param(
    [string]$SourceDir = '',
    [string]$Commit = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest

$RepoRoot = Split-Path -Parent $PSScriptRoot
$LockPath = Join-Path $RepoRoot 'texts.lock.json'
$CatalogRoot = Join-Path $RepoRoot 'src/Granary.Core/Texts/Catalogs'
$KeyListPath = Join-Path $CatalogRoot 'web-keys.txt'
$OutDir = Join-Path $CatalogRoot 'web'

# What the lock and the key list may hold. Patterns use [0-9] (\d takes any script's digits) and \z (a $ allows a line
# end after it). Keys follow Silo's key rules (silo-server docs/architecture/i18n.md, "Keys").
$RepositoryPattern = '\A[A-Za-z0-9-]{1,39}/(?!\.{1,2}\z)[A-Za-z0-9._-]{1,100}\z'
$PathPattern = '\A(?!.*(?:\A|/)\.{1,2}(?:/|\z))[A-Za-z0-9._-]{1,100}(?:/[A-Za-z0-9._-]{1,100}){0,9}\z'
$CommitPattern = '\A[0-9a-f]{40}\z'
$Sha256Pattern = '\A[0-9a-f]{64}\z'
$TagPattern = '\A[a-z]{2,3}(?:-[A-Za-z0-9]{2,8}){0,2}\z'
$KeyPattern = '\A[a-z][A-Za-z0-9_]*(?:\.[a-z0-9][A-Za-z0-9_]*)*\z'
# What -Commit may name for git: no option, no space, nothing git reads as a path or a revision range.
$RevisionPattern = '\A[A-Za-z0-9][A-Za-z0-9._/-]{0,199}\z'
# Today the largest file, Arabic, is 4.4 MB.
$MaxFileBytes = 32MB

$Utf8NoBom = New-Object System.Text.UTF8Encoding $false
$Utf8Strict = New-Object System.Text.UTF8Encoding $false, $true

<#
A strict JSON reader that keeps every object's members in order and every number as written, and a writer that lays
JSON out as the web's catalogs are written: JavaScript's JSON.stringify(value, null, 2), with invisible characters
escaped. A message Granary keeps is byte for byte the upstream one. C# 5, for Windows PowerShell's compiler.
#>
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GranaryWebTexts
{
    public sealed class JsonObjectValue
    {
        public readonly List<KeyValuePair<string, object>> Members = new List<KeyValuePair<string, object>>();

        public object Get(string name)
        {
            foreach (KeyValuePair<string, object> member in Members)
            {
                if (string.Equals(member.Key, name, StringComparison.Ordinal)) return member.Value;
            }
            throw new KeyNotFoundException(name);
        }

        public bool Has(string name)
        {
            foreach (KeyValuePair<string, object> member in Members)
            {
                if (string.Equals(member.Key, name, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        public void Add(string name, object value)
        {
            Members.Add(new KeyValuePair<string, object>(name, value));
        }
    }

    public sealed class JsonNumberValue
    {
        public readonly string Text;

        public JsonNumberValue(string text)
        {
            Text = text;
        }
    }

    public static class CatalogJson
    {
        private const int MaxDepth = 64;

        public static JsonObjectValue ParseObject(string text)
        {
            Reader reader = new Reader(text);
            reader.SkipSpace();
            if (reader.Peek() != '{') throw reader.Error("the file is not a JSON object");
            JsonObjectValue result = (JsonObjectValue)reader.ReadValue(0);
            reader.SkipSpace();
            if (!reader.AtEnd) throw reader.Error("text after the object");
            return result;
        }

        // The members of catalog whose names are in keys, in ordinal order, as a file's text.
        public static string Trim(JsonObjectValue catalog, string[] keys)
        {
            Dictionary<string, object> byName = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> member in catalog.Members) byName[member.Key] = member.Value;
            string[] sorted = (string[])keys.Clone();
            Array.Sort(sorted, StringComparer.Ordinal);
            JsonObjectValue trimmed = new JsonObjectValue();
            foreach (string key in sorted)
            {
                object value;
                if (byName.TryGetValue(key, out value)) trimmed.Add(key, value);
            }
            return Write(trimmed);
        }

        // A file's text: the value as JSON.stringify(value, null, 2) writes it, with a final line end.
        public static string Write(object value)
        {
            StringBuilder text = new StringBuilder();
            WriteValue(text, value, "");
            return text.Append('\n').ToString();
        }

        private static void WriteValue(StringBuilder text, object value, string indent)
        {
            if (value == null) { text.Append("null"); return; }
            if (value is bool) { text.Append((bool)value ? "true" : "false"); return; }
            string s = value as string;
            if (s != null) { WriteString(text, s); return; }
            JsonNumberValue number = value as JsonNumberValue;
            if (number != null) { text.Append(number.Text); return; }
            string inner = indent + "  ";
            JsonObjectValue obj = value as JsonObjectValue;
            if (obj != null)
            {
                if (obj.Members.Count == 0) { text.Append("{}"); return; }
                text.Append("{\n");
                for (int i = 0; i < obj.Members.Count; i++)
                {
                    if (i > 0) text.Append(",\n");
                    text.Append(inner);
                    WriteString(text, obj.Members[i].Key);
                    text.Append(": ");
                    WriteValue(text, obj.Members[i].Value, inner);
                }
                text.Append('\n').Append(indent).Append('}');
                return;
            }
            List<object> array = value as List<object>;
            if (array != null)
            {
                if (array.Count == 0) { text.Append("[]"); return; }
                text.Append("[\n");
                for (int i = 0; i < array.Count; i++)
                {
                    if (i > 0) text.Append(",\n");
                    text.Append(inner);
                    WriteValue(text, array[i], inner);
                }
                text.Append('\n').Append(indent).Append(']');
                return;
            }
            throw new ArgumentException("Not a JSON value: " + value.GetType().FullName);
        }

        // As the web's catalogs: JSON.stringify's escapes (two characters, or \u00xx for other control characters and a
        // lone surrogate), and \uXXXX in capitals for the invisible marks of bidirectional text, such as the
        // right-to-left mark U+200F. The marks are a fixed list, not a Unicode category, so Windows PowerShell and
        // PowerShell 7 write the same file.
        private static bool IsInvisibleMark(char c)
        {
            int code = c;
            return code == 0x061C || (code >= 0x200B && code <= 0x200F) || (code >= 0x202A && code <= 0x202E)
                || (code >= 0x2060 && code <= 0x2064) || (code >= 0x2066 && code <= 0x2069) || code == 0xFEFF;
        }

        private static void WriteString(StringBuilder text, string value)
        {
            text.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': text.Append("\\\""); continue;
                    case '\\': text.Append("\\\\"); continue;
                    case '\b': text.Append("\\b"); continue;
                    case '\f': text.Append("\\f"); continue;
                    case '\n': text.Append("\\n"); continue;
                    case '\r': text.Append("\\r"); continue;
                    case '\t': text.Append("\\t"); continue;
                }
                bool pairStart = char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]);
                bool pairEnd = char.IsLowSurrogate(c) && i > 0 && char.IsHighSurrogate(value[i - 1]);
                bool loneSurrogate = char.IsSurrogate(c) && !pairStart && !pairEnd;
                if (c < ' ' || loneSurrogate)
                {
                    text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    continue;
                }
                if (IsInvisibleMark(c))
                {
                    text.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    continue;
                }
                text.Append(c);
            }
            text.Append('"');
        }

        private sealed class Reader
        {
            private readonly string _text;
            private int _position;

            public Reader(string text)
            {
                _text = text;
            }

            public bool AtEnd { get { return _position >= _text.Length; } }

            public char Peek()
            {
                return AtEnd ? '\0' : _text[_position];
            }

            public FormatException Error(string what)
            {
                string at = _position.ToString(CultureInfo.InvariantCulture);
                return new FormatException("Not valid JSON at character " + at + ": " + what + ".");
            }

            public void SkipSpace()
            {
                while (!AtEnd)
                {
                    char c = _text[_position];
                    if (c != ' ' && c != '\t' && c != '\n' && c != '\r') return;
                    _position++;
                }
            }

            public object ReadValue(int depth)
            {
                if (depth > MaxDepth) throw Error("nested too deeply");
                SkipSpace();
                char c = Peek();
                if (c == '{') return ReadObject(depth);
                if (c == '[') return ReadArray(depth);
                if (c == '"') return ReadString();
                if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                if (ReadWord("true")) return true;
                if (ReadWord("false")) return false;
                if (ReadWord("null")) return null;
                throw Error("a value was expected");
            }

            private JsonObjectValue ReadObject(int depth)
            {
                JsonObjectValue result = new JsonObjectValue();
                Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.Ordinal);
                _position++;
                SkipSpace();
                if (Peek() == '}') { _position++; return result; }
                while (true)
                {
                    SkipSpace();
                    if (Peek() != '"') throw Error("a member name was expected");
                    string name = ReadString();
                    if (seen.ContainsKey(name)) throw Error("the member \"" + name + "\" appears twice");
                    seen[name] = true;
                    SkipSpace();
                    if (Peek() != ':') throw Error("':' was expected");
                    _position++;
                    result.Add(name, ReadValue(depth + 1));
                    SkipSpace();
                    char c = Peek();
                    _position++;
                    if (c == '}') return result;
                    if (c != ',') { _position--; throw Error("',' or '}' was expected"); }
                }
            }

            private List<object> ReadArray(int depth)
            {
                List<object> result = new List<object>();
                _position++;
                SkipSpace();
                if (Peek() == ']') { _position++; return result; }
                while (true)
                {
                    result.Add(ReadValue(depth + 1));
                    SkipSpace();
                    char c = Peek();
                    _position++;
                    if (c == ']') return result;
                    if (c != ',') { _position--; throw Error("',' or ']' was expected"); }
                }
            }

            private string ReadString()
            {
                StringBuilder value = new StringBuilder();
                _position++;
                while (true)
                {
                    if (AtEnd) throw Error("the string does not end");
                    char c = _text[_position++];
                    if (c == '"') return value.ToString();
                    if (c < ' ') { _position--; throw Error("a control character in a string"); }
                    if (c != '\\') { value.Append(c); continue; }
                    if (AtEnd) throw Error("the string does not end");
                    char escape = _text[_position++];
                    switch (escape)
                    {
                        case '"': value.Append('"'); break;
                        case '\\': value.Append('\\'); break;
                        case '/': value.Append('/'); break;
                        case 'b': value.Append('\b'); break;
                        case 'f': value.Append('\f'); break;
                        case 'n': value.Append('\n'); break;
                        case 'r': value.Append('\r'); break;
                        case 't': value.Append('\t'); break;
                        case 'u':
                            int code;
                            if (_position + 4 > _text.Length || !int.TryParse(_text.Substring(_position, 4),
                                NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code))
                            {
                                throw Error("a \\u escape needs four hex digits");
                            }
                            value.Append((char)code);
                            _position += 4;
                            break;
                        default:
                            _position--;
                            throw Error("an unknown escape");
                    }
                }
            }

            private JsonNumberValue ReadNumber()
            {
                int start = _position;
                if (Peek() == '-') _position++;
                if (Peek() == '0') _position++;
                else if (!ReadDigits()) throw Error("a digit was expected");
                if (Peek() == '.')
                {
                    _position++;
                    if (!ReadDigits()) throw Error("a digit was expected");
                }
                if (Peek() == 'e' || Peek() == 'E')
                {
                    _position++;
                    if (Peek() == '+' || Peek() == '-') _position++;
                    if (!ReadDigits()) throw Error("a digit was expected");
                }
                return new JsonNumberValue(_text.Substring(start, _position - start));
            }

            private bool ReadDigits()
            {
                int start = _position;
                while (!AtEnd && _text[_position] >= '0' && _text[_position] <= '9') _position++;
                return _position > start;
            }

            private bool ReadWord(string word)
            {
                if (string.CompareOrdinal(_text, _position, word, 0, word.Length) != 0) return false;
                _position += word.Length;
                return true;
            }
        }
    }
}
'@

function Get-Sha256Hex {
    param([Parameter(Mandatory)][AllowEmptyCollection()][byte[]]$Bytes)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return -join ($sha.ComputeHash($Bytes) | ForEach-Object { $_.ToString('x2') })
    }
    finally {
        $sha.Dispose()
    }
}

# Copies a stream into memory and stops with an error as soon as more than MaxFileBytes have come.
function Read-StreamCapped {
    param([Parameter(Mandatory)][IO.Stream]$Source, [Parameter(Mandatory)][string]$What)
    $target = New-Object IO.MemoryStream
    $buffer = New-Object byte[] 65536
    while (($read = $Source.Read($buffer, 0, $buffer.Length)) -gt 0) {
        if ($target.Length + $read -gt $MaxFileBytes) {
            throw "$What is larger than the $MaxFileBytes bytes it may have."
        }
        $target.Write($buffer, 0, $read)
    }
    return , $target.ToArray()
}

# The text a file of the repository holds, or $null when there is none.
function Read-RepoText {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }
    return [IO.File]::ReadAllText($Path, $Utf8Strict)
}

<#
Whether a file already holds this text. Only line ends may differ, as a checkout with CRLF has them; a byte order mark
or bytes that are not UTF-8 make it a change, so the script writes the file afresh.
#>
function Test-SameText {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Text)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        return $false
    }
    try {
        $old = $Utf8Strict.GetString($bytes)
    }
    catch {
        return $false
    }
    return $old.Replace("`r`n", "`n") -ceq $Text
}

function Get-LockString {
    param([Parameter(Mandatory)]$Object, [Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Pattern)
    if (-not $Object.Has($Name) -or $Object.Get($Name) -isnot [string] -or $Object.Get($Name) -cnotmatch $Pattern) {
        throw "texts.lock.json: web.$Name is missing or not valid."
    }
    return [string]$Object.Get($Name)
}

<#
texts.lock.json's web entry: the repository, the commit, the folder of the catalogs, a SHA-256 per language tag and a
note. A field it does not know stops the script, since rewriting the lock would drop it. With -Commit the old hashes
are not used, so a new language's may be empty.
#>
function Read-TextsLock {
    param([Parameter(Mandatory)][bool]$Repin)
    $text = Read-RepoText $LockPath
    if ($null -eq $text) {
        throw "There is no texts.lock.json at $LockPath."
    }
    $root = [GranaryWebTexts.CatalogJson]::ParseObject($text)
    if ($root.Members.Count -ne 1 -or $root.Members[0].Key -cne 'web' -or $root.Members[0].Value -isnot [GranaryWebTexts.JsonObjectValue]) {
        throw 'texts.lock.json holds more than its web entry; teach sync-web-texts.ps1 to keep the rest.'
    }
    $web = $root.Members[0].Value
    $known = @('repository', 'commit', 'path', 'sha256', 'note')
    foreach ($member in $web.Members) {
        if ($known -cnotcontains $member.Key) {
            throw "texts.lock.json: web has a field sync-web-texts.ps1 does not know: $($member.Key)"
        }
    }
    if (-not $web.Has('sha256') -or $web.Get('sha256') -isnot [GranaryWebTexts.JsonObjectValue]) {
        throw 'texts.lock.json: web.sha256 is missing.'
    }
    $files = [ordered]@{}
    foreach ($member in $web.Get('sha256').Members) {
        if ($member.Key -cnotmatch $TagPattern) {
            throw "texts.lock.json: $($member.Key) is not a language tag."
        }
        # The tags name files, and Windows does not tell pt-BR.json from pt-br.json.
        if ($files.Contains($member.Key)) {
            throw "texts.lock.json names $($member.Key) twice."
        }
        $valid = $member.Value -is [string] -and ($member.Value -cmatch $Sha256Pattern -or ($Repin -and $member.Value -ceq ''))
        if (-not $valid) {
            throw "texts.lock.json: the SHA-256 of $($member.Key) is not valid."
        }
        $files[$member.Key] = $member.Value
    }
    if (-not $files.Contains('en')) {
        throw 'texts.lock.json does not pin English (en), the fallback of every language.'
    }
    $note = ''
    if ($web.Has('note')) {
        $note = Get-LockString $web 'note' '\A[^\x00-\x1f]*\z'
    }
    return [pscustomobject]@{
        Repository = Get-LockString $web 'repository' $RepositoryPattern
        Commit     = Get-LockString $web 'commit' $CommitPattern
        Path       = Get-LockString $web 'path' $PathPattern
        Files      = $files
        Note       = $note
    }
}

function New-LockText {
    param([Parameter(Mandatory)]$Lock, [Parameter(Mandatory)][string]$Commit, [Parameter(Mandatory)]$Hashes)
    $sha256 = New-Object GranaryWebTexts.JsonObjectValue
    $sorted = [string[]]@($Hashes.Keys)
    [Array]::Sort($sorted, [StringComparer]::Ordinal)
    foreach ($tag in $sorted) {
        $sha256.Add($tag, $Hashes[$tag])
    }
    $web = New-Object GranaryWebTexts.JsonObjectValue
    $web.Add('repository', $Lock.Repository)
    $web.Add('commit', $Commit)
    $web.Add('path', $Lock.Path)
    $web.Add('sha256', $sha256)
    if ($Lock.Note) {
        $web.Add('note', $Lock.Note)
    }
    $root = New-Object GranaryWebTexts.JsonObjectValue
    $root.Add('web', $web)
    return [GranaryWebTexts.CatalogJson]::Write($root)
}

<#
The keys of web-keys.txt: one per line, in ordinal order, each once. A blank line and a line starting with # are left
out.
#>
function Read-KeyList {
    $text = Read-RepoText $KeyListPath
    if ($null -eq $text) {
        throw "There is no key list at $KeyListPath."
    }
    $keys = New-Object 'System.Collections.Generic.List[string]'
    $number = 0
    foreach ($line in ($text -split "`r?`n")) {
        $number++
        $trimmed = $line.Trim()
        if ($trimmed.Length -eq 0 -or $trimmed[0] -eq [char]'#') {
            continue
        }
        if ($line -cne $trimmed -or $line -cnotmatch $KeyPattern) {
            throw "web-keys.txt, line ${number}: '$line' is not a key alone on its line."
        }
        if ($keys.Count -gt 0) {
            $previous = $keys[$keys.Count - 1]
            $order = [string]::CompareOrdinal($previous, $line)
            if ($order -eq 0) {
                throw "web-keys.txt, line ${number}: $line is listed twice."
            }
            if ($order -gt 0) {
                throw "web-keys.txt, line ${number}: $line belongs before $previous; keep the keys in ordinal order."
            }
        }
        $keys.Add($line)
    }
    if ($keys.Count -eq 0) {
        throw 'web-keys.txt lists no key.'
    }
    return , $keys.ToArray()
}

<#
Runs git in a folder and returns its exit code, what it wrote as bytes, and its error text. The output is read as
bytes, so a file comes exactly as git stores it. The callers check the arguments.
#>
function Invoke-Git {
    param([Parameter(Mandatory)][string]$Folder, [Parameter(Mandatory)][string]$Arguments, [Parameter(Mandatory)][string]$What)
    # git.exe itself: a git.cmd would run through cmd.exe, which takes the ^ of ^{commit} for its own.
    $git = Get-Command git -CommandType Application -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -eq '.exe' } | Select-Object -First 1
    if (-not $git) {
        throw '-SourceDir reads the files with git, and there is no git.exe on the PATH.'
    }
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $git.Path
    $info.Arguments = "-C `"$Folder`" $Arguments"
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($info)
    try {
        $errors = $process.StandardError.ReadToEndAsync()
        $bytes = Read-StreamCapped -Source $process.StandardOutput.BaseStream -What $What
        $process.WaitForExit()
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Bytes = $bytes; Errors = $errors.Result.Trim() }
    }
    finally {
        if (-not $process.HasExited) {
            $process.Kill()
        }
        $process.Dispose()
    }
}

# A file of the clone at a commit, as git stores it: line ends as committed, whatever the checkout has.
function Read-SourceFile {
    param([Parameter(Mandatory)][string]$Folder, [Parameter(Mandatory)][string]$Commit, [Parameter(Mandatory)][string]$Path)
    $result = Invoke-Git -Folder $Folder -Arguments "cat-file blob ${Commit}:$Path" -What $Path
    if ($result.ExitCode -ne 0) {
        throw "git could not read $Path at $Commit in ${Folder}: $($result.Errors)"
    }
    return , $result.Bytes
}

# The full commit a revision of the clone names, or an error when the clone does not have it.
function Resolve-SourceCommit {
    param([Parameter(Mandatory)][string]$Folder, [Parameter(Mandatory)][string]$Revision)
    if ($Revision -cnotmatch $RevisionPattern) {
        throw "-Commit takes a commit, not '$Revision'."
    }
    $result = Invoke-Git -Folder $Folder -Arguments "rev-parse --verify --quiet $Revision^{commit}" -What "commit $Revision"
    if ($result.ExitCode -ne 0) {
        throw "$Folder has no commit $Revision. Fetch it there first."
    }
    $sha = $Utf8Strict.GetString($result.Bytes).Trim()
    if ($sha -cnotmatch $CommitPattern) {
        throw "git named $Revision as '$sha', not a full commit."
    }
    return $sha
}

# The client reads each answer whole, so its timeout covers the body too and it refuses one over the size bound.
function New-HttpClient {
    Add-Type -AssemblyName System.Net.Http
    $client = New-Object System.Net.Http.HttpClient
    $client.Timeout = [TimeSpan]::FromMinutes(2)
    $client.MaxResponseContentBufferSize = $MaxFileBytes
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('granary-sync-web-texts')
    return $client
}

function Receive-File {
    param([Parameter(Mandatory)]$Client, [Parameter(Mandatory)][string]$Url)
    Write-Host "Downloading $Url"
    try {
        $response = $Client.GetAsync($Url).GetAwaiter().GetResult()
    }
    catch {
        throw "Downloading $Url failed: $($_.Exception.Message)"
    }
    try {
        if (-not $response.IsSuccessStatusCode) {
            throw "Downloading $Url failed with HTTP $([int]$response.StatusCode)."
        }
        return , $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
    }
    finally {
        $response.Dispose()
    }
}

$repin = $Commit -ne ''
$lock = Read-TextsLock -Repin $repin
$keys = Read-KeyList
$tags = @($lock.Files.Keys)

$source = $null
if ($SourceDir) {
    if (-not (Test-Path -LiteralPath $SourceDir -PathType Container)) {
        throw "-SourceDir: there is no folder $SourceDir."
    }
    $source = (Resolve-Path -LiteralPath $SourceDir).ProviderPath.TrimEnd('\', '/')
}

$commitSha = $lock.Commit
if ($repin) {
    if ($source) {
        $commitSha = Resolve-SourceCommit -Folder $source -Revision $Commit
    }
    elseif ($Commit -match $CommitPattern) {
        $commitSha = $Commit.ToLowerInvariant()
    }
    else {
        throw '-Commit needs the full 40-character commit for a download; with -SourceDir, git resolves a shorter one.'
    }
    Write-Host "Moving the pin from $($lock.Commit) to $commitSha."
}
elseif ($source) {
    [void](Resolve-SourceCommit -Folder $source -Revision $commitSha)
}

# Every file is fetched, checked and read before anything is written.
$client = $null
$hashes = [ordered]@{}
$catalogs = @{}
try {
    foreach ($tag in $tags) {
        $relative = "$($lock.Path)/$tag.json"
        if ($source) {
            $bytes = Read-SourceFile -Folder $source -Commit $commitSha -Path $relative
        }
        else {
            if (-not $client) {
                $client = New-HttpClient
            }
            $bytes = Receive-File -Client $client -Url "https://raw.githubusercontent.com/$($lock.Repository)/$commitSha/$relative"
        }
        $sha = Get-Sha256Hex $bytes
        if (-not $repin -and $sha -cne $lock.Files[$tag]) {
            throw "SHA-256 mismatch for $relative at ${commitSha}:`n  expected $($lock.Files[$tag])`n  actual   $sha"
        }
        $hashes[$tag] = $sha
        try {
            $catalogs[$tag] = [GranaryWebTexts.CatalogJson]::ParseObject($Utf8Strict.GetString($bytes))
        }
        catch {
            throw "$relative at $commitSha is not a JSON catalog: $($_.Exception.Message)"
        }
    }
}
finally {
    if ($client) {
        $client.Dispose()
    }
}

$english = $catalogs['en']
$missing = @($keys | Where-Object { -not $english.Has($_) })
if ($missing.Count -gt 0) {
    throw "English (en.json at $commitSha) has no message for $($missing.Count) listed key(s), and English is every language's fallback:`n  $($missing -join "`n  ")"
}

$outputs = [ordered]@{}
foreach ($tag in $tags) {
    $catalog = $catalogs[$tag]
    $absent = @($keys | Where-Object { -not $catalog.Has($_) })
    if ($absent.Count -gt 0) {
        Write-Host "$tag.json lacks $($absent.Count) listed key(s), which show in English: $($absent -join ', ')"
    }
    $outputs[(Join-Path $OutDir "$tag.json")] = [GranaryWebTexts.CatalogJson]::Trim($catalog, $keys)
}
if ($repin) {
    $outputs[$LockPath] = New-LockText -Lock $lock -Commit $commitSha -Hashes $hashes
}

# Writes only what changed, decided for every file first. A checkout may hold the files with CRLF line ends; that alone
# is no change. The lock comes last, so a run that stops halfway leaves the old pin to run again from.
$writes = @($outputs.Keys | Where-Object { -not (Test-SameText -Path $_ -Text $outputs[$_]) })
[void][IO.Directory]::CreateDirectory($OutDir)
$changed = 0
foreach ($path in $writes) {
    [IO.File]::WriteAllText($path, $outputs[$path], $Utf8NoBom)
    Write-Host "Wrote $([IO.Path]::GetFileName($path))"
    $changed++
}
# A language the lock no longer names leaves no catalog behind.
foreach ($file in @(Get-ChildItem -LiteralPath $OutDir -Filter '*.json' -File)) {
    if ($tags -cnotcontains $file.BaseName) {
        Remove-Item -LiteralPath $file.FullName
        Write-Host "Removed $($file.Name): texts.lock.json no longer names $($file.BaseName)."
        $changed++
    }
}

Write-Host "$($keys.Count) keys in $($tags.Count) languages from $($lock.Repository) at $commitSha."
if ($changed -eq 0) {
    Write-Host 'Nothing changed.'
}
