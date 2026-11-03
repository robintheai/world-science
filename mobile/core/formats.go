package mobilecore

import (
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"net/url"
	"regexp"
	"strconv"
	"strings"

	"gopkg.in/yaml.v3"
)

var clashDocument = regexp.MustCompile(`(?m)^proxies:`)

// shareLinks turns any supported subscription body (share links, base64, Clash/Mihomo YAML,
// SIP008 JSON or the legacy {"links": ...} bundle) into whitespace-separated share links.
func shareLinks(body string) (string, error) {
	text := strings.TrimSpace(strings.TrimPrefix(strings.TrimSpace(body), "\uFEFF"))
	if strings.HasPrefix(text, "{") || strings.HasPrefix(text, "[") {
		var bundle subscriptionBundle
		if json.Unmarshal([]byte(text), &bundle) == nil && bundle.Links != "" {
			text = strings.TrimSpace(bundle.Links)
		} else if links := sip008Links(text); len(links) > 0 {
			return strings.Join(links, "\n"), nil
		} else {
			return "", errors.New("Invalid subscription routing metadata.")
		}
	}
	if !clashDocument.MatchString(text) && !strings.Contains(text, "://") {
		decoded, e := decode64(text)
		if e != nil {
			return "", e
		}
		text = strings.TrimSpace(decoded)
		if strings.HasPrefix(text, "{") || strings.HasPrefix(text, "[") {
			return strings.Join(sip008Links(text), "\n"), nil
		}
	}
	if clashDocument.MatchString(text) {
		links, _ := clashLinks(text)
		return strings.Join(links, "\n"), nil
	}
	return text, nil
}

func ssLink(server string, port int, method, password, name string) string {
	user := base64.RawURLEncoding.EncodeToString([]byte(method + ":" + password))
	return fmt.Sprintf("ss://%s@%s#%s", user, hostPort(server, port), url.PathEscape(name))
}

func hostPort(server string, port int) string {
	if strings.Contains(server, ":") && !strings.HasPrefix(server, "[") {
		server = "[" + server + "]"
	}
	return server + ":" + strconv.Itoa(port)
}

// sip008Links reads Shadowsocks SIP008 ({"servers": [...]} or a bare array). Plugin servers are skipped.
func sip008Links(text string) []string {
	type server struct {
		Server   string          `json:"server"`
		Port     json.RawMessage `json:"server_port"`
		Password string          `json:"password"`
		Method   string          `json:"method"`
		Remarks  string          `json:"remarks"`
		Plugin   string          `json:"plugin"`
	}
	var doc struct {
		Servers []server `json:"servers"`
	}
	if json.Unmarshal([]byte(text), &doc) != nil {
		if json.Unmarshal([]byte(text), &doc.Servers) != nil {
			return nil
		}
	}
	var links []string
	for _, s := range doc.Servers {
		port := number(s.Port)
		if s.Plugin != "" || s.Server == "" || s.Method == "" || s.Password == "" || port < 1 || port > 65535 {
			continue
		}
		links = append(links, ssLink(s.Server, port, s.Method, s.Password, s.Remarks))
	}
	return links
}

func str(m map[string]any, key string) string {
	switch v := m[key].(type) {
	case string:
		return v
	case int, int64, uint64, float64, bool:
		return fmt.Sprint(v)
	}
	return ""
}

func sub(m map[string]any, key string) map[string]any {
	v, _ := m[key].(map[string]any)
	return v
}

func yes(m map[string]any, key string) bool {
	return truthy(str(m, key))
}

func query(pairs ...string) string {
	var parts []string
	for i := 0; i+1 < len(pairs); i += 2 {
		if pairs[i+1] != "" {
			parts = append(parts, pairs[i]+"="+url.QueryEscape(pairs[i+1]))
		}
	}
	return strings.Join(parts, "&")
}

// clashLinks converts a Clash/Mihomo document's proxies into share links; unsupported proxies are skipped individually.
func clashLinks(text string) (links []string, skipped int) {
	var doc struct {
		Proxies []map[string]any `yaml:"proxies"`
	}
	if yaml.Unmarshal([]byte(text), &doc) != nil {
		return nil, 0
	}
	for _, p := range doc.Proxies {
		if link := clashLink(p); link != "" {
			links = append(links, link)
		} else {
			skipped++
		}
	}
	return links, skipped
}

