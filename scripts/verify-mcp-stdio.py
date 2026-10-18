"""Exercise the real stdio host with newline-delimited MCP JSON-RPC."""

import argparse
import json
from pathlib import Path
import queue
import subprocess
import threading
import time


class StdioClient:
    def __init__(self, host, config):
        self.messages = queue.Queue()
        self.stderr = []
        self.transcript = []
        self.next_id = 0
        self.process = subprocess.Popen(
            ['dotnet', str(host), '--config', str(config)],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding='utf-8', bufsize=1)
        self.readers = [threading.Thread(target=self._read, daemon=True),
                        threading.Thread(target=self._read_stderr, daemon=True)]
        for reader in self.readers:
            reader.start()

    def _read(self):
        try:
            for line in self.process.stdout:
                message = json.loads(line)
                self.transcript.append({'at': time.time(), 'receive': message})
                self.messages.put(message)
        except Exception as error:
            self.messages.put(error)
        finally:
            self.messages.put(EOFError('MCP stdout closed'))

    def _read_stderr(self):
        for line in self.process.stderr:
            if sum(map(len, self.stderr)) < 1024 * 1024:
                self.stderr.append(line)

    def send(self, message):
        self.transcript.append({'at': time.time(), 'send': message})
        self.process.stdin.write(json.dumps(message) + '\n')
        self.process.stdin.flush()

    def begin(self, method, params):
        self.next_id += 1
        self.send({'jsonrpc': '2.0', 'id': self.next_id, 'method': method, 'params': params})
        return self.next_id

    def receive(self, request_id, timeout=35):
        deadline = time.monotonic() + timeout
        while True:
            message = self.messages.get(timeout=max(.01, deadline - time.monotonic()))
            if isinstance(message, Exception):
                raise message
            if message.get('id') == request_id:
                return message

    def request(self, method, params):
        return self.receive(self.begin(method, params))

    def initialize(self):
        result = self.request('initialize', {'protocolVersion': '2025-11-25',
                              'capabilities': {}, 'clientInfo': {'name': 's4mt-smoke', 'version': '1'}})
        assert 'result' in result, result
        self.send({'jsonrpc': '2.0', 'method': 'notifications/initialized'})
        tools = self.request('tools/list', {})
        names = sorted(tool['name'] for tool in tools['result']['tools'])
        assert names == ['s4mt_project_build', 's4mt_project_init', 's4mt_project_validate'], names
        return result, tools

    def call(self, name, arguments):
        return self.request('tools/call', {'name': name, 'arguments': arguments})

    def close(self):
        self.process.stdin.close()
        try:
            self.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait(timeout=5)
            raise AssertionError('Host did not stop on stdin EOF')
        for reader in self.readers:
            reader.join(timeout=2)


def report(response):
    assert 'result' in response and response['result'].get('isError') is not True, response
    return response['result']['structuredContent']


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', type=Path, required=True)
    parser.add_argument('--config', type=Path, required=True)
    parser.add_argument('--project', type=Path, required=True)
    parser.add_argument('--output-root', type=Path)
    parser.add_argument('--transcript', type=Path, required=True)
    args = parser.parse_args()
    client = StdioClient(args.host.resolve(), args.config.resolve())
    try:
        initialize, tools = client.initialize()
        print('PASS initialize and tools/list')
        validated = report(client.call('s4mt_project_validate', {'project': str(args.project.resolve())}))
        print(json.dumps(validated, indent=2))
        if args.output_root:
            built = report(client.call('s4mt_project_build', {'project': str(args.project.resolve()),
                           'outputRoot': str(args.output_root.resolve())}))
            print(json.dumps(built, indent=2))
    finally:
        client.close()
        args.transcript.parent.mkdir(parents=True, exist_ok=True)
        args.transcript.write_text(json.dumps({'messages': client.transcript, 'stderr': ''.join(client.stderr),
                                   'hostExitCode': client.process.returncode}, indent=2), encoding='utf-8')


if __name__ == '__main__':
    main()
