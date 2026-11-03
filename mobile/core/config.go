package mobilecore

import (
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"net/url"
	"strconv"
	"strings"
)

type object = map[string]any

func decode64(s string) (string, error) {
	// Many panels wrap base64 at 76 columns; remove every kind of whitespace first.
	s = strings.Join(strings.Fields(s), "")
	for _, enc := range []*base64.Encoding{base64.RawURLEncoding, base64.URLEncoding, base64.RawStdEncoding, base64.StdEncoding} {
		b, e := enc.DecodeString(s)
		if e == nil {
			return string(b), nil
		}
	}
	return "", errors.New("Invalid subscription encoding")
}

// Accept the previous internal bundle format for compatibility; routing metadata is ignored.
type subscriptionBundle struct {
	Links string `json:"links"`
}

func BuildConfig(links string) (string, error) {
	links, e := shareLinks(links)
	if e != nil {
		return "", e
	}
	var out []object
	var tags []string
	for _, line := range strings.Fields(links) {
		n, name, e := parseNode(line)
		if e != nil {
			continue
		}
		tag := fmt.Sprintf("%s · %d", displayName(name), len(tags)+1)
		n["tag"] = tag
		n["domain_resolver"] = object{"server": "bootstrap", "strategy": "ipv4_only"}
		out = append(out, n)
		tags = append(tags, tag)

	}
	if len(tags) == 0 {
		return "", errors.New("No supported IPv4 connections in your subscription.")
	}
	auto := func(tag string, nodes []string) object {
		return object{"type": "urltest", "tag": tag, "outbounds": nodes, "url": "https://cp.cloudflare.com/generate_204", "interval": "1m", "tolerance": 80, "interrupt_exist_connections": false}
	}
	out = append(out, auto("auto", tags), object{"type": "direct", "tag": "direct"})

	cfg := object{"log": object{"level": "error"}, "dns": object{"reverse_mapping": true, "servers": []object{{"type": "udp", "tag": "bootstrap", "server": "223.5.5.5"}, {"type": "https", "tag": "remote", "server": "1.1.1.1", "path": "/dns-query", "tls": object{"enabled": true, "server_name": "cloudflare-dns.com"}, "detour": "auto"}}, "rules": []object{{"query_type": "AAAA", "action": "predefined", "rcode": "NOERROR"}}, "final": "remote", "strategy": "ipv4_only"}, "inbounds": []object{{"type": "tun", "tag": "tun", "address": []string{"172.19.0.1/30", "fdfe:dcba:9876::1/126"}, "mtu": 1400, "auto_route": true, "strict_route": true, "stack": "gvisor"}}, "outbounds": out, "route": object{"auto_detect_interface": true, "default_domain_resolver": object{"server": "bootstrap", "strategy": "ipv4_only"}, "rules": []object{{"ip_cidr": []string{"::/0"}, "action": "reject"}, {"action": "sniff"}, {"protocol": "dns", "action": "hijack-dns"}, {"ip_is_private": true, "action": "route", "outbound": "direct"}}, "final": "auto"}}
	b, e := json.Marshal(cfg)
	return string(b), e
}
func parseNode(raw string) (object, string, error) {
	bad := errors.New("Unsupported connection")
	if strings.HasPrefix(raw, "vmess://") {
		return parseVmess(strings.TrimPrefix(raw, "vmess://"))
	}
	u, e := url.Parse(raw)
	if e != nil || u.Hostname() == "" || u.User == nil {
		return nil, "", bad
	}
	if ip := net.ParseIP(u.Hostname()); ip != nil && ip.To4() == nil {
		return nil, "", bad
	}
	port, e := strconv.Atoi(u.Port())
	if e != nil || port < 1 || port > 65535 {
		return nil, "", bad
	}
	q := u.Query()
	n := object{"type": u.Scheme, "server": u.Hostname(), "server_port": port}
	password := u.User.Username()
	if p, ok := u.User.Password(); ok {
		password += ":" + p
	}
	switch u.Scheme {
	case "vless":
		n["uuid"] = u.User.Username()
		if f := q.Get("flow"); f != "" {
			if f != "xtls-rprx-vision" {
				return nil, "", bad
			}
			n["flow"] = f
		}
	case "trojan", "anytls":
		n["password"] = password
	case "hy2", "hysteria2":
		n["type"] = "hysteria2"
		n["password"] = password
		if q.Get("obfs") == "salamander" {
			n["obfs"] = object{"type": "salamander", "password": q.Get("obfs-password")}
		}
	case "tuic":
		p, ok := u.User.Password()
		if !ok {
			return nil, "", bad
		}
		n["uuid"] = u.User.Username()
		n["password"] = p
		n["congestion_control"] = "bbr"
		if cc := q.Get("congestion_control"); allowed(cc, "cubic", "new_reno", "bbr") {
			n["congestion_control"] = cc
		}
	case "ss":
		if !strings.Contains(password, ":") {
			password, e = decode64(password)
			if e != nil {
				return nil, "", bad
			}
		}
		parts := strings.SplitN(password, ":", 2)
		if len(parts) != 2 || q.Get("plugin") != "" || !allowed(parts[0], shadowsocksMethods...) {
			return nil, "", bad
		}
		n["type"] = "shadowsocks"
		n["method"] = parts[0]
		n["password"] = parts[1]
	default:
		return nil, "", bad
	}
	if u.Scheme != "ss" && (u.Scheme != "vless" || q.Get("security") == "tls" || q.Get("security") == "reality") {
		tls := object{"enabled": true}
		sni := q.Get("sni")
		if sni == "" {
			sni = q.Get("peer")
		}
		if sni != "" {
			tls["server_name"] = sni
		}
		if a := q.Get("alpn"); a != "" {
			tls["alpn"] = strings.Split(a, ",")
		} else if u.Scheme == "tuic" {
			tls["alpn"] = []string{"h3"}
		}
		if truthy(q.Get("insecure")) || truthy(q.Get("allowInsecure")) || truthy(q.Get("allow_insecure")) {
			tls["insecure"] = true
		}
		if fp := q.Get("fp"); validFingerprint(fp) && q.Get("security") != "reality" {
			tls["utls"] = object{"enabled": true, "fingerprint": fp}
		}
		if q.Get("security") == "reality" {
			if q.Get("pbk") == "" {
				return nil, "", bad
			}
			tls["reality"] = object{"enabled": true, "public_key": q.Get("pbk"), "short_id": q.Get("sid")}
			fp := q.Get("fp")
			if !validFingerprint(fp) {
				fp = "chrome"
			}
			tls["utls"] = object{"enabled": true, "fingerprint": fp}
		}
		n["tls"] = tls
	}
	t, ok := transport(q.Get("type"), q.Get("host"), q.Get("path"), q.Get("serviceName"))
	if !ok {
		return nil, "", bad
	}
	if t != nil {
		n["transport"] = t
	}
	return n, u.Fragment, nil
}

