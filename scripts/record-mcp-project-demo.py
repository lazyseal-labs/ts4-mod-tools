"""Record real MCP project calls for a terminal transcript replay."""

import argparse
import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import sys

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location('stdio', Path(__file__).with_name('verify-mcp-stdio.py'))
stdio = importlib.util.module_from_spec(spec)
spec.loader.exec_module(stdio)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', type=Path, required=True)
    parser.add_argument('--cli', type=Path, required=True)
    parser.add_argument('--root', type=Path, required=True)
    args = parser.parse_args()
    root = args.root.resolve()
    assert not root.exists(), 'Use a fresh disposable demo root'
    root.mkdir(parents=True)
    config = {'cliPath': str(args.cli.resolve()), 'dotnetPath': shutil.which('dotnet'),
              'readRoots': [str(root)], 'writeRoots': [str(root)], 'gameRoots': []}
    (root / 'config.json').write_text(json.dumps(config))
    client = stdio.StdioClient(args.host.resolve(), root / 'config.json')
    try:
        client.initialize()
        project = root / 'Demo project'
        initialized = stdio.report(client.call('s4mt_project_init', {'path': str(project), 'name': 'McpDemo'}))
        assert initialized['created'] is True
        (project / 'resources/tuning.xml').write_text('<I c="Buff" i="buff" m="buffs.buff" n="mcp_demo" s="1"/>')
        (project / 'package.json').write_text(json.dumps({'format': 1, 'resources': [
            {'type': '0x03B33DDF', 'group': '0x00000000', 'instance': '0x0000000000000001',
             'source': 'resources/tuning.xml', 'encoding': 'utf8'}]}))
        validated = stdio.report(client.call('s4mt_project_validate', {'project': str(project)}))
        assert validated['valid'] is True
        built = stdio.report(client.call('s4mt_project_build', {'project': str(project), 'outputRoot': str(root / 'builds')}))
        assert built['valid'] is True and Path(built['package']).is_file()
    finally:
        client.close()
        receipt = {'lane': 'demo', 'head': subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip(),
                   'checks': ['Real stdio workflow. Frames replay the recorded protocol transcript.'],
                   'messages': client.transcript, 'stderr': ''.join(client.stderr), 'hostExitCode': client.process.returncode}
        (root / 'receipt.json').write_text(json.dumps(receipt, indent=2), encoding='utf-8')
    print(root / 'receipt.json')


if __name__ == '__main__':
    main()
