"""Adds strings to a pair of .resx files (English and Arabic) from a JSON spec.

    python tools/strings.py add spec.json [spec2.json ...]
    python tools/strings.py list src/CashGear.Capture.App/Resources/TrayText

A spec is one JSON object, or a list of them:

    {
      "file": "src/CashGear.Capture.App/Resources/TrayText",   # without .resx; made if missing
      "strings": [
        {"key": "CaptureRegion", "en": "Capture _Region", "ar": "التقاط منطقة (_R)", "comment": "tray menu"}
      ]
    }

A key that is already there gets the new text. Plural strings are keys ending in _one/_other in English and
_zero/_one/_two/_few/_many/_other in Arabic: give "en" and "ar" as objects, e.g.
{"key": "Files", "en": {"one": "{0} file", "other": "{0} files"}, "ar": {"zero": "...", ...}}.
See docs/localization.md for the rules and the glossary.
"""

import json
import os
import sys
import xml.etree.ElementTree as ET
from xml.sax.saxutils import escape

HEADER = """<?xml version="1.0" encoding="utf-8"?>
<root>
  <resheader name="resmimetype">
    <value>text/microsoft-resx</value>
  </resheader>
  <resheader name="version">
    <value>2.0</value>
  </resheader>
  <resheader name="reader">
    <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
  <resheader name="writer">
    <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
"""

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def read(path):
    """The entries of a .resx file, in order: list of [key, value, comment]."""
    if not os.path.exists(path):
        return []
    tree = ET.parse(path)
    entries = []
    for data in tree.getroot().findall('data'):
        if data.get('type') or data.get('mimetype'):
            continue
        value = data.find('value')
        comment = data.find('comment')
        entries.append([data.get('name'), (value.text or '') if value is not None else '', comment.text if comment is not None else None])
    return entries


def write(path, entries):
    lines = [HEADER]
    for key, value, comment in entries:
        lines.append('  <data name="%s" xml:space="preserve">\n' % escape(key, {'"': '&quot;'}))
        # Line breaks inside a value are written as character references: as raw text they would follow the file's
        # line endings (CRLF here, or after a git checkout), and the string would change with them.
        text = escape(value).replace('\r\n', '\n').replace('\r', '\n').replace('\n', '&#10;')
        lines.append('    <value>%s</value>\n' % text)
        if comment:
            lines.append('    <comment>%s</comment>\n' % escape(comment))
        lines.append('  </data>\n')
    lines.append('</root>\n')
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w', encoding='utf-8', newline='\r\n') as f:
        f.write(''.join(lines))


def put(entries, key, value, comment):
    for entry in entries:
        if entry[0] == key:
            entry[1] = value
            if comment is not None:
                entry[2] = comment
            return
    entries.append([key, value, comment])


def expand(key, text):
    """A plain string, or a plural object {"one": ..., "other": ...} as Key_one, Key_other."""
    if isinstance(text, dict):
        return [(key + '_' + form, value) for form, value in text.items()]
    return [(key, text)]


def add(spec_path):
    with open(spec_path, encoding='utf-8') as f:
        specs = json.load(f)
    if isinstance(specs, dict):
        specs = [specs]
    for spec in specs:
        base = os.path.join(ROOT, spec['file'].replace('/', os.sep))
        if base.endswith('.resx'):
            base = base[:-5]
        english = read(base + '.resx')
        arabic = read(base + '.ar.resx')
        for item in spec['strings']:
            comment = item.get('comment')
            for key, value in expand(item['key'], item['en']):
                put(english, key, value, comment)
            for key, value in expand(item['key'], item['ar']):
                put(arabic, key, value, None)
        write(base + '.resx', english)
        write(base + '.ar.resx', arabic)
        print('%s: %d strings (%d in Arabic)' % (os.path.relpath(base, ROOT), len(english), len(arabic)))


def show(base):
    base = os.path.join(ROOT, base.replace('/', os.sep))
    if base.endswith('.resx'):
        base = base[:-5]
    arabic = {k: v for k, v, _ in read(base + '.ar.resx')}
    for key, value, _ in read(base + '.resx'):
        print('%s\n  en: %s\n  ar: %s' % (key, value, arabic.get(key, '(missing)')))


if __name__ == '__main__':
    if sys.stdout.encoding.lower() != 'utf-8':
        sys.stdout.reconfigure(encoding='utf-8')
    if len(sys.argv) >= 3 and sys.argv[1] == 'add':
        for spec in sys.argv[2:]:
            add(spec)
    elif len(sys.argv) >= 2 and sys.argv[1] == 'normalize':
        # Rewrites resx files (the given ones, or every one under src), so line breaks in values become &#10;.
        targets = sys.argv[2:] or [os.path.relpath(os.path.join(d, f), ROOT)
                                   for d, _, files in os.walk(os.path.join(ROOT, 'src'))
                                   if os.sep + 'bin' not in d and os.sep + 'obj' not in d
                                   for f in files if f.endswith('.resx')]
        for target in targets:
            path = os.path.join(ROOT, target)
            write(path, read(path))
            print('normalized ' + path)
    elif len(sys.argv) == 3 and sys.argv[1] == 'list':
        show(sys.argv[2])
    else:
        print(__doc__)
        sys.exit(1)
