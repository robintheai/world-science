"""Disposable CI-only VLESS endpoint. Serves one reserved test destination; never a public proxy."""
import socketserver, struct, argparse
parser=argparse.ArgumentParser()
parser.add_argument('--host', default='0.0.0.0')
parser.add_argument('--port', type=int, default=19191)
parser.add_argument('--label', default='ws-vpn-tunnel-fixture-ok')
args=parser.parse_args()
UUID=bytes.fromhex('00000000000040008000000000000001')
class Handler(socketserver.BaseRequestHandler):
 def read(self,n):
  data=b''
  while len(data)<n:
   part=self.request.recv(n-len(data))
   if not part:raise EOFError()
   data+=part
  return data
 def handle(self):
  self.request.settimeout(10)
  try:
   version=self.read(1); uid=self.read(16);self.read(self.read(1)[0]);cmd=self.read(1);port=struct.unpack('!H',self.read(2))[0];kind=self.read(1)[0]
   host=self.read(4) if kind==1 else self.read(self.read(1)[0]) if kind==2 else self.read(16)
   if uid!=UUID or cmd!=b'\x01' or kind!=1 or host!=bytes([198,18,0,2]) or port!=18080:return
   self.request.sendall(version+b'\x00')
   req=b''
   while b'\r\n\r\n' not in req and len(req)<8192:req+=self.read(1)
   body=args.label.encode('ascii')
   self.request.sendall(b'HTTP/1.1 200 OK\r\nContent-Length: '+str(len(body)).encode()+b'\r\nConnection: close\r\n\r\n'+body)
  except (EOFError,OSError):pass
class Server(socketserver.ThreadingTCPServer):allow_reuse_address=True;daemon_threads=True
Server((args.host,args.port),Handler).serve_forever()
