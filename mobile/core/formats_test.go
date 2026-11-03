package mobilecore

import (
	"encoding/base64"
	"strings"
	"testing"
)

const clashFixture = `port: 7890
x-base: &base {server: 192.0.2.10, port: 443}
proxies:
  - {name: "SS 🇯🇵", type: ss, server: 192.0.2.1, port: 8388, cipher: aes-256-gcm, password: "p@ss:w/rd", udp: true}
  - name: VMess WS
    type: vmess
    server: vmess.example.com
    port: 443
    uuid: 00000000-0000-4000-8000-000000000002
    alterId: 0
    cipher: auto
    tls: true
    servername: cdn.example.com
    network: ws
    ws-opts: {path: "/ws?ed=2048", headers: {Host: cdn.example.com}}
  - {name: VMess gRPC, type: vmess, server: 192.0.2.3, port: 443, uuid: 00000000-0000-4000-8000-000000000003, alterId: 0, cipher: auto, tls: true, network: grpc, grpc-opts: {grpc-service-name: svc}}
  - {name: VMess H2, type: vmess, server: 192.0.2.11, port: 443, uuid: 00000000-0000-4000-8000-000000000011, alterId: 0, cipher: auto, tls: true, network: h2, h2-opts: {host: [h2.example.com], path: /h2}}
  - name: VLESS Reality
    type: vless
    server: 192.0.2.4
    port: 443
    uuid: 00000000-0000-4000-8000-000000000004
    flow: xtls-rprx-vision
    tls: true
    servername: www.example.com
    client-fingerprint: chrome
    reality-opts: {public-key: jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0, short-id: 6ba85179e30d4fc2}
  - {name: VLESS WS, type: vless, server: 192.0.2.5, port: 443, uuid: 00000000-0000-4000-8000-000000000005, tls: true, network: ws, ws-opts: {path: /vl}, skip-cert-verify: true}
  - {name: Trojan gRPC, type: trojan, server: 192.0.2.6, port: 443, password: "tro jan", sni: t.example.com, network: grpc, grpc-opts: {grpc-service-name: tg}}
  - {name: HY2, type: hysteria2, server: 192.0.2.7, port: 8443, password: hy, sni: h.example.com, obfs: salamander, obfs-password: ob, skip-cert-verify: true}
  - {name: TUIC, type: tuic, server: 192.0.2.8, port: 443, uuid: 00000000-0000-4000-8000-000000000008, password: tu, alpn: [h3], congestion-controller: cubic, sni: tu.example.com}
  - {name: TUIC v4, type: tuic, server: 192.0.2.9, port: 443, token: abc}
  - {name: SSR, type: ssr, server: 192.0.2.12, port: 443, cipher: aes-256-cfb, password: x, obfs: plain, protocol: origin}
  - {name: Snell, type: snell, server: 192.0.2.13, port: 443, psk: x}
  - {name: SS obfs, type: ss, server: 192.0.2.14, port: 443, cipher: aes-128-gcm, password: x, plugin: obfs, plugin-opts: {mode: tls}}
  - {name: VLESS PQ, type: vless, server: 192.0.2.15, port: 443, uuid: 00000000-0000-4000-8000-000000000015, encryption: mlkem768x25519plus.native.0rtt.fixture}
  - {<<: *base, name: Merged, type: trojan, password: m}
proxy-groups:
  - {name: Proxy, type: select, proxies: [VMess WS]}
rules:
  - MATCH,Proxy
`

func byName(t *testing.T, c string) map[string]map[string]any {
	nodes := map[string]map[string]any{}
	for _, o := range outbounds(t, c) {
		if _, ok := o["server"]; ok {
			name := strings.TrimSpace(o["tag"].(string)[:strings.LastIndex(o["tag"].(string), " · ")])
			nodes[name] = o
		}
	}
	return nodes
}

