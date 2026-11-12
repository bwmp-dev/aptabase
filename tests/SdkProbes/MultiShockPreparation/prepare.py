from pathlib import Path
import hashlib, json
source=Path('D:/Coding/PiShock/MultiShock/MultiShock')
root=Path(__file__).resolve().parent
out=root/'bin'/'isolated-runtime'
if out.exists():
    raise SystemExit('Refusing to overwrite an existing preparation directory')
files=['Core/App.xaml','Platforms/Windows/App.xaml','Platforms/Windows/App.xaml.cs','Platforms/Windows/app.manifest']
evidence={}
for name in files:
    data=(source/name).read_bytes()
    evidence[name]=hashlib.sha256(data).hexdigest()
    target=out/name; target.parent.mkdir(parents=True,exist_ok=True)
    target.write_bytes(data)
win=out/'Platforms/Windows/App.xaml.cs'
s=win.read_text(encoding='utf-8-sig').replace('using Velopack;', '').replace('VelopackApp.Build().Run();','ProbeIsolation.DemandLaunchPermission();')
win.write_text(s,encoding='utf-8')
manifest=out/'Platforms/Windows/app.manifest'
manifest.write_text(manifest.read_text().replace('MultiShock.WinUI.app','Aptabase.Isolated.MultiShockProbe'),encoding='utf-8')
for name in ['MauiProgram.cs','App.xaml.cs','ProbeIsolation.cs','SyntheticMauiProbe.cs']:
    (out/'Core'/name).write_bytes((root/(name+'.template')).read_bytes())
(out/'IsolatedProbe.csproj').write_bytes((root/'IsolatedProbe.csproj.template').read_bytes())
(out/'packages.lock.json').write_bytes((root/'packages.lock.json').read_bytes())
(out/'NuGet.Config').write_text('<configuration><packageSources><clear /></packageSources></configuration>',encoding='utf-8')
(root/'bin'/'source-hashes.json').write_text(json.dumps(evidence,indent=2),encoding='utf-8')
assert 'Velopack' not in win.read_text()
assert 'ProbeIsolation.DemandLaunchPermission();' in win.read_text()
for p in out.rglob('*'):
    if p.is_file() and p.suffix in {'.cs', '.xaml', '.csproj', '.manifest'}:
        text=p.read_text(encoding='utf-8-sig')
        assert not any(x in text for x in ['glitchtip.n1','aptabase.n1','api.pishock','auth.pishock','ps.pishock','AppPaths','AppConfigurationService','MainPage','WebView','SerialRelayService','StartupCoordinator'])
print('Prepared reduced source-derived harness; original files only read. No launch performed.')
