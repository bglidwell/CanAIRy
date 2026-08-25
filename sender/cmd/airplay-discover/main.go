package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"sort"
	"time"

	"doubletake/internal/airplay"
)

type device struct {
	Name     string `json:"name"`
	Model    string `json:"model"`
	IP       string `json:"ip"`
	Port     int    `json:"port"`
	DeviceID string `json:"deviceId"`
}

func main() {
	timeout := flag.Duration("timeout", 2500*time.Millisecond, "mDNS browse duration")
	flag.Parse()
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	found, err := airplay.DiscoverAirPlayDevices(ctx)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	unique := make(map[string]device)
	for _, item := range found {
		key := item.DeviceID
		if key == "" {
			key = item.IP
		}
		unique[key] = device{
			Name: item.Name, Model: item.Model, IP: item.IP,
			Port: item.Port, DeviceID: item.DeviceID,
		}
	}
	devices := make([]device, 0, len(unique))
	for _, item := range unique {
		devices = append(devices, item)
	}
	sort.Slice(devices, func(i, j int) bool {
		if devices[i].Name == devices[j].Name {
			return devices[i].IP < devices[j].IP
		}
		return devices[i].Name < devices[j].Name
	})
	if err := json.NewEncoder(os.Stdout).Encode(devices); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}
