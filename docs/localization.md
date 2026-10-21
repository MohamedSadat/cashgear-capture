# Localization

CashGear Capture is in English and Arabic. Arabic is laid out right to left. The language follows Windows' display language unless Settings › General › Language says otherwise. A change applies when the app starts again, and Settings offers to restart.

## Where the text lives

All text a user can see is in `.resx` files, never in code or XAML:

| Project | Files |
|---|---|
| App | `src/CashGear.Capture.App/Resources/<Area>Text.resx`: `CommonText` (OK, Cancel, units…), `AppText` (start-up, errors, restarting), `TrayText`, `CapturingText`, `SettingsText`, `EditorText`, `ImageToolsText`, `VideoEditorText`, `RecordingText`, `HistoryText`, `ToolsText`, `UpdatesText`, `DiagnosticsText`… |
| Libraries | `src/CashGear.Capture.<Project>/Resources/<Project>Strings.resx` (`CoreStrings`, `MediaStrings`, `RecordingStrings`…): a different ending from the App's, so the names never clash |

Each has an Arabic translation next to it: `TrayText.ar.resx`.

The build turns every `.resx` into a class (`src/CashGear.Capture.Analyzers`, `ResxGenerator`) in the folder's namespace:

```csharp
using CashGear.Capture.App.Resources;

TrayText.CaptureRegion                // a string
TrayText.UpdateReady(version)         // "Version {0} is ready": one argument per placeholder, checked by the compiler
CommonText.Seconds(count)             // plural forms: picks "_one"/"_other", in Arabic also "_zero", "_two", "_few", "_many"
```

In XAML:

```xml
xmlns:r="clr-namespace:CashGear.Capture.App.Resources"
<MenuItem Header="{x:Static r:TrayText.CaptureRegion}" />
<TextBlock Text="{Binding Delay, StringFormat={x:Static r:CommonText.SecondsFormat}}" />
```

## The checks

The build reports these as warnings, and the build must have none:

| Id | What |
|---|---|
| CG0001 | C# text a user would see: a phrase (two words) anywhere, or any word passed to the UI (WPF text properties, MessageBox, file dialogs, `[Localizable(true)]` parameters such as `ITrayNotifier.Notify`, members named `…Text`, `…Title`, `…Message`, `…Label`, `…Status`, `…Tip`…) |
| CG0002 | Literal text in XAML: `Text`, `Content`, `Header`, `ToolTip`, `Title`, `AutomationProperties.Name`/`HelpText`, text between tags |
| CG0003 | A `.resx` key that isn't a C# identifier |
| CG0004 | A `.resx` file that can't be read, or a stray `{` or `}` (write `{{` or `}}`) |
| CG0005 | A string with no Arabic translation, or a plural form missing |
| CG0006 | A translation whose `{0}` placeholders differ from the original's. A plural form may leave out `{0}` (the count): «ملف واحد» |
| CG0007 | A translation of a key the original doesn't have |

Text that never reaches a user stays in code. Mark it so the check knows: `[Localizable(false)]` on the member, type or parameter. This covers:
- log message templates (attributes, never reported);
- the diagnostics zip's README;
- settings and file-format keys;
- FFmpeg options;
- shader code.

The messages of .NET's own exceptions (`ArgumentException`, `InvalidOperationException`…) are for developers and aren't reported. Text meant for users goes in our own exceptions, whose message parameter is `[Localizable(true)]`.

## Writing strings

- **Keys.** PascalCase, saying what the text is: `CaptureRegion`, `SavedTitle`, `CopyLinkTip`. Plural strings are one key with forms (`Files_one`, `Files_other`).
- **English.** Keep the existing wording: tests compare it. Write whole sentences with placeholders, never sentences glued together from pieces, since word order differs between languages: `"{0} saved"`, not `what + " saved"`.
- **Plurals.** Any text with a count uses a plural key: Arabic has six forms (`zero`, `one`, `two`, `few` for 3–10, `many` for 11–99, `other`). Arabic forms usually leave the number out for 1 and 2: «لقطة واحدة», «لقطتان».
- **Access keys.** English menus and buttons keep their `_` letter. In Arabic the same Latin letter goes in parentheses at the end: `التقاط منطقة (_R)`. It works on an English keyboard layout, and the underline never breaks the joining of Arabic letters.
- **Kept in English or Latin script:**
  - key names in hotkeys and keystroke captions (`Ctrl+Shift+PrintScreen`): they match the keycaps;
  - product, format and technology names: CashGear Capture, Windows, PNG, MP4, H.264, FFmpeg, Whisper;
  - technical units: px, ms, fps, pt, dB, Hz, %, KB, MB, GB;
  - file-name tokens (`{yyyy}`, `{kind}` and their values);
  - command-line switches;
  - log messages and the diagnostics zip.
