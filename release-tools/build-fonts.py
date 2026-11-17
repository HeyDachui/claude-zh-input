"""Make WPF-compatible static weights from Noto Sans SC's OFL variable font."""
import hashlib, json, sys
from pathlib import Path
from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont
source = Path(sys.argv[1])
output = Path(__file__).resolve().parents[1] / 'src' / 'Fonts'
output.mkdir(exist_ok=True)
records=[]
for weight, style in [(400,'Regular'),(600,'SemiBold')]:
    font=instantiateVariableFont(TTFont(source), {'wght':weight}, inplace=True, updateFontNames=True)
    font.save(output / f'NotoSansSC-{style}.ttf')
    records.append({'weight':weight,'style':style,'family':font['name'].getDebugName(1),
        'sha256':hashlib.sha256((output/f'NotoSansSC-{style}.ttf').read_bytes()).hexdigest()})
(output/'font-build.json').write_text(json.dumps({'sourceSha256':hashlib.sha256(source.read_bytes()).hexdigest(),
    'source':'https://github.com/google/fonts/tree/main/ofl/notosanssc','modification':'static instances at weights 400 and 600; glyph coverage unchanged',
    'fonts':records},ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(records,ensure_ascii=False))