func TestClashSubscription(t *testing.T) {
	links, skipped := clashLinks(clashFixture)
	if len(links) != 10 || skipped != 5 {
		t.Fatal("unexpected Clash conversion", len(links), skipped)
	}
	c, e := BuildConfig(clashFixture)
	if e != nil {
		t.Fatal(e)
	}
	if e = CheckConfig(c); e != nil {
		t.Fatal(e)
	}
	n := byName(t, c)
	if len(n) != 10 {
		t.Fatal("expected ten Clash nodes", len(n))
	}
	ss := n["SS 🇯🇵"]
	if ss["type"] != "shadowsocks" || ss["method"] != "aes-256-gcm" || ss["password"] != "p@ss:w/rd" || ss["server_port"] != float64(8388) {
		t.Fatal("ss", ss)
	}
	ws := n["VMess WS"]
	wsT, wsTLS := ws["transport"].(map[string]any), ws["tls"].(map[string]any)
	if ws["type"] != "vmess" || wsT["type"] != "ws" || wsT["path"] != "/ws?ed=2048" || wsT["headers"].(map[string]any)["Host"] != "cdn.example.com" || wsTLS["server_name"] != "cdn.example.com" {
		t.Fatal("vmess ws", ws)
	}
	if n["VMess gRPC"]["transport"].(map[string]any)["service_name"] != "svc" || n["VMess H2"]["transport"].(map[string]any)["type"] != "http" {
		t.Fatal("vmess grpc/h2", n["VMess gRPC"], n["VMess H2"])
	}
	r := n["VLESS Reality"]
	rTLS := r["tls"].(map[string]any)
	reality := rTLS["reality"].(map[string]any)
	if r["flow"] != "xtls-rprx-vision" || reality["public_key"] != "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0" || reality["short_id"] != "6ba85179e30d4fc2" || rTLS["server_name"] != "www.example.com" || rTLS["utls"].(map[string]any)["fingerprint"] != "chrome" {
		t.Fatal("vless reality", r)
	}
	if n["VLESS WS"]["tls"].(map[string]any)["insecure"] != true || n["VLESS WS"]["transport"].(map[string]any)["path"] != "/vl" {
		t.Fatal("vless ws", n["VLESS WS"])
	}
	tr := n["Trojan gRPC"]
	if tr["password"] != "tro jan" || tr["transport"].(map[string]any)["service_name"] != "tg" || tr["tls"].(map[string]any)["server_name"] != "t.example.com" {
		t.Fatal("trojan", tr)
	}
	hy := n["HY2"]
	if hy["type"] != "hysteria2" || hy["obfs"].(map[string]any)["password"] != "ob" || hy["tls"].(map[string]any)["insecure"] != true {
		t.Fatal("hysteria2", hy)
	}
	tu := n["TUIC"]
	if tu["uuid"] != "00000000-0000-4000-8000-000000000008" || tu["password"] != "tu" || tu["congestion_control"] != "cubic" {
		t.Fatal("tuic", tu)
	}
	if n["Merged"]["server"] != "192.0.2.10" {
		t.Fatal("merge keys", n["Merged"])
	}
	encoded, e := BuildConfig(base64.StdEncoding.EncodeToString([]byte(clashFixture)))
	if e != nil || len(byName(t, encoded)) != 10 {
		t.Fatal("base64-wrapped Clash YAML", e)
	}
}

func TestClashWithoutSupportedProxiesFails(t *testing.T) {
	for _, s := range []string{"proxies:\n  - {name: x, type: snell, server: 192.0.2.1, port: 1, psk: x}\n", "proxies: [ {name: broken"} {
		if _, e := BuildConfig(s); e == nil {
			t.Fatal("Clash document without usable proxies accepted")
		}
	}
}

func TestSIP008Subscription(t *testing.T) {
	doc := `{"version": 1, "servers": [{"id": "a", "remarks": "SIP A", "server": "192.0.2.41", "server_port": 8388, "password": "pw", "method": "chacha20-ietf-poly1305"}, {"remarks": "SIP B", "server": "192.0.2.42", "server_port": "8389", "password": "pw2", "method": "aes-256-gcm"}, {"remarks": "SIP plugin", "server": "192.0.2.43", "server_port": 8390, "password": "pw3", "method": "aes-256-gcm", "plugin": "obfs-local"}]}`
	for _, body := range []string{doc, `[{"remarks": "SIP A", "server": "192.0.2.41", "server_port": 8388, "password": "pw", "method": "chacha20-ietf-poly1305"}, {"remarks": "SIP B", "server": "192.0.2.42", "server_port": 8389, "password": "pw2", "method": "aes-256-gcm"}]`} {
		c, e := BuildConfig(body)
		if e != nil {
			t.Fatal(e)
		}
		if e = CheckConfig(c); e != nil {
			t.Fatal(e)
		}
		n := byName(t, c)
		if len(n) != 2 || n["SIP B"]["password"] != "pw2" || n["SIP B"]["server_port"] != float64(8389) {
			t.Fatal("SIP008", n)
		}
	}
	if _, e := BuildConfig(`{"servers": []}`); e == nil {
		t.Fatal("empty SIP008 accepted")
	}
}

func TestPastedAndURLSafeBase64Links(t *testing.T) {
	links := "vless://00000000-0000-4000-8000-000000000001@192.0.2.20:443?security=tls#A vless://00000000-0000-4000-8000-000000000001@192.0.2.21:443?security=tls#B\r\nss://YWVzLTEyOC1nY206cGFzcw@192.0.2.22:8388#C\n"
	for _, body := range []string{links, strings.TrimRight(base64.URLEncoding.EncodeToString([]byte(links)), "="), base64.RawStdEncoding.EncodeToString([]byte(links))} {
		c, e := BuildConfig(body)
		if e != nil {
			t.Fatal(e)
		}
		if len(byName(t, c)) != 3 {
			t.Fatal("pasted links", body)
		}
	}
}

// Every value a node may carry must be accepted by sing-box, or one bad node would break the whole config.
func TestNodeValuesSingBoxAccepts(t *testing.T) {
	for _, method := range shadowsocksMethods {
		key := "pw"
		if strings.HasPrefix(method, "2022-") {
			key = base64.StdEncoding.EncodeToString(make([]byte, map[bool]int{true: 16, false: 32}[strings.Contains(method, "128")]))
		}
		c, e := BuildConfig(ssLink("192.0.2.1", 8388, method, key, method))
		if e != nil {
			t.Fatal(method, e)
		}
		if e = CheckConfig(c); e != nil {
			t.Fatal(method, e)
		}
	}
	for _, link := range []string{"ss://" + base64.RawURLEncoding.EncodeToString([]byte("rot13:pw")) + "@192.0.2.1:8388#x", "vless://00000000-0000-4000-8000-000000000001@192.0.2.1:443?security=tls&flow=xtls-rprx-vision-udp443#x"} {
		if _, e := BuildConfig(link); e == nil {
			t.Fatal("unsupported value accepted", link)
		}
	}
}
