$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$backup = Join-Path $root 'build/International-20261002/source-before-integration'
$utf8 = [Text.UTF8Encoding]::new($true)
function Edit([string]$path, [scriptblock]$action) {
    $full = Join-Path $root $path
    $old = [IO.File]::ReadAllText($full)
    $saved = Join-Path $backup $path
    if (!(Test-Path -LiteralPath $saved)) { New-Item -ItemType Directory -Force -Path (Split-Path $saved -Parent) | Out-Null; [IO.File]::WriteAllText($saved,$old,$utf8) }
    $next = & $action $old
    if ($null -eq $next) { throw ('Empty edit: ' + $path) }
    [IO.File]::WriteAllText($full,$next,$utf8)
}
function ReplaceExact([string]$text,[string]$old,[string]$new) {
    if (!$text.Contains($old)) { throw ('Missing edit anchor: ' + $old) }
    return $text.Replace($old,$new)
}
$shared = 'Xiongxin.OWTS.Localization.UiLanguage'
$catalogs = @('modules-src/DataBrowser/src/BrowserLanguages.cs','modules-src/DataAnalysis/src/ReviewLanguages.cs','modules-src/ReportGenerator/src/ReportLanguages.cs','modules-src/ProjectManagement/src/ProjectManagementLanguages.cs','modules-src/ProjectCreation/src/ProjectCreationLanguageContract.cs','modules-src/DataBrowser/vendor/Item/UiText.cs')
foreach($path in $catalogs) {
    Edit $path {
        param($t)
        foreach($field in @('Names','Codes','CultureNames')) {
            $pattern = '(?s)(internal static readonly string\[\] '+$field+'\s*=)\s*\{[^}]*\};'
            $t = [regex]::Replace($t,$pattern,('$1 '+$shared+'.'+$field+';'))
        }
        # Resolve the full regional locale before its neutral prefix.
        $pattern = '(?s)(internal static int (?:FindCode|IndexOfCode)\(string code\)\s*)\{.*?\n        \}'
        $t = [regex]::Replace($t,$pattern,('$1{ return '+$shared+'.FindCode(code); }'))
        return $t
    }
}
Edit 'src/Localization.cs' {
    param($t)
    $t = [regex]::Replace($t,'(?s)public static readonly LanguageItem\[\] All =\s*\{.*?\n        \};',@'
public static readonly LanguageItem[] All = BuildItems();
        static LanguageItem[] BuildItems()
        {
            var items = new LanguageItem[UiLanguage.Codes.Length];
            for (int i = 0; i < items.Length; i++) items[i] = new LanguageItem(UiLanguage.Codes[i], UiLanguage.Names[i]);
            return items;
        }
'@)
    $t = ReplaceExact $t 'if (IndexOf(preference) != 18) return;' 'if (IndexOf(preference) == 0) return;'
    $t = ReplaceExact $t 'item.Text = UiLanguage.ToTraditional(item.Text);' 'item.Text = UiLanguage.BuiltIn(IndexOf(preference), item.Text);'
    $t = $t.Replace('"Pressure test"','"Voltage test"').Replace('"Oscillating-wave PD diagnostic system"','"DAC partial discharge diagnostic system"')
    return $t
}
Edit 'modules-src/DataBrowser/src/BrowserLanguages.cs' {
    param($t)
    $t = ReplaceExact $t 'if (index == 1) return english;' ('string localized; if ('+$shared+'.TryTranslate(index, english, out localized)) return localized;'+"`r`n            index = "+$shared+'.LegacyIndex(index);'+"`r`n            if (index == 1) return "+$shared+'.Translate(index, english);')
    return $t
}
Edit 'modules-src/ProjectManagement/src/ProjectManagementLanguages.cs' {
    param($t)
    $t = ReplaceExact $t 'if (language == 1 || string.IsNullOrEmpty(english)) return english;' ('string localized; if ('+$shared+'.TryTranslate(language, english, out localized)) return localized;'+"`r`n            language = "+$shared+'.LegacyIndex(language);'+"`r`n            if (language == 1 || string.IsNullOrEmpty(english)) return "+$shared+'.Translate(language, english);')
    return $t
}
Edit 'modules-src/DataAnalysis/src/ReviewLanguages.cs' {
    param($t)
    $t = ReplaceExact $t 'if(language<2||language==18||String.IsNullOrEmpty(text))return text;' ('string localized;if(language!=0&&language!=18&&'+$shared+'.TryTranslate(language,text,out localized))return localized;'+"`r`n            language="+$shared+'.LegacyIndex(language);'+"`r`n            if(language<2||language==18||String.IsNullOrEmpty(text))return text;")
    return $t
}
Edit 'modules-src/ReportGenerator/src/ReportLanguages.cs' {
    param($t)
    $t = ReplaceExact $t 'if (languageIndex == 1) return english;' ('string localized; if ('+$shared+'.TryTranslate(languageIndex, english, out localized)) return localized;'+"`r`n            languageIndex = "+$shared+'.LegacyIndex(languageIndex);'+"`r`n            if (languageIndex == 1) return "+$shared+'.Translate(languageIndex, english);')
    $t = ReplaceExact $t 'if (languageIndex == 1) return english ?? String.Empty;' ('string localized; if ('+$shared+'.TryTranslate(languageIndex, english, out localized)) return localized;'+"`r`n            languageIndex = "+$shared+'.LegacyIndex(languageIndex);'+"`r`n            if (languageIndex == 1) return "+$shared+'.Translate(languageIndex, english) ?? String.Empty;')
    return $t
}
Edit 'modules-src/ProjectCreation/src/ProjectCreationLanguageContract.cs' {
    param($t)
    $t = ReplaceExact $t 'if (values.Length != Names.Length)' 'if (values.Length != 19)'
    $t = ReplaceExact $t 'if (language == 1) return english ?? String.Empty;' ('string localized; if (UiLanguage.TryTranslate(language, english, out localized)) return localized;'+"`r`n            language = UiLanguage.LegacyIndex(language);`r`n            if (language == 1) return UiLanguage.Translate(language, english) ?? String.Empty;")
    $t = ReplaceExact $t 'if (language == 0) return canonical ?? String.Empty;' ('if (language == 0) return canonical ?? String.Empty;'+"`r`n            string localized; if (language != 18 && UiLanguage.TryTranslate(language, english, out localized)) return localized;`r`n            language = UiLanguage.LegacyIndex(language);")
    return $t
}
Edit 'modules-src/DataBrowser/vendor/Item/UiText.cs' {
    param($t)
    $t = ReplaceExact $t 'return values[languageId];' ('string localized;if('+$shared+'.TryTranslate(languageId,values[1],out localized))return localized;'+"`r`n   return values["+$shared+'.LegacyIndex(languageId)];')
    return $t
}
Edit 'modules-src/DataBrowser/vendor/Item/ItemInformationControl.cs' { param($t) return $t.Replace('languageId > 18',('languageId >= '+$shared+'.Codes.Length')) }
Edit 'src/WithstandTestControl.cs' { param($t) return $t.Replace('value>18',('value>='+$shared+'.Codes.Length')) }
Edit 'modules-src/CalibrationTest/src/CalibrationProgramControl.Language.cs' { param($t) return ReplaceExact $t 'return drawingLanguage == 18 ? UiLanguage.ToTraditional(text) : text;' 'return UiLanguage.BuiltIn(drawingLanguage, text);' }
Edit 'modules-src/PressureTest/src/PressureBuiltInLanguage.cs' {
    param($t)
    return ReplaceExact $t 'sidebarEnglish.TryGetValue(entry.Value,out english)?english:entry.Value' ('sidebarEnglish.TryGetValue(entry.Value,out english)?'+$shared+'.Text(LanguageIndex,entry.Value,english):'+$shared+'.BuiltIn(LanguageIndex,entry.Value)')
}
Edit 'src/HelpForm.cs' { param($t) return $t.Replace('Pressure test','Voltage test').Replace('pressure testing','voltage testing') }
Edit 'tools/Build-UiModules.ps1' {
    param($t)
    return ReplaceExact $t "(Join-Path `$projectRoot 'src\UiLanguage.cs')," "(Join-Path `$projectRoot 'src\UiLanguage.cs'),(Join-Path `$projectRoot 'src\InternationalText.cs'),(Join-Path `$projectRoot 'src\InternationalText.Generated.cs'),"
}
Edit 'tools/Test-Internal12-Calibration.ps1' {
    param($t)
    return $t.Replace("'src\UiLanguage.cs'", "'src\UiLanguage.cs','src\InternationalText.cs','src\InternationalText.Generated.cs'")
}
Edit 'tests/LanguagePreferenceSmoke.cs' {
    param($t)
    return $t.Replace('Length == 20, "System plus 19 languages"','Length == 41, "System plus 40 locales"').Replace('"ja-JP|en"','"ja-JP|ja"')
}
Write-Output 'International integration applied; original touched files preserved under build/International-20261002/source-before-integration.'
