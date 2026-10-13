#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""从 Docker Registry v2 API 直拉镜像,导出 docker-archive tar(docker load / podman load 兼容)。

用途:无 Docker/Podman 运行环境的机器(或目标服务器无外网时)把基础镜像打进部署包离线分发。
用法:python pull-base-image.py <镜像引用> <输出.tar>
示例:python pull-base-image.py mcr.microsoft.com/dotnet/runtime-deps:10.0-noble basedeps.tar

实现:匿名(必要时 Bearer token)-> 取 manifest(多架构则选 linux/amd64)-> 下载 config+layers
      -> 校验 sha256/diff_ids -> 组装 manifest.json 格式的 docker-archive。
"""
import gzip
import hashlib
import json
import sys
import tarfile
import urllib.error
import urllib.parse
import urllib.request

MANIFEST_ACCEPT = ', '.join([
    'application/vnd.docker.distribution.manifest.v2+json',
    'application/vnd.docker.distribution.manifest.list.v2+json',
    'application/vnd.oci.image.manifest.v1+json',
    'application/vnd.oci.image.index.v1+json',
])


def parse_ref(ref):
    parts = ref.split('/')
    if '.' in parts[0] or ':' in parts[0] or parts[0] == 'localhost':
        registry, repo = parts[0], '/'.join(parts[1:])
    else:
        registry, repo = 'registry-1.docker.io', ref
    if '@sha256:' in repo:
        repo, digest = repo.split('@', 1)
        reference = digest
    else:
        repo, _, tag = repo.partition(':')
        reference = tag or 'latest'
    if not repo:
        raise SystemExit('非法镜像引用: %s' % ref)
    return registry, repo, reference


class Registry:
    def __init__(self, registry, repo):
        self.base = 'https://' + registry
        self.repo = repo
        self.token = None

    def _fetch_token(self, auth_header):
        params = {}
        for item in auth_header.split(' ', 1)[1].split(','):
            k, _, v = item.partition('=')
            params[k.strip()] = v.strip().strip('"')
        q = {'service': params.get('service', ''),
             'scope': params.get('scope') or ('repository:%s:pull' % self.repo)}
        url = params['realm'] + '?' + urllib.parse.urlencode(q)
        with urllib.request.urlopen(url, timeout=60) as r:
            return json.load(r)['token']

    def get(self, path, accept=None):
        url = self.base + path
        for _ in range(2):
            req = urllib.request.Request(url)
            if accept:
                req.add_header('Accept', accept)
            if self.token:
                req.add_header('Authorization', 'Bearer ' + self.token)
            try:
                return urllib.request.urlopen(req, timeout=300)
            except urllib.error.HTTPError as e:
                if e.code == 401 and self.token is None:
                    self.token = self._fetch_token(e.headers.get('WWW-Authenticate', ''))
                    continue
                raise
        raise SystemExit('认证失败')


def add_bytes(tf, name, data):
    info = tarfile.TarInfo(name)
    info.size = len(data)
    info.mtime = 0
    tf.addfile(info, __import__('io').BytesIO(data))


def main():
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    image, out = sys.argv[1], sys.argv[2]
    registry, repo, reference = parse_ref(image)
    print('registry=%s repo=%s ref=%s' % (registry, repo, reference))
    reg = Registry(registry, repo)

    mf = json.load(reg.get('/v2/%s/manifests/%s' % (repo, reference), MANIFEST_ACCEPT))
    if 'manifests' in mf:  # 多架构索引 -> 选 linux/amd64
        pick = None
        for m in mf['manifests']:
            p = m.get('platform', {})
            if p.get('architecture') == 'amd64' and p.get('os') == 'linux':
                pick = m['digest']
                break
        if not pick:
            raise SystemExit('索引中无 linux/amd64 架构')
        print('多架构索引 -> %s' % pick)
        mf = json.load(reg.get('/v2/%s/manifests/%s' % (repo, pick), MANIFEST_ACCEPT))

    cfg_digest = mf['config']['digest']
    cfg_hex = cfg_digest.split(':', 1)[1]
    cfg_bytes = reg.get('/v2/%s/blobs/%s' % (repo, cfg_digest)).read()
    if hashlib.sha256(cfg_bytes).hexdigest() != cfg_hex:
        raise SystemExit('config 校验失败')
    cfg = json.loads(cfg_bytes)
    diff_ids = cfg.get('rootfs', {}).get('diff_ids', [])
    print('config ok,层数=%d' % len(mf['layers']))

    layer_paths = []
    with tarfile.open(out, 'w') as tf:
        add_bytes(tf, cfg_hex + '.json', cfg_bytes)
        for i, layer in enumerate(mf['layers']):
            d = layer['digest']
            blob = reg.get('/v2/%s/blobs/%s' % (repo, d)).read()
            if hashlib.sha256(blob).hexdigest() != d.split(':', 1)[1]:
                raise SystemExit('layer %d 摘要校验失败' % i)
            raw = gzip.decompress(blob) if blob[:2] == b'\x1f\x8b' else blob
            diff_hex = hashlib.sha256(raw).hexdigest()
            if diff_ids and diff_ids[i] != 'sha256:' + diff_hex:
                raise SystemExit('layer %d diff_id 与 config 不一致(组装错误)' % i)
            add_bytes(tf, diff_hex + '/layer.tar', raw)
            layer_paths.append(diff_hex + '/layer.tar')
            print('  layer %d/%d: %d MB' % (i + 1, len(mf['layers']), len(raw) // 1048576))
        manifest = [{'Config': cfg_hex + '.json', 'RepoTags': [image], 'Layers': layer_paths}]
        add_bytes(tf, 'manifest.json', json.dumps(manifest, indent=2).encode('utf-8'))

    print('完成: %s (RepoTags=%s, %d 层)' % (out, image, len(layer_paths)))


if __name__ == '__main__':
    main()
