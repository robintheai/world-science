package mobilecore

import (
	"encoding/base64"
	"encoding/json"
	"strings"
	"testing"
)

const fixture = "vless://00000000-0000-4000-8000-000000000001@192.0.2.10:443?security=tls&sni=example.com#%E7%BE%8E%E5%9B%BD%20US-VPS"

func TestConfigPolicy(t *testing.T) {
	c, e := BuildConfig(fixture)
	if e != nil {
		t.Fatal(e)
	}
	if e = CheckConfig(c); e != nil {
		t.Fatal(e)
	}
	var v map[string]any
	json.Unmarshal([]byte(c), &v)
	r := v["route"].(map[string]any)
	if r["final"] != "auto" {
		t.Fatal("public traffic bypasses proxy")
	}
	rules := r["rules"].([]any)
	first := rules[0].(map[string]any)
	if first["action"] != "reject" || first["ip_cidr"].([]any)[0] != "::/0" {
		t.Fatal("IPv6 must be blocked before sniffing")
	}
	if !strings.Contains(c, `"strategy":"ipv4_only"`) {
		t.Fatal("route policy missing")
	}
}

// A general-purpose client routes no hard-coded service domain around the proxy.
func TestNoHardCodedServiceDomains(t *testing.T) {
	c, e := BuildConfig(fixture)
	if e != nil {
		t.Fatal(e)
	}
	var v map[string]any
	json.Unmarshal([]byte(c), &v)
	for _, section := range []string{"route", "dns"} {
		for _, rule := range v[section].(map[string]any)["rules"].([]any) {
			if _, ok := rule.(map[string]any)["domain"]; ok {
				t.Fatal("unexpected domain rule in " + section)
			}
		}
	}
	if e = CheckConfig(c); e != nil {
		t.Fatal(e)
	}
}
func TestInvalidSubscriptions(t *testing.T) {
	for _, s := range []string{"", "not-base64", "vless://id@[2001:db8::1]:443", "trojan://secret@192.0.2.1:0", "unknown://secret@192.0.2.1:443"} {
		if _, e := BuildConfig(s); e == nil {
			t.Fatal("invalid configuration accepted")
		}
	}
}
func TestBase64UsesSelectedVPN(t *testing.T) {
	s := strings.ReplaceAll(fixture, "%E7%BE%8E%E5%9B%BD%20US-VPS", "Japan")
	c, e := BuildConfig(base64.StdEncoding.EncodeToString([]byte(s)))
	if e != nil {
		t.Fatal(e)
	}
	if strings.Contains(c, `"outbound":"gemini-us"`) {
		t.Fatal("unowned route selected")
	}
	if e = CheckConfig(c); e != nil {
		t.Fatal(e)
	}
}

// Faster alternatives must not tear down established streaming/chat/TCP sessions.
func TestPeriodicSelectionPreservesExistingConnections(t *testing.T) {
	c, err := BuildConfig(fixture)
	if err != nil {
		t.Fatal(err)
	}
	var v map[string]any
	json.Unmarshal([]byte(c), &v)
	groups := 0
	for _, row := range v["outbounds"].([]any) {
		o := row.(map[string]any)
		if o["type"] == "urltest" {
			groups++
			if o["interrupt_exist_connections"] != false {
				t.Fatal("periodic selection interrupts existing connections")
			}
		}
	}
	if groups != 1 {
		t.Fatal("the selected-country group must preserve established connections")
	}
	if err = CheckConfig(c); err != nil {
		t.Fatal(err)
	}
}

func TestSubscriptionMetadataDoesNotChangeTunnelConfig(t *testing.T) {
	a, _ := BuildConfig(fixture + "\nhttps://example.invalid/usage?bytes=123")
	b, _ := BuildConfig(fixture + "\nhttps://example.invalid/usage?bytes=456")
	if a != b {
		t.Fatal("account usage metadata changes must not restart a tunnel")
	}
}

func TestAllWebsitesUseSelectedVPNRegardlessOfLegacyMetadata(t *testing.T) {
	normal, err := BuildConfig(fixture)
	if err != nil {
		t.Fatal(err)
	}
	legacy, _ := json.Marshal(map[string]any{"links": fixture, "gemini_endpoints": []any{map[string]any{"protocol": "vless", "host": "192.0.2.10", "port": 443}}})
	old, err := BuildConfig(string(legacy))
	if err != nil || old != normal {
		t.Fatal("legacy metadata must not change routing", err)
	}
	var v object
	json.Unmarshal([]byte(normal), &v)
	r := v["route"].(map[string]any)
	rulesJSON, _ := json.Marshal(r["rules"])
	if r["final"] != "auto" || strings.Contains(normal, "gemini") || strings.Contains(string(rulesJSON), "US-VPS") {
		t.Fatal("website-specific route remains")
	}
	for _, raw := range r["rules"].([]any) {
		rule := raw.(map[string]any)
		if _, ok := rule["domain"]; ok {
			t.Fatal("public website override remains")
		}
		if _, ok := rule["domain_suffix"]; ok {
			t.Fatal("website override remains")
		}
	}
	for _, raw := range v["outbounds"].([]any) {
		node := raw.(map[string]any)
		if _, ok := node["detour"]; ok {
			t.Fatal("node chain remains")
		}
	}
	if err = CheckConfig(normal); err != nil {
		t.Fatal(err)
	}
}