func truthy(v string) bool { return v == "1" || strings.EqualFold(v, "true") }

// Values outside these sets make sing-box reject the whole config, which would break every node, not just one.
func allowed(v string, options ...string) bool {
	for _, o := range options {
		if v == o {
			return true
		}
	}
	return false
}

var shadowsocksMethods = []string{"2022-blake3-aes-128-gcm", "2022-blake3-aes-256-gcm", "2022-blake3-chacha20-poly1305", "none", "aes-128-gcm", "aes-192-gcm", "aes-256-gcm", "chacha20-ietf-poly1305", "xchacha20-ietf-poly1305",
	"aes-128-ctr", "aes-192-ctr", "aes-256-ctr", "aes-128-cfb", "aes-192-cfb", "aes-256-cfb", "rc4-md5", "chacha20-ietf", "xchacha20"}

func validFingerprint(fp string) bool {
	return allowed(fp, "chrome", "firefox", "edge", "safari", "360", "qq", "ios", "android", "random", "randomized")
}

// transport maps share-link transport parameters to sing-box. Unknown types are rejected rather than guessed.
func transport(kind, host, path, service string) (object, bool) {
	switch kind {
	case "", "tcp", "none":
		return nil, true
	case "ws":
		t := object{"type": "ws", "path": path}
		if host != "" {
			t["headers"] = object{"Host": host}
		}
		return t, true
	case "grpc":
		return object{"type": "grpc", "service_name": service}, true
	case "httpupgrade":
		return object{"type": "httpupgrade", "host": host, "path": path}, true
	case "h2", "http":
		t := object{"type": "http", "path": path}
		if host != "" {
			t["host"] = strings.Split(host, ",")
		}
		return t, true
	}
	return nil, false
}

