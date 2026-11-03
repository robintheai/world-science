package mobilecore

import (
	"encoding/json"
	"errors"
	"github.com/sagernet/sing-box/experimental/libbox"
	"os"
	"path/filepath"
	"sync"
	"time"
)

// Native is implemented by Android VpnService and the iOS packet tunnel extension.
// The platform owns its descriptor; libbox duplicates it and closes its own copy.
type Native interface {
	OpenTun() (int32, error)
	ProtectSocket(fd int32) error
	Interfaces() string
	DefaultInterface() string
	IsApple() bool
}
type Engine struct {
	mu       sync.Mutex
	server   *libbox.CommandServer
	client   *libbox.CommandClient
	platform *platform
	status   string
	location string
	statusMu sync.RWMutex
}

func NewEngine() *Engine { return &Engine{status: `{"up":0,"down":0}`} }
func (e *Engine) Start(config, dir string, n Native) error {
	e.mu.Lock()
	defer e.mu.Unlock()
	if e.server != nil {
		return errors.New("Already connected")
	}
	if n == nil {
		return errors.New("Missing VPN platform")
	}
	if err := os.MkdirAll(dir, 0700); err != nil {
		return err
	}
	if err := libbox.Setup(&libbox.SetupOptions{BasePath: dir, WorkingPath: dir, TempPath: filepath.Join(dir, "tmp"), LogMaxLines: 50, FixAndroidStack: !n.IsApple()}); err != nil {
		return err
	}
	os.MkdirAll(filepath.Join(dir, "tmp"), 0700)
	if err := libbox.CheckConfig(config); err != nil {
		return errors.New("Connection configuration is not supported")
	}
	p := &platform{native: n}
	s, err := libbox.NewCommandServer(&handler{}, p)
	if err != nil {
		return err
	}
	if err = s.Start(); err != nil {
		s.Close()
		return err
	}
	if err = s.StartOrReloadService(config, &libbox.OverrideOptions{}); err != nil {
		s.CloseService()
		s.Close()
		return err
	}
	e.server = s
	e.platform = p
	opt := &libbox.CommandClientOptions{StatusInterval: int64(time.Second)}
	opt.AddCommand(libbox.CommandStatus)
	opt.AddCommand(libbox.CommandGroup)
	c := libbox.NewCommandClient(&statusHandler{engine: e}, opt)
	e.client = c
	if err = c.Connect(); err != nil {
		c.Disconnect()
		e.client = nil
	}
	return nil
}
func (e *Engine) Stop() {
	e.mu.Lock()
	defer e.mu.Unlock()
	if e.client != nil {
		e.client.Disconnect()
		e.client = nil
	}
	if e.server != nil {
		e.server.CloseService()
		e.server.Close()
		e.server = nil
	}
	e.statusMu.Lock()
	e.status = `{"up":0,"down":0}`
	e.statusMu.Unlock()
}
func (e *Engine) NetworkChanged() {
	e.mu.Lock()
	defer e.mu.Unlock()
	if e.server != nil {
		e.platform.update()
		e.server.ResetNetwork()
	}
}
func (e *Engine) Status() string {
	e.statusMu.RLock()
	defer e.statusMu.RUnlock()
	var v object
	json.Unmarshal([]byte(e.status), &v)
	v["location"] = e.location
	b, _ := json.Marshal(v)
	return string(b)
}
func CheckConfig(config string) error { return libbox.CheckConfig(config) }

type platform struct {
	native   Native
	listener libbox.InterfaceUpdateListener
	mu       sync.Mutex
}

func (p *platform) LocalDNSTransport() libbox.LocalDNSTransport { return nil }
func (p *platform) UsePlatformAutoDetectInterfaceControl() bool { return !p.native.IsApple() }
func (p *platform) AutoDetectInterfaceControl(fd int32) error   { return p.native.ProtectSocket(fd) }
func (p *platform) OpenTun(libbox.TunOptions) (int32, error)    { return p.native.OpenTun() }
func (p *platform) UseProcFS() bool                             { return false }
func (p *platform) FindConnectionOwner(int32, string, int32, string, int32) (*libbox.ConnectionOwner, error) {
	return nil, errors.New("Process routing disabled")
}
func (p *platform) StartDefaultInterfaceMonitor(l libbox.InterfaceUpdateListener) error {
	p.mu.Lock()
	p.listener = l
	p.mu.Unlock()
	p.update()
	return nil
}
func (p *platform) CloseDefaultInterfaceMonitor(libbox.InterfaceUpdateListener) error {
	p.mu.Lock()
	p.listener = nil
	p.mu.Unlock()
	return nil
}
func (p *platform) update() {
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.listener == nil {
		return
	}
	name := p.native.DefaultInterface()
	it, err := p.GetInterfaces()
	if err != nil || it == nil {
		p.listener.UpdateDefaultInterface("", -1, false, false)
		return
	}
	for it.HasNext() {
		n := it.Next()
		if n.Name == name {
			p.listener.UpdateDefaultInterface(name, n.Index, n.Metered, false)
			return
		}
	}
	p.listener.UpdateDefaultInterface("", -1, false, false)
}

