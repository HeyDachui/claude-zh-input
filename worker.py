"""Offline whole-sentence translation through a private CPU inference process."""
from __future__ import annotations

from collections import OrderedDict
from pathlib import Path
import json
import os
import re
import subprocess
import sys
import time
import ctypes

import translation_text

ROOT = Path(__file__).resolve().parent
os.chdir(ROOT)
MODEL = Path('models/translation-llm/model.gguf')
HAN = translation_text.HAN
LITERAL = translation_text.LITERAL

def key(text: str) -> str:
    text = text.strip()
    if text.endswith(('。', '.')) and (len(text) == 1 or text[-2] not in '。.'): 
        return text[:-1].strip()
    return text

def load_phrases() -> dict[str, str]:
    data = json.loads((ROOT / 'phrases.json').read_text(encoding='utf-8-sig'))
    return {key(p['zh']): p['en'] for p in data}

class Engine:
    def __init__(self, runtime: Path | None = None, model: Path | None = None):
        self.runtime = runtime or ROOT / 'translation-runtime'
        self.model = model or ROOT / MODEL
        self.process = None
        self.phrases = load_phrases()
        self.cache: OrderedDict[str, dict] = OrderedDict()
        self.cancel_handle = None
        name = os.environ.get('CZI_TRANSLATION_CANCEL_EVENT')
        if name:
            self.kernel = ctypes.WinDLL('kernel32', use_last_error=True)
            self.kernel.OpenEventW.argtypes = [ctypes.c_ulong, ctypes.c_bool, ctypes.c_wchar_p]
            self.kernel.OpenEventW.restype = ctypes.c_void_p
            self.kernel.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
            self.kernel.CloseHandle.argtypes = [ctypes.c_void_p]
            self.cancel_handle = self.kernel.OpenEventW(0x00100000, False, name)
            if not self.cancel_handle:
                raise RuntimeError('cancel_channel_unavailable')

    def check_cancelled(self):
        if self.cancel_handle and self.kernel.WaitForSingleObject(self.cancel_handle, 0) == 0:
            raise InterruptedError('cancelled')

    def prepare(self):
        # Only the fixed public instruction prefix is prefetched, no user text.
        return self.generate(translation_text.prompt('', translation_text.SYSTEM), op='prepare')

    def close(self):
        if self.process is None:
            return
        process, self.process = self.process, None
        try:
            if process.stdin:
                process.stdin.close()
            process.wait(timeout=1)
        except (OSError, subprocess.TimeoutExpired):
            process.kill()
            process.wait(timeout=5)
        finally:
            if process.stdout:
                process.stdout.close()

    def generate(self, prompt: str, op: str = 'translate') -> dict:
        self.check_cancelled()
        if self.process is None or self.process.poll() is not None:
            self.close()
            executable = self.runtime / 'ClaudeZhInput.Translate.exe'
            if not executable.is_file() or not self.model.is_file():
                raise RuntimeError('本地翻译组件缺失，请重新运行完整安装包。')
            self.process = subprocess.Popen([str(executable), str(self.model.resolve())],
                cwd=self.runtime, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                text=True, encoding='utf-8', bufsize=1, creationflags=subprocess.CREATE_NO_WINDOW)
        self.process.stdin.write(json.dumps({'prompt': prompt, 'max_tokens': 768, 'op': op}, ensure_ascii=False) + '\n')
        self.process.stdin.flush()
        line = self.process.stdout.readline(131072)
        if not line or len(line) >= 131072:
            self.close()
            raise RuntimeError('本地翻译进程未完成本次转换。')
        result = json.loads(line)
        self.check_cancelled()
        if result.get('error') == 'cancelled':
            raise InterruptedError('cancelled')
        if 'error' in result:
            raise RuntimeError('本地翻译未完成，原文仍可保留。')
        return result

    def translate(self, source: str) -> dict:
        self.check_cancelled()
        started = time.perf_counter()
        text = source.strip()
        if not text:
            raise ValueError('请先输入中文要求。')
        if len(text) > 5000:
            raise ValueError('一次最多翻译 5000 个字符，请分段处理。')
        if not HAN.search(text) or re.match(r'^/[A-Za-z][\w-]*(?:\s|$)', text):
            return dict(text=source, engine='原样保留', seconds=0, warnings=[])
        if text in self.cache:
            result = dict(self.cache[text])
            result.update(engine='本次会话缓存', seconds=round(time.perf_counter()-started, 3))
            self.cache.move_to_end(text)
            return result
        if key(text) in self.phrases:
            return dict(text=self.phrases[key(text)], engine='常用指令', seconds=0, warnings=[])
        masked, literals = translation_text.protect(text)
        masked = translation_text.canonicalize(masked)
        output = None
        for attempt in range(2):
            self.check_cancelled()
            system = translation_text.SYSTEM
            if attempt:
                system += '\nRe-translate carefully. Render EVERY prohibition explicitly with do not, must not, cannot, never, or an equivalent negative. Preserve the scope of exceptions: a prohibition outside an exception must remain a prohibition, not a command to perform the action. Copy every supplied opaque token exactly once and keep original identifiers and URLs verbatim.'
            reply = self.generate(translation_text.prompt(masked, system))
            draft = reply.get('text', '').strip()
            if not draft or HAN.search(draft):
                continue
            if translation_text.omitted_prohibition(masked, draft):
                continue
            try:
                output = translation_text.restore(draft, literals)
                break
            except ValueError:
                continue
        if output is None:
            # No partial output or silently worse model fallback. The coordinator
            # preserves the source on an actual conversion failure.
            raise RuntimeError('本次转换未能完整保留文字与代码，原文仍可保留。')
        self.check_cancelled()
        result = dict(text=output, engine='本地整句翻译', seconds=round(time.perf_counter()-started, 3), warnings=[])
        self.cache[text] = result
        if len(self.cache) > 128:
            self.cache.popitem(last=False)
        return result

def main():
    for stream in (sys.stdin, sys.stdout, sys.stderr):
        if hasattr(stream, 'reconfigure'):
            stream.reconfigure(encoding='utf-8')
    engine = Engine()
    try:
        for line in sys.stdin:
            try:
                request = json.loads(line)
                result = engine.prepare() if request.get('op') == 'prepare' else engine.translate(request['text'])
            except InterruptedError:
                result = {'cancelled': True}
            except Exception:
                # No original text, model output or personal paths in errors.
                result = {'error': '本地翻译未完成，原文仍可保留。'}
            print(json.dumps(result, ensure_ascii=False), flush=True)
    finally:
        engine.close()

if __name__ == '__main__':
    main()