// vmessLink is the base64 JSON share format used by v2rayN and most panels.
type vmessLink struct {
	Name     string          `json:"ps"`
	Address  string          `json:"add"`
	Port     json.RawMessage `json:"port"`
	ID       string          `json:"id"`
	AlterID  json.RawMessage `json:"aid"`
	Security string          `json:"scy"`
	Network  string          `json:"net"`
	Host     string          `json:"host"`
	Path     string          `json:"path"`
	TLS      string          `json:"tls"`
	SNI      string          `json:"sni"`
	ALPN     string          `json:"alpn"`
	FP       string          `json:"fp"`
	Header   string          `json:"type"`
	Insecure json.RawMessage `json:"allowInsecure"`
}

func number(raw json.RawMessage) int {
	v, _ := strconv.Atoi(strings.Trim(strings.TrimSpace(string(raw)), `"`))
	return v
}

func parseVmess(payload string) (object, string, error) {
	bad := errors.New("Unsupported connection")
	text, e := decode64(payload)
	if e != nil {
		return nil, "", bad
	}
	var v vmessLink
	if json.Unmarshal([]byte(text), &v) != nil || v.Address == "" || v.ID == "" {
		return nil, "", bad
	}
	if ip := net.ParseIP(v.Address); ip != nil && ip.To4() == nil {
		return nil, "", bad
	}
	port := number(v.Port)
	if port < 1 || port > 65535 {
		return nil, "", bad
	}
	security := v.Security
	if security == "" {
		security = "auto"
	}
	if !allowed(security, "auto", "none", "zero", "aes-128-gcm", "chacha20-poly1305", "aes-128-ctr") {
		return nil, "", bad
	}
	// TCP with HTTP header obfuscation has no sing-box equivalent; building plain TCP would always fail.
	if (v.Network == "" || v.Network == "tcp") && v.Header != "" && v.Header != "none" {
		return nil, "", bad
	}
	n := object{"type": "vmess", "server": v.Address, "server_port": port, "uuid": v.ID, "security": security, "alter_id": number(v.AlterID)}
	if v.TLS == "tls" {
		tls := object{"enabled": true}
		if v.SNI != "" {
			tls["server_name"] = v.SNI
		} else if v.Host != "" {
			tls["server_name"] = strings.Split(v.Host, ",")[0]
		}
		if v.ALPN != "" {
			tls["alpn"] = strings.Split(v.ALPN, ",")
		}
		if validFingerprint(v.FP) {
			tls["utls"] = object{"enabled": true, "fingerprint": v.FP}
		}
		if truthy(strings.Trim(string(v.Insecure), `"`)) {
			tls["insecure"] = true
		}
		n["tls"] = tls
	}
	t, ok := transport(v.Network, v.Host, v.Path, v.Path)
	if !ok {
		return nil, "", bad
	}
	if t != nil {
		n["transport"] = t
	}
	return n, v.Name, nil
}

// displayName keeps the provider's node name, which users recognise, and falls back to a region.
func displayName(name string) string {
	name = strings.Join(strings.Fields(strings.ReplaceAll(name, "·", " ")), " ")
	if r := []rune(name); len(r) > 40 {
		name = string(r[:40])
	}
	if name == "" {
		return location(name)
	}
	return name
}

func location(name string) string {
	for _, p := range [][2]string{{"美国", "United States"}, {"🇺🇸", "United States"}, {"US-VPS", "United States"}, {"日本", "Japan"}, {"🇯🇵", "Japan"}, {"香港", "Hong Kong"}, {"新加坡", "Singapore"}, {"德国", "Germany"}, {"英国", "United Kingdom"}, {"荷兰", "Netherlands"}, {"台湾", "Taiwan"}, {"韩国", "South Korea"}, {"加拿大", "Canada"}, {"澳大利亚", "Australia"}} {
		if strings.Contains(name, p[0]) {
			return p[1]
		}
	}
	return "Automatic location"
}
