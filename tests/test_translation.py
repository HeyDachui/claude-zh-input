from pathlib import Path
import json
import socket
import sys
import time
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from worker import Engine


class TranslationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.engine = Engine()
        cls.evidence = []
        cls.original_connect = socket.socket.connect
        socket.socket.connect = lambda *args, **kwargs: (_ for _ in ()).throw(AssertionError('Network is not permitted during offline translation'))

    @classmethod
    def tearDownClass(cls):
        socket.socket.connect = cls.original_connect
        (ROOT/'artifacts/translation-tests.json').write_text(json.dumps(cls.evidence, ensure_ascii=False, indent=2), encoding='utf-8')

    def translate(self, source):
        start=time.perf_counter()
        res=self.engine.translate(source)
        self.evidence.append({'source':source, **res, 'wall_seconds':round(time.perf_counter()-start,3)})
        self.assertNotIn('▁', res['text'])
        return res

    def test_permissions_and_order(self):
        result=self.translate('先检查登录失败的原因，不要修改代码，找到后用中文告诉我。')['text']
        self.assertIn('Do not modify the code', result)
        self.assertIn('Chinese', result)
        self.assertIn('First investigate', result)

    def test_confirmation(self):
        result=self.translate('请先给我三个方案，等我确认后再执行。')['text']
        self.assertIn('three options', result)
        self.assertIn('Wait for my confirmation before taking action', result)

    def test_arbitrary_code_and_path(self):
        result=self.translate('把 `src/登录.ts` 里的 `timeoutMs` 改成 30 秒，保持 `userID` 不变。')['text']
        for literal in ['`src/登录.ts`','`timeoutMs`','`userID`','30']:
            self.assertIn(literal,result)
        self.assertIn('Change `timeoutMs` in `src/登录.ts` to 30',result)
        self.assertIn('Keep `userID` unchanged',result)
        self.assertNotRegex(result, r'MARKER\d+')

    def test_windows_path(self):
        result=self.translate(r'请检查 "E:\项目 甲\文件.ts" 是否存在，不要删除任何文件。')['text']
        self.assertIn(r'"E:\项目 甲\文件.ts"',result)
        self.assertIn('Do not delete any files',result)

    def test_fenced_code_is_verbatim(self):
        code='```python\nprint("中文")\n```'
        result=self.translate('解释这段代码：\n'+code)['text']
        self.assertIn(code,result)

    def test_command_is_passthrough(self):
        source='/compact 保留当前目标和约束'
        self.assertEqual(self.translate(source)['text'],source)

    def test_english_is_passthrough(self):
        source='Do not change USER_ID or src/User.ts.'
        self.assertEqual(self.translate(source)['text'],source)

    def test_english_preserves_case_and_whitespace(self):
        source='  OpenAI userID API_v2 [0] + value  '
        self.assertEqual(self.translate(source)['text'],source)

    def test_mixed_brand_and_identifier(self):
        result=self.translate('请让 OpenAI 检查 userID 和 API_v2，不要修改 README.md。')['text']
        for literal in ['OpenAI','userID','API_v2','README.md']:
            self.assertIn(literal,result)

    def test_cache_keeps_original(self):
        source='请检查新建用户时出现的数据库连接错误。'
        result=self.translate(source)
        repeated=self.translate(source)
        self.assertEqual(result['text'],repeated['text'])
        self.assertEqual(repeated['engine'],'本次会话缓存')

    def test_rejects_over_limit(self):
        with self.assertRaisesRegex(ValueError,'5000'):
            self.engine.translate('长'*5001)

    def test_fallback_literal_preservation(self):
        engine=Engine()
        engine.raw=lambda parts: ['translated fragment' for _ in parts]
        result=engine.translate('修改 `abc.ts` 中的 `UserID`。')
        self.assertIn('`abc.ts`',result['text'])
        self.assertIn('`UserID`',result['text'])
        self.assertTrue(result['warnings'])


if __name__=='__main__':
    unittest.main(verbosity=2)
