"""Exercise the actual image through loopback TLS with synthetic, isolated services."""
import datetime as dt
import http.cookiejar
import ipaddress
import json
import os
from pathlib import Path
import re
import secrets
import socket
import ssl
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.x509.oid import NameOID

IMAGE = os.environ.get('APTABASE_IMAGE_TEST_IMAGE', 'aptabase-local:retirement-candidate')
ORIGIN = 'https://127.0.0.1:55470'
MAIL = 'http://127.0.0.1:55471'
run = 'aptabase-image-' + uuid.uuid4().hex[:12]
resources = []
network = None
edge_network = None
checks = []

def docker(*args):
    result = subprocess.run(['docker', *args], capture_output=True, text=True, timeout=90)
    if result.returncode:
        raise RuntimeError('Docker operation failed: ' + result.stderr[-500:])
    return result.stdout.strip()

def key_fingerprints(container):
    # Hash as the application's existing container identity; never export key contents or widen permissions.
    obj = json.loads(docker('inspect', container))[0]
    assert (container, obj['Id']) in resources
    assert obj['Config']['Labels'].get('aptabase.image-test') == run
    output = docker('exec', container, 'sh', '-c', 'sha256sum /keys/*.xml')
    fingerprints = {}
    for line in output.splitlines():
        match = re.fullmatch(r'([0-9a-f]{64})  (/keys/key-[0-9a-f-]{36}\.xml)', line)
        assert match, 'Unexpected key digest output'
        assert match[2] not in fingerprints
        fingerprints[match[2]] = match[1]
    assert fingerprints, 'No persisted data protection keys'
    return fingerprints


def passed(name):
    checks.append(name)
    print('PASS:', name, flush=True)

def start(kind, image, options, command=()):
    name = run + '-' + kind
    ident = docker('run', '-d', '--rm', '--name', name, '--label', 'aptabase.image-test=' + run,
                   '--network', network, *options, image, *command)
    resources.append((name, ident))
    if kind in ('mail', 'proxy'):
        docker('network', 'connect', edge_network, name)
    return name

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None

def client(context, cookies=None):
    handlers = [urllib.request.ProxyHandler({}), NoRedirect(), urllib.request.HTTPSHandler(context=context)]
    if cookies is not None:
        handlers.append(urllib.request.HTTPCookieProcessor(cookies))
    return urllib.request.build_opener(*handlers)

def request(opener, path, body=None, method=None, raw=False, headers=None):
    url = path if path.startswith('http') else ORIGIN + path
    assert urllib.parse.urlsplit(url).netloc in ('127.0.0.1:55470', '127.0.0.1:55471')
    data = body if raw else (json.dumps(body).encode() if body is not None else None)
    req = urllib.request.Request(url, data=data, method=method,
        headers={'Content-Type': 'application/x-sentry-envelope' if raw else 'application/json', **(headers or {})})
    try:
        response = opener.open(req, timeout=8)
    except urllib.error.HTTPError as e:
        response = e
    with response:
        return response.status, response.headers, response.read()

for port in (55470, 55471):
    with socket.socket() as s:
        s.bind(('127.0.0.1', port))