- **Seconds and minutes** are translated: `{0} s` → `{0} ث`, `{0} min` → `{0} د`.
- **Arabic punctuation:** `،` `؛` `؟`. Use «» around names in Arabic sentences, and keep the `…` of menu items that open a window.
- **Style.** Modern Standard Arabic in the style of Windows' Arabic text. Commands and menu items are verbal nouns (التقاط، حفظ، فتح، تصدير); messages are short full sentences.

## Glossary

| English | Arabic |
|---|---|
| CashGear Capture | CashGear Capture |
| capture (verb) | التقاط |
| capture (the picture), screenshot | لقطة، لقطة شاشة |
| region / window / full screen / active window | منطقة / نافذة / الشاشة كاملة / النافذة النشطة |
| scrolling capture | التقاط مع التمرير |
| fixed-size region / freehand / polygon / rounded | منطقة بحجم ثابت / منطقة حرة / مضلع / بزوايا مستديرة |
| object (part of a window) / open menu | عنصر (جزء من نافذة) / قائمة مفتوحة |
| repeat the last capture | تكرار آخر التقاط |
| capture toolbar / tools | شريط أدوات الالتقاط / الأدوات |
| magnifier / colour picker / screen ruler / crosshair | المكبّر / منتقي الألوان / مسطرة الشاشة / خطّا التقاطع |
| batch process / combine images / images to PDF | معالجة دفعية / دمج الصور / الصور إلى PDF |
| delay / no delay | تأخير / بلا تأخير |
| record, recording / screen recording | تسجيل / تسجيل الشاشة |
| pause / resume / stop | إيقاف مؤقت / استئناف / إيقاف |
| overlays (clicks, keystrokes, webcam) | التراكبات |
| video editor / project / timeline / clip / track | محرر الفيديو / مشروع / المخطط الزمني / مقطع / مسار |
| export / import | تصدير / استيراد |
| capture history / recent captures | سجل اللقطات / اللقطات الأخيرة |
| settings / general / appearance / theme / language | الإعدادات / عام / المظهر / السمة / اللغة |
| output / destination / workflow | الإخراج / الوجهة / سير العمل |
| hotkey / keyboard shortcut | مفتاح اختصار / اختصار لوحة المفاتيح |
| notification area (tray) | منطقة الإعلامات |
| update / restart to update | تحديث / إعادة التشغيل للتحديث |
| stable / beta (update channel) | المستقر / التجريبي |
| release notes / what's new | ملاحظات الإصدار / ما الجديد |
| diagnostics | معلومات التشخيص |
| crash report / privacy | تقرير الأعطال / الخصوصية |
| portable | محمول |
| licence | الترخيص |
| image editor / annotation | محرر الصور / تعليق توضيحي |
| arrow / line / rectangle / ellipse / text / highlight | سهم / خط / مستطيل / شكل بيضاوي / نص / تمييز |
| blur / pixelate | تمويه / تقطيع إلى بكسلات |
| crop / resize / rotate / flip | اقتصاص / تغيير الحجم / تدوير / قلب |
| effect / preset / watermark | تأثير / إعداد مسبق / علامة مائية |
| undo / redo | تراجع / إعادة |
| copy / paste / delete | نسخ / لصق / حذف |
| open / save / save as / close | فتح / حفظ / حفظ باسم / إغلاق |
| OK / Cancel / Apply / Yes / No | موافق / إلغاء / تطبيق / نعم / لا |
| browse | استعراض |
| clipboard / file / folder / printer | الحافظة / ملف / مجلد / الطابعة |
| microphone / system audio / webcam | الميكروفون / صوت النظام / كاميرا الويب |
| noise suppression | منع الضوضاء |
| captions / transcribe | التسميات التوضيحية / تحويل الكلام إلى نص |
| encoder / quality / frames per second | المرمّز / الجودة / إطارات في الثانية |
| upload / server / link | رفع / خادم / رابط |
| exit | خروج |

## Adding a language

1. Add `<Name>.<culture>.resx` next to each `.resx`. The build then reports every missing string (CG0005).
2. Add the language to `UiLanguage` (Core), to `AppLanguage.Resolve` and `AppLanguage.Languages` (App), and to the plural rules in `ResxGenerator.PluralForm` and `RequiredForms` if it has more than one and other.
3. A right-to-left language needs nothing more: windows follow the culture's direction.

`tools/strings.py add spec.json` adds strings to an English and Arabic pair of files from a JSON list, which avoids shell quoting.
