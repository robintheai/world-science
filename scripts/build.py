#!/usr/bin/env python3
"""Reproducible Simple packaging. Core binaries are pinned to a reviewed upstream commit."""
import argparse, hashlib, json, os, pathlib, plistlib, shutil, subprocess, urllib.request, zipfile
ROOT = pathlib.Path(__file__).resolve().parents[1]
CORE_COMMIT = 'dd13a61184af1981545fe5f34a1283286959e51d'

def run(*args):
    subprocess.run([str(a) for a in args], cwd=ROOT, check=True)

def main():
    p=argparse.ArgumentParser();p.add_argument('--rid',required=True,choices=['osx-arm64','osx-x64','win-x64','win-arm64']);p.add_argument('--version',default='1.0.3');p.add_argument('--output',default=str(ROOT/'dist'));a=p.parse_args()
    rid=a.rid;arch=rid.split('-')[1];mac=rid.startswith('osx');out=pathlib.Path(a.output).resolve();out.mkdir(parents=True,exist_ok=True)
    pub=ROOT/'build'/rid;shutil.rmtree(pub,ignore_errors=True)
    run(os.environ.get('DOTNET','dotnet'),'publish','v2rayN/Simple/Simple.csproj','-c','Release','-r',rid,'--self-contained','true','-p:Version='+a.version,'-o',pub)
    cache=ROOT/'build'/'core-cache';cache.mkdir(parents=True,exist_ok=True)
    core_os='macos' if mac else 'windows';core_arch='64' if arch=='x64' else 'arm64';name=f'v2rayN-{core_os}-{core_arch}.zip';bundle=cache/name
    if not bundle.exists():
        urllib.request.urlretrieve(f'https://raw.githubusercontent.com/2dust/v2rayN-core-bin/{CORE_COMMIT}/{name}',bundle)
    digest=hashlib.sha256(bundle.read_bytes()).hexdigest()
    expected=json.loads((ROOT/'scripts/core-checksums.json').read_text()).get(name)
    if not expected or digest!=expected:raise RuntimeError('Pinned core archive checksum mismatch: '+name)
    with zipfile.ZipFile(bundle) as z:
        for entry in z.infolist():
            path=pathlib.PurePosixPath(entry.filename)
            if '..' in path.parts or path.is_absolute():raise RuntimeError('Unsafe archive member')
        extracted = ROOT/'build'/('cores-'+rid)
        shutil.rmtree(extracted, ignore_errors=True)
        z.extractall(extracted)
        candidates = list(extracted.glob('*/bin'))
        source = extracted/'bin' if (extracted/'bin').exists() else candidates[0] if len(candidates)==1 else None
        if source is None: raise RuntimeError('Core archive has no unique bin directory')
        shutil.copytree(source,pub/'bin',dirs_exist_ok=True)
    # Upstream bundles contain only core/rules files; never replace the custom GUI.
    if mac:
        helper_source=ROOT/'native/macos/network-helper.c'
        helper=pub/'Assets/network-helper'
        run('xcrun','clang','-Wall','-Wextra','-Werror','-O2','-mmacosx-version-min=13.6','-arch','arm64' if arch=='arm64' else 'x86_64',helper_source,'-o',helper)
        helper.with_suffix('.version').write_text(hashlib.sha256(helper_source.read_bytes()).hexdigest())
        shutil.copy2(ROOT/'native/macos/README.md',pub/'NETWORK-SERVICE.md')
        for path in (pub/'bin').rglob('*'):
            if path.is_file() and (path.name in ['xray','sing-box','mihomo'] or path.suffix=='.sh'):path.chmod(0o755)
    shutil.copy2(ROOT/'LICENSE',pub/'LICENSE-v2rayN.txt')
    shutil.copy2(ROOT/'THIRD-PARTY.md',pub/'THIRD-PARTY.md')
    shutil.copy2(ROOT/'QUICKSTART.md',pub/'QUICKSTART.md')
    shutil.copy2(ROOT/'INSTALL.md',pub/'INSTALL.md')
    (pub/'BUILD-INFO.json').write_text(json.dumps({'application':'Owlpath','version':a.version,'rid':rid,'source':subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip(),'upstream':'57f17d5939e88c8e643ab49b90869d637765e75a','core_commit':CORE_COMMIT,'core_sha256':digest},indent=2))
    filename=f'World-Science-{a.version}-{core_os}-{arch}'
    if mac:
        stage=ROOT/'build'/('dmg-'+rid);shutil.rmtree(stage,ignore_errors=True);stage.mkdir()
        app=stage/'Owlpath.app';contents=app/'Contents';resources=contents/'Resources';resources.mkdir(parents=True);shutil.copytree(pub,contents/'MacOS')
        shutil.copy2(ROOT/'v2rayN/Simple/Assets/simple.icns',resources/'AppIcon.icns')
        with (contents/'Info.plist').open('wb') as f:plistlib.dump({'CFBundleIdentifier':'io.github.robintheai.worldscience','CFBundleName':'Owlpath','CFBundleDisplayName':'Owlpath','CFBundleExecutable':'WorldScience','CFBundleIconFile':'AppIcon.icns','CFBundlePackageType':'APPL','CFBundleShortVersionString':a.version,'CFBundleVersion':a.version,'LSMinimumSystemVersion':'13.6','NSHighResolutionCapable':True,'CFBundleDevelopmentRegion':'zh_CN'},f)
        # Ad-hoc integrity signature, NOT Developer ID signing or Apple notarization.
        run('codesign','--force','--deep','--sign','-',app)
        (stage/'Applications').symlink_to('/Applications')
        shutil.copy2(ROOT/'INSTALL.md',stage/'安装说明.md')
        destination=out/(filename+'.dmg');destination.unlink(missing_ok=True)
        run('hdiutil','create','-volname','Owlpath','-srcfolder',stage,'-ov','-format','UDZO',destination)
    else:
        destination=out/(filename+'.zip');destination.unlink(missing_ok=True)
        with zipfile.ZipFile(destination,'w',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
            for path in sorted(pub.rglob('*')):
                if path.is_file():z.write(path,path.relative_to(pub))
    print(destination)
if __name__=='__main__':main()