type iface struct {
	Name      string   `json:"name"`
	Index     int32    `json:"index"`
	MTU       int32    `json:"mtu"`
	Addresses []string `json:"addresses"`
	Type      int32    `json:"type"`
	Metered   bool     `json:"metered"`
}

// libbox accepts native IFF_UP | IFF_RUNNING (1 | 64), not Go net.Flags.
func (p *platform) GetInterfaces() (libbox.NetworkInterfaceIterator, error) {
	var rows []iface
	if err := json.Unmarshal([]byte(p.native.Interfaces()), &rows); err != nil {
		return nil, err
	}
	var items []*libbox.NetworkInterface
	for _, r := range rows {
		items = append(items, &libbox.NetworkInterface{Name: r.Name, Index: r.Index, MTU: r.MTU, Addresses: &stringsIterator{items: r.Addresses}, Flags: 65, Type: r.Type, Metered: r.Metered})
	}
	return &interfaces{items: items}, nil
}
func (p *platform) UnderNetworkExtension() bool                 { return p.native.IsApple() }
func (p *platform) IncludeAllNetworks() bool                    { return false }
func (p *platform) ReadWIFIState() *libbox.WIFIState            { return nil }
func (p *platform) ClearDNSCache()                              {}
func (p *platform) SendNotification(*libbox.Notification) error { return nil }
func (p *platform) CancelNotification(string, int32) error      { return nil }
func (p *platform) StartNeighborMonitor(libbox.NeighborUpdateListener) error {
	return errors.New("Neighbor routing disabled")
}
func (p *platform) CloseNeighborMonitor(libbox.NeighborUpdateListener) error { return nil }
func (p *platform) RegisterMyInterface(string)                               {}
func (p *platform) UsePlatformShell() bool                                   { return false }
func (p *platform) CheckPlatformShell() error                                { return errors.New("Shell disabled") }
func (p *platform) OpenShellSession(*libbox.PlatformUser, string, libbox.StringIterator, string, int32, int32) (libbox.ShellSession, error) {
	return nil, errors.New("Shell disabled")
}
func (p *platform) LookupUser(string) (*libbox.PlatformUser, error) {
	return nil, errors.New("Shell disabled")
}
func (p *platform) LookupSFTPServer() (string, error)     { return "", errors.New("Shell disabled") }
func (p *platform) ReadSystemSSHHostKey() (string, error) { return "", errors.New("Shell disabled") }
func (p *platform) TailscaleHostname() string             { return "" }
func (p *platform) UsePlatformBridge() bool               { return false }
func (p *platform) CreateBridge(*libbox.BridgeOptions) (libbox.BridgeSession, error) {
	return nil, errors.New("Bridge disabled")
}

type stringsIterator struct{ items []string }

func (i *stringsIterator) Len() int32    { return int32(len(i.items)) }
func (i *stringsIterator) HasNext() bool { return len(i.items) > 0 }
func (i *stringsIterator) Next() string  { s := i.items[0]; i.items = i.items[1:]; return s }

type interfaces struct{ items []*libbox.NetworkInterface }

func (i *interfaces) HasNext() bool { return len(i.items) > 0 }
func (i *interfaces) Next() *libbox.NetworkInterface {
	n := i.items[0]
	i.items = i.items[1:]
	return n
}

type handler struct{}

func (*handler) ServiceStop() error   { return nil }
func (*handler) ServiceReload() error { return nil }
func (*handler) GetSystemProxyStatus() (*libbox.SystemProxyStatus, error) {
	return &libbox.SystemProxyStatus{}, nil
}
func (*handler) SetSystemProxyEnabled(bool) error { return nil }
func (*handler) TriggerNativeCrash() error        { return errors.New("Disabled") }
func (*handler) WriteDebugMessage(string)         {}
func (*handler) ConnectSSHAgent() (int32, error)  { return -1, errors.New("Disabled") }

type statusHandler struct{ engine *Engine }

func (*statusHandler) Connected()                   {}
func (*statusHandler) Disconnected(string)          {}
func (*statusHandler) SetDefaultLogLevel(int32)     {}
func (*statusHandler) ClearLogs()                   {}
func (*statusHandler) WriteLogs(libbox.LogIterator) {}
func (h *statusHandler) WriteStatus(s *libbox.StatusMessage) {
	b, _ := json.Marshal(object{"up": s.Uplink, "down": s.Downlink, "up_total": s.UplinkTotal, "down_total": s.DownlinkTotal, "available": s.TrafficAvailable})
	h.engine.statusMu.Lock()
	h.engine.status = string(b)
	h.engine.statusMu.Unlock()
}
func (h *statusHandler) WriteGroups(groups libbox.OutboundGroupIterator) {
	for groups.HasNext() {
		g := groups.Next()
		if g.Tag == "auto" {
			h.engine.statusMu.Lock()
			h.engine.location = g.Selected
			h.engine.statusMu.Unlock()
		}
	}
}
func (*statusHandler) WriteOutbounds(libbox.OutboundGroupItemIterator)   {}
func (*statusHandler) InitializeClashMode(libbox.StringIterator, string) {}
func (*statusHandler) UpdateClashMode(string)                            {}
func (*statusHandler) WriteConnectionEvents(*libbox.ConnectionEvents)    {}