func outbounds(t *testing.T, c string) []map[string]any {
	t.Helper()
	var v map[string]any
	if err := json.Unmarshal([]byte(c), &v); err != nil {
		t.Fatal(err)
	}
	var nodes []map[string]any
	for _, raw := range v["outbounds"].([]any) {
		if o := raw.(map[string]any); o["server"] != nil {
			nodes = append(nodes, o)
		}
	}
	return nodes
}

// VMess is still the most common protocol in Chinese subscriptions.
func TestVmessLinksAreSupported(t *testing.T) {
	payload, _ := json.Marshal(map[string]any{"v": "2", "ps": "🇭🇰 香港 01", "add": "192.0.2.20", "port": "8443", "id": "00000000-0000-4000-8000-000000000002", "aid": 0, "scy": "auto", "net": "ws", "host": "cdn.example.com", "path": "/ray", "tls": "tls", "sni": "cdn.example.com"})
	c, err := BuildConfig("vmess://" + base64.StdEncoding.EncodeToString(payload))
	if err != nil {
		t.Fatal(err)
	}
	if err = CheckConfig(c); err != nil {
		t.Fatal(err)
	}
	n := outbounds(t, c)[0]
	if n["type"] != "vmess" || n["server_port"].(float64) != 8443 || n["transport"].(map[string]any)["type"] != "ws" || n["tls"].(map[string]any)["server_name"] != "cdn.example.com" {
		t.Fatal("vmess fields were not mapped", n)
	}
	if !strings.HasPrefix(n["tag"].(string), "🇭🇰 香港 01 · ") {
		t.Fatal("the provider's node name must be kept for the location list", n["tag"])
	}
}

// Panels often wrap base64 subscriptions at 76 columns.
func TestWrappedBase64Subscription(t *testing.T) {
	encoded := base64.StdEncoding.EncodeToString([]byte(fixture + "\n" + strings.ReplaceAll(fixture, "192.0.2.10", "192.0.2.11")))
	var wrapped strings.Builder
	for i := 0; i < len(encoded); i += 76 {
		wrapped.WriteString(encoded[i:min(i+76, len(encoded))] + "\r\n")
	}
	c, err := BuildConfig(wrapped.String())
	if err != nil {
		t.Fatal(err)
	}
	if len(outbounds(t, c)) != 2 {
		t.Fatal("wrapped subscription lost nodes")
	}
}

func TestTLSOptionsAndTransports(t *testing.T) {
	links := strings.Join([]string{
		"hy2://secret@192.0.2.30:443?insecure=1&sni=hy.example.com#Self-signed",
		"tuic://00000000-0000-4000-8000-000000000003:pw@192.0.2.31:443?sni=tuic.example.com#TUIC",
		"vless://00000000-0000-4000-8000-000000000004@192.0.2.32:443?security=tls&type=httpupgrade&host=up.example.com&path=%2Fup#Upgrade",
		"trojan://pw@192.0.2.33:443?type=xhttp#Unknown",
	}, "\n")
	c, err := BuildConfig(links)
	if err != nil {
		t.Fatal(err)
	}
	if err = CheckConfig(c); err != nil {
		t.Fatal(err)
	}
	nodes := outbounds(t, c)
	if len(nodes) != 3 {
		t.Fatal("unknown transports must be skipped, supported ones kept", len(nodes))
	}
	if nodes[0]["tls"].(map[string]any)["insecure"] != true {
		t.Fatal("insecure=1 must allow self-signed certificates")
	}
	if alpn := nodes[1]["tls"].(map[string]any)["alpn"].([]any); len(alpn) != 1 || alpn[0] != "h3" {
		t.Fatal("TUIC needs h3 ALPN by default")
	}
	if nodes[2]["transport"].(map[string]any)["type"] != "httpupgrade" {
		t.Fatal("httpupgrade transport must be mapped")
	}
}

// One node with values sing-box does not accept must be skipped or normalised, never break the others.
func TestUnsupportedNodeValuesDoNotBreakConfig(t *testing.T) {
	odd, _ := json.Marshal(map[string]any{"ps": "odd", "add": "192.0.2.40", "port": 443, "id": "00000000-0000-4000-8000-000000000005", "scy": "aes-128-cfb", "net": "tcp"})
	header, _ := json.Marshal(map[string]any{"ps": "header", "add": "192.0.2.41", "port": 80, "id": "00000000-0000-4000-8000-000000000006", "net": "tcp", "type": "http"})
	links := strings.Join([]string{
		fixture,
		"trojan://pw@192.0.2.42:443?fp=none&sni=t.example.com#FP",
		"tuic://00000000-0000-4000-8000-000000000007:pw@192.0.2.43:443?congestion_control=weird#CC",
		"vmess://" + base64.StdEncoding.EncodeToString(odd),
		"vmess://" + base64.StdEncoding.EncodeToString(header),
	}, "\n")
	c, err := BuildConfig(links)
	if err != nil {
		t.Fatal(err)
	}
	if err = CheckConfig(c); err != nil {
		t.Fatal("an unsupported value broke the whole configuration:", err)
	}
	if n := len(outbounds(t, c)); n != 3 {
		t.Fatal("unsupported VMess ciphers and HTTP-header TCP must be skipped; fixable values kept", n)
	}
}