with tempfile.TemporaryDirectory(prefix=run + '-') as temporary:
    root = Path(temporary).resolve()
    assert root.parent == Path(tempfile.gettempdir()).resolve()
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    subject = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, 'Aptabase isolated image test')])
    now = dt.datetime.now(dt.timezone.utc)
    cert = (x509.CertificateBuilder().subject_name(subject).issuer_name(subject).public_key(key.public_key())
        .serial_number(x509.random_serial_number()).not_valid_before(now-dt.timedelta(minutes=1))
        .not_valid_after(now+dt.timedelta(hours=1))
        .add_extension(x509.SubjectAlternativeName([x509.IPAddress(ipaddress.ip_address('127.0.0.1'))]), False)
        .sign(key, hashes.SHA256()))
    (root/'cert.pem').write_bytes(cert.public_bytes(serialization.Encoding.PEM))
    (root/'key.pem').write_bytes(key.private_bytes(serialization.Encoding.PEM,
        serialization.PrivateFormat.PKCS8, serialization.NoEncryption()))
    (root/'keys').mkdir()
    (root/'nginx.conf').write_text('''events {}
http { access_log off; server { listen 8443 ssl; server_name 127.0.0.1;
ssl_certificate /test/cert.pem; ssl_certificate_key /test/key.pem;
location / { proxy_pass http://collector:8080; proxy_set_header Host $http_host;
proxy_set_header X-Forwarded-Proto https; proxy_set_header X-Forwarded-For $remote_addr; }
} }
''')
    context = ssl.create_default_context(cafile=str(root/'cert.pem'))
    anonymous = client(context)
    cookies = http.cookiejar.CookieJar()
    owner = client(context, cookies)
    try:
        identity = json.loads(docker('image', 'inspect', IMAGE))[0]['Id']
        print('IMAGE:', identity, 'RUN:', run, flush=True)
        IMAGE = identity
        network = docker('network', 'create', '--internal', '--label', 'aptabase.image-test=' + run, run)
        # Docker Desktop does not publish ports on an internal-only bridge.
        # Only the fixed-target proxy and local mail sink join this edge bridge.
        edge_network = docker('network', 'create', '--label', 'aptabase.image-test=' + run, run+'-edge')
        pg = start('pg', 'postgres:15-alpine', ['--network-alias', 'database', '--cpus', '0.5', '--memory', '256m',
            '-e', 'POSTGRES_HOST_AUTH_METHOD=trust', '-e', 'POSTGRES_DB=aptabase'])
        ch = start('ch', 'clickhouse/clickhouse-server:23.8.4.69-alpine', ['--network-alias', 'analytics', '--cpus', '1', '--memory', '768m', '-e', 'CLICKHOUSE_SKIP_USER_SETUP=1'])
        mail = start('mail', 'dockage/mailcatcher:0.8.2', ['--network-alias', 'mail', '--cpus', '0.25', '--memory', '128m', '-p', '127.0.0.1:55471:1080'])
        for _ in range(30):
            try:
                docker('exec', pg, 'pg_isready', '-U', 'postgres')
                docker('exec', ch, 'clickhouse-client', '--query', 'SELECT 1')
                break
            except RuntimeError:
                time.sleep(1)
        else:
            raise RuntimeError('Isolated stores did not become ready')
        os.environ['AUTH_SECRET'] = secrets.token_urlsafe(48)
        app_options = ['--network-alias', 'collector', '--cpus', '1', '--memory', '512m',
            '-e', 'AUTH_SECRET', '-e', 'ASPNETCORE_ENVIRONMENT=Production', '-e', 'REGION=SH',
            '-e', 'ASPNETCORE_HTTP_PORTS=8080', '-e', 'BASE_URL='+ORIGIN,
            '-e', 'DATABASE_URL=Host=database;Username=postgres;Database=aptabase',
            '-e', 'CLICKHOUSE_URL=Host=analytics;Port=8123;Username=default',
            '-e', 'SMTP_HOST=mail', '-e', 'SMTP_PORT=1025', '-e', 'SMTP_FROM_ADDRESS=sender@example.test',
            '-e', 'DATA_PROTECTION_KEYS_DIRECTORY=/keys',
            '--mount', 'type=bind,source='+str(root/'keys')+',target=/keys']
        app = start('app', IMAGE, app_options)
        proxy = start('proxy', 'nginx:1.27-alpine', ['--cpus', '0.25', '--memory', '64m', '-p', '127.0.0.1:55470:8443',
            '--mount', 'type=bind,source='+str(root)+',target=/test,readonly',
            '--mount', 'type=bind,source='+str(root/'nginx.conf')+',target=/etc/nginx/nginx.conf,readonly'])
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline:
            try:
                if request(anonymous, '/readyz')[0] == 200:
                    break
            except (OSError, urllib.error.URLError):
                pass
            time.sleep(1)
        else:
            raise RuntimeError('Production image readiness timed out')
        passed('actual image Production startup, migrations and storage readiness over verified loopback TLS')
        try:
            request(client(ssl.create_default_context()), '/readyz')
            raise AssertionError('Test certificate unexpectedly system-trusted')
        except urllib.error.URLError as e:
            assert isinstance(e.reason, ssl.SSLCertVerificationError)
        passed('default system trust rejects the test certificate')
        assert docker('exec', pg, 'psql', '-U', 'postgres', '-d', 'aptabase', '-Atc', 'SELECT max(version) FROM migration_history') == '25'
        tables = docker('exec', ch, 'clickhouse-client', '--query', 'SHOW TABLES')
        assert 'error_events' in tables and 'events' in tables
        passed('PostgreSQL schema25 and ClickHouse analytics/error tables created by image startup')
        assert request(anonymous, '/api/_auth/me')[0] == 401
        assert request(owner, '/api/_auth/register', {'name':'Synthetic owner','email':'owner@example.test'})[0] == 200
        messages = json.loads(request(anonymous, MAIL+'/messages')[2])
        message = next(m for m in reversed(messages) if '<owner@example.test>' in m['recipients'])
        html = request(anonymous, MAIL+'/messages/'+str(message['id'])+'.html')[2].decode()
        import html as html_module
        link = html_module.unescape(re.search(r'href=[\"\x27]([^\"\x27]+)', html).group(1))
        assert link.startswith(ORIGIN+'/api/_auth/continue?')
        status, headers, _ = request(owner, link)
        assert status == 302
        cookie = headers.get('Set-Cookie','').lower()
        assert all(flag in cookie for flag in ['secure','httponly','samesite=strict'])
        assert request(owner, '/api/_auth/me')[0] == 200
        assert 'error=used' in request(anonymous, link)[1].get('Location','')
        passed('real registration/mail-catcher magic link, secure cookie, session and token replay rejection')
        status, headers, _ = request(owner, '/', headers={'Host': 'image-test.invalid'})
        assert status == 200
        assert headers.get('Strict-Transport-Security') and headers.get('Content-Security-Policy')
        assert headers.get('X-Frame-Options') == 'DENY'
        assert headers.get('X-Content-Type-Options') == 'nosniff'
        passed('production shell security headers (non-loopback Host for HSTS policy)')
        assert request(owner, '/api/_apps', {'name':'Synthetic image app'})[0] == 200
        appid = json.loads(request(owner, '/api/_apps')[2])[0]['id']
        projects = json.loads(request(owner, f'/api/v0/apps/{appid}/sentry-projects', {}, 'POST')[2])['projects']
        project = next(p for p in projects if p['buildMode']=='release')
        dsn = urllib.parse.urlsplit(project['dsn'])
        assert dsn.scheme=='https' and dsn.netloc.endswith('@127.0.0.1:55470')
        eventid = uuid.uuid4().hex
        event = {'event_id':eventid,'timestamp':dt.datetime.now(dt.timezone.utc).isoformat(),
            'platform':'csharp','release':'image@1','environment':'synthetic','exception':{'values':[{
            'type':'SyntheticImageException','value':'synthetic image failure', 'stacktrace':{'frames':[{
            'function':'Synthetic.Throw','filename':'Synthetic.cs','lineno':353,'colno':17}]}}]}}
        wire = ('{}\n'+json.dumps({'type':'event'})+'\n'+json.dumps(event)+'\n').encode()
        endpoint = '/api/'+str(project['id'])+'/envelope/?sentry_key='+dsn.username
        assert request(anonymous, endpoint, wire, raw=True)[0] == 200
        assert request(anonymous, endpoint, wire, raw=True)[0] == 200
        assert request(anonymous, f'/api/v0/apps/{appid}/issues')[0] == 401
        issues = json.loads(request(owner, f'/api/v0/apps/{appid}/issues')[2])
        assert issues['pagination']['total']==1 and issues['issues'][0]['occurrenceCount']==1
        assert 'Synthetic.Throw (Synthetic.cs) line 353:17' in issues['issues'][0]['stackTrace']
        passed('HTTPS DSN, synthetic error ingestion/retry dedup and authenticated issue access')
        session = {'sid': uuid.uuid4().hex, 'started': event['timestamp'], 'timestamp': event['timestamp'],
            'seq': 1, 'init': True, 'status': 'unhandled', 'errors': 1,
            'attrs': {'release': 'image@1', 'environment': 'synthetic'}}
        mixed = wire + (json.dumps({'type':'session'})+'\n'+json.dumps(session)+'\n').encode()
        assert request(anonymous, endpoint, mixed, raw=True)[0] == 200
        assert request(anonymous, endpoint, mixed, raw=True)[0] == 200
        health_path = f'/api/v0/apps/{appid}/monitoring/health?buildMode=release'
        assert request(anonymous, health_path)[0] == 401
        health = json.loads(request(owner, health_path)[2])['sessions']
        assert health['observed'] == 1 and health['unhandled'] == 1
        assert health['eligible'] == 0 and health['crashed'] == 0 and health['active'] == 0
        assert health['incomplete'] == 1 and health['crashFreeRate'] is None
        assert json.loads(request(owner, f'/api/v0/apps/{appid}/issues')[2])['issues'][0]['occurrenceCount'] == 1
        passed('mixed unhandled session replay preserves error dedup and excludes unconfirmed crashes from rate')
        key_files = key_fingerprints(app)
        assert key_files
        old_resource = next(item for item in resources if item[0] == app)
        old_container = json.loads(docker('inspect', app))[0]
        assert old_container['Id'] == old_resource[1]
        assert old_container['Config']['Labels'].get('aptabase.image-test') == run
        docker('stop', '--time', '5', app)
        resources.remove(old_resource)
        app = start('app-replacement', IMAGE, app_options)
        # Re-resolve the collector alias after its container address changes.
        docker('exec', proxy, 'nginx', '-s', 'reload')
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline:
            try:
                if request(owner, '/api/_auth/me')[0]==200: break
            except (OSError, urllib.error.URLError): pass
            time.sleep(1)
        else: raise AssertionError('Persisted login not accepted after container replacement')
        assert key_fingerprints(app) == key_files
        assert request(anonymous, endpoint, wire, raw=True)[0] == 200
        issues = json.loads(request(owner, f'/api/v0/apps/{appid}/issues')[2])
        assert issues['pagination']['total']==1 and issues['issues'][0]['occurrenceCount']==1
        assert json.loads(request(owner, health_path)[2])['sessions'] == health
        passed('container replacement preserves mounted keys, login and retry receipts; migrations remain idempotent')
        assert request(owner, '/api/_auth/signout', {}, 'POST')[0]==302
        assert request(owner, '/api/_auth/me')[0]==401
        passed('logout clears authenticated session')
        print('RESULT:',len(checks),'passed',flush=True)
    finally:
        failures=[]
        for name, ident in reversed(resources):
            try:
                obj=json.loads(docker('inspect',name))[0]
                assert obj['Id']==ident and obj['Config']['Labels'].get('aptabase.image-test')==run
                docker('stop','--time','5',name)
            except Exception as e:
                failures.append(name+': '+type(e).__name__)
        if network and not failures:
            docker('network','rm',network)
        if edge_network and not failures:
            docker('network','rm',edge_network)
        print('CLEANUP:', 'complete' if not failures else ', '.join(failures),flush=True)
        if failures:
            raise RuntimeError('Task-owned service cleanup incomplete')
