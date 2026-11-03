//go:build darwin || ios

package mobilecore

import "github.com/sagernet/sing-box/experimental/libbox"

func TunnelFD() int32 { return libbox.GetTunnelFileDescriptor() }
