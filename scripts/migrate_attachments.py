#!/usr/bin/env python3
"""Copy legacy immutable attachments to private Storage without changing their IDs.

Stop attachment writes during migration. Nothing is deleted or overwritten.
Use --apply explicitly, after the dry run and a backup. Credentials come from the environment.
"""
import argparse
import hashlib
import json
import os
import re
import ssl
import struct
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from pathlib import Path

MAX = 20 * 1024 * 1024
BUCKET = 'reconstruction-attachments'


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class Storage:
    def __init__(self):
        self.base = os.environ['Attachments__Storage__BaseUrl'].rstrip('/')
        uri = urllib.parse.urlsplit(self.base)
        if uri.scheme != 'https' or not uri.hostname or uri.username or uri.password or uri.path or uri.query or uri.fragment:
            raise ValueError('HTTPS storage origin required')
        self.key = os.environ['Attachments__Storage__PublishableKey']
        self.opener = urllib.request.build_opener(NoRedirect(), urllib.request.HTTPSHandler(context=ssl.create_default_context()))
        self.token, self.expires = None, 0

    def request(self, path, data=None, token=None, mime='application/json', limit=65536):
        headers = {'apikey': self.key, 'Content-Type': mime, 'x-upsert': 'false'}
        if token:
            headers['Authorization'] = 'Bearer ' + token
        request = urllib.request.Request(self.base + path, data=data, headers=headers)
        try:
            response = self.opener.open(request, timeout=30)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            payload = response.read(limit + 1)
            if len(payload) > limit:
                raise RuntimeError('Storage response limit exceeded')
            return response.status, payload

    def authorization(self):
        if time.monotonic() >= self.expires:
            status, response = self.request('/auth/v1/token?grant_type=password', json.dumps({
                'email': os.environ['Attachments__Storage__Email'],
                'password': os.environ['Attachments__Storage__Password'],
            }).encode())
            if status != 200:
                raise RuntimeError('Storage identity rejected')
            result = json.loads(response)
            lifetime = int(result['expires_in'])
            if lifetime < 1 or not result['access_token']:
                raise RuntimeError('Invalid storage credential')
            self.token, self.expires = result['access_token'], time.monotonic() + max(1, lifetime - 60)
        return self.token

    def copy(self, path, payload):
        endpoint = '/storage/v1/object/authenticated/' + path
        status, current = self.request(endpoint, token=self.authorization(), limit=MAX + 8196)
        if status == 200:
            # JSON serialization can differ in escaping/order; compare the decoded metadata and exact content.
            if unpack(current) != unpack(payload):
                raise RuntimeError('Stored attachment conflicts with legacy identity')
            return False
        if status != 404 and not (status == 400 and str(json.loads(current).get('statusCode')) == '404'):
            raise RuntimeError('Unable to verify storage object absence')
        status, _ = self.request('/storage/v1/object/' + path, payload, self.authorization(), 'application/octet-stream')
        if status not in (200, 201):
            raise RuntimeError('Storage upload failed; rerun after verifying uncertain outcome')
        status, current = self.request(endpoint, token=self.authorization(), limit=MAX + 8196)
        if status != 200 or unpack(current) != unpack(payload):
            raise RuntimeError('Read-after-write verification failed')
        return True


def unpack(payload):
    length = struct.unpack('>I', payload[:4])[0]
    if not 0 < length <= 8192 or len(payload) <= length + 4:
        raise ValueError('Invalid attachment envelope')
    return json.loads(payload[4:4 + length]), payload[4 + length:]


def load(folder, path):
    if path.is_symlink() or not re.fullmatch('[0-9a-f]{32}', path.stem):
        raise ValueError('Invalid attachment metadata path')
    if path.stat().st_size > 8192:
        raise ValueError('Metadata exceeds limit')
    meta = json.loads(path.read_text())
    if set(meta) != {'Id', 'ProjectId', 'Name', 'ContentType', 'Length', 'Sha256', 'Category'}:
        raise ValueError('Invalid attachment metadata')
    identity, project = uuid.UUID(meta['Id']), uuid.UUID(meta['ProjectId'])
    if identity.hex != path.stem or not project.int or not identity.int:
        raise ValueError('Invalid attachment identity')
    data = folder / (path.stem + '.bin')
    if data.is_symlink() or not data.is_file() or not 0 < data.stat().st_size <= MAX:
        raise ValueError('Missing or oversized attachment data')
    content = data.read_bytes()
    name = meta['Name']
    if (not isinstance(name, str) or not 0 < len(name) <= 200 or any(ord(c) < 32 or ord(c) == 127 or c in '/\\' for c in name)
            or meta['Category'] not in ('evidence', 'document') or meta['Length'] != len(content)
            or meta['Sha256'].upper() != hashlib.sha256(content).hexdigest().upper()):
        raise ValueError('Attachment integrity check failed')
    signatures = {'.pdf': ('application/pdf', b'%PDF-'), '.png': ('image/png', b'\x89PNG\r\n\x1a\n'),
                  '.jpg': ('image/jpeg', b'\xff\xd8\xff'), '.jpeg': ('image/jpeg', b'\xff\xd8\xff')}
    mime, signature = signatures[Path(name).suffix.lower()]
    if meta['ContentType'] != mime or not content.startswith(signature):
        raise ValueError('Attachment file type mismatch')
    encoded = json.dumps(meta, ensure_ascii=False, separators=(',', ':')).encode()
    if len(encoded) > 8192:
        raise ValueError('Encoded metadata exceeds limit')
    return f'{BUCKET}/{project.hex}/{identity.hex}', struct.pack('>I', len(encoded)) + encoded + content


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path)
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    folder = args.directory.resolve(strict=True)
    if not folder.is_dir():
        raise ValueError('Attachment directory required')
    paths = sorted(folder.glob('*.json'))
    if {p.stem for p in paths} != {p.stem for p in folder.glob('*.bin')}:
        raise ValueError('Resolve orphan metadata/data before migration')
    # Validate all before making any remote changes.
    for path in paths:
        load(folder, path)
    if not args.apply:
        print(f'Validated {len(paths)} attachment pairs; no remote writes')
        return
    storage = Storage()
    copied = sum(storage.copy(*load(folder, path)) for path in paths)
    print(f'Checked {len(paths)} attachments; uploaded {copied}; originals preserved')


if __name__ == '__main__':
    main()
