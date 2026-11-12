from pathlib import Path
import hashlib,json
root=Path(__file__).resolve().parent
publish=root/'bin'/'publish'
assembly=publish/'Aptabase.Isolated.MultiShockProbe.dll'
data=assembly.read_bytes()
deps=json.loads((publish/'Aptabase.Isolated.MultiShockProbe.deps.json').read_text())
packages=deps['libraries']
assert 'Sentry.Maui/6.8.0' in packages
assert 'Sentry/6.8.0' in packages
assert not any(any(x in name.lower() for x in ['velopack','pishock','aptabase.maui']) for name in packages)
for marker in [b'UseSentry',b'MauiWinUIApplication',b'DemandLaunchPermission',b'LoopbackOnly']:
    assert marker in data, marker
for marker in ['Velopack','StartupCoordinator','DeviceManager','SerialRelayService','PluginManager','AppConfigurationService','MainPage','glitchtip.n1','aptabase.n1']:
    assert marker.encode() not in data and marker.encode('utf-16-le') not in data, marker
assert (publish/'Aptabase.Isolated.MultiShockProbe.pdb').read_bytes()[:4]==b'BSJB'
sdk=(publish/'Sentry.dll').read_bytes()
assert b'WinUIUnhandledExceptionIntegration' in sdk and b'add_UnhandledException' in sdk
original=Path('D:/Coding/PiShock/MultiShock/MultiShock')
for name,digest in json.loads((root/'bin'/'source-hashes.json').read_text()).items():
    assert hashlib.sha256((original/name).read_bytes()).hexdigest()==digest,name
print('PASS static published-artifact checks: Sentry.Maui/core6.8.0, WinUI host and launch/transport guards; forbidden startup/dependencies absent; Portable PDB; original copied files unchanged.')
print('SDK contains UnhandledException hook metadata; attachment/execution NOT tested. Application NOT launched.')
