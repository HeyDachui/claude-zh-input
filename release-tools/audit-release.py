"""Inspect the actual public package and optional ZIP without launching the IME."""
import argparse
import hashlib
import json
import re
import struct
import zipfile
from pathlib import Path


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def pe_info(path):
    data = path.read_bytes()
    assert data[:2] == b'MZ', f'Not a PE image: {path.name}'
    pe = struct.unpack_from('<I', data, 0x3C)[0]
    assert data[pe:pe + 4] == b'PE\0\0', path.name
    machine, count = struct.unpack_from('<HH', data, pe + 4)
    opt = pe + 24
    magic = struct.unpack_from('<H', data, opt)[0]
    dirs = opt + (112 if magic == 0x20B else 96)
    section_start = opt + struct.unpack_from('<H', data, pe + 20)[0]
    sections = [struct.unpack_from('<IIII', data, section_start + 40 * n + 8)
                for n in range(count)]

    def offset(rva):
        for virtual_size, start, raw_size, raw in sections:
            if start <= rva < start + max(virtual_size, raw_size):
                return raw + rva - start
        if rva < section_start:
            return rva
        raise ValueError(f'Invalid RVA {rva:x} in {path.name}')

    def string(rva):
        start = offset(rva)
        return data[start:data.index(b'\0', start)].decode('utf8', errors='replace')

    def directory(index):
        return struct.unpack_from('<II', data, dirs + index * 8)

    imports = []
    rva, size = directory(1)
    if rva:
        start = offset(rva)
        for pos in range(start, start + size, 20):
            descriptor = struct.unpack_from('<IIIII', data, pos)
            if not any(descriptor):
                break
            imports.append(string(descriptor[3]))
    rva, size = directory(13)
    if rva:
        start = offset(rva)
        for pos in range(start, start + size, 32):
            descriptor = struct.unpack_from('<IIIIIIII', data, pos)
            if not any(descriptor):
                break
            assert descriptor[0] & 1, 'Delay imports must use RVA'
            imports.append(string(descriptor[1]))
    debug_types = []
    rva, size = directory(6)
    if rva:
        for pos in range(offset(rva), offset(rva) + size, 28):
            debug_types.append(struct.unpack_from('<I', data, pos + 12)[0])
    exports = []
    rva, _ = directory(0)
    if rva:
        pos = offset(rva)
        names_count = struct.unpack_from('<I', data, pos + 24)[0]
        names_rva = struct.unpack_from('<I', data, pos + 32)[0]
        for n in range(names_count):
            exports.append(string(struct.unpack_from('<I', data, offset(names_rva) + n * 4)[0]))
    return {'machine': hex(machine), 'imports': imports, 'debugTypes': debug_types, 'exports': exports}


