//go:build windows

package airplay

import (
	"context"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
)

func windowsFFmpegPath(cfg CaptureConfig) (string, error) {
	candidates := []string{cfg.FFmpegPath}
	if exe, err := os.Executable(); err == nil {
		candidates = append(candidates, filepath.Join(filepath.Dir(exe), "ffmpeg.exe"))
	}
	candidates = append(candidates, "ffmpeg.exe")
	for _, candidate := range candidates {
		if candidate == "" {
			continue
		}
		if path, err := exec.LookPath(candidate); err == nil {
			return path, nil
		}
	}
	return "", fmt.Errorf("FFmpeg was not found; set CaptureConfig.FFmpegPath or place ffmpeg.exe beside the sender")
}

func validateWindowsFFmpegCapture(cfg CaptureConfig) error {
	if normalizeVideoCodec(cfg.VideoCodec) == VideoCodecHEVC {
		return fmt.Errorf("Windows HEVC capture is not enabled yet; use H.264")
	}
	path, err := windowsFFmpegPath(cfg)
	if err != nil {
		return err
	}
	cmd := exec.Command(path, "-hide_banner", "-version")
	if err := cmd.Run(); err != nil {
		return fmt.Errorf("run FFmpeg at %q: %w", path, err)
	}
	return nil
}

func startPreparedWindowsFFmpegCapture(ctx context.Context, cfg CaptureConfig, synthetic bool) (*ScreenCapture, error) {
	path, err := windowsFFmpegPath(cfg)
	if err != nil {
		return nil, err
	}
	fps := cfg.FPS
	if fps <= 0 {
		fps = 30
	}
	width, height := cfg.MaxWidth, cfg.MaxHeight
	if width <= 0 || height <= 0 {
		width, height = testCaptureWidth, testCaptureHeight
	}
	// H.264 4:2:0 requires even chroma dimensions.
	width -= width % 2
	height -= height % 2
	bitrate := captureBitrateKbps(cfg)

	args := []string{"-hide_banner", "-loglevel", "warning", "-nostdin"}
	if synthetic {
		args = append(args,
			"-f", "lavfi",
			"-i", fmt.Sprintf("testsrc2=size=%dx%d:rate=%d", width, height, fps),
		)
	} else {
		input := cfg.WindowsInput
		if input == "" {
			input = "desktop"
		}
		args = append(args,
			"-f", "gdigrab",
			"-framerate", strconv.Itoa(fps),
			"-draw_mouse", boolFlag(cfg.ShowCursor),
			"-i", input,
		)
	}
	args = append(args,
		"-vf", fmt.Sprintf("scale=%d:%d:force_original_aspect_ratio=decrease,pad=%d:%d:(ow-iw)/2:(oh-ih)/2:black,format=yuv420p", width, height, width, height),
		"-an",
		"-c:v", "libx264",
		"-preset", "ultrafast",
		"-tune", "zerolatency",
		"-profile:v", "high",
		"-level:v", "4.1",
		"-b:v", fmt.Sprintf("%dk", bitrate),
		"-maxrate", fmt.Sprintf("%dk", bitrate),
		"-bufsize", fmt.Sprintf("%dk", bitrate*2),
		"-g", strconv.Itoa(fps),
		"-keyint_min", strconv.Itoa(fps),
		"-bf", "0",
		"-x264-params", "aud=1:repeat-headers=1:scenecut=0",
		"-f", "h264",
		"pipe:1",
	)

	captureCtx, cancel := context.WithCancel(ctx)
	cmd := exec.CommandContext(captureCtx, path, args...)
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		cancel()
		return nil, fmt.Errorf("FFmpeg stdout pipe: %w", err)
	}
	stderr, err := cmd.StderrPipe()
	if err != nil {
		cancel()
		return nil, fmt.Errorf("FFmpeg stderr pipe: %w", err)
	}
	waitResult, err := startGStreamerCommand(cmd)
	if err != nil {
		cancel()
		return nil, fmt.Errorf("start FFmpeg: %w", err)
	}
	go logStderr("FFMPEG", stderr)
	capture := &ScreenCapture{
		cmd: cmd, stdout: stdout, cancel: cancel, waitCh: make(chan struct{}),
	}
	go func() {
		capture.waitErr = <-waitResult
		close(capture.waitCh)
	}()
	return capture, nil
}

func boolFlag(value bool) string {
	if value {
		return "1"
	}
	return "0"
}
