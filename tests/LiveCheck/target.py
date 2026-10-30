import http.server
class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path.startswith('/slow'):
            import time
            self.send_response(200); self.send_header('Content-Length',str(3_000_000)); self.end_headers()
            for _ in range(30): self.wfile.write(b'x'*100_000); time.sleep(0.1)
        elif self.path.startswith('/big'):
            data=b'x'*3_000_000; self.send_response(200); self.send_header('Content-Length',str(len(data))); self.end_headers(); self.wfile.write(data)
        else:
            self.send_response(204); self.end_headers()
    do_HEAD=do_GET
    def log_message(self,*a): pass
import ssl,sys,threading
srv=http.server.ThreadingHTTPServer(('127.0.0.1',18080),H)
tls=http.server.ThreadingHTTPServer(('127.0.0.1',18443),H)
if len(sys.argv)>2:
    ctx=ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER); ctx.load_cert_chain(sys.argv[1],sys.argv[2]); tls.socket=ctx.wrap_socket(tls.socket,server_side=True)
    threading.Thread(target=tls.serve_forever,daemon=True).start()
srv.serve_forever()
