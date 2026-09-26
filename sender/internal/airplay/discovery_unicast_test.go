package airplay

import (
	"context"
	"net"
	"testing"
	"time"

	"github.com/grandcat/zeroconf"
	"github.com/miekg/dns"
)

func discoveryRecord(t *testing.T, record string) dns.RR {
	t.Helper()
	rr, err := dns.NewRR(record)
	if err != nil {
		t.Fatal(err)
	}
	return rr
}

func TestAirPlayResponseCacheCombinesPackets(t *testing.T) {
	cache := airPlayResponseCache{entries: make(map[string]*zeroconf.ServiceEntry), addresses: make(map[string][]net.IP)}
	// Current Apple TVs include device-info and other services in their replies.
	cache.add(&dns.Msg{Answer: []dns.RR{
		discoveryRecord(t, "great-room.local. 10 IN A 192.168.4.117"),
		discoveryRecord(t, `Great\ Room._device-info._tcp.local. 10 IN TXT "model=J255AP"`),
		discoveryRecord(t, `Great\ Room._airplay._tcp.local. 10 IN TXT "model=AppleTV14,1" "osvers=27.0" "features=0x4A7FDFD5,0x3C177FDE" "fex=1d9/St5/Fzw4oY7cDg"`),
	}})
	if got := cache.devices(); len(got) != 0 {
		t.Fatalf("incomplete service emitted: %+v", got)
	}
	cache.add(&dns.Msg{Extra: []dns.RR{
		discoveryRecord(t, `Great\ Room._AIRPLAY._TCP.LOCAL. 10 IN SRV 0 0 7000 Great-Room.local.`),
	}})
	devices := cache.devices()
	if len(devices) != 1 {
		t.Fatalf("devices = %+v", devices)
	}
	got := devices[0]
	if got.Name != "Great Room" || got.IP != "192.168.4.117" || got.Port != 7000 || got.Model != "AppleTV14,1" || got.RawTXT["osvers"] != "27.0" || !got.SupportsScreen() {
		t.Fatalf("unexpected device: %+v", got)
	}
}

func TestBrowseAirPlayUnicastReceivesMultipleDevices(t *testing.T) {
	server, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatal(err)
	}
	defer server.Close()
	client, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatal(err)
	}
	defer client.Close()
	ctx, cancel := context.WithTimeout(context.Background(), 300*time.Millisecond)
	defer cancel()
	responses := [][]dns.RR{
		{discoveryRecord(t, `One._airplay._tcp.local. 10 IN SRV 0 0 7000 one.local.`), discoveryRecord(t, `one.local. 10 IN A 192.168.4.40`)},
		{discoveryRecord(t, `Two._airplay._tcp.local. 10 IN SRV 0 0 7000 two.local.`), discoveryRecord(t, `two.local. 10 IN A 192.168.4.117`)},
	}
	serverDone := make(chan error, 1)
	go func() {
		server.SetReadDeadline(time.Now().Add(time.Second))
		buf := make([]byte, 1500)
		n, source, err := server.ReadFromUDP(buf)
		if err != nil {
			serverDone <- err
			return
		}
		var query dns.Msg
		if err := query.Unpack(buf[:n]); err != nil {
			serverDone <- err
			return
		}
		for _, records := range responses {
			reply := new(dns.Msg)
			reply.SetReply(&query)
			reply.Answer = records
			wire, err := reply.Pack()
			if err == nil {
				_, err = server.WriteToUDP(wire, source)
			}
			if err != nil {
				serverDone <- err
				return
			}
		}
		serverDone <- nil
	}()
	devices, err := browseAirPlayUnicast(ctx, client, server.LocalAddr().(*net.UDPAddr))
	if err != nil {
		t.Fatal(err)
	}
	if err := <-serverDone; err != nil {
		t.Fatal(err)
	}
	if len(devices) != 2 {
		t.Fatalf("expected both receivers, got %+v", devices)
	}
}

func TestBrowseAirPlayUnicastCancellation(t *testing.T) {
	conn, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close()
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	devices, err := browseAirPlayUnicast(ctx, conn, conn.LocalAddr().(*net.UDPAddr))
	if err != nil || len(devices) != 0 {
		t.Fatalf("cancelled browse = %v, %v", devices, err)
	}
}
