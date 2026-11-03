#!/usr/bin/env python3
"""Sign a verified unsigned Android release with a persistent, local-only identity.
Never place the signing directory or password in Git, release artifacts or the website.
"""
import argparse,hashlib,json,os,pathlib,secrets,shutil,subprocess,sys
p=argparse.ArgumentParser();p.add_argument('--apk',required=True);p.add_argument('--validation',required=True);p.add_argument('--apksigner',required=True);p.add_argument('--output',required=True);a=p.parse_args()
validation=json.loads(pathlib.Path(a.validation).read_text())
if not validation.get('vpn_tunnel_and_cleanup') or not validation.get('package_reinstall_keeps_session') or hashlib.sha256(pathlib.Path(a.apk).read_bytes()).hexdigest()!=validation.get('unsigned_apk_sha256'):
 raise SystemExit('APK does not match the successful VPN and session validation artifact.')
# The published app's identity is passed by the private release script; these defaults suit a local build.
folder=os.environ.get('SIGNING_FOLDER','World Science Build Signing');alias=os.environ.get('SIGNING_ALIAS','world-science-android')
base=pathlib.Path.home()/'Library'/'Application Support'/folder if sys.platform=='darwin' else pathlib.Path(os.environ.get('LOCALAPPDATA',str(pathlib.Path.home()/'.local/share')))/folder
base.mkdir(parents=True,exist_ok=True,mode=0o700);os.chmod(base,0o700)
key=base/'android-release.p12';password=base/'android-release.password'
if not key.exists():
 if password.exists():raise SystemExit('Incomplete signing setup; inspect locally before continuing.')
 fd=os.open(password,os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600)
 with os.fdopen(fd,'w') as f:f.write(secrets.token_urlsafe(48))
 try:
  subprocess.run(['keytool','-genkeypair','-keystore',str(key),'-storetype','PKCS12','-storepass:file',str(password),'-alias',alias,'-keyalg','RSA','-keysize','4096','-validity','10000','-dname','CN=World Science, O=World Science','-noprompt'],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.PIPE)
  os.chmod(key,0o600)
 except Exception:
  password.unlink(missing_ok=True);raise
if not password.exists():raise SystemExit('Signing password file is missing. Do not generate a new identity.')
output=pathlib.Path(a.output);output.parent.mkdir(parents=True,exist_ok=True)
subprocess.run(['java','-jar',a.apksigner,'sign','--ks',str(key),'--ks-key-alias',alias,'--ks-pass','file:'+str(password),'--out',str(output),a.apk],check=True)
subprocess.run(['java','-jar',a.apksigner,'verify','--verbose',str(output)],check=True)
print('Signed release APK:',output)
print('Persistent signing identity retained locally; not included in the release.')
