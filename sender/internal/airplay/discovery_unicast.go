package airplay

import (
	"context"
	"errors"
	"fmt"
	"net"
	"strings"
	"sync"
	"time"

	"github.com/grandcat/zeroconf"
	"github.com/miekg/dns"
	"golang.org/x/net/ipv4"
)

const airPlayService = "_airplay._tcp.local."

// Windows can fail to deliver multicast replies to zeroconf's UDP/5353
// listener. Also issue RFC 6762 section 6.7 legacy queries from an ephemeral
// port on each LAN interface so receivers reply directly to our socket.
// Keep the multicast browser for receivers/networks which require it and IPv6.
func discoverAirPlayWindows(ctx context.Context, ifaces []net.Interface, traffic zeroconf.IPType) ([]AirPlayDevice, error) {
	type result struct {
		devices []AirPlayDevice
		err     error
	}
	legacy := make(chan result, 1)
	go func() {
		devices, err := discoverAirPlayUnicast(ctx, ifaces)
		legacy <- result{devices, err}
	}()
	devices, err := discoverAirPlayMulticast(ctx, ifaces, traffic)
	other := <-legacy
	if err != nil && other.err != nil {
		return nil, errors.Join(err, other.err)
	}
	// Prefer the directly resolved IPv4 endpoint when both browsers find it.
	merged := make(map[string]AirPlayDevice)
	for _, device := range append(devices, other.devices...) {
		key := device.DeviceID
		if key == "" {
			key = device.IP
		}
		merged[key] = device
	}
	devices = nil
	for _, device := range merged {
		devices = append(devices, device)
	}
	return devices, nil
}

func discoverAirPlayUnicast(ctx context.Context, ifaces []net.Interface) ([]AirPlayDevice, error) {
	type result struct {
		devices []AirPlayDevice
		err     error
	}
	results := make(chan result)
	var workers sync.WaitGroup
	var setupErrors []error
	for _, iface := range ifaces {
		addrs, err := iface.Addrs()
		if err != nil {
			setupErrors = append(setupErrors, err)
			continue
		}
		for _, addr := range addrs {
			ip := ipFromAddr(addr)
			if !isUsableMDNSAddress(ip) || ip.To4() == nil {
				continue
			}
			conn, err := net.ListenUDP("udp4", &net.UDPAddr{IP: ip})
			if err != nil {
				setupErrors = append(setupErrors, err)
				continue
			}
			packet := ipv4.NewPacketConn(conn)
			if err := packet.SetMulticastInterface(&iface); err != nil {
				conn.Close()
				setupErrors = append(setupErrors, err)
				continue
			}
			if err := packet.SetMulticastTTL(255); err != nil {
				conn.Close()
				setupErrors = append(setupErrors, err)
				continue
			}
			workers.Add(1)
			go func() {
				defer workers.Done()
				defer conn.Close()
				devices, err := browseAirPlayUnicast(ctx, conn, &net.UDPAddr{IP: net.IPv4(224, 0, 0, 251), Port: 5353})
				results <- result{devices, err}
			}()
		}
	}
	go func() {
		workers.Wait()
		close(results)
	}()
	var devices []AirPlayDevice
	succeeded := false
	for result := range results {
		devices = append(devices, result.devices...)
		if result.err == nil {
			succeeded = true
		} else {
			setupErrors = append(setupErrors, result.err)
		}
	}
	if !succeeded {
		return devices, fmt.Errorf("unicast mDNS: %w", errors.Join(append(setupErrors, errors.New("no working IPv4 discovery socket"))...))
	}
	return devices, nil
}

func browseAirPlayUnicast(ctx context.Context, conn *net.UDPConn, destination *net.UDPAddr) ([]AirPlayDevice, error) {
	stop := context.AfterFunc(ctx, func() { conn.Close() })
	defer stop()
	query := new(dns.Msg)
	query.SetQuestion(airPlayService, dns.TypePTR)
	query.RecursionDesired = false
	wire, err := query.Pack()
	if err != nil {
		return nil, err
	}
	cache := airPlayResponseCache{entries: make(map[string]*zeroconf.ServiceEntry), addresses: make(map[string][]net.IP)}
	buf := make([]byte, 65535)
	var nextQuery time.Time
	for ctx.Err() == nil {
		if !time.Now().Before(nextQuery) {
			if _, err := conn.WriteToUDP(wire, destination); err != nil {
				if ctx.Err() != nil {
					break
				}
				return cache.devices(), err
			}
			nextQuery = time.Now().Add(time.Second)
		}
		conn.SetReadDeadline(nextQuery)
		n, source, err := conn.ReadFromUDP(buf)
		if err != nil {
			if ctx.Err() != nil {
				break
			}
			if timeout, ok := err.(net.Error); ok && timeout.Timeout() {
				continue
			}
			return cache.devices(), err
		}
		var reply dns.Msg
		if source.Port != destination.Port || reply.Unpack(buf[:n]) != nil || !reply.Response || reply.Id != query.Id || reply.Rcode != dns.RcodeSuccess {
			continue
		}
		cache.add(&reply)
	}
	return cache.devices(), nil
}

// Retain records across datagrams and tolerate any RR order. In particular,
// address records can precede the SRV record that names their host.
type airPlayResponseCache struct {
	entries   map[string]*zeroconf.ServiceEntry
	addresses map[string][]net.IP
}

func (c *airPlayResponseCache) add(reply *dns.Msg) {
	for _, records := range [][]dns.RR{reply.Answer, reply.Extra} {
		for _, record := range records {
			header := record.Header()
			if header.Ttl == 0 || header.Class&0x7fff != dns.ClassINET {
				continue
			}
			key := strings.ToLower(header.Name)
			switch rr := record.(type) {
			case *dns.A:
				if isUsableMDNSAddress(rr.A) {
					c.addresses[key] = append(c.addresses[key], rr.A)
				}
			case *dns.SRV, *dns.TXT:
				if !strings.HasSuffix(key, "."+airPlayService) {
					continue
				}
				entry := c.entries[key]
				if entry == nil {
					instance := header.Name[:len(header.Name)-len(airPlayService)-1]
					entry = zeroconf.NewServiceEntry(instance, "_airplay._tcp", "local.")
					c.entries[key] = entry
				}
				switch rr := rr.(type) {
				case *dns.SRV:
					entry.HostName = strings.ToLower(rr.Target)
					entry.Port = int(rr.Port)
				case *dns.TXT:
					entry.Text = rr.Txt
				}
			}
		}
	}
}

func (c *airPlayResponseCache) devices() []AirPlayDevice {
	var devices []AirPlayDevice
	for _, entry := range c.entries {
		if entry.Port == 0 {
			continue
		}
		entry.AddrIPv4 = c.addresses[entry.HostName]
		if device := parseServiceEntry(entry); device != nil {
			devices = append(devices, *device)
		}
	}
	return devices
}
