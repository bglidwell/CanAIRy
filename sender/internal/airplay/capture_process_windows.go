//go:build windows

package airplay

import "os/exec"

// CommandContext terminates FFmpeg when its capture context is cancelled. A
// Windows job object will be added by the packaged host so grandchildren are
// terminated as well.
func configureCaptureChild(_ *exec.Cmd) func() {
	return func() {}
}
