# Manual literal S3 sleep/resume UAT

`Invoke-ManualSleepResumeUat.ps1` is a manual-only evidence harness for the Phase 6 sleep/resume requirement. It never calls a sleep, hibernate, shutdown, wake, scheduled-task, or power-setting API.

It creates a previously nonexistent, probe-owned artifact root, starts a real published-app batch through the real folder pickers, and waits for the operator to put the machine into S3 standby and wake it locally. After resume it requires the batch still to be active, uses the app's real close confirmation to cancel and close, and scans its owned output directory for temporary files.

A pass is deliberately strict. The System event log must contain records newer than the probe's baseline record ID:

- `Microsoft-Windows-Kernel-Power`, Event ID `42`, with `TargetState=4` (S3); and
- a later `Microsoft-Windows-Kernel-Power` Event ID `107` or `Microsoft-Windows-Power-Troubleshooter` Event ID `1`.

It records event IDs, record IDs, UTC timing, the S3 target state, post-resume process/batch state, and cleanup results in `manual-literal-s3-sleep-resume.json`. A failed or timed-out report is not sleep/resume acceptance evidence. Simulated events, screen locking, monitor-off, and synthetic process suspension do not satisfy this probe.

Run it only on a test machine whose user has saved unrelated work and can wake the machine locally:

```powershell
.\tools\Invoke-ManualSleepResumeUat.ps1 `
  -AppPath .\artifacts\publish-smoke\20260811-final2-win-x64\WinBulkTranscript.exe `
  -FixturePath .\test-assets\synthetic\flat\fixture-001.mp4 `
  -ArtifactRoot .\artifacts\manual-sleep-resume\20260811-win-x64 `
  -TransitionTimeoutMinutes 10
```

When the script says it is armed, use the normal physical power control or Windows power menu to enter S3 standby, wake the machine locally, and leave the script running. Do not use this command in a remote-only session without a reliable local wake path. On success, retain both the JSON report and the published artifact hash it records.
