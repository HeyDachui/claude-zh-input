import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from worker import Engine, key


class PhraseDictionaryTests(unittest.TestCase):
    def test_reviewed_dictionary_returns_exact_bytes_without_model(self):
        engine = Engine()
        try:
            for phrase in json.loads((ROOT / 'phrases.json').read_text(encoding='utf-8-sig')):
                with self.subTest(zh=phrase['zh']):
                    self.assertEqual(phrase['en'], engine.translate(phrase['zh'])['text'])
            self.assertIsNone(engine.process)
        finally:
            engine.close()

    def test_dictionary_punctuation_is_not_normalized(self):
        engine = Engine()
        try:
            for output in ['Why?', 'Stop!', 'Wait...', 'No punctuation', 'Continue.']:
                engine.phrases = {key('测试句'): output}
                self.assertEqual(output, engine.translate('测试句')['text'])
            self.assertIsNone(engine.process)
        finally:
            engine.close()


if __name__ == '__main__':
    unittest.main()
