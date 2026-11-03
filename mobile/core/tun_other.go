//go:build !darwin && !ios

package mobilecore

// Android receives its descriptor directly from VpnService.
func TunnelFD() int32 { return -1 }
