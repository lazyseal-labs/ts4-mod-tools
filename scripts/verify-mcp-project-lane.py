"""Run one MCP-1 live lane against a built host in a disposable fixture."""

import argparse
import ctypes
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location('stdio', Path(__file__).with_name('verify-mcp-stdio.py'))
stdio = importlib.util.module_from_spec(spec)
spec.loader.exec_module(stdio)


def alive(pid):
    if os.name == 'nt':
        kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        kernel.OpenProcess.restype = ctypes.c_void_p
        kernel.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
        kernel.CloseHandle.argtypes = [ctypes.c_void_p]
        handle = kernel.OpenProcess(0x100000, False, pid)
        if not handle:
            return False
        try:
            return kernel.WaitForSingleObject(handle, 0) == 258
        finally:
            kernel.CloseHandle(handle)
    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False


def wait(predicate, seconds=5):
    deadline = time.monotonic() + seconds
    while not predicate():
        assert time.monotonic() < deadline, 'Timed out waiting for observed process or file state'
        time.sleep(.02)


def minimal(root, malformed=False):
    root.mkdir(parents=True)
    (root / 'resources').mkdir()
    (root / 'resources/tuning.xml').write_text('<I><T' if malformed else '<I c="Buff" i="buff" m="buffs.buff" n="mcp_minimal" s="1"/>')
    (root / 's4mt.json').write_text(json.dumps({'format': 1, 'name': 'McpMinimal', 'packageManifest': 'package.json', 'scriptArchives': []}))
    (root / 'package.json').write_text(json.dumps({'format': 1, 'resources': [
        {'type': '0x03B33DDF', 'group': '0x00000000', 'instance': '0x0000000000000001',
         'source': 'resources/tuning.xml', 'encoding': 'utf8'}]}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--lane', type=int, choices=range(1, 11), required=True)
    parser.add_argument('--host', type=Path, required=True)
    parser.add_argument('--cli', type=Path, required=True)
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--regression-project', type=Path)
    parser.add_argument('--fake-cli', type=Path)
    args = parser.parse_args()
    root = args.root.resolve()
    assert not root.exists(), 'Each lane requires a fresh disposable root'
    root.mkdir(parents=True)
    project = root / 'project'
    if args.lane == 1:
        assert args.regression_project
        shutil.copytree(args.regression_project, project)
    else:
        minimal(project, malformed=args.lane in (5, 6))
    cli = args.cli.resolve()
    if args.lane in (8, 9):
        assert args.fake_cli
        fixture = root / 'fake-cli'
        shutil.copytree(args.fake_cli.resolve().parent, fixture)
        (fixture / 'mode.txt').write_text('wait' if args.lane == 8 else 'success')
        cli = fixture / args.fake_cli.name
    if args.lane == 10:
        cli = root / 'missing-cli.dll'
    config = {'cliPath': str(cli), 'dotnetPath': shutil.which('dotnet'), 'readRoots': [str(root)],
              'writeRoots': [str(root)], 'gameRoots': [], 'timeoutMilliseconds': 15000}
    (root / 'config.json').write_text(json.dumps(config))
    client = stdio.StdioClient(args.host.resolve(), root / 'config.json')
    checks = []
    results = {}

    def direct(arguments, expected=0):
        result = subprocess.run(['dotnet', str(args.cli.resolve()), *arguments], capture_output=True, text=True, encoding='utf-8')
        assert result.returncode == expected, result.stderr or result.stdout
        return json.loads(result.stdout)

    def call(name, arguments):
        response = client.call(name, arguments)
        results[name] = response
        return response

    try:
        initialize, tools = client.initialize()
        checks.append('PASS initialize and discovery of exactly three project tools')
        if args.lane == 1:
            baseline = direct(['project', 'validate', '--project', str(project)])
            cli_build = direct(['project', 'build', '--project', str(project), '--output-root', str(root / 'cli-builds')])
            validated = stdio.report(call('s4mt_project_validate', {'project': str(project)}))
            assert validated == baseline and validated['valid'] is True
            built = stdio.report(call('s4mt_project_build', {'project': str(project), 'outputRoot': str(root / 'mcp-builds')}))
            assert built['fingerprint'] == cli_build['fingerprint']
            verified = direct(['verify', '--manifest', str(project / 'package.json'), '--package', built['package']])
            assert verified['valid'] is True
            results['packageVerification'] = verified
            reopened = direct(['project', 'build', '--project', str(project), '--output-root', str(root / 'mcp-builds')])
            assert reopened['package'] == built['package'] and reopened['fingerprint'] == built['fingerprint'] and reopened['reused'] is True
            checks += ['PASS trunk CLI has no MCP host; baseline direct validate/build succeeded',
                       'PASS full validation JSON parity, exact payload read-back, exact reopened package path and fingerprint']
            results['baseline'] = baseline
            results['baselineBuild'] = cli_build
        elif args.lane == 2:
            target = root / 'Project with spaces'
            invalid = call('s4mt_project_init', {'path': str(target), 'name': 'Name with spaces'})
            assert invalid['result']['isError'] is True and not target.exists()
            initialized = stdio.report(call('s4mt_project_init', {'path': str(target), 'name': 'NameWithSpaces'}))
            assert Path(initialized['projectFile']).is_file() and Path(initialized['packageManifest']).is_file()
            assert stdio.report(call('s4mt_project_init', {'path': str(target), 'name': 'NameWithSpaces'}))['created'] is False
            checks.append('PASS directory path with spaces initializes and retries; package names with spaces reject under existing CLI grammar')
        elif args.lane == 3:
            target = root.parent / (root.name + '-outside')
            response = call('s4mt_project_init', {'path': str(target), 'name': 'Outside'})
            assert response['result']['isError'] is True and response['result']['structuredContent']['error']['code'] == 'path_outside_root'
            assert not target.exists()
            checks.append('PASS outside write root rejected before file creation')
        elif args.lane in (4, 5, 6):
            response = call('s4mt_project_validate', {'project': str(project)})
            validated = stdio.report(response)
            assert validated['valid'] is (args.lane == 4)
            if args.lane in (5, 6):
                assert any(item['code'] == 'invalid_tuning_xml' and item['path'] == str(project / 'resources/tuning.xml') for item in validated['diagnostics'])
                assert response['result']['_meta']['cliExitCode'] == 1
                assert validated == direct(['project', 'validate', '--project', str(project)], expected=1)
            checks.append('PASS valid:true minimal project' if args.lane == 4 else 'PASS exit-1 invalid_tuning_xml report preserves source path and complete CLI JSON')
        elif args.lane == 7:
            arguments = {'project': str(project), 'outputRoot': str(root / 'builds')}
            first = stdio.report(call('s4mt_project_build', arguments))
            second = stdio.report(call('s4mt_project_build', arguments))
            assert first['fingerprint'] == second['fingerprint'] and first['package'] == second['package'] and second['reused'] is True
            checks.append('PASS second build reuses the same verified package path and fingerprint')
        elif args.lane == 8:
            arguments = {'project': str(project), 'outputRoot': str(root / 'builds')}
            request = client.begin('tools/call', {'name': 's4mt_project_build', 'arguments': arguments})
            wait(lambda: (fixture / 'descendant.pid').exists())
            pids = [int((fixture / file).read_text()) for file in ('child.pid', 'descendant.pid')]
            assert any((root / 'builds').glob('.s4mt-stage-*'))
            client.send({'jsonrpc': '2.0', 'method': 'notifications/cancelled', 'params': {'requestId': request, 'reason': 'Live cancellation lane'}})
            wait(lambda: all(not alive(pid) for pid in pids) and not any((root / 'builds').glob('.s4mt-stage-*')))
            (fixture / 'mode.txt').write_text('success')
            assert stdio.report(call('s4mt_project_build', arguments))['valid'] is True
            results['cancelledProcessIds'] = pids
            checks.append('PASS explicit MCP cancellation stopped parent and descendant, removed partial staging, and next build succeeded')
            if os.name == 'nt':
                for file in ('child.pid', 'descendant.pid'):
                    (fixture / file).unlink()
                (fixture / 'mode.txt').write_text('wait')
                client.begin('tools/call', {'name': 's4mt_project_build', 'arguments': arguments})
                wait(lambda: (fixture / 'descendant.pid').exists())
                crashed_pids = [int((fixture / file).read_text()) for file in ('child.pid', 'descendant.pid')]
                client.process.kill()
                client.process.wait(timeout=5)
                wait(lambda: all(not alive(pid) for pid in crashed_pids))
                client.close()
                results['crashedHostTranscript'] = client.transcript
                results['crashedHostStderr'] = ''.join(client.stderr)
                (fixture / 'mode.txt').write_text('success')
                client = stdio.StdioClient(args.host.resolve(), root / 'config.json')
                client.initialize()
                assert stdio.report(call('s4mt_project_build', arguments))['valid'] is True
                assert not any((root / 'builds').glob('.s4mt-stage-*'))
                checks.append('PASS abrupt host exit terminated its Windows job children; restarted host recovered staging and built successfully')
        elif args.lane == 9:
            response = call('s4mt_project_init', {'name': 'MissingPath'})
            assert response['result']['isError'] is True
            assert not (fixture / 'child.pid').exists()
            checks.append('PASS missing required path rejected before any child or output creation')
        elif args.lane == 10:
            response = call('s4mt_project_validate', {'project': str(project)})
            assert response['result']['isError'] is True and response['result']['structuredContent']['error']['code'] == 'cli_unavailable'
            assert len(client.request('tools/list', {})['result']['tools']) == 3
            checks.append('PASS missing CLI has actionable stable error and subsequent protocol discovery remains parseable')
    finally:
        client.close()
        receipt = {'lane': args.lane, 'head': subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip(),
                   'checks': checks, 'results': results, 'messages': client.transcript,
                   'stderr': ''.join(client.stderr), 'hostExitCode': client.process.returncode}
        (root / 'receipt.json').write_text(json.dumps(receipt, indent=2), encoding='utf-8')
    assert client.process.returncode == 0
    print(json.dumps({'lane': args.lane, 'checks': checks, 'receipt': str(root / 'receipt.json')}, indent=2))


if __name__ == '__main__':
    main()
