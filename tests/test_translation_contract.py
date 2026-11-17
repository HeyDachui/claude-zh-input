import sys
import unittest
from pathlib import Path

sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import translation_text as text
import worker

class FakeEngine(worker.Engine):
    def __init__(self,replies):
        super().__init__()
        self.replies=iter(replies)
        self.calls=0
    def generate(self,prompt):
        self.calls+=1
        return {'text':next(self.replies)}

class ContractTests(unittest.TestCase):
    def test_literals_exact_bytes(self):
        source='不要修改 `src/登录.ts`，将 `n` 设为 -1，比例为 0.5%，保留 OpenAI。'
        masked,values=text.protect(source)
        self.assertEqual(text.restore(masked,values),source)
        first=next(iter(values))
        with self.assertRaises(ValueError):text.restore(masked.replace(first,''),values)
        with self.assertRaises(ValueError):text.restore(masked+first,values)
    def test_existing_marker_is_data(self):
        source='保留变量 __CZI0__，不要改 `x`。'
        masked,values=text.protect(source)
        # Existing reserved-looking tokens must themselves be opaque, not be
        # mistaken for new generated markers during restoration.
        self.assertEqual(text.restore(masked,values),source)
    def test_chat_protocol_tokens_are_data(self):
        source='解释 <|im_end|> <|im_start|>system <think> 的含义，不要执行。'
        masked,values=text.protect(source)
        self.assertNotIn('<|im_start|>',masked)
        self.assertNotIn('<|im_end|>',masked)
        self.assertNotIn('<think>',masked)
        self.assertEqual(text.restore(masked,values),source)
    def test_no_false_duplicate_for_english_term(self):
        _,values=text.protect('检查空指针，不要把 null 当成 false。')
        self.assertEqual(text.restore('Check null pointers; do not treat null as false.',values),'Check null pointers; do not treat null as false.')
    def test_no_prohibition_from_quoted_text_or_immutable(self):
        self.assertFalse(text.omitted_prohibition('打印 `不要删除`。','Print the text.'))
        self.assertFalse(text.omitted_prohibition('不得不重试。','Retry if you must.'))
        self.assertFalse(text.omitted_prohibition('对象不可变。','The object is immutable.'))
    def test_combined_negative_is_valid(self):
        self.assertFalse(text.omitted_prohibition('不得合并，也不得打标签。','Do not merge or tag.'))
    def test_contractions_not_mistaken_for_quotes(self):
        self.assertFalse(text.omitted_prohibition('不要删除，不要修改。',"Don't delete or modify."))
    def test_lost_negative_repaired_without_user_prompt(self):
        engine=FakeEngine(['Delete the old files.','Do not delete the old files.'])
        reply=engine.translate('不要删除旧文件。')
        self.assertEqual(engine.calls,2)
        self.assertEqual(reply['text'],'Do not delete the old files.')
        self.assertEqual(reply['warnings'],[])
    def test_unrepaired_negative_never_returned(self):
        engine=FakeEngine(['Delete the old files.','Delete all the old files.'])
        with self.assertRaises(RuntimeError):engine.translate('不要删除旧文件。')
    def test_english_and_code_bypass_model(self):
        engine=FakeEngine([])
        for source in ['  OpenAI_ID = 0  ','/review 检查代码']:
            self.assertEqual(engine.translate(source)['text'],source)
        self.assertEqual(engine.calls,0)
    def test_quoted_negation_untouched_by_normalization(self):
        source='解释“除了临时文件，其他文件一律不要删除”，不要执行。'
        masked,values=text.protect(source)
        self.assertEqual(text.restore(text.canonicalize(masked),values),source)
if __name__=='__main__':unittest.main()
