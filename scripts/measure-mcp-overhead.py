"""Measure direct CLI and MCP project validation on the same pinned fixture."""

import argparse
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import platform
import subprocess
import sys
import time

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location('stdio', Path(__file__).with_name('verify-mcp-stdio.py'))
stdio = importlib.util.module_from_spec(spec)
spec.loader.exec_module(stdio)


def timed(action):
    start = time.perf_counter()
    value = action()
    return (time.perf_counter() - start) * 1000, value


def stats(samples):
    ordered = sorted(samples)
    return {'p50_ms': ordered[len(ordered) // 2 - 1], 'p95_ms': ordered[math.ceil(len(ordered) * .95) - 1],
            'min_ms': ordered[0], 'max_ms': ordered[-1], 'samples_ms': samples}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--cli', type=Path, required=True)
    parser.add_argument('--project', type=Path, required=True)
    parser.add_argument('--host', type=Path)
    parser.add_argument('--config', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--baseline', action='store_true')
    args = parser.parse_args()
    project = str(args.project.resolve())
    cli = str(args.cli.resolve())

    def direct(arguments):
        result = subprocess.run(['dotnet', cli, *arguments], capture_output=True, text=True, encoding='utf-8')
        assert result.returncode == 0, result.stderr
        return json.loads(result.stdout)

    def validate():
        result = direct(['project', 'validate', '--project', project])
        assert result['valid'] is True and result['command'] == 'project.validate', result
        return result

    digest = hashlib.sha256()
    for file in sorted(args.project.rglob('*')):
        if file.is_file():
            digest.update(str(file.relative_to(args.project)).replace('\\', '/').encode())
            digest.update(file.read_bytes())
    receipt = {'head': subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip(),
               'platform': platform.platform(), 'cpu_count': os.cpu_count(), 'fixture_sha256': digest.hexdigest(),
               'configuration': 'Release/net10.0', 'errors': 0, 'samples_per_path': 30, 'warmups_per_path': 1}
    validate()
    if args.baseline:
        receipt['direct_cli'] = stats([timed(validate)[0] for _ in range(30)])
        receipt['trunk_mcp'] = 'absent'
    else:
        assert args.host and args.config
        client = stdio.StdioClient(args.host.resolve(), args.config.resolve())
        try:
            client.initialize()

            def call():
                result = stdio.report(client.call('s4mt_project_validate', {'project': project}))
                assert result == validate_reference, result
                return result

            validate_reference = validate()
            call()
            cli_samples, mcp_samples = [], []
            for _ in range(30):
                cli_samples.append(timed(validate)[0])
                mcp_samples.append(timed(call)[0])
            receipt['direct_cli'] = stats(cli_samples)
            receipt['mcp_validate'] = stats(mcp_samples)
            client.request('tools/list', {})
            discovery = []
            for _ in range(30):
                elapsed, result = timed(lambda: client.request('tools/list', {}))
                assert len(result['result']['tools']) == 3, result
                discovery.append(elapsed)
            receipt['discovery'] = stats(discovery)
        finally:
            client.close()
        initialize_samples = []
        for i in range(31):
            start = time.perf_counter()
            cold = stdio.StdioClient(args.host.resolve(), args.config.resolve())
            try:
                initialized = cold.request('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {},
                                                'clientInfo': {'name': 's4mt-perf', 'version': '1'}})
                elapsed = (time.perf_counter() - start) * 1000
                assert 'result' in initialized, initialized
                if i:
                    initialize_samples.append(elapsed)
                cold.send({'jsonrpc': '2.0', 'method': 'notifications/initialized'})
            finally:
                cold.close()
        receipt['cold_initialize'] = stats(initialize_samples)
        direct(['--version'])
        receipt['cli_version_launch'] = stats([timed(lambda: direct(['--version']))[0] for _ in range(30)])
        receipt['tool_p95_overhead_ms'] = receipt['mcp_validate']['p95_ms'] - receipt['direct_cli']['p95_ms']
        receipt['budget_pass'] = receipt['tool_p95_overhead_ms'] <= 1000 and receipt['cold_initialize']['p95_ms'] <= 2000
        assert receipt['budget_pass'], receipt
        receipt['work'] = 'Every timed validation awaited and matched the full CLI report; every discovery returned three tools.'
        receipt['launch_probe_limit'] = 'CLI --version includes runtime startup and version dispatch; it is not subtracted from tool latency.'
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(receipt, indent=2), encoding='utf-8')
    print(json.dumps({key: ({k: v for k, v in value.items() if k != 'samples_ms'} if isinstance(value, dict) else value)
                      for key, value in receipt.items()}, indent=2))


if __name__ == '__main__':
    main()
