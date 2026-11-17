"""Exact phrase boundary checks without loading a model or writing user data."""
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from worker import Engine, key


class ModelRequested(Exception):
    pass


class ProbeEngine(Engine):
    def generate(self, *args, **kwargs):
        raise ModelRequested()


class PhraseRoutingTests(unittest.TestCase):
    def setUp(self):
        self.engine = ProbeEngine()

    def tearDown(self):
        self.engine.close()

    def test_complete_phrase_and_single_sentence_stop(self):
        for text in ('继续', ' 继续 ', '继续。', '继续.'):
            with self.subTest(text=text):
                reply = self.engine.translate(text)
                self.assertEqual(reply['text'], 'Continue.')
                self.assertEqual(reply['engine'], '常用指令')

    def test_near_matches_must_reach_the_model(self):
        for text in ('继续...', '继续。。', '继续.。', '继续？', '继续!',
                     '不要继续', '继续，但不要修改代码', '如果失败就不要继续'):
            with self.subTest(text=text):
                with self.assertRaises(ModelRequested):
                    self.engine.translate(text)

    def test_literal_case_and_number_are_not_normalized(self):
        self.assertNotEqual(key('保留 UserID'), key('保留 userid'))
        self.assertNotEqual(key('运行 3 次'), key('运行 30 次'))

    def test_command_passthrough_is_exact(self):
        text = '  /review 中文 UserID  '
        self.assertEqual(self.engine.translate(text)['text'], text)


if __name__ == '__main__':
    unittest.main()