func clashLink(p map[string]any) string {
	name, server := str(p, "name"), str(p, "server")
	port, e := strconv.Atoi(str(p, "port"))
	if server == "" || e != nil || port < 1 || port > 65535 {
		return ""
	}
	address, remark := hostPort(server, port), "#"+url.PathEscape(name)
	sni := str(p, "servername")
	if sni == "" {
		sni = str(p, "sni")
	}
	alpn := str(p, "alpn")
	if list, ok := p["alpn"].([]any); ok {
		var values []string
		for _, v := range list {
			values = append(values, fmt.Sprint(v))
		}
		alpn = strings.Join(values, ",")
	}
	fp, insecure := str(p, "client-fingerprint"), ""
	if yes(p, "skip-cert-verify") {
		insecure = "1"
	}
	network := strings.ToLower(str(p, "network"))
	if network == "" {
		network = "tcp"
	}
	ws, h2 := sub(p, "ws-opts"), sub(p, "h2-opts")
	wsPath, wsHost := str(ws, "path"), str(sub(ws, "headers"), "Host")
	if wsPath == "" {
		wsPath = "/"
	}
	h2Host := str(h2, "host")
	if list, ok := h2["host"].([]any); ok {
		var hosts []string
		for _, v := range list {
			hosts = append(hosts, fmt.Sprint(v))
		}
		h2Host = strings.Join(hosts, ",")
	}
	service := str(sub(p, "grpc-opts"), "grpc-service-name")
	reality := sub(p, "reality-opts")
	pbk, sid := str(reality, "public-key"), str(reality, "short-id")
	var transport []string
	switch network {
	case "tcp":
		transport = []string{"type", "tcp"}
	case "ws":
		transport = []string{"type", "ws", "host", wsHost, "path", wsPath}
	case "grpc":
		transport = []string{"type", "grpc", "serviceName", service}
	case "h2":
		transport = []string{"type", "h2", "host", h2Host, "path", str(h2, "path")}
	default:
		// HTTP-header obfuscation and other transports have no sing-box equivalent.
		return ""
	}
	user := func(key string) string { return url.PathEscape(str(p, key)) }
	switch strings.ToLower(str(p, "type")) {
	case "ss":
		method, password := str(p, "cipher"), str(p, "password")
		if method == "chacha20-poly1305" {
			method = "chacha20-ietf-poly1305"
		}
		if method == "" || password == "" || str(p, "plugin") != "" {
			return ""
		}
		return ssLink(server, port, method, password, name)
	case "vmess":
		if str(p, "uuid") == "" {
			return ""
		}
		v := map[string]string{"v": "2", "ps": name, "add": server, "port": strconv.Itoa(port), "id": str(p, "uuid"), "aid": str(p, "alterId"), "scy": str(p, "cipher"), "net": network, "type": "none", "sni": sni, "alpn": alpn, "fp": fp, "insecure": insecure, "allowInsecure": insecure}
		if v["aid"] == "" {
			v["aid"] = "0"
		}
		if yes(p, "tls") {
			v["tls"] = "tls"
		}
		switch network {
		case "ws":
			v["host"], v["path"] = wsHost, wsPath
		case "grpc":
			v["path"] = service
		case "h2":
			v["host"], v["path"] = h2Host, str(h2, "path")
		}
		b, _ := json.Marshal(v)
		return "vmess://" + base64.StdEncoding.EncodeToString(b)
	case "vless":
		// sing-box has no VLESS encryption layer; such servers would reject a plain connection.
		if str(p, "uuid") == "" || (str(p, "encryption") != "" && str(p, "encryption") != "none") {
			return ""
		}
		security := "none"
		if pbk != "" {
			security = "reality"
		} else if yes(p, "tls") {
			security = "tls"
		}
		return "vless://" + user("uuid") + "@" + address + "?" + query(append([]string{"encryption", "none", "flow", str(p, "flow"), "security", security, "sni", sni, "alpn", alpn, "fp", fp, "pbk", pbk, "sid", sid, "allowInsecure", insecure}, transport...)...) + remark
	case "trojan":
		if str(p, "password") == "" {
			return ""
		}
		security := "tls"
		if pbk != "" {
			security = "reality"
		}
		return "trojan://" + user("password") + "@" + address + "?" + query(append([]string{"security", security, "sni", sni, "alpn", alpn, "fp", fp, "pbk", pbk, "sid", sid, "allowInsecure", insecure}, transport...)...) + remark
	case "hysteria2", "hy2":
		obfs := str(p, "obfs")
		if str(p, "password") == "" || (obfs != "" && obfs != "salamander") {
			return ""
		}
		return "hysteria2://" + user("password") + "@" + address + "?" + query("sni", sni, "alpn", alpn, "insecure", insecure, "obfs", obfs, "obfs-password", str(p, "obfs-password")) + remark
	case "tuic":
		// TUIC v4 uses a token instead of uuid/password and is not supported by sing-box.
		if str(p, "uuid") == "" || str(p, "password") == "" {
			return ""
		}
		if alpn == "" {
			alpn = "h3"
		}
		cc := str(p, "congestion-controller")
		if cc == "" {
			cc = "bbr"
		}
		return "tuic://" + user("uuid") + ":" + user("password") + "@" + address + "?" + query("sni", sni, "alpn", alpn, "congestion_control", cc, "allow_insecure", insecure) + remark
	}
	return ""
}