def audit(root, archive=None, expected_version='1.1.4'):
    root = root.resolve()
    files = {p.relative_to(root).as_posix(): p for p in root.rglob('*') if p.is_file()}
    manifest = {}
    for line in (root / 'RELEASE-MANIFEST.sha256').read_text(encoding='utf-8-sig').splitlines():
        digest, name = re.split(r'\s{2,}', line, maxsplit=1)
        assert re.fullmatch('[0-9A-Fa-f]{64}', digest), name
        assert not name.startswith('/') and '..' not in Path(name).parts, name
        assert name not in manifest, f'Duplicate manifest entry: {name}'
        manifest[name] = digest.upper()
    assert set(manifest) == set(files) - {'RELEASE-MANIFEST.sha256'}, 'Manifest file set differs'
    forbidden = {'.py', '.pyc', '.pyo', '.cs', '.csproj', '.xaml', '.c', '.cc', '.cpp', '.h', '.hpp',
                 '.pdb', '.obj', '.lib', '.map', '.sln', '.vcxproj'}
    private_names = {'phrases.json', 'mapping.txt', 'mapping.xml', '.env', 'preferences.json',
                     'ime-state.json', 'toolbar-position.json', 'behavior-recording.json',
                     'user-dictionary.tsv', 'activity.jsonl'}
    scripts = {'native/install.ps1', 'native/uninstall.ps1', 'setup/install.ps1', 'setup/start.ps1',
               'setup/uninstall.ps1', 'setup/setup-common.ps1', 'setup/combine-model-pack.ps1'}
    binaries = {}
    for name, path in files.items():
        assert path.suffix.lower() not in forbidden, f'Development file: {name}'
        assert path.name.lower() not in private_names, f'Private data: {name}'
        assert not {'.git', 'staging', 'behavior-log', 'install-log', 'user_data'}.intersection(path.parts), name
        if path.suffix.lower() == '.ps1':
            assert name in scripts, f'Unexpected script: {name}'
        if name in manifest:
            assert sha(path) == manifest[name], f'Hash mismatch: {name}'
        if path.suffix.lower() in {'.dll', '.exe', '.pyd'}:
            info = pe_info(path)
            assert not any(re.search(r'cudnn|cublas|nvcuda|libiomp', dll, re.I) for dll in info['imports']), name
            binaries[name] = info
        if path.suffix.lower() in {'.txt', '.md', '.json', '.xml', '.yaml', '.yml', '.ps1', '.cmd'}:
            text = path.read_bytes().decode('utf8', errors='replace')
            assert not re.search(r'C:[\\/]+Users[\\/]+Administrator|E:[\\/]+临时沙箱', text, re.I), name
    native = binaries['native/ClaudeZhInput.NativeCandidate.dll']
    assert native['machine'] == '0x8664'
    assert 2 not in native['debugTypes'] and 17 not in native['debugTypes'], 'Native debug information'
    assert {'DllGetClassObject', 'DllCanUnloadNow', 'DllRegisterServer', 'DllUnregisterServer'} <= set(native['exports'])
    for name in files:
        if name.startswith('app/ClaudeZhInput') and name.endswith('.dll'):
            assert not {2, 17}.intersection(binaries[name]['debugTypes']), name
    build = json.loads((root / 'BUILD-INFO.json').read_text(encoding='utf-8-sig'))
    assert build['version'] == expected_version
    assert build['userDataIncluded'] is False
    compact = build.get('workerProfile') == 'stdlib-llm-v1'
    if compact:
        assert build['legacyTranslationIncluded'] is False
        assert build['appRuntime'] == 'self-contained-net8-windows-x64'
        assert not any(re.search(r'(^|/)(ctranslate2|numpy|sentencepiece|translate-zh_en-1_9)([/.]|$)', name, re.I) for name in files)
        config = json.loads((root/'app/ClaudeZhInput.runtimeconfig.json').read_text(encoding='utf-8-sig'))
        assert {'Microsoft.NETCore.App','Microsoft.WindowsDesktop.App'} <= {f['name'] for f in config['runtimeOptions']['includedFrameworks']}
        for name in ('coreclr.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll','PresentationFramework.dll','PresentationNative_cor3.dll'):
            assert 'app/' + name in files, name
        assert not any(name.startswith('setup/prerequisites/windowsdesktop-runtime-') for name in files)
    else:
        runtime = build['translationRuntime']
        assert runtime['cuda'] is False and runtime['version'] == '4.6.0'
        assert sha(root / 'ctranslate2.dll') == runtime['sha256'].upper()
        for file in runtime['runtimeFiles']:
            assert sha(root / file['name']) == file['sha256'].upper()
    assert not any(re.search(r'cudnn|libiomp', name, re.I) for name in files), 'Unused GPU/Intel runtime'
    if build.get('primaryTranslation'):
        llm = build['primaryTranslation']
        assert llm['cpuOnly'] is True and llm['transport'] == 'private-stdio'
        assert sha(root / llm['model']['file']) == llm['model']['sha256'].upper()
        for file in llm['files']:
            assert sha(root / file['file']) == file['sha256'].upper(), file['file']
        helper = binaries['translation-runtime/ClaudeZhInput.Translate.exe']
        assert helper['machine'] == '0x8664' and not {2, 17}.intersection(helper['debugTypes'])
        assert not any(re.search(r'server|rpc', n, re.I) for n in files if n.startswith('translation-runtime/'))
        for name in ('Qwen3.5-4B-Apache-2.0.txt', 'llama.cpp-b11580-MIT.txt', 'llama-LLVM-OpenMP.txt', 'TRANSLATION-MODEL-NOTICE.txt'):
            assert 'licenses/redistribution/' + name in files, name
    required_licenses = ['dotnet-runtime-LICENSE.txt', 'dotnet-runtime-THIRD-PARTY-NOTICES.txt',
                         'dotnet-windowsdesktop-LICENSE.txt', 'nlohmann-json-3.11.3-MIT.txt']
    if not compact:
        required_licenses += ['oneDNN-3.1.1-Apache-2.0.txt', 'oneDNN-3.1.1-THIRD-PARTY-PROGRAMS.txt',
                             'cpu_features-LICENSE.txt', 'spdlog-LICENSE.txt', 'SOURCES.txt']
    else:
        required_licenses += ['OpenSSL-3.0.13-Apache-2.0.txt', 'Python-3.11.9-THIRD-PARTY-NOTICES.rst']
    for license_file in required_licenses:
        assert 'licenses/redistribution/' + license_file in files, license_file
    archive_result = None
    if archive:
        with zipfile.ZipFile(archive) as z:
            names = [i.filename for i in z.infolist() if not i.is_dir()]
            assert len(names) == len(set(names)) and set(names) == set(files), 'ZIP contents differ'
            for name in names:
                expected = manifest.get(name) or sha(files[name])
                with z.open(name) as stream:
                    assert hashlib.file_digest(stream, 'sha256').hexdigest().upper() == expected, name
        archive_result = {'name': archive.name, 'sha256': sha(archive), 'bytes': archive.stat().st_size}
    return {'passed': True, 'files': len(files), 'peFiles': len(binaries),
            'nativeDebugTypes': native['debugTypes'], 'cpuImports': binaries['translation-runtime/llama.dll' if compact else 'ctranslate2.dll']['imports'],
            'archive': archive_result, 'scope': 'Package content, hashes, PE imports/debug data; no desktop input or installation'}


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('package', type=Path)
    parser.add_argument('--archive', type=Path)
    parser.add_argument('--expected-version', default='1.1.4')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = audit(args.package, args.archive, args.expected_version)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf8')
    print(json.dumps(result, ensure_ascii=True))
