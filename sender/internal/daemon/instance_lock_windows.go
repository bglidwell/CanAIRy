//go:build windows

package daemon

import (
	"errors"
	"os"

	"golang.org/x/sys/windows"
)

var errInstanceLocked = errors.New("daemon instance lock is already held")

func tryInstanceLock(file *os.File) error {
	var overlapped windows.Overlapped
	err := windows.LockFileEx(windows.Handle(file.Fd()),
		windows.LOCKFILE_EXCLUSIVE_LOCK|windows.LOCKFILE_FAIL_IMMEDIATELY,
		0, 1, 0, &overlapped)
	if errors.Is(err, windows.ERROR_LOCK_VIOLATION) {
		return errInstanceLocked
	}
	return err
}

func isInstanceLockBusy(err error) bool {
	return errors.Is(err, errInstanceLocked)
}

func unlockInstanceLock(file *os.File) error {
	var overlapped windows.Overlapped
	return windows.UnlockFileEx(windows.Handle(file.Fd()), 0, 1, 0, &overlapped)
}
